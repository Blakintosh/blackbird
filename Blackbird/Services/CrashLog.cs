using System;
using System.IO;

namespace Blackbird.Services;

public static class CrashLog
{
    private static readonly object WriteLock = new();

    public static void Write(string source, object? exception)
    {
        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                File.AppendAllText(
                    AppPaths.CrashLog,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Nowhere left to report a failure to write the crash log.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
