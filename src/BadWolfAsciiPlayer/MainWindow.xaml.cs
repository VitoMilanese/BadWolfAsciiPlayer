using BadWolfAsciiPlayer.Core;
using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace BadWolfAsciiPlayer;

public partial class MainWindow : Window
{
    private const int EdgeAnalysisScale = 3;

    private readonly FfmpegProbeService _probeService = new();
    private readonly FfmpegAudioPlayer _audioPlayer = new();
    private readonly AsciiRenderer _renderer = new();
    private readonly DispatcherTimer _uiTimer;

    private string? _filePath;
    private VideoInfo? _videoInfo;
    private CancellationTokenSource? _decoderCts;
    private Task? _decoderTask;
    private bool _isPlaying;
    private bool _isSeeking;
    private bool _handlingMediaEnd;
    private long _decoderGeneration;
    private byte[]? _lastFrame;
    private int _lastFrameSourceWidth;
    private int _lastFrameSourceHeight;
    private int _lastFrameColumns;
    private int _lastFrameRows;
    private string _lastAsciiText = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        _audioPlayer.Volume = VolumeSlider.Value;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open local video",
            Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.m4v;*.wmv|All files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
            await LoadVideoAsync(dialog.FileName);
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            await LoadVideoAsync(files[0]);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async Task LoadVideoAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        StatusText.Text = "Opening...";
        PlayPauseButton.IsEnabled = false;
        SeekSlider.IsEnabled = false;
        CopyFrameButton.IsEnabled = false;
        _isPlaying = false;
        _lastFrame = null;
        _lastAsciiText = string.Empty;
        SelectableAsciiText.Clear();
        await StopDecoderAsync();

        try
        {
            VideoInfo info = await _probeService.ProbeAsync(filePath);
            _filePath = filePath;
            _videoInfo = info;

            FileNameText.Text = Path.GetFileName(filePath);
            DropHint.Visibility = Visibility.Collapsed;
            SeekSlider.Minimum = 0;
            SeekSlider.Maximum = Math.Max(0.01, info.Duration.TotalSeconds);
            SeekSlider.Value = 0;
            PositionText.Text = $"00:00 / {FormatTime(info.Duration)}";
            PlayPauseButton.Content = "Play";

            await _audioPlayer.OpenAsync(filePath, TimeSpan.Zero);
            await RestartDecoderAsync(TimeSpan.Zero);

            PlayPauseButton.IsEnabled = true;
            SeekSlider.IsEnabled = true;
            StatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            _filePath = null;
            _videoInfo = null;
            DropHint.Visibility = Visibility.Visible;
        }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath is null || _videoInfo is null)
            return;

        if (_isPlaying)
        {
            _audioPlayer.Pause();
            _isPlaying = false;
            PlayPauseButton.Content = "Play";
        }
        else
        {
            if (_audioPlayer.Position >= _videoInfo.Duration - TimeSpan.FromMilliseconds(250))
                await SeekPlaybackAsync(TimeSpan.Zero, resumePlayback: false);

            _audioPlayer.Play();
            _isPlaying = true;
            PlayPauseButton.Content = "Pause";
        }
    }

    private async void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (_videoInfo is null)
            return;

        TimeSpan position = _audioPlayer.Position;
        if (position > _videoInfo.Duration)
            position = _videoInfo.Duration;

        if (!_isSeeking)
            SeekSlider.Value = Math.Clamp(position.TotalSeconds, SeekSlider.Minimum, SeekSlider.Maximum);

        PositionText.Text = $"{FormatTime(position)} / {FormatTime(_videoInfo.Duration)}";

        if (_isPlaying && !_handlingMediaEnd && position >= _videoInfo.Duration - TimeSpan.FromMilliseconds(50))
        {
            _handlingMediaEnd = true;
            try
            {
                _isPlaying = false;
                _audioPlayer.Pause();
                PlayPauseButton.Content = "Play";
                await SeekPlaybackAsync(TimeSpan.Zero, resumePlayback: false);
            }
            finally
            {
                _handlingMediaEnd = false;
            }
        }
    }

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = true;
    }

    private async void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_videoInfo is null || _filePath is null)
        {
            _isSeeking = false;
            return;
        }

        TimeSpan target = TimeSpan.FromSeconds(Math.Clamp(SeekSlider.Value, 0, _videoInfo.Duration.TotalSeconds));
        bool resumePlayback = _isPlaying;

        try
        {
            await SeekPlaybackAsync(target, resumePlayback);
        }
        finally
        {
            _isSeeking = false;
        }
    }

    private async Task SeekPlaybackAsync(TimeSpan target, bool resumePlayback)
    {
        if (_filePath is null)
            return;

        await _audioPlayer.SeekAsync(_filePath, target, resumePlayback);
        await RestartDecoderAsync(target);
        _isPlaying = resumePlayback;
        PlayPauseButton.Content = resumePlayback ? "Pause" : "Play";
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audioPlayer is not null)
            _audioPlayer.Volume = e.NewValue;
    }

    private async void RenderSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _filePath is null || _videoInfo is null)
            return;

        SelectableAsciiText.Select(0, 0);
        await RestartDecoderAsync(_audioPlayer.Position);
    }

    private void DisplayMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        UpdateDisplayMode();
    }

    private void UpdateDisplayMode()
    {
        bool selectable = IsSelectableTextMode();
        AsciiImage.Visibility = selectable ? Visibility.Collapsed : Visibility.Visible;
        SelectableAsciiView.Visibility = selectable ? Visibility.Visible : Visibility.Collapsed;

        if (selectable && _lastFrame is not null)
        {
            _lastAsciiText = _renderer.RenderText(
                _lastFrame,
                _lastFrameSourceWidth,
                _lastFrameSourceHeight,
                _lastFrameColumns,
                _lastFrameRows,
                GetEdgeStrength());
            SelectableAsciiText.Text = _lastAsciiText;
        }
    }

    private void CopyFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFrame is null)
            return;

        if (string.IsNullOrEmpty(_lastAsciiText))
        {
            _lastAsciiText = _renderer.RenderText(
                _lastFrame,
                _lastFrameSourceWidth,
                _lastFrameSourceHeight,
                _lastFrameColumns,
                _lastFrameRows,
                GetEdgeStrength());
        }

        try
        {
            Clipboard.SetText(_lastAsciiText);
            StatusText.Text = "ASCII frame copied";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not copy frame: {ex.Message}";
        }
    }

    private async Task RestartDecoderAsync(TimeSpan startAt)
    {
        if (_filePath is null || _videoInfo is null)
            return;

        await StopDecoderAsync();

        int columns = GetComboInt(ColumnsCombo, 160);
        int fps = GetComboInt(FpsCombo, 30);
        int rows = CalculateRows(_videoInfo, columns);
        int sourceWidth = checked(columns * EdgeAnalysisScale);
        int sourceHeight = checked(rows * EdgeAnalysisScale);
        long generation = Interlocked.Increment(ref _decoderGeneration);
        _decoderCts = new CancellationTokenSource();
        CancellationToken token = _decoderCts.Token;
        AsciiMode mode = GetMode();
        double edgeStrength = GetEdgeStrength();
        string filePath = _filePath;

        AsciiImage.Source = _renderer.EnsureBitmap(columns, rows);

        _decoderTask = Task.Run(async () =>
        {
            try
            {
                await using FfmpegFrameReader reader = FfmpegFrameReader.Start(
                    filePath,
                    startAt,
                    sourceWidth,
                    sourceHeight,
                    fps);
                long frameIndex = 0;
                double frameDuration = 1.0 / fps;

                while (!token.IsCancellationRequested && generation == Volatile.Read(ref _decoderGeneration))
                {
                    byte[]? frame = await reader.ReadFrameAsync(token);
                    if (frame is null)
                        break;

                    TimeSpan frameTime = startAt + TimeSpan.FromSeconds(frameIndex * frameDuration);
                    frameIndex++;

                    while (!token.IsCancellationRequested)
                    {
                        TimeSpan mediaPosition = _audioPlayer.Position;
                        bool playing = await Dispatcher.InvokeAsync(() => _isPlaying, DispatcherPriority.Background, token);
                        double deltaMs = (frameTime - mediaPosition).TotalMilliseconds;

                        if (deltaMs <= 10 || !playing)
                            break;

                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(deltaMs, 1, 25)), token);
                    }

                    if (token.IsCancellationRequested)
                        break;

                    TimeSpan now = _audioPlayer.Position;
                    bool currentlyPlaying = await Dispatcher.InvokeAsync(() => _isPlaying, DispatcherPriority.Background, token);
                    if (currentlyPlaying && (now - frameTime).TotalMilliseconds > 180)
                        continue;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (generation != Volatile.Read(ref _decoderGeneration))
                            return;

                        _lastFrame = frame;
                        _lastFrameSourceWidth = sourceWidth;
                        _lastFrameSourceHeight = sourceHeight;
                        _lastFrameColumns = columns;
                        _lastFrameRows = rows;
                        _lastAsciiText = string.Empty;
                        CopyFrameButton.IsEnabled = true;

                        if (IsSelectableTextMode())
                        {
                            if (SelectableAsciiText.SelectionLength == 0)
                            {
                                _lastAsciiText = _renderer.RenderText(
                                    frame,
                                    sourceWidth,
                                    sourceHeight,
                                    columns,
                                    rows,
                                    edgeStrength);
                                SelectableAsciiText.Text = _lastAsciiText;
                            }
                        }
                        else
                        {
                            _renderer.Render(
                                frame,
                                sourceWidth,
                                sourceHeight,
                                columns,
                                rows,
                                mode,
                                edgeStrength);
                        }
                    }, DispatcherPriority.Render, token);

                    bool isPlaying = await Dispatcher.InvokeAsync(() => _isPlaying, DispatcherPriority.Background, token);
                    if (!isPlaying)
                    {
                        while (!token.IsCancellationRequested)
                        {
                            bool resumed = await Dispatcher.InvokeAsync(() => _isPlaying, DispatcherPriority.Background, token);
                            if (resumed)
                                break;
                            await Task.Delay(30, token);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when seeking, changing settings, opening a new file, or closing.
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => StatusText.Text = ex.Message);
            }
        }, token);
    }

    private async Task StopDecoderAsync()
    {
        Interlocked.Increment(ref _decoderGeneration);
        CancellationTokenSource? cts = _decoderCts;
        Task? task = _decoderTask;
        _decoderCts = null;
        _decoderTask = null;

        if (cts is not null)
        {
            cts.Cancel();
            try
            {
                if (task is not null)
                    await task;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
            finally
            {
                cts.Dispose();
            }
        }
    }

    private bool IsSelectableTextMode()
    {
        return DisplayCombo.SelectedItem is ComboBoxItem item
            && string.Equals(item.Content?.ToString(), "Selectable text", StringComparison.OrdinalIgnoreCase);
    }

    private double GetEdgeStrength()
    {
        if (EdgeCombo.SelectedItem is not ComboBoxItem item)
            return 1.0;

        return item.Content?.ToString() switch
        {
            "Off" => 0.0,
            "Low" => 0.55,
            "High" => 1.35,
            _ => 0.9
        };
    }

    private AsciiMode GetMode()
    {
        return ModeCombo.SelectedItem is ComboBoxItem item && string.Equals(item.Content?.ToString(), "Mono", StringComparison.OrdinalIgnoreCase)
            ? AsciiMode.Mono
            : AsciiMode.Color;
    }

    private static int GetComboInt(ComboBox combo, int fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out int value))
            return value;
        return fallback;
    }

    private static int CalculateRows(VideoInfo info, int columns)
    {
        double sourceAspect = info.Height / (double)info.Width;
        double cellAspectCorrection = AsciiRenderer.CellWidth / (double)AsciiRenderer.CellHeight;
        return Math.Clamp((int)Math.Round(columns * sourceAspect * cellAspectCorrection), 20, 180);
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value.TotalHours >= 1)
            return value.ToString(@"h\:mm\:ss");
        return value.ToString(@"m\:ss");
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        _uiTimer.Stop();
        await StopDecoderAsync();
        await _audioPlayer.DisposeAsync();
    }
}
