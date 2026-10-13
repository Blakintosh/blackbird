using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Blackbird.Models;
using Microsoft.Win32.SafeHandles;

namespace Blackbird.Services;

/// <summary>
/// Where the Mod Tools apps leave notes for each other: <c>%LOCALAPPDATA%\BO3ModTools\components\&lt;install hash&gt;\&lt;id&gt;.json</c>,
/// one folder per Black Ops III install. The install hash is the Stock Library's, so every app agrees on it.
/// </summary>
public static class ComponentPaths
{
    /// <summary>%LOCALAPPDATA%\BO3ModTools, or a folder inside BLACKBIRD_DATA_DIR when that overrides the data (the screenshot harness).</summary>
    public static string Root { get; } = AppPaths.IsDataFolderOverridden
        ? Path.Combine(AppPaths.DataFolder, "BO3ModTools")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BO3ModTools");

    public static string ComponentsFolder(string install) => Path.Combine(Root, "components", InstallHash(install));

    public static string Entry(string install, string id) => Path.Combine(ComponentsFolder(install), id + ".json");

    /// <summary>The Stock Library's index for this install.</summary>
    public static string StockDb(string install) => Path.Combine(Root, "stock", InstallHash(install), "stock.db");

    /// <summary>FNV-1a 64 of the lower-cased canonical install path, as 16 hex digits (the Stock Library's derivation).</summary>
    public static string InstallHash(string install)
    {
        var canonical = Canonical(install).ToLowerInvariant().Replace('/', '\\').TrimEnd('\\');
        var hash = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(canonical))
        {
            hash ^= b;
            hash *= 0x100000001b3UL;
        }
        return hash.ToString("x16");
    }

    /// <summary>
    /// The install path as the Stock Library stores and hashes it: the final path Windows resolves it to (links and
    /// substituted drives followed), without the <c>\\?\</c> prefix. A folder that isn't there is hashed as given.
    /// </summary>
    public static string Canonical(string install)
    {
        var full = Path.GetFullPath(install);
        var resolved = FinalPath(full) ?? full;
        return resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + resolved[8..]
            : resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[4..]
            : resolved;
    }

    private static string? FinalPath(string folder)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(folder))
            return null;
        using var handle = CreateFile(folder, 0, 7, IntPtr.Zero, 3, 0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;
        var text = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, text, text.Capacity, 0);
        return length == 0 || length >= text.Capacity ? null : text.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int size, uint flags);
}

/// <summary>
/// Blackbird's entry in the components registry (<c>blackbird.json</c>): what it is, whether it links with
/// bb-linker and with which, and how the last link of each project went. Written whole to a temp file and renamed
/// over the old one, so a reader never sees half of it. Best effort: another app's notes are not worth failing a build.
/// </summary>
public sealed class ComponentRegistry
{
    private const int MaxProjects = 100;

    private readonly Func<string> _installPath;
    private readonly Func<string> _toolsPath;
    private readonly Func<bool> _useBbLinker;
    private readonly object _lock = new();
    private readonly Dictionary<string, JsonObject> _builds = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public ComponentRegistry(Func<string> installPath, Func<string> toolsPath, Func<bool> useBbLinker)
    {
        _installPath = installPath;
        _toolsPath = toolsPath;
        _useBbLinker = useBbLinker;
    }

    public static string Version { get; } = AppUpdates.CurrentVersion;

    /// <summary>Rewrites the entry from the current settings and tools. Runs off the UI thread.</summary>
    public Task WriteAsync() => Task.Run(Write);

    /// <summary>Notes how a project's link went (the latest per project) and rewrites the entry.</summary>
    public Task RecordBuildAsync(string project, LinkOutcome outcome) => Task.Run(() =>
    {
        lock (_lock)
        {
            LoadBuilds();
            _builds[project] = new JsonObject
            {
                ["linker"] = outcome.Linker,
                ["fell_back"] = outcome.FellBack,
                ["exit"] = outcome.ExitCode,
                ["ran"] = outcome.Ran,
                ["time"] = DateTime.UtcNow.ToString("o"),
            };
            foreach (var stale in _builds.OrderByDescending(b => b.Value["time"]?.GetValue<string>(), StringComparer.Ordinal)
                         .Skip(MaxProjects).Select(b => b.Key).ToList())
                _builds.Remove(stale);
        }
        Write();
    });

    public void Write()
    {
        try
        {
            var install = _installPath();
            if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install))
                return;

            var tools = _toolsPath();
            var configured = BbLinker.ConfiguredPath(tools);
            var found = File.Exists(configured);
            var useBbLinker = _useBbLinker();
            var info = found ? BbLinker.Probe(configured, BbLinker.InstallEnvironment(install, tools)) : null;
            var carries = info?.HasStockCarry == true;

            JsonObject builds;
            lock (_lock)
            {
                LoadBuilds();
                builds = new JsonObject();
                foreach (var (project, record) in _builds.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
                    builds[project] = record.DeepClone();
            }

            var entry = new JsonObject
            {
                ["id"] = "blackbird",
                ["version"] = Version,
                ["path"] = Environment.ProcessPath ?? "",
                ["use_bb_linker"] = useBbLinker,
                ["bb_linker_path"] = configured,
                ["bb_linker_effective"] = useBbLinker && found && carries,
                ["bb_linker_version"] = info?.Version,
                ["bb_linker_dev_build"] = found && BbLinker.IsOverride(configured),
                ["written_at"] = DateTime.UtcNow.ToString("o"),
                ["last_builds"] = builds,
            };

            var target = ComponentPaths.Entry(install, "blackbird");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = $"{target}.{Environment.ProcessId}.tmp";
            try
            {
                File.WriteAllText(temp, entry.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Nothing reads this to build; a missing note is not worth a message.
        }
    }

    // Builds from earlier runs stay in the file: read them once, before the first write replaces it.
    private void LoadBuilds()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            var path = ComponentPaths.Entry(_installPath(), "blackbird");
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject { } root
                && root["last_builds"] is JsonObject old)
                foreach (var (project, record) in old)
                    if (record is JsonObject recordObject)
                        _builds[project] = (JsonObject)recordObject.DeepClone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // An unreadable old entry is replaced by the next write.
        }
    }
}
