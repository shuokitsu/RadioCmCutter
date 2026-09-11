using RadioCmCutter.Core.Ffmpeg;

namespace RadioCmCutter.Core.Detection;

/// <summary>
/// カット位置候補を、無音・低音量点（RMSの局所最小）にスナップして精緻化する（補助処理）。
/// CM/本編の判別そのものには使わず、境界のズレ補正のみに使う。
/// 候補区間ごとに開始・終了を個別補正すると、隣接区間の境界が食い違って区間長が数秒ずれるため、
/// 共有された境界リストに対して「1点につき1回だけ」補正する。
/// </summary>
public static class LoudnessBoundaryRefiner
{
    private const double SearchWindowSeconds = 3.0;
    private const double SubFrameSeconds = 0.02;

    /// <summary>最小RMSに対してこの割合までは「同程度に静か」とみなし、切れ目に近い方を優先する。</summary>
    private const double RmsTieTolerance = 1.1;

    /// <summary>境界候補を時刻順に1点ずつ補正する。
    /// 補正で隣の境界を追い越さないよう、探索範囲を「直前の補正後の境界」と「次の未補正の境界」の間に制限する。
    /// 探索範囲が潰れる場合はその境界を補正せず元の位置のまま残す。
    /// ファイル先頭・末尾は境界リストに含めないこと（スナップすると実音声を削る／余らせるため）。</summary>
    public static List<BoundaryPoint> RefineBoundaries(
        IReadOnlyList<BoundaryPoint> boundaries, DecodedAudio audio)
    {
        var refined = new List<BoundaryPoint>(boundaries.Count);
        var lowerLimit = TimeSpan.Zero;

        for (var i = 0; i < boundaries.Count; i++)
        {
            var upperLimit = i + 1 < boundaries.Count ? boundaries[i + 1].Position : audio.Duration;
            var position = SnapToLocalMinimum(audio, boundaries[i].Position, lowerLimit, upperLimit);

            refined.Add(new BoundaryPoint(position, boundaries[i].Strength));
            lowerLimit = position;
        }

        return refined;
    }

    private static TimeSpan SnapToLocalMinimum(
        DecodedAudio audio, TimeSpan around, TimeSpan lowerLimit, TimeSpan upperLimit)
    {
        var sampleRate = audio.SampleRate;
        var subFrameSize = Math.Max(1, (int)(SubFrameSeconds * sampleRate));
        var windowSamples = (int)(SearchWindowSeconds * sampleRate);

        var centerSample = (int)(around.TotalSeconds * sampleRate);
        // 直前の境界と同一位置になると長さ0の区間ができてしまうため、1サブフレーム分は必ず後ろから探す
        var loSample = Math.Max(
            (int)(lowerLimit.TotalSeconds * sampleRate) + subFrameSize,
            centerSample - windowSamples);
        var hiSample = Math.Min(
            Math.Min((int)(upperLimit.TotalSeconds * sampleRate), audio.Samples.Length),
            centerSample + windowSamples);

        // 隣の境界に挟まれて探索範囲が潰れた場合は補正しない
        if (loSample + subFrameSize > hiSample) return around;

        var offsets = new List<int>();
        var rmsValues = new List<double>();
        var minRms = double.MaxValue;

        for (var offset = loSample; offset + subFrameSize <= hiSample; offset += subFrameSize)
        {
            double sumSquares = 0;
            for (var i = 0; i < subFrameSize; i++)
            {
                var s = audio.Samples[offset + i];
                sumSquares += s * s;
            }
            var rms = Math.Sqrt(sumSquares / subFrameSize);

            offsets.Add(offset);
            rmsValues.Add(rms);
            if (rms < minRms) minRms = rms;
        }

        // 音量が同程度に小さい点が複数ある場合（定常的な音楽・長い無音など）は、
        // 検出された切れ目に最も近い点を選ぶ。音量はあくまで微調整の手がかりであり、
        // 切れ目の位置そのものが主たる根拠のため、無用に数秒ずれるのを防ぐ。
        var tolerance = (minRms * RmsTieTolerance) + 1e-6;
        var bestOffset = centerSample;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < offsets.Count; i++)
        {
            if (rmsValues[i] > tolerance) continue;

            var distance = Math.Abs(offsets[i] - centerSample);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestOffset = offsets[i];
            }
        }

        return TimeSpan.FromSeconds(bestOffset / (double)sampleRate);
    }
}
