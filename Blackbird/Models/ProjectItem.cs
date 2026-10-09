using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Blackbird.Models;

public enum ProjectType { Map, Mod }

public class ProjectItem
{
    public string Name { get; }
    public string DisplayName { get; set; }
    public string Category { get; set; }
    public bool IsFavorite { get; set; }
    public string Notes { get; set; } = "";
    public bool IsPublished { get; set; }
    public long SizeBytes { get; set; }
    public string FolderPath { get; }
    public ProjectType Type { get; }

    // Maps only
    public string? ZoneFilePath { get; }

    // Mods only
    public ObservableCollection<ModZoneFile> ZoneFiles { get; } = [];

    // Group header sentinel (not selectable)
    public bool IsGroupHeader { get; }
    public string? GroupName { get; }
    public int GroupProjectCount { get; }
    public bool IsGroupCollapsed { get; }
    public string GroupCollapseIcon => IsGroupCollapsed ? "\uE70D" : "\uE70E";
    public string DefaultCategory => Type == ProjectType.Map ? "Maps" : "Mods";

    /// <summary>Identifies the project in settings, recents and log names; a map and a mod may share a name.</summary>
    public string Key => KeyFor(Type, Name);
    public bool HasCustomDisplayName =>
        !IsGroupHeader && !string.Equals(DisplayName, Name, System.StringComparison.Ordinal);
    public bool HasNotes => !IsGroupHeader && !string.IsNullOrWhiteSpace(Notes);
    public string TypeShortLabel => Type == ProjectType.Map ? "MAP" : "MOD";
    public string PublishedLabel => IsPublished ? "Published" : "Unpublished";
    public string SizeLabel => IsGroupHeader || SizeBytes <= 0 ? "" : FormatSize(SizeBytes);
    /// <summary>The project picker's second line: the folder name when it differs from the display name, then whether it's on the Workshop.</summary>
    public string PickerDetail
    {
        get
        {
            if (IsGroupHeader)
                return "";
            var published = IsPublished ? "Published" : "Not published";
            return HasCustomDisplayName ? $"{Name} · {published}" : published;
        }
    }

    private ProjectItem(string groupName, int projectCount, bool isCollapsed)
    {
        IsGroupHeader = true;
        GroupName = groupName;
        GroupProjectCount = projectCount;
        IsGroupCollapsed = isCollapsed;
        Name = groupName;
        DisplayName = groupName;
        Category = groupName;
        FolderPath = "";
    }

    public ProjectItem(MapItem map)
    {
        Name = map.Name;
        DisplayName = map.Name;
        Category = "Maps";
        FolderPath = map.FolderPath;
        ZoneFilePath = map.ZoneFilePath;
        Type = ProjectType.Map;
    }

    public ProjectItem(ModItem mod)
    {
        Name = mod.Name;
        DisplayName = mod.Name;
        Category = "Mods";
        FolderPath = mod.FolderPath;
        Type = ProjectType.Mod;
        foreach (var zone in mod.ZoneFiles)
            ZoneFiles.Add(zone);
    }

    public static string KeyFor(ProjectType type, string name) =>
        $"{(type == ProjectType.Map ? "map" : "mod")}:{name}";

    public static ProjectItem CreateHeader(string groupName, int projectCount, bool isCollapsed) =>
        new(groupName, projectCount, isCollapsed);

    public override string ToString() => IsGroupHeader ? GroupName! : DisplayName;

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var suffix = 0;

        while (size >= 1024 && suffix < suffixes.Length - 1)
        {
            size /= 1024;
            suffix++;
        }

        return suffix == 0
            ? $"{bytes} {suffixes[suffix]}"
            : $"{size:0.#} {suffixes[suffix]}";
    }
}
