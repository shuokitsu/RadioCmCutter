using RadioCmCutter.Core.Detection;
using RadioCmCutter.Core.Ffmpeg;
using RadioCmCutter.Core.Models;

namespace RadioCmCutter.Core.Pipeline;

/// <summary>
/// 「デコード→特徴抽出→繰り返し検出→音量境界の精緻化」までを一括で行う。
/// 単一ファイルモードと、複数ファイルをまとめて処理するディレクトリモード（ファイル間の繰り返しも検出）に対応。
/// </summary>
public sealed class CmDetectionPipeline(RepeatDetectionOptions? options = null)
{
    private readonly RepeatDetectionOptions _options = options ?? new RepeatDetectionOptions();

    public async Task<DetectionResult> DetectSingleAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var audio = await AudioDecoder.DecodeForAnalysisAsync(filePath, cancellationToken);

        // 特徴抽出・繰り返し検出はCPU負荷が高い同期処理のため、呼び出し元スレッド（UIスレッド等）を
        // ブロックしないようバックグラウンドスレッドに退避させる。
        var candidates = await Task.Run(() =>
        {
            var frames = FeatureExtractor.Extract(audio);
            var result = RepeatSegmentDetector.Detect(frames, _options);
            LoudnessBoundaryRefiner.Refine(result, audio);
            return result;
        }, cancellationToken);

        return new DetectionResult
        {
            SourceFilePath = filePath,
            TotalDuration = audio.Duration,
            Candidates = candidates,
        };
    }

    /// <summary>
    /// 複数ファイルを一括処理する。ファイル内の繰り返しに加え、ファイル間（別日の同一CM等）の繰り返しも検出対象にする。
    /// </summary>
    public async Task<List<DetectionResult>> DetectBatchAsync(
        IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        var decodedAudios = new List<DecodedAudio>(filePaths.Count);
        var framesByFile = new List<List<FrameFeatures>>(filePaths.Count);

        foreach (var path in filePaths)
        {
            var audio = await AudioDecoder.DecodeForAnalysisAsync(path, cancellationToken);
            decodedAudios.Add(audio);
            framesByFile.Add(FeatureExtractor.Extract(audio));
        }

        return await Task.Run(() =>
        {
            var combinedFrames = framesByFile.SelectMany(f => f).ToList();
            var (hitCount, hitScoreSum) = RepeatSegmentDetector.ComputeHits(combinedFrames, _options);

            var results = new List<DetectionResult>(filePaths.Count);
            var frameOffset = 0;
            for (var fileIndex = 0; fileIndex < filePaths.Count; fileIndex++)
            {
                var frames = framesByFile[fileIndex];
                var hitCountSlice = hitCount.Skip(frameOffset).Take(frames.Count).ToArray();
                var hitScoreSumSlice = hitScoreSum.Skip(frameOffset).Take(frames.Count).ToArray();
                frameOffset += frames.Count;

                var candidates = RepeatSegmentDetector.BuildCandidates(frames, hitCountSlice, hitScoreSumSlice);
                LoudnessBoundaryRefiner.Refine(candidates, decodedAudios[fileIndex]);

                results.Add(new DetectionResult
                {
                    SourceFilePath = filePaths[fileIndex],
                    TotalDuration = decodedAudios[fileIndex].Duration,
                    Candidates = candidates,
                });
            }

            return results;
        }, cancellationToken);
    }
}
