using System.Diagnostics;

namespace RadioCmCutter.Core.Ffmpeg;

/// <summary>
/// ffmpeg.exe / ffprobe.exe の実体を探す。
/// 配布時はアプリ実行ファイルと同じ場所の tools/ffmpeg/ に同梱する想定（別PCへの追加インストール不要）。
/// 同梱が見つからない場合はPATH上のものにフォールバックする（開発時の動作確認用）。
/// </summary>
public static class FfmpegLocator
{
    public static string FfmpegExePath { get; } = Resolve("ffmpeg.exe", "ffmpeg");
    public static string FfprobeExePath { get; } = Resolve("ffprobe.exe", "ffprobe");

    private static string Resolve(string bundledFileName, string pathCommandName)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", bundledFileName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        return pathCommandName;
    }

    /// <summary>ffmpeg/ffprobeが実行可能かどうかを確認する（起動時のチェック用）。</summary>
    public static bool TryVerify(out string message)
    {
        foreach (var (exe, label) in new[] { (FfmpegExePath, "ffmpeg"), (FfprobeExePath, "ffprobe") })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                process!.WaitForExit(5000);
                if (process.ExitCode != 0)
                {
                    message = $"{label} の起動確認に失敗しました（終了コード: {process.ExitCode}）。";
                    return false;
                }
            }
            catch (Exception ex)
            {
                message = $"{label} が見つかりません（{exe}）。tools/ffmpeg フォルダに同梱するか、PATHに追加してください。詳細: {ex.Message}";
                return false;
            }
        }

        message = "OK";
        return true;
    }
}
