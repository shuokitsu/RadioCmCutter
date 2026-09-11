using RadioCmCutter.Core.Detection;
using RadioCmCutter.Core.Ffmpeg;
using RadioCmCutter.Core.History;
using RadioCmCutter.Core.Models;

namespace RadioCmCutter.Core.Pipeline;

/// <summary>
/// 「デコード→特徴抽出→①カット位置候補の検出・精緻化→②区間ごとのCM判定」までを一括で行う。
/// 「どこで切るか」（<see cref="BoundaryDetector"/>）と「それがCMか」（<see cref="SegmentClassifier"/>）を
/// 分離しているため、すべての候補区間の境界が共通のカット位置候補に揃う。
/// 単一ファイルモードと、複数ファイルをまとめて処理するディレクトリモード
/// （ファイル間の繰り返しも検出）に対応。
/// </summary>
public sealed class CmDetectionPipeline(
    RepeatDetectionOptions? repeatOptions = null,
    BoundaryDetectionOptions? boundaryOptions = null,
    SegmentClassificationOptions? classificationOptions = null,
    CmHistoryStore? historyStore = null)
{
    private readonly RepeatDetectionOptions _repeatOptions = repeatOptions ?? new RepeatDetectionOptions();
    private readonly BoundaryDetectionOptions _boundaryOptions = boundaryOptions ?? new BoundaryDetectionOptions();
    private readonly SegmentClassificationOptions _classificationOptions =
        classificationOptions ?? new SegmentClassificationOptions();
    private readonly HistoryMatchOptions _historyOptions = new();
    private readonly CmHistoryStore? _historyStore = historyStore;

    public async Task<DetectionResult> DetectSingleAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var audio = await AudioDecoder.DecodeForAnalysisAsync(filePath, cancellationToken);

        // 特徴抽出・検出はCPU負荷が高い同期処理のため、呼び出し元スレッド（UIスレッド等）を
        // ブロックしないようバックグラウンドスレッドに退避させる。
        var candidates = await Task.Run(() =>
        {
            var frames = FeatureExtractor.Extract(audio);
            var (hitCount, hitScoreSum) = RepeatSegmentDetector.ComputeHits(frames, _repeatOptions);
            return ClassifyFile(frames, audio, hitCount, hitScoreSum);
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

        return await Task.Run(
            () => ClassifyDecodedBatch(decodedAudios, framesByFile)
                .Select((candidates, fileIndex) => new DetectionResult
                {
                    SourceFilePath = filePaths[fileIndex],
                    TotalDuration = decodedAudios[fileIndex].Duration,
                    Candidates = candidates,
                })
                .ToList(),
            cancellationToken);
    }

    /// <summary>
    /// デコード済みの音声・フレーム特徴から、ファイルごとのCM候補を判定する
    /// （通常の入口は <see cref="DetectBatchAsync"/>）。
    /// 繰り返し検出だけは全ファイルのフレームを連結して一度に計算し、ファイル間の繰り返し
    /// （別日の同一CM等）も拾う。境界検出・区間分割・CM判定は、区間がファイルを跨がないよう
    /// ファイルごとに行う。
    /// </summary>
    public List<List<CmCandidate>> ClassifyDecodedBatch(
        IReadOnlyList<DecodedAudio> audios, IReadOnlyList<List<FrameFeatures>> framesByFile)
    {
        var combinedFrames = framesByFile.SelectMany(f => f).ToList();
        var (hitCount, hitScoreSum) = RepeatSegmentDetector.ComputeHits(combinedFrames, _repeatOptions);

        var candidatesByFile = new List<List<CmCandidate>>(framesByFile.Count);
        var frameOffset = 0;
        for (var fileIndex = 0; fileIndex < framesByFile.Count; fileIndex++)
        {
            var frames = framesByFile[fileIndex];
            var hitCountSlice = hitCount[frameOffset..(frameOffset + frames.Count)];
            var hitScoreSumSlice = hitScoreSum[frameOffset..(frameOffset + frames.Count)];
            frameOffset += frames.Count;

            candidatesByFile.Add(ClassifyFile(frames, audios[fileIndex], hitCountSlice, hitScoreSumSlice));
        }

        return candidatesByFile;
    }

    /// <summary>1ファイル分の「カット位置候補の検出・精緻化 → 区間ごとのCM判定」を行う。</summary>
    private List<CmCandidate> ClassifyFile(
        IReadOnlyList<FrameFeatures> frames, DecodedAudio audio, int[] hitCount, double[] hitScoreSum)
    {
        var boundaries = BoundaryDetector.DetectBoundaries(frames, _boundaryOptions);
        var refined = LoudnessBoundaryRefiner.RefineBoundaries(boundaries, audio);
        // 境界補正は1点ずつ独立にスナップするため、補正後に再び近接し得る
        refined = BoundaryDetector.CollapseCloseBoundaries(refined, _boundaryOptions.MinBoundaryGapSeconds);

        var history = HistoryMatchDetector.ComputeScores(frames, _historyStore);
        var repeat = new RepeatEvidence(hitCount, hitScoreSum, _repeatOptions.MinRunSeconds);

        return SegmentClassifier.Classify(
            frames,
            WithFileEdges(refined, audio.Duration),
            repeat,
            history,
            _historyOptions,
            _classificationOptions);
    }

    /// <summary>区間がファイル全体を隙間なく覆うよう、先頭と末尾を境界リストに足す。
    /// この2点は音量境界補正の対象にしない（スナップすると実音声を削る／余らせるため）。</summary>
    private static List<BoundaryPoint> WithFileEdges(IReadOnlyList<BoundaryPoint> boundaries, TimeSpan totalDuration)
    {
        var withEdges = new List<BoundaryPoint>(boundaries.Count + 2) { new(TimeSpan.Zero, 1.0) };
        withEdges.AddRange(boundaries.Where(b => b.Position > TimeSpan.Zero && b.Position < totalDuration));
        withEdges.Add(new BoundaryPoint(totalDuration, 1.0));
        return withEdges;
    }
}
