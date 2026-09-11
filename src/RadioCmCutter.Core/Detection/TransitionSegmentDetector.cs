using RadioCmCutter.Core.Models;

namespace RadioCmCutter.Core.Detection;

public sealed class TransitionDetectionOptions
{
    /// <summary>変化スコアがこの上位パーセンタイル以上のフレーム境界を「急変点」とみなす。
    /// 値を大きくするほど「よほど大きな変化」だけを急変点とみなし、候補が減る。</summary>
    public double ChangeScorePercentile { get; init; } = 0.93;

    /// <summary>急変点同士の間がこの秒数未満なら、短すぎる（相槌・言葉の切れ目等）として候補にしない。
    /// 一般的なCM尺（15秒以上）を想定し、繰り返し検出側のMinRunSecondsと揃えている。</summary>
    public double MinSegmentSeconds { get; init; } = 8.0;

    /// <summary>急変点同士の間がこの秒数を超えたら候補にしない（番組内の1曲フルサイズ等、CMとしては長すぎるため）。</summary>
    public double MaxSegmentSeconds { get; init; } = 300.0;

    /// <summary>ラウドネスの変化をスペクトル変化と合成する際の重み。
    /// 会話は抑揚だけでも音量が大きく揺れるため、音色（スペクトル）変化より重みを下げている。</summary>
    public double RmsJumpWeight { get; init; } = 1.5;

    /// <summary>CMスポットの基本尺（秒）。日本のラジオCMは主にこの倍数（15/30/45/60秒）で構成される。</summary>
    public double SpotUnitSeconds { get; init; } = 15.0;

    /// <summary>区間の長さがCMスポット尺（SpotUnitSecondsの倍数）にこの秒数以内で近ければ「CM尺相当」とみなす。</summary>
    public double SpotLengthToleranceSeconds { get; init; } = 3.0;
}

/// <summary>
/// 繰り返し検出（同じ音源が複数回登場すること）に依存せず、「会話↔音楽のような音色・音量の急変」を
/// 手がかりにCM候補を検出する。学習済みモデルを使わない前提で、CMの前後にはトークと異なる
/// 音響的特徴（BGM・ジングル等）への切り替わりが起きやすいという経験則に基づく補助的な検出。
/// </summary>
public static class TransitionSegmentDetector
{
    public static List<CmCandidate> Detect(IReadOnlyList<FrameFeatures> frames, TransitionDetectionOptions? options = null)
    {
        options ??= new TransitionDetectionOptions();
        if (frames.Count < 4) return [];

        var changeScores = new double[frames.Count];
        for (var i = 1; i < frames.Count; i++)
        {
            var rmsJump = Math.Abs(frames[i].Rms - frames[i - 1].Rms);
            changeScores[i] = frames[i].SpectralChangeMagnitude + (rmsJump * options.RmsJumpWeight);
        }

        var threshold = Percentile(changeScores.Skip(1), options.ChangeScorePercentile);
        if (threshold <= 0) return [];

        var transitionIndices = new List<int>();
        for (var i = 1; i < frames.Count; i++)
        {
            if (changeScores[i] >= threshold)
            {
                transitionIndices.Add(i);
            }
        }

        var candidates = new List<CmCandidate>();
        for (var k = 0; k < transitionIndices.Count - 1; k++)
        {
            var startIdx = transitionIndices[k];
            var endIdx = transitionIndices[k + 1];
            var duration = (frames[endIdx].Start - frames[startIdx].Start).TotalSeconds;
            if (duration < options.MinSegmentSeconds || duration > options.MaxSegmentSeconds)
            {
                continue;
            }

            var strength = (changeScores[startIdx] + changeScores[endIdx]) / (2 * threshold);
            var baseConfidence = Math.Clamp(0.5 * Math.Min(strength, 2.0), 0, 1);
            var isCmLength = IsCloseToSpotLength(duration, options.SpotUnitSeconds, options.SpotLengthToleranceSeconds);

            candidates.Add(new CmCandidate
            {
                Segment = new AudioSegment(frames[startIdx].Start, frames[endIdx].Start),
                // CM尺（15秒の倍数）から外れる長尺の区間は、番組内の音楽（本編）である可能性を考慮して確信度を下げる。
                Confidence = isCmLength ? baseConfidence : baseConfidence * 0.6,
                RepeatCount = 0,
                Reason = isCmLength ? DetectionReason.AcousticTransitionCmLength : DetectionReason.AcousticTransitionLong,
                // 音響急変＋長さ一致だけでは実データ検証でも誤検出（本編の曲・コーナー転換等）が多かったため、
                // 長さ一致・長尺のいずれもデフォルトではカット対象にしない（ユーザーが必要に応じて手動でONにする）。
                CutEnabled = false,
            });
        }

        return candidates;
    }

    /// <summary>durationが unitSeconds の倍数（15,30,45,60...）にtoleranceSeconds以内で近いかどうか。
    /// 候補結合後（<see cref="Pipeline.CmDetectionPipeline"/>）の長さ再判定でも使うためinternal公開。</summary>
    internal static bool IsCloseToSpotLength(double durationSeconds, double unitSeconds, double toleranceSeconds)
    {
        if (unitSeconds <= 0) return false;
        var remainder = durationSeconds % unitSeconds;
        var distanceToNearestMultiple = Math.Min(remainder, unitSeconds - remainder);
        return distanceToNearestMultiple <= toleranceSeconds;
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Clamp(percentile * (sorted.Length - 1), 0, sorted.Length - 1);
        return sorted[index];
    }
}
