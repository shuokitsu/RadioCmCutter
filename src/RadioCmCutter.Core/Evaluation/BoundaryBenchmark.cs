using System.Text;

namespace RadioCmCutter.Core.Evaluation;

/// <summary>検出結果と各ベースラインを、複数の許容誤差で採点した結果。</summary>
public sealed record BenchmarkRow(string Label, IReadOnlyDictionary<double, BoundaryScore> ScoresByTolerance);

/// <summary>
/// 検出結果を「一定間隔で区切るだけ」「ランダムに置くだけ」のベースラインと並べて採点する。
///
/// この問題では、何も検出せず一定間隔で区切るだけでもF値が0.5前後出ることが知られているため
/// （Serrà et al. 2012）、検出結果の数値だけを見ても良し悪しを判断できない。
/// 必ず同じ正解・同じ指標でベースラインと比較する。
/// </summary>
public static class BoundaryBenchmark
{
    /// <param name="rangeStartSeconds">採点対象の範囲。確認し終えた範囲だけを採点するために使う。
    /// 指定しないとファイル全体が対象になり、未確認部分の検出がすべて「外れ」と数えられて
    /// 適合率が不当に低く出る。</param>
    public static List<BenchmarkRow> Run(
        IReadOnlyList<double> reference,
        IReadOnlyList<double> estimated,
        double totalDurationSeconds,
        IReadOnlyList<double>? tolerances = null,
        double? rangeStartSeconds = null,
        double? rangeEndSeconds = null)
    {
        tolerances ??= BoundaryEvaluation.StandardTolerances;

        var from = rangeStartSeconds ?? 0;
        var to = rangeEndSeconds ?? totalDurationSeconds;

        var trimmedReference = InRange(BoundaryEvaluation.Trim(reference, totalDurationSeconds), from, to);
        var trimmedEstimated = InRange(BoundaryEvaluation.Trim(estimated, totalDurationSeconds), from, to);

        // ベースラインは正解と同じ土俵に乗るよう、採点範囲の中で同程度の密度で作る
        var rangeSeconds = Math.Max(0, to - from);
        var meanSegmentSeconds = trimmedReference.Count > 0
            ? rangeSeconds / (trimmedReference.Count + 1)
            : rangeSeconds;

        var candidates = new List<(string Label, IReadOnlyList<double> Boundaries)>
        {
            ("検出結果", trimmedEstimated),
            ($"ベースライン: {meanSegmentSeconds:F0}秒ごと（正解と同じ密度）",
                InRange(BoundaryEvaluation.UniformBaseline(totalDurationSeconds, meanSegmentSeconds), from, to)),
            ("ベースライン: 3秒ごと",
                InRange(BoundaryEvaluation.UniformBaseline(totalDurationSeconds, 3.0), from, to)),
            ($"ベースライン: ランダム{trimmedEstimated.Count}点（検出結果と同数）",
                InRange(BoundaryEvaluation.RandomBaseline(totalDurationSeconds, trimmedEstimated.Count), from, to)),
        };

        return candidates
            .Select(c => new BenchmarkRow(
                c.Label,
                tolerances.ToDictionary(t => t, t => BoundaryEvaluation.Evaluate(trimmedReference, c.Boundaries, t))))
            .ToList();
    }

    private static List<double> InRange(IReadOnlyList<double> boundaries, double from, double to) =>
        boundaries.Where(b => b >= from && b <= to).ToList();

    /// <summary>画面やログにそのまま出せる表形式の文字列にする。</summary>
    public static string Format(IReadOnlyList<BenchmarkRow> rows, IReadOnlyList<double>? tolerances = null)
    {
        tolerances ??= BoundaryEvaluation.StandardTolerances;

        var text = new StringBuilder();
        foreach (var tolerance in tolerances)
        {
            text.AppendLine($"■ 許容誤差 {tolerance}秒");
            foreach (var row in rows)
            {
                if (!row.ScoresByTolerance.TryGetValue(tolerance, out var score)) continue;
                text.AppendLine($"   {row.Label,-40} {score}");
            }
            text.AppendLine();
        }

        return text.ToString().TrimEnd();
    }
}
