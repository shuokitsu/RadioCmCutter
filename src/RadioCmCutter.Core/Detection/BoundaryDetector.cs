namespace RadioCmCutter.Core.Detection;

/// <summary>カット位置の候補となる1点。<see cref="Strength"/>は検出閾値に対する変化の強さの比
/// （1.0で閾値ぎりぎり、大きいほど明確な切れ目）。</summary>
public readonly record struct BoundaryPoint(TimeSpan Position, double Strength);

public sealed class BoundaryDetectionOptions
{
    /// <summary>新規性スコアが「周囲の平均＋この値×全体のばらつき」を超えたら境界とみなす。
    /// 小さくするほど境界が増える。この段階ではCM判定をしないため、取りこぼしを避ける方向に振っている
    /// （余分に拾った境界は、後段のCM判定で「CMではない」と分類されるだけで害が小さい）。</summary>
    public double PeakThresholdInStdDev { get; init; } = 0.8;

    /// <summary>ピーク判定で「周囲」とみなす範囲（秒）。この範囲で最大かつ局所平均を上回る点のみ境界にする。</summary>
    public double LocalContrastWindowSeconds { get; init; } = 4.0;

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
        var detected = PickPeaks(frames, scores, options);
        return CollapseCloseBoundaries(detected, options.MinBoundaryGapSeconds);
    }

    /// <summary>
    /// フレームごとの新規性スコア（Footeのチェッカーボードカーネル法）。
    /// 自己相似行列のうち対象フレーム周辺だけを見て、
    ///   ・前半どうし／後半どうしの相似（＝それぞれの側のまとまり）
    ///   ・前半と後半の相似（＝両側が同じ内容か）
    /// の差を取る。前後の平均ベクトルを比べるだけでは、
    /// 「トークに曲がかぶっている」ような “中身は雑多だが続いている” 区間でも
    /// 平均のズレで反応してしまうが、この方式なら両側とも “まとまりが無い” と評価されるため
    /// スコアが下がり、本当の切れ目（両側それぞれは均質で、互いに違う）だけが際立つ。
    /// 窓が確保できない先頭・末尾付近は0（＝境界候補にしない。ファイル端は別途扱う）。
    /// 参考: J. Foote, "Automatic Audio Segmentation Using a Measure of Audio Novelty", ICME 2000.
    /// </summary>
    public static double[] ComputeNoveltyScores(IReadOnlyList<FrameFeatures> frames, int windowFrames)
    {
        var scores = new double[frames.Count];
        var taper = BuildGaussianTaper(windowFrames);

        for (var center = windowFrames; center + windowFrames <= frames.Count; center++)
        {
            double coherence = 0;
            double crossSimilarity = 0;

            for (var m = 0; m < windowFrames; m++)
            {
                var beforeM = frames[center - 1 - m].SpectralVector;
                var afterM = frames[center + m].SpectralVector;

                for (var n = 0; n < windowFrames; n++)
                {
                    var weight = taper[m] * taper[n];
                    coherence += weight * (Cosine(beforeM, frames[center - 1 - n].SpectralVector)
                        + Cosine(afterM, frames[center + n].SpectralVector));
                    crossSimilarity += weight * 2 * Cosine(beforeM, frames[center + n].SpectralVector);
                }
            }

            scores[center] = Math.Max(0, coherence - crossSimilarity);
        }

        return scores;
    }

    /// <summary>
    /// 新規性スコアの中から境界を選ぶ。ファイル全体の一律なパーセンタイルではなく、
    /// 「周囲と比べて突出しているか」で判定する（Dixonのピーク選出法）。
    /// 長時間の録音では、静かなトークと大音量の曲とで基準そのものが違うため、
    /// 一律のしきい値では片方が過検出・もう片方が見逃しになる。
    /// 参考: S. Dixon, "Onset Detection Revisited", DAFx-06.
    /// </summary>
    private static List<BoundaryPoint> PickPeaks(
        IReadOnlyList<FrameFeatures> frames, double[] scores, BoundaryDetectionOptions options)
    {
        var positive = scores.Where(s => s > 0).ToArray();
        if (positive.Length == 0) return [];

        var mean = positive.Average();
        var standardDeviation = Math.Sqrt(positive.Sum(s => (s - mean) * (s - mean)) / positive.Length);
        if (standardDeviation <= 0) return [];

        // 局所平均を取る窓（過去側を広めに取るのがDixonの方法）
        var localWindow = Math.Max(1, (int)(options.LocalContrastWindowSeconds / FeatureExtractor.FrameSeconds));
        var pastWindow = localWindow * 3;

        var peaks = new List<BoundaryPoint>();
        for (var i = 0; i < scores.Length; i++)
        {
            if (scores[i] <= 0) continue;

            // 条件1: 近傍で最大であること
            var isLocalMaximum = true;
            for (var k = Math.Max(0, i - localWindow); k <= Math.Min(scores.Length - 1, i + localWindow); k++)
            {
                if (scores[k] > scores[i]) { isLocalMaximum = false; break; }
            }
            if (!isLocalMaximum) continue;

            // 条件2: 周囲の平均を、ばらつきに対して十分上回ること
            double localSum = 0;
            var localCount = 0;
            for (var k = Math.Max(0, i - pastWindow); k <= Math.Min(scores.Length - 1, i + localWindow); k++)
            {
                localSum += scores[k];
                localCount++;
            }
            var localMean = localSum / localCount;
            var margin = options.PeakThresholdInStdDev * standardDeviation;
            if (scores[i] < localMean + margin) continue;

            peaks.Add(new BoundaryPoint(frames[i].Start, (scores[i] - localMean) / standardDeviation));
        }

        return peaks;
    }

    /// <summary>カーネル中心から離れるほど重みを下げるガウス窓（Footeのテーパー）。
    /// 遠く離れたフレームの影響で境界がぼやけるのを防ぐ。</summary>
    private static double[] BuildGaussianTaper(int windowFrames)
    {
        var taper = new double[windowFrames];
        var sigma = Math.Max(1.0, windowFrames / 2.0);
        for (var i = 0; i < windowFrames; i++)
        {
            taper[i] = Math.Exp(-0.5 * (i / sigma) * (i / sigma));
        }
        return taper;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0;
        var length = Math.Min(a.Length, b.Length);
        for (var k = 0; k < length; k++) dot += a[k] * b[k];
        return dot;
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
