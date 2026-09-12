using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RadioCmCutter.Core.Detection;
using RadioCmCutter.Core.Evaluation;
using RadioCmCutter.Core.Ffmpeg;
using RadioCmCutter.Core.History;
using RadioCmCutter.Core.Models;
using RadioCmCutter.Core.Pipeline;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;
using ShapePath = System.Windows.Shapes.Path; // System.IO.Path と名前が衝突するため別名にする

namespace RadioCmCutter.App;

/// <summary>
/// メイン画面: ファイル/フォルダ指定→CM検出→波形と候補一覧での確認→カット実行、を一通り行う。
/// </summary>
public partial class MainWindow : Window
{
    private const double PlaybackPreviewSeconds = 1.0;

    private static readonly string[] SupportedExtensions = [".mp3", ".aac", ".m4a", ".wav"];

    private readonly CmHistoryStore _historyStore = CmHistoryStore.Load(CmHistoryStore.GetDefaultFilePath());
    private readonly CmDetectionPipeline _pipeline;
    private readonly MediaPlayer _mediaPlayer = new();
    private readonly DispatcherTimer _playheadTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Line _playheadLine = new() { Stroke = Brushes.Red, StrokeThickness = 2 };

    private List<FileResultItem> _fileResultItems = [];
    private FileResultItem? _selectedItem;
    private DecodedAudio? _selectedAudio;

    private string? _mediaOpenedForPath;
    private TimeSpan? _pendingSeekOnOpen;
    private TimeSpan? _playbackStopAt;

    // 波形の表示範囲（拡大・縮小・スクロール）。全体表示のときは 0〜ファイル長。
    private double _viewStartSeconds;
    private double _viewDurationSeconds;

    // ドラッグによる時間移動の状態。押した位置からの移動量で「クリック」と「ドラッグ」を区別する。
    private Point? _dragOrigin;
    private double _dragOriginViewStartSeconds;
    private bool _dragMoved;

    /// <summary>いま再生位置が入っている区間（一覧で「再生中」の印を付けている行）。</summary>
    private TimelineSegmentRow? _playingRow;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;

        Title = $"{Title}　[build: {GetBuildTimestamp():yyyy-MM-dd HH:mm:ss}]";

        _pipeline = new CmDetectionPipeline(historyStore: _historyStore);
        _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        _mediaPlayer.MediaEnded += (_, _) => StopPlayback();
        _playheadTimer.Tick += PlayheadTimer_Tick;
    }

    /// <summary>リビルドが実際に反映されているかをタイトルバーで確認できるよう、
    /// 実行中のexe/dllのファイル更新日時をビルド時刻として表示する。</summary>
    private static DateTime GetBuildTimestamp() =>
        File.GetLastWriteTime(System.Reflection.Assembly.GetExecutingAssembly().Location);

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
        ClearPlayingRow();

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

        CandidatesDataGrid.ItemsSource = _selectedItem.TimelineRows;
        SelectedFileHeaderText.Text = $"{Path.GetFileName(_selectedItem.FilePath)}　（長さ: {_selectedItem.Result.TotalDuration:hh\\:mm\\:ss}）";

        try
        {
            StatusText.Text = "";
            FooterStatusText.Text = "波形を読み込み中...";
            _selectedAudio = await AudioDecoder.DecodeForAnalysisAsync(_selectedItem.FilePath);
            ResetView();
            UpdatePlaybackPositionText(TimeSpan.Zero);
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
        if (width < 1 || height < 1 || _viewDurationSeconds <= 0) return;

        // 区間のハイライト（背景）: カット対象は赤、検出はされたがカット対象外の候補は黄色で薄く表示
        foreach (var row in _selectedItem.TimelineRows)
        {
            if (!row.CutEnabled && !row.IsDetectedCandidate) continue;

            var fill = row.CutEnabled
                ? new SolidColorBrush(Color.FromArgb(120, 220, 60, 60))
                : new SolidColorBrush(Color.FromArgb(80, 220, 190, 60));
            AddHighlightRectangle(row.Segment.Start.TotalSeconds, row.Segment.End.TotalSeconds, width, height, fill, null);
        }

        // 一覧で選択中の区間（全体のどこにあるかを見るためのハイライト）
        if (CandidatesDataGrid.SelectedItem is TimelineSegmentRow selected)
        {
            AddHighlightRectangle(
                selected.Segment.Start.TotalSeconds,
                selected.Segment.End.TotalSeconds,
                width,
                height,
                new SolidColorBrush(Color.FromArgb(90, 255, 240, 0)),
                new SolidColorBrush(Color.FromArgb(230, 255, 230, 0)));
        }

        WaveformCanvas.Children.Add(BuildWaveformPath(width, height));

        WaveformCanvas.Children.Add(_playheadLine);
        _playheadLine.Y1 = 0;
        _playheadLine.Y2 = height;
        UpdatePlayheadPosition(_mediaPlayer.Position);
    }

    /// <summary>表示範囲に収まる部分だけを1つのPathにまとめて描く（拡大・ドラッグ中も軽快に動くよう、
    /// ピクセル列ごとに個別の図形を作らない）。</summary>
    private ShapePath BuildWaveformPath(double width, double height)
    {
        var samples = _selectedAudio!.Samples;
        var sampleRate = _selectedAudio.SampleRate;
        var half = height / 2;
        var pixelWidth = (int)width;
        var secondsPerPixel = _viewDurationSeconds / width;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var x = 0; x < pixelWidth; x++)
            {
                var fromSeconds = _viewStartSeconds + (x * secondsPerPixel);
                var startSample = (long)(fromSeconds * sampleRate);
                var endSample = (long)((fromSeconds + secondsPerPixel) * sampleRate);
                startSample = Math.Clamp(startSample, 0, samples.Length);
                endSample = Math.Clamp(Math.Max(endSample, startSample + 1), 0, samples.Length);

                float peak = 0;
                for (var s = startSample; s < endSample; s++)
                {
                    var v = Math.Abs(samples[s]);
                    if (v > peak) peak = v;
                }

                var lineHeight = peak * half;
                context.BeginFigure(new Point(x, half - lineHeight), false, false);
                context.LineTo(new Point(x, half + lineHeight), true, false);
            }
        }
        geometry.Freeze();

        return new ShapePath { Data = geometry, Stroke = Brushes.PaleGreen, StrokeThickness = 1 };
    }

    private void AddHighlightRectangle(
        double startSeconds, double endSeconds, double width, double height, Brush fill, Brush? stroke)
    {
        var viewEnd = _viewStartSeconds + _viewDurationSeconds;
        if (endSeconds < _viewStartSeconds || startSeconds > viewEnd) return;

        var x1 = TimeToX(Math.Max(startSeconds, _viewStartSeconds), width);
        var x2 = TimeToX(Math.Min(endSeconds, viewEnd), width);
        var rect = new Rectangle
        {
            Width = Math.Max(1, x2 - x1),
            Height = height,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = stroke is null ? 0 : 1.5,
        };
        Canvas.SetLeft(rect, x1);
        Canvas.SetTop(rect, 0);
        WaveformCanvas.Children.Add(rect);
    }

    private void RenderTimeRuler()
    {
        TimeRulerCanvas.Children.Clear();
        if (_selectedAudio is null || _viewDurationSeconds <= 0) return;

        var width = TimeRulerCanvas.ActualWidth;
        if (width < 1) return;

        var interval = PickTickIntervalSeconds(_viewDurationSeconds);
        var viewEnd = _viewStartSeconds + _viewDurationSeconds;
        var first = Math.Ceiling(_viewStartSeconds / interval) * interval;

        for (var t = first; t <= viewEnd; t += interval)
        {
            var x = TimeToX(t, width);
            var tick = new Line { X1 = x, X2 = x, Y1 = 0, Y2 = 6, Stroke = Brushes.Gray, StrokeThickness = 1 };
            TimeRulerCanvas.Children.Add(tick);

            var label = new TextBlock
            {
                Text = FormatRulerLabel(t, interval),
                Foreground = Brushes.LightGray,
                FontSize = 10,
            };
            Canvas.SetLeft(label, Math.Max(0, x - 15));
            Canvas.SetTop(label, 8);
            TimeRulerCanvas.Children.Add(label);
        }
    }

    /// <summary>目盛りの表記。拡大して目盛り間隔が1秒未満になったときだけ小数を出す。</summary>
    private static string FormatRulerLabel(double seconds, double interval)
    {
        var time = TimeSpan.FromSeconds(seconds);
        if (interval < 1) return time.ToString(@"m\:ss\.f");
        return seconds >= 3600 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private double TimeToX(double seconds, double width) =>
        (seconds - _viewStartSeconds) / _viewDurationSeconds * width;

    private double XToTime(double x, double width) =>
        _viewStartSeconds + (x / width * _viewDurationSeconds);

    private static double PickTickIntervalSeconds(double totalSeconds)
    {
        double[] candidates = [0.1, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800];
        var target = totalSeconds / 10.0;
        foreach (var c in candidates)
        {
            if (c >= target) return c;
        }
        return candidates[^1];
    }

    /// <summary>現在の再生位置からそのまま再生する（停止と合わせて一時停止・再開として使える）。</summary>
    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAudio is null) return;

        var position = _mediaPlayer.Position;
        StartPlayback(position, stopAt: null);
        UpdatePlaybackPositionText(position);
        FooterStatusText.Text = $"{FormatTime(position)} から再生中（停止ボタンで停止）";
    }

    private void PlayStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidatesDataGrid.SelectedItem is TimelineSegmentRow row)
        {
            PlayAround(row.Segment.Start);
        }
    }

    private void PlayEndButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidatesDataGrid.SelectedItem is TimelineSegmentRow row)
        {
            PlayAround(row.Segment.End);
        }
    }

    private void StopPlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
    }

    private void CopyRowsButton_Click(object sender, RoutedEventArgs e)
    {
        CopySelectedRowsToClipboard();
    }

    private void CandidatesDataGrid_CopyExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        CopySelectedRowsToClipboard();
    }

    /// <summary>選択中の行を、デバッグ報告等でそのまま貼り付けられるテキスト形式でクリップボードにコピーする。</summary>
    private void CopySelectedRowsToClipboard()
    {
        var rows = CandidatesDataGrid.SelectedItems.Cast<TimelineSegmentRow>()
            .OrderBy(r => r.Segment.Start)
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var lines = rows.Select(r =>
            $"{FormatTime(r.Segment.Start)} - {FormatTime(r.Segment.End)} " +
            $"({r.Segment.Duration.TotalSeconds:F1}秒) {r.Kind} " +
            $"確信度{r.Confidence:P0} 繰り返し{r.RepeatCount}回 " +
            $"カット{(r.CutEnabled ? "ON" : "OFF")}");

        Clipboard.SetText(string.Join(Environment.NewLine, lines));
        FooterStatusText.Text = $"{rows.Count}行をクリップボードにコピーしました。";
    }

    private static string FormatTime(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.ff");

    private const double MinViewSeconds = 2.0;
    private const double DragThresholdPixels = 4.0;

    private void ResetView()
    {
        _viewStartSeconds = 0;
        _viewDurationSeconds = _selectedAudio?.Duration.TotalSeconds ?? 0;
        RedrawView();
    }

    /// <summary>表示範囲をファイル内に収める。</summary>
    private void ClampView()
    {
        if (_selectedAudio is null) return;

        var totalSeconds = _selectedAudio.Duration.TotalSeconds;
        _viewDurationSeconds = Math.Clamp(_viewDurationSeconds, Math.Min(MinViewSeconds, totalSeconds), totalSeconds);
        _viewStartSeconds = Math.Clamp(_viewStartSeconds, 0, Math.Max(0, totalSeconds - _viewDurationSeconds));
    }

    private void RedrawView()
    {
        ClampView();
        RenderWaveform();
        RenderTimeRuler();
        UpdateViewRangeText();
    }

    private void UpdateViewRangeText()
    {
        if (_selectedAudio is null || _viewDurationSeconds <= 0)
        {
            ViewRangeText.Text = "";
            return;
        }

        var isWholeFile = _viewDurationSeconds >= _selectedAudio.Duration.TotalSeconds - 0.001;
        ViewRangeText.Text = isWholeFile
            ? "全体表示"
            : $"表示範囲 {FormatTime(TimeSpan.FromSeconds(_viewStartSeconds))} 〜 "
                + $"{FormatTime(TimeSpan.FromSeconds(_viewStartSeconds + _viewDurationSeconds))}（ドラッグで移動）";
    }

    /// <summary>表示範囲を拡大・縮小する。再生位置が見えていればそこを、無ければ表示中央を軸にする
    /// （映像編集ソフトと同じく、注目している位置が画面から逃げないようにするため）。</summary>
    private void Zoom(double factor)
    {
        if (_selectedAudio is null || _viewDurationSeconds <= 0) return;

        var playhead = _mediaPlayer.Position.TotalSeconds;
        var anchor = playhead > _viewStartSeconds && playhead < _viewStartSeconds + _viewDurationSeconds
            ? playhead
            : _viewStartSeconds + (_viewDurationSeconds / 2);

        var ratio = (anchor - _viewStartSeconds) / _viewDurationSeconds;
        _viewDurationSeconds *= factor;
        ClampView();
        _viewStartSeconds = anchor - (ratio * _viewDurationSeconds);

        RedrawView();
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => Zoom(0.5);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => Zoom(2.0);

    private void ZoomResetButton_Click(object sender, RoutedEventArgs e) => ResetView();

    private void WaveformCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_selectedAudio is null) return;
        Zoom(e.Delta > 0 ? 0.8 : 1.25);
        e.Handled = true;
    }

    private void WaveformCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_selectedAudio is null) return;

        _dragOrigin = e.GetPosition(WaveformCanvas);
        _dragOriginViewStartSeconds = _viewStartSeconds;
        _dragMoved = false;
        WaveformCanvas.CaptureMouse();
    }

    /// <summary>拡大中はドラッグで時間方向にスクロールする。</summary>
    private void WaveformCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragOrigin is not { } origin || _selectedAudio is null) return;

        var width = WaveformCanvas.ActualWidth;
        if (width < 1) return;

        var deltaX = e.GetPosition(WaveformCanvas).X - origin.X;
        if (!_dragMoved && Math.Abs(deltaX) < DragThresholdPixels) return;

        _dragMoved = true;
        _viewStartSeconds = _dragOriginViewStartSeconds - (deltaX / width * _viewDurationSeconds);
        RedrawView();
    }

    /// <summary>ドラッグでなければ（＝その場でのクリックなら）その位置から再生する。
    /// 検出結果に関係なく自由に内容を確認できるよう、区間再生と違って停止するまで流し続ける。</summary>
    private void WaveformCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        WaveformCanvas.ReleaseMouseCapture();
        if (_dragOrigin is null || _selectedAudio is null) return;

        var wasDrag = _dragMoved;
        _dragOrigin = null;
        _dragMoved = false;
        if (wasDrag) return;

        var width = WaveformCanvas.ActualWidth;
        if (width < 1) return;

        var seconds = Math.Clamp(
            XToTime(e.GetPosition(WaveformCanvas).X, width), 0, _selectedAudio.Duration.TotalSeconds);
        var position = TimeSpan.FromSeconds(seconds);

        // Shift+クリックは再生ではなく、その位置に区切りを追加する
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            SplitAt(position);
            return;
        }

        StartPlayback(position, stopAt: null);
        UpdatePlaybackPositionText(position);
        MarkPlayingRowAt(position);
        SelectRowAt(position); // 位置を明示的に指定した操作なので、編集対象としても選んでおく
        FooterStatusText.Text = $"{FormatTime(position)} から再生中（停止ボタンで停止）";
    }

    /// <summary>区切りを増やしても意味がないほど短い区間を作らないための下限。</summary>
    private const double MinSplitSeconds = 0.2;

    private void SplitAtPlayheadButton_Click(object sender, RoutedEventArgs e) =>
        SplitAt(_mediaPlayer.Position);

    /// <summary>指定時刻を含む区間を2つに分ける（＝その位置に区切りを追加する）。</summary>
    private void SplitAt(TimeSpan at)
    {
        if (_selectedItem is null) return;

        var rows = _selectedItem.TimelineRows;
        var index = rows.FindIndex(r => at > r.Segment.Start && at < r.Segment.End);
        if (index < 0)
        {
            StatusText.Text = "その位置には区切れる区間がありません。";
            return;
        }

        var target = rows[index];
        if ((at - target.Segment.Start).TotalSeconds < MinSplitSeconds
            || (target.Segment.End - at).TotalSeconds < MinSplitSeconds)
        {
            StatusText.Text = "区間の端に近すぎるため区切れません。";
            return;
        }

        var (before, after) = target.SplitAt(at);
        rows[index] = before;
        rows.Insert(index + 1, after);

        StatusText.Text = "";
        RefreshTimelineRows(selectIndex: index + 1);
        FooterStatusText.Text = $"{FormatTime(at)} に区切りを追加しました。";
    }

    private void MergeWithPreviousButton_Click(object sender, RoutedEventArgs e) => MergeSelected(withNext: false);

    private void MergeWithNextButton_Click(object sender, RoutedEventArgs e) => MergeSelected(withNext: true);

    /// <summary>選択中の区間を隣の区間と1つにまとめる（＝間の区切りを取り除く）。</summary>
    private void MergeSelected(bool withNext)
    {
        if (_selectedItem is null) return;

        if (CandidatesDataGrid.SelectedItem is not TimelineSegmentRow selected)
        {
            StatusText.Text = "結合する区間を一覧から選択してください。";
            return;
        }

        var rows = _selectedItem.TimelineRows;
        var index = rows.IndexOf(selected);
        var firstIndex = withNext ? index : index - 1;
        if (firstIndex < 0 || firstIndex + 1 >= rows.Count)
        {
            StatusText.Text = withNext ? "次の区間がありません。" : "前の区間がありません。";
            return;
        }

        var merged = TimelineSegmentRow.Merge(rows[firstIndex], rows[firstIndex + 1]);
        rows[firstIndex] = merged;
        rows.RemoveAt(firstIndex + 1);

        StatusText.Text = "";
        RefreshTimelineRows(selectIndex: firstIndex);
        FooterStatusText.Text =
            $"{FormatTime(merged.Segment.Start)} 〜 {FormatTime(merged.Segment.End)} を1つの区間にまとめました。";
    }

    private void RefreshTimelineRows(int selectIndex)
    {
        CandidatesDataGrid.Items.Refresh();
        if (_selectedItem is not null && selectIndex >= 0 && selectIndex < _selectedItem.TimelineRows.Count)
        {
            CandidatesDataGrid.SelectedItem = _selectedItem.TimelineRows[selectIndex];
            CandidatesDataGrid.ScrollIntoView(_selectedItem.TimelineRows[selectIndex]);
        }

        RedrawView();
    }

    /// <summary>一覧で選択された区間を波形上で黄色く示す。
    /// 拡大中で選択区間が画面外にある場合は、その区間が見える位置まで表示範囲を移動する。</summary>
    private void CandidatesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedAudio is null) return;

        if (CandidatesDataGrid.SelectedItem is TimelineSegmentRow row)
        {
            var viewEnd = _viewStartSeconds + _viewDurationSeconds;
            var isOutsideView = row.Segment.End.TotalSeconds < _viewStartSeconds
                || row.Segment.Start.TotalSeconds > viewEnd;
            if (isOutsideView)
            {
                var center = (row.Segment.Start.TotalSeconds + row.Segment.End.TotalSeconds) / 2;
                _viewStartSeconds = center - (_viewDurationSeconds / 2);
            }
        }

        RedrawView();
    }

    /// <summary>指定位置の少し手前から再生する。前後の流れを聴いて区切りを判断するため、
    /// 区間の終わりで自動停止はせずそのまま流し続ける。</summary>
    private void PlayAround(TimeSpan center)
    {
        if (_selectedAudio is null) return;

        var start = TimeSpan.FromSeconds(Math.Max(0, center.TotalSeconds - PlaybackPreviewSeconds));
        StartPlayback(start, stopAt: null);
        UpdatePlaybackPositionText(start);
    }

    /// <param name="stopAt">この位置で自動停止する。nullなら停止操作があるまで再生を続ける。</param>
    private void StartPlayback(TimeSpan start, TimeSpan? stopAt)
    {
        if (_selectedItem is null) return;

        _playbackStopAt = stopAt;

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

        UpdatePlayheadPosition(position);
        UpdatePlaybackPositionText(position);
        MarkPlayingRowAt(position);
    }

    /// <summary>再生位置を含む区間に「再生中」の印を付ける。
    /// 一覧の選択は編集のための操作なので動かさない（再生しながら別の区間を編集できるようにするため）。</summary>
    private void MarkPlayingRowAt(TimeSpan position)
    {
        if (_selectedItem is null) return;

        if (_playingRow is not null
            && position >= _playingRow.Segment.Start && position < _playingRow.Segment.End)
        {
            return; // 区間が変わっていなければ何もしない
        }

        var row = _selectedItem.TimelineRows
            .FirstOrDefault(r => position >= r.Segment.Start && position < r.Segment.End);

        if (_playingRow is not null) _playingRow.IsPlaying = false;
        _playingRow = row;
        if (row is not null) row.IsPlaying = true;
    }

    private void ClearPlayingRow()
    {
        if (_playingRow is null) return;
        _playingRow.IsPlaying = false;
        _playingRow = null;
    }

    /// <summary>画面で確認・修正した区切りを「正解」として保存する。
    /// 検出精度を測るためだけに使い、検出処理には渡さない。</summary>
    private void SaveGroundTruthButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null) return;

        var totalSeconds = _selectedItem.Result.TotalDuration.TotalSeconds;

        // どこまで確認し終えたかを、選択中の行の終わりとして受け取る。
        // ファイルの一部しか確認していないのに全体を採点対象にすると、未確認部分の検出が
        // すべて「外れ」と数えられて適合率が意味を失うため。
        var annotatedEnd = CandidatesDataGrid.SelectedItem is TimelineSegmentRow selected
            ? selected.Segment.End.TotalSeconds
            : totalSeconds;

        var boundaries = GetTimelineBoundaries().Where(b => b <= annotatedEnd).ToList();

        var groundTruth = new BoundaryGroundTruth
        {
            SourceFileName = Path.GetFileName(_selectedItem.FilePath),
            TotalDurationSeconds = totalSeconds,
            BoundarySeconds = boundaries,
            Segments = _selectedItem.TimelineRows
                .Where(r => r.Segment.Start.TotalSeconds < annotatedEnd)
                .Select(r => new LabeledSegment
                {
                    StartSeconds = r.Segment.Start.TotalSeconds,
                    EndSeconds = Math.Min(r.Segment.End.TotalSeconds, annotatedEnd),
                    Label = r.UserLabel,
                })
                .ToList(),
            AnnotatedRangeStartSeconds = 0,
            AnnotatedRangeEndSeconds = annotatedEnd,
            SavedAtUtc = DateTime.UtcNow,
        };

        var path = BoundaryGroundTruth.GetDefaultFilePath(_selectedItem.FilePath);
        try
        {
            groundTruth.Save(path);
            StatusText.Text = "";

            var isWholeFile = annotatedEnd >= totalSeconds - 0.001;
            FooterStatusText.Text = isWholeFile
                ? $"区切り{boundaries.Count}件を正解として保存しました（ファイル全体を確認済みとして記録）: {path}"
                : $"区切り{boundaries.Count}件を正解として保存しました"
                    + $"（確認済みの範囲を 0:00 〜 {FormatTime(TimeSpan.FromSeconds(annotatedEnd))} として記録。"
                    + $"範囲は確認し終えた最後の行を選んでから保存すると変えられます）: {path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"正解の保存に失敗しました: {ex.Message}";
        }
    }

    /// <summary>保存済みの正解と、検出結果（手動修正前）を突き合わせて精度を出す。</summary>
    private void EvaluateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null) return;

        var path = BoundaryGroundTruth.GetDefaultFilePath(_selectedItem.FilePath);
        var groundTruth = BoundaryGroundTruth.Load(path);
        if (groundTruth is null)
        {
            StatusText.Text = "正解データがありません。区切りを確認・修正してから「正解として保存」してください。";
            return;
        }

        var totalSeconds = _selectedItem.Result.TotalDuration.TotalSeconds;
        var rows = BoundaryBenchmark.Run(
            groundTruth.BoundarySeconds,
            GetDetectedBoundaries(),
            totalSeconds,
            tolerances: null,
            rangeStartSeconds: groundTruth.AnnotatedRangeStartSeconds,
            rangeEndSeconds: groundTruth.AnnotatedRangeEndSeconds);

        var report = new System.Text.StringBuilder();
        report.AppendLine($"{Path.GetFileName(_selectedItem.FilePath)}  （長さ {FormatTime(_selectedItem.Result.TotalDuration)}）");
        report.AppendLine($"正解 {groundTruth.BoundarySeconds.Count}件 / 検出 {GetDetectedBoundaries().Count}件");
        if (groundTruth.AnnotatedRangeEndSeconds is { } rangeEnd)
        {
            var rangeStart = groundTruth.AnnotatedRangeStartSeconds ?? 0;
            report.AppendLine(
                $"採点対象の範囲: {FormatTime(TimeSpan.FromSeconds(rangeStart))} 〜 {FormatTime(TimeSpan.FromSeconds(rangeEnd))}"
                + "（確認し終えていない範囲があるときは、正解ファイルのAnnotatedRange…を書き換えてください）");
        }
        report.AppendLine();
        report.AppendLine(BoundaryBenchmark.Format(rows));

        var text = report.ToString();
        try
        {
            Clipboard.SetText(text); // そのまま報告に貼れるようにする
        }
        catch (Exception)
        {
            // クリップボードが使えなくても測定結果の表示は続ける
        }

        StatusText.Text = "";
        FooterStatusText.Text = "測定結果をクリップボードにコピーしました。";
        MessageBox.Show(this, text, "区切り検出の精度", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>画面上の区間の切れ目（ファイル先頭・末尾を除く）。</summary>
    private List<double> GetTimelineBoundaries() =>
        _selectedItem is null
            ? []
            : _selectedItem.TimelineRows
                .Select(r => r.Segment.Start.TotalSeconds)
                .Where(s => s > 0)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

    /// <summary>検出結果そのものの区切り（手動修正の影響を受けない、採点対象の推定値）。</summary>
    private List<double> GetDetectedBoundaries() =>
        _selectedItem is null
            ? []
            : _selectedItem.Result.Candidates
                .SelectMany(c => new[] { c.Segment.Start.TotalSeconds, c.Segment.End.TotalSeconds })
                .Where(s => s > 0)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

    /// <summary>一覧の行をダブルクリックしたら、その区切りの位置を聴いて確認できるようにする。
    /// 「終了」列なら区間の終わり、それ以外の列なら区間の始まりから再生する
    /// （どちらも少し手前から流し、停止するまで続ける）。</summary>
    private void CandidatesDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CandidatesDataGrid.SelectedItem is not TimelineSegmentRow row) return;

        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        var isEndColumn = cell?.Column == EndTimeColumn;

        PlayAround(isEndColumn ? row.Segment.End : row.Segment.Start);
        FooterStatusText.Text = isEndColumn
            ? $"区間の終了 {FormatTime(row.Segment.End)} を再生中（停止ボタンで停止）"
            : $"区間の開始 {FormatTime(row.Segment.Start)} を再生中（停止ボタンで停止）";
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;

            // クリック元がテキスト要素などVisualでない場合があるため、論理ツリーにも遡れるようにする
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>その時刻を含む区間を一覧で選択する（＝編集対象にする）。
    /// 波形をクリックしたときのように、ユーザーが明示的に位置を指定した操作からのみ呼ぶこと。
    /// 再生の進行に合わせて呼ぶと、再生中に別の区間を選んで編集できなくなる。</summary>
    private void SelectRowAt(TimeSpan position)
    {
        if (_selectedItem is null) return;

        var row = _selectedItem.TimelineRows
            .FirstOrDefault(r => position >= r.Segment.Start && position < r.Segment.End);
        if (row is null) return;

        CandidatesDataGrid.SelectedItem = row;
        CandidatesDataGrid.ScrollIntoView(row);
    }

    private void UpdatePlayheadPosition(TimeSpan position)
    {
        if (_selectedAudio is null || _viewDurationSeconds <= 0) return;

        var width = WaveformCanvas.ActualWidth;
        if (width < 1) return;

        if (!WaveformCanvas.Children.Contains(_playheadLine))
        {
            WaveformCanvas.Children.Add(_playheadLine);
        }

        // 表示範囲の外にいるときは線を隠す（画面端に貼り付いて誤解を招かないように）
        var seconds = position.TotalSeconds;
        var isInView = seconds >= _viewStartSeconds && seconds <= _viewStartSeconds + _viewDurationSeconds;
        _playheadLine.Visibility = isInView ? Visibility.Visible : Visibility.Collapsed;
        if (!isInView) return;

        var x = TimeToX(seconds, width);
        _playheadLine.X1 = x;
        _playheadLine.X2 = x;
    }

    private void UpdatePlaybackPositionText(TimeSpan position)
    {
        if (_selectedAudio is null)
        {
            PlaybackPositionText.Text = "";
            return;
        }

        PlaybackPositionText.Text = $"{FormatTime(position)} / {FormatTime(_selectedAudio.Duration)}";
    }

    private void StopPlayback()
    {
        _playheadTimer.Stop();
        _mediaPlayer.Pause();
        _playbackStopAt = null;
        UpdatePlaybackPositionText(_mediaPlayer.Position);
    }

    /// <summary>
    /// カット実行時点でのユーザーの判断（カット対象ON＝CMとして確定／検出されたのにOFF＝CMではないと確定）を
    /// 音響指紋として履歴に保存する。次回以降の検出（他番組含む）で参考にする。
    /// 何も判断していない区間（未検出かつOFFのまま）は保存しない。
    /// </summary>
    private async Task SaveHistoryFromUserDecisionsAsync(FileResultItem item)
    {
        try
        {
            var audio = await AudioDecoder.DecodeForAnalysisAsync(item.FilePath);
            var frames = FeatureExtractor.Extract(audio);

            foreach (var row in item.TimelineRows)
            {
                if (!row.IsDetectedCandidate && !row.CutEnabled) continue;

                var frameVectors = frames
                    .Where(f => f.Start >= row.Segment.Start && f.Start < row.Segment.End)
                    .Select(f => f.Vector)
                    .ToList();
                if (frameVectors.Count == 0) continue;

                _historyStore.Add(new CmHistoryEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    IsConfirmedCm = row.CutEnabled,
                    FrameVectors = frameVectors,
                    SavedAtUtc = DateTime.UtcNow,
                    SourceFileName = Path.GetFileName(item.FilePath),
                });
            }
        }
        catch
        {
            // 履歴保存の失敗はカット処理自体を妨げない（ベストエフォート）
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
                    item.TimelineRows.Where(r => r.CutEnabled).Select(r => r.Segment));

                await SaveHistoryFromUserDecisionsAsync(item);

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

        try
        {
            _historyStore.Save(CmHistoryStore.GetDefaultFilePath());
        }
        catch
        {
            // 履歴の保存失敗はカット結果自体には影響させない（ベストエフォート）
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
