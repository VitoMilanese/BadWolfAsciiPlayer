using NAudio.Wave;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace BadWolfAsciiPlayer.Core;

public sealed class FfmpegAudioPlayer : IAsyncDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int BitsPerSample = 16;

    private readonly Stopwatch _clock = new();
    private readonly object _sync = new();

    private Process? _process;
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private TimeSpan _basePosition;
    private TimeSpan _pausedPosition;
    private double _volume = 0.8;
    private bool _isPlaying;

    public bool IsPlaying
    {
        get
        {
            lock (_sync)
                return _isPlaying;
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_sync)
            {
                if (_isPlaying)
                    return _basePosition + _clock.Elapsed;

                return _pausedPosition;
            }
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_output is not null)
                _output.Volume = (float)_volume;
        }
    }

    public async Task OpenAsync(string filePath, TimeSpan startAt, CancellationToken cancellationToken = default)
    {
        await StopPipelineAsync();
        StartPipeline(filePath, startAt, cancellationToken);

        lock (_sync)
        {
            _basePosition = startAt;
            _pausedPosition = startAt;
            _clock.Reset();
            _isPlaying = false;
        }
    }

    public void Play()
    {
        lock (_sync)
        {
            if (_output is null || _isPlaying)
                return;

            _basePosition = _pausedPosition;
            _clock.Restart();
            _isPlaying = true;
            _output.Play();
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_output is null || !_isPlaying)
                return;

            _pausedPosition = _basePosition + _clock.Elapsed;
            _clock.Stop();
            _isPlaying = false;
            _output.Pause();
        }
    }

    public async Task SeekAsync(string filePath, TimeSpan target, bool resumePlayback, CancellationToken cancellationToken = default)
    {
        await OpenAsync(filePath, target, cancellationToken);
        if (resumePlayback)
            Play();
    }

    private void StartPipeline(string filePath, TimeSpan startAt, CancellationToken cancellationToken)
    {
        string ffmpeg = FfmpegLocator.Find("ffmpeg");
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add(startAt.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(filePath);
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-sn");
        startInfo.ArgumentList.Add("-dn");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("s16le");
        startInfo.ArgumentList.Add("-acodec");
        startInfo.ArgumentList.Add("pcm_s16le");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add(Channels.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add(SampleRate.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("pipe:1");

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ffmpeg audio decoder.");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var waveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels);
        _buffer = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(6),
            DiscardOnBufferOverflow = false,
            ReadFully = true
        };

        _output = new WaveOutEvent
        {
            DesiredLatency = 100,
            NumberOfBuffers = 3,
            Volume = (float)_volume
        };
        _output.Init(_buffer);

        _pumpTask = PumpAudioAsync(_process, _buffer, _cts.Token);
        _ = DrainErrorsAsync(_process);
    }

    private static async Task PumpAudioAsync(Process process, BufferedWaveProvider buffer, CancellationToken cancellationToken)
    {
        Stream stream = process.StandardOutput.BaseStream;
        byte[] chunk = new byte[16 * 1024];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (buffer.BufferedDuration > TimeSpan.FromSeconds(4) && !cancellationToken.IsCancellationRequested)
                    await Task.Delay(20, cancellationToken);

                int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
                if (read == 0)
                    break;

                buffer.AddSamples(chunk, 0, read);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during seek, file changes, and shutdown.
        }
        catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
        {
            // The buffer can be disposed while a pending seek/shutdown completes.
        }
    }

    private static async Task DrainErrorsAsync(Process process)
    {
        try
        {
            await process.StandardError.ReadToEndAsync();
        }
        catch
        {
            // Process shutdown can race the stderr drain.
        }
    }

    private async Task StopPipelineAsync()
    {
        lock (_sync)
        {
            if (_isPlaying)
                _pausedPosition = _basePosition + _clock.Elapsed;

            _clock.Reset();
            _isPlaying = false;
        }

        CancellationTokenSource? cts = _cts;
        Task? pumpTask = _pumpTask;
        Process? process = _process;
        WaveOutEvent? output = _output;
        BufferedWaveProvider? buffer = _buffer;

        _cts = null;
        _pumpTask = null;
        _process = null;
        _output = null;
        _buffer = null;

        if (cts is not null)
            cts.Cancel();

        try
        {
            output?.Stop();
        }
        catch
        {
            // Best-effort shutdown.
        }

        try
        {
            if (process is not null && !process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort shutdown.
        }

        if (pumpTask is not null)
        {
            try
            {
                await pumpTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        output?.Dispose();
        buffer?.ClearBuffer();
        process?.Dispose();
        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopPipelineAsync();
    }
}
