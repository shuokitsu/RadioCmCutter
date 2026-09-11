using RadioCmCutter.Core.Detection;
using Xunit;

namespace RadioCmCutter.Core.Tests;

public class BoundaryDetectorTests
{
    private const double FrameSeconds = FeatureExtractor.FrameSeconds; // 0.5秒

    [Fact]
    public void DetectBoundaries_SpikesInChangeScore_AreReportedAsBoundaries()
    {
        // 指定フレームだけ大きなスペクトル変化を持たせ、そこが切れ目として検出されることを確認する
        // （98フレームが変化量0.1、2フレームが10.0。上位1%だけを拾えばスパイクだけが残る）
        var frames = BuildFrames(totalFrames: 100, spikeIndices: [20, 60], baselineChange: 0.1);

        var boundaries = BoundaryDetector.DetectBoundaries(frames, new BoundaryDetectionOptions
        {
            ChangeScorePercentile = 0.99,
            MinBoundaryGapSeconds = 2.0,
        });

        Assert.Equal(
            [TimeSpan.FromSeconds(20 * FrameSeconds), TimeSpan.FromSeconds(60 * FrameSeconds)],
            boundaries.Select(b => b.Position));
    }

    [Fact]
    public void DetectBoundaries_UniformAudio_ReturnsNoBoundaries()
    {
        // 変化が全く無い（＝閾値が0になる）入力では、境界を作らずファイル全体を1区間として扱わせる
        var frames = BuildFrames(totalFrames: 40, spikeIndices: []);

        var boundaries = BoundaryDetector.DetectBoundaries(frames);

        Assert.Empty(boundaries);
    }

    [Fact]
    public void CollapseCloseBoundaries_KeepsStrongestWithinGap()
    {
        // 2秒以内に密集した3点は、最も変化が強い1点（12.0秒・強さ5.0）に畳まれる
        List<BoundaryPoint> boundaries =
        [
            new(TimeSpan.FromSeconds(10.0), 1.2),
            new(TimeSpan.FromSeconds(11.0), 3.0),
            new(TimeSpan.FromSeconds(11.5), 5.0),
            new(TimeSpan.FromSeconds(30.0), 1.1),
        ];

        var collapsed = BoundaryDetector.CollapseCloseBoundaries(boundaries, minGapSeconds: 2.0);

        Assert.Equal(2, collapsed.Count);
        Assert.Equal(TimeSpan.FromSeconds(11.5), collapsed[0].Position);
        Assert.Equal(5.0, collapsed[0].Strength);
        Assert.Equal(TimeSpan.FromSeconds(30.0), collapsed[1].Position);
    }

    [Theory]
    [InlineData(15.0, 15.0, 3.0, true)]  // ちょうどCMスポット尺
    [InlineData(28.5, 15.0, 3.0, true)]  // 30秒に1.5秒差で近い
    [InlineData(306.4, 15.0, 3.0, false)] // 実データで誤判定されていた本編区間（300秒から6.4秒差）
    [InlineData(22.0, 15.0, 3.0, false)] // 15秒・30秒どちらからも遠い
    public void IsCloseToSpotLength_MatchesOnlyNearMultiples(
        double durationSeconds, double unitSeconds, double toleranceSeconds, bool expected)
    {
        Assert.Equal(expected, BoundaryDetector.IsCloseToSpotLength(durationSeconds, unitSeconds, toleranceSeconds));
    }

    private static List<FrameFeatures> BuildFrames(int totalFrames, int[] spikeIndices, double baselineChange = 0.0)
    {
        var frames = new List<FrameFeatures>(totalFrames);
        for (var i = 0; i < totalFrames; i++)
        {
            var start = TimeSpan.FromSeconds(i * FrameSeconds);
            frames.Add(new FrameFeatures
            {
                Start = start,
                End = start + TimeSpan.FromSeconds(FrameSeconds),
                Vector = [1f],
                Rms = 0.1,
                SpectralChangeMagnitude = spikeIndices.Contains(i) ? 10.0 : baselineChange,
            });
        }
        return frames;
    }
}
