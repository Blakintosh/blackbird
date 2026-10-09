using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blackbird.Models;

namespace Blackbird.Services;

public class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _settingsPath = AppPaths.SettingsFile;
    private readonly object _writeLock = new();
    private long _latestSnapshot;
    private long _writtenSnapshot;

    // Set when a corrupt settings file couldn't be moved aside: saving would overwrite the
    // only copy of the user's data, so nothing is written this session.
    private bool _writesBlocked;

    public string? LoadError { get; private set; }

    public double WindowWidth { get; set; } = 1024;
    public double WindowHeight { get; set; } = 640;
    public double WindowX { get; set; } = 200;
    public double WindowY { get; set; } = 200;
    public bool WindowMaximized { get; set; }
    public string GamePath { get; set; } = "";
    public string ToolsPath { get; set; } = "";
    public string LastActiveProject { get; set; } = "";
    public string LastActiveProjectType { get; set; } = "";
    public List<string> RecentProjects { get; set; } = [];
    public List<string> RecentCommandActions { get; set; } = [];
    public List<string> CollapsedProjectGroups { get; set; } = [];
    public List<CachedProjectData> CachedProjects { get; set; } = [];
    public Dictionary<string, ProjectSettings> Projects { get; set; } = new();
    public List<ToolShortcut> ToolShortcuts { get; set; } = [];
    public string Theme { get; set; } = nameof(ThemeChoice.System);
    public DateTime? LastUpdateCheckUtc { get; set; }

    public JsonSettingsService()
    {
        Directory.CreateDirectory(AppPaths.LogsFolder);
    }

    public string GetBuildLogPath(string projectKey)
    {
        return Path.Combine(AppPaths.LogsFolder, $"{projectKey.Replace(':', '_')}_last.log");
    }

    public void Load()
    {
        if (!File.Exists(_settingsPath))
            return;

        string json;
        try
        {
            json = ReadWithRetry(_settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked (antivirus, a sync client), not corrupt: leave it alone and don't overwrite it.
            _writesBlocked = true;
            LoadError = "Another program has Blackbird's settings file open, so defaults are in use this session "
                        + $"and nothing will be saved over it. Restart Blackbird once it's free:\n{_settingsPath}";
            return;
        }

        if (TryParse(json) is { } data)
        {
            Apply(data);
            return;
        }

        // Corrupt. Keep it for recovery, and fall back to the copy the last good save left behind.
        var corruptPath = $"{_settingsPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(_settingsPath, corruptPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writesBlocked = true;
            LoadError = "Blackbird's settings file is damaged, so defaults are in use this session and nothing "
                        + $"will be saved over it:\n{_settingsPath}";
            return;
        }

        var backupPath = _settingsPath + ".bak";
        if (File.Exists(backupPath) && TryParse(TryRead(backupPath)) is { } backup)
        {
            Apply(backup);
            LoadError = "Blackbird's settings file was damaged, so it restored the backup from "
                        + $"{File.GetLastWriteTime(backupPath):d MMM yyyy, HH:mm}. The damaged file was kept:\n{corruptPath}";
            return;
        }

        LoadError = $"Blackbird's settings file was damaged, so defaults are in use. The damaged file was kept:\n{corruptPath}";
    }

    private static string ReadWithRetry(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            Thread.Sleep(150);
            return File.ReadAllText(path);
        }
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static SettingsData? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SettingsData>(json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private void Apply(SettingsData data)
    {
        WindowWidth = data.WindowWidth;
        WindowHeight = data.WindowHeight;
        WindowX = data.WindowX;
        WindowY = data.WindowY;
        WindowMaximized = data.WindowMaximized;
        GamePath = data.GamePath ?? "";
        ToolsPath = data.ToolsPath ?? "";
        LastActiveProject = data.LastActiveProject ?? "";
        LastActiveProjectType = data.LastActiveProjectType ?? "";
        RecentProjects = data.RecentProjects ?? [];
        RecentCommandActions = data.RecentCommandActions ?? [];
        CollapsedProjectGroups = data.CollapsedProjectGroups ?? [];
        CachedProjects = data.CachedProjects ?? [];
        Projects = data.Projects ?? new();
        ToolShortcuts = data.ToolShortcuts ?? [];
        Theme = data.Theme ?? nameof(ThemeChoice.System);
        LastUpdateCheckUtc = data.LastUpdateCheckUtc;
    }


    public void Save() => Write(Snapshot());

    public Task SaveAsync()
    {
        var snapshot = Snapshot();
        return Task.Run(() => Write(snapshot));
    }

    /// <summary>Serializes on the calling thread, which owns the settings objects.</summary>
    private (long Sequence, string Json) Snapshot()
    {
        var data = new SettingsData
        {
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
            WindowX = WindowX,
            WindowY = WindowY,
            WindowMaximized = WindowMaximized,
            GamePath = GamePath,
            ToolsPath = ToolsPath,
            LastActiveProject = LastActiveProject,
            LastActiveProjectType = LastActiveProjectType,
            RecentProjects = RecentProjects,
            RecentCommandActions = RecentCommandActions,
            CollapsedProjectGroups = CollapsedProjectGroups,
            CachedProjects = CachedProjects,
            Projects = Projects,
            ToolShortcuts = ToolShortcuts,
            Theme = Theme,
            LastUpdateCheckUtc = LastUpdateCheckUtc,
        };

        return (Interlocked.Increment(ref _latestSnapshot), JsonSerializer.Serialize(data, SerializerOptions));
    }

    /// <summary>Writes a snapshot unless a newer one has already reached the disk.</summary>
    private void Write((long Sequence, string Json) snapshot)
    {
        lock (_writeLock)
        {
            if (_writesBlocked || snapshot.Sequence <= _writtenSnapshot)
                return;

            WriteAtomic(_settingsPath, snapshot.Json, backupPath: _settingsPath + ".bak");
            _writtenSnapshot = snapshot.Sequence;
        }
    }

    /// <summary>
    /// Writes via a temp file flushed to disk, then swaps it in, so neither a crash nor a power
    /// cut mid-write can leave a truncated file behind. With <paramref name="backupPath"/>, the
    /// previous file is kept there. The temp name is unique per write, and a failed write
    /// removes it.
    /// </summary>
    internal static void WriteAtomic(string path, string contents, string? backupPath = null)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));
                stream.Flush(flushToDisk: true);
            }

            if (backupPath is not null && File.Exists(path))
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private class SettingsData
    {
        public double WindowWidth { get; set; } = 1024;
        public double WindowHeight { get; set; } = 640;
        public double WindowX { get; set; } = 200;
        public double WindowY { get; set; } = 200;
        public bool WindowMaximized { get; set; }
        public string? GamePath { get; set; } = "";
        public string? ToolsPath { get; set; } = "";
        public string? LastActiveProject { get; set; } = "";
        public string? LastActiveProjectType { get; set; } = "";
        public List<string>? RecentProjects { get; set; } = [];
        public List<string>? RecentCommandActions { get; set; } = [];
        public List<string>? CollapsedProjectGroups { get; set; } = [];
        public List<CachedProjectData>? CachedProjects { get; set; } = [];
        public Dictionary<string, ProjectSettings>? Projects { get; set; } = new();
        public List<ToolShortcut>? ToolShortcuts { get; set; } = [];
        public string? Theme { get; set; } = nameof(ThemeChoice.System);
        public DateTime? LastUpdateCheckUtc { get; set; }
    }
}
