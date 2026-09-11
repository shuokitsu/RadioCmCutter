using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RadioCmCutter.Core.Ffmpeg;
using RadioCmCutter.Core.Models;
using RadioCmCutter.Core.Pipeline;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace RadioCmCutter.App;

/// <summary>
/// メイン画面: ファイル/フォルダ指定→CM検出→波形と候補一覧での確認→カット実行、を一通り行う。
/// </summary>
public partial class MainWindow : Window
{
    private const double PlaybackPreviewSeconds = 3.0;

    private static readonly string[] SupportedExtensions = [".mp3", ".aac", ".m4a", ".wav"];

    private readonly CmDetectionPipeline _pipeline = new();
    private readonly MediaPlayer _mediaPlayer = new();
    private readonly DispatcherTimer _playheadTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Line _playheadLine = new() { Stroke = Brushes.Yellow, StrokeThickness = 2 };

    private List<FileResultItem> _fileResultItems = [];
    private FileResultItem? _selectedItem;
    private DecodedAudio? _selectedAudio;

    private string? _mediaOpenedForPath;
    private TimeSpan? _pendingSeekOnOpen;
    private TimeSpan? _playbackStopAt;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;

        _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        _playheadTimer.Tick += PlayheadTimer_Tick;
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

        StatusText.Text = "";
        SetBusy(true);
        FooterStatusText.Text = $"読み込み中... (0/{files.Count})";
        ShowProgress(indeterminate: false, value: 0, max: files.Count);

        try
        {
            List<Core.Models.DetectionResult> results;
            if (files.Count == 1)
            {
                ShowProgress(indeterminate: true);
                FooterStatusText.Text = "検出中...";
                results = [await _pipeline.DetectSingleAsync(files[0])];
            }
            else
            {
                var loadProgress = new Progress<(int Done, int Total)>(p =>
                {
                    FooterStatusText.Text = $"読み込み中... ({p.Done}/{p.Total})";
                    ShowProgress(indeterminate: false, value: p.Done, max: p.Total);
                });

                results = await _pipeline.DetectBatchAsync(files, loadProgress);

                ShowProgress(indeterminate: true);
                FooterStatusText.Text = "検出処理中...";
            }

            _fileResultItems = results.Select(r => new FileResultItem(r)).ToList();
            FilesListBox.ItemsSource = _fileResultItems;
            if (_fileResultItems.Count > 0)
            {
                FilesListBox.SelectedIndex = 0;
            }

            FooterStatusText.Text = $"検出完了: {files.Count}件のファイルを処理しました。ファイルを選択して結果を確認してください。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "検出中にエラーが発生しました", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "検出に失敗しました。";
            FooterStatusText.Text = "";
        }
        finally
        {
            HideProgress();
            SetBusy(false);
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

    private async void FilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopPlayback();

        _selectedItem = FilesListBox.SelectedItem as FileResultItem;
        _selectedAudio = null;
        WaveformCanvas.Children.Clear();
        TimeRulerCanvas.Children.Clear();

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
            StatusText.Text = "";
            FooterStatusText.Text = "波形を読み込み中...";
            _selectedAudio = await AudioDecoder.DecodeForAnalysisAsync(_selectedItem.FilePath);
            RenderWaveform();
            RenderTimeRuler();
            FooterStatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"波形の読み込みに失敗しました: {ex.Message}";
            FooterStatusText.Text = "";
        }
    }

    private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderWaveform();
        RenderTimeRuler();
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

        WaveformCanvas.Children.Add(_playheadLine);
        _playheadLine.Y1 = 0;
        _playheadLine.Y2 = height;
    }

    private void RenderTimeRuler()
    {
        TimeRulerCanvas.Children.Clear();
        if (_selectedAudio is null) return;

        var width = TimeRulerCanvas.ActualWidth;
        var height = TimeRulerCanvas.ActualHeight;
        if (width < 1 || height < 1) return;

        var totalSeconds = _selectedAudio.Duration.TotalSeconds;
        if (totalSeconds <= 0) return;

        var interval = PickTickIntervalSeconds(totalSeconds);
        for (var t = 0.0; t <= totalSeconds; t += interval)
        {
            var x = t / totalSeconds * width;
            var tick = new Line { X1 = x, X2 = x, Y1 = 0, Y2 = 6, Stroke = Brushes.Gray, StrokeThickness = 1 };
            TimeRulerCanvas.Children.Add(tick);

            var label = new TextBlock
            {
                Text = TimeSpan.FromSeconds(t).ToString(t >= 3600 ? @"h\:mm\:ss" : @"m\:ss"),
                Foreground = Brushes.LightGray,
                FontSize = 10,
            };
            Canvas.SetLeft(label, Math.Max(0, x - 15));
            Canvas.SetTop(label, 8);
            TimeRulerCanvas.Children.Add(label);
        }
    }

    private static double PickTickIntervalSeconds(double totalSeconds)
    {
        double[] candidates = [5, 10, 15, 30, 60, 120, 300, 600, 900, 1800];
        var target = totalSeconds / 10.0;
        foreach (var c in candidates)
        {
            if (c >= target) return c;
        }
        return candidates[^1];
    }

    private void PlayStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidatesDataGrid.SelectedItem is CmCandidate candidate)
        {
            PlayAround(candidate.Segment.Start);
        }
    }

    private void PlayEndButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidatesDataGrid.SelectedItem is CmCandidate candidate)
        {
            PlayAround(candidate.Segment.End);
        }
    }

    private void StopPlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
    }

    private void PlayAround(TimeSpan center)
    {
        if (_selectedItem is null || _selectedAudio is null) return;

        var totalSeconds = _selectedAudio.Duration.TotalSeconds;
        var start = TimeSpan.FromSeconds(Math.Max(0, center.TotalSeconds - PlaybackPreviewSeconds));
        var stop = TimeSpan.FromSeconds(Math.Min(totalSeconds, center.TotalSeconds + PlaybackPreviewSeconds));
        _playbackStopAt = stop;

        if (_mediaOpenedForPath == _selectedItem.FilePath)
        {
            _mediaPlayer.Position = start;
            _mediaPlayer.Play();
            _playheadTimer.Start();
        }
        else
        {
            _pendingSeekOnOpen = start;
            _mediaOpenedForPath = _selectedItem.FilePath;
            _mediaPlayer.Open(new Uri(_selectedItem.FilePath));
        }
    }

    private void MediaPlayer_MediaOpened(object? sender, EventArgs e)
    {
        if (_pendingSeekOnOpen is not { } seek) return;

        _mediaPlayer.Position = seek;
        _mediaPlayer.Play();
        _playheadTimer.Start();
        _pendingSeekOnOpen = null;
    }

    private void PlayheadTimer_Tick(object? sender, EventArgs e)
    {
        if (_selectedAudio is null) return;

        var position = _mediaPlayer.Position;
        if (_playbackStopAt is { } stopAt && position >= stopAt)
        {
            StopPlayback();
            return;
        }

        var width = WaveformCanvas.ActualWidth;
        var totalSeconds = _selectedAudio.Duration.TotalSeconds;
        if (width < 1 || totalSeconds <= 0) return;

        if (!WaveformCanvas.Children.Contains(_playheadLine))
        {
            WaveformCanvas.Children.Add(_playheadLine);
        }

        var x = position.TotalSeconds / totalSeconds * width;
        _playheadLine.X1 = x;
        _playheadLine.X2 = x;
    }

    private void StopPlayback()
    {
        _playheadTimer.Stop();
        _mediaPlayer.Pause();
        _playbackStopAt = null;
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

        StatusText.Text = "";
        var total = _fileResultItems.Count;
        SetBusy(true);
        FooterStatusText.Text = $"カット中... (0/{total})";
        ShowProgress(indeterminate: false, value: 0, max: total);

        var succeeded = 0;
        var errors = new List<string>();

        for (var i = 0; i < _fileResultItems.Count; i++)
        {
            var item = _fileResultItems[i];
            FooterStatusText.Text = $"カット中... ({i + 1}/{total}) {Path.GetFileName(item.FilePath)}";
            ShowProgress(indeterminate: false, value: i, max: total);

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

            ShowProgress(indeterminate: false, value: i + 1, max: total);
        }

        FooterStatusText.Text = $"カット完了: 成功 {succeeded}件 / 失敗 {errors.Count}件（出力先: {outputFolder}）";
        HideProgress();
        SetBusy(false);

        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "一部のファイルでエラーが発生しました", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowProgress(bool indeterminate, double value = 0, double max = 1)
    {
        FooterProgressBar.Visibility = Visibility.Visible;
        FooterProgressBar.IsIndeterminate = indeterminate;
        if (!indeterminate)
        {
            FooterProgressBar.Maximum = Math.Max(1, max);
            FooterProgressBar.Value = value;
        }
    }

    private void HideProgress()
    {
        FooterProgressBar.Visibility = Visibility.Collapsed;
        FooterProgressBar.IsIndeterminate = false;
    }

    private void SetBusy(bool busy)
    {
        DetectButton.IsEnabled = !busy;
        ExecuteCutButton.IsEnabled = !busy;
        BrowseInputButton.IsEnabled = !busy;
        BrowseOutputButton.IsEnabled = !busy;
    }
}
