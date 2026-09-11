using RadioCmCutter.Core.Detection;
using Xunit;

namespace RadioCmCutter.Core.Tests;

public class RepeatSegmentDetectorTests
{
    private const double FrameSeconds = FeatureExtractor.FrameSeconds; // 0.5秒
    private const int VectorDims = 90;

    [Fact]
    public void Detect_TwoIdenticalBlocksFarApart_AreReportedAsTwoCandidates()
    {
        // frames[5..25) と frames[45..65) をまったく同じ特徴ベクトル（同一CM相当）にし、
        // それ以外のフレームは互いに無関係なベクトルにする。
        var frames = BuildSyntheticFrames(totalFrames: 80, repeatedRanges: [(5, 25), (45, 65)]);

        var options = new RepeatDetectionOptions
        {
            SimilarityThreshold = 0.99,
            MinRunSeconds = 2.0, // 4フレーム
            MinSeparationSeconds = 5.0, // 10フレーム
        };

        var candidates = RepeatSegmentDetector.Detect(frames, options);

        Assert.Equal(2, candidates.Count);

        var first = candidates.OrderBy(c => c.Segment.Start).First();
        var second = candidates.OrderBy(c => c.Segment.Start).Last();

        Assert.Equal(TimeSpan.FromSeconds(5 * FrameSeconds), first.Segment.Start);
        Assert.Equal(TimeSpan.FromSeconds(25 * FrameSeconds), first.Segment.End);
        Assert.Equal(TimeSpan.FromSeconds(45 * FrameSeconds), second.Segment.Start);
        Assert.Equal(TimeSpan.FromSeconds(65 * FrameSeconds), second.Segment.End);
    }

    [Fact]
    public void Detect_NoRepeats_ReturnsNoCandidates()
    {
        var frames = BuildSyntheticFrames(totalFrames: 40, repeatedRanges: []);

        var candidates = RepeatSegmentDetector.Detect(frames);

        Assert.Empty(candidates);
    }

    [Fact]
    public void Detect_RepeatShorterThanMinRun_IsIgnored()
    {
        // 1フレーム(0.5秒)だけの一致はCM(数秒〜数十秒)としては短すぎるため無視されるべき
        var frames = BuildSyntheticFrames(totalFrames: 40, repeatedRanges: [(5, 6), (25, 26)]);

        var options = new RepeatDetectionOptions
        {
            SimilarityThreshold = 0.99,
            MinRunSeconds = 2.0,
            MinSeparationSeconds = 5.0,
        };

        var candidates = RepeatSegmentDetector.Detect(frames, options);

        Assert.Empty(candidates);
    }

    private static List<FrameFeatures> BuildSyntheticFrames(int totalFrames, (int Start, int End)[] repeatedRanges)
    {
        var frames = new List<FrameFeatures>(totalFrames);
        for (var i = 0; i < totalFrames; i++)
        {
            var isRepeated = repeatedRanges.Any(r => i >= r.Start && i < r.End);
            var vector = new float[VectorDims];
            vector[isRepeated ? 0 : i + 1] = 1f; // one-hotベクトル。repeated範囲は全て同じ次元=同一内容とみなす

            var start = TimeSpan.FromSeconds(i * FrameSeconds);
            frames.Add(new FrameFeatures
            {
                Start = start,
                End = start + TimeSpan.FromSeconds(FrameSeconds),
                Vector = vector,
                Rms = 0.1,
            });
        }
        return frames;
    }
}
