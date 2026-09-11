using RadioCmCutter.Core.History;

namespace RadioCmCutter.Core.Detection;

public sealed class HistoryMatchOptions
{
    /// <summary>過去に確定した区間のフレームとのコサイン類似度がこの値以上なら「一致」とみなす。</summary>
    public double SimilarityThreshold { get; init; } = 0.93;

    /// <summary>一致とみなす連続フレームの最小秒数。CM全体ではなくBGM部分等の部分一致も
    /// 検出したいため、繰り返し検出（8秒）より短く設定している。</summary>
    public double MinRunSeconds { get; init; } = 3.0;
}

/// <summary>フレームごとの、過去の確定区間との一致度。確定CM側・確定非CM側の両方を持つ。</summary>
public readonly record struct HistoryMatchScores(double[] ConfirmedCm, double[] Rejected)
{
    public static HistoryMatchScores Empty(int frameCount) => new(new double[frameCount], new double[frameCount]);
}

/// <summary>
/// ユーザーが過去に確定した「CMである/CMではない」区間の音響指紋（<see cref="CmHistoryStore"/>）と、
/// 今回のフレーム列をフレーム単位で比較する。フレーム単位で比較するため、CMの一部
/// （BGM部分のみ等）が共通していれば部分一致として拾える。
/// 番組を問わずグローバルに蓄積したデータを使うため、他番組で確定した同じCMにも効く。
/// 区間の切り出し（どこからどこまでをCM候補とするか）は行わず、
/// <see cref="SegmentClassifier"/> が共通のカット位置候補に沿って判定する。
/// </summary>
public static class HistoryMatchDetector
{
    public static HistoryMatchScores ComputeScores(
        IReadOnlyList<FrameFeatures> frames, CmHistoryStore? historyStore)
    {
        if (historyStore is null || frames.Count == 0) return HistoryMatchScores.Empty(frames.Count);

        var confirmedFrames = historyStore.ConfirmedCmEntries.SelectMany(e => e.FrameVectors).ToList();
        if (confirmedFrames.Count == 0) return HistoryMatchScores.Empty(frames.Count);

        var rejectedFrames = historyStore.RejectedEntries.SelectMany(e => e.FrameVectors).ToList();

        return new HistoryMatchScores(
            ComputeBestMatchScores(frames, confirmedFrames),
            rejectedFrames.Count > 0 ? ComputeBestMatchScores(frames, rejectedFrames) : new double[frames.Count]);
    }

    private static double[] ComputeBestMatchScores(IReadOnlyList<FrameFeatures> frames, List<float[]> historyFrames)
    {
        var scores = new double[frames.Count];
        for (var i = 0; i < frames.Count; i++)
        {
            var best = 0.0;
            var vector = frames[i].Vector;
            foreach (var historyVector in historyFrames)
            {
                var sim = CosineSimilarity(vector, historyVector);
                if (sim > best) best = sim;
            }
            scores[i] = best;
        }
        return scores;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0;
        var length = Math.Min(a.Length, b.Length);
        for (var k = 0; k < length; k++)
        {
            dot += a[k] * b[k];
        }
        return dot;
    }
}
