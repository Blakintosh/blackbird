using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Blackbird.Models;
using Blackbird.Services;
using Blackbird.ViewModels;
using Blackbird.Views;
using static Blackbird.Shots.Program;

namespace Blackbird.Shots;

/// <summary>What gets shot and checked, in order. One main window lives throughout; dialogs open over it through the app's own code.</summary>
internal static class Scenarios
{
    private static MainWindow _main = null!;
    private static MainWindowViewModel _vm = null!;
    private static ISettingsService _settings = null!;

    public static void Run()
    {
        // As in the app: Progress<T> reports and awaits started here come back to the UI thread, in order.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new Avalonia.Threading.AvaloniaSynchronizationContext());

        _settings = App.Services.GetRequiredService<ISettingsService>();
        _vm = App.Services.GetRequiredService<MainWindowViewModel>();
        // BLACKBIRD_SHOTS_THEME=light|slate|graphite renders every shot in that theme.
        // The fake data folder keeps settings between runs, so the theme is always set: Graphite unless asked for another
        // (headless Windows reports light, so "Match Windows" would shoot Light).
        _vm.SetThemeCommand.Execute(Environment.GetEnvironmentVariable("BLACKBIRD_SHOTS_THEME") is { Length: > 0 } theme
            ? AppTheme.Parse(theme)
            : ThemeChoice.Graphite);
        _main = new MainWindow { DataContext = _vm };
        _main.Show();
        _main.Activate();

        WaitFor(() => _vm.HasSelectedProject && !_vm.IsProjectScanRunning && _vm.Output.Length > 0, 8000);
        Check($"startup: restores the last project with its saved log ({_vm.SelectedProject?.Name}, {_vm.ErrorCount} errors)",
            _vm.SelectedProject?.Name == "zm_castle_redux" && _vm.Output.Text.Contains("^1ERROR"));

        FileSafety();
        MainWindow();
        Menus();
        UpdateScenario.Run(_main, _vm.Updates);
        ThemeScenario.Run(_main, _vm, _settings);
        LogFind();
        LogFollows();
        Building();
        CommandPalette();
        LaunchSettings();
        ProjectInfoAndRename();
        OtherProjectDialogs();
        SetupDialogs();
        Messages();
        Publish();
        EmptyStates();
    }

    private static void SelectCastle()
    {
        _vm.SelectProjectByName("zm_castle_redux", ProjectType.Map);
        WaitFor(() => _vm.SelectedProject?.Name == "zm_castle_redux" && _vm.Output.Length > 0);
        Pump(100);
    }

    private static void Refresh()
    {
        var refresh = _vm.RefreshSetupAsync();
        WaitFor(() => refresh.IsCompleted, 8000);
        Pump(150);
    }

    // ── File safety (no UI) ─────────────────────────────────────────────────

    private static void FileSafety()
    {
        // Text files keep their bytes: ANSI stays ANSI, a BOM stays, mixed line endings stay mixed.
        byte[] ansi = [.. "// caf"u8, 0xE9, .. "\r\nzm_castle_redux\n"u8];
        byte[] bom = [0xEF, 0xBB, 0xBF, .. "zm_castle_redux \u00e9\r\n"u8];
        var ansiRoundTrip = TextFiles.Decode(ansi) is var a && TextFiles.Encode(a.Text, a.Encoding).AsSpan().SequenceEqual(ansi);
        var bomRoundTrip = TextFiles.Decode(bom) is var b && TextFiles.Encode(b.Text, b.Encoding).AsSpan().SequenceEqual(bom);
        Check($"files: ANSI and UTF-8 with BOM round-trip byte for byte (ansi {ansiRoundTrip}, bom {bomRoundTrip})",
            ansiRoundTrip && bomRoundTrip);

        // GDT cleanup: only copies identical to the first are removed, and `"x" ( "t" ) {` blocks end where they end.
        var folder = Path.Combine(FakeInstall.Root, "gdt-cleanup");
        Directory.CreateDirectory(folder);
        const string first = "{\r\n\t\"dup_same\" ( \"xmodel.gdf\" )\r\n\t{\r\n\t\t\"filename\" \"a.xmodel_bin\"\r\n\t}\r\n"
            + "\t\"dup_diff\" ( \"xmodel.gdf\" )\r\n\t{\r\n\t\t\"filename\" \"first.xmodel_bin\"\r\n\t}\r\n"
            + "\t\"inline_same\" ( \"xmodel.gdf\" ) {\r\n\t\t\"filename\" \"c.xmodel_bin\"\r\n\t}\r\n}\r\n";
        const string second = "{\n\t\"dup_same\" ( \"xmodel.gdf\" )\n\t{\n\t\t\"filename\" \"a.xmodel_bin\"\r\n\t}\n"
            + "\t\"dup_diff\" ( \"xmodel.gdf\" )\n\t{\n\t\t\"filename\" \"second.xmodel_bin\"\n\t}\n"
            + "\t\"inline_same\" ( \"xmodel.gdf\" ) {\n\t\t\"filename\" \"c.xmodel_bin\"\n\t}\n}\n";
        File.WriteAllText(Path.Combine(folder, "a.gdt"), first);
        File.WriteAllText(Path.Combine(folder, "b.gdt"), second);

        var analysis = typeof(ProjectAnalysisService);
        var find = analysis.GetMethod("FindDuplicateGdtAssetRemovals", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var removals = ((System.Collections.IEnumerable)find.Invoke(null, [folder])!).Cast<object>()
            .Select(r => (
                File: Path.GetFileName((string)r.GetType().GetProperty("FilePath")!.GetValue(r)!),
                Name: (string)r.GetType().GetProperty("Name")!.GetValue(r)!,
                Start: (int)r.GetType().GetProperty("StartLine")!.GetValue(r)!,
                End: (int)r.GetType().GetProperty("EndLine")!.GetValue(r)!))
            .OrderBy(r => r.Start)
            .ToList();
        Check($"gdt cleanup: removes only identical later copies, never a differing one ({string.Join(", ", removals.Select(r => $"{r.File}:{r.Name}:{r.Start}-{r.End}"))})",
            removals.Count == 2
            && removals.All(r => r.File == "b.gdt")
            && removals[0] is { Name: "dup_same", Start: 2, End: 5 }
            && removals[1] is { Name: "inline_same", Start: 10, End: 12 });

        var split = analysis.GetMethod("SplitLinesKeepingEndings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var lines = (List<string>)split.Invoke(null, [second])!;
        lines.RemoveRange(1, 4);
        Check("gdt cleanup: removing a block keeps every other line's own ending (mixed CRLF/LF)",
            string.Concat(lines) == second.Remove(second.IndexOf("\t\"dup_same", StringComparison.Ordinal),
                second.IndexOf("\t\"dup_diff", StringComparison.Ordinal) - second.IndexOf("\t\"dup_same", StringComparison.Ordinal)));
    }

    // ── Main window ─────────────────────────────────────────────────────────

    private static void MainWindow()
    {
        Pump(300);
        Shot(_main, "main-project");
        Shot150(_main, "main-project@1.5x");
    }

    private static void Menus()
    {
        var launch = _main.FindControl<Button>("LaunchConfigButton")!;
        var flyout = OpenFlyout(launch);
        Shot(_main, "menu-launch-options");
        flyout.Hide();

        var preset = _main.FindControl<Button>("BuildPresetButton")!;
        flyout = OpenFlyout(preset);
        Shot(_main, "menu-build-preset");
        flyout.Hide();

        flyout = OpenFlyout(ButtonNamed(_main, "Project actions"));
        Shot(_main, "menu-project-actions");
        flyout.Hide();

        flyout = OpenFlyout(_main.FindControl<Button>("ProjectPickerButton")!);
        Pump(100);
        Shot(_main, "menu-project-picker");

        // Arrows browse; only Enter (or a click) opens the highlighted project. The list lives in
        // the flyout's own popup, so the keys go to it directly.
        var list = _main.FindControl<ListBox>("ProjectPickerList")!;
        list.Focus(NavigationMethod.Directional);
        var highlighted = _vm.PickerSelection;
        KeyOn(list, Avalonia.Input.Key.Down);
        KeyOn(list, Avalonia.Input.Key.Down);
        Check($"picker: arrow keys move the highlight without switching project ({highlighted?.Name} → {_vm.PickerSelection?.Name}, still {_vm.SelectedProject?.Name})",
            !ReferenceEquals(highlighted, _vm.PickerSelection) && _vm.SelectedProject?.Name == "zm_castle_redux" && flyout.IsOpen);
        _vm.PickerSelection = _vm.Projects.First(p => p is { IsGroupHeader: false, Name: "zm_foundry" });
        KeyOn(list, Avalonia.Input.Key.Enter);
        Check($"picker: Enter opens the highlighted project and closes the picker ({_vm.SelectedProject?.Name})",
            _vm.SelectedProject?.Name == "zm_foundry" && !flyout.IsOpen);

        flyout.Hide();
        SelectCastle();

        // Enter pressed before the typing pause ends still opens what was typed, not the old first row.
        _vm.ProjectSearchText = "foundry";
        var match = _vm.BestPickerMatch();
        Check($"picker: Enter straight after typing picks the match for what's typed ({match?.Name})", match?.Name == "zm_foundry");
        _vm.ProjectSearchText = "";
        Pump(200);
    }


    private static void LogFind()
    {
        var log = _main.FindControl<ColoredLogView>("BuildLogView")!;
        var find = Find<TextBox>(log).First(t => t.Name == "SearchBox");
        _main.FindControl<Button>("BuildPresetButton")!.Focus();
        Pump();

        Key(_main, Avalonia.Input.Key.F, RawInputModifiers.Control);
        Check("log: Ctrl+F puts the cursor in the log's find field", find.IsFocused);
        Key(_main, Avalonia.Input.Key.F, RawInputModifiers.Control);
        Check("log: Ctrl+F again keeps it there (it never toggles find closed)", find.IsFocused && find.IsEffectivelyVisible);

        Type(_main, "ERROR");
        Pump(300);
        Shot(_main, "main-log-find");
        Key(_main, Avalonia.Input.Key.Escape);
        Check("log: Esc clears the find", string.IsNullOrEmpty(find.Text));

        // Switching to a project with a long saved log: it renders off the UI thread, so no frame
        // stalls past the switch budget, and the whole log, with its counts, still arrives.
        var original = _vm.Output.Text;
        var lines = Enumerable.Range(0, 40_000).Select(i => i % 40 == 0
            ? $"^1ERROR: xmodel 'p7_asset_{i}' is referenced by zone_source/zm_castle_redux.zone but wasn't found in any GDT"
            : $"Linking xmodel p7_asset_{i} into zone zm_castle_redux ^3(streamed)^7 ok");
        var big = string.Join("\n", lines) + "\n";
        var errorsChip = Find<TextBlock>(log).First(t => t.Name == "ErrorsChipText");
        var editor = Find<AvaloniaEdit.TextEditor>(log).First();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastTick = 0, longestStall = 0;
        var ticker = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(1), Avalonia.Threading.DispatcherPriority.Send, (_, _) =>
        {
            longestStall = Math.Max(longestStall, clock.ElapsedMilliseconds - lastTick);
            lastTick = clock.ElapsedMilliseconds;
        });
        lastTick = clock.ElapsedMilliseconds;
        _vm.Output.Replace(big);
        longestStall = Math.Max(longestStall, clock.ElapsedMilliseconds - lastTick);
        lastTick = clock.ElapsedMilliseconds;
        var arrived = WaitFor(() => errorsChip.Text == "1,000 errors" && editor.Document.LineCount == 40_000, 5000);
        ticker.Stop();
        Check($"log: a {big.Length / 1_000_000.0:0.0} MB log arrives whole ({editor.Document.LineCount:N0} lines, '{errorsChip.Text}') with no UI stall over 50 ms (longest {longestStall} ms)",
            arrived && longestStall <= 50);
        _vm.Output.Replace(original);
        Pump(100);

    }

    /// <summary>A build's output arriving in bursts keeps the log pinned to its end.</summary>
    private static void LogFollows()
    {
        var log = _main.FindControl<ColoredLogView>("BuildLogView")!;
        var editor = Find<AvaloniaEdit.TextEditor>(log).First();
        var original = _vm.Output.Text;
        _vm.Output.Replace("");
        Pump(200);

        var missed = 0;
        var worst = 0.0;
        for (var burst = 0; burst < 40; burst++)
        {
            var lines = Enumerable.Range(0, 30).Select(i => $"Linking xmodel p7_asset_{burst}_{i} into zone zm_castle_redux ^3(streamed)^7 ok");
            _vm.Output.Append(string.Join('\n', lines) + '\n');
            Pump(150);
            var gap = Math.Max(0, editor.ExtentHeight - editor.ViewportHeight) - editor.VerticalOffset;
            worst = Math.Max(worst, gap);
            Console.WriteLine($"  burst {burst}: offset {editor.VerticalOffset:0} extent {editor.ExtentHeight:0} viewport {editor.ViewportHeight:0} gap {gap:0}");
            if (gap > 20)
                missed++;
        }
        Check($"log: a streaming build stays pinned to the end ({missed} of 40 bursts left behind, worst gap {worst:0} px)", missed == 0);
        _vm.Output.Replace(original);
        Pump(100);
    }

    private static void Building()
    {
        Build.Gate = new TaskCompletionSource();
        _vm.BuildOnlyOnceCommand.Execute(null);
        WaitFor(() => _vm.IsBuildRunning && _vm.Output.Text.Contains("Compiling BSP"));
        Pump(250);
        Check("build: Ctrl+B's command starts a build that streams into the log", _vm.IsBuildRunning);
        Check($"status bar: a running build shows once, as the progress line, with no glyph beside it ('{_vm.StatusKind}')", _vm.StatusKind == "");
        Check("picker: switching project waits for the build, and the picker says so", !_vm.CanSwitchProject);
        Shot(_main, "main-building");

        Key(_main, Avalonia.Input.Key.K, RawInputModifiers.Control);
        var palette = Owned<CommandPaletteDialog>(_main);
        var keys = ((CommandPaletteDialogViewModel)palette.DataContext!).Items.Select(e => e.ActionKey).ToHashSet();
        Check($"palette: mid-build it lists no action that can't run (build {keys.Contains("build")}, run {keys.Contains("run")}, rename {keys.Contains("rename-project")})",
            !keys.Contains("build") && !keys.Contains("build-run-once") && !keys.Contains("run") && !keys.Contains("rename-project")
            && keys.Contains("analyze-project"));
        Key(palette, Avalonia.Input.Key.Escape);
        WaitFor(() => !palette.IsVisible);

        _settings.RecentCommandActions.Clear();
        Invoke(_main, "ExecuteCommandPaletteEntryAsync", new CommandPaletteEntry { ActionKey = "build", Name = "Build", Detail = "", TypeLabel = "" });
        Check("palette: an action that couldn't run isn't recorded as recent", !_settings.RecentCommandActions.Contains("build"));

        Build.Gate.SetResult();
        Build.Gate = null;
        WaitFor(() => !_vm.IsBuildRunning);
        Pump(150);
        Check($"build: finishing counts the log's errors and warnings ({_vm.ErrorCount}, {_vm.WarningCount})",
            _vm.ErrorCount == 2 && _vm.WarningCount == 3);
        Check($"status bar: errors show the error glyph on a neutral bar ({_vm.StatusKind})", _vm.StatusKind == "error");

        // The same build with the errors fixed: it succeeds with warnings.
        Build.Tail = FakeInstall.BuildLogTailWarningsOnly;
        _vm.BuildOnlyOnceCommand.Execute(null);
        WaitFor(() => _vm.IsBuildRunning);
        WaitFor(() => !_vm.IsBuildRunning);
        Pump(150);
        Check($"status bar: a build with only warnings shows the warning glyph ({_vm.StatusLine}, {_vm.StatusKind})",
            _vm.StatusKind == "warning" && _vm.ErrorCount == 0);
        Shot(_main, "main-build-warnings");

        _vm.ShowTransientStatus("Copied to clipboard.");
        Pump();
        Check($"status bar: a passing message drops the outcome glyph ('{_vm.StatusKind}')", _vm.StatusKind == "");

        // Back to the log with errors, which the rest of the run expects.
        Build.Tail = null;
        _vm.BuildOnlyOnceCommand.Execute(null);
        WaitFor(() => _vm.IsBuildRunning);
        WaitFor(() => !_vm.IsBuildRunning);
        Pump(150);
    }

    // ── Command palette ─────────────────────────────────────────────────────

    private static void CommandPalette()
    {
        Key(_main, Avalonia.Input.Key.K, RawInputModifiers.Control);
        var palette = Owned<CommandPaletteDialog>(_main);
        var entries = ((CommandPaletteDialogViewModel)palette.DataContext!).Items;
        var newMap = entries.FirstOrDefault(e => e.ActionKey == "new-map");
        var project = entries.FirstOrDefault(e => e.Project is not null);
        Check($"palette: actions carry no badge and show their shortcut as keys ({newMap?.TypeLabel}|{string.Join(" ", newMap?.ShortcutKeys ?? [])}|{newMap?.Detail})",
            newMap is { HasTypeLabel: false, Shortcut: "Ctrl+N" } && !newMap.Detail.Contains("Ctrl"));
        Check($"palette: projects carry a MAP/MOD badge ({project?.TypeLabel})", project?.TypeLabel is "MAP" or "MOD");
        Shot(palette, "command-palette");

        Type(palette, "castle");
        Pump(100);
        var top = ((CommandPaletteDialogViewModel)palette.DataContext!).Items.FirstOrDefault();
        Check($"palette: a project's name beats actions that merely mention it ('castle' → {top?.Name})", top?.Project?.Name == "zm_castle_redux");
        for (var i = 0; i < "castle".Length; i++)
            Key(palette, Avalonia.Input.Key.Back);

        Type(palette, "foundry");
        Pump(100);
        Shot(palette, "command-palette-search");


        var list = palette.FindControl<ListBox>("ResultsList")!;
        var entry = list.Items.OfType<CommandPaletteEntry>().FirstOrDefault(e => e.Project?.Name == "zm_foundry");
        var row = entry is null ? null : list.ContainerFromItem(entry) as Control;
        if (row is null)
        {
            Check("palette: 'foundry' finds the Foundry map", false);
            palette.Close();
            return;
        }

        Click(row);
        WaitFor(() => !palette.IsVisible && _vm.SelectedProject?.Name == "zm_foundry");

        var wasOnline = _vm.LaunchOnline;
        Invoke(_main, "ExecuteCommandPaletteEntryAsync", new CommandPaletteEntry { ActionKey = "set-launch-offline", Name = "Launch offline", Detail = "", TypeLabel = "" });
        Check("palette: an action that ran is recorded as recent", _settings.RecentCommandActions.FirstOrDefault() == "set-launch-offline");
        _vm.LaunchOnline = wasOnline;
        Check($"palette: one click on a row runs it (selected: {_vm.SelectedProject?.Name})",
            !palette.IsVisible && _vm.SelectedProject?.Name == "zm_foundry");
        SelectCastle();
    }

    // ── Launch settings ─────────────────────────────────────────────────────

    private static void LaunchSettings()
    {
        Invoke(_main, "ShowLaunchSettingsAsync");
        var dialog = Owned<DvarsDialog>(_main);
        var dvm = (DvarsDialogViewModel)dialog.DataContext!;
        Shot(dialog, "launch-settings");
        var setCount = dvm.Dvars.Count;

        var search = dialog.FindControl<TextBox>("SearchBox")!;
        search.Focus();
        Type(dialog, "fov");
        var before = dvm.Dvars.Count;
        Pump(250);
        Check($"launch settings: typing 'fov' filters the catalog after the pause ({before} rows while typing, {dvm.Dvars.Count} after)",
            before == setCount && dvm.Dvars.Count > 0 && dvm.Dvars.All(d =>
                d.Name.Contains("fov", StringComparison.OrdinalIgnoreCase) || d.Description.Contains("fov", StringComparison.OrdinalIgnoreCase)));
        Shot(dialog, "launch-settings-search");

        Key(dialog, Avalonia.Input.Key.Escape);
        Check($"launch settings: Esc clears the search first and keeps the dialog open ({dvm.Dvars.Count} rows)",
            dialog.IsVisible && dvm.DvarSearchText.Length == 0 && dvm.Dvars.Count == setCount);

        Type(dialog, "zzqx");
        Pump(250);
        Check($"launch settings: no match says so ('{dvm.EmptyMessage}')", dvm.IsEmpty);
        Shot(dialog, "launch-settings-no-match");
        Key(dialog, Avalonia.Input.Key.Escape);

        // A value typed into a row commits on lost focus; Enter saves without leaving the field.
        var developer = Find<TextBox>(dialog).First(t => t.DataContext is Dvar { Name: "developer" } && t.IsEffectivelyVisible);
        developer.Focus();
        developer.SelectAll();
        Type(dialog, "1");
        Key(dialog, Avalonia.Input.Key.Enter);
        WaitFor(() => !dialog.IsVisible);
        var saved = _settings.Projects.GetValueOrDefault("map:zm_castle_redux")?.EnvironmentDvars.GetValueOrDefault("Dev");
        Check($"launch settings: Enter saves a value typed without leaving the field (developer = {saved?.GetValueOrDefault("developer")})",
            !dialog.IsVisible && saved?.GetValueOrDefault("developer") == "1");

        // Launch config needs a project; with none it doesn't open (its menu entry is disabled).
        Invoke(_main, "ShowLaunchSettingsAsync");
        dialog = Owned<DvarsDialog>(_main);
        var removeNames = Find<Button>(dialog)
            .Where(b => b.IsEffectivelyVisible && b.DataContext is Dvar && b.Content is TextBlock)
            .Select(b => Avalonia.Automation.AutomationProperties.GetName(b) ?? "")
            .ToList();
        Check($"launch config: each remove button names its dvar ({string.Join(", ", removeNames.Take(2))})",
            removeNames.Count > 0 && removeNames.All(n => n.StartsWith("Remove ", StringComparison.Ordinal) && n.Length > 7)
            && removeNames.Contains("Remove developer"));
        Key(dialog, Avalonia.Input.Key.Escape);
        Check("launch settings: Esc with no search cancels", WaitFor(() => !dialog.IsVisible));
    }

    // ── Project dialogs ─────────────────────────────────────────────────────

    private static void ProjectInfoAndRename()
    {
        Invoke(_main, "ShowProjectDetailsAsync");
        var info = Owned<ProjectDetailsDialog>(_main);
        var displayName = info.FindControl<TextBox>("DisplayNameBox")!;
        Check("project info: opens in Display name with its text selected",
            displayName.IsFocused && displayName.SelectionStart == 0 && displayName.SelectionEnd == (displayName.Text ?? "").Length);
        Shot(info, "project-info");

        Click(Find<Button>(info).First(b => b.IsEffectivelyVisible && (b.Content?.ToString() ?? "").StartsWith("Rename", StringComparison.Ordinal)));
        var rename = Owned<RenameProjectDialog>(info);
        var name = rename.FindControl<TextBox>("NameBox")!;
        Check("rename: opens with the current name focused and selected",
            name.IsFocused && name.SelectionStart == 0 && name.SelectionEnd == "zm_castle_redux".Length);
        Shot(rename, "rename-project");

        Type(rename, "zm_foundry");
        Key(rename, Avalonia.Input.Key.Enter);
        Pump(100);
        var rvm = (RenameProjectDialogViewModel)rename.DataContext!;
        Check($"rename: a taken name keeps the dialog open with the reason ('{rvm.NameError}')",
            rename.IsVisible && rvm.NameError is not null && Directory.Exists(FakeInstall.CastleFolder));
        Shot(rename, "rename-project-error");

        name.SelectAll();
        Type(rename, FakeInstall.StrayMapSourceName);
        Key(rename, Avalonia.Input.Key.Enter);
        Pump(100);
        Check($"rename: a name whose map_source .map belongs to another map is refused inline ('{rvm.NameError}')",
            rename.IsVisible && rvm.NameError?.Contains("map_source", StringComparison.Ordinal) == true
            && Directory.Exists(FakeInstall.CastleFolder)
            && File.ReadAllText(FakeInstall.StrayMapSourcePath) == FakeInstall.StrayMapSourceText);

        Key(rename, Avalonia.Input.Key.Escape);
        WaitFor(() => !rename.IsVisible);

        // The save runs inside the dialog: when it fails, the dialog stays with the edits and says why.
        var ivm = (ProjectDetailsDialogViewModel)info.DataContext!;
        var realSave = ivm.SaveAsync;
        ivm.SaveAsync = () => Task.FromException(new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)));
        displayName.Focus();
        displayName.SelectAll();
        Type(info, "Castle Redux Remastered");
        Key(info, Avalonia.Input.Key.Enter);
        WaitFor(() => !ivm.IsBusy);
        Pump(100);
        Check($"project info: a failed save keeps it open with the edits and the reason inline ('{ivm.ErrorMessage}')",
            info.IsVisible && ivm.ErrorMessage == "Couldn't save the project info. The disk is full."
            && ivm.DisplayName == "Castle Redux Remastered");
        Shot(info, "project-info-save-error");

        ivm.SaveAsync = realSave;
        Key(info, Avalonia.Input.Key.Escape);
        WaitFor(() => !info.IsVisible);
    }

    private static void OtherProjectDialogs()
    {
        Invoke(_main, "DuplicateSelectedProjectAsync");
        var duplicate = Owned<DuplicateProjectDialog>(_main);
        Shot(duplicate, "duplicate-project");
        var dvm = (DuplicateProjectDialogViewModel)duplicate.DataContext!;
        var newName = duplicate.FindControl<TextBox>("NameBox")!;
        newName.Focus();
        newName.SelectAll();
        Type(duplicate, FakeInstall.StrayMapSourceName);
        Key(duplicate, Avalonia.Input.Key.Enter);
        Pump(100);
        Check($"duplicate: a name whose map_source .map belongs to another map is refused inline ('{dvm.NameError}')",
            duplicate.IsVisible && dvm.NameError?.Contains("map_source", StringComparison.Ordinal) == true
            && !Directory.Exists(Path.Combine(FakeInstall.GamePath, "usermaps", FakeInstall.StrayMapSourceName))
            && File.ReadAllText(FakeInstall.StrayMapSourcePath) == FakeInstall.StrayMapSourceText);
        // A real copy: it must come away with no Workshop identity, or publishing it would update the original's item.
        newName.SelectAll();
        Type(duplicate, "zm_castle_copy");
        Key(duplicate, Avalonia.Input.Key.Enter);
        WaitFor(() => !duplicate.IsVisible, 8000);
        var copy = Path.Combine(FakeInstall.GamePath, "usermaps", "zm_castle_copy");
        var copyJson = Path.Combine(copy, "zone", "workshop.json");
        var copiedId = File.Exists(copyJson) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(copyJson))?["PublisherID"]?.ToString() : null;
        Check($"duplicate: the copy keeps no Workshop item ID or versions file (PublisherID '{copiedId}')",
            Directory.Exists(copy)
            && string.IsNullOrEmpty(copiedId)
            && !File.Exists(Path.Combine(copy, WorkshopFiles.ProfilesName))
            && !File.Exists(Path.Combine(copy, "zone", WorkshopFiles.ProfilesName))
            && !Directory.Exists(Path.Combine(copy, WorkshopFiles.MediaFolderName)));
        SelectCastle();

        _vm.NewItemCommand!.Execute(null);

        var newItem = Owned<NewItemDialog>(_main);
        Shot(newItem, "new-item-map");
        var mapHeight = newItem.Bounds.Height;
        var nvm = (NewItemDialogViewModel)newItem.DataContext!;
        nvm.SelectedProjectKindIndex = 1;
        Pump(100);
        Shot(newItem, "new-item-mod");
        var modHeight = newItem.Bounds.Height;
        nvm.CreateCoreZone = nvm.CreateZombiesZone = false;
        nvm.ValidateAll();
        Pump(100);
        Shot(newItem, "new-item-mod-zone-error");
        var zoneErrorHeight = newItem.Bounds.Height;
        nvm.SelectedProjectKindIndex = 0;
        Pump(100);
        Check($"new item: switching Map and Mod, or showing a zone error, never changes the height ({mapHeight} map, {modHeight} mod, {zoneErrorHeight} with error, {newItem.Bounds.Height} map again)",
            mapHeight == modHeight && modHeight == zoneErrorHeight && newItem.Bounds.Height == mapHeight);
        Key(newItem, Avalonia.Input.Key.Escape);
        WaitFor(() => !newItem.IsVisible);

        Invoke(_main, "AnalyzeSelectedProjectAsync");
        var analysis = Owned<ProjectAnalysisDialog>(_main);
        var avm = (ProjectAnalysisDialogViewModel)analysis.DataContext!;
        WaitFor(() => !avm.IsBusy, 8000);
        Pump(150);
        Shot(analysis, "project-analysis");

        var search = Find<TextBox>(analysis).First(t => t.Watermark == "Search findings");
        search.Focus();
        var allFindings = avm.Issues.Count;
        Type(analysis, "zzqx");
        var whileTyping = avm.Issues.Count;
        Pump(250);
        var afterPause = avm.Issues.Count;
        search.Text = "";
        Pump();
        Check($"analysis: search filters once typing pauses, clearing applies at once ({allFindings} → {whileTyping} while typing → {afterPause} → {avm.Issues.Count})",
            allFindings > 0 && whileTyping == allFindings && afterPause == 0 && avm.Issues.Count == allFindings);
        analysis.Close();

        Invoke(_main, "ShowExport2RustAsync");
        var export = Owned<Export2RustDialog>(_main);
        var models = Path.Combine(FakeInstall.Root, "exports");
        Directory.CreateDirectory(models);
        var files = new[] { "vm_ray_gun_castle.xmodel_export", "wm_ray_gun_castle.xmodel_export", "zm_castle_bell_toll.xanim_export" }
            .Select(file => Path.Combine(models, file)).ToList();
        foreach (var file in files)
            File.WriteAllText(file, "// fake export\n" + new string('x', 20_000));
        var adding = ((Export2RustDialogViewModel)export.DataContext!).AddFilesAsync(files);
        WaitFor(() => adding.IsCompleted);
        Pump(100);
        Shot(export, "export2rust");
        export.Close();
        Pump();
    }

    // ── Setup ───────────────────────────────────────────────────────────────

    private static void SetupDialogs()
    {
        var fs = HarnessFileSystem.Instance;
        int checksBefore;
        lock (fs.ValidateThreads)
            checksBefore = fs.ValidateThreads.Count;

        Invoke(_main, "ShowSetupDoctorAsync", false, null);
        var doctor = Owned<SetupDoctorDialog>(_main);
        var svm = (SetupDoctorViewModel)doctor.DataContext!;
        Shot(doctor, "setup-doctor");
        svm.ToolsPath = FakeInstall.GameOnlyPath;
        Pump(400);
        Shot(doctor, "setup-doctor-missing");
        List<int> checkThreads;
        lock (fs.ValidateThreads)
            checkThreads = fs.ValidateThreads.Skip(checksBefore).ToList();
        Check($"setup doctor: opening and typing check the folders off the UI thread ({checkThreads.Count} checks, healthy {svm.IsHealthy})",
            checkThreads.Count >= 2 && checkThreads.All(t => t != UiThreadId) && !svm.IsHealthy);

        fs.Detected = new SetupDetectionResult();
        var detecting = svm.DetectAsync();
        WaitFor(() => detecting.IsCompleted);
        Pump(50);
        Check($"setup doctor: Auto-detect finding nothing says so ('{svm.StatusText}')",
            svm.StatusText == "Couldn't find Black Ops III automatically. Choose the folder.");
        Shot(doctor, "setup-doctor-not-detected");
        Key(doctor, Avalonia.Input.Key.Escape);
        WaitFor(() => !doctor.IsVisible);

        // Searching, held until the shot is taken, then found.
        fs.Detected = new SetupDetectionResult { GamePath = FakeInstall.GamePath, ToolsPath = FakeInstall.GamePath };
        fs.DetectGate = new ManualResetEventSlim(false);
        Invoke(_main, "ShowFirstRunAsync");
        var firstRun = Owned<FirstRunDialog>(_main);
        var fvm = (FirstRunViewModel)firstRun.DataContext!;
        WaitFor(() => fvm.ShowSearching);
        Shot(firstRun, "first-run-searching");
        fs.DetectGate.Set();
        fs.DetectGate = null;
        WaitFor(() => fvm.IsReady);
        Pump(100);
        Check("first run: when everything is found, Continue has focus",
            firstRun.FindControl<Button>("ContinueButton")!.IsFocused);
        Shot(firstRun, "first-run-ready");
        Key(firstRun, Avalonia.Input.Key.Escape);
        WaitFor(() => !firstRun.IsVisible);

        fs.Detected = new SetupDetectionResult { GamePath = FakeInstall.GameOnlyPath };
        Invoke(_main, "ShowFirstRunAsync");
        firstRun = Owned<FirstRunDialog>(_main);
        fvm = (FirstRunViewModel)firstRun.DataContext!;
        WaitFor(() => fvm.IsToolsMissing);
        Pump(100);
        Check("first run: when the Mod Tools are missing, Check again has focus",
            firstRun.FindControl<Button>("CheckAgainButton")!.IsFocused);
        Shot(firstRun, "first-run-tools-missing");
        Key(firstRun, Avalonia.Input.Key.Escape);
        WaitFor(() => !firstRun.IsVisible);

        fs.Detected = new SetupDetectionResult();
        Invoke(_main, "ShowFirstRunAsync");
        firstRun = Owned<FirstRunDialog>(_main);
        fvm = (FirstRunViewModel)firstRun.DataContext!;
        WaitFor(() => fvm.IsNotFound);
        Pump(100);
        Check("first run: when nothing is found, Choose folder has focus",
            firstRun.FindControl<Button>("ChooseFolderButton")!.IsFocused);
        Shot(firstRun, "first-run-not-found");
        Key(firstRun, Avalonia.Input.Key.Escape);
        WaitFor(() => !firstRun.IsVisible);
    }

    // ── Shared message dialog ───────────────────────────────────────────────

    private static void Messages()
    {
        Invoke(_main, "ClearLogAsync");
        var confirm = Owned<Window>(_main);
        var focused = confirm.FocusManager?.GetFocusedElement() as Button;
        Check($"clear log: the confirmation starts on the safe choice ({focused?.Content})", focused?.Content as string == "Cancel");
        Shot(confirm, "message-confirm-clear-log");
        Key(confirm, Avalonia.Input.Key.Enter);
        Pump(100);
        Check("clear log: Enter doesn't confirm a destructive action (it presses the focused Cancel)", _vm.Output.Length > 0);
        if (confirm.IsVisible)
            confirm.Close();

        _ = MessageDialog.ShowAsync(_main, "Rename incomplete",
            "The folder was renamed to zm_castle_redux2, but some files inside it couldn't be updated, so they may still use "
            + "the old name zm_castle_redux. A file is open in another program. Close it and try again.",
            folderPath: FakeInstall.CastleFolder);
        var message = Owned<Window>(_main);
        Shot(message, "message-info");
        Key(message, Avalonia.Input.Key.Escape);
        WaitFor(() => !message.IsVisible);
    }

    // ── Publish ─────────────────────────────────────────────────────────────

    private static int WorkshopWrites()
    {
        lock (HarnessFileSystem.Instance.WorkshopWriteThreads)
            return HarnessFileSystem.Instance.WorkshopWriteThreads.Count;
    }

    /// <summary>Every file and folder under <paramref name="folder"/>, with its size, so a rename or rewrite shows.</summary>
    private static string Snapshot(string folder) => string.Join("\n",
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => File.Exists(path) ? $"{path} {new FileInfo(path).Length}" : path));

    private static void Publish()
    {
        // The fake install keeps an older build's layout: the versions file and media in zone.
        var zoneBefore = Snapshot(FakeInstall.CastleWorkshopFolder);
        var writesBefore = WorkshopWrites();

        _vm.PublishCommand!.Execute(null);
        var dialog = Owned<PublishDialog>(_main, 6000);
        var pvm = (PublishDialogViewModel)dialog.DataContext!;
        WaitFor(() => pvm.ThumbnailPreview is not null);
        Pump(300);
        Check($"publish: the Workshop sync check runs in the footer, with no placeholder row, and clears when Steam answers ('{pvm.DraftStatusText}')",
            !pvm.DraftStatusText.StartsWith("Checking Steam", StringComparison.Ordinal)
            && !pvm.Checks.Any(c => c.Check == "Workshop sync"));
        Shot(dialog, "publish-editing");
        Shot150(dialog, "publish-editing@1.5x");

        var stable = pvm.SelectedProfile!;
        pvm.SelectedProfile = pvm.PublishProfiles.First(p => !ReferenceEquals(p, stable));
        Pump();
        pvm.SelectedProfile = stable;
        Pump(900);
        Check($"publish: opening the dialog and switching Workshop version write nothing ({WorkshopWrites() - writesBefore} writes)",
            WorkshopWrites() == writesBefore && Snapshot(FakeInstall.CastleWorkshopFolder) == zoneBefore);

        ThumbnailDebounce(dialog, pvm);
        DescriptionShortcuts(dialog, pvm);
        RenameWorkshopVersion(dialog, pvm);
        NewWorkshopVersion(pvm);
        PullFromWorkshop(dialog, pvm);
        PrepareForPublish(dialog, pvm);
        Upload(dialog, pvm);

        Click(dialog.FindControl<Button>("PrimaryButton")!.Parent is Panel panel
            ? panel.Children.OfType<Button>().Last()
            : throw new InvalidOperationException("No secondary button"));
        WaitFor(() => !dialog.IsVisible);
        Check("publish: Done closes the dialog", !dialog.IsVisible);

        CorruptWorkshopJson();
    }

    private static void ThumbnailDebounce(PublishDialog dialog, PublishDialogViewModel pvm)
    {
        var box = Find<TextBox>(dialog).First(t => t.Text == FakeInstall.ThumbnailPath);
        box.Focus();
        box.CaretIndex = box.Text!.Length;
        var changes = 0;
        void Count(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => changes++;
        pvm.Checks.CollectionChanged += Count;
        Type(dialog, ".bak");
        var whileTyping = changes;
        var blockedWhileTyping = pvm.IsThumbnailBlocker;
        Pump(300);
        pvm.Checks.CollectionChanged -= Count;
        Check($"publish: the thumbnail path is checked once typing pauses ({whileTyping} check updates while typing, {changes} after)",
            whileTyping == 0 && !blockedWhileTyping && changes == 1 && pvm.IsThumbnailBlocker);

        // Clearing applies at once; restoring the real path clears the blocker.
        box.Text = "";
        Pump();
        Check("publish: clearing the thumbnail applies immediately", !pvm.IsThumbnailBlocker && pvm.IsThumbnailWarning);
        box.Text = FakeInstall.ThumbnailPath;
        Pump(300);
        WaitFor(() => pvm.ThumbnailPreview is not null);
    }

    private static void DescriptionShortcuts(PublishDialog dialog, PublishDialogViewModel pvm)
    {
        var original = pvm.Description;
        var box = dialog.FindControl<TextBox>("DescriptionTextBox")!;
        box.Focus();
        box.SelectionStart = 0;
        box.SelectionEnd = 4;
        Key(dialog, Avalonia.Input.Key.B, RawInputModifiers.Control);
        Check($"publish: Ctrl+B in the description wraps the selection in [b] ('{pvm.Description[..Math.Min(20, pvm.Description.Length)]}')",
            pvm.Description == "[b]" + original[..4] + "[/b]" + original[4..]
            && box.SelectionStart == 3 && box.SelectionEnd == 7);

        box.Text = original;
        box.SelectionStart = box.SelectionEnd = original.Length;
        Invoke(dialog, "OnLinkClick", null, new RoutedEventArgs());
        var selected = box.Text![Math.Min(box.SelectionStart, box.SelectionEnd)..Math.Max(box.SelectionStart, box.SelectionEnd)];
        Check($"publish: inserting a link selects the URL to type over ('{selected}')", selected == "https://");
        box.Text = original;
        Pump();
    }

    private static void RenameWorkshopVersion(PublishDialog dialog, PublishDialogViewModel pvm)
    {
        var writesBefore = WorkshopWrites();

        Invoke(dialog, "OnRenameProfileClick", null, new RoutedEventArgs());
        var prompt = Owned<Window>(dialog);
        var input = Find<TextBox>(prompt).First();
        Check("prompt: opens with the current name focused and selected",
            input.IsFocused && input.SelectionStart == 0 && input.SelectionEnd == (input.Text ?? "").Length);
        Shot(prompt, "text-prompt");
        Type(prompt, "Canary");
        Key(prompt, Avalonia.Input.Key.Enter);
        Pump(100);
        Check("prompt: a taken name keeps it open with the reason", prompt.IsVisible);
        Shot(prompt, "text-prompt-error");
        input.SelectAll();
        Type(prompt, "Release");
        Key(prompt, Avalonia.Input.Key.Enter);
        WaitFor(() => !prompt.IsVisible);

        WaitFor(() => WorkshopWrites() >= writesBefore + 2);
        WaitFor(() => pvm.Thumbnail.StartsWith(FakeInstall.CastleFolder + Path.DirectorySeparatorChar + "workshop_media", StringComparison.OrdinalIgnoreCase));
        List<int> threads;
        lock (HarnessFileSystem.Instance.WorkshopWriteThreads)
            threads = HarnessFileSystem.Instance.WorkshopWriteThreads.Skip(writesBefore).ToList();
        var profilesFile = Path.Combine(FakeInstall.CastleFolder, "workshop.profiles.json");
        Check($"publish: renaming a Workshop version writes the Workshop files off the UI thread ({threads.Count} writes)",
            pvm.SelectedProfile?.Name == "Release" && threads.Count >= 2 && threads.All(t => t != UiThreadId)
            && File.Exists(profilesFile) && File.ReadAllText(profilesFile).Contains("\"Release\""));

        var zone = FakeInstall.CastleWorkshopFolder;
        var movedThumbnail = Path.Combine(FakeInstall.CastleFolder, "workshop_media", "thumbnail.png");
        Check($"publish: a draft save moves the versions file and media out of zone, and the paths follow ('{pvm.Thumbnail}')",
            !File.Exists(Path.Combine(zone, "workshop.profiles.json"))
            && !Directory.Exists(Path.Combine(zone, "workshop_media"))
            && File.Exists(movedThumbnail)
            && string.Equals(pvm.Thumbnail, movedThumbnail, StringComparison.OrdinalIgnoreCase)
            && pvm.GetPreviewImagePaths().All(File.Exists)
            && !File.ReadAllText(profilesFile).Contains(@"zone\\workshop_media", StringComparison.OrdinalIgnoreCase)
            && !Directory.EnumerateFiles(zone, "*.tmp").Any());
        Check($"publish: the media folder button opens the moved folder ('{pvm.WorkshopMediaFolder}')",
            pvm.HasWorkshopMediaFolder
            && string.Equals(pvm.WorkshopMediaFolder, Path.Combine(FakeInstall.CastleFolder, "workshop_media"), StringComparison.OrdinalIgnoreCase));
    }

    private static void NewWorkshopVersion(PublishDialogViewModel pvm)
    {
        var release = pvm.SelectedProfile!;
        pvm.CreateProfile("Fresh");
        Pump();
        Check($"publish: a new Workshop version starts blank but for the project's name ('{pvm.Title}', {pvm.SelectedVisibility}, {pvm.PreviewImages.Count} images)",
            pvm.SelectedProfile?.Name == "Fresh" && pvm.Title == pvm.ProjectName && pvm.Description == ""
            && pvm.Thumbnail == "" && pvm.Changelog == "" && pvm.PreviewImages.Count == 0
            && pvm.GetSelectedTags().Length == 0 && pvm.SelectedVisibility == WorkshopItemVisibility.Private
            && pvm.IsNewWorkshopItem);
        pvm.DeleteSelectedProfile();
        pvm.SelectedProfile = release;
        Pump(100);
    }

    private static void PullFromWorkshop(PublishDialog dialog, PublishDialogViewModel pvm)
    {
        var release = pvm.SelectedProfile!;
        var localTitle = pvm.Title;
        var localThumbnail = pvm.Thumbnail;
        var localGallery = pvm.GetPreviewImagePaths();
        var remote = pvm.GetActiveWorkshopData().Clone();
        remote.Title = "Castle Redux (from Steam)";
        Steam.RemoteMetadata = remote;

        // Asked first; Cancel leaves the draft alone.
        pvm.OnCheckAction!("pull-workshop", "");
        var confirm = Owned<Window>(dialog);
        Check($"publish: Pull from Workshop asks before replacing the draft (pulling {pvm.IsPulling}, title '{pvm.Title}')",
            confirm.IsVisible && !pvm.IsPulling && pvm.Title == localTitle);
        Shot(confirm, "publish-pull-confirm");
        Key(confirm, Avalonia.Input.Key.Escape);
        WaitFor(() => !confirm.IsVisible);
        Pump(100);
        Check("publish: cancelling the pull keeps the draft", !pvm.IsPulling && pvm.Title == localTitle);

        // Switching Workshop version while Steam answers: nothing lands on the other version.
        Steam.FetchGate = new TaskCompletionSource();
        pvm.OnCheckAction!("pull-workshop", "");
        confirm = Owned<Window>(dialog);
        Click(ButtonWithText(confirm, "Replace draft"));
        WaitFor(() => pvm.IsPulling);
        Check($"publish: pulling locks the form and Publish ({pvm.IsPulling}, form {pvm.IsFormEnabled}, publish {pvm.IsPrimaryEnabled})",
            pvm.IsPulling && !pvm.IsFormEnabled && !pvm.IsPrimaryEnabled);
        var other = pvm.PublishProfiles.First(p => !ReferenceEquals(p, release));
        pvm.SelectedProfile = other;
        var otherTitle = pvm.Title;
        Steam.FetchGate.SetResult();
        Steam.FetchGate = null;
        WaitFor(() => !pvm.IsPulling);
        Check($"publish: a pull answered after the Workshop version changed is dropped ('{pvm.Title}')",
            pvm.Title == otherTitle && pvm.IsFormEnabled);

        pvm.SelectedProfile = release;
        Pump();
        pvm.OnCheckAction!("pull-workshop", "");
        confirm = Owned<Window>(dialog);
        Click(ButtonWithText(confirm, "Replace draft"));
        WaitFor(() => !confirm.IsVisible && !pvm.IsPulling);
        Pump(900);
        Check($"publish: a pull lands on the version it started from, and its status stays ('{pvm.Title}', '{pvm.DraftStatusText}')",
            pvm.Title == remote.Title && pvm.DraftStatusText == "Pulled from the Workshop");
        Check($"publish: Steam having no thumbnail or gallery clears both ('{pvm.Thumbnail}', {pvm.PreviewImages.Count})",
            pvm.Thumbnail == "" && pvm.PreviewImages.Count == 0);

        pvm.Title = localTitle;
        pvm.Thumbnail = localThumbnail;
        pvm.AddPreviewImages(localGallery);
        Steam.RemoteMetadata = null;
        Pump(300);
        WaitFor(() => pvm.ThumbnailPreview is not null);
    }

    private static void PrepareForPublish(PublishDialog dialog, PublishDialogViewModel pvm)
    {
        var prepare = ButtonWithText(dialog, "Prepare now");
        prepare.BringIntoView();
        Pump(50);
        var runs = Build.Runs;
        Click(prepare);
        Pump(50);
        Check($"publish: Prepare now starts without a confirmation (XPaks go to the Recycle Bin) (runs {Build.Runs - runs}, error '{pvm.ErrorMessage}')",
            dialog.OwnedWindows.All(w => !w.IsVisible) && Build.Runs == runs + 1);
        WaitFor(() => !_vm.IsBuildRunning && pvm.DraftStatusText.StartsWith("Prepare for publish", StringComparison.Ordinal), 6000);
        Check($"publish: Prepare for publish reports back in the dialog ('{pvm.DraftStatusText}')",
            pvm.DraftStatusText.StartsWith("Prepare for publish finished", StringComparison.Ordinal));
        Pump(150);
    }

    private static void Upload(PublishDialog dialog, PublishDialogViewModel pvm)
    {
        var writesBefore = WorkshopWrites();
        var changelog = pvm.Changelog;

        // A temp file a crashed write left in zone must never ship.
        var strayTemp = Path.Combine(FakeInstall.CastleWorkshopFolder, "workshop.json.0123abcd.tmp");
        File.WriteAllText(strayTemp, "{");

        Steam.UploadGate = new TaskCompletionSource();
        Click(dialog.FindControl<Button>("PrimaryButton")!);
        WaitFor(() => pvm.IsUploading && pvm.UploadStatusText.Contains("42"));
        Pump(100);
        Check($"publish: Publish update uploads with the dialog open ('{pvm.UploadStatusText}')", pvm.IsUploading);
        Check($"publish: uploading shows determinate progress and only Cancel ({pvm.UploadPercent:0}%)",
            pvm.IsUploadProgressKnown && Math.Abs(pvm.UploadPercent - 42) < 0.5
            && !dialog.FindControl<Button>("PrimaryButton")!.IsVisible);
        Check($"publish: the main window's status isn't narrating the upload ('{_vm.StatusText}')",
            !_vm.StatusText.Contains("Uploading", StringComparison.OrdinalIgnoreCase));
        Shot(dialog, "publish-uploading");

        List<int> threads;
        lock (HarnessFileSystem.Instance.WorkshopWriteThreads)
            threads = HarnessFileSystem.Instance.WorkshopWriteThreads.Skip(writesBefore).ToList();
        Check($"publish: Publish writes the Workshop files off the UI thread before uploading ({threads.Count} writes)",
            threads.Count >= 2 && threads.All(t => t != UiThreadId));
        Check($"publish: zone holds none of Blackbird's own files when Steam takes it ({string.Join(", ", Steam.LastUploadedFiles)})",
            Steam.LastUploadedFiles.Length > 0
            && Steam.LastUploadedFiles.All(f => !WorkshopFiles.IsBlackbirdFile(f)
                && !f.StartsWith("workshop_media", StringComparison.OrdinalIgnoreCase))
            && !File.Exists(strayTemp));

        Steam.ReleaseUpload();
        Steam.UploadGate = null;
        WaitFor(() => pvm.IsPublished);
        Pump(150);
        Check("publish: a finished upload shows the success state", pvm.IsPublished);
        Check($"publish: the changelog went up once and is cleared for the next upload ('{Steam.LastChangeNote}' → '{pvm.Changelog}')",
            Steam.LastChangeNote == changelog && changelog.Length > 0 && pvm.Changelog == ""
            && pvm.SelectedProfile!.Profile.WorkshopJson.Changelog == "");
        Check($"publish: the main window gets one line when it's over ('{_vm.StatusText}')",
            _vm.StatusText.StartsWith("Published ", StringComparison.Ordinal) && _vm.StatusText.EndsWith(" to the Workshop.", StringComparison.Ordinal));
        Shot(dialog, "publish-success");

        pvm.CompleteUpload("2987654321", pvm.SelectedVisibility, needsLegalAgreement: true);
        Pump(100);
        Check($"publish: Steam's Workshop agreement flag points the primary button at the agreement ('{pvm.PrimaryButtonText}', {pvm.PrimaryUrl})",
            pvm.NeedsLegalAgreement && pvm.PublishedStatusText.Contains("Workshop agreement", StringComparison.Ordinal)
            && pvm.PrimaryUrl == PublishDialogViewModel.WorkshopAgreementUrl);
        Shot(dialog, "publish-success-agreement");
    }

    /// <summary>A workshop.json that won't parse: Publish blocks on it and nothing on disk is renamed or rewritten.</summary>
    private static void CorruptWorkshopJson()
    {
        var zone = Path.Combine(FakeInstall.GamePath, "mods", "wraith_weapons", "zone");
        var workshopJson = Path.Combine(zone, "workshop.json");
        var original = File.ReadAllBytes(workshopJson);
        File.WriteAllText(Path.Combine(zone, "core_mod.ff"), new string('x', 4096));
        File.WriteAllText(workshopJson, "{ \"PublisherID\": 2912345678, \"Title\": \"Wraith Weapons\",");
        var before = Snapshot(Path.GetDirectoryName(zone)!);

        _vm.SelectProjectByName("wraith_weapons", ProjectType.Mod);
        WaitFor(() => _vm.SelectedProject?.Name == "wraith_weapons");
        Pump(200);
        _vm.PublishCommand!.Execute(null);
        var dialog = Owned<PublishDialog>(_main, 6000);
        var pvm = (PublishDialogViewModel)dialog.DataContext!;
        Pump(300);
        Check($"publish: a corrupt workshop.json blocks publishing with a way to open it ({string.Join(", ", pvm.Checks.Where(c => c.IsBlocker).Select(c => c.Check))})",
            pvm.Checks.Any(c => c.IsBlocker && c.PrimaryAction?.Kind == "open-file"
                                && string.Equals(c.PrimaryAction.Payload, workshopJson, StringComparison.OrdinalIgnoreCase))
            && !pvm.IsPrimaryEnabled);
        Shot(dialog, "publish-corrupt-workshop-json");

        // An edit tries to save the draft; the corrupt file is left exactly as it was.
        pvm.Title = "Wraith Weapons (edited)";
        pvm.FlushLocalAutosave();
        WaitFor(() => pvm.HasError);
        Check($"publish: opening Publish on a corrupt workshop.json renames and rewrites nothing ('{pvm.ErrorMessage}')",
            Snapshot(Path.GetDirectoryName(zone)!) == before && pvm.HasError);

        Key(dialog, Avalonia.Input.Key.Escape);
        WaitFor(() => !dialog.IsVisible);
        File.WriteAllBytes(workshopJson, original);
        SelectCastle();
    }

    // ── Empty states (last: they repoint the install) ───────────────────────

    private static void EmptyStates()
    {
        // Delete is one step: the folder goes to the Recycle Bin, and the project's settings stay for a restore.
        _vm.SelectProjectByName("zm_foundry", ProjectType.Map);
        WaitFor(() => _vm.SelectedProject?.Name == "zm_foundry");
        var foundry = Path.Combine(FakeInstall.GamePath, "usermaps", "zm_foundry");
        Invoke(_main, "DeleteSelectedProjectAsync");
        WaitFor(() => _vm.StatusLine.Contains("Recycle Bin", StringComparison.Ordinal), 8000);
        Check($"delete: moves the project to the Recycle Bin with no confirmation, and says where it went ({_main.OwnedWindows.Count(w => w.IsVisible)} dialogs, '{_vm.StatusLine}')",
            !Directory.Exists(foundry) && !_main.OwnedWindows.Any(w => w.IsVisible) && _vm.StatusLine.Contains("Recycle Bin", StringComparison.Ordinal));

        Check("delete: the project's settings stay, so restoring the folder brings them back",
            _settings.Projects.ContainsKey("map:zm_foundry"));

        _vm.SelectedProject = null;

        Pump(150);
        Check($"empty: no selection says '{_vm.EmptyStateTitle}'", _vm.EmptyStateTitle == "Choose a project");
        Shot(_main, "main-empty-none-selected");

        _settings.GamePath = FakeInstall.EmptyGamePath;
        _settings.ToolsPath = FakeInstall.EmptyGamePath;
        Refresh();
        Check($"empty: an install with no projects says '{_vm.EmptyStateTitle}'", _vm.EmptyStateTitle == "No maps or mods yet");
        Shot(_main, "main-empty-no-projects");

        _settings.GamePath = Path.Combine(FakeInstall.Root, "Nowhere");
        _settings.ToolsPath = Path.Combine(FakeInstall.Root, "Nowhere");
        Refresh();
        Check($"empty: a broken setup says '{_vm.EmptyStateTitle}'", _vm.HasSetupIssues);
        Check("empty: the setup problem shows once, with no title-bar warning beside the empty state", !_vm.HasToolHealthIssues);

        Shot(_main, "main-empty-setup");

        _settings.GamePath = FakeInstall.GamePath;
        _settings.ToolsPath = FakeInstall.GamePath;
        Refresh();
    }
}
