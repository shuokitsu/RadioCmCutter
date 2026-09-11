using RadioCmCutter.Core.Models;

namespace RadioCmCutter.Core.Detection;

public sealed class TransitionDetectionOptions
{
    /// <summary>変化スコアがこの上位パーセンタイル以上のフレーム境界を「急変点」とみなす。</summary>
    public double ChangeScorePercentile { get; init; } = 0.85;

    /// <summary>急変点同士の間がこの秒数未満なら、短すぎる（相槌等）として候補にしない。</summary>
    public double MinSegmentSeconds { get; init; } = 4.0;

    /// <summary>急変点同士の間がこの秒数を超えたら、CM尺として長すぎるため候補にしない。</summary>
    public double MaxSegmentSeconds { get; init; } = 90.0;

    /// <summary>ラウドネスの変化をスペクトル変化と合成する際の重み。</summary>
    public double RmsJumpWeight { get; init; } = 3.0;
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
            candidates.Add(new CmCandidate
            {
                Segment = new AudioSegment(frames[startIdx].Start, frames[endIdx].Start),
                Confidence = Math.Clamp(0.5 * Math.Min(strength, 2.0), 0, 1),
                RepeatCount = 0,
            });
        }

        return candidates;
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Clamp(percentile * (sorted.Length - 1), 0, sorted.Length - 1);
        return sorted[index];
    }
}
