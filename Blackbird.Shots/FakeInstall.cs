using System.Text.Json;
using System.Text.Json.Nodes;
using Blackbird.Models;

namespace Blackbird.Shots;

/// <summary>
/// A throwaway Black Ops III install and Blackbird data folder under %TEMP%: a few maps
/// and mods, empty stand-ins for the Mod Tools executables, Workshop files, a saved build
/// log and settings. Nothing here touches the real install or %APPDATA%\Blackbird.
/// </summary>
internal static class FakeInstall
{
    public static string Root { get; private set; } = "";
    public static string GamePath { get; private set; } = "";
    public static string DataPath { get; private set; } = "";

    /// <summary>A second install with the game and Mod Tools but no projects.</summary>
    public static string EmptyGamePath { get; private set; } = "";

    /// <summary>The game without the Mod Tools.</summary>
    public static string GameOnlyPath { get; private set; } = "";

    public static string CastleFolder => Path.Combine(GamePath, "usermaps", "zm_castle_redux");
    public static string CastleWorkshopFolder => Path.Combine(CastleFolder, "zone");
    public static string ThumbnailPath => Path.Combine(CastleWorkshopFolder, "workshop_media", "thumbnail.png");

    /// <summary>A .map in map_source with no map folder of its own: renaming or duplicating onto its name must never overwrite it.</summary>
    public const string StrayMapSourceName = "zm_castle_old";
    public static string StrayMapSourcePath => Path.Combine(GamePath, "map_source", "zm", StrayMapSourceName + ".map");
    public const string StrayMapSourceText = "iwmap 4\n// another map's source\n";

    public static readonly string[] BuildLogHead =
    [
        "^7Blackbird build · zm_castle_redux · Dev",
        "^5> cod2map64.exe -platform pc -loadFrom map_source\\zm\\zm_castle_redux.map zm_castle_redux",
        "^7Loading map source… 18,412 brushes, 2,106 entities",
        "^6Compiling BSP (full)",
    ];

    public static readonly string[] BuildLogTail =
    [
        "^7BSP compiled in 41.2 s",
        "^5> linker_modtools.exe -language english -modsource zm_castle_redux",
        "^7Linking zone zm_castle_redux",
        "^3WARNING: image 'i_castle_banner_c' is 4096x4096 and not streamed; consider enabling streaming",
        "^3WARNING: sound alias 'zmb_castle_bell_toll' has no subtitle",
        "^1ERROR: xmodel 'p7_zm_der_banner_torn' is referenced by zone_source\\zm_castle_redux.zone but wasn't found in any GDT",
        "^3WARNING: weapon 'ray_gun_castle' uses a deprecated fx 'wpn_ray_gun_trail'",
        "^1ERROR: script scripts\\zm\\zm_castle_redux.gsc(212): unknown function 'zm_castle_redux::init_bells'",
        "^7Wrote zone\\zm_castle_redux.ff (212.4 MB)",
        "^2Link finished with 2 errors and 3 warnings",
    ];

    /// <summary>The same link after the errors are fixed: it succeeds with warnings.</summary>
    public static readonly string[] BuildLogTailWarningsOnly =
    [
        "^7BSP compiled in 40.8 s",
        "^5> linker_modtools.exe -language english -modsource zm_castle_redux",
        "^7Linking zone zm_castle_redux",
        "^3WARNING: image 'i_castle_banner_c' is 4096x4096 and not streamed; consider enabling streaming",
        "^3WARNING: sound alias 'zmb_castle_bell_toll' has no subtitle",
        "^3WARNING: weapon 'ray_gun_castle' uses a deprecated fx 'wpn_ray_gun_trail'",
        "^7Wrote zone\\zm_castle_redux.ff (212.4 MB)",
        "^2Link finished with 3 warnings",
    ];

    public static void Create()
    {
        // One folder per run, so runs side by side (CI, a second terminal) never delete each other's files.
        var runs = Path.Combine(Path.GetTempPath(), "BlackbirdShots");
        foreach (var stale in Directory.Exists(runs) ? Directory.GetDirectories(runs) : [])
        {
            try { Directory.Delete(stale, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a run still using it
        }

        Root = Path.Combine(runs, Environment.ProcessId.ToString());


        GamePath = Path.Combine(Root, "Steam", "steamapps", "common", "Call of Duty Black Ops III");
        EmptyGamePath = Path.Combine(Root, "Fresh install", "Call of Duty Black Ops III");
        GameOnlyPath = Path.Combine(Root, "Game only", "Call of Duty Black Ops III");
        DataPath = Path.Combine(Root, "AppData", "Blackbird");

        CreateGameAndTools(GamePath);
        CreateGameAndTools(EmptyGamePath);
        Touch(Path.Combine(GameOnlyPath, "BlackOps3.exe"));

        Map("zm_castle_redux");
        Map("zm_foundry");
        Map("mp_skyline");
        Mod("wraith_weapons", "core_mod", "zm_mod");
        Mod("perk_overhaul", "zm_mod");
        File.WriteAllText(StrayMapSourcePath, StrayMapSourceText);

        WriteWorkshopFiles();
        WriteSettings();
        WriteSavedLog();
    }

    private static void CreateGameAndTools(string game)
    {
        Touch(Path.Combine(game, "BlackOps3.exe"));
        Directory.CreateDirectory(Path.Combine(game, "usermaps"));
        Directory.CreateDirectory(Path.Combine(game, "mods"));
        foreach (var exe in new[] { "AssetEditor_modtools.exe", "radiant_modtools.exe", "cod2map64.exe", "linker_modtools.exe", "export2rust.exe" })
            Touch(Path.Combine(game, "bin", exe));
        Touch(Path.Combine(game, "gdtdb", "gdtdb.exe"));
        foreach (var template in new[] { "MP Mod Level", "ZM Mod Level" })
            Directory.CreateDirectory(Path.Combine(game, "rex", "templates", template, "usermaps", "template"));
        Directory.CreateDirectory(Path.Combine(game, "rex", "templates", "Mod"));
    }

    private static void Map(string name)
    {
        var folder = Path.Combine(GamePath, "usermaps", name);
        File.WriteAllText(Path.Combine(Dir(folder, "zone_source"), $"{name}.zone"),
            $"// {name}\n>mode,zm\nscriptparsetree,scripts/zm/{name}.gsc\nxmodel,p7_zm_der_banner_torn\n");
        File.WriteAllText(Path.Combine(Dir(folder, "scripts", "zm"), $"{name}.gsc"), "#using scripts\\zm\\_zm;\n\nfunction main()\n{\n}\n");
        File.WriteAllText(Path.Combine(Dir(folder, "sound", "zoneconfig"), $"{name}.szc"), "{}");
        var prefix = name.Split('_')[0];
        File.WriteAllText(Path.Combine(Dir(GamePath, "map_source", prefix), $"{name}.map"), "iwmap 4\n");
        // No .xpak files: Prepare for publish would send them to the real Recycle Bin.
    }

    private static void Mod(string name, params string[] zones)
    {
        var folder = Path.Combine(GamePath, "mods", name);
        foreach (var zone in zones)
            File.WriteAllText(Path.Combine(Dir(folder, "zone_source"), $"{zone}.zone"), $"// {name} {zone}\n");
        File.WriteAllText(Path.Combine(Dir(folder, "scripts", "zm"), $"{name}.gsc"), "function autoexec init()\n{\n}\n");
    }

    private static void WriteWorkshopFiles()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ThumbnailPath)!);
        Images.WriteThumbnail(ThumbnailPath, "CASTLE REDUX");
        var gallery1 = Path.Combine(CastleWorkshopFolder, "workshop_media", "courtyard.png");
        var gallery2 = Path.Combine(CastleWorkshopFolder, "workshop_media", "undercroft.png");
        Images.WriteThumbnail(gallery1, "COURTYARD");
        Images.WriteThumbnail(gallery2, "UNDERCROFT");

        var stable = new WorkshopItemData
        {
            PublisherId = "2987654321",
            Title = "Castle Redux",
            Description = "[h1]Castle Redux[/h1]\nDer Eisendrache, rebuilt from the ground up for custom zombies.\n\n[list]\n[*] Four reworked bow quests\n[*] New Pack-a-Punch camo\n[*] Wunderwaffe DG-2 in the box\n[/list]\n\n[b]Report bugs[/b] in the comments with your console log.",
            Thumbnail = ThumbnailPath,
            Type = "map",
            FolderName = "zm_castle_redux",
            Changelog = "Fixed the bell step softlock on solo. Rebalanced round 30+ health.",
        };
        stable.SetTagList(["Zombies", "Map"]);
        stable.SetVisibility(WorkshopItemVisibility.Unlisted);
        stable.SetPreviewImageList([gallery1, gallery2]);

        var canary = stable.Clone();
        canary.PublisherId = "";
        canary.Title = "Castle Redux (canary)";
        canary.Changelog = "Testing the new bow quest steps.";
        canary.SetVisibility(WorkshopItemVisibility.FriendsOnly);

        var profiles = new WorkshopProfilesData
        {
            ActiveProfileId = "stable",
            Profiles =
            [
                new WorkshopPublishProfile { Id = "stable", Name = "Stable", WorkshopJson = stable },
                new WorkshopPublishProfile { Id = "canary", Name = "Canary", WorkshopJson = canary },
            ],
        };

        // Castle has been built: a linked zone to publish. Its Workshop files keep an older build's
        // layout (versions file and media in zone), which is what tests their move out of zone.
        File.WriteAllText(Path.Combine(CastleWorkshopFolder, "zm_castle_redux.ff"), new string('x', 4096));
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(CastleWorkshopFolder, "workshop.json"), JsonSerializer.Serialize(stable, options));

        File.WriteAllText(Path.Combine(CastleWorkshopFolder, "workshop.profiles.json"), JsonSerializer.Serialize(profiles, options));

        // A published mod, so the picker shows both states.
        var wraithZone = Dir(GamePath, "mods", "wraith_weapons", "zone");
        var wraith = new WorkshopItemData { PublisherId = "2912345678", Title = "Wraith Weapons", Type = "mod", FolderName = "wraith_weapons" };
        File.WriteAllText(Path.Combine(wraithZone, "workshop.json"), JsonSerializer.Serialize(wraith, options));
    }

    private static void WriteSettings()
    {
        var castle = new ProjectSettings
        {
            DisplayName = "Castle Redux",
            Category = "Zombies",
            IsFavorite = true,
            Notes = "Bell step softlocks on solo. Check before the next update.",
            LaunchConfigIndex = 0,
            EnvironmentDvars = new()
            {
                ["Dev"] = new() { ["developer"] = "2", ["logfile"] = "1", ["scr_mod_enable_devblock"] = "1", ["ai_disableSpawn"] = "0" },
                ["Ship"] = new() { ["developer"] = "0" },
            },
            IsCompileChecked = true,
            CompileModeIndex = 1,
            IsLinkChecked = true,
            BuildModeIndex = 1,
            BuildPresets =
            [
                new BuildPreset { Name = "Link only", IsLinkChecked = true },
                new BuildPreset { Name = "Full rebuild", IsCompileChecked = true, IsLightChecked = true, LightQualityIndex = 2, IsLinkChecked = true },
            ],
            LastBuildStatus = "completed with errors",
            LastBuildErrorCount = 2,
            LastBuildWarningCount = 3,
            LastBuildTimestamp = new DateTime(2026, 9, 25, 21, 14, 0),
            LastBuildDurationMs = 94_000,
        };

        var projects = new Dictionary<string, ProjectSettings>
        {
            ["map:zm_castle_redux"] = castle,
            ["map:zm_foundry"] = new() { DisplayName = "Foundry", Category = "Zombies", IsLinkChecked = true },
            ["map:mp_skyline"] = new() { Category = "Multiplayer", IsCompileChecked = true, IsLinkChecked = true },
            ["mod:wraith_weapons"] = new() { DisplayName = "Wraith Weapons", Category = "Weapons", IsFavorite = true, IsLinkChecked = true, CheckedZones = ["core_mod", "zm_mod"] },
            ["mod:perk_overhaul"] = new() { DisplayName = "Perk Overhaul", IsLinkChecked = true },
        };

        var settings = new JsonObject
        {
            ["WindowWidth"] = 1150,
            ["WindowHeight"] = 700,
            ["WindowX"] = 0,
            ["WindowY"] = 0,
            ["GamePath"] = GamePath,
            ["ToolsPath"] = GamePath,
            ["LastActiveProject"] = "zm_castle_redux",
            ["LastActiveProjectType"] = "map",
            ["RecentProjects"] = new JsonArray("map:zm_castle_redux", "mod:wraith_weapons", "map:zm_foundry"),
            ["Projects"] = JsonSerializer.SerializeToNode(projects),
        };

        Directory.CreateDirectory(DataPath);
        File.WriteAllText(Path.Combine(DataPath, "settings.json"), settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteSavedLog()
    {
        var log = string.Join("\n", BuildLogHead.Concat(BuildLogTail)) + "\n\nBuild completed with errors.\n";
        File.WriteAllText(Path.Combine(Dir(DataPath, "logs"), "map_zm_castle_redux_last.log"), log);
    }

    private static string Dir(params string[] parts)
    {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }
}
