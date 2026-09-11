using RadioCmCutter.Core.Models;

namespace RadioCmCutter.Core.Detection;

public sealed class RepeatDetectionOptions
{
    /// <summary>コサイン類似度がこの値以上ならフレーム同士が「一致」とみなす。</summary>
    public double SimilarityThreshold { get; init; } = 0.93;

    /// <summary>一致とみなす連続フレーム run の最小長（秒）。短すぎる一致（相槌等）を除外する。</summary>
    public double MinRunSeconds { get; init; } = 8.0;

    /// <summary>この間隔（秒）より近いフレーム同士は「同じ場所」とみなし比較対象から除外する。</summary>
    public double MinSeparationSeconds { get; init; } = 30.0;
}

/// <summary>
/// フレーム特徴列の自己相似行列から、繰り返し出現する区間（CM候補）を検出する。
/// 単一ファイル内の繰り返しだけでなく、複数ファイルのフレームを連結して渡せば
/// 「別日の録音間での繰り返し」も同じロジックで検出できる（呼び出し側でファイル境界を管理する）。
/// </summary>
public static class RepeatSegmentDetector
{
    public static List<CmCandidate> Detect(IReadOnlyList<FrameFeatures> frames, RepeatDetectionOptions? options = null)
    {
        var (hitCount, hitScoreSum) = ComputeHits(frames, options ?? new RepeatDetectionOptions());
        return BuildCandidates(frames, hitCount, hitScoreSum);
    }

    /// <summary>
    /// フレームごとの一致回数・類似度合計を計算する。
    /// 複数ファイルのフレームを連結して渡すことで、ファイル間の繰り返し（別日の同一CM等）も検出できる。
    /// 呼び出し側でファイル境界ごとに結果を切り出し、<see cref="BuildCandidates"/> に渡すこと
    /// （境界をまたいで候補区間を作らないようにするため）。
    /// </summary>
    public static (int[] HitCount, double[] HitScoreSum) ComputeHits(
        IReadOnlyList<FrameFeatures> frames, RepeatDetectionOptions? options = null)
    {
        options ??= new RepeatDetectionOptions();
        var n = frames.Count;
        var hitCount = new int[n];
        var hitScoreSum = new double[n];
        if (n == 0) return (hitCount, hitScoreSum);

        var minSeparationFrames = (int)(options.MinSeparationSeconds / FeatureExtractor.FrameSeconds);
        var minRunFrames = Math.Max(1, (int)(options.MinRunSeconds / FeatureExtractor.FrameSeconds));

        for (var i = 0; i < n; i++)
        {
            for (var j = i + minSeparationFrames; j < n; j++)
            {
                var sim = CosineSimilarity(frames[i].Vector, frames[j].Vector);
                if (sim < options.SimilarityThreshold) continue;

                // 一致点(i,j)を起点に、対角方向にどこまで一致run が続くか調べる
                var runLength = ExtendRun(frames, i, j, options.SimilarityThreshold);
                if (runLength < minRunFrames) continue;

                for (var k = 0; k < runLength; k++)
                {
                    hitCount[i + k]++;
                    hitCount[j + k]++;
                    hitScoreSum[i + k] += sim;
                    hitScoreSum[j + k] += sim;
                }
            }
        }

        return (hitCount, hitScoreSum);
    }

    private static int ExtendRun(IReadOnlyList<FrameFeatures> frames, int i, int j, double threshold)
    {
        var length = 0;
        while (j + length < frames.Count &&
               CosineSimilarity(frames[i + length].Vector, frames[j + length].Vector) >= threshold)
        {
            length++;
        }
        return length;
    }

    public static List<CmCandidate> BuildCandidates(
        IReadOnlyList<FrameFeatures> frames, int[] hitCount, double[] hitScoreSum)
    {
        var candidates = new List<CmCandidate>();
        var i = 0;
        while (i < frames.Count)
        {
            if (hitCount[i] == 0)
            {
                i++;
                continue;
            }

            var start = i;
            var totalHits = 0;
            var totalScore = 0.0;
            while (i < frames.Count && hitCount[i] > 0)
            {
                totalHits += hitCount[i];
                totalScore += hitScoreSum[i];
                i++;
            }

            var segment = new AudioSegment(frames[start].Start, frames[i - 1].End);
            var avgSimilarity = totalScore / totalHits;
            candidates.Add(new CmCandidate
            {
                Segment = segment,
                Confidence = Math.Clamp(avgSimilarity, 0, 1),
                RepeatCount = totalHits / Math.Max(1, i - start),
                Reason = DetectionReason.RepeatedContent,
            });
        }

        return candidates;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0;
        for (var k = 0; k < a.Length; k++)
        {
            dot += a[k] * b[k];
        }
        return dot; // 各ベクトルは既にL2正規化済みなのでdotがコサイン類似度そのもの
    }
}
