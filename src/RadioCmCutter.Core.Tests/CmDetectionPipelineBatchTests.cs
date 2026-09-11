using RadioCmCutter.Core.Detection;
using RadioCmCutter.Core.Ffmpeg;
using RadioCmCutter.Core.Models;
using RadioCmCutter.Core.Pipeline;
using Xunit;

namespace RadioCmCutter.Core.Tests;

/// <summary>
/// 複数ファイル一括処理では、繰り返し検出だけを全ファイル連結で計算し、結果をファイルごとに
/// 切り出して判定する。この切り出し位置がずれると、別ファイルの繰り返しの根拠を
/// 取り違えて誤った時刻をCM候補にしてしまうため、専用のテストで担保する。
/// </summary>
public class CmDetectionPipelineBatchTests
{
    private const double FrameSeconds = FeatureExtractor.FrameSeconds; // 0.5秒
    private const int SampleRate = 8000;
    private const int VectorDims = 200;

    [Fact]
    public void ClassifyDecodedBatch_SharedBlockAcrossFiles_IsAttributedToEachFilesOwnTimeline()
    {
        // ファイルA(30秒)の12〜18秒と、ファイルB(24秒)の6〜12秒に同一内容のブロック（別日の同一CM相当）を置く。
        // ファイル内では繰り返しと判定できない配置（ブロック長6秒 < 最小間隔15秒）にしてあるため、
        // 検出できるのはファイルを跨いだ一致のみ。
        var framesA = BuildFrames(totalFrames: 60, sharedBlock: (24, 36), fileTag: 0);
        var framesB = BuildFrames(totalFrames: 48, sharedBlock: (12, 24), fileTag: 1);

        var pipeline = new CmDetectionPipeline(
            new RepeatDetectionOptions
            {
                SimilarityThreshold = 0.99,
                MinRunSeconds = 3.0,       // 6フレーム
                MinSeparationSeconds = 15.0, // 30フレーム
            },
            new BoundaryDetectionOptions
            {
                ChangeScorePercentile = 0.99,
                MinBoundaryGapSeconds = 2.0,
            });

        var results = pipeline.ClassifyDecodedBatch(
            [BuildAudio(30), BuildAudio(24)],
            [framesA, framesB]);

        var repeatedInA = Assert.Single(results[0], c => c.Reason == DetectionReason.RepeatedContent);
        var repeatedInB = Assert.Single(results[1], c => c.Reason == DetectionReason.RepeatedContent);

        Assert.Equal(TimeSpan.FromSeconds(12), repeatedInA.Segment.Start);
        Assert.Equal(TimeSpan.FromSeconds(18), repeatedInA.Segment.End);
        Assert.Equal(TimeSpan.FromSeconds(6), repeatedInB.Segment.Start);
        Assert.Equal(TimeSpan.FromSeconds(12), repeatedInB.Segment.End);
    }

    [Fact]
    public void ClassifyDecodedBatch_CandidatesNeverExceedTheirOwnFileDuration()
    {
        var framesA = BuildFrames(totalFrames: 60, sharedBlock: (24, 36), fileTag: 0);
        var framesB = BuildFrames(totalFrames: 48, sharedBlock: (12, 24), fileTag: 1);
        TimeSpan[] durations = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(24)];

        var pipeline = new CmDetectionPipeline(
            new RepeatDetectionOptions { SimilarityThreshold = 0.99, MinRunSeconds = 3.0, MinSeparationSeconds = 15.0 },
            new BoundaryDetectionOptions { ChangeScorePercentile = 0.99, MinBoundaryGapSeconds = 2.0 });

        var results = pipeline.ClassifyDecodedBatch([BuildAudio(30), BuildAudio(24)], [framesA, framesB]);

        for (var fileIndex = 0; fileIndex < results.Count; fileIndex++)
        {
            Assert.All(results[fileIndex], candidate =>
            {
                Assert.True(candidate.Segment.Start >= TimeSpan.Zero);
                Assert.True(
                    candidate.Segment.End <= durations[fileIndex],
                    $"ファイル{fileIndex}の候補がファイル長を超えています: {candidate.Segment.End} > {durations[fileIndex]}");
            });
        }
    }

    /// <summary>共有ブロックのフレームは全ファイルで同一ベクトル、それ以外はファイル・位置ごとに固有の
    /// ベクトルにする（偶然の一致を避けるため）。ブロック境界にはスペクトル変化のスパイクを置く。</summary>
    private static List<FrameFeatures> BuildFrames(int totalFrames, (int Start, int End) sharedBlock, int fileTag)
    {
        var frames = new List<FrameFeatures>(totalFrames);
        for (var i = 0; i < totalFrames; i++)
        {
            var isShared = i >= sharedBlock.Start && i < sharedBlock.End;
            var vector = new float[VectorDims];
            vector[isShared ? 0 : 1 + (fileTag * totalFrames) + i] = 1f;

            var isBlockEdge = i == sharedBlock.Start || i == sharedBlock.End;
            var start = TimeSpan.FromSeconds(i * FrameSeconds);
            frames.Add(new FrameFeatures
            {
                Start = start,
                End = start + TimeSpan.FromSeconds(FrameSeconds),
                Vector = vector,
                Rms = 0.1,
                SpectralChangeMagnitude = isBlockEdge ? 10.0 : 0.1,
            });
        }
        return frames;
    }

    /// <summary>境界補正用の一定振幅の合成音声（無音が無いため境界はほぼ動かない）。</summary>
    private static DecodedAudio BuildAudio(double durationSeconds)
    {
        var samples = new float[(int)(durationSeconds * SampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i % 2 == 0 ? 0.5f : -0.5f;
        }
        return new DecodedAudio { SampleRate = SampleRate, Samples = samples };
    }
}
