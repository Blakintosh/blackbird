using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Blackbird.Models;

namespace Blackbird.Services;

public interface ISettingsService
{
    double WindowWidth { get; set; }
    double WindowHeight { get; set; }
    double WindowX { get; set; }
    double WindowY { get; set; }
    bool WindowMaximized { get; set; }
    string GamePath { get; set; }
    string ToolsPath { get; set; }
    string LastActiveProject { get; set; }
    string LastActiveProjectType { get; set; }
    List<string> RecentProjects { get; set; }
    List<string> RecentCommandActions { get; set; }
    List<string> CollapsedProjectGroups { get; set; }
    List<CachedProjectData> CachedProjects { get; set; }
    Dictionary<string, ProjectSettings> Projects { get; set; }
    List<ToolShortcut> ToolShortcuts { get; set; }
    /// <summary>The theme (<see cref="ThemeChoice"/>): System follows Windows.</summary>
    string Theme { get; set; }
    /// <summary>When the automatic update check last ran; it runs at most once a day.</summary>
    DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Non-null when the last <see cref="Load"/> found an unreadable settings
    /// file. The corrupt file is preserved on disk at the path this message names.</summary>
    string? LoadError { get; }

    string GetBuildLogPath(string projectKey);
    void Save();

    /// <summary>Snapshots the settings on the calling thread and writes them in the background.</summary>
    Task SaveAsync();
    void Load();
}
