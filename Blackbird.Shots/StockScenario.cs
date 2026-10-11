using System.Text;
using System.Text.Json.Nodes;
using Blackbird.Models;
using Blackbird.Services;
using Blackbird.ViewModels;
using Blackbird.Views;
using static Blackbird.Shots.Program;

namespace Blackbird.Shots;

/// <summary>
/// Stock carry in the launcher: the fallback rule (decision table, then the real build service over stand-in linkers),
/// the components registry entry, and the Components view over fake installs.
/// </summary>
internal static class StockScenario
{
    private const int Unsupported = 0x0BB10001;
    private const int Internal = 0x0BB10002;
    private const int Carry = 0x0BB10003;

    public static void Run(MainWindow main, MainWindowViewModel vm, ISettingsService settings)
    {
        FallbackDecision();
        InstallHash();

        var game = FakeInstall.GamePath;
        var scratch = Path.Combine(FakeInstall.Root, "stock");
        Directory.CreateDirectory(scratch);
        var dragon = Path.Combine(scratch, "bb-linker.cmd");
        var stockLinker = Path.Combine(scratch, "linker.cmd");
        var oldDragon = Path.Combine(scratch, "bb-linker-old.cmd");
        File.WriteAllText(dragon, Crlf(DragonScript));
        File.WriteAllText(stockLinker, "@echo off\r\necho STOCK LINKER RAN\r\nexit /b 0\r\n");
        File.WriteAllText(oldDragon, "@echo off\r\necho usage: bb-linker [-progress pipe] -modsource zone\r\nexit /b 0\r\n");
        var priorLinker = Environment.GetEnvironmentVariable("BLACKBIRD_BB_LINKER");
        var added = new List<string>();

        try
        {
            Environment.SetEnvironmentVariable("BLACKBIRD_BB_LINKER", dragon);
            BuildServiceFallbacks(dragon, oldDragon, stockLinker, out var carryLog);

            Registry(vm, game);
            Plan(vm, settings, game, dragon, added);

            Install(game, added, apex: true, stocklib: true, index: true, host: true, plugin: false);
            ComponentsView(main, "components", expectOn: true);

            settings.UseBbLinker = false;
            ComponentsView(main, "components-link-off", expectOn: false, expectReason: "Link with bb-linker is off");
            settings.UseBbLinker = true;

            Environment.SetEnvironmentVariable("BLACKBIRD_BB_LINKER", null);
            Install(game, added, apex: false, stocklib: false, index: false, host: false, plugin: false);
            ComponentsView(main, "components-none", expectOn: false, expectReason: "Dragon isn't installed");

            Environment.SetEnvironmentVariable("BLACKBIRD_BB_LINKER", dragon);
            Install(game, added, apex: true, stocklib: true, index: true, host: true, plugin: true);
            CarryFailureShot(main, vm, carryLog);
            Palette(main);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLACKBIRD_BB_LINKER", priorLinker);
            foreach (var path in added.Where(File.Exists))
                File.Delete(path);
            var marker = BbLinker.CarryMarkerPath(FakeInstall.CastleFolder, "zm_castle_redux");
            if (File.Exists(marker))
                File.Delete(marker);
            Build.Result = true;
            Build.Tail = null;
        }
    }

    // ── The fallback rule ───────────────────────────────────────────────────

    private static void FallbackDecision()
    {
        var table = new (int Exit, bool Carry, BbLinker.Next Expect)[]
        {
            (0, false, BbLinker.Next.Accept),
            (0, true, BbLinker.Next.Accept),
            (3, true, BbLinker.Next.Accept),
            (Unsupported, false, BbLinker.Next.UseStockLinker),
            (Internal, false, BbLinker.Next.UseStockLinker),
            (Unsupported, true, BbLinker.Next.FailStockCarry),
            (Internal, true, BbLinker.Next.FailStockCarry),
            (Carry, false, BbLinker.Next.FailStockCarry),
            (Carry, true, BbLinker.Next.FailStockCarry),
        };
        var wrong = table.Where(t => BbLinker.AfterStep(t.Exit, t.Carry) != t.Expect).Select(t => $"0x{t.Exit:X}/{t.Carry}").ToList();
        Check($"fallback: 0x0BB10001/2 fall back only without a carry marker, 0x0BB10003 never does ({(wrong.Count == 0 ? "all nine rows" : string.Join(", ", wrong))})",
            wrong.Count == 0);
        Check("fallback: MeansUseStock still matches only 0x0BB10001 and 0x0BB10002",
            BbLinker.MeansUseStock(Unsupported) && BbLinker.MeansUseStock(Internal) && !BbLinker.MeansUseStock(Carry) && !BbLinker.MeansUseStock(1));

        var reasons = new[]
        {
            BbLinker.CarryBlockedReason(false, false, false, false),
            BbLinker.CarryBlockedReason(true, true, true, true),
            BbLinker.CarryBlockedReason(true, false, true, true),
            BbLinker.CarryBlockedReason(true, true, false, false),
            BbLinker.CarryBlockedReason(true, true, true, false),
        };
        Check($"fallback: a carry project is blocked when the setting is off, bb-linker is missing or can't carry, never otherwise",
            reasons[0] is null && reasons[1] is null
            && reasons[2]!.Contains("Link with bb-linker is off") && reasons[3]!.Contains("isn't installed") && reasons[4]!.Contains("can't carry")
            && reasons.Skip(2).All(r => r!.EndsWith(BbLinker.CarryHint, StringComparison.Ordinal)));
    }

    private static void InstallHash()
    {
        // The directory the Stock Library already made for this machine's install.
        Check("registry: the install hash matches the Stock Library's for a known path",
            ComponentPaths.InstallHash(@"I:\SteamLibrary\steamapps\common\Call of Duty Black Ops III") == "6820b7c7f93ff5b3"
            && ComponentPaths.InstallHash(@"i:/steamlibrary/steamapps/common/Call of Duty Black Ops III\") == "6820b7c7f93ff5b3");
    }

    // ── The real build service over stand-in linkers ────────────────────────

    private sealed class Collect : IProgress<string>
    {
        public StringBuilder Text { get; } = new();
        public void Report(string value) { lock (Text) Text.Append(value); }
        public override string ToString() { lock (Text) return Text.ToString(); }
    }

    private static BuildCommand Link(string exe, string description, bool bb, string? marker, BuildCommand? fallback, int exit,
        List<LinkOutcome> into, string? blocked = null) =>
        new(exe, ["-modsource", "zm_castle_redux"], description)
        {
            ReportsProgress = bb,
            CarryMarker = marker,
            CarryBlockedReason = blocked,
            Fallback = fallback,
            Environment = new Dictionary<string, string> { ["FAKE_BBL_EXIT"] = exit.ToString() },
            OnLinked = o => { lock (into) into.Add(o); },
        };

    private static void BuildServiceFallbacks(string dragon, string oldDragon, string stockLinker, out string carryLog)
    {
        var marker = Path.Combine(Path.GetDirectoryName(dragon)!, "zm_castle_redux.stockcarry.json");
        File.WriteAllText(marker, "{\"carry_contract\":1,\"carried\":[]}");
        var outcomes = new List<LinkOutcome>();
        BuildCommand Stock(bool fellBack) => new(stockLinker, ["-modsource", "zm_castle_redux"], "Link zm_castle_redux")
        {
            OnLinked = o => { lock (outcomes) outcomes.Add(o with { FellBack = fellBack }); },
        };

        async Task<(bool Ok, string Log)> Run(params BuildCommand[] steps)
        {
            var log = new Collect();
            var ok = await new BuildService().RunBuildAsync([.. steps], false, log, CancellationToken.None);
            return (ok, log.ToString());
        }

        (bool Ok, string Log) Go(params BuildCommand[] steps) => Task.Run(() => Run(steps)).GetAwaiter().GetResult();

        // 1. A carry project: 0x0BB10003 fails the build with bb-linker's own lines and the hint; the stock linker never runs.
        var one = Go(Link(dragon, "Link zm_castle_redux (bb-linker)", true, marker, Stock(true), Carry, outcomes));
        carryLog = one.Log;
        Check($"build: 0x0BB10003 fails the build and the stock linker never runs (ok {one.Ok})",
            !one.Ok && !one.Log.Contains("STOCK LINKER RAN") && !one.Log.Contains("is linking it"));
        Check("build: the failure shows bb-linker's own lines naming the assets, then the hint",
            one.Log.Contains("^1ERROR: Dragon stock carry: weapon 'elemental_bow4_zm'")
            && one.Log.Contains("[launcher] bb-linker couldn't carry every stock asset")
            && one.Log.Contains("Create a local copy in Apex, or turn on Link with bb-linker."));

        // 2. A carry project: the two "use the stock linker" codes fail it too.
        foreach (var code in new[] { Unsupported, Internal })
        {
            var run = Go(Link(dragon, "Link zm_castle_redux (bb-linker)", true, marker, Stock(true), code, outcomes));
            Check($"build: 0x{code:X8} on a carry project fails instead of falling back (ok {run.Ok})",
                !run.Ok && !run.Log.Contains("STOCK LINKER RAN") && run.Log.Contains(BbLinker.CarryHint));
        }

        // 3. No marker: the fallback is unchanged.
        var plain = Go(Link(dragon, "Link zm_castle_redux (bb-linker)", true, null, Stock(true), Unsupported, outcomes));
        Check($"build: without a carry marker 0x0BB10001 still falls back to the stock linker (ok {plain.Ok})",
            plain.Ok && plain.Log.Contains("STOCK LINKER RAN") && plain.Log.Contains("so the stock linker is linking it"));
        var plainCarry = Go(Link(dragon, "Link zm_castle_redux (bb-linker)", true, null, Stock(true), Carry, outcomes));
        Check($"build: without a marker, 0x0BB10003 still never falls back (ok {plainCarry.Ok})",
            !plainCarry.Ok && !plainCarry.Log.Contains("STOCK LINKER RAN"));

        // 4. The stock linker would be used (setting off, bb-linker missing): the build stops before running anything.
        var reason = BbLinker.CarryBlockedReason(true, useBbLinker: false, linkerFound: true, hasStockCarry: true);
        var blocked = Go(
            new BuildCommand(stockLinker, ["-skip"], "Compile first") { },
            Link(stockLinker, "Link zm_castle_redux", false, marker, null, 0, outcomes, reason));
        Check($"build: a carry project whose link would use the stock linker fails before any step runs (ok {blocked.Ok})",
            !blocked.Ok && !blocked.Log.Contains("STOCK LINKER RAN") && blocked.Log.Contains("Link with bb-linker is off")
            && blocked.Log.Contains(BbLinker.CarryHint));

        // 5. A bb-linker without the stock-carry capability.
        var old = Go(Link(oldDragon, "Link zm_castle_redux (bb-linker)", true, marker, Stock(true), 0, outcomes));
        Check($"build: a bb-linker that can't carry stock fails a carry project before it links (ok {old.Ok})",
            !old.Ok && old.Log.Contains("can't carry stock assets") && !old.Log.Contains("STOCK LINKER RAN"));

        // 6. A project without a marker, with an old bb-linker, links as before.
        var oldPlain = Go(Link(oldDragon, "Link zm_castle_redux (bb-linker)", true, null, Stock(true), 0, outcomes));
        Check($"build: a project without a marker links with an old bb-linker as before (ok {oldPlain.Ok})", oldPlain.Ok);

        var exits = outcomes.Select(o => $"{o.Linker}{(o.FellBack ? "*" : "")}:{o.ExitCode:X}").ToList();
        Check($"build: every link reports its linker, whether it fell back, and its exit ({string.Join(" ", exits)})",
            outcomes.Count >= 8 && outcomes.Any(o => o is { Linker: "stock", FellBack: true, ExitCode: 0 })
            && outcomes.Any(o => o is { Linker: "bb-linker", ExitCode: Carry }));
    }

    // ── Registry ────────────────────────────────────────────────────────────

    private static JsonObject? ReadEntry(string game)
    {
        try { return JsonNode.Parse(File.ReadAllText(ComponentPaths.Entry(game, "blackbird"))) as JsonObject; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return null; }
    }

    private static void Registry(MainWindowViewModel vm, string game)
    {
        var path = ComponentPaths.Entry(game, "blackbird");
        WaitFor(() => File.Exists(path), 4000);
        var entry = ReadEntry(game);
        Check($"registry: Blackbird's entry is written under the install hash on start ({Path.GetFileName(Path.GetDirectoryName(path))})",
            entry is not null && entry["id"]?.GetValue<string>() == "blackbird" && entry["version"] is not null
            && entry["written_at"] is not null && entry["last_builds"] is JsonObject
            && entry["use_bb_linker"]?.GetValue<bool>() == true && entry["bb_linker_effective"]?.GetValue<bool>() == false);

        // The scenario's linker appeared after the start write; a settings change rewrites the entry and finds it.
        vm.UseBbLinker = false;
        WaitFor(() => ReadEntry(game)?["use_bb_linker"]?.GetValue<bool>() == false);
        entry = ReadEntry(game);
        Check("registry: turning the Link setting off rewrites the entry, and the linker stops being effective",
            entry?["use_bb_linker"]?.GetValue<bool>() == false && entry["bb_linker_effective"]?.GetValue<bool>() == false);
        vm.UseBbLinker = true;
        WaitFor(() => ReadEntry(game)?["bb_linker_effective"]?.GetValue<bool>() == true);
        entry = ReadEntry(game);
        Check("registry: with the setting on and a linker that lists stock-carry, the linker is effective at its resolved path",
            entry?["use_bb_linker"]?.GetValue<bool>() == true && entry["bb_linker_effective"]?.GetValue<bool>() == true
            && entry["bb_linker_path"]?.GetValue<string>()?.EndsWith("bb-linker.cmd", StringComparison.Ordinal) == true);
        Check("registry: no temp files are left beside the entry",
            !Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any());
    }

    // ── The view model's plan, and a failed carry build in the log ──────────

    private static void Plan(MainWindowViewModel vm, ISettingsService settings, string game, string dragon, List<string> added)
    {
        var marker = BbLinker.CarryMarkerPath(FakeInstall.CastleFolder, "zm_castle_redux");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "{\"carry_contract\":1,\"zone\":\"zm_castle_redux\",\"carried\":[{\"type\":\"xmodel\",\"name\":\"p7_zm_zod_master_switch\",\"zone\":\"zm_zod\"}]}");

        List<BuildCommand> Planned()
        {
            Build.Result = true;
            vm.BuildOnlyOnceCommand.Execute(null);
            WaitFor(() => vm.IsBuildRunning);
            WaitFor(() => !vm.IsBuildRunning);
            Pump(100);
            return Build.LastCommands;
        }

        var on = Planned().Where(c => c.CarryMarker is not null).ToList();
        Check($"plan: with Link with bb-linker on, a carry project links with bb-linker and may not fall back ({on.Count} step, blocked '{on.FirstOrDefault()?.CarryBlockedReason}')",
            on.Count == 1 && on[0].ReportsProgress && on[0].CarryBlockedReason is null && on[0].Fallback is not null
            && on[0].OnLinked is not null && on[0].Fallback!.OnLinked is not null);

        settings.UseBbLinker = false;
        var off = Planned().Where(c => c.CarryMarker is not null).ToList();
        Check("plan: with the setting off, the carry project's link is blocked with the reason instead of planned for the stock linker",
            off.Count == 1 && off[0].CarryBlockedReason is { } why && why.Contains("Link with bb-linker is off") && why.EndsWith(BbLinker.CarryHint, StringComparison.Ordinal));

        Environment.SetEnvironmentVariable("BLACKBIRD_BB_LINKER", Path.Combine(FakeInstall.Root, "stock", "missing.cmd"));
        settings.UseBbLinker = true;
        var missing = Planned().Where(c => c.CarryMarker is not null).ToList();
        Check("plan: with bb-linker missing, the carry project's link is blocked with the reason",
            missing.Count == 1 && missing[0].CarryBlockedReason is { } gone && gone.Contains("isn't installed"));
        Environment.SetEnvironmentVariable("BLACKBIRD_BB_LINKER", dragon);

        File.Delete(marker);
        var plain = Planned();
        Check("plan: without a marker, nothing is blocked and the link keeps its fallback",
            plain.All(c => c.CarryBlockedReason is null && c.CarryMarker is null) && plain.Any(c => c.Fallback is not null));
        File.WriteAllText(marker, "{\"carry_contract\":1,\"zone\":\"zm_castle_redux\",\"carried\":[]}");
    }

    private static void CarryFailureShot(MainWindow main, MainWindowViewModel vm, string realLog)
    {
        // The real build service's output for a failed carry, replayed through the main window's log.
        Build.Tail = realLog.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.StartsWith("Link zm_castle", StringComparison.Ordinal)).ToArray();
        Build.Result = false;
        vm.BuildOnlyOnceCommand.Execute(null);
        WaitFor(() => vm.IsBuildRunning);
        WaitFor(() => !vm.IsBuildRunning);
        Pump(250);
        Check($"build: a failed carry link lands as a failed build with the hint in the log ('{vm.StatusLine}')",
            vm.Output.Text.Contains("Create a local copy in Apex, or turn on Link with bb-linker.") && vm.StatusKind == "error");
        Shot(main, "main-carry-failed");
        Build.Result = true;
        Build.Tail = null;
        vm.BuildOnlyOnceCommand.Execute(null);
        WaitFor(() => vm.IsBuildRunning);
        WaitFor(() => !vm.IsBuildRunning);
        Pump(150);
    }

    // ── Components view ─────────────────────────────────────────────────────

    private static void Touch(string path, List<string> added)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
            added.Add(path);
        File.WriteAllBytes(path, [0]);
    }

    private static void Install(string game, List<string> added, bool apex, bool stocklib, bool index, bool host, bool plugin)
    {
        void Set(bool want, string path)
        {
            if (want)
                Touch(path, added);
            else if (File.Exists(path))
                File.Delete(path);
        }

        Set(apex, Path.Combine(game, "bin", "Apex.exe"));
        Set(stocklib, Path.Combine(game, "bin", "stocklib", "stocklib.exe"));
        Set(index, ComponentPaths.StockDb(game));
        Set(host, Path.Combine(game, "bin", "plugins", "RadiantPlugins.dll"));
        Set(plugin, Path.Combine(game, "bin", "plugins", "StockLibrary", "StockLibrary.dll"));
    }

    private static void ComponentsView(MainWindow main, string shot, bool expectOn, string? expectReason = null)
    {
        Invoke(main, "ShowComponentsAsync");
        var dialog = Owned<ComponentsDialog>(main);
        var model = (ComponentsDialogViewModel)dialog.DataContext!;
        Pump(150);
        var names = model.Rows.Select(r => r.Name).ToList();
        Check($"components: lists Blackbird, Apex, Dragon, Stock Library, Ultrasound and Radiant integration ({string.Join(", ", names)})",
            names.SequenceEqual(["Blackbird", "Apex", "Dragon", "Stock Library", "Ultrasound", "Radiant integration"]));
        Check($"components: Use directly is {(expectOn ? "on" : "off")} ({model.UseDirectlyText}: {model.UseDirectlyReason})",
            model.UseDirectly == expectOn && (expectReason is null || model.UseDirectlyReason.Contains(expectReason, StringComparison.Ordinal)));
        Shot(dialog, shot);
        Shot150(dialog, shot + "@1.5x");

        if (shot == "components")
        {
            var rows = model.Rows.ToDictionary(r => r.Id);
            Check("components: states follow what is installed (Dragon ready, Stock Library ready, Radiant needs its plugin, Ultrasound missing)",
                rows["dragon"].IsReady && rows["dragon"].StateText.Contains("stock-carry") && rows["stocklib"].IsReady
                && rows["radiant"].NeedsAttention && rows["ultrasound"].IsMissing && rows["apex"].IsReady && rows["blackbird"].IsReady);
            Check("components: a missing component offers its product page, an installed one offers nothing",
                rows["ultrasound"].HasAction && rows["ultrasound"].ActionUrl!.StartsWith("https://", StringComparison.Ordinal)
                && !rows["dragon"].HasAction && !rows["blackbird"].HasAction);
        }

        Key(dialog, Avalonia.Input.Key.Escape);
        Check($"components: Esc closes the view ({shot})", WaitFor(() => !dialog.IsVisible));
        Pump(100);
    }

    private static void Palette(MainWindow main)
    {
        // The command palette reaches the same view; the menu is its one visible home.
        Invoke(main, "ExecuteCommandPaletteEntryAsync", new CommandPaletteEntry { ActionKey = "components", Name = "Components…", Detail = "", TypeLabel = "" });
        var dialog = Owned<ComponentsDialog>(main);
        Check("components: the command palette opens the same view", dialog.IsVisible);
        Key(dialog, Avalonia.Input.Key.Escape);
        WaitFor(() => !dialog.IsVisible);
    }

    private static string Crlf(string text) => text.ReplaceLineEndings("\r\n");

    private const string DragonScript = """
        @echo off
        if "%~1"=="-capabilities" goto caps
        echo ^^7Linking zone zm_castle_redux
        if "%FAKE_BBL_EXIT%"=="0" goto done
        echo ^^1ERROR: Dragon stock carry: weapon 'elemental_bow4_zm' needs streamed data that no zone package holds. Use "Create local copy" in Apex for this asset.
        echo ^^1ERROR: Dragon stock carry: xmodel 'p7_zm_zod_master_switch' needs image 't7_zm_zod_switch_c', which zm_zod does not define.
        :done
        exit /b %FAKE_BBL_EXIT%
        :caps
        echo {"id":"dragon","version":"0.1.0","capabilities":["stock-carry"],"carry_contract":1,"stock_db_schema":[1,1],"stock_carry_active":true}
        exit /b 0
        """;
}
