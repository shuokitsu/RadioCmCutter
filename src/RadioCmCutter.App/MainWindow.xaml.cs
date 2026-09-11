using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using RadioCmCutter.Core.Ffmpeg;
using RadioCmCutter.Core.Pipeline;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace RadioCmCutter.App;

/// <summary>
/// メイン画面: ファイル/フォルダ指定→CM検出→波形と候補一覧での確認→カット実行、を一通り行う。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] SupportedExtensions = [".mp3", ".aac", ".m4a", ".wav"];

    private readonly CmDetectionPipeline _pipeline = new();
    private List<FileResultItem> _fileResultItems = [];
    private FileResultItem? _selectedItem;
    private DecodedAudio? _selectedAudio;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!FfmpegLocator.TryVerify(out var message))
        {
            StatusText.Text = $"警告: {message}";
        }
    }

    private void BrowseInputButton_Click(object sender, RoutedEventArgs e)
    {
        if (SingleFileModeRadio.IsChecked == true)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "対応音声ファイル|*.mp3;*.aac;*.m4a;*.wav|すべてのファイル|*.*",
            };
            if (dialog.ShowDialog(this) == true)
            {
                InputPathTextBox.Text = dialog.FileName;
            }
        }
        else
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog(this) == true)
            {
                InputPathTextBox.Text = dialog.FolderName;
            }
        }
    }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true)
        {
            OutputFolderTextBox.Text = dialog.FolderName;
        }
    }

    private async void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        List<string> files;
        try
        {
            files = ResolveInputFiles();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (files.Count == 0)
        {
            MessageBox.Show(this, "対応するファイルが見つかりませんでした。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, $"検出中... (0/{files.Count})");
        try
        {
            var results = files.Count == 1
                ? [await _pipeline.DetectSingleAsync(files[0])]
                : await _pipeline.DetectBatchAsync(files);

            _fileResultItems = results.Select(r => new FileResultItem(r)).ToList();
            FilesListBox.ItemsSource = _fileResultItems;
            if (_fileResultItems.Count > 0)
            {
                FilesListBox.SelectedIndex = 0;
            }

            StatusText.Text = $"検出完了: {files.Count}件のファイルを処理しました。ファイルを選択して結果を確認してください。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "検出中にエラーが発生しました", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "検出に失敗しました。";
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private List<string> ResolveInputFiles()
    {
        var path = InputPathTextBox.Text?.Trim() ?? "";

        if (SingleFileModeRadio.IsChecked == true)
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("ファイルが見つかりません。パスを確認してください。");
            }
            return [path];
        }

        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException("フォルダが見つかりません。パスを確認してください。");
        }

        return Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async void FilesListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedItem = FilesListBox.SelectedItem as FileResultItem;
        _selectedAudio = null;
        WaveformCanvas.Children.Clear();

        if (_selectedItem is null)
        {
            CandidatesDataGrid.ItemsSource = null;
            SelectedFileHeaderText.Text = "";
            return;
        }

        CandidatesDataGrid.ItemsSource = _selectedItem.Result.Candidates;
        SelectedFileHeaderText.Text = $"{Path.GetFileName(_selectedItem.FilePath)}　（長さ: {_selectedItem.Result.TotalDuration:hh\\:mm\\:ss}）";

        try
        {
            StatusText.Text = "波形を読み込み中...";
            _selectedAudio = await AudioDecoder.DecodeForAnalysisAsync(_selectedItem.FilePath);
            RenderWaveform();
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"波形の読み込みに失敗しました: {ex.Message}";
        }
    }

    private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderWaveform();
    }

    private void RenderWaveform()
    {
        WaveformCanvas.Children.Clear();
        if (_selectedAudio is null || _selectedItem is null) return;

        var width = WaveformCanvas.ActualWidth;
        var height = WaveformCanvas.ActualHeight;
        if (width < 1 || height < 1) return;

        var totalSeconds = _selectedAudio.Duration.TotalSeconds;
        if (totalSeconds <= 0) return;

        // CM候補区間のハイライト（背景）
        foreach (var candidate in _selectedItem.Result.Candidates)
        {
            var x1 = candidate.Segment.Start.TotalSeconds / totalSeconds * width;
            var x2 = candidate.Segment.End.TotalSeconds / totalSeconds * width;
            var rect = new Rectangle
            {
                Width = Math.Max(1, x2 - x1),
                Height = height,
                Fill = new SolidColorBrush(Color.FromArgb(120, 220, 60, 60)),
            };
            Canvas.SetLeft(rect, x1);
            Canvas.SetTop(rect, 0);
            WaveformCanvas.Children.Add(rect);
        }

        // 波形（縦バー方式）
        var samples = _selectedAudio.Samples;
        var pixelWidth = (int)width;
        var half = height / 2;
        for (var x = 0; x < pixelWidth; x++)
        {
            var startSample = (long)((double)x / pixelWidth * samples.Length);
            var endSample = (long)((double)(x + 1) / pixelWidth * samples.Length);
            endSample = Math.Min(samples.Length, Math.Max(endSample, startSample + 1));

            float peak = 0;
            for (var s = startSample; s < endSample; s++)
            {
                var v = Math.Abs(samples[s]);
                if (v > peak) peak = v;
            }

            var lineHeight = peak * half;
            var line = new Line
            {
                X1 = x, X2 = x,
                Y1 = half - lineHeight, Y2 = half + lineHeight,
                Stroke = Brushes.PaleGreen,
                StrokeThickness = 1,
            };
            WaveformCanvas.Children.Add(line);
        }
    }

    private async void ExecuteCutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_fileResultItems.Count == 0)
        {
            MessageBox.Show(this, "先に「検出開始」を実行してください。", "カット対象がありません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var outputFolder = OutputFolderTextBox.Text?.Trim() ?? "";
        if (outputFolder.Length == 0)
        {
            outputFolder = Path.Combine(Path.GetDirectoryName(_fileResultItems[0].FilePath) ?? ".", "output");
            OutputFolderTextBox.Text = outputFolder;
        }

        try
        {
            Directory.CreateDirectory(outputFolder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"出力先フォルダを作成できませんでした: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SetBusy(true, "カット処理中...");
        var succeeded = 0;
        var errors = new List<string>();

        foreach (var item in _fileResultItems)
        {
            try
            {
                var keptSegments = AudioCutter.ComputeKeptSegments(
                    item.Result.TotalDuration,
                    item.Result.Candidates.Where(c => c.CutEnabled).Select(c => c.Segment));

                var ext = Path.GetExtension(item.FilePath);
                var baseName = Path.GetFileNameWithoutExtension(item.FilePath);

                if (ConcatModeRadio.IsChecked == true)
                {
                    var outputPath = Path.Combine(outputFolder, $"{baseName}_cut{ext}");
                    await AudioCutter.ConcatKeptSegmentsAsync(item.FilePath, keptSegments, outputPath);
                }
                else
                {
                    await AudioCutter.ExportKeptSegmentsIndividuallyAsync(item.FilePath, keptSegments, outputFolder, baseName, ext);
                }

                succeeded++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(item.FilePath)}: {ex.Message}");
            }
        }

        FooterStatusText.Text = $"カット完了: 成功 {succeeded}件 / 失敗 {errors.Count}件（出力先: {outputFolder}）";
        SetBusy(false, StatusText.Text);

        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "一部のファイルでエラーが発生しました", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetBusy(bool busy, string statusMessage)
    {
        DetectButton.IsEnabled = !busy;
        ExecuteCutButton.IsEnabled = !busy;
        BrowseInputButton.IsEnabled = !busy;
        BrowseOutputButton.IsEnabled = !busy;
        StatusText.Text = statusMessage;
    }
}
