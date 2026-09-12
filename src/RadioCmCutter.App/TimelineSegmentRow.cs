using RadioCmCutter.Core.Ffmpeg;
using RadioCmCutter.Core.Models;

namespace RadioCmCutter.App;

/// <summary>
/// 画面表示用: ファイル全体を「CM候補区間」と「その間（検出されなかった区間）」に途切れなく分割した1行。
/// CM候補は検出結果由来でCutEnabledの初期値がON、間の区間は検出されていないためOFFで始まる
/// （ユーザーがONにすれば、検出漏れの区間を手動でカット対象に追加できる）。
/// </summary>
public sealed class TimelineSegmentRow
{
    /// <summary>検出境界のズレ等で生じるごく短い「検出外」区間は、単独表示せず前後どちらかの
    /// CM候補に吸収させる。この秒数以下の隙間のみが対象（意味のありそうな短い非CM区間まで
    /// 飲み込まないよう小さめに設定）。</summary>
    private const double MaxBridgedGapSeconds = 2.0;

    public required AudioSegment Segment { get; set; }
    public bool CutEnabled { get; set; }
    public double Confidence { get; init; }
    public int RepeatCount { get; init; }
    public required bool IsDetectedCandidate { get; init; }
    public DetectionReason? Reason { get; init; }

    /// <summary>ユーザーが手動で区切りを追加・結合した区間。
    /// 検出結果ではなく人の判断なので、表示で区別できるようにする（将来は検出精度の評価用の
    /// 正解データとしても使える）。</summary>
    public bool IsUserEdited { get; init; }

    public string Kind
    {
        get
        {
            var baseKind = Reason switch
            {
                DetectionReason.RepeatedContent => "繰り返し検出",
                DetectionReason.AcousticTransitionCmLength => "音量/音色変化(CM尺)",
                DetectionReason.AcousticTransitionLong => "音量/音色変化(長尺・音楽?)",
                DetectionReason.HistoryMatch => "過去の確定履歴と一致",
                _ => IsDetectedCandidate ? "検出" : "（検出外）",
            };
            return IsUserEdited ? $"{baseKind}（手動）" : baseKind;
        }
    }

    /// <summary>指定時刻で2つに分ける。区切りを手動で追加するときに使う。</summary>
    public (TimelineSegmentRow Before, TimelineSegmentRow After) SplitAt(TimeSpan at)
    {
        var before = CloneWith(new AudioSegment(Segment.Start, at));
        var after = CloneWith(new AudioSegment(at, Segment.End));
        return (before, after);
    }

    /// <summary>隣り合う2区間を1つにまとめる（＝間の区切りを取り除く）。</summary>
    public static TimelineSegmentRow Merge(TimelineSegmentRow first, TimelineSegmentRow second) =>
        new()
        {
            Segment = new AudioSegment(first.Segment.Start, second.Segment.End),
            // 結合前で判断が分かれている場合は「カットしない」側に倒す。
            // 本編とCMを1つにまとめた結果、本編まで削ってしまう事故を避けるため。
            CutEnabled = first.CutEnabled && second.CutEnabled,
            Confidence = Math.Max(first.Confidence, second.Confidence),
            RepeatCount = Math.Max(first.RepeatCount, second.RepeatCount),
            IsDetectedCandidate = first.IsDetectedCandidate || second.IsDetectedCandidate,
            Reason = first.Reason == second.Reason ? first.Reason : null,
            IsUserEdited = true,
        };

    private TimelineSegmentRow CloneWith(AudioSegment segment) =>
        new()
        {
            Segment = segment,
            CutEnabled = CutEnabled,
            Confidence = Confidence,
            RepeatCount = RepeatCount,
            IsDetectedCandidate = IsDetectedCandidate,
            Reason = Reason,
            IsUserEdited = true,
        };

    /// <summary>検出されたCM候補区間の一覧から、ファイル全体を途切れなくカバーする表示行を作る。</summary>
    public static List<TimelineSegmentRow> Build(TimeSpan totalDuration, IReadOnlyList<CmCandidate> candidates)
    {
        var candidateRows = candidates.Select(c => new TimelineSegmentRow
        {
            Segment = c.Segment,
            CutEnabled = c.CutEnabled,
            Confidence = c.Confidence,
            RepeatCount = c.RepeatCount,
            IsDetectedCandidate = true,
            Reason = c.Reason,
        });

        var gapSegments = AudioCutter.ComputeKeptSegments(totalDuration, candidates.Select(c => c.Segment));
        var gapRows = gapSegments.Select(s => new TimelineSegmentRow
        {
            Segment = s,
            CutEnabled = false,
            Confidence = 0,
            RepeatCount = 0,
            IsDetectedCandidate = false,
        });

        var rows = candidateRows.Concat(gapRows).OrderBy(r => r.Segment.Start).ToList();
        return BridgeTinyGaps(rows);
    }

    /// <summary>検出境界のズレによるごく短い「検出外」の隙間を、前後のCM候補の一方に吸収させて
    /// 一覧から消す。両隣にカット候補がある場合はカットOFF側（安全側）を優先して延長し、
    /// 片方しか候補がない場合はそちらへ延長する。</summary>
    private static List<TimelineSegmentRow> BridgeTinyGaps(List<TimelineSegmentRow> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.IsDetectedCandidate || row.Segment.Duration.TotalSeconds > MaxBridgedGapSeconds)
            {
                continue;
            }

            var prev = i > 0 ? rows[i - 1] : null;
            var next = i < rows.Count - 1 ? rows[i + 1] : null;
            var prevIsCandidate = prev is { IsDetectedCandidate: true };
            var nextIsCandidate = next is { IsDetectedCandidate: true };
            if (!prevIsCandidate && !nextIsCandidate)
            {
                continue;
            }

            var extendPrev = prevIsCandidate && (!nextIsCandidate || !prev!.CutEnabled || next!.CutEnabled);
            if (extendPrev)
            {
                prev!.Segment = new AudioSegment(prev.Segment.Start, row.Segment.End);
            }
            else
            {
                next!.Segment = new AudioSegment(row.Segment.Start, next.Segment.End);
            }

            rows.RemoveAt(i);
            i--;
        }

        return rows;
    }
}
