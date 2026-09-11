using System.Globalization;

namespace RadioCmCutter.Core.Ffmpeg;

public sealed class DecodedAudio
{
    public required int SampleRate { get; init; }
    public required float[] Samples { get; init; } // モノラルに変換済み、-1.0〜1.0

    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);
}

/// <summary>ffmpegを使って任意フォーマットの音声ファイルを解析用PCMにデコードする。</summary>
public static class AudioDecoder
{
    /// <summary>特徴抽出用に低サンプルレート・モノラルのPCMへデコードする。</summary>
    public const int AnalysisSampleRate = 8000;

    public static async Task<TimeSpan> GetDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var args = $"-v error -show_entries format=duration -of csv=p=0 \"{filePath}\"";
        var (exitCode, stdOut, stdErr) = await FfmpegProcessRunner.RunCapturingStdOutAsync(
            FfmpegLocator.FfprobeExePath, args, cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ffprobeによる長さ取得に失敗しました: {stdErr}");
        }

        var text = System.Text.Encoding.UTF8.GetString(stdOut).Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new InvalidOperationException($"ffprobeの出力を解析できませんでした: '{text}'");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>元ファイルの音声ビットレート（bps）を取得する。再エンコード時に元のビットレートを
    /// 引き継ぐために使う（固定値を使うと、元より低ビットレートの場合にファイルサイズが不必要に膨らむ）。
    /// 取得できない場合はnullを返す。</summary>
    public static async Task<int?> GetAudioBitrateBpsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var streamArgs = $"-v error -select_streams a:0 -show_entries stream=bit_rate -of csv=p=0 \"{filePath}\"";
        var (streamExitCode, streamStdOut, _) = await FfmpegProcessRunner.RunCapturingStdOutAsync(
            FfmpegLocator.FfprobeExePath, streamArgs, cancellationToken);
        if (streamExitCode == 0 && TryParseBitrate(streamStdOut, out var streamBps))
        {
            return streamBps;
        }

        // ストリームにビットレート情報が無いコンテナ（一部のmp4/m4a等）は、コンテナ全体の値にフォールバック
        var formatArgs = $"-v error -show_entries format=bit_rate -of csv=p=0 \"{filePath}\"";
        var (formatExitCode, formatStdOut, _) = await FfmpegProcessRunner.RunCapturingStdOutAsync(
            FfmpegLocator.FfprobeExePath, formatArgs, cancellationToken);
        if (formatExitCode == 0 && TryParseBitrate(formatStdOut, out var formatBps))
        {
            return formatBps;
        }

        return null;
    }

    private static bool TryParseBitrate(byte[] stdOut, out int bps)
    {
        var text = System.Text.Encoding.UTF8.GetString(stdOut).Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out bps) && bps > 0;
    }

    public static async Task<DecodedAudio> DecodeForAnalysisAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // モノラル・16bit signed little-endian PCM・低サンプルレートで標準出力に書き出させる
        var args = $"-v error -i \"{filePath}\" -f s16le -ac 1 -ar {AnalysisSampleRate} -";
        var (exitCode, stdOut, stdErr) = await FfmpegProcessRunner.RunCapturingStdOutAsync(
            FfmpegLocator.FfmpegExePath, args, cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ffmpegによる音声デコードに失敗しました: {stdErr}");
        }

        var sampleCount = stdOut.Length / 2;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var s16 = (short)(stdOut[i * 2] | (stdOut[(i * 2) + 1] << 8));
            samples[i] = s16 / 32768f;
        }

        return new DecodedAudio { SampleRate = AnalysisSampleRate, Samples = samples };
    }
}
