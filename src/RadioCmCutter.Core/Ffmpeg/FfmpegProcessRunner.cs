using System.Diagnostics;

namespace RadioCmCutter.Core.Ffmpeg;

public sealed class FfmpegProcessResult
{
    public required int ExitCode { get; init; }
    public required string StandardError { get; init; }
}

/// <summary>ffmpeg/ffprobeプロセスの起動・待機・出力回収を行う薄いラッパー。</summary>
public static class FfmpegProcessRunner
{
    public static async Task<FfmpegProcessResult> RunAsync(
        string exePath, string arguments, CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        var stderr = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginErrorReadLine();
        await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new FfmpegProcessResult { ExitCode = process.ExitCode, StandardError = stderr.ToString() };
    }

    /// <summary>標準出力をバイト列として取得したい場合（PCM抽出など）に使う。</summary>
    public static async Task<(int ExitCode, byte[] StdOut, string StdErr)> RunCapturingStdOutAsync(
        string exePath, string arguments, CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        var stderr = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginErrorReadLine();

        using var memoryStream = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(memoryStream, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, memoryStream.ToArray(), stderr.ToString());
    }
}
