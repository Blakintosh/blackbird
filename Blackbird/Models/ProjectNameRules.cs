using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Blackbird.Models;

/// <summary>
/// The one set of rules for map and mod folder names, shared by New, Rename and Duplicate.
/// </summary>
public static partial class ProjectNameRules
{
    public const int MaxLength = 64;
    public static readonly string[] MapPrefixes = ["zm_", "mp_"];

    /// <summary>The map's prefix folder ("zm", "mp") under map_source and share\raw\maps, as Treyarch's templates lay them out.</summary>
    public static string MapPrefixFolder(string mapName) => mapName.Length >= 2 ? mapName[..2] : mapName;

    /// <summary>&lt;BO3&gt;\map_source\zm\zm_name.map: the file Radiant edits and the compile and light steps read.</summary>
    public static string MapSourcePath(string gamePath, string mapName) =>
        System.IO.Path.Combine(gamePath, "map_source", MapPrefixFolder(mapName), $"{mapName}.map");


    public static readonly string[] BuiltInMaps =
    [
        "mp_aerospace", "mp_apartments", "mp_arena", "mp_banzai", "mp_biodome",
        "mp_chinatown", "mp_city", "mp_conduit", "mp_crucible", "mp_cryogen",
        "mp_ethiopia", "mp_freerun_01", "mp_freerun_02", "mp_freerun_03",
        "mp_freerun_04", "mp_havoc", "mp_infection", "mp_kung_fu", "mp_metro",
        "mp_miniature", "mp_nuketown_x", "mp_redwood", "mp_rise", "mp_rome",
        "mp_ruins", "mp_sector", "mp_shrine", "mp_skyjacked", "mp_spire",
        "mp_stronghold", "mp_veiled", "mp_waterpark", "mp_western",
        "zm_castle", "zm_factory", "zm_genesis", "zm_island", "zm_levelcommon",
        "zm_stalingrad", "zm_zod"
    ];

    /// <summary>"zm_" or "mp_" when the name carries one, otherwise null.</summary>
    public static string? MapPrefixOf(string name) =>
        MapPrefixes.FirstOrDefault(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The prefix a map made from a Mod Tools template must use ("ZM Mod Level" → "zm_").</summary>
    public static string? MapPrefixForTemplate(string templateName) =>
        MapPrefixes.FirstOrDefault(prefix =>
            templateName.StartsWith(prefix[..2], StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns why <paramref name="name"/> can't be used, or null when it can.
    /// </summary>
    /// <param name="requiredMapPrefix">For maps, the prefix the name must keep; null accepts either.</param>
    /// <param name="currentName">The name of the project being renamed, which doesn't clash with itself.</param>
    public static string? Validate(
        string name,
        ProjectType type,
        string? requiredMapPrefix,
        IEnumerable<ProjectItem> existingProjects,
        string? currentName = null)
    {
        if (name.Length == 0)
            return "Enter a name.";

        if (name.Length > MaxLength)
            return $"Use {MaxLength} characters or fewer.";

        if (!NamePattern().IsMatch(name))
            return "Use lowercase letters, numbers and underscores, starting with a letter.";

        if (type == ProjectType.Map)
        {
            var prefix = MapPrefixOf(name);
            if (prefix is null || (requiredMapPrefix is not null && prefix != requiredMapPrefix))
            {
                return requiredMapPrefix is null
                    ? "Map names start with “zm_” or “mp_”."
                    : $"This map's name must start with “{requiredMapPrefix}”.";
            }

            if (name.Length == prefix.Length)
                return $"Add a name after “{prefix}”.";

            if (BuiltInMaps.Contains(name, StringComparer.OrdinalIgnoreCase))
                return "That name belongs to a built-in map.";
        }

        var clash = existingProjects.FirstOrDefault(project =>
            !project.IsGroupHeader
            && !(project.Type == type && string.Equals(project.Name, currentName, StringComparison.OrdinalIgnoreCase))
            && string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
            return $"A {(clash.Type == ProjectType.Map ? "map" : "mod")} named {clash.Name} already exists.";

        return null;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex NamePattern();
}
