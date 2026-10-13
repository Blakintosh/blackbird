using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Blackbird.Models;

namespace Blackbird.Services;

/// <summary>
/// Finds the Mod Tools apps and parts installed for one Black Ops III install, for the Components view. Every app
/// shows the same list. Detection reads the registry entries other apps write
/// (<see cref="ComponentPaths"/>) and the known places their files go; a later step asks
/// <c>stocklib status --json</c> for the index details.
/// </summary>
public static class ComponentScanner
{
    // The product download pages: the latest GitHub release of each product (Dragon and the Stock Library publish to binaries-only repos).
    public const string ApexUrl = "https://github.com/Blakintosh/apex/releases/latest";
    public const string UltrasoundUrl = "https://github.com/Blakintosh/ultrasound/releases/latest";
    public const string DragonUrl = "https://github.com/Blakintosh/dragon-releases/releases/latest";
    public const string StockLibraryUrl = "https://github.com/Blakintosh/stocklib-releases/releases/latest";
    public const string MtPluginsUrl = "https://github.com/Aidan601/MTPlugins";

    /// <summary>Reads the install. Touches the disk and runs <c>dragon -capabilities</c> (cached), so not on the UI thread.</summary>
    public static ComponentsReport Scan(string gamePath, string toolsPath, bool useDragon)
    {
        var tools = string.IsNullOrWhiteSpace(toolsPath) ? gamePath : toolsPath;
        var bin = Path.Combine(tools, "bin");
        string Entry(string id) => ComponentPaths.Entry(gamePath, id);

        var apex = Apex(bin, Entry("apex"));
        var dragon = Dragon(gamePath, tools, useDragon);
        var stock = StockLibrary(gamePath, bin, Entry("stocklib"));
        var rows = new List<ComponentRow>
        {
            new()
            {
                Id = "blackbird",
                Name = "Blackbird",
                State = ComponentState.Ready,
                StateText = $"{AppUpdates.CurrentVersion} · this app",
                Detail = "Builds, runs and publishes your mods.",
            },
            apex.Row,
            dragon.Row,
            stock.Row,
            Ultrasound(gamePath),
            RadiantIntegration(bin, stock.Installed),
        };

        // The Stock Library's verdict is the one Apex and Radiant show; when it answers, this view shows the same. Its
        // own checks (Dragon's registry entry, schema ranges) see things this scan doesn't.
        var (on, reason) = stock.Exe is { } stocklib && StockLibraryVerdict(stocklib, gamePath, tools) is { } verdict
            ? verdict
            : UseDirectly(dragon, stock, useDragon);
        return new ComponentsReport { InstallPath = gamePath, Rows = rows, UseDirectly = on, UseDirectlyReason = reason };
    }

    private static (bool On, string Reason) UseDirectly(DragonInfo dragon, StockInfo stock, bool useDragon)
    {
        if (!dragon.Found)
            return (false, "Dragon isn't installed, so only the stock linker is available.");
        if (dragon.CarryContract is { } contract && contract != DragonLinker.CarryContract)
            return (false, $"Dragon's carry contract is {contract}; Blackbird and the Stock Library need {DragonLinker.CarryContract}. Update them to the same release.");
        if (!dragon.CanCarry)
            return (false, "This Dragon can't carry stock assets. Update it.");
        if (!stock.Installed)
            return (false, "Stock Library isn't installed, so Dragon has no index of the stock assets.");
        if (!stock.HasIndex)
            return (false, "The Stock Library index isn't built yet.");
        if (!useDragon)
            return (false, "Link with Dragon is off in Tools. Projects that use stock assets won't build.");
        if (dragon.DevBuild)
            return (true, $"Stock assets link by name with the dev build of Dragon ({DragonLinker.OverrideVariable}); the Stock Library may judge the installed Dragon instead.");
        return (true, "Stock assets link by name; Dragon carries them, so there are no copies to make.");
    }

    /// <summary>
    /// <c>stocklib status --install &lt;BO3&gt; --json</c>'s <c>use_directly</c> verdict, or null when it can't answer
    /// (an old or broken Stock Library, a timeout). Its reason and fix are shown as one line.
    /// </summary>
    private static (bool On, string Reason)? StockLibraryVerdict(string exe, string gamePath, string tools)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "status", "--install", gamePath, "--json" })
                startInfo.ArgumentList.Add(arg);
            foreach (var (name, value) in DragonLinker.InstallEnvironment(gamePath, tools))
                startInfo.Environment[name] = value;
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            if (process.ExitCode != 0 || JsonNode.Parse(stdout.Result) is not JsonObject root
                || root["verdicts"]?["use_directly"] is not JsonObject verdict
                || verdict["ok"] is not JsonValue ok || !ok.TryGetValue<bool>(out var isOk)
                || verdict["reason"] is not JsonValue reasonValue || !reasonValue.TryGetValue<string>(out var reason))
                return null;
            var fix = verdict["fix"] is JsonValue fixValue && fixValue.TryGetValue<string>(out var f) && f.Length > 0 ? f : null;
            return (isOk, fix is null ? reason : $"{reason.TrimEnd('.')}. {fix.TrimEnd('.')}.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException
                                       or System.Text.Json.JsonException or AggregateException)
        {
            return null;
        }
    }

    // ── Apex ────────────────────────────────────────────────────────────────

    private static (ComponentRow Row, bool Found) Apex(string bin, string registryEntry)
    {
        var alongside = Path.Combine(bin, "Apex.exe");
        var replacing = Path.Combine(bin, "asseteditor_modtools.exe");
        var exe = File.Exists(alongside) ? alongside
            : File.Exists(replacing) && ProductNameContains(replacing, "Apex") ? replacing
            : null;
        if (exe is null)
            return (new ComponentRow
            {
                Id = "apex",
                Name = "Apex",
                State = ComponentState.Missing,
                StateText = "not installed",
                Detail = "Edit GDT assets with live previews, and make local copies of stock assets.",
                ActionLabel = "Get Apex",
                ActionUrl = ApexUrl,
            }, false);

        var version = RegistryText(registryEntry, "version") ?? ProductVersion(exe);
        return (new ComponentRow
        {
            Id = "apex",
            Name = "Apex",
            State = ComponentState.Ready,
            StateText = version is null ? "installed" : $"{version} · installed",
            Detail = "Edit GDT assets with live previews, and make local copies of stock assets.",
        }, true);
    }

    // ── Dragon ──────────────────────────────────────────────────

    private sealed record DragonInfo(ComponentRow Row, bool Found, bool CanCarry, bool DevBuild = false, int? CarryContract = null);

    private static DragonInfo Dragon(string gamePath, string tools, bool useDragon)
    {
        // Only the Dragon builds would use. dragon.json names whichever Dragon ran last (a developer's build, a
        // test), so it never stands in for an installed one.
        var exe = DragonLinker.Locate(tools);
        if (exe is null)
            return new DragonInfo(new ComponentRow
            {
                Id = "dragon",
                Name = "Dragon",
                State = ComponentState.Missing,
                StateText = DragonLinker.OverridePath is null ? "not installed" : "dev build missing",
                Detail = DragonLinker.OverridePath is { } named
                    ? $"{DragonLinker.OverrideVariable} names {named}, which doesn't exist, so builds use the stock linker."
                    : "A faster linker, and it links stock assets by name so you don't have to copy them.",
                ActionLabel = "Get Dragon",
                ActionUrl = DragonUrl,
            }, false, false);

        var dev = DragonLinker.IsOverride(exe);
        var info = DragonLinker.Probe(exe, DragonLinker.InstallEnvironment(gamePath, tools));
        var version = info?.Version;
        var kind = dev ? "dev build" : "installed";
        var prefix = version is null ? kind : $"{version} · {kind}";
        var installed = DragonLinker.InstalledPath(tools);
        var devNote = !dev ? ""
            : File.Exists(installed)
                ? $" This is the dev build {exe} ({DragonLinker.OverrideVariable}); the installed Dragon at {installed} isn't used."
                : $" This is the dev build {exe} ({DragonLinker.OverrideVariable}), not an installed Dragon.";
        if (info?.HasStockCarry == true && info.CarryContract == DragonLinker.CarryContract)
            return new DragonInfo(new ComponentRow
            {
                Id = "dragon",
                Name = "Dragon",
                State = ComponentState.Ready,
                StateText = $"{prefix} · stock-carry",
                Detail = (useDragon
                    ? "Blackbird links with Dragon, so stock assets can be used directly with no copies."
                    : "Stock assets can be used with no copies, but Link with Dragon is off in Tools.") + devNote,
            }, true, true, dev, info.CarryContract);

        if (info?.HasStockCarry == true)
            return new DragonInfo(new ComponentRow
            {
                Id = "dragon",
                Name = "Dragon",
                State = ComponentState.Attention,
                StateText = $"{prefix} · carry contract {info.CarryContract?.ToString() ?? "unknown"}",
                Detail = $"This Dragon speaks a different stock-carry contract than Blackbird and the Stock Library ({DragonLinker.CarryContract}). Update both to the same release." + devNote,
            }, true, false, dev, info.CarryContract);

        return new DragonInfo(new ComponentRow
        {
            Id = "dragon",
            Name = "Dragon",
            State = ComponentState.Attention,
            StateText = $"{prefix} · no stock-carry",
            Detail = "This version links faster but can't use stock assets directly. Update it to carry them." + devNote,
        }, true, false, dev);
    }

    // ── Stock Library ───────────────────────────────────────────────────────

    private sealed record StockInfo(ComponentRow Row, bool Installed, bool HasIndex, string? Exe = null);

    private static StockInfo StockLibrary(string gamePath, string bin, string registryEntry)
    {
        var exe = Path.Combine(bin, "stocklib", "stocklib.exe");
        var found = File.Exists(exe) ? exe : RegistryText(registryEntry, "path") is { } p && File.Exists(p) ? p : null;
        var installed = found is not null;
        var db = new FileInfo(ComponentPaths.StockDb(gamePath));
        if (!installed)
            return new StockInfo(new ComponentRow
            {
                Id = "stocklib",
                Name = "Stock Library",
                State = ComponentState.Missing,
                StateText = "not installed",
                Detail = "Every stock asset in search, previews and Create local copy, and what Dragon links by name.",
                ActionLabel = "Get Stock Library",
                ActionUrl = StockLibraryUrl,
            }, false, false);

        var version = RegistryText(registryEntry, "version") ?? ProductVersion(exe);
        var prefix = version is null ? "installed" : $"{version} · installed";
        if (!db.Exists)
            return new StockInfo(new ComponentRow
            {
                Id = "stocklib",
                Name = "Stock Library",
                State = ComponentState.Attention,
                StateText = $"{prefix} · no index",
                Detail = "The stock index isn't built yet. Apex and Radiant build it when they open.",
            }, true, false, found);

        return new StockInfo(new ComponentRow
        {
            Id = "stocklib",
            Name = "Stock Library",
            State = ComponentState.Ready,
            StateText = $"{prefix} · index {db.LastWriteTime:d MMM yyyy}",
            Detail = "Stock assets in search, previews and Create local copy.",
        }, true, true, found);
    }

    // ── Ultrasound ──────────────────────────────────────────────────────────

    private static ComponentRow Ultrasound(string gamePath)
    {
        var converter = Path.Combine(gamePath, "sound", "snd_convert.exe");
        // Ultrasound takes over Treyarch's file name: it is Ultrasound by its version resource or its registry entry, never by the name.
        if (File.Exists(converter) && ComponentUpdater.IsInstalled(ComponentSpecs.Ultrasound, converter, gamePath))
            return new ComponentRow
            {
                Id = "ultrasound",
                Name = "Ultrasound",
                State = ComponentState.Ready,
                StateText = (ProductVersion(converter) ?? RegistryText(ComponentPaths.Entry(gamePath, "ultrasound"), "version")) is { } v ? $"{v} · installed" : "installed",
                Detail = "Replaces the Mod Tools sound converter.",
            };

        return new ComponentRow
        {
            Id = "ultrasound",
            Name = "Ultrasound",
            State = ComponentState.Missing,
            StateText = "not installed",
            Detail = "Replaces the Mod Tools sound converter.",
            ActionLabel = "Get Ultrasound",
            ActionUrl = UltrasoundUrl,
        };
    }

    // ── Radiant integration ─────────────────────────────────────────────────

    private static ComponentRow RadiantIntegration(string bin, bool stockInstalled)
    {
        var host = File.Exists(Path.Combine(bin, "plugins", "RadiantPlugins.dll"));
        var plugin = File.Exists(Path.Combine(bin, "plugins", "StockLibrary", "StockLibrary.dll"));
        if (host && plugin)
            return new ComponentRow
            {
                Id = "radiant",
                Name = "Radiant integration",
                State = ComponentState.Ready,
                StateText = "MTPlugins + StockLibrary plugin",
                Detail = "Stock models in Radiant's model browser, and dragging one in links without a copy.",
            };

        if (host)
            return new ComponentRow
            {
                Id = "radiant",
                Name = "Radiant integration",
                State = ComponentState.Attention,
                StateText = "MTPlugins found · no StockLibrary plugin",
                // TODO(installer): the Stock Library installer adds the plugin; offer that here once Blackbird can run it.
                Detail = stockInstalled
                    ? "The StockLibrary plugin adds stock models to Radiant's model browser. Reinstall Stock Library to add it."
                    : "The StockLibrary plugin comes with Stock Library and adds stock models to Radiant's model browser.",
            };

        return new ComponentRow
        {
            Id = "radiant",
            Name = "Radiant integration",
            State = ComponentState.Attention,
            StateText = "MTPlugins not found",
            Detail = "Stock models in Radiant's model browser need MTPlugins, a free Radiant plugin host.",
            ActionLabel = "Get MTPlugins",
            ActionUrl = MtPluginsUrl,
        };
    }

    // ── Reading ─────────────────────────────────────────────────────────────

    private static string? RegistryText(string path, string key)
    {
        try
        {
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root
                && root[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null; // an entry another app is rewriting, or one that isn't JSON: treated as absent
        }
    }

    private static bool ProductNameContains(string exe, string word)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(exe).ProductName?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? ProductVersion(string exe)
    {
        try
        {
            var text = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0].Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
