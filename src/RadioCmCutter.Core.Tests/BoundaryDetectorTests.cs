using RadioCmCutter.Core.Detection;
using Xunit;

namespace RadioCmCutter.Core.Tests;

public class BoundaryDetectorTests
{
    private const double FrameSeconds = FeatureExtractor.FrameSeconds; // 0.5秒

    [Fact]
    public void DetectBoundaries_SustainedSpectralChange_IsReportedAsBoundary()
    {
        // 30秒地点で音の性質が持続的に切り替わる（帯域Aの音→帯域Bの音）合成データ。
        // 前後の窓の平均が明確に異なるため、その1点だけが境界として検出されるべき。
        var frames = BuildFrames(totalFrames: 120, switchAt: 60);

        var boundaries = BoundaryDetector.DetectBoundaries(frames, new BoundaryDetectionOptions
        {
            NoveltyWindowSeconds = 4.0,
            MinBoundaryGapSeconds = 2.0,
        });

        var boundary = Assert.Single(boundaries);
        Assert.Equal(TimeSpan.FromSeconds(60 * FrameSeconds), boundary.Position);
    }

    [Fact]
    public void DetectBoundaries_SustainedChange_WinsOverTransientEvent()
    {
        // 持続的な切り替わり（60フレーム目）と、1フレームだけ別の音が混じる局所的な出来事
        // （30フレーム目。連続BGM中の話し始め等に相当）が同じファイルにある状態。
        // 前後の窓で平均するため、局所的な出来事のスコアは持続的な変化に比べて桁違いに小さくなり、
        // 境界としては持続的な変化だけが残る。
        var frames = BuildFrames(totalFrames: 120, switchAt: 60, transientAt: 30);

        var boundaries = BoundaryDetector.DetectBoundaries(frames, new BoundaryDetectionOptions
        {
            NoveltyWindowSeconds = 4.0,
            MinBoundaryGapSeconds = 2.0,
        });

        var boundary = Assert.Single(boundaries);
        Assert.Equal(TimeSpan.FromSeconds(60 * FrameSeconds), boundary.Position);

        // スコアそのものも確認（持続的な変化は1.0、局所的な出来事はその1/10以下）
        var scores = BoundaryDetector.ComputeNoveltyScores(frames, windowFrames: 8);
        Assert.True(
            scores[60] > scores[30] * 10,
            $"持続的な変化({scores[60]:F3})が局所的な出来事({scores[30]:F3})を十分に上回っていません");
    }

    [Fact]
    public void DetectBoundaries_UniformAudio_ReturnsNoBoundaries()
    {
        // 変化が全く無い入力では、境界を作らずファイル全体を1区間として扱わせる
        var frames = BuildFrames(totalFrames: 60, switchAt: null);

        Assert.Empty(BoundaryDetector.DetectBoundaries(frames));
    }

    [Fact]
    public void DetectBoundaries_FramesShorterThanWindow_ReturnsNoBoundaries()
    {
        var frames = BuildFrames(totalFrames: 4, switchAt: 2);

        Assert.Empty(BoundaryDetector.DetectBoundaries(frames, new BoundaryDetectionOptions
        {
            NoveltyWindowSeconds = 8.0,
        }));
    }

    [Fact]
    public void CollapseCloseBoundaries_KeepsStrongestWithinGap()
    {
        // 2秒以内に密集した3点は、最も変化が強い1点（11.5秒・強さ5.0）に畳まれる
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
    [InlineData(15.0, 15.0, 3.0, true)]   // ちょうどCMスポット尺
    [InlineData(28.5, 15.0, 3.0, true)]   // 30秒に1.5秒差で近い
    [InlineData(306.4, 15.0, 3.0, false)] // 実データで誤判定されていた本編区間（300秒から6.4秒差）
    [InlineData(22.0, 15.0, 3.0, false)]  // 15秒・30秒どちらからも遠い
    public void IsCloseToSpotLength_MatchesOnlyNearMultiples(
        double durationSeconds, double unitSeconds, double toleranceSeconds, bool expected)
    {
        Assert.Equal(expected, BoundaryDetector.IsCloseToSpotLength(durationSeconds, unitSeconds, toleranceSeconds));
    }

    /// <summary>switchAt以降は別の帯域にエネルギーを持つ（＝音の性質が変わる）フレーム列を作る。
    /// transientAtを指定すると、その1フレームだけ別の帯域にする（局所的な出来事）。</summary>
    private static List<FrameFeatures> BuildFrames(int totalFrames, int? switchAt, int? transientAt = null)
    {
        var frames = new List<FrameFeatures>(totalFrames);
        for (var i = 0; i < totalFrames; i++)
        {
            var band = i == transientAt ? 2 : (switchAt.HasValue && i >= switchAt.Value ? 1 : 0);
            var spectral = new float[3];
            spectral[band] = 1f;

            var start = TimeSpan.FromSeconds(i * FrameSeconds);
            frames.Add(new FrameFeatures
            {
                Start = start,
                End = start + TimeSpan.FromSeconds(FrameSeconds),
                Vector = spectral,
                SpectralVector = spectral,
            });
        }
        return frames;
    }
}
