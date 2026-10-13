using System.Text;
using Blackbird.Models;
using Blackbird.Services;
using Blackbird.ViewModels;
using static Blackbird.Shots.Program;

namespace Blackbird.Shots;

/// <summary>
/// Dragon and the stock linker side by side: every build log names the linker that linked (a coloured Dragon line from
/// bb-linker's own -capabilities, or the stock linker's plain line), the status line says it too, and a missing
/// bb-linker disables Tools > Link with bb-linker with a reason. Logs come from the real build service over stand-in
/// linkers, then replay through the main window for the shots; each is also saved as text beside the shots.
/// </summary>
internal static class LinkerScenario
{
    private const int Unsupported = 0x0BB10001;

    private sealed class Collect : IProgress<string>
    {
        private readonly StringBuilder _text = new();
        public void Report(string value) { lock (_text) _text.Append(value); }
        public override string ToString() { lock (_text) return _text.ToString(); }
    }

    public static void Run(MainWindow main, MainWindowViewModel vm, ISettingsService settings, string dragon, string stockLinker)
    {
        StatusNotes();

        var marker = BbLinker.CarryMarkerPath(FakeInstall.CastleFolder, "zm_castle_redux");
        var markerText = File.Exists(marker) ? File.ReadAllText(marker) : null;
        if (markerText is not null)
            File.Delete(marker);
        var prior = Environment.GetEnvironmentVariable(BbLinker.OverrideVariable);
        var priorSetting = settings.UseBbLinker;
        var missing = Path.Combine(FakeInstall.Root, "stock", "missing", "bb-linker.exe");

        try
        {
            // 1. Dragon as installed (no override): the coloured line from -capabilities, before the linker's output.
            Environment.SetEnvironmentVariable(BbLinker.OverrideVariable, null);
            var installed = Real(Dragon(dragon, 0, stockLinker));
            var head = $"^4Dragon 0.1.0 (bb-linker, built {File.GetLastWriteTime(dragon):d MMM yyyy HH:mm})";
            Check($"linker: a Dragon link opens with '{FirstLine(installed, "Dragon")}'",
                installed.Contains(head + "\n", StringComparison.Ordinal) && !installed.Contains("dev build", StringComparison.Ordinal)
                && installed.IndexOf(head, StringComparison.Ordinal) < installed.IndexOf("Linking zone", StringComparison.Ordinal));
            Check("linker: the Dragon line is coloured but counts as neither an error nor a warning",
                !BuildLogCodes.IsError(head) && !BuildLogCodes.IsWarning(head));

            // 1b. A Dragon that reports its commit and build date (1.0.0) is named by them, not by the exe's date.
            var v1 = Path.Combine(Path.GetDirectoryName(dragon)!, "bb-linker-1.0.0.cmd");
            File.WriteAllText(v1, "@echo off\r\nif \"%~1\"==\"-capabilities\" goto caps\r\necho ^^7Linking zone zm_castle_redux\r\nexit /b 0\r\n:caps\r\n"
                + "echo {\"id\":\"dragon\",\"version\":\"1.0.0\",\"commit\":\"0123456789abcdef0123\",\"build_date\":\"2026-10-13\",\"capabilities\":[\"stock-carry\",\"stock-sound\"],\"carry_contract\":1}\r\nexit /b 0\r\n");
            var release = Real(Dragon(v1, 0, stockLinker));
            Check($"linker: a Dragon reporting commit and build_date is named by them ('{FirstLine(release, "Dragon")}')",
                release.Contains("^4Dragon 1.0.0 (bb-linker, 012345678, built 2026-10-13)\n", StringComparison.Ordinal));

            // 2. A developer's build named by BLACKBIRD_BB_LINKER says so, with its path.
            Environment.SetEnvironmentVariable(BbLinker.OverrideVariable, dragon);
            var dev = Real(Dragon(dragon, 0, stockLinker));
            Check($"linker: a dev build named by {BbLinker.OverrideVariable} says so ('{FirstLine(dev, "Dragon")}')",
                dev.Contains($"^4Dragon 0.1.0 (bb-linker, dev build: {dragon}, built ", StringComparison.Ordinal));

            // 3. A fallback names both: the Dragon line, then the hand-over, then the stock linker's output.
            var handsOver = Path.Combine(Path.GetDirectoryName(dragon)!, "bb-linker-handover.cmd");
            File.WriteAllText(handsOver, HandOverScript.ReplaceLineEndings("\r\n"));
            var fallback = Real(Dragon(handsOver, Unsupported, stockLinker));
            Check("linker: a fallback shows the Dragon line, then hands over to the stock linker by name",
                fallback.Contains("^4Dragon 0.1.0", StringComparison.Ordinal)
                && fallback.IndexOf("^4Dragon", StringComparison.Ordinal) < fallback.IndexOf("so the stock linker is linking it", StringComparison.Ordinal)
                && fallback.Contains("STOCK LINKER RAN", StringComparison.Ordinal));

            // 4. The switch off with Dragon installed: the stock linker's plain line says so, once per build.
            settings.UseBbLinker = false;
            var offPlan = Planned(vm);
            var offHeaders = offPlan.Where(c => c.Header is not null).ToList();
            Check($"linker: with the switch off, the first stock link names the linker and the switch, once ('{offHeaders.FirstOrDefault()?.Header}')",
                offHeaders.Count == 1 && offHeaders[0].Header == BbLinker.StockHeader("linker_modtools.exe", BbLinker.SwitchedOffNote)
                && !offPlan.Any(c => c.ReportsProgress));
            var off = Real(Stock(stockLinker, offHeaders.FirstOrDefault()?.Header));
            Check("linker: the stock line is plain (no colour code) and comes before the linker's output",
                off.Contains("[launcher] Linking with the stock linker (linker_modtools.exe). bb-linker is installed, but Tools > Link with bb-linker is off.\n", StringComparison.Ordinal)
                && !FirstLine(off, "Linking with the stock linker").Contains('^')
                && off.IndexOf("Linking with the stock linker", StringComparison.Ordinal) < off.IndexOf("STOCK LINKER RAN", StringComparison.Ordinal));
            Check("linker: Tools > Link with bb-linker shows unchecked but enabled when switched off",
                vm.IsBbLinkerInstalled && !vm.IsLinkingWithBbLinker);

            // 5. bb-linker missing: the switch is disabled with a reason; builds link with the stock linker, no note.
            Environment.SetEnvironmentVariable(BbLinker.OverrideVariable, missing);
            settings.UseBbLinker = true;
            var gonePlan = Planned(vm);
            var goneHeaders = gonePlan.Where(c => c.Header is not null).ToList();
            Check($"linker: with bb-linker missing, the stock link names the linker with no note ('{goneHeaders.FirstOrDefault()?.Header}')",
                goneHeaders.Count == 1 && goneHeaders[0].Header == BbLinker.StockHeader("linker_modtools.exe", null)
                && !gonePlan.Any(c => c.ReportsProgress));
            Check($"linker: with bb-linker missing, the switch is disabled, unchecked, and says why ('{vm.LinkWithBbLinkerToolTip}')",
                !vm.IsBbLinkerInstalled && !vm.IsLinkingWithBbLinker
                && vm.LinkWithBbLinkerToolTip.Contains("doesn't exist", StringComparison.Ordinal)
                && vm.LinkWithBbLinkerToolTip.Contains("The stock linker links every build.", StringComparison.Ordinal));
            Environment.SetEnvironmentVariable(BbLinker.OverrideVariable, null);
            Check($"linker: with no override and nothing installed, the reason names where Blackbird looks ('{BbLinker.MissingReason(FakeInstall.GamePath)}')",
                BbLinker.MissingReason(FakeInstall.GamePath) is { } why && why.Contains("isn't installed", StringComparison.Ordinal)
                && why.Contains(Path.Combine("bb-linker", "bb-linker.exe"), StringComparison.OrdinalIgnoreCase));
            var gone = Real(Stock(stockLinker, goneHeaders.FirstOrDefault()?.Header));

            // 6. Prepare for publish names the stock linker and why.
            Check("linker: Prepare for publish's stock line says it always links with it",
                BbLinker.StockHeader("linker_modtools.exe", BbLinker.PublishNote).EndsWith("Prepare for publish always links with it.", StringComparison.Ordinal));

            // 7. Dragon links the install Blackbird is building (TA_GAME_PATH / TA_TOOLS_PATH, as the stock launcher sets them).
            Environment.SetEnvironmentVariable(BbLinker.OverrideVariable, dragon);
            settings.UseBbLinker = true;
            var bbStep = Planned(vm).FirstOrDefault(c => c.ReportsProgress);
            var expectInstall = Path.TrimEndingDirectorySeparator(FakeInstall.GamePath) + "\\";
            Check($"linker: every Dragon link names Blackbird's install ({bbStep?.Environment?.GetValueOrDefault("TA_TOOLS_PATH")})",
                bbStep?.Environment is { } bbEnv && bbEnv.GetValueOrDefault("TA_TOOLS_PATH") == expectInstall
                && bbEnv.GetValueOrDefault("TA_GAME_PATH") == expectInstall && bbEnv.GetValueOrDefault("BBL_REAL_BUILD") == "1");
            var envEcho = Path.Combine(Path.GetDirectoryName(dragon)!, "bb-linker-env.cmd");
            File.WriteAllText(envEcho, "@echo off\r\nif \"%~1\"==\"-capabilities\" exit /b 0\r\necho install=%TA_TOOLS_PATH%\r\necho done: 0m0.01s\r\nexit /b 0\r\n");
            var echoed = Real(new BuildCommand(envEcho, ["-modsource", "zm_castle_redux"], "Link") { ReportsProgress = true, Environment = bbStep?.Environment });
            Check("linker: the Dragon process sees that install", echoed.Contains($"install={expectInstall}\n", StringComparison.Ordinal));

            // 8. An older Dragon marked a map that uses only stock sounds with .stocksound.json alone; it still counts.
            var soundMarker = BbLinker.SoundMarkerPath(FakeInstall.CastleFolder, "zm_castle_redux");
            File.WriteAllText(soundMarker, "{\"stock_sound_contract\":1,\"entries\":[]}");
            try
            {
                var soundStep = Planned(vm).FirstOrDefault(c => c.ReportsProgress);
                Check($"linker: a stock-sound marker makes the Dragon link a carry link that may not fall back ('{Path.GetFileName(soundStep?.CarryMarker)}')",
                    soundStep?.CarryMarker == soundMarker && soundStep.Fallback is not null
                    && BbLinker.AfterStep(Unsupported, soundStep.CarryMarker is not null) == BbLinker.Next.FailStockCarry);
                settings.UseBbLinker = false;
                var hook = vm.ConfirmAsync;
                vm.ConfirmAsync = null; // the plan as it stands without an answer; section 9 covers the question
                var soundOff = Planned(vm).Where(c => c.CarryMarker is not null).ToList();
                vm.ConfirmAsync = hook;
                Check("linker: with the switch off, a stock-sound project is blocked instead of linked by the stock linker",
                    soundOff.Count == 1 && soundOff[0].CarryBlockedReason?.Contains("Link with bb-linker is off", StringComparison.Ordinal) == true);
                settings.UseBbLinker = true;
                // The stand-in Dragon lists stock-carry but not stock-sound, so it can't link this project.
                var noSound = Real(new BuildCommand(dragon, ["-modsource", "zm_castle_redux"], "Link (bb-linker)")
                {
                    ReportsProgress = true, CarryMarker = soundMarker,
                    StockDependency = BbLinker.StockDependencyOf(FakeInstall.CastleFolder, "zm_castle_redux"),
                    Environment = new Dictionary<string, string> { ["FAKE_BBL_EXIT"] = "0" },
                    Fallback = new BuildCommand(stockLinker, ["-modsource", "zm_castle_redux"], "Link"),
                });
                Check("linker: a Dragon without stock-sound refuses a stock-sound project before linking, and the stock linker never runs",
                    noSound.Contains("can't carry stock sounds", StringComparison.Ordinal) && noSound.Contains(BbLinker.SoundHint, StringComparison.Ordinal)
                    && !noSound.Contains("STOCK LINKER RAN", StringComparison.Ordinal));
            }
            finally
            {
                File.Delete(soundMarker);
            }

            // 9. A carry map with Dragon switched off is never a dead end: the build asks, and can link with the stock linker anyway.
            File.WriteAllText(marker, "{\"carry_contract\":1,\"zone\":\"zm_castle_redux\",\"carried\":[{\"type\":\"xmodel\",\"name\":\"p7_zm_zod_master_switch\",\"zone\":\"zm_zod\"}],\"sound_aliases\":0}");
            var realConfirm = vm.ConfirmAsync;
            try
            {
                Check("linker: the main window gives the view model the shared confirm dialog", realConfirm is not null);
                settings.UseBbLinker = false;
                var asked = new List<string>();
                vm.ConfirmAsync = (title, message, action) =>
                {
                    asked.Add($"{title}|{message}|{action}");
                    return Task.FromResult(false);
                };
                var runs = Build.Runs;
                vm.BuildOnlyOnceCommand.Execute(null);
                Pump(200);
                Check($"linker: declining leaves the build unstarted and says why ('{vm.StatusLine}')",
                    Build.Runs == runs && asked.Count == 1
                    && asked[0] == $"{MainWindowViewModel.StockAnywayTitle}|This map uses stock assets carried by Dragon. Install Dragon or switch it on; linking with the stock linker will leave those assets out.|{MainWindowViewModel.StockAnywayAction}"
                    && vm.StatusLine.StartsWith("Build not started.", StringComparison.Ordinal));

                vm.ConfirmAsync = (_, _, _) => Task.FromResult(true);
                var anyway = Planned(vm);
                var anywayLinks = anyway.Where(c => c.Header?.Contains("anyway", StringComparison.Ordinal) == true).ToList();
                Check($"linker: 'Link with stock linker anyway' plans one stock link, unblocked, with a warning line ('{anywayLinks.FirstOrDefault()?.Header}')",
                    anywayLinks.Count == 1 && anywayLinks[0].CarryBlockedReason is null && anywayLinks[0].CarryMarker is null
                    && anywayLinks[0].Fallback is null && !anywayLinks[0].ReportsProgress
                    && BuildLogCodes.IsWarning(anywayLinks[0].Header!) && !anyway.Any(c => c.CarryBlockedReason is not null));
                var anywayLog = Real(Stock(stockLinker, anywayLinks.FirstOrDefault()?.Header));
                Replay(main, vm, "main-log-stock-anyway", anywayLog,
                    c => c.OnLinked is not null ? new LinkOutcome("stock", false, 0) : null, "linked with the stock linker");
                Check($"linker: the link-anyway build counts its warning ({vm.WarningCount})", vm.WarningCount >= 1);

                // The real question, through the shared message dialog; Esc declines.
                vm.ConfirmAsync = realConfirm;
                runs = Build.Runs;
                vm.BuildOnlyOnceCommand.Execute(null);
                var dialog = Owned<Avalonia.Controls.Window>(main);
                var texts = Find<Avalonia.Controls.TextBlock>(dialog).Select(t => t.Text ?? "").ToList();
                var buttons = Find<Avalonia.Controls.Button>(dialog).Select(b => b.Content as string ?? (b.Content as Avalonia.Controls.TextBlock)?.Text ?? "").ToList();
                Check($"linker: the dialog says what's left out and offers the stock linker by name ({string.Join(" / ", buttons)})",
                    texts.Contains(MainWindowViewModel.StockAnywayTitle) && texts.Any(t => t.Contains("leave those assets out", StringComparison.Ordinal))
                    && buttons.Contains(MainWindowViewModel.StockAnywayAction) && buttons.Contains("Cancel"));
                Shot(dialog, "dialog-stock-anyway");
                Key(dialog, Avalonia.Input.Key.Escape);
                Check("linker: Esc on the dialog declines, and nothing builds", WaitFor(() => !dialog.IsVisible) && Pump0() && Build.Runs == runs);
            }
            finally
            {
                vm.ConfirmAsync = realConfirm;
                settings.UseBbLinker = true;
                File.Delete(marker);
            }

            // 11. Dragon 1.0.0's single marker: .stockcarry.json counts when it lists carried assets or sound_aliases > 0.
            var folder = FakeInstall.CastleFolder;
            const string zone = "zm_castle_redux";
            BbLinker.StockDependency? With(string? carry, string? sound)
            {
                if (carry is null) File.Delete(marker); else File.WriteAllText(marker, carry);
                var soundFile = BbLinker.SoundMarkerPath(folder, zone);
                if (sound is null) File.Delete(soundFile); else File.WriteAllText(soundFile, sound);
                return BbLinker.StockDependencyOf(folder, zone);
            }
            try
            {
                var empty = With("{\"carry_contract\":1,\"carried\":[],\"sound_aliases\":0}", null);
                var soundsOnly = With("{\"carry_contract\":1,\"carried\":[],\"sound_aliases\":3}", null);
                var assets = With("{\"carry_contract\":1,\"carried\":[{\"type\":\"xmodel\",\"name\":\"m\",\"zone\":\"z\"}],\"sound_aliases\":2}", null);
                var broken = With("{ not json", null);
                var older = With(null, "{\"stock_sound_contract\":1,\"aliases\":[\"a\",\"b\"],\"entries\":[]}");
                var bothOld = With("{\"carry_contract\":1,\"carried\":[]}", "{\"stock_sound_contract\":1,\"entries\":[]}");
                Check($"linker: an empty .stockcarry.json is no dependency; carried assets or sound_aliases make one ({empty?.What ?? "none"}, {soundsOnly?.What}, {assets?.What})",
                    empty is null && soundsOnly is { SoundOnly: true, SoundAliases: 3, Capability: BbLinker.StockSoundCapability } && soundsOnly.Marker == marker
                    && assets is { SoundOnly: false, Assets: 1, Capability: BbLinker.StockCarryCapability });
                Check($"linker: an unreadable marker still counts (as stock assets); an older Dragon's .stocksound.json alone still counts ({older?.What}, {bothOld?.What})",
                    broken is { SoundOnly: false, Assets: -1 } && older is { SoundOnly: true, SoundAliases: 2 } && older.What == "2 stock sounds"
                    && bothOld is { SoundOnly: true } && bothOld.Marker.EndsWith(".stocksound.json", StringComparison.Ordinal));

                var soundReason = BbLinker.CarryBlockedReason(true, false, true, true, soundsOnly);
                var assetReason = BbLinker.CarryBlockedReason(true, false, true, true, assets);
                Check($"linker: a sound-only map's refusal names its sounds and Apex's Stock sounds view, not 'Create a local copy' ('{soundReason}')",
                    soundReason!.Contains("3 stock sounds", StringComparison.Ordinal) && soundReason.EndsWith(BbLinker.SoundHint, StringComparison.Ordinal)
                    && !soundReason.Contains("Create a local copy", StringComparison.Ordinal)
                    && assetReason!.EndsWith(BbLinker.CarryHint, StringComparison.Ordinal)
                    && BbLinker.CarryFailureMessage(BbLinker.ExitStockCarry, soundsOnly).Contains("stock sound", StringComparison.Ordinal)
                    && !BbLinker.CarryFailureMessage(BbLinker.ExitStockCarry, soundsOnly).Contains("Create a local copy", StringComparison.Ordinal));

                // The question and the link-anyway line say the same for a sound-only map.
                With("{\"carry_contract\":1,\"carried\":[],\"sound_aliases\":3}", null);
                settings.UseBbLinker = false;
                string? askedSound = null;
                vm.ConfirmAsync = (_, message, _) =>
                {
                    askedSound = message;
                    return Task.FromResult(true);
                };
                var soundAnyway = Planned(vm).FirstOrDefault(c => c.Header?.Contains("anyway", StringComparison.Ordinal) == true);
                Check($"linker: for a sound-only map the question and the warning name its sounds ('{askedSound}' / '{soundAnyway?.Header}')",
                    askedSound == "This map uses 3 stock sounds carried by Dragon. Install Dragon or switch it on; linking with the stock linker will leave those sounds out. Apex's Stock sounds view can make local copies of them."
                    && soundAnyway?.Header?.EndsWith("anyway: the 3 stock sounds Dragon carried for this map are left out.", StringComparison.Ordinal) == true);
            }
            finally
            {
                vm.ConfirmAsync = realConfirm;
                settings.UseBbLinker = true;
                File.Delete(marker);
                File.Delete(BbLinker.SoundMarkerPath(folder, zone));
            }

            // 10. Dragon installed while Blackbird runs: the menu re-reads it (on window focus and before each build).
            Environment.SetEnvironmentVariable(BbLinker.OverrideVariable, missing);
            vm.RefreshBbLinker();
            var wasInstalled = vm.IsBbLinkerInstalled;
            var raised = new List<string>();
            void OnChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e) => raised.Add(e.PropertyName ?? "");
            vm.PropertyChanged += OnChanged;
            Environment.SetEnvironmentVariable(BbLinker.OverrideVariable, dragon);
            vm.RefreshBbLinker();
            vm.PropertyChanged -= OnChanged;
            Check($"linker: re-detecting Dragon enables the menu item without a restart ({string.Join(", ", raised)})",
                !wasInstalled && vm.IsBbLinkerInstalled && vm.IsLinkingWithBbLinker
                && raised.Contains(nameof(MainWindowViewModel.IsBbLinkerInstalled)) && raised.Contains(nameof(MainWindowViewModel.IsLinkingWithBbLinker)));

            // The shots: each log through the main window, with the status line naming the linker.
            Environment.SetEnvironmentVariable(BbLinker.OverrideVariable, dragon);
            settings.UseBbLinker = true;
            Replay(main, vm, "main-log-dragon", installed, c => c.ReportsProgress ? new LinkOutcome("bb-linker", false, 0) : null,
                "linked with Dragon");
            Replay(main, vm, "main-log-dragon-dev", dev, c => c.ReportsProgress ? new LinkOutcome("bb-linker", false, 0) : null,
                "linked with Dragon");
            Replay(main, vm, "main-log-fallback", fallback, c => c.ReportsProgress ? new LinkOutcome("stock", true, 0) : null,
                "linked with the stock linker (bb-linker fell back)");
            settings.UseBbLinker = false;
            Replay(main, vm, "main-log-stock-switched-off", off, c => c.OnLinked is not null ? new LinkOutcome("stock", false, 0) : null,
                "linked with the stock linker");
            Environment.SetEnvironmentVariable(BbLinker.OverrideVariable, missing);
            settings.UseBbLinker = true;
            Replay(main, vm, "main-log-dragon-missing", gone, c => c.OnLinked is not null ? new LinkOutcome("stock", false, 0) : null,
                "linked with the stock linker");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BbLinker.OverrideVariable, prior);
            settings.UseBbLinker = priorSetting;
            if (markerText is not null)
                File.WriteAllText(marker, markerText);
            Build.Links = null;
            Build.Tail = null;
            Build.Result = true;
        }
    }

    private static void StatusNotes()
    {
        LinkOutcome Bb(int exit = 0) => new("bb-linker", false, exit);
        LinkOutcome Stock(bool fellBack = false) => new("stock", fellBack, 0);
        var table = new (LinkOutcome[] Links, string? Expect)[]
        {
            ([], null),
            ([new LinkOutcome("stock", false, BbLinker.ExitStockCarry, Ran: false)], null),
            ([Bb()], "linked with Dragon"),
            ([Bb(BbLinker.ExitStockCarry)], "linked with Dragon"),
            ([Stock()], "linked with the stock linker"),
            ([Stock(fellBack: true)], "linked with the stock linker (bb-linker fell back)"),
            ([Bb(), Stock()], "linked with Dragon and the stock linker"),
            ([Bb(), Stock(fellBack: true)], "linked with Dragon and the stock linker (bb-linker fell back)"),
        };
        var wrong = table.Where(t => MainWindowViewModel.DescribeLinkers(t.Links) != t.Expect)
            .Select(t => $"[{string.Join(",", t.Links.Select(l => l.Linker))}] → '{MainWindowViewModel.DescribeLinkers(t.Links)}'").ToList();
        Check($"linker: the status line's note names the linker that ran ({(wrong.Count == 0 ? "all eight rows" : string.Join("; ", wrong))})",
            wrong.Count == 0);
    }

    // A bb-linker that meets something it can't convert yet and hands the link to the stock linker (0x0BB10001).
    private const string HandOverScript = """
        @echo off
        if "%~1"=="-capabilities" goto caps
        echo ^^7Linking zone zm_castle_redux
        echo ^^7Dragon: zone zm_castle_redux uses an asset type this build can't convert yet
        exit /b %FAKE_BBL_EXIT%
        :caps
        echo {"id":"dragon","version":"0.1.0","capabilities":["stock-carry"],"carry_contract":1}
        exit /b 0
        """;

    private static BuildCommand Dragon(string exe, int exit, string stockLinker) =>
        new(exe, ["-modsource", "zm_castle_redux"], "Link zm_castle_redux (bb-linker)")
        {
            ReportsProgress = true,
            Environment = new Dictionary<string, string> { ["FAKE_BBL_EXIT"] = exit.ToString() },
            Fallback = new BuildCommand(stockLinker, ["-modsource", "zm_castle_redux"], "Link zm_castle_redux"),
        };

    private static BuildCommand Stock(string exe, string? header) =>
        new(exe, ["-modsource", "zm_castle_redux"], "Link zm_castle_redux") { Header = header };

    private static string Real(params BuildCommand[] steps)
    {
        var log = new Collect();
        Task.Run(() => new BuildService().RunBuildAsync([.. steps], false, log, CancellationToken.None)).GetAwaiter().GetResult();
        return log.ToString().ReplaceLineEndings("\n");
    }

    private static bool Pump0()
    {
        Pump(100);
        return true;
    }

    private static string FirstLine(string log, string containing) =>
        log.Split('\n').FirstOrDefault(l => l.Contains(containing, StringComparison.Ordinal)) ?? "";

    private static List<BuildCommand> Planned(MainWindowViewModel vm)
    {
        Build.Result = true;
        vm.BuildOnlyOnceCommand.Execute(null);
        WaitFor(() => vm.IsBuildRunning);
        WaitFor(() => !vm.IsBuildRunning);
        Pump(100);
        return Build.LastCommands;
    }

    private static void Replay(MainWindow main, MainWindowViewModel vm, string shot, string log,
        Func<BuildCommand, LinkOutcome?> links, string expectNote)
    {
        // The real build service's lines, minus the command lines (the stand-in linkers' paths), then the shot.
        Build.Tail = log.Split('\n').Where(l => l.Length > 0 && !l.Contains(".cmd", StringComparison.OrdinalIgnoreCase) || l.Contains("dev build", StringComparison.Ordinal)).ToArray();
        Build.Links = links;
        Build.Result = true;
        vm.BuildOnlyOnceCommand.Execute(null);
        WaitFor(() => vm.IsBuildRunning);
        WaitFor(() => !vm.IsBuildRunning);
        Pump(250);
        Check($"linker: {shot} lands with the status line naming the linker ('{vm.StatusLine}')",
            vm.StatusLine.EndsWith(" · " + expectNote, StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(OutDir, shot + ".txt"), log);
        Shot(main, shot);
        Build.Links = null;
    }
}
