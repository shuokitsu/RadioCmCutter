namespace RadioCmCutter.Core.Detection;

/// <summary>カット位置の候補となる1点。<see cref="Strength"/>は検出閾値に対する変化の強さの比
/// （1.0で閾値ぎりぎり、大きいほど明確な切れ目）。</summary>
public readonly record struct BoundaryPoint(TimeSpan Position, double Strength);

public sealed class BoundaryDetectionOptions
{
    /// <summary>変化スコアがこの上位パーセンタイル以上のフレーム境界を「カット位置候補」とみなす。
    /// この段階ではCM判定をしないため、取りこぼし（見逃し）を避ける方向に振っている
    /// （余分に拾った境界は、後段のCM判定で「CMではない」と分類されるだけで害が小さい）。</summary>
    public double ChangeScorePercentile { get; init; } = 0.88;

    /// <summary>ラウドネスの変化をスペクトル変化と合成する際の重み。
    /// 会話は抑揚だけでも音量が大きく揺れるため、音色（スペクトル）変化より重みを下げている。</summary>
    public double RmsJumpWeight { get; init; } = 1.5;

    /// <summary>この秒数以内に密集した境界候補は1つにまとめる（過剰な細分化を防ぐ）。</summary>
    public double MinBoundaryGapSeconds { get; init; } = 2.0;
}

/// <summary>
/// 音量・音色の急変から「カット位置の候補」だけを検出する（CMかどうかの判定はしない）。
/// 「どこで切るか」と「それがCMか」を分離するための第1段階で、検出された境界は
/// <see cref="LoudnessBoundaryRefiner.RefineBoundaries"/> で精緻化された後、
/// <see cref="SegmentClassifier"/> が区間ごとにCM判定を行う共通の土台になる。
/// </summary>
public static class BoundaryDetector
{
    /// <summary>カット位置候補を時刻順に返す（ファイル先頭・末尾は含まない）。</summary>
    public static List<BoundaryPoint> DetectBoundaries(
        IReadOnlyList<FrameFeatures> frames, BoundaryDetectionOptions? options = null)
    {
        options ??= new BoundaryDetectionOptions();
        if (frames.Count < 4) return [];

        var changeScores = ComputeChangeScores(frames, options.RmsJumpWeight);

        // 先頭フレームは変化量が定義できない（常に0）ため、閾値の算出からは除外する
        var threshold = Percentile(changeScores.Skip(1), options.ChangeScorePercentile);
        if (threshold <= 0) return [];

        var detected = new List<BoundaryPoint>();
        for (var i = 1; i < frames.Count; i++)
        {
            if (changeScores[i] >= threshold)
            {
                detected.Add(new BoundaryPoint(frames[i].Start, changeScores[i] / threshold));
            }
        }

        return CollapseCloseBoundaries(detected, options.MinBoundaryGapSeconds);
    }

    /// <summary>フレーム境界ごとの変化スコア（スペクトル変化＋音量の跳ね）。</summary>
    public static double[] ComputeChangeScores(IReadOnlyList<FrameFeatures> frames, double rmsJumpWeight)
    {
        var scores = new double[frames.Count];
        for (var i = 1; i < frames.Count; i++)
        {
            var rmsJump = Math.Abs(frames[i].Rms - frames[i - 1].Rms);
            scores[i] = frames[i].SpectralChangeMagnitude + (rmsJump * rmsJumpWeight);
        }
        return scores;
    }

    /// <summary>minGapSeconds以内に密集した境界候補を、最も変化が強い1点に畳む。
    /// 境界補正（局所最小へのスナップ）で再び近接し得るため、補正後にも再度適用する。</summary>
    public static List<BoundaryPoint> CollapseCloseBoundaries(
        IReadOnlyList<BoundaryPoint> boundaries, double minGapSeconds)
    {
        var collapsed = new List<BoundaryPoint>();
        var index = 0;
        while (index < boundaries.Count)
        {
            var clusterEnd = boundaries[index].Position + TimeSpan.FromSeconds(minGapSeconds);
            var strongest = boundaries[index];
            while (index < boundaries.Count && boundaries[index].Position < clusterEnd)
            {
                if (boundaries[index].Strength > strongest.Strength)
                {
                    strongest = boundaries[index];
                }
                index++;
            }
            collapsed.Add(strongest);
        }

        return collapsed;
    }

    /// <summary>durationが unitSeconds の倍数（15,30,45,60...）にtoleranceSeconds以内で近いかどうか。</summary>
    public static bool IsCloseToSpotLength(double durationSeconds, double unitSeconds, double toleranceSeconds)
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
