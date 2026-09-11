using RadioCmCutter.Core.Detection;
using RadioCmCutter.Core.Ffmpeg;
using Xunit;

namespace RadioCmCutter.Core.Tests;

public class LoudnessBoundaryRefinerTests
{
    private const int SampleRate = 8000;

    [Fact]
    public void RefineBoundaries_SnapsToNearbySilence()
    {
        // 10秒地点に無音を置き、9秒の境界候補がそこへ引き寄せられることを確認する
        var audio = BuildAudio(durationSeconds: 30, silenceRanges: [(10.0, 10.2)]);

        var refined = LoudnessBoundaryRefiner.RefineBoundaries([new BoundaryPoint(TimeSpan.FromSeconds(9), 1.0)], audio);

        Assert.InRange(refined[0].Position.TotalSeconds, 10.0, 10.2);
        Assert.Equal(1.0, refined[0].Strength); // 強さは保持される
    }

    [Fact]
    public void RefineBoundaries_NeighboringBoundaries_DoNotCrossAfterSnapping()
    {
        // 2つの境界候補（11秒・13秒）の間の1箇所（12秒）だけが無音。
        // 双方が同じ無音に吸い寄せられても、順序が入れ替わったり同一位置に重なってはいけない。
        var audio = BuildAudio(durationSeconds: 30, silenceRanges: [(12.0, 12.2)]);

        var refined = LoudnessBoundaryRefiner.RefineBoundaries(
            [new BoundaryPoint(TimeSpan.FromSeconds(11), 1.0), new BoundaryPoint(TimeSpan.FromSeconds(13), 1.0)],
            audio);

        Assert.Equal(2, refined.Count);
        Assert.True(
            refined[0].Position < refined[1].Position,
            $"境界が追い越しています: {refined[0].Position} >= {refined[1].Position}");
    }

    [Fact]
    public void RefineBoundaries_WhenSearchWindowCollapses_KeepsOriginalPosition()
    {
        // 探索の刻み(0.02秒)より近い間隔で境界が詰まっており、探索範囲が潰れるケース。
        // 補正できない境界は元の位置のまま残し、長さ0や順序逆転の区間を作らない。
        var audio = BuildAudio(durationSeconds: 30, silenceRanges: [(10.0, 10.2)]);
        List<BoundaryPoint> boundaries =
        [
            new(TimeSpan.FromSeconds(10.0), 1.0),
            new(TimeSpan.FromSeconds(10.005), 1.0),
            new(TimeSpan.FromSeconds(10.01), 1.0),
        ];

        var refined = LoudnessBoundaryRefiner.RefineBoundaries(boundaries, audio);

        Assert.Equal(3, refined.Count);
        Assert.Equal(TimeSpan.FromSeconds(10.005), refined[1].Position); // 補正せず元の位置
        for (var i = 1; i < refined.Count; i++)
        {
            Assert.True(
                refined[i - 1].Position < refined[i].Position,
                $"境界の順序が崩れています: {refined[i - 1].Position} >= {refined[i].Position}");
        }
    }

    [Fact]
    public void RefineBoundaries_EmptyInput_ReturnsEmpty()
    {
        var audio = BuildAudio(durationSeconds: 10, silenceRanges: []);

        Assert.Empty(LoudnessBoundaryRefiner.RefineBoundaries([], audio));
    }

    /// <summary>指定範囲だけ無音、それ以外は一定振幅の合成音声を作る。</summary>
    private static DecodedAudio BuildAudio(double durationSeconds, (double Start, double End)[] silenceRanges)
    {
        var samples = new float[(int)(durationSeconds * SampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            var seconds = i / (double)SampleRate;
            var isSilent = silenceRanges.Any(r => seconds >= r.Start && seconds < r.End);
            samples[i] = isSilent ? 0f : (i % 2 == 0 ? 0.5f : -0.5f);
        }

        return new DecodedAudio { SampleRate = SampleRate, Samples = samples };
    }
}
