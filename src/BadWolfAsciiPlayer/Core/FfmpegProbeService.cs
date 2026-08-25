using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace BadWolfAsciiPlayer.Core;

public sealed class FfmpegProbeService
{
    public async Task<VideoInfo> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        string ffprobe = FfmpegLocator.Find("ffprobe");
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-select_streams");
        startInfo.ArgumentList.Add("v:0");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("stream=width,height,avg_frame_rate:format=duration");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add(filePath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ffprobe.");
        string json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe failed: {error.Trim()}");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement streams = document.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() == 0)
            throw new InvalidOperationException("The file does not contain a video stream.");

        JsonElement stream = streams[0];
        int width = stream.GetProperty("width").GetInt32();
        int height = stream.GetProperty("height").GetInt32();
        string rateText = stream.TryGetProperty("avg_frame_rate", out JsonElement rateElement)
            ? rateElement.GetString() ?? "0/1"
            : "0/1";
        double frameRate = ParseFraction(rateText);
        if (frameRate <= 0 || double.IsNaN(frameRate) || double.IsInfinity(frameRate))
            frameRate = 30;

        double seconds = 0;
        if (document.RootElement.TryGetProperty("format", out JsonElement format) &&
            format.TryGetProperty("duration", out JsonElement durationElement))
        {
            double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
        }

        return new VideoInfo(width, height, frameRate, TimeSpan.FromSeconds(Math.Max(0, seconds)));
    }

    private static double ParseFraction(string value)
    {
        string[] parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) &&
            denominator != 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double direct)
            ? direct
            : 0;
    }
}
