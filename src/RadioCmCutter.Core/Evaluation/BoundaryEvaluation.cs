namespace RadioCmCutter.Core.Evaluation;

/// <summary>区切り検出の採点結果。</summary>
public sealed record BoundaryScore(
    int ReferenceCount,
    int EstimatedCount,
    int MatchedCount,
    double Precision,
    double Recall,
    double FMeasure,
    double MedianReferenceToEstimate,
    double MedianEstimateToReference)
{
    public override string ToString() =>
        $"F={FMeasure:F3} (適合率={Precision:F3} 再現率={Recall:F3}) "
        + $"一致={MatchedCount}/{ReferenceCount}件 "
        + $"中央値ずれ 正解→推定={MedianReferenceToEstimate:F2}秒 推定→正解={MedianEstimateToReference:F2}秒";
}

/// <summary>
/// 区切り検出の精度を、ユーザーが確認した正解の時刻リストと突き合わせて採点する。
///
/// 目視での確認だけでは、アルゴリズムの変更が効いたのか、たまたまそのファイルに合っただけなのかを
/// 区別できない。特にこの問題では「一定間隔で機械的に区切る」だけでもF値が0.5前後出てしまうため
/// （Serrà et al. 2012 が同様のベースラインを報告）、必ずベースラインと並べて判断する必要がある。
///
/// 指標は mir_eval (Raffel et al., ISMIR 2014) の構造境界の評価に合わせている。
/// </summary>
public static class BoundaryEvaluation
{
    /// <summary>慣例的な許容誤差（秒）。厳しい方と緩い方の両方で見る。</summary>
    public static readonly double[] StandardTolerances = [0.5, 3.0];

    /// <summary>
    /// 推定した区切りを正解と突き合わせて採点する。
    /// 1つの正解に複数の推定が当たって二重に得点しないよう、1対1で対応付ける。
    /// </summary>
    /// <param name="toleranceSeconds">この秒数以内なら「当たり」とみなす。</param>
    public static BoundaryScore Evaluate(
        IReadOnlyList<double> reference, IReadOnlyList<double> estimated, double toleranceSeconds)
    {
        var referenceTimes = Normalize(reference);
        var estimatedTimes = Normalize(estimated);

        if (referenceTimes.Count == 0 || estimatedTimes.Count == 0)
        {
            return new BoundaryScore(
                referenceTimes.Count, estimatedTimes.Count, 0, 0, 0, 0,
                MedianNearestDistance(referenceTimes, estimatedTimes),
                MedianNearestDistance(estimatedTimes, referenceTimes));
        }

        var matched = CountOneToOneMatches(referenceTimes, estimatedTimes, toleranceSeconds);
        var precision = matched / (double)estimatedTimes.Count;
        var recall = matched / (double)referenceTimes.Count;
        var f = precision + recall <= 0 ? 0 : 2 * precision * recall / (precision + recall);

        return new BoundaryScore(
            referenceTimes.Count,
            estimatedTimes.Count,
            matched,
            precision,
            recall,
            f,
            MedianNearestDistance(referenceTimes, estimatedTimes),
            MedianNearestDistance(estimatedTimes, referenceTimes));
    }

    /// <summary>ファイル全体を等間隔に区切るだけのベースライン。
    /// 検出結果がこれを上回っていなければ、検出している意味がない。</summary>
    public static List<double> UniformBaseline(double totalDurationSeconds, double intervalSeconds)
    {
        var boundaries = new List<double>();
        if (intervalSeconds <= 0) return boundaries;

        for (var t = intervalSeconds; t < totalDurationSeconds; t += intervalSeconds)
        {
            boundaries.Add(t);
        }
        return boundaries;
    }

    /// <summary>指定した数の区切りをランダムに置くベースライン。
    /// 乱数の種を固定して、実行ごとに結果が変わらないようにする。</summary>
    public static List<double> RandomBaseline(double totalDurationSeconds, int count, int seed = 12345)
    {
        var random = new Random(seed);
        var boundaries = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            boundaries.Add(random.NextDouble() * totalDurationSeconds);
        }
        boundaries.Sort();
        return boundaries;
    }

    /// <summary>ファイルの先頭・末尾は「当てるまでもない自明な区切り」なので採点から除く
    /// （含めると、何も検出できていなくても得点してしまう）。</summary>
    public static List<double> Trim(IReadOnlyList<double> boundaries, double totalDurationSeconds, double edgeSeconds = 0.5)
    {
        return boundaries
            .Where(b => b > edgeSeconds && b < totalDurationSeconds - edgeSeconds)
            .ToList();
    }

    /// <summary>近い順に1対1で対応付けて、当たった数を数える。</summary>
    private static int CountOneToOneMatches(
        List<double> reference, List<double> estimated, double toleranceSeconds)
    {
        var pairs = new List<(double Distance, int ReferenceIndex, int EstimatedIndex)>();
        for (var r = 0; r < reference.Count; r++)
        {
            for (var e = 0; e < estimated.Count; e++)
            {
                var distance = Math.Abs(reference[r] - estimated[e]);
                if (distance <= toleranceSeconds)
                {
                    pairs.Add((distance, r, e));
                }
            }
        }

        pairs.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        var usedReference = new bool[reference.Count];
        var usedEstimated = new bool[estimated.Count];
        var matched = 0;
        foreach (var (_, referenceIndex, estimatedIndex) in pairs)
        {
            if (usedReference[referenceIndex] || usedEstimated[estimatedIndex]) continue;
            usedReference[referenceIndex] = true;
            usedEstimated[estimatedIndex] = true;
            matched++;
        }

        return matched;
    }

    /// <summary>fromの各点について、toの中で最も近い点までの距離の中央値。
    /// 「当たり外れ」とは別に、位置がどれだけずれているかを見るための指標。</summary>
    private static double MedianNearestDistance(List<double> from, List<double> to)
    {
        if (from.Count == 0 || to.Count == 0) return double.NaN;

        var distances = from
            .Select(f => to.Min(t => Math.Abs(f - t)))
            .OrderBy(d => d)
            .ToList();

        return distances.Count % 2 == 1
            ? distances[distances.Count / 2]
            : (distances[(distances.Count / 2) - 1] + distances[distances.Count / 2]) / 2;
    }

    private static List<double> Normalize(IReadOnlyList<double> boundaries) =>
        boundaries.Distinct().OrderBy(b => b).ToList();
}
