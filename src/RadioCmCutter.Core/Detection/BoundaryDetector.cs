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

    /// <summary>境界の前後それぞれ何秒を平均して比較するか。
    /// 隣接フレーム間（0.5秒）の瞬間的な変化を見ると、連続した同一BGMの中でも
    /// 話し始め・話し終わりや曲中の楽器・ボーカルの入りに反応して細かく切れてしまう。
    /// 数秒の窓で平均すると、そうした局所的な出来事ではなく「前後で持続的に音の性質が
    /// 変わったか」を見られる（実データ検証では、連続区間での過検出が約1/5になり、
    /// かつ実際の切れ目の位置精度も向上した）。</summary>
    public double NoveltyWindowSeconds { get; init; } = 8.0;

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

        var windowFrames = Math.Max(1, (int)(options.NoveltyWindowSeconds / FeatureExtractor.FrameSeconds));
        if (frames.Count < (windowFrames * 2) + 1) return [];

        var scores = ComputeNoveltyScores(frames, windowFrames);
        var threshold = Percentile(scores.Where(s => s > 0), options.ChangeScorePercentile);
        if (threshold <= 0) return [];

        var detected = new List<BoundaryPoint>();
        for (var i = 0; i < frames.Count; i++)
        {
            if (scores[i] >= threshold)
            {
                detected.Add(new BoundaryPoint(frames[i].Start, scores[i] / threshold));
            }
        }

        return CollapseCloseBoundaries(detected, options.MinBoundaryGapSeconds);
    }

    /// <summary>
    /// フレーム境界ごとの変化スコア。境界の前後 windowFrames 分のスペクトル形状の平均を取り、
    /// その非類似度（1 - コサイン類似度）を返す。
    /// 窓が確保できない先頭・末尾付近は0（＝境界候補にしない。ファイル端は別途扱う）。
    /// </summary>
    public static double[] ComputeNoveltyScores(IReadOnlyList<FrameFeatures> frames, int windowFrames)
    {
        var scores = new double[frames.Count];
        for (var i = windowFrames; i + windowFrames <= frames.Count; i++)
        {
            var before = MeanSpectralVector(frames, i - windowFrames, i);
            var after = MeanSpectralVector(frames, i, i + windowFrames);

            double similarity = 0;
            for (var k = 0; k < before.Length; k++)
            {
                similarity += before[k] * after[k];
            }
            scores[i] = 1.0 - similarity;
        }
        return scores;
    }

    private static double[] MeanSpectralVector(IReadOnlyList<FrameFeatures> frames, int fromFrame, int toFrame)
    {
        var mean = new double[frames[fromFrame].SpectralVector.Length];
        for (var i = fromFrame; i < toFrame; i++)
        {
            var vector = frames[i].SpectralVector;
            for (var k = 0; k < mean.Length && k < vector.Length; k++)
            {
                mean[k] += vector[k];
            }
        }

        double normSquared = 0;
        foreach (var value in mean) normSquared += value * value;
        var norm = Math.Sqrt(normSquared);
        if (norm > 1e-9)
        {
            for (var k = 0; k < mean.Length; k++) mean[k] /= norm;
        }

        return mean;
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
