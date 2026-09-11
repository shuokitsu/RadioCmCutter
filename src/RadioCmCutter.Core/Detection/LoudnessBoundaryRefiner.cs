using RadioCmCutter.Core.Ffmpeg;
using RadioCmCutter.Core.Models;

namespace RadioCmCutter.Core.Detection;

/// <summary>
/// 繰り返し検出で得たCM候補区間の開始・終了位置を、無音・低音量点にスナップして精緻化する（補助処理）。
/// CM/本編の判別そのものには使わず、境界のズレ補正のみに使う。
/// </summary>
public static class LoudnessBoundaryRefiner
{
    private const double SearchWindowSeconds = 3.0;
    private const double SubFrameSeconds = 0.02;

    public static void Refine(IReadOnlyList<CmCandidate> candidates, DecodedAudio audio)
    {
        foreach (var candidate in candidates)
        {
            var newStart = SnapToLocalMinimum(audio, candidate.Segment.Start);
            var newEnd = SnapToLocalMinimum(audio, candidate.Segment.End);
            if (newEnd > newStart)
            {
                candidate.Segment = new AudioSegment(newStart, newEnd);
            }
        }
    }

    private static TimeSpan SnapToLocalMinimum(DecodedAudio audio, TimeSpan around)
    {
        var sampleRate = audio.SampleRate;
        var subFrameSize = Math.Max(1, (int)(SubFrameSeconds * sampleRate));
        var windowSamples = (int)(SearchWindowSeconds * sampleRate);

        var centerSample = (int)(around.TotalSeconds * sampleRate);
        var loSample = Math.Max(0, centerSample - windowSamples);
        var hiSample = Math.Min(audio.Samples.Length, centerSample + windowSamples);

        var bestOffset = centerSample;
        var bestRms = double.MaxValue;

        for (var offset = loSample; offset + subFrameSize <= hiSample; offset += subFrameSize)
        {
            double sumSquares = 0;
            for (var i = 0; i < subFrameSize; i++)
            {
                var s = audio.Samples[offset + i];
                sumSquares += s * s;
            }
            var rms = Math.Sqrt(sumSquares / subFrameSize);
            if (rms < bestRms)
            {
                bestRms = rms;
                bestOffset = offset;
            }
        }

        return TimeSpan.FromSeconds(bestOffset / (double)sampleRate);
    }
}
