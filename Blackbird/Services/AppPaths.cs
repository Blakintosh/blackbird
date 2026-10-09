using System;
using System.IO;
using System.Linq;

namespace Blackbird.Services;

public static class AppPaths
{
    /// <summary>%APPDATA%\Blackbird, or BLACKBIRD_DATA_DIR when set (the screenshot harness
    /// uses it so it never reads or writes the user's own settings).</summary>
    public static readonly string DataFolder =
        Environment.GetEnvironmentVariable("BLACKBIRD_DATA_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blackbird");

    /// <summary>True when BLACKBIRD_DATA_DIR points the data somewhere other than %APPDATA%.</summary>
    public static bool IsDataFolderOverridden =>
        Environment.GetEnvironmentVariable("BLACKBIRD_DATA_DIR") is { Length: > 0 };

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");
    public static string LogsFolder => Path.Combine(DataFolder, "logs");
    public static string IconsFolder => Path.Combine(DataFolder, "icons");
    public static string CrashLog => Path.Combine(DataFolder, "crash.log");

    /// <summary>
    /// Earlier builds kept settings, logs and icons under %APPDATA%\Typhoon. Moves each file
    /// Blackbird doesn't already have, and removes the legacy folder once it's empty. Anything
    /// that couldn't move stays put and is retried on the next start; nothing is overwritten.
    /// </summary>
    public static void MigrateLegacyData()
    {
        // An overridden data folder (the screenshot harness) must never pull in the user's real files.
        if (IsDataFolderOverridden)
            return;

        var legacyFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Typhoon");

        if (!Directory.Exists(legacyFolder))
            return;

        Exception? firstFailure = null;
        MergeFolder(legacyFolder, DataFolder, ref firstFailure);

        if (firstFailure is not null)
            throw new IOException($"Some files in {legacyFolder} couldn't be moved.", firstFailure);
    }

    private static void MergeFolder(string source, string target, ref Exception? firstFailure)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var targetFile = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(targetFile))
                continue;

            try
            {
                File.Move(file, targetFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                firstFailure ??= ex;
            }
        }

        foreach (var folder in Directory.EnumerateDirectories(source))
            MergeFolder(folder, Path.Combine(target, Path.GetFileName(folder)), ref firstFailure);

        if (!Directory.EnumerateFileSystemEntries(source).Any())
            Directory.Delete(source);
    }
}
