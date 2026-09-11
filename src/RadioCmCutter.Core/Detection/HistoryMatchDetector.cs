using RadioCmCutter.Core.History;
using RadioCmCutter.Core.Models;

namespace RadioCmCutter.Core.Detection;

public sealed class HistoryMatchOptions
{
    /// <summary>過去に確定した区間のフレームとのコサイン類似度がこの値以上なら「一致」とみなす。</summary>
    public double SimilarityThreshold { get; init; } = 0.93;

    /// <summary>一致とみなす連続フレームの最小秒数。CM全体ではなくBGM部分等の部分一致も
    /// 検出したいため、繰り返し検出（8秒）より短く設定している。</summary>
    public double MinRunSeconds { get; init; } = 3.0;
}

/// <summary>
/// ユーザーが過去に確定した「CMである/CMではない」区間の音響指紋（<see cref="CmHistoryStore"/>）と、
/// 今回のフレーム列を比較し、過去のCMと似ている区間を候補として検出する。
/// フレーム単位で比較するため、CMの一部（BGM部分のみ等）が共通していれば部分一致として拾える。
/// 番組を問わずグローバルに蓄積したデータを使うため、他番組で確定した同じCMにも効く。
/// </summary>
public static class HistoryMatchDetector
{
    public static List<CmCandidate> Detect(
        IReadOnlyList<FrameFeatures> frames, CmHistoryStore historyStore, HistoryMatchOptions? options = null)
    {
        options ??= new HistoryMatchOptions();
        if (frames.Count == 0) return [];

        var confirmedFrames = historyStore.ConfirmedCmEntries.SelectMany(e => e.FrameVectors).ToList();
        if (confirmedFrames.Count == 0) return [];

        var rejectedFrames = historyStore.RejectedEntries.SelectMany(e => e.FrameVectors).ToList();

        var confirmedScores = ComputeBestMatchScores(frames, confirmedFrames);
        var rejectedScores = rejectedFrames.Count > 0
            ? ComputeBestMatchScores(frames, rejectedFrames)
            : new double[frames.Count];

        var minRunFrames = Math.Max(1, (int)(options.MinRunSeconds / FeatureExtractor.FrameSeconds));
        var candidates = new List<CmCandidate>();

        var i = 0;
        while (i < frames.Count)
        {
            var isMatch = confirmedScores[i] >= options.SimilarityThreshold && confirmedScores[i] > rejectedScores[i];
            if (!isMatch)
            {
                i++;
                continue;
            }

            var start = i;
            var scoreSum = 0.0;
            var count = 0;
            while (i < frames.Count && confirmedScores[i] >= options.SimilarityThreshold && confirmedScores[i] > rejectedScores[i])
            {
                scoreSum += confirmedScores[i];
                count++;
                i++;
            }

            if (count < minRunFrames) continue;

            candidates.Add(new CmCandidate
            {
                Segment = new AudioSegment(frames[start].Start, frames[i - 1].End),
                Confidence = Math.Clamp(scoreSum / count, 0, 1),
                RepeatCount = 0,
                Reason = DetectionReason.HistoryMatch,
                CutEnabled = true,
            });
        }

        return candidates;
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
