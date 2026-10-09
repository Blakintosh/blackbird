using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Blackbird.Models;

namespace Blackbird.Services;

/// <summary>
/// Where the Workshop files live. workshop.json is Treyarch's and stays in zone, which Steam
/// uploads whole; Blackbird's own versions file and media sit beside zone in the project folder
/// so they never ship to subscribers. Older builds kept them in zone: that copy is read as a
/// fallback and moved out on the next save the user makes.
/// </summary>
public static class WorkshopFiles
{
    public const string WorkshopJsonName = "workshop.json";
    public const string ProfilesName = "workshop.profiles.json";
    public const string MediaFolderName = "workshop_media";

    /// <summary>What Steam takes for a thumbnail or gallery image.</summary>
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif"];

    public static string ProjectFolderOf(string zoneFolder) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(zoneFolder)) ?? zoneFolder;

    public static string ProfilesPath(string zoneFolder) => Path.Combine(ProjectFolderOf(zoneFolder), ProfilesName);
    public static string MediaFolder(string zoneFolder) => Path.Combine(ProjectFolderOf(zoneFolder), MediaFolderName);
    public static string LegacyProfilesPath(string zoneFolder) => Path.Combine(zoneFolder, ProfilesName);
    public static string LegacyMediaFolder(string zoneFolder) => Path.Combine(zoneFolder, MediaFolderName);

    /// <summary>The versions file to read: the project folder's, else the one an older build left in zone.</summary>
    public static string? ExistingProfilesPath(string zoneFolder) =>
        File.Exists(ProfilesPath(zoneFolder)) ? ProfilesPath(zoneFolder)
        : File.Exists(LegacyProfilesPath(zoneFolder)) ? LegacyProfilesPath(zoneFolder)
        : null;

    /// <summary>The media folder that exists (the project folder's, else zone's legacy one), or null.</summary>
    public static string? ExistingMediaFolder(string zoneFolder) =>
        Directory.Exists(MediaFolder(zoneFolder)) ? MediaFolder(zoneFolder)
        : Directory.Exists(LegacyMediaFolder(zoneFolder)) ? LegacyMediaFolder(zoneFolder)
        : null;

    /// <summary>
    /// True for Blackbird's own files in zone (path relative to zone): the versions file, media,
    /// and a temp or set-aside copy of either JSON file. None of them may be uploaded.
    /// </summary>
    public static bool IsBlackbirdFile(string relativePath)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length > 1)
            return parts[0].Equals(MediaFolderName, StringComparison.OrdinalIgnoreCase);

        var name = parts[0];
        return name.Equals(ProfilesName, StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("workshop.", StringComparison.OrdinalIgnoreCase)
                && (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || name.Contains(".corrupt-", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>workshop.json or the versions file when it exists but isn't valid JSON; null otherwise.</summary>
    public static string? FindCorruptFile(string zoneFolder)
    {
        var workshopJson = Path.Combine(zoneFolder, WorkshopJsonName);
        if (IsCorrupt<WorkshopItemData>(workshopJson))
            return workshopJson;

        var profiles = ExistingProfilesPath(zoneFolder);
        return profiles is not null && IsCorrupt<WorkshopProfilesData>(profiles) ? profiles : null;
    }

    /// <summary>True when the file is there but can't be parsed. Missing, locked or unreadable isn't corrupt.</summary>
    internal static bool IsCorrupt<T>(string path)
    {
        try
        {
            JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            return false;
        }
        catch (JsonException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Moves an older build's zone\workshop_media into the project folder's, never overwriting:
    /// a clashing name gets a number. Returns old path → new path for every file moved. A file
    /// that won't move (open elsewhere) stays and is tried again on the next save.
    /// </summary>
    public static Dictionary<string, string> MigrateLegacyMedia(string zoneFolder)
    {
        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var legacy = LegacyMediaFolder(zoneFolder);
        if (!Directory.Exists(legacy))
            return moved;

        var target = MediaFolder(zoneFolder);
        foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories).ToList())
        {
            try
            {
                var destination = Path.Combine(target, Path.GetRelativePath(legacy, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                destination = UniqueFilePath(Path.GetDirectoryName(destination)!, Path.GetFileName(destination));
                File.Move(file, destination);
                moved[Path.GetFullPath(file)] = destination;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var folder in Directory.EnumerateDirectories(legacy, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length)
                     .Append(legacy))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return moved;
    }

    /// <summary>
    /// Points a thumbnail or gallery path that was in zone\workshop_media at its moved copy:
    /// from <paramref name="moved"/>, else (moved by an earlier save) the same name in the new folder.
    /// </summary>
    public static string RemapMediaPath(string path, string zoneFolder, IReadOnlyDictionary<string, string> moved)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }

        if (moved.TryGetValue(full, out var to))
            return to;

        var legacy = Path.GetFullPath(LegacyMediaFolder(zoneFolder)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(legacy, StringComparison.OrdinalIgnoreCase) || File.Exists(full))
            return path;

        var candidate = Path.Combine(MediaFolder(zoneFolder), full[legacy.Length..]);
        return File.Exists(candidate) ? candidate : path;
    }

    /// <summary>Rewrites the thumbnail and gallery paths; true when any changed.</summary>
    public static bool RemapMediaPaths(WorkshopItemData data, string zoneFolder, IReadOnlyDictionary<string, string> moved)
    {
        var thumbnail = RemapMediaPath(data.Thumbnail, zoneFolder, moved);
        var gallery = data.PreviewImages.Select(path => RemapMediaPath(path, zoneFolder, moved)).ToList();
        if (thumbnail == data.Thumbnail && gallery.SequenceEqual(data.PreviewImages))
            return false;

        data.Thumbnail = thumbnail;
        data.SetPreviewImageList(gallery);
        return true;
    }

    /// <summary>
    /// Clears zone of Blackbird's files before Steam uploads it: an older build's versions file
    /// and media move to the project folder, a left-over temp file is deleted, and a set-aside
    /// corrupt copy moves to the project folder. Throws when anything is still there. Returns old
    /// path → new path for media it moved, for the draft's paths to follow.
    /// </summary>
    public static Dictionary<string, string> PrepareZoneForUpload(string zoneFolder)
    {
        var project = ProjectFolderOf(zoneFolder);
        var moved = MigrateLegacyMedia(zoneFolder);

        foreach (var file in Directory.EnumerateFiles(zoneFolder).ToList())
        {
            var name = Path.GetFileName(file);
            if (!IsBlackbirdFile(name))
                continue;

            try
            {
                // A temp file is only ever a failed write. Anything else, an older build's versions file
                // included, moves beside the live one rather than being lost.
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
                else
                    File.Move(file, UniqueFilePath(project, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        var left = Directory.EnumerateFileSystemEntries(zoneFolder)
            .Select(Path.GetFileName)
            .FirstOrDefault(name => name is not null
                && (IsBlackbirdFile(name) || name.Equals(MediaFolderName, StringComparison.OrdinalIgnoreCase)));
        if (left is not null)
            throw new WorkshopZoneException($"{left} is still in the zone folder, so nothing was uploaded (it would ship to subscribers). Close anything using it, then try again.");

        return moved;
    }

    /// <summary><paramref name="fileName"/> in <paramref name="folder"/>, numbered "name (2).ext" when taken.</summary>
    public static string UniqueFilePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
        return path;
    }
}

/// <summary>A Workshop file that isn't valid JSON. Blackbird won't overwrite it: it may hold the only copy of the Workshop ID.</summary>
public sealed class WorkshopFileCorruptException(string path)
    : IOException($"{Path.GetFileName(path)} isn't valid JSON, so Blackbird won't overwrite it. Fix or delete it, then try again.")
{
    public string FilePath { get; } = path;
}

/// <summary>One of Blackbird's own files couldn't be moved out of zone before an upload.</summary>
public sealed class WorkshopZoneException(string message) : IOException(message);
