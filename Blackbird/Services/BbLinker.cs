using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Blackbird.Services;

/// <summary>
/// bb-linker, the Rust linker. When <c>bb-linkerb-linker.exe</c> sits beside the launcher (or
/// <c>BLACKBIRD_BB_LINKER</c> names one) and Tools > Link with bb-linker is on, normal builds link with it and fall
/// back to the stock linker for anything it can't link yet.
/// </summary>
public static class BbLinker
{
    /// <summary>An input bb-linker can't convert yet; the stock linker should link instead.</summary>
    public const int ExitUnsupported = 0x0BB10001;

    /// <summary>bb-linker failed or crashed; the stock linker should link instead.</summary>
    public const int ExitInternal = 0x0BB10002;

    public static string? InstalledPath { get; } = FindInstalled();

    private static string? FindInstalled()
    {
        if (Environment.GetEnvironmentVariable("BLACKBIRD_BB_LINKER") is { Length: > 0 } path && File.Exists(path))
            return path;
        var beside = Path.Combine(AppContext.BaseDirectory, "bb-linker", "bb-linker.exe");
        return File.Exists(beside) ? beside : null;
    }

    private static readonly ConcurrentDictionary<(string Path, DateTime Stamp), Task<string>> Usage = new();

    /// <summary>
    /// Whether this bb-linker takes <paramref name="option"/> (<c>-progress</c>): its usage text, printed
    /// when run bare, lists the options it knows. Read once per exe build, so a linker from before an option still
    /// links, just without it.
    /// </summary>
    public static async Task<bool> SupportsAsync(string exe, string option)
    {
        var usage = await Usage.GetOrAdd((exe, File.GetLastWriteTimeUtc(exe)), key => Task.Run(() => ReadUsage(key.Path)))
            .ConfigureAwait(false);
        return usage.Contains(option, StringComparison.Ordinal);
    }

    private static string ReadUsage(string exe)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null)
                return "";
            var output = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return "";
            }
            return output.Result + stdout.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "";
        }
    }

    public static bool MeansUseStock(int exitCode) => exitCode is ExitUnsupported or ExitInternal;
}
