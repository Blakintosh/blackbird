using System;
using System.Collections.Generic;

namespace Blackbird.Models;

public class ProjectSettings
{
    // Display metadata. These do not rename folders or project assets.
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public bool IsFavorite { get; set; }
    public string Notes { get; set; } = "";

    // Active environment index (0=Dev, 1=Ship)
    public int LaunchConfigIndex { get; set; }

    // Per-environment dvars: key = config name ("Dev"/"Ship"), value = dvar dict
    public Dictionary<string, Dictionary<string, string>> EnvironmentDvars { get; set; } = new();

    // Per-environment build language: key = config name ("Dev"/"Ship"), value = "All" or language name
    public Dictionary<string, string> EnvironmentBuildLanguages { get; set; } = new();

    // Build step selections
    public bool IsCompileChecked { get; set; }
    public int CompileModeIndex { get; set; } = 1;
    public bool IsLightChecked { get; set; }
    public int LightQualityIndex { get; set; } = 1;
    public bool IsLinkChecked { get; set; }
    public List<BuildPreset> BuildPresets { get; set; } = [];

    // Build mode (0=Build & Run, 1=Build, 2=Build & Run Ignore Errors)
    public int BuildModeIndex { get; set; } = 1;

    // Mod zone selections (list of checked zone names)
    public List<string>? CheckedZones { get; set; }

    // Run options string
    public string RunOptions { get; set; } = "";
    public string QuickLaunchMap { get; set; } = "";
    public bool LaunchOnline { get; set; }

    // Last build metadata
    public string? LastBuildStatus { get; set; }
    public int LastBuildErrorCount { get; set; }
    public int LastBuildWarningCount { get; set; }
    public DateTime? LastBuildTimestamp { get; set; }
    public long LastBuildDurationMs { get; set; }

    // Set when "Prepare for publish" completes successfully; cleared by any later
    // build so the publish dialog can warn that the linked output is stale.
    public DateTime? LastPublishPrepTimestamp { get; set; }
}

public class BuildPreset
{
    /// <summary>What the build bar shows when every step is ticked, or a mix no preset matches; reserved as preset names.</summary>
    public const string AllStepsName = "All steps";
    public const string CustomName = "Custom";

    public string Name { get; set; } = "";
    public bool IsCompileChecked { get; set; }
    public int CompileModeIndex { get; set; } = 1;
    public bool IsLightChecked { get; set; }
    public int LightQualityIndex { get; set; } = 1;
    public bool IsLinkChecked { get; set; }
    public List<string>? CheckedZones { get; set; }

    /// <summary>What the preset runs, as the build bar names it ("Compile · Light (high) · Link").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary
    {
        get
        {
            var steps = new List<string>();
            if (IsCompileChecked) steps.Add(CompileModeIndex == 0 ? "Compile (ents)" : "Compile");
            if (IsLightChecked) steps.Add(LightQualityIndex switch { 0 => "Light (low)", 2 => "Light (high)", _ => "Light" });
            if (IsLinkChecked)
                steps.Add(CheckedZones is { Count: > 0 } zones ? $"Link {zones.Count} zone{(zones.Count == 1 ? "" : "s")}" : "Link");
            if (steps.Count == 0) steps.Add("No steps");

            return string.Join(" · ", steps);

        }
    }
}

public class CachedProjectData
{
    public string Name { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string ZoneFilePath { get; set; } = "";
    public string Type { get; set; } = "";
    public List<string> ZoneFiles { get; set; } = [];
    public long SizeBytes { get; set; }
    public bool IsPublished { get; set; }
}
