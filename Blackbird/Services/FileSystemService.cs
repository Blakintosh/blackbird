using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Blackbird.Models;

namespace Blackbird.Services;

#pragma warning disable CA1416 // BO3 Mod Tools are Windows-only.
public class FileSystemService : IFileSystemService
{
    private static readonly string[] ModZoneFiles = ["core_mod", "mp_mod", "cp_mod", "zm_mod"];
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly ISettingsService _settings;

    public string GamePath => NormalizePath(_settings.GamePath);
    public string ToolsPath => NormalizePath(_settings.ToolsPath);

    public FileSystemService(ISettingsService settings)
    {
        _settings = settings;
        ApplyInitialPaths();
    }

    // Steam's language names, as its appmanifest stores them, to the ones the BO3 linker takes.
    private static readonly Dictionary<string, string> SteamToGameLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "english",
        ["french"] = "french",
        ["italian"] = "italian",
        ["spanish"] = "spanish",
        ["latam"] = "spanish",
        ["german"] = "german",
        ["portuguese"] = "portuguese",
        ["brazilian"] = "portuguese",
        ["russian"] = "russian",
        ["polish"] = "polish",
        ["japanese"] = "japanese",
        ["tchinese"] = "traditionalchinese",
        ["schinese"] = "simplifiedchinese",
        ["arabic"] = "englisharabic",
    };

    /// <summary>
    /// The language Steam runs Black Ops III in, from the game's appmanifest beside its
    /// steamapps\common folder. English when the manifest can't be read.
    /// </summary>
    public string DetectGameLanguage()
    {
        try
        {
            var steamApps = Path.GetDirectoryName(Path.GetDirectoryName(GamePath));
            var manifest = steamApps is null ? null : Path.Combine(steamApps, "appmanifest_311210.acf");
            if (manifest is not null && File.Exists(manifest))
            {
                var match = Regex.Match(
                    File.ReadAllText(manifest),
                    "\"UserConfig\"\\s*\\{[^}]*?\"language\"\\s*\"([^\"]+)\"",
                    RegexOptions.IgnoreCase);
                if (match.Success && SteamToGameLanguage.TryGetValue(match.Groups[1].Value, out var language))
                    return language;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable manifest falls back to English, like a missing one.
        }

        return "english";
    }

    public List<MapItem> ScanMaps()
    {
        var maps = new List<MapItem>();
        var userMapsFolder = Path.Combine(GamePath, "usermaps");

        if (!Directory.Exists(userMapsFolder))
            return maps;

        foreach (var dir in Directory.GetDirectories(userMapsFolder))
        {
            var mapName = Path.GetFileName(dir);
            var zoneFile = Path.Combine(dir, "zone_source", $"{mapName}.zone");

            if (File.Exists(zoneFile))
                maps.Add(new MapItem(mapName, dir, zoneFile));
        }

        return maps;
    }

    public List<ModItem> ScanMods()
    {
        var mods = new List<ModItem>();
        var modsFolder = Path.Combine(GamePath, "mods");

        if (!Directory.Exists(modsFolder))
            return mods;

        foreach (var dir in Directory.GetDirectories(modsFolder))
        {
            var modName = Path.GetFileName(dir);
            ModItem? modItem = null;

            foreach (var zoneName in ModZoneFiles)
            {
                var zoneFile = Path.Combine(dir, "zone_source", $"{zoneName}.zone");
                if (File.Exists(zoneFile))
                {
                    modItem ??= new ModItem(modName, dir);
                    modItem.ZoneFiles.Add(new ModZoneFile(zoneName, modName));
                }
            }

            if (modItem is not null)
                mods.Add(modItem);
        }

        return mods;
    }

    public List<string> ScanMapTemplates()
    {
        var templatesFolder = Path.Combine(ToolsPath, "rex", "templates");

        if (!Directory.Exists(templatesFolder))
            return [];

        // rex\templates also holds the "Mod" template; only folders with a
        // usermaps\template tree are map templates.
        var templates = new List<string>();
        foreach (var dir in Directory.GetDirectories(templatesFolder))
        {
            if (Directory.Exists(Path.Combine(dir, "usermaps", "template")))
                templates.Add(Path.GetFileName(dir));
        }

        templates.Sort();
        return templates;
    }

    public SetupDetectionResult DetectPaths()
    {
        var result = new SetupDetectionResult();

        if (IsGamePath(_settings.GamePath))
        {
            result.GamePath = NormalizePath(_settings.GamePath);
            result.Source = "Existing setting";
        }

        foreach (var candidate in GetGamePathCandidates())
        {
            if (!string.IsNullOrWhiteSpace(result.GamePath)) break;
            if (!IsGamePath(candidate)) continue;

            result.GamePath = NormalizePath(candidate);
            result.Source = "Detected from Steam or environment";
            break;
        }

        if (IsToolsPath(_settings.ToolsPath))
        {
            result.ToolsPath = NormalizePath(_settings.ToolsPath);
            if (string.IsNullOrEmpty(result.Source))
                result.Source = "Existing setting";
        }

        var gamePathForTools = !string.IsNullOrWhiteSpace(result.GamePath)
            ? result.GamePath
            : _settings.GamePath;

        foreach (var candidate in GetToolsPathCandidates(gamePathForTools))
        {
            if (!string.IsNullOrWhiteSpace(result.ToolsPath)) break;
            if (!IsToolsPath(candidate)) continue;

            result.ToolsPath = NormalizePath(candidate);
            if (string.IsNullOrEmpty(result.Source))
                result.Source = "Detected from Steam or environment";
            break;
        }

        return result;
    }

    public SetupValidationResult ValidateSetup(string? gamePath = null, string? toolsPath = null)
    {
        var game = NormalizePath(gamePath ?? GamePath);
        var tools = NormalizePath(toolsPath ?? ToolsPath);

        var result = new SetupValidationResult
        {
            GamePath = game,
            ToolsPath = tools,
            Checks =
            [
                CheckDirectory("Game folder", game),
                CheckFile("BlackOps3.exe", Path.Combine(game, "BlackOps3.exe")),
                CheckDirectory("usermaps folder", Path.Combine(game, "usermaps"), isRequired: false),
                CheckDirectory("mods folder", Path.Combine(game, "mods"), isRequired: false),
                CheckDirectory("Tools folder", tools),
                CheckFile("Asset Editor", Path.Combine(tools, "bin", "AssetEditor_modtools.exe")),
                CheckFile("Radiant", Path.Combine(tools, "bin", "radiant_modtools.exe")),
                CheckFile("Compiler", Path.Combine(tools, "bin", "cod2map64.exe")),
                CheckFile("Linker", Path.Combine(tools, "bin", "linker_modtools.exe")),
                CheckFile("GDT database updater", Path.Combine(tools, "gdtdb", "gdtdb.exe")),
                CheckDirectory("Map templates", Path.Combine(tools, "rex", "templates")),
                CheckFile("Export2Rust", Path.Combine(tools, "bin", "export2rust.exe"), isRequired: false),
            ]
        };

        return result;
    }

    public async System.Threading.Tasks.Task SetPathsAsync(string gamePath, string toolsPath)
    {
        var (previousGame, previousTools) = (_settings.GamePath, _settings.ToolsPath);
        _settings.GamePath = NormalizePath(gamePath);
        _settings.ToolsPath = NormalizePath(toolsPath);
        try
        {
            await _settings.SaveAsync();
        }
        catch
        {
            // Not saved, so not in effect either: the dialog that asked stays open to retry.
            (_settings.GamePath, _settings.ToolsPath) = (previousGame, previousTools);
            throw;
        }
    }

    // Reading never writes: a file that won't parse stays exactly as it is, and the publish
    // checks block on it (see WorkshopFiles.FindCorruptFile).
    public WorkshopItemData? ReadWorkshopJson(string zoneFolderPath)
    {
        var filePath = Path.Combine(zoneFolderPath, WorkshopFiles.WorkshopJsonName);
        if (!File.Exists(filePath))
            return null;

        try
        {
            return JsonSerializer.Deserialize<WorkshopItemData>(File.ReadAllText(filePath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void WriteWorkshopJson(string zoneFolderPath, WorkshopItemData data)
    {
        // Either file corrupt leaves both alone: one may hold the only copy of the Workshop ID.
        if (WorkshopFiles.FindCorruptFile(zoneFolderPath) is { } corrupt)
            throw new WorkshopFileCorruptException(corrupt);

        var filePath = Path.Combine(zoneFolderPath, WorkshopFiles.WorkshopJsonName);
        Directory.CreateDirectory(zoneFolderPath);
        WorkshopFiles.RemapMediaPaths(data, zoneFolderPath, new Dictionary<string, string>());
        JsonSettingsService.WriteAtomic(filePath, JsonSerializer.Serialize(data, SerializerOptions));
    }

    public WorkshopProfilesData ReadWorkshopProfiles(string zoneFolderPath, WorkshopItemData? fallbackData = null)
    {
        if (WorkshopFiles.ExistingProfilesPath(zoneFolderPath) is { } filePath)
        {
            try
            {
                var data = JsonSerializer.Deserialize<WorkshopProfilesData>(File.ReadAllText(filePath));
                if (data is not null)
                {
                    data.Normalize(fallbackData);
                    return data;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A single-version view of workshop.json; the file itself is left alone.
            }
        }

        var fromWorkshopJson = WorkshopProfilesData.FromWorkshopJson(fallbackData);
        fromWorkshopJson.Normalize(fallbackData);
        return fromWorkshopJson;
    }

    /// <summary>
    /// Writes the versions file to the project folder. An older build's copy in zone, and its
    /// zone\workshop_media, move out here (with every path in <paramref name="data"/> following).
    /// </summary>
    public void WriteWorkshopProfiles(string zoneFolderPath, WorkshopProfilesData data)
    {
        if (WorkshopFiles.FindCorruptFile(zoneFolderPath) is { } corrupt)
            throw new WorkshopFileCorruptException(corrupt);

        data.Normalize();
        var moved = WorkshopFiles.MigrateLegacyMedia(zoneFolderPath);
        foreach (var profile in data.Profiles)
            WorkshopFiles.RemapMediaPaths(profile.WorkshopJson, zoneFolderPath, moved);

        var filePath = WorkshopFiles.ProfilesPath(zoneFolderPath);
        var hadProjectCopy = File.Exists(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        JsonSettingsService.WriteAtomic(filePath, JsonSerializer.Serialize(data, SerializerOptions));

        // zone's copy was what this save migrated, unless the project folder already had one:
        // then zone's is a separate older copy, kept beside it rather than lost.
        var legacy = WorkshopFiles.LegacyProfilesPath(zoneFolderPath);
        if (File.Exists(legacy))
        {
            if (hadProjectCopy)
                File.Move(legacy, WorkshopFiles.UniqueFilePath(Path.GetDirectoryName(filePath)!, WorkshopFiles.ProfilesName));
            else
                File.Delete(legacy);
        }
    }

    private void ApplyInitialPaths()
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(_settings.GamePath))
        {
            var gameEnv = Environment.GetEnvironmentVariable("TA_GAME_PATH");
            if (IsGamePath(gameEnv))
            {
                _settings.GamePath = NormalizePath(gameEnv);
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(_settings.ToolsPath))
        {
            var toolsEnv = Environment.GetEnvironmentVariable("TA_TOOLS_PATH");
            if (IsToolsPath(toolsEnv))
            {
                _settings.ToolsPath = NormalizePath(toolsEnv);
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(_settings.GamePath) || string.IsNullOrWhiteSpace(_settings.ToolsPath))
        {
            var detected = DetectPaths();
            if (string.IsNullOrWhiteSpace(_settings.GamePath) && !string.IsNullOrWhiteSpace(detected.GamePath))
            {
                _settings.GamePath = detected.GamePath;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_settings.ToolsPath) && !string.IsNullOrWhiteSpace(detected.ToolsPath))
            {
                _settings.ToolsPath = detected.ToolsPath;
                changed = true;
            }
        }

        if (changed)
            _settings.Save();
    }

    private static SetupCheck CheckDirectory(string name, string path, bool isRequired = true)
    {
        var passed = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        return new SetupCheck
        {
            Name = name,
            Detail = string.IsNullOrWhiteSpace(path) ? "No path configured" : path,
            Passed = passed,
            IsRequired = isRequired
        };
    }

    private static SetupCheck CheckFile(string name, string path, bool isRequired = true)
    {
        var passed = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        return new SetupCheck
        {
            Name = name,
            Detail = string.IsNullOrWhiteSpace(path) ? "No path configured" : path,
            Passed = passed,
            IsRequired = isRequired
        };
    }

    private static bool IsGamePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && File.Exists(Path.Combine(NormalizePath(path), "BlackOps3.exe"));

    private static bool IsToolsPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && File.Exists(Path.Combine(NormalizePath(path), "bin", "linker_modtools.exe"))
        && File.Exists(Path.Combine(NormalizePath(path), "bin", "radiant_modtools.exe"));

    // Candidates are produced lazily so detection stops at the first hit; the exe's own
    // install comes before any Steam registry or library lookups.
    private static IEnumerable<string> GetGamePathCandidates()
    {
        return Distinct(Candidates());

        static IEnumerable<string?> Candidates()
        {
            yield return Environment.GetEnvironmentVariable("TA_GAME_PATH");
            yield return GetInstallRootFromExe();

            foreach (var library in GetSteamLibraryPaths())
            {
                yield return Path.Combine(library, "steamapps", "common", "Call of Duty Black Ops III");
                yield return GetInstallDirFromManifest(library, "311210");
            }
        }
    }

    private static IEnumerable<string> GetToolsPathCandidates(string? gamePath)
    {
        return Distinct(Candidates());

        IEnumerable<string?> Candidates()
        {
            yield return Environment.GetEnvironmentVariable("TA_TOOLS_PATH");
            yield return gamePath;
            yield return GetInstallRootFromExe();

            foreach (var library in GetSteamLibraryPaths())
            {
                yield return Path.Combine(library, "steamapps", "common", "Call of Duty Black Ops III");
                yield return GetInstallDirFromManifest(library, "455130");
                yield return GetInstallDirFromManifest(library, "311210");
            }
        }
    }

    private static IEnumerable<string> Distinct(IEnumerable<string?> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var normalized = NormalizePath(candidate);
            if (seen.Add(normalized))
                yield return normalized;
        }
    }

    /// <summary>The BO3 folder when Blackbird runs from &lt;BO3&gt;\bin in place of the official launcher.</summary>
    private static string? GetInstallRootFromExe()
    {
        var exeFolder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        return string.Equals(Path.GetFileName(exeFolder), "bin", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(exeFolder)
            : null;
    }

    private static IEnumerable<string> GetSteamLibraryPaths()
    {
        var roots = new List<string>();
        AddIfDirectory(roots, GetSteamPathFromRegistry());
        AddIfDirectory(roots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));

        for (int i = 0; i < roots.Count; i++)
        {
            var libraryFile = Path.Combine(roots[i], "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile)) continue;

            foreach (var path in ParseSteamLibraryFile(libraryFile))
                AddIfDirectory(roots, path);
        }

        return roots;
    }

    private static string? GetSteamPathFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string
                ?? key?.GetValue("InstallPath") as string;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ParseSteamLibraryFile(string filePath)
    {
        string text;
        try { text = File.ReadAllText(filePath); }
        catch { yield break; }

        foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"(?<path>[^\"]+)\""))
            yield return match.Groups["path"].Value.Replace(@"\\", @"\");

        foreach (Match match in Regex.Matches(text, "\"\\d+\"\\s+\"(?<path>[A-Za-z]:[^\"]+)\""))
            yield return match.Groups["path"].Value.Replace(@"\\", @"\");
    }

    private static string? GetInstallDirFromManifest(string libraryRoot, string appId)
    {
        var manifest = Path.Combine(libraryRoot, "steamapps", $"appmanifest_{appId}.acf");
        if (!File.Exists(manifest)) return null;

        try
        {
            var text = File.ReadAllText(manifest);
            var installDir = Regex.Match(text, "\"installdir\"\\s+\"(?<dir>[^\"]+)\"");
            if (!installDir.Success) return null;

            return Path.Combine(libraryRoot, "steamapps", "common", installDir.Groups["dir"].Value);
        }
        catch
        {
            return null;
        }
    }

    private static void AddIfDirectory(List<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var normalized = NormalizePath(path);
        if (Directory.Exists(normalized)
            && !paths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(normalized);
        }
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        var expanded = Environment.ExpandEnvironmentVariables(path)
            .Trim()
            .Trim('"')
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        // "D:\" stays a root; trimmed to "D:" it would mean "the current folder on D:".
        var trimmed = expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }

}
#pragma warning restore CA1416
