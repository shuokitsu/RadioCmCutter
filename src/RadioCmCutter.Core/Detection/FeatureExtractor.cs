using System.Numerics;
using RadioCmCutter.Core.Ffmpeg;

namespace RadioCmCutter.Core.Detection;

/// <summary>
/// デコード済みPCMを一定長のフレームに分割し、対数帯域エネルギーの特徴ベクトルを抽出する。
/// 帯域はメル尺度そのものではなく、対数周波数間隔の簡易バンド分割（実装・計算コストを抑えるため）。
///
/// 各フレームの特徴ベクトルは「その瞬間のスペクトル形状」と「直前フレームからの変化量（デルタ）」を
/// 連結したもの。瞬間のスペクトルだけだと、一定トーンやBGMのような時間的に変化しない音が
/// 自分自身と誤って「繰り返し」判定されやすい（実データ検証で確認済み）。デルタを含めることで、
/// 実際に同じ時間変化パターンを持つ区間（＝本当の繰り返しCM）だけを一致とみなしやすくする。
/// </summary>
public static class FeatureExtractor
{
    public const double FrameSeconds = 0.5;
    private const int BandCount = 24;
    private const int MinFrequencyHz = 50;

    public static List<FrameFeatures> Extract(DecodedAudio audio)
    {
        var sampleRate = audio.SampleRate;
        var frameSize = NextPowerOfTwo((int)(FrameSeconds * sampleRate));
        var samples = audio.Samples;

        var bandEdges = BuildLogBandEdges(sampleRate, frameSize);
        var window = BuildHammingWindow(frameSize);

        var rawBandEnergies = new List<float[]>();
        var rmsValues = new List<double>();
        var starts = new List<TimeSpan>();
        var ends = new List<TimeSpan>();

        for (var offset = 0; offset + frameSize <= samples.Length; offset += frameSize)
        {
            var buffer = new Complex[frameSize];
            double sumSquares = 0;
            for (var i = 0; i < frameSize; i++)
            {
                var s = samples[offset + i];
                sumSquares += s * s;
                buffer[i] = new Complex(s * window[i], 0);
            }

            Fft.Forward(buffer);

            var bandEnergies = new float[BandCount];
            for (var b = 0; b < BandCount; b++)
            {
                var (loBin, hiBin) = bandEdges[b];
                double energy = 0;
                for (var bin = loBin; bin < hiBin && bin < frameSize / 2; bin++)
                {
                    energy += buffer[bin].Magnitude;
                }
                bandEnergies[b] = (float)Math.Log10(1.0 + energy);
            }

            rawBandEnergies.Add(bandEnergies);
            rmsValues.Add(Math.Sqrt(sumSquares / frameSize));
            starts.Add(TimeSpan.FromSeconds(offset / (double)sampleRate));
            ends.Add(TimeSpan.FromSeconds((offset + frameSize) / (double)sampleRate));
        }

        var frames = new List<FrameFeatures>(rawBandEnergies.Count);
        for (var i = 0; i < rawBandEnergies.Count; i++)
        {
            var spectral = (float[])rawBandEnergies[i].Clone();
            Normalize(spectral);

            var delta = new float[BandCount];
            if (i > 0)
            {
                for (var b = 0; b < BandCount; b++)
                {
                    delta[b] = rawBandEnergies[i][b] - rawBandEnergies[i - 1][b];
                }
            }
            Normalize(delta);

            var combined = new float[BandCount * 2];
            Array.Copy(spectral, 0, combined, 0, BandCount);
            Array.Copy(delta, 0, combined, BandCount, BandCount);
            Normalize(combined);

            frames.Add(new FrameFeatures { Start = starts[i], End = ends[i], Vector = combined, Rms = rmsValues[i] });
        }

        return frames;
    }

    private static void Normalize(float[] vector)
    {
        double normSquared = 0;
        foreach (var v in vector) normSquared += v * v;
        var norm = Math.Sqrt(normSquared);
        if (norm < 1e-9) return;
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }
    }

    private static (int LoBin, int HiBin)[] BuildLogBandEdges(int sampleRate, int frameSize)
    {
        var nyquist = sampleRate / 2.0;
        var minFreq = Math.Max(MinFrequencyHz, 1);
        var logMin = Math.Log(minFreq);
        var logMax = Math.Log(nyquist);
        var edges = new (int LoBin, int HiBin)[BandCount];
        for (var b = 0; b < BandCount; b++)
        {
            var loFreq = Math.Exp(logMin + ((logMax - logMin) * b / BandCount));
            var hiFreq = Math.Exp(logMin + ((logMax - logMin) * (b + 1) / BandCount));
            var loBin = (int)(loFreq / nyquist * (frameSize / 2));
            var hiBin = Math.Max(loBin + 1, (int)(hiFreq / nyquist * (frameSize / 2)));
            edges[b] = (loBin, hiBin);
        }
        return edges;
    }

    private static double[] BuildHammingWindow(int size)
    {
        var window = new double[size];
        for (var i = 0; i < size; i++)
        {
            window[i] = 0.54 - (0.46 * Math.Cos(2 * Math.PI * i / (size - 1)));
        }
        return window;
    }

    private static int NextPowerOfTwo(int value)
    {
        var power = 1;
        while (power < value) power <<= 1;
        return power;
    }
}
