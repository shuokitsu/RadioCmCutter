using RadioCmCutter.Core.Models;

namespace RadioCmCutter.Core.Detection;

public sealed class SegmentClassificationOptions
{
    /// <summary>音響急変のみを根拠とする（＝繰り返しも履歴一致もない）区間を、確認用の候補として
    /// 一覧に出す長さの下限。これより短い区間はCMとして短すぎるため候補にしない。</summary>
    public double MinSegmentSeconds { get; init; } = 8.0;

    /// <summary>同上の上限。これより長い区間は番組本編（曲・コーナー全体等）と考えられるため候補にしない。</summary>
    public double MaxSegmentSeconds { get; init; } = 300.0;

    /// <summary>CMスポットの基本尺（秒）。日本のラジオCMは主にこの倍数（15/30/45/60秒）で構成される。</summary>
    public double SpotUnitSeconds { get; init; } = 15.0;

    /// <summary>区間の長さがCMスポット尺の倍数にこの秒数以内で近ければ「CM尺相当」とみなす。</summary>
    public double SpotLengthToleranceSeconds { get; init; } = 3.0;

    /// <summary>最終的にこの秒数以下になった候補はCMとして意味がないため除外する。</summary>
    public double MinFinalCandidateSeconds { get; init; } = 5.0;

    /// <summary>区間内にこの秒数以上の連続した繰り返し一致があれば、その区間を繰り返し由来とみなす。
    /// <see cref="RepeatSegmentDetector.ComputeHits"/>の段階で既に最小run長（既定8秒）のふるいを
    /// 通しているため、ここで同じ長さを再度要求すると条件の二重適用になり、
    /// カット位置候補が1本の繰り返しrunの中に落ちただけで真陽性が消えてしまう
    /// （実データでは9.2秒のrunが8.6秒+0.6秒に分断され、検出が失われた）。
    /// そのため「繰り返しの音声が確かに含まれる」ことを確認できる短めの長さにしている
    /// （散発的な単発ヒットで区間全体を繰り返し扱いにしないための下限）。</summary>
    public double MinRepeatEvidenceRunSeconds { get; init; } = 2.0;
}

/// <summary>繰り返し検出の根拠（<see cref="RepeatSegmentDetector.ComputeHits"/>の結果）。
/// 複数ファイル一括処理では、対象ファイルのフレーム範囲に切り出した配列を渡す。</summary>
public readonly record struct RepeatEvidence(int[] HitCount, double[] HitScoreSum);

/// <summary>
/// 共通のカット位置候補で区切られた各区間について、「CMかどうか」を判定する（第2段階）。
/// 判定は根拠の強い順（繰り返し出現 → 過去の確定履歴との一致 → 音響急変のみ）で行い、
/// 裏付けのある根拠を持つ区間だけをデフォルトのカット対象にする。
///
/// 区間内の一致フレームの「割合」ではなく「条件を満たす連続run が存在するか」で判定する点が重要:
/// 割合で見ると、40秒の区間に10秒の本物の繰り返しが含まれる場合に 0.25 となり、
/// 各検出器が本来持っている最小run長の担保（繰り返し8秒・履歴3秒）を、
/// 無関係な周辺音声の長さで薄めてしまうため。
/// </summary>
public static class SegmentClassifier
{
    private enum SegmentClass
    {
        /// <summary>音響的な切れ目のみ（裏付けとなる根拠なし）。</summary>
        Weak,
        Repeat,
        History,
    }

    /// <param name="boundaries">ファイル先頭(0)・末尾(長さ)を含む、時刻順のカット位置候補。</param>
    public static List<CmCandidate> Classify(
        IReadOnlyList<FrameFeatures> frames,
        IReadOnlyList<BoundaryPoint> boundaries,
        RepeatEvidence repeat,
        HistoryMatchScores history,
        HistoryMatchOptions historyOptions,
        SegmentClassificationOptions options)
    {
        if (boundaries.Count < 2 || frames.Count == 0) return [];

        var repeatMinRunFrames = ToFrameCount(options.MinRepeatEvidenceRunSeconds);
        var historyMinRunFrames = ToFrameCount(historyOptions.MinRunSeconds);

        var classified = new List<ClassifiedSegment>();
        var frameIndex = 0;

        for (var i = 0; i + 1 < boundaries.Count; i++)
        {
            var segment = new AudioSegment(boundaries[i].Position, boundaries[i + 1].Position);
            if (segment.Duration <= TimeSpan.Zero) continue;

            var fromFrame = frameIndex;
            while (frameIndex < frames.Count && frames[frameIndex].Start < segment.End)
            {
                frameIndex++;
            }
            var toFrame = frameIndex;
            if (toFrame <= fromFrame) continue;

            var segmentFrames = toFrame - fromFrame;
            var segmentClass = SegmentClass.Weak;

            if (HasQualifyingRun(fromFrame, toFrame, Math.Min(repeatMinRunFrames, segmentFrames),
                    index => repeat.HitCount[index] > 0))
            {
                segmentClass = SegmentClass.Repeat;
            }
            else if (HasQualifyingRun(fromFrame, toFrame, Math.Min(historyMinRunFrames, segmentFrames),
                         index => IsHistoryMatch(history, historyOptions, index)))
            {
                segmentClass = SegmentClass.History;
            }

            classified.Add(new ClassifiedSegment
            {
                Segment = segment,
                FromFrame = fromFrame,
                ToFrame = toFrame,
                Class = segmentClass,
                LeadingStrength = boundaries[i].Strength,
                TrailingStrength = boundaries[i + 1].Strength,
            });
        }

        return BuildCandidates(CoalesceSameClass(classified), repeat, history, historyOptions, options);
    }

    /// <summary>裏付けのある根拠が同じ隣接区間を1つにまとめる。
    /// 1つのCMが余分な境界で分断されていても、1件の候補として扱えるようにするため。
    ///
    /// 弱い根拠（音響急変のみ）の区間はまとめない。隣り合う弱い区間の間にあるのは
    /// 実際に検出された切れ目であり、まとめてしまうと「どこで切れるか」という
    /// この検出の主目的の情報を捨ててしまう（かつ結合後の巨大な区間は長さの確認レンジを
    /// 超えて候補から落ち、確認すべき行が一切出なくなる）。</summary>
    private static List<ClassifiedSegment> CoalesceSameClass(List<ClassifiedSegment> segments)
    {
        var coalesced = new List<ClassifiedSegment>();
        foreach (var segment in segments)
        {
            if (coalesced.Count > 0 && coalesced[^1].Class == segment.Class && segment.Class != SegmentClass.Weak)
            {
                var last = coalesced[^1];
                last.Segment = new AudioSegment(last.Segment.Start, segment.Segment.End);
                last.ToFrame = segment.ToFrame;
                last.TrailingStrength = segment.TrailingStrength;
                continue;
            }

            coalesced.Add(segment);
        }

        return coalesced;
    }

    private static List<CmCandidate> BuildCandidates(
        List<ClassifiedSegment> segments,
        RepeatEvidence repeat,
        HistoryMatchScores history,
        HistoryMatchOptions historyOptions,
        SegmentClassificationOptions options)
    {
        var candidates = new List<CmCandidate>();
        foreach (var segment in segments)
        {
            var durationSeconds = segment.Segment.Duration.TotalSeconds;
            if (durationSeconds <= options.MinFinalCandidateSeconds) continue;

            var candidate = segment.Class switch
            {
                SegmentClass.Repeat => BuildRepeatCandidate(segment, repeat),
                SegmentClass.History => BuildHistoryCandidate(segment, history, historyOptions),
                _ => BuildWeakCandidate(segment, durationSeconds, options),
            };

            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    /// <summary>確信度・繰り返し回数は、結合後の範囲について元の配列から再計算する
    /// （平均値の平均を取ると、長さの異なる区間同士でバイアスが乗るため）。</summary>
    private static CmCandidate BuildRepeatCandidate(ClassifiedSegment segment, RepeatEvidence repeat)
    {
        var hitFrames = 0;
        var totalHits = 0;
        var totalScore = 0.0;
        for (var i = segment.FromFrame; i < segment.ToFrame; i++)
        {
            if (repeat.HitCount[i] == 0) continue;
            hitFrames++;
            totalHits += repeat.HitCount[i];
            totalScore += repeat.HitScoreSum[i];
        }

        return new CmCandidate
        {
            Segment = segment.Segment,
            Confidence = totalHits > 0 ? Math.Clamp(totalScore / totalHits, 0, 1) : 0,
            RepeatCount = hitFrames > 0 ? totalHits / hitFrames : 0,
            Reason = DetectionReason.RepeatedContent,
            CutEnabled = true,
        };
    }

    private static CmCandidate BuildHistoryCandidate(
        ClassifiedSegment segment, HistoryMatchScores history, HistoryMatchOptions historyOptions)
    {
        var matchedFrames = 0;
        var totalScore = 0.0;
        for (var i = segment.FromFrame; i < segment.ToFrame; i++)
        {
            if (!IsHistoryMatch(history, historyOptions, i)) continue;
            matchedFrames++;
            totalScore += history.ConfirmedCm[i];
        }

        return new CmCandidate
        {
            Segment = segment.Segment,
            Confidence = matchedFrames > 0 ? Math.Clamp(totalScore / matchedFrames, 0, 1) : 0,
            RepeatCount = 0,
            Reason = DetectionReason.HistoryMatch,
            CutEnabled = true,
        };
    }

    /// <summary>音響急変のみを根拠とする区間。実データ検証で本編（曲・コーナー転換等）の誤検出が
    /// 多かったため、デフォルトではカット対象にしない（ユーザーが手動でONにする）。
    /// 確認する価値のある長さの範囲外なら候補にもしない。</summary>
    private static CmCandidate? BuildWeakCandidate(
        ClassifiedSegment segment, double durationSeconds, SegmentClassificationOptions options)
    {
        if (durationSeconds < options.MinSegmentSeconds || durationSeconds > options.MaxSegmentSeconds)
        {
            return null;
        }

        var strength = (segment.LeadingStrength + segment.TrailingStrength) / 2;
        var baseConfidence = Math.Clamp(0.5 * Math.Min(strength, 2.0), 0, 1);
        var isCmLength = BoundaryDetector.IsCloseToSpotLength(
            durationSeconds, options.SpotUnitSeconds, options.SpotLengthToleranceSeconds);

        return new CmCandidate
        {
            Segment = segment.Segment,
            // CM尺から外れる長尺の区間は、番組内の音楽（本編）である可能性を考慮して確信度を下げる
            Confidence = isCmLength ? baseConfidence : baseConfidence * 0.6,
            RepeatCount = 0,
            Reason = isCmLength ? DetectionReason.AcousticTransitionCmLength : DetectionReason.AcousticTransitionLong,
            CutEnabled = false,
        };
    }

    private static bool IsHistoryMatch(HistoryMatchScores history, HistoryMatchOptions options, int frameIndex)
    {
        // 確定非CM側の方が近い場合は、繰り返しがちな誤検出パターンとみなして一致としない
        return history.ConfirmedCm[frameIndex] >= options.SimilarityThreshold
            && history.ConfirmedCm[frameIndex] > history.Rejected[frameIndex];
    }

    private static bool HasQualifyingRun(int fromFrame, int toFrame, int minRunFrames, Func<int, bool> isMatch)
    {
        if (minRunFrames <= 0) return false;

        var run = 0;
        for (var i = fromFrame; i < toFrame; i++)
        {
            run = isMatch(i) ? run + 1 : 0;
            if (run >= minRunFrames) return true;
        }

        return false;
    }

    private static int ToFrameCount(double seconds) =>
        Math.Max(1, (int)(seconds / FeatureExtractor.FrameSeconds));

    private sealed class ClassifiedSegment
    {
        public required AudioSegment Segment { get; set; }
        public required int FromFrame { get; init; }
        public required int ToFrame { get; set; }
        public required SegmentClass Class { get; init; }
        public required double LeadingStrength { get; init; }
        public required double TrailingStrength { get; set; }
    }
}
