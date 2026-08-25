using System.Diagnostics;
using System.IO;

namespace BadWolfAsciiPlayer.Core;

public static class FfmpegLocator
{
    public static string Find(string executableName)
    {
        string local = Path.Combine(AppContext.BaseDirectory, "tools", executableName + ".exe");
        if (File.Exists(local))
            return local;

        local = Path.Combine(AppContext.BaseDirectory, executableName + ".exe");
        if (File.Exists(local))
            return local;

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim(), executableName + ".exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        throw new FileNotFoundException(
            $"{executableName}.exe was not found. Put ffmpeg.exe and ffprobe.exe in the app's tools folder or add them to PATH.");
    }
}
