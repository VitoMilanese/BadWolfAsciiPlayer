using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace BadWolfAsciiPlayer.Core;

public sealed class FfmpegFrameReader : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _stream;
    private readonly int _frameSize;

    private FfmpegFrameReader(Process process, int width, int height)
    {
        _process = process;
        _stream = process.StandardOutput.BaseStream;
        _frameSize = checked(width * height * 3);
    }

    public static FfmpegFrameReader Start(
        string filePath,
        TimeSpan startAt,
        int width,
        int height,
        int fps)
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
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-sn");
        startInfo.ArgumentList.Add("-dn");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"fps={fps},scale={width}:{height}:flags=bicubic");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("rgb24");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("pipe:1");

        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        _ = DrainErrorsAsync(process);
        return new FfmpegFrameReader(process, width, height);
    }

    public async Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        byte[] frame = new byte[_frameSize];
        int offset = 0;
        while (offset < frame.Length)
        {
            int read = await _stream.ReadAsync(frame.AsMemory(offset, frame.Length - offset), cancellationToken);
            if (read == 0)
                return null;
            offset += read;
        }

        return frame;
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

    public ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort shutdown.
        }

        _stream.Dispose();
        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
