using RadioCmCutter.Core.Detection;
using RadioCmCutter.Core.Ffmpeg;
using RadioCmCutter.Core.History;
using RadioCmCutter.Core.Models;

namespace RadioCmCutter.Core.Pipeline;

/// <summary>
/// 「デコード→特徴抽出→繰り返し検出＋急変点検出＋過去の確定履歴との照合→統合→音量境界の精緻化」までを
/// 一括で行う。単一ファイルモードと、複数ファイルをまとめて処理するディレクトリモード
/// （ファイル間の繰り返しも検出）に対応。
/// </summary>
public sealed class CmDetectionPipeline(
    RepeatDetectionOptions? repeatOptions = null,
    TransitionDetectionOptions? transitionOptions = null,
    CmHistoryStore? historyStore = null)
{
    private readonly RepeatDetectionOptions _repeatOptions = repeatOptions ?? new RepeatDetectionOptions();
    private readonly TransitionDetectionOptions _transitionOptions = transitionOptions ?? new TransitionDetectionOptions();
    private readonly CmHistoryStore? _historyStore = historyStore;

    public async Task<DetectionResult> DetectSingleAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var audio = await AudioDecoder.DecodeForAnalysisAsync(filePath, cancellationToken);

        // 特徴抽出・検出はCPU負荷が高い同期処理のため、呼び出し元スレッド（UIスレッド等）を
        // ブロックしないようバックグラウンドスレッドに退避させる。
        var candidates = await Task.Run(() =>
        {
            var frames = FeatureExtractor.Extract(audio);
            var repeatCandidates = RepeatSegmentDetector.Detect(frames, _repeatOptions);
            var transitionCandidates = TransitionSegmentDetector.Detect(frames, _transitionOptions);
            var historyCandidates = _historyStore is null ? [] : HistoryMatchDetector.Detect(frames, _historyStore);
            var merged = MergeOverlappingCandidates(repeatCandidates.Concat(transitionCandidates).Concat(historyCandidates));
            LoudnessBoundaryRefiner.Refine(merged, audio);
            return merged;
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
    /// <param name="loadProgress">ファイルの読み込み（デコード＋特徴抽出）が1件完了するごとに (完了数, 総数) を通知する。</param>
    public async Task<List<DetectionResult>> DetectBatchAsync(
        IReadOnlyList<string> filePaths,
        IProgress<(int Done, int Total)>? loadProgress = null,
        CancellationToken cancellationToken = default)
    {
        var decodedAudios = new List<DecodedAudio>(filePaths.Count);
        var framesByFile = new List<List<FrameFeatures>>(filePaths.Count);

        for (var i = 0; i < filePaths.Count; i++)
        {
            var audio = await AudioDecoder.DecodeForAnalysisAsync(filePaths[i], cancellationToken);
            var frames = await Task.Run(() => FeatureExtractor.Extract(audio), cancellationToken);
            decodedAudios.Add(audio);
            framesByFile.Add(frames);
            loadProgress?.Report((i + 1, filePaths.Count));
        }

        return await Task.Run(() =>
        {
            var combinedFrames = framesByFile.SelectMany(f => f).ToList();
            var (hitCount, hitScoreSum) = RepeatSegmentDetector.ComputeHits(combinedFrames, _repeatOptions);

            var results = new List<DetectionResult>(filePaths.Count);
            var frameOffset = 0;
            for (var fileIndex = 0; fileIndex < filePaths.Count; fileIndex++)
            {
                var frames = framesByFile[fileIndex];
                var hitCountSlice = hitCount.Skip(frameOffset).Take(frames.Count).ToArray();
                var hitScoreSumSlice = hitScoreSum.Skip(frameOffset).Take(frames.Count).ToArray();
                frameOffset += frames.Count;

                var repeatCandidates = RepeatSegmentDetector.BuildCandidates(frames, hitCountSlice, hitScoreSumSlice);
                var transitionCandidates = TransitionSegmentDetector.Detect(frames, _transitionOptions);
                var historyCandidates = _historyStore is null ? [] : HistoryMatchDetector.Detect(frames, _historyStore);
                var candidates = MergeOverlappingCandidates(repeatCandidates.Concat(transitionCandidates).Concat(historyCandidates));
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

    /// <summary>重複・隣接する候補区間を1つにまとめる（確信度は最大値、繰り返し回数は合算）。
    /// 繰り返し検出と急変点検出の両方が同じCMを検出した場合に、一覧に二重で出さないようにする。</summary>
    private static List<CmCandidate> MergeOverlappingCandidates(IEnumerable<CmCandidate> candidates)
    {
        var sorted = candidates.OrderBy(c => c.Segment.Start).ToList();
        var merged = new List<CmCandidate>();
        foreach (var candidate in sorted)
        {
            if (merged.Count > 0 && candidate.Segment.Start <= merged[^1].Segment.End)
            {
                var last = merged[^1];
                var newEnd = candidate.Segment.End > last.Segment.End ? candidate.Segment.End : last.Segment.End;
                // どちらかが繰り返し検出由来なら、最も信頼できる根拠として優先する
                var isRepeated = last.Reason == DetectionReason.RepeatedContent || candidate.Reason == DetectionReason.RepeatedContent;

                last.Segment = new AudioSegment(last.Segment.Start, newEnd);
                last.Confidence = Math.Max(last.Confidence, candidate.Confidence);
                last.RepeatCount += candidate.RepeatCount;
                last.Reason = isRepeated ? DetectionReason.RepeatedContent : last.Reason;
                last.CutEnabled = last.CutEnabled || candidate.CutEnabled || isRepeated;
            }
            else
            {
                merged.Add(new CmCandidate
                {
                    Segment = candidate.Segment,
                    Confidence = candidate.Confidence,
                    RepeatCount = candidate.RepeatCount,
                    Reason = candidate.Reason,
                    CutEnabled = candidate.CutEnabled,
                });
            }
        }

        return merged;
    }
}
