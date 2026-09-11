using RadioCmCutter.Core.Ffmpeg;
using RadioCmCutter.Core.Models;
using Xunit;

namespace RadioCmCutter.Core.Tests;

public class AudioCutterTests
{
    [Fact]
    public void ComputeKeptSegments_NoRemovals_ReturnsWholeDuration()
    {
        var kept = AudioCutter.ComputeKeptSegments(TimeSpan.FromMinutes(10), []);

        Assert.Single(kept);
        Assert.Equal(TimeSpan.Zero, kept[0].Start);
        Assert.Equal(TimeSpan.FromMinutes(10), kept[0].End);
    }

    [Fact]
    public void ComputeKeptSegments_SingleRemovalInMiddle_ReturnsTwoKeptSegments()
    {
        var totalDuration = TimeSpan.FromMinutes(10);
        var removal = new AudioSegment(TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));

        var kept = AudioCutter.ComputeKeptSegments(totalDuration, [removal]);

        Assert.Equal(2, kept.Count);
        Assert.Equal((TimeSpan.Zero, TimeSpan.FromMinutes(4)), (kept[0].Start, kept[0].End));
        Assert.Equal((TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)), (kept[1].Start, kept[1].End));
    }

    [Fact]
    public void ComputeKeptSegments_OverlappingRemovals_AreMergedBeforeComplementing()
    {
        var totalDuration = TimeSpan.FromMinutes(10);
        var removals = new[]
        {
            new AudioSegment(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3.5)),
            new AudioSegment(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(4)), // 前の区間と重複
        };

        var kept = AudioCutter.ComputeKeptSegments(totalDuration, removals);

        Assert.Equal(2, kept.Count);
        Assert.Equal(TimeSpan.FromMinutes(2), kept[0].End);
        Assert.Equal(TimeSpan.FromMinutes(4), kept[1].Start);
    }

    [Fact]
    public void ComputeKeptSegments_RemovalCoversEntireFile_ReturnsNoKeptSegments()
    {
        var totalDuration = TimeSpan.FromMinutes(10);
        var removal = new AudioSegment(TimeSpan.Zero, totalDuration);

        var kept = AudioCutter.ComputeKeptSegments(totalDuration, [removal]);

        Assert.Empty(kept);
    }

    [Fact]
    public void ComputeKeptSegments_RemovalAtVeryEnd_ReturnsOnlyLeadingSegment()
    {
        var totalDuration = TimeSpan.FromMinutes(10);
        var removal = new AudioSegment(TimeSpan.FromMinutes(9), totalDuration);

        var kept = AudioCutter.ComputeKeptSegments(totalDuration, [removal]);

        Assert.Single(kept);
        Assert.Equal(TimeSpan.Zero, kept[0].Start);
        Assert.Equal(TimeSpan.FromMinutes(9), kept[0].End);
    }
}
