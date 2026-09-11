using RadioCmCutter.Core.Models;

namespace RadioCmCutter.Core.Ffmpeg;

/// <summary>
/// CM区間（削除対象）の補集合として「残す区間」を求め、ffmpegでカット・再結合／個別書き出しを行う。
/// </summary>
public static class AudioCutter
{
    private static readonly string[] LossyExtensions = [".mp3", ".aac", ".m4a"];

    /// <summary>削除対象区間（重複・隙間ありうる）から、残す区間の一覧（時系列・マージ済み）を求める。</summary>
    public static List<AudioSegment> ComputeKeptSegments(TimeSpan totalDuration, IEnumerable<AudioSegment> segmentsToRemove)
    {
        var removals = segmentsToRemove
            .Where(s => s.Duration > TimeSpan.Zero)
            .OrderBy(s => s.Start)
            .ToList();

        // 重複・隣接する削除区間をマージ
        var merged = new List<AudioSegment>();
        foreach (var seg in removals)
        {
            if (merged.Count > 0 && seg.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = new AudioSegment(last.Start, seg.End > last.End ? seg.End : last.End);
            }
            else
            {
                merged.Add(seg);
            }
        }

        var kept = new List<AudioSegment>();
        var cursor = TimeSpan.Zero;
        foreach (var removal in merged)
        {
            if (removal.Start > cursor)
            {
                kept.Add(new AudioSegment(cursor, removal.Start));
            }
            cursor = removal.End > cursor ? removal.End : cursor;
        }

        if (cursor < totalDuration)
        {
            kept.Add(new AudioSegment(cursor, totalDuration));
        }

        return kept;
    }

    /// <summary>カット＋自動再結合モード: 残す区間をつなぎ、1つの出力ファイルにする。</summary>
    public static async Task ConcatKeptSegmentsAsync(
        string sourceFilePath, IReadOnlyList<AudioSegment> keptSegments, string outputFilePath,
        CancellationToken cancellationToken = default)
    {
        if (keptSegments.Count == 0)
        {
            throw new InvalidOperationException("残す区間がありません（全区間がカット対象になっています）。");
        }

        var filterParts = new List<string>();
        var labels = new List<string>();
        for (var i = 0; i < keptSegments.Count; i++)
        {
            var seg = keptSegments[i];
            var label = $"a{i}";
            labels.Add(label);
            filterParts.Add(
                $"[0:a]atrim=start={seg.Start.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}:end={seg.End.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS[{label}]");
        }

        var concatInputs = string.Concat(labels.Select(l => $"[{l}]"));
        var filterComplex = string.Join(";", filterParts) + $";{concatInputs}concat=n={keptSegments.Count}:v=0:a=1[outa]";

        var sourceBitrateBps = await AudioDecoder.GetAudioBitrateBpsAsync(sourceFilePath, cancellationToken);
        var extraEncodingArgs = BuildEncodingArgs(outputFilePath, sourceBitrateBps);
        var args = $"-y -i \"{sourceFilePath}\" -filter_complex \"{filterComplex}\" -map \"[outa]\" {extraEncodingArgs} \"{outputFilePath}\"";

        var result = await FfmpegProcessRunner.RunAsync(FfmpegLocator.FfmpegExePath, args, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpegによる結合カットに失敗しました: {result.StandardError}");
        }
    }

    /// <summary>カットのみモード: 残す区間を個別ファイルとして書き出す（結合しない・動作確認用）。</summary>
    public static async Task<List<string>> ExportKeptSegmentsIndividuallyAsync(
        string sourceFilePath, IReadOnlyList<AudioSegment> keptSegments, string outputDirectory, string outputBaseName,
        string extensionWithDot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var outputFiles = new List<string>();
        var sourceBitrateBps = await AudioDecoder.GetAudioBitrateBpsAsync(sourceFilePath, cancellationToken);

        for (var i = 0; i < keptSegments.Count; i++)
        {
            var seg = keptSegments[i];
            var outputPath = Path.Combine(outputDirectory, $"{outputBaseName}_part{i + 1:D2}{extensionWithDot}");
            var extraEncodingArgs = BuildEncodingArgs(outputPath, sourceBitrateBps);
            var args = $"-y -i \"{sourceFilePath}\" -ss {seg.Start.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                       $"-to {seg.End.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} {extraEncodingArgs} \"{outputPath}\"";

            var result = await FfmpegProcessRunner.RunAsync(FfmpegLocator.FfmpegExePath, args, cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpegによる区間書き出しに失敗しました（{outputPath}）: {result.StandardError}");
            }

            outputFiles.Add(outputPath);
        }

        return outputFiles;
    }

    /// <summary>元ファイルのビットレートを引き継いで再エンコードする（固定値だと元より低い場合に
    /// ファイルサイズが不必要に膨らむ・高い場合は逆に劣化するため）。取得できない場合は128kbpsを既定値とする。</summary>
    private static string BuildEncodingArgs(string outputFilePath, int? sourceBitrateBps)
    {
        var ext = Path.GetExtension(outputFilePath);
        if (!LossyExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return "";
        }

        var kbps = sourceBitrateBps.HasValue ? Math.Max(32, sourceBitrateBps.Value / 1000) : 128;
        return $"-b:a {kbps}k";
    }
}
