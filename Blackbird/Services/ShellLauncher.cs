using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Blackbird.Services;

/// <summary>
/// Starts tools, files, folders and URLs. A start that fails (no handler for the file type, the
/// exe gone or blocked) comes back as a reason for the caller to show, never as an exception.
/// </summary>
public static class ShellLauncher
{
    /// <summary>Starts <paramref name="startInfo"/>; returns null when it started, else why it didn't, as the end of a sentence (\"Couldn't open Radiant: {reason}\").</summary>
    public static string? TryStart(ProcessStartInfo startInfo)
    {
        try
        {
            Process.Start(startInfo)?.Dispose();
            return null;
        }
        catch (Win32Exception ex)
        {
            return ex.NativeErrorCode switch
            {
                2 or 3 => "it isn't there any more.",
                5 => "Windows denied access.",
                1155 => "Windows has no program set to open it.",
                1223 => "it was cancelled.",
                _ => "Windows couldn't start it.",
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or PlatformNotSupportedException)
        {
            return "Windows couldn't start it.";
        }
    }

    /// <summary>Opens a file, folder or URL with whatever Windows associates with it.</summary>
    public static string? TryOpen(string pathOrUrl) =>
        TryStart(new ProcessStartInfo { FileName = pathOrUrl, UseShellExecute = true });
}
