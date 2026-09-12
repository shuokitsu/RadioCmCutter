using RadioCmCutter.Core.Evaluation;
using Xunit;

namespace RadioCmCutter.Core.Tests;

public class BoundaryEvaluationTests
{
    [Fact]
    public void Evaluate_ExactMatch_ScoresPerfect()
    {
        var score = BoundaryEvaluation.Evaluate([10.0, 20.0, 30.0], [10.0, 20.0, 30.0], toleranceSeconds: 0.5);

        Assert.Equal(3, score.MatchedCount);
        Assert.Equal(1.0, score.FMeasure);
        Assert.Equal(0.0, score.MedianReferenceToEstimate);
    }

    [Fact]
    public void Evaluate_WithinTolerance_CountsAsMatch()
    {
        // 許容3秒なら当たり、0.5秒なら外れになる2秒のずれ
        var loose = BoundaryEvaluation.Evaluate([10.0], [12.0], toleranceSeconds: 3.0);
        var strict = BoundaryEvaluation.Evaluate([10.0], [12.0], toleranceSeconds: 0.5);

        Assert.Equal(1, loose.MatchedCount);
        Assert.Equal(0, strict.MatchedCount);
        // 当たり外れとは別に、ずれの大きさは同じ値が出る
        Assert.Equal(2.0, loose.MedianReferenceToEstimate);
        Assert.Equal(2.0, strict.MedianReferenceToEstimate);
    }

    [Fact]
    public void Evaluate_MultipleEstimatesNearOneReference_CountOnlyOnce()
    {
        // 1つの正解の周りに推定を並べれば当たる、という抜け道を塞ぐ（1対1で対応付ける）
        var score = BoundaryEvaluation.Evaluate([10.0], [9.9, 10.0, 10.1, 10.2], toleranceSeconds: 0.5);

        Assert.Equal(1, score.MatchedCount);
        Assert.Equal(1.0, score.Recall);
        Assert.Equal(0.25, score.Precision); // 4件中1件しか当たっていない
    }

    [Fact]
    public void Evaluate_GreedyMatchingPrefersClosestPair()
    {
        // 正解10.0には10.1、正解11.0には11.2が当たるべき（近い順に対応付ける）
        var score = BoundaryEvaluation.Evaluate([10.0, 11.0], [10.1, 11.2], toleranceSeconds: 0.5);

        Assert.Equal(2, score.MatchedCount);
    }

    [Fact]
    public void Evaluate_NoEstimates_ScoresZeroWithoutCrashing()
    {
        var score = BoundaryEvaluation.Evaluate([10.0, 20.0], [], toleranceSeconds: 3.0);

        Assert.Equal(0, score.MatchedCount);
        Assert.Equal(0, score.FMeasure);
        Assert.True(double.IsNaN(score.MedianReferenceToEstimate));
    }

    [Fact]
    public void Trim_RemovesFileEdges()
    {
        // 先頭・末尾は当てるまでもない自明な区切りなので採点から外す
        var trimmed = BoundaryEvaluation.Trim([0.0, 10.0, 100.0, 120.0], totalDurationSeconds: 120.0);

        Assert.Equal([10.0, 100.0], trimmed);
    }

    [Fact]
    public void UniformBaseline_CoversFileWithoutIncludingEdges()
    {
        var baseline = BoundaryEvaluation.UniformBaseline(totalDurationSeconds: 10.0, intervalSeconds: 3.0);

        Assert.Equal([3.0, 6.0, 9.0], baseline);
    }

    [Fact]
    public void RandomBaseline_IsReproducibleForTheSameSeed()
    {
        var first = BoundaryEvaluation.RandomBaseline(100.0, count: 5, seed: 42);
        var second = BoundaryEvaluation.RandomBaseline(100.0, count: 5, seed: 42);

        Assert.Equal(first, second);
        Assert.Equal(5, first.Count);
        Assert.All(first, b => Assert.InRange(b, 0, 100.0));
    }

    [Fact]
    public void Run_IncludesDetectionAndBaselinesAtEachTolerance()
    {
        var reference = new[] { 30.0, 60.0, 90.0 };
        var estimated = new[] { 30.2, 61.0, 200.0 };

        var rows = BoundaryBenchmark.Run(reference, estimated, totalDurationSeconds: 300.0);

        Assert.Equal(4, rows.Count); // 検出結果＋ベースライン3種
        Assert.Equal("検出結果", rows[0].Label);
        Assert.All(rows, r => Assert.Equal(BoundaryEvaluation.StandardTolerances.Length, r.ScoresByTolerance.Count));

        // 許容0.5秒では30.2のみ当たり、3秒では30.2と61.0が当たる
        Assert.Equal(1, rows[0].ScoresByTolerance[0.5].MatchedCount);
        Assert.Equal(2, rows[0].ScoresByTolerance[3.0].MatchedCount);
    }

    [Fact]
    public void Run_EveryThreeSecondsBaseline_ScoresHighAtLooseTolerance()
    {
        // 「3秒ごとに機械的に区切る」だけでも緩い許容では高い再現率が出てしまうことを、
        // テストとして固定しておく（検出結果の数値だけを見て良し悪しを判断しないための歯止め）。
        var reference = new[] { 30.0, 60.0, 90.0, 150.0, 210.0 };
        var rows = BoundaryBenchmark.Run(reference, [40.0], totalDurationSeconds: 300.0);

        var everyThreeSeconds = rows.Single(r => r.Label.Contains("3秒ごと"));
        Assert.Equal(1.0, everyThreeSeconds.ScoresByTolerance[3.0].Recall);
    }

    [Fact]
    public void Run_PartiallyAnnotatedFile_ScoresOnlyTheAnnotatedRange()
    {
        // 実データで実際にはまった落とし穴の再現:
        // ファイルの先頭だけ確認した状態で全体を採点すると、未確認部分の検出が
        // すべて「外れ」と数えられ、適合率が不当に低く出る。
        // 確認し終えた範囲を指定すれば、その範囲だけで正しく採点される。
        var reference = new[] { 30.0, 60.0 };                    // 0〜100秒だけ確認済み
        var estimated = new[] { 30.0, 60.0, 500.0, 800.0, 1100.0 }; // 未確認部分にも検出がある

        var wholeFile = BoundaryBenchmark.Run(reference, estimated, totalDurationSeconds: 1200.0);
        var annotatedOnly = BoundaryBenchmark.Run(
            reference, estimated, totalDurationSeconds: 1200.0,
            tolerances: null, rangeStartSeconds: 0, rangeEndSeconds: 100.0);

        // 全体を採点すると、当てているのに適合率が2/5まで下がる
        Assert.Equal(1.0, wholeFile[0].ScoresByTolerance[0.5].Recall);
        Assert.Equal(0.4, wholeFile[0].ScoresByTolerance[0.5].Precision);

        // 確認済みの範囲だけなら、適合率も再現率も満点になる
        Assert.Equal(1.0, annotatedOnly[0].ScoresByTolerance[0.5].Precision);
        Assert.Equal(1.0, annotatedOnly[0].ScoresByTolerance[0.5].FMeasure);
    }

    [Fact]
    public void GroundTruth_RoundTripsThroughJson()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rcc_gt_{Guid.NewGuid():N}.json");
        try
        {
            new BoundaryGroundTruth
            {
                SourceFileName = "test.aac",
                TotalDurationSeconds = 7200.0,
                BoundarySeconds = [12.82, 80.14, 378.26],
                Note = "1:20-6:18は1曲",
                SavedAtUtc = DateTime.UtcNow,
            }.Save(path);

            var loaded = BoundaryGroundTruth.Load(path);

            Assert.NotNull(loaded);
            Assert.Equal([12.82, 80.14, 378.26], loaded.BoundarySeconds);
            Assert.Equal("1:20-6:18は1曲", loaded.Note);
            Assert.Equal(7200.0, loaded.TotalDurationSeconds);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void GroundTruth_MissingOrBrokenFile_ReturnsNull()
    {
        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rcc_missing_{Guid.NewGuid():N}.json");
        Assert.Null(BoundaryGroundTruth.Load(missing));

        var broken = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rcc_broken_{Guid.NewGuid():N}.json");
        try
        {
            System.IO.File.WriteAllText(broken, "{ これはJSONではない");
            Assert.Null(BoundaryGroundTruth.Load(broken));
        }
        finally
        {
            if (System.IO.File.Exists(broken)) System.IO.File.Delete(broken);
        }
    }
}
