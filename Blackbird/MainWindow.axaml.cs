using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Blackbird.Models;
using Blackbird.Services;
using Blackbird.ViewModels;
using Blackbird.Views;

namespace Blackbird;

public partial class MainWindow : Window
{
    private static readonly HttpClient HttpClient = new();
    private static readonly IReadOnlyList<FilePickerFileType> LogExportFileTypes =
    [
        new("Log files") { Patterns = ["*.log"] },
        new("Text files") { Patterns = ["*.txt"] },
        new("All files") { Patterns = ["*"] }
    ];
    private enum ConfirmChoice { Cancel, Confirm }

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowBounds();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        // Title bar drag behavior
        var dragRegion = this.FindControl<Border>("TitleBarDrag");
        if (dragRegion is not null)
        {
            dragRegion.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginMoveDrag(e);
            };
            dragRegion.DoubleTapped += (_, _) => ToggleMaximize();
        }

        // Dropping an .exe anywhere on the title bar pins it as a tool.
        var titleBar = this.FindControl<Grid>("TitleBar");
        if (titleBar is not null)
        {
            titleBar.AddHandler(DragDrop.DragOverEvent, OnToolbarDragOver);
            titleBar.AddHandler(DragDrop.DragLeaveEvent, OnToolbarDragLeave);
            titleBar.AddHandler(DragDrop.DropEvent, OnToolbarDrop);
        }

        if (this.FindControl<ColoredLogView>("BuildLogView") is { } logView)
            logView.ActionRequested += OnLogViewAction;

        if (this.FindControl<Button>("UpdateButton") is { } updateButton)
            updateButton.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty)
                    OnUpdateButtonShown(e.GetNewValue<bool>());
            };
    }

    /// <summary>
    /// Window-wide shortcuts. Standard ones keep their standard meaning: Ctrl+A selects
    /// all, Ctrl+F finds, Ctrl+K is the palette. Text fields keep their own keys.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null)
            return;

        var ctrl = e.KeyModifiers == KeyModifiers.Control;
        var ctrlShift = e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.F5 when e.KeyModifiers == KeyModifiers.None:
                ViewModel.RunForModeCommand.Execute(null);
                break;
            case Key.B when ctrl:
                ViewModel.BuildOnlyOnceCommand.Execute(null);
                break;
            case Key.N when ctrl:
                ViewModel.NewItemCommand?.Execute(null);
                break;
            case Key.P when ctrl:
                OpenProjectPicker();
                break;
            case Key.K when ctrl:
            case Key.P when ctrlShift:
                _ = OpenCommandPaletteAsync();
                break;
            case Key.F when ctrl:
                // A text field other than the log's own find keeps Ctrl+F (the project
                // search, dialogs' fields); everywhere else it goes to the log's find.
                if (IsTextInputTarget(e.Source) && !IsInsideLogView(e.Source))
                    return;
                FocusLogSearch();
                break;
            case Key.A when ctrlShift:
                ViewModel.OpenAssetEditorCommand.Execute(null);
                break;
            case Key.R when ctrlShift:
                ViewModel.OpenRadiantCommand.Execute(null);
                break;
            case Key.U when ctrlShift:
                ViewModel.PublishCommand?.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void FocusLogSearch()
    {
        if (ViewModel?.HasSelectedProject == true)
            this.FindControl<ColoredLogView>("BuildLogView")?.FocusSearch();
    }

    private bool IsInsideLogView(object? source) =>
        source is Visual visual
        && this.FindControl<ColoredLogView>("BuildLogView") is { } logView
        && (ReferenceEquals(visual, logView) || visual.GetVisualAncestors().Contains(logView));

    // A click on a project row opens it. Header rows are buttons that fold their group.
    private void OnProjectPickerTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source
            && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: ProjectItem { IsGroupHeader: false } row })
        {
            CommitPickerRow(row);
        }
    }

    // Arrows only move the highlight; Enter opens it (or folds a header).
    private void OnProjectPickerListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is not null)
        {
            CommitPickerRow(ViewModel.PickerSelection);
            e.Handled = true;
        }
    }

    private void CommitPickerRow(ProjectItem? row)
    {
        if (ViewModel?.CommitPickerSelection(row) != true)
            return;

        ViewModel.ProjectSearchText = "";
        this.FindControl<Button>("ProjectPickerButton")?.Flyout?.Hide();
    }

    /// <summary>Ctrl+P and the empty state's "Choose a project".</summary>
    private void OpenProjectPicker()
    {
        if (this.FindControl<Button>("ProjectPickerButton") is { Flyout: { } flyout } button)
            flyout.ShowAt(button);
    }

    private void OnChooseProjectClick(object? sender, RoutedEventArgs e) => OpenProjectPicker();

    // The picker opens ready to type into, however it was opened.
    private void OnProjectPickerOpened(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<TextBox>("ProjectSearchBox") is { } search)
            {
                search.Focus();
                search.SelectAll();
            }
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    // Enter in the search opens the best match; Down moves into the list.
    private void OnProjectSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (e.Key == Key.Enter)
        {
            CommitPickerRow(ViewModel.BestPickerMatch());
            e.Handled = true;
        }

        else if (e.Key == Key.Down && this.FindControl<ListBox>("ProjectPickerList") is { IsVisible: true } list)
        {
            list.Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
    }

    private static bool IsTextInputTarget(object? source)
    {
        static bool IsTextControl(object target)
        {
            var typeName = target.GetType().Name;
            return target is TextBox
                   || typeName.Contains("TextEditor", StringComparison.Ordinal)
                   || typeName.Contains("TextArea", StringComparison.Ordinal);
        }

        if (source is null)
            return false;
        if (IsTextControl(source))
            return true;
        if (source is Visual visual)
            return visual.GetVisualAncestors().Any(IsTextControl);

        return false;
    }

    private void RestoreWindowBounds()
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        if (settings.WindowWidth >= MinWidth)
            Width = settings.WindowWidth;
        if (settings.WindowHeight >= MinHeight)
            Height = settings.WindowHeight;

        // Monitors left of or above the primary have negative coordinates; a monitor that's
        // since been unplugged has none. Restore only where the title bar can still be grabbed.
        var saved = new PixelPoint((int)Math.Round(settings.WindowX), (int)Math.Round(settings.WindowY));
        var grabPoint = saved + new PixelVector(120, 16);
        if (Screens.All.Any(screen => screen.WorkingArea.Contains(grabPoint)))
            Position = saved;
        else
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal && Width >= MinWidth && Height >= MinHeight)

        {
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
            settings.WindowX = Position.X;
            settings.WindowY = Position.Y;
        }
    }

    private async void OnCommandPaletteClick(object? sender, RoutedEventArgs e)
    {
        await OpenCommandPaletteAsync();
    }

    /// <summary>The log's own menu and context menu: copy, export and clear.</summary>
    private async void OnLogViewAction(object? sender, LogViewAction action)
    {
        switch (action)
        {
            case LogViewAction.CopyLog:
                await CopyBuildLogAsync(MainWindowViewModel.LogExportKind.Full);
                break;
            case LogViewAction.CopyErrors:
                await CopyBuildLogAsync(MainWindowViewModel.LogExportKind.Errors);
                break;
            case LogViewAction.CopyWarnings:
                await CopyBuildLogAsync(MainWindowViewModel.LogExportKind.Warnings);
                break;
            case LogViewAction.ExportLog:
                await ExportBuildLogAsync(MainWindowViewModel.LogExportKind.Full);
                break;
            case LogViewAction.Clear:

                await ClearLogAsync();
                break;
        }
    }

    private async void OnCopyRunCommandClick(object? sender, RoutedEventArgs e)
    {
        await CopyRunCommandAsync();
    }

    private async void OnCopyBuildCommandsClick(object? sender, RoutedEventArgs e)
    {
        await CopyBuildCommandsAsync();
    }

    private async void OnCopyWorkshopUrlClick(object? sender, RoutedEventArgs e)
    {
        await CopyWorkshopUrlAsync();
    }

    private async void OnCopyProjectNameClick(object? sender, RoutedEventArgs e)
    {
        await CopySelectedProjectNameAsync();
    }

    private async void OnCopyProjectFolderClick(object? sender, RoutedEventArgs e)
    {
        await CopySelectedProjectFolderPathAsync();
    }

    private System.Threading.Tasks.Task CopySelectedProjectNameAsync() =>
        CopyTextToClipboardAsync(ViewModel?.SelectedProject?.Name, "Copied the project name.", "Choose a project first.");

    private System.Threading.Tasks.Task CopySelectedProjectFolderPathAsync() =>
        CopyTextToClipboardAsync(ViewModel?.SelectedProject?.FolderPath, "Copied the folder path.", "Choose a project first.");

    private System.Threading.Tasks.Task CopyRunCommandAsync() =>
        CopyTextToClipboardAsync(ViewModel?.GetRunCommandText(), "Copied the run command.", "There's no run command for this project.");

    private System.Threading.Tasks.Task CopyBuildCommandsAsync() =>
        CopyTextToClipboardAsync(ViewModel?.GetBuildCommandText(), "Copied the build commands.", "Tick a build step first.");

    private System.Threading.Tasks.Task CopyWorkshopUrlAsync() =>
        CopyTextToClipboardAsync(ViewModel?.GetSelectedProjectWorkshopUrl(), "Copied the Workshop URL.", "This project isn't on the Workshop yet.");

    private System.Threading.Tasks.Task CopyBuildLogAsync(MainWindowViewModel.LogExportKind kind) =>
        CopyTextToClipboardAsync(ViewModel?.GetLogExportText(kind), GetCopiedLogMessage(kind), GetEmptyLogMessage(kind, "copy"));

    /// <summary>Every Copy goes through here: one status line for what happened, including a clipboard another program holds.</summary>
    private async System.Threading.Tasks.Task CopyTextToClipboardAsync(string? text, string copiedMessage, string emptyMessage)
    {
        if (ViewModel is null)
            return;

        if (string.IsNullOrWhiteSpace(text))
        {
            ViewModel.ShowTransientStatus(emptyMessage);
            return;
        }

        try
        {
            if (Clipboard is null)
                throw new InvalidOperationException();
            await Clipboard.SetTextAsync(text);
            ViewModel.ShowTransientStatus(copiedMessage);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ViewModel.ShowTransientStatus("Couldn't copy. Another program may be holding the clipboard.");
        }
    }

    private async System.Threading.Tasks.Task ExportBuildLogAsync(MainWindowViewModel.LogExportKind kind)
    {
        if (ViewModel is null)
            return;

        var text = ViewModel.GetLogExportText(kind);
        if (string.IsNullOrEmpty(text))
        {
            ViewModel.ShowTransientStatus(GetEmptyLogMessage(kind, "export"));
            return;
        }

        IStorageFolder? startLocation = null;
        var startFolder = ViewModel.GetLogExportStartFolder();
        if (Directory.Exists(startFolder))
            startLocation = await StorageProvider.TryGetFolderFromPathAsync(startFolder);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = GetLogExportDialogTitle(kind),
            SuggestedFileName = ViewModel.GetSuggestedLogExportFileName(kind),
            DefaultExtension = "log",
            FileTypeChoices = LogExportFileTypes,
            SuggestedStartLocation = startLocation
        });

        if (file is null)
            return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(text);

            var destination = file.Path.IsFile ? file.Path.LocalPath : file.Name;
            ViewModel.ShowTransientStatus($"{GetExportedLogLabel(kind)}: {destination}");
        }
        catch (Exception ex)
        {
            CrashLog.Write("Log export failed", ex);
            await ShowMessageAsync("Couldn't export the log", "The file couldn't be written. Check the folder isn't read-only, then try again.");
        }
    }

    private static string GetEmptyLogMessage(MainWindowViewModel.LogExportKind kind, string verb) =>
        kind switch
        {
            MainWindowViewModel.LogExportKind.Errors => $"No errors to {verb}.",
            MainWindowViewModel.LogExportKind.Warnings => $"No warnings to {verb}.",
            _ => $"No log output to {verb}.",
        };

    private static string GetCopiedLogMessage(MainWindowViewModel.LogExportKind kind) =>
        kind switch
        {
            MainWindowViewModel.LogExportKind.Errors => "Copied the errors.",
            MainWindowViewModel.LogExportKind.Warnings => "Copied the warnings.",
            _ => "Copied the log.",
        };

    private static string GetLogExportDialogTitle(MainWindowViewModel.LogExportKind kind) =>
        kind switch
        {
            MainWindowViewModel.LogExportKind.Errors => "Export errors",
            MainWindowViewModel.LogExportKind.Warnings => "Export warnings",
            _ => "Export log",
        };

    private static string GetExportedLogLabel(MainWindowViewModel.LogExportKind kind) =>
        kind switch
        {
            MainWindowViewModel.LogExportKind.Errors => "Exported errors",
            MainWindowViewModel.LogExportKind.Warnings => "Exported warnings",
            _ => "Exported log",
        };

    // A build or an outside tool may have made or removed files since the project was selected;
    // re-check them so the Open items' enabled states are current.
    private void OnProjectActionsMenuOpening(object? sender, EventArgs e) =>
        _ = ViewModel?.RefreshProjectCapabilitiesAsync();

    private async System.Threading.Tasks.Task OpenCommandPaletteAsync()
    {
        if (ViewModel is null) return;

        // The palette lists only actions that can run; that includes files that exist right now.
        await ViewModel.RefreshProjectCapabilitiesAsync();


        var settings = App.Services.GetRequiredService<ISettingsService>();
        var vm = new CommandPaletteDialogViewModel(
            ViewModel.ProjectCatalog,
            settings.RecentProjects,
            settings.RecentCommandActions,
            BuildCommandPaletteActions().Where(action => CanRunPaletteAction(action.Key)));
        var dialog = new CommandPaletteDialog { DataContext = vm };
        var picked = await dialog.ShowDialog<CommandPaletteEntry?>(this);
        if (picked is not null)
            await ExecuteCommandPaletteEntryAsync(picked);
    }

    private IEnumerable<CommandPaletteAction> BuildCommandPaletteActions()
    {
        if (ViewModel is null)
            yield break;

        yield return PaletteAction("new-map", "New map or mod…", "Create a map from a template, or a mod with the zones you pick", "new create map mod template zone add", 10, shortcut: "Ctrl+N");
        yield return PaletteAction("setup-doctor", "Setup Doctor…", "Check the game and Mod Tools folders", "setup doctor paths tools health fix diagnose", 20);
        yield return PaletteAction("asset-editor", "Asset Editor", "Open AssetEditor_modtools.exe", "ape asset editor tools gdt gdtdb", 30, shortcut: "Ctrl+Shift+A");
        yield return PaletteAction("radiant", "Radiant", "Open the Radiant level editor", "radiant level editor tools map edit", 31, shortcut: "Ctrl+Shift+R");

        if (ViewModel.IsApexAvailable)
            yield return PaletteAction("apex", "Apex", "Open the Apex asset editor", "apex asset property editor ape gdt tools", 31);

        if (ViewModel.IsExport2RustAvailable)
            yield return PaletteAction("export2rust", "Export2Rust…", "Open the batch converter", "export rust converter batch convert", 32);

        yield return PaletteAction("gscode-library", "GSCode Library", "Open the BO3 scripting reference", "gscode script reference library gsc csc docs api", 33);
        yield return PaletteAction("bo3-source-explorer", "BO3 Source Explorer", "Open ZeRoY's BO3 script and function explorer", "bo3 source explorer zeroy script reference gsc functions cross reference", 34);
        yield return PaletteAction("add-tool", "Add a tool…", "Pin a program to the title bar", "add custom tool shortcut executable exe toolbar pin external", 35);

        foreach (var tool in ViewModel.CustomTools)
        {
            yield return PaletteAction(
                GetToolActionKey(tool.ExePath),
                tool.Name,
                string.IsNullOrWhiteSpace(tool.Arguments)
                    ? tool.ExePath
                    : $"{tool.ExePath} {tool.Arguments}",
                $"custom tool launch run open exe shortcut external {tool.Name} {tool.ExePath} {tool.Arguments}",
                35,
                "Tool");
        }

        yield return PaletteAction("open-logs", "Open logs folder", "Open the folder Blackbird saves build logs in", "logs build output folder explorer", 36);
        yield return PaletteAction("open-game-root", "Open game folder", "Open the Black Ops III install folder", "root game folder blackops3 explorer", 37);
        yield return PaletteAction("open-tools-root", "Open Mod Tools folder", "Open the BO3 Mod Tools folder", "root tools modtools folder explorer", 38);
        yield return PaletteAction("check-updates", "Check for updates", "Look for a newer Blackbird release on GitHub", "update updates github release version latest", 40);

        // The themes other than the one in use (File > Theme is their home).
        foreach (var (choice, name, detail, words) in ThemePaletteEntries)
        {
            if (choice != ViewModel.Theme)
                yield return PaletteAction(ThemeActionPrefix + choice, name, detail, "theme appearance colour color dark light mode " + words, 41);
        }

        if (ViewModel.IsGameRunning)
            yield return PaletteAction("close-game", "Close Black Ops III", "Close the running game", "close stop kill quit game blackops3 running", 41);

        if (ViewModel.HasOutput)
        {
            yield return PaletteAction("copy-log", "Copy log", "Copy the whole build log", "copy build log output console clipboard", 47);
            yield return PaletteAction("copy-log-errors", "Copy errors", "Copy the build log's error lines", "copy errors log output console clipboard", 48);
            yield return PaletteAction("copy-log-warnings", "Copy warnings", "Copy the build log's warning lines", "copy warnings log output console clipboard", 49);
            yield return PaletteAction("export-log", "Export log…", "Save the build log to a file", "export save build log output file", 50);
            if (ViewModel.CanClearLog)
                yield return PaletteAction("clear-log", "Clear log…", "Delete the saved log and last build result", "clear delete build log output reset", 51);
        }

        if (!ViewModel.HasSelectedProject)
            yield break;

        // F5 runs whatever the Build button's mode says: Run in Build mode, otherwise Build & run.
        var f5 = ViewModel.BuildModeIndex switch { 1 => "run", 2 => "build-run-ignore-errors-once", _ => "build-run-once" };
        yield return PaletteAction("build", "Build", "Build without running the game", "compile light link build make only", 40, shortcut: "Ctrl+B");
        yield return PaletteAction("build-run-once", "Build & run", "Build, then run the game", "build run launch play", 42, shortcut: f5 == "build-run-once" ? "F5" : "");
        yield return PaletteAction("build-run-ignore-errors-once", "Build & run (ignore errors)", "Build, then run even if the build reports errors", "build run ignore errors launch", 43, shortcut: f5 == "build-run-ignore-errors-once" ? "F5" : "");
        yield return PaletteAction("run", "Run", "Run the game without building", "run game launch play", 44, shortcut: f5 == "run" ? "F5" : "");
        yield return PaletteAction("find-in-log", "Find in log", "Search the build log", "find search filter log output", 46, shortcut: "Ctrl+F");
        yield return PaletteAction("publish", "Publish to Workshop…", "Workshop versions, checks and upload", "publish workshop steam upload ws version prepare", 45, shortcut: "Ctrl+Shift+U");
        if (ViewModel.CanModifyProject)
            yield return PaletteAction("duplicate-project", "Duplicate project…", "Copy this map or mod under a new name", "duplicate copy clone project map mod", 46);
        yield return PaletteAction("analyze-project", "Analyze project…", "Check zone references and script #using paths", "analyze diagnose inspect zone script using references", 47);
        if (ViewModel.CanModifyProject)
        {
            yield return PaletteAction("rename-project", "Rename project folder…", "Rename the project on disk and update its references", "rename project folder files references", 49);
            yield return PaletteAction("delete-project", "Delete project", "Move the project to the Recycle Bin", "delete remove trash recycle project", 69);
        }

        if (ViewModel.CanEditBuildOptions)
        {
            yield return PaletteAction("select-all-build-steps", "All build steps", "Tick every build step or mod zone", "select all build steps compile light link zones preset", 47);
            yield return PaletteAction("save-build-preset", "Save steps as build preset…", "Save the ticked steps as a build preset", "build preset save steps", 52);
            foreach (var preset in ViewModel.BuildPresets)
            {
                yield return PaletteAction(
                    BuildPresetActionPrefix + preset.Name,
                    $"Build preset: {preset.Name}",
                    preset.Summary,
                    $"build preset apply steps {preset.Name}",
                    52);
            }
            yield return PaletteAction("set-build-mode-build", "Build button: Build", "The Build button builds only", "build mode only compile link no run", 49);
            yield return PaletteAction("set-build-mode-build-run", "Build button: Build & run", "The Build button builds, then runs the game", "build mode run after launch", 48);
            yield return PaletteAction("set-build-mode-ignore-errors", "Build button: Build & run (ignore errors)", "The Build button runs the game even if the build reports errors", "build mode ignore errors run after failed", 50);
        }

        yield return PaletteAction("set-launch-dev", "Launch config: Dev", "Developer dvars on", "launch config dev developer dvars", 51);
        yield return PaletteAction("set-launch-ship", "Launch config: Ship", "Developer dvars off, all languages", "launch config ship release dvars", 51);
        yield return PaletteAction("set-launch-offline", "Launch offline", "Start BlackOps3.exe directly for local iteration", "launch mode offline direct blackops3 exe", 51);
        yield return PaletteAction("set-launch-online", "Launch online", "Start through Steam so online services work", "launch mode online steam handoff applaunch", 52);
        yield return PaletteAction("launch-settings", "Edit launch config…", "Run options and dvars for the launch config", "launch settings config dvars run options language dev ship preferences", 51);

        yield return PaletteAction("project-details", "Project info…", "Display name, category, notes and folder", "details info metadata notes category display name rename", 47);
        yield return PaletteAction("copy-project-name", "Copy project name", "Copy the internal folder and build name", "copy project name internal folder build clipboard", 48);
        yield return PaletteAction("copy-project-folder", "Copy folder path", "Copy the project's folder path", "copy project folder path explorer clipboard", 49);
        yield return PaletteAction(
            "toggle-favorite",
            ViewModel.FavoriteActionLabel,
            "Favorites sort first and have their own filter",
            "favorite star pin project toggle favorites",
            50);
        yield return PaletteAction("open-folder", "Open project folder", "Open the project in File Explorer", "folder explorer files", 53);
        yield return PaletteAction("open-zone", "Open zone file", "Open the project's zone file", "zone source edit file", 54);
        yield return PaletteAction("open-zone-source-folder", "Open zone source folder", "Open the project's zone_source folder", "zone_source source folder explorer files", 55);

        if (ViewModel.IsMapSelected)
        {
            yield return PaletteAction("open-map-source", "Open map source file", "Open the map's .map file", "map source radiant file edit", 56);
            yield return PaletteAction("open-map-source-folder", "Open map source folder", "Open the map's map_source folder", "map source folder radiant explorer files", 57);
            yield return PaletteAction("open-szc", "Open sound zone config (SZC)", "Open the map's .szc file", "sound zone config szc audio file edit", 58);
        }

        yield return PaletteAction("open-workshop-content-folder", "Open Workshop content folder", "Open the project's zone folder that gets uploaded", "workshop content zone folder publish explorer", 59);
        yield return PaletteAction("open-workshop-json", "Open workshop.json", "Open the project's Workshop metadata file", "workshop metadata json publish steam file edit", 61);

        if (ViewModel.CanOpenWorkshopPage)
        {
            yield return PaletteAction("open-workshop-page", "Open Workshop page", "Open the project's Steam Workshop page", "workshop steam page browser published file id", 62);
            yield return PaletteAction("copy-workshop-url", "Copy Workshop URL", "Copy the project's Steam Workshop link", "copy workshop steam url link clipboard published file id", 63);
        }

        if (ViewModel.CanCopyRunCommand)
            yield return PaletteAction("copy-run-command", "Copy run command", "Copy the exact Black Ops III launch command", "copy run command launch args blackops3 troubleshoot", 64);

        yield return PaletteAction("copy-build-commands", "Copy build commands", "Copy the planned build, link and run commands", "copy build commands compile light link run troubleshoot clipboard", 65);

        if (ViewModel.CanOpenScriptsInVsCode)
            yield return PaletteAction("open-scripts", "Open scripts in VS Code", "Open the scripts folder", "scripts gsc csc vscode code", 66);

        if (ViewModel.CanOpenLuiInVsCode)
            yield return PaletteAction("open-lui", "Open LUI in VS Code", "Open the ui folder", "lui ui menu vscode code", 67);

        if (ViewModel.CanModifyProject)
            yield return PaletteAction("clean-xpaks", "Clean XPaks", "Move generated .xpak files to the Recycle Bin", "clean xpaks delete recycle", 68);
    }

    private const string ThemeActionPrefix = "theme-";

    private static readonly (ThemeChoice Choice, string Name, string Detail, string Words)[] ThemePaletteEntries =
    [
        (ThemeChoice.System, "Match Windows theme", "Graphite when Windows is dark, Light when it's light", "system windows auto"),
        (ThemeChoice.Graphite, "Graphite theme", "Neutral dark greys", "grey gray neutral"),
        (ThemeChoice.Slate, "Slate theme", "Blue-black dark", "blue navy"),
        (ThemeChoice.Light, "Light theme", "White cards on a pale window", "white day"),
    ];

    /// <summary>
    /// Whether a palette action would do something right now. The palette lists only those, so
    /// picking one never answers with "not possible"; the action's own button explains why it's off.
    /// </summary>
    private bool CanRunPaletteAction(string key)
    {
        if (ViewModel is not { } vm)
            return false;

        if (key.StartsWith(ToolActionPrefix, StringComparison.OrdinalIgnoreCase))
            return true;
        if (key.StartsWith(BuildPresetActionPrefix, StringComparison.Ordinal))
            return vm.CanEditBuildOptions;

        return key switch
        {
            "asset-editor" => vm.OpenAssetEditorCommand.CanExecute(null),
            "radiant" => vm.OpenRadiantCommand.CanExecute(null),
            "apex" => vm.OpenApexCommand.CanExecute(null),
            "build" => vm.CanStartBuildOnly,
            "build-run-once" or "build-run-ignore-errors-once" => vm.CanStartBuildAndRun,
            "run" => vm.CanRun,
            "close-game" => vm.CanStopGame,
            "publish" => !vm.IsPublishing && !_isOpeningPublish,
            "clear-log" => vm.CanClearLog,
            "duplicate-project" or "rename-project" or "delete-project" or "clean-xpaks" => vm.CanModifyProject,
            "select-all-build-steps" or "save-build-preset" or "set-build-mode-build" or "set-build-mode-build-run"
                or "set-build-mode-ignore-errors" => vm.CanEditBuildOptions,
            "new-map" => !_isOpeningNewItem,
            "project-details" => !_isOpeningProjectInfo,
            "open-zone" => vm.HasZoneFile,
            "open-zone-source-folder" => vm.HasZoneSourceFolder,
            "open-map-source" => vm.HasMapSourceFile,
            "open-map-source-folder" => vm.HasMapSourceFolder,
            "open-szc" => vm.HasSzcFile,
            "open-workshop-content-folder" => vm.HasContentFolder,
            "open-workshop-json" => vm.HasWorkshopJson,
            _ => true,

        };
    }

    private static CommandPaletteAction PaletteAction(
        string key,
        string name,
        string detail,
        string keywords,
        int priority,
        string typeLabel = "",
        string shortcut = "") => new()
        {
            Key = key,
            Name = name,
            Detail = detail,
            SearchKeywords = keywords,
            Priority = priority,
            TypeLabel = typeLabel,
            Shortcut = shortcut,
        };

    private const string ToolActionPrefix = "tool:";
    private const string BuildPresetActionPrefix = "preset:";

    private static string GetToolActionKey(string exePath) => ToolActionPrefix + exePath;

    private async System.Threading.Tasks.Task ExecuteCommandPaletteEntryAsync(CommandPaletteEntry entry)
    {
        if (ViewModel is null)
            return;

        if (entry.Project is not null)
        {
            ViewModel.TrySelectProject(entry.Project);
            return;
        }

        // State can change while the palette is open (a build finishing, say): check again.
        if (!CanRunPaletteAction(entry.ActionKey))
            return;

        // Recorded once the action has run without throwing, never for one that couldn't start.
        await RunCommandPaletteActionAsync(entry);
        UpdateRecentCommandAction(entry.ActionKey);
    }

    private async System.Threading.Tasks.Task RunCommandPaletteActionAsync(CommandPaletteEntry entry)
    {
        if (ViewModel is null)
            return;

        if (entry.ActionKey.StartsWith(ThemeActionPrefix, StringComparison.Ordinal))
        {
            ViewModel.SetThemeCommand.Execute(AppTheme.Parse(entry.ActionKey[ThemeActionPrefix.Length..]));
            return;
        }

        if (entry.ActionKey.StartsWith(ToolActionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var exePath = entry.ActionKey[ToolActionPrefix.Length..];
            var tool = ViewModel.CustomTools.FirstOrDefault(t =>
                string.Equals(t.ExePath, exePath, StringComparison.OrdinalIgnoreCase));
            ViewModel.LaunchCustomToolCommand.Execute(tool);
            return;
        }

        if (entry.ActionKey.StartsWith(BuildPresetActionPrefix, StringComparison.Ordinal))
        {
            var presetName = entry.ActionKey[BuildPresetActionPrefix.Length..];
            ViewModel.ApplyBuildPreset(ViewModel.BuildPresets.FirstOrDefault(p =>
                string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase)));
            return;
        }

        switch (entry.ActionKey)
        {
            case "new-map":
                await OnNewItemAsync();
                break;
            case "setup-doctor":
                await ShowSetupDoctorAsync(showOnlyIfUnhealthy: false);
                break;
            case "asset-editor":
                ViewModel.OpenAssetEditorCommand.Execute(null);
                break;
            case "radiant":
                ViewModel.OpenRadiantCommand.Execute(null);
                break;
            case "apex":
                ViewModel.OpenApexCommand.Execute(null);
                break;
            case "export2rust":
                await ShowExport2RustAsync();
                break;
            case "gscode-library":
                ViewModel.OpenGsCodeLibraryCommand.Execute(null);
                break;
            case "bo3-source-explorer":
                ViewModel.OpenBo3SourceExplorerCommand.Execute(null);
                break;
            case "add-tool":
                await AddToolFromPickerAsync();
                break;
            case "open-logs":
                ViewModel.OpenLogFolderCommand.Execute(null);
                break;
            case "open-game-root":
                ViewModel.OpenGameRootCommand.Execute(null);
                break;
            case "open-tools-root":
                ViewModel.OpenToolsRootCommand.Execute(null);
                break;
            case "check-updates":
                await CheckForUpdatesAsync();
                break;
            case "close-game":
                ViewModel.StopGameCommand.Execute(null);
                break;
            case "copy-log":
                await CopyBuildLogAsync(MainWindowViewModel.LogExportKind.Full);
                break;
            case "copy-log-errors":
                await CopyBuildLogAsync(MainWindowViewModel.LogExportKind.Errors);
                break;
            case "copy-log-warnings":
                await CopyBuildLogAsync(MainWindowViewModel.LogExportKind.Warnings);
                break;
            case "export-log":
                await ExportBuildLogAsync(MainWindowViewModel.LogExportKind.Full);
                break;
            case "clear-log":
                await ClearLogAsync();
                break;
            case "build":
                ViewModel.BuildOnlyOnceCommand.Execute(null);
                break;
            case "build-run-once":
                ViewModel.BuildAndRunOnceCommand.Execute(null);
                break;
            case "build-run-ignore-errors-once":
                ViewModel.BuildAndRunIgnoreErrorsOnceCommand.Execute(null);
                break;
            case "run":
                ViewModel.RunProjectCommand.Execute(null);
                break;
            case "find-in-log":
                FocusLogSearch();
                break;
            case "publish":
                await OnPublishAsync();
                break;
            case "duplicate-project":
                await DuplicateSelectedProjectAsync();
                break;
            case "analyze-project":
                await AnalyzeSelectedProjectAsync();
                break;
            case "rename-project":
                await RenameSelectedProjectAsync(this);
                break;
            case "delete-project":
                await DeleteSelectedProjectAsync();
                break;
            case "select-all-build-steps":
                ViewModel.EnableAllCommand.Execute(null);
                break;
            case "save-build-preset":
                await SaveBuildPresetAsync();
                break;
            case "set-build-mode-build-run":
                ViewModel.SetBuildModeCommand.Execute("0");
                break;
            case "set-build-mode-build":
                ViewModel.SetBuildModeCommand.Execute("1");
                break;
            case "set-build-mode-ignore-errors":
                ViewModel.SetBuildModeCommand.Execute("2");
                break;
            case "set-launch-dev":
                ViewModel.LaunchConfigIndex = 0;
                break;
            case "set-launch-ship":
                ViewModel.LaunchConfigIndex = 1;
                break;
            case "set-launch-offline":
                ViewModel.LaunchOnline = false;
                break;
            case "set-launch-online":
                ViewModel.LaunchOnline = true;
                break;
            case "launch-settings":
                await ShowLaunchSettingsAsync();
                break;
            case "project-details":
                await ShowProjectDetailsAsync();
                break;
            case "copy-project-name":
                await CopySelectedProjectNameAsync();
                break;
            case "copy-project-folder":
                await CopySelectedProjectFolderPathAsync();
                break;
            case "toggle-favorite":
                ViewModel.ToggleFavoriteCommand.Execute(null);
                break;
            case "open-folder":
                ViewModel.OpenFolderCommand.Execute(null);
                break;
            case "open-zone":
                ViewModel.EditZoneFileCommand.Execute(null);
                break;
            case "open-map-source":
                ViewModel.EditMapSourceFileCommand.Execute(null);
                break;
            case "open-map-source-folder":
                ViewModel.OpenMapSourceFolderCommand.Execute(null);
                break;
            case "open-szc":
                ViewModel.EditSzcFileCommand.Execute(null);
                break;
            case "open-zone-source-folder":
                ViewModel.OpenZoneSourceFolderCommand.Execute(null);
                break;
            case "open-workshop-content-folder":
                ViewModel.OpenWorkshopContentFolderCommand.Execute(null);
                break;
            case "open-workshop-json":
                ViewModel.EditWorkshopJsonFileCommand.Execute(null);
                break;
            case "open-workshop-page":
                ViewModel.OpenWorkshopPageCommand.Execute(null);
                break;
            case "copy-workshop-url":
                await CopyWorkshopUrlAsync();
                break;
            case "copy-run-command":
                await CopyRunCommandAsync();
                break;
            case "copy-build-commands":
                await CopyBuildCommandsAsync();
                break;
            case "open-scripts":
                ViewModel.OpenScriptsInVsCodeCommand.Execute(null);
                break;
            case "open-lui":
                ViewModel.OpenLuiInVsCodeCommand.Execute(null);
                break;
            case "clean-xpaks":
                await CleanSelectedXPaksAsync();
                break;
        }
    }

    private void UpdateRecentCommandAction(string actionKey)
    {
        if (string.IsNullOrWhiteSpace(actionKey))
            return;

        var settings = App.Services.GetRequiredService<ISettingsService>();
        settings.RecentCommandActions.RemoveAll(key => string.Equals(key, actionKey, StringComparison.OrdinalIgnoreCase));
        settings.RecentCommandActions.Insert(0, actionKey);
        if (settings.RecentCommandActions.Count > 8)
            settings.RecentCommandActions.RemoveRange(8, settings.RecentCommandActions.Count - 8);
        ViewModel?.QueueSettingsSave();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async System.Threading.Tasks.Task ClearLogAsync()
    {
        if (ViewModel is null || !ViewModel.HasSelectedProject || !ViewModel.CanClearLog)
            return;

        // Clearing does more than empty the pane: it deletes the persisted log file
        // and discards the stored build result for the project, with no undo.
        var summary = new List<string>();
        if (ViewModel.HasErrors)
            summary.Add($"{ViewModel.ErrorCount} error{(ViewModel.ErrorCount == 1 ? "" : "s")}");
        if (ViewModel.HasWarnings)
            summary.Add($"{ViewModel.WarningCount} warning{(ViewModel.WarningCount == 1 ? "" : "s")}");

        var detail = summary.Count > 0
            ? $"\n\nThe current result ({string.Join(" and ", summary)}) will be discarded."
            : "";

        var confirmed = await ShowConfirmAsync(
            "Clear the build log?",
            $"This deletes the saved log file and the last build result for this project, and can't be undone.{detail}",
            "Clear log",
            destructive: true);

        if (confirmed == ConfirmChoice.Confirm && ViewModel.CanClearLog)
            ViewModel.ClearLogCommand.Execute(null);
    }

    // Nothing to confirm: only the shortcut goes, and dropping the .exe on the title bar brings it back.
    private void OnRemoveCustomToolClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && (sender as MenuItem)?.Tag is ToolShortcutViewModel tool)
            ViewModel.RemoveCustomToolCommand.Execute(tool);
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        await ShowMessageAsync(
            "Blackbird",
            $"Version {AppUpdates.CurrentVersion}\n\nA launcher for the Black Ops III Mod Tools.");
    }

    /// <summary>
    /// The palette's Check for updates: the outcome goes to the status bar (up to date, or why it couldn't check); a
    /// newer release shows up in the title bar's update control. Help > Updates shows the same outcome in its menu.
    /// </summary>
    private async System.Threading.Tasks.Task CheckForUpdatesAsync()
    {
        if (ViewModel is not { } vm)
            return;
        await vm.Updates.CheckAndReportAsync(vm.ShowTransientStatus, text =>
        {
            if (text.Length > 0)
                vm.ShowTransientStatus(text);
        });
    }

    /// <summary>
    /// Help > Updates' status line names the update the title bar control holds; clicking it opens the control's flyout
    /// (the menu has closed by then).
    /// </summary>
    private void OnUpdateStatusClick(object? sender, RoutedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<Button>("UpdateButton") is { IsEffectivelyVisible: true, Flyout: { IsOpen: false } flyout } button)
                flyout.ShowAt(button);
        }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>
    /// Check now found a newer release: the update control appears in the title bar, so the menu gets out of its way
    /// rather than turning into a download under the cursor.
    /// </summary>
    private void OnUpdateButtonShown(bool shown)
    {
        if (shown && this.FindControl<MenuItem>("UpdatesMenu") is { IsSubMenuOpen: true }
            && this.FindControl<Menu>("TitleMenu") is { } menu)
            menu.Close();
    }

    private void OnUpdateFlyoutClosed(object? sender, EventArgs e) => ViewModel?.Updates.Dismiss();

    private readonly record struct ProjectWorkshopDetails(string Summary, IReadOnlyList<WorkshopVersionRow> Versions);

    private async System.Threading.Tasks.Task ShowLaunchSettingsAsync()
    {
        // The launch config belongs to the selected project; the button and palette entry need one.
        if (ViewModel is not { SelectedProject: { IsGroupHeader: false } project } mainVm)
            return;

        var settings = App.Services.GetRequiredService<ISettingsService>();
        var configName = mainVm.LaunchConfigs[mainVm.LaunchConfigIndex];
        var projSettings = settings.Projects.TryGetValue(project.Key, out var existing)
            ? existing
            : new Models.ProjectSettings();
        projSettings.EnvironmentDvars.TryGetValue(configName, out var dvars);

        var vm = new DvarsDialogViewModel
        {
            ScopeLabel = $"{project.DisplayName} · {configName}",
            IsProjectScoped = true,
        };
        vm.Load(dvars ?? MainWindowViewModel.GetLaunchConfigPreset(configName), MainWindowViewModel.GetLaunchConfigPreset(configName), $"Reset to {configName} defaults");
        vm.RunOptions = mainVm.RunOptions;

        var dialog = new DvarsDialog { DataContext = vm };
        if (!await dialog.ShowDialog<bool>(this))
            return;

        projSettings.EnvironmentDvars[configName] = vm.GetValues();
        settings.Projects[project.Key] = projSettings;
        mainVm.QueueSettingsSave();
        mainVm.SetRunDvars(vm.BuildRunDvars());
        mainVm.RunOptions = vm.RunOptions;
    }


    private async void OnSetupDoctorClick(object? sender, RoutedEventArgs e)
    {
        await ShowSetupDoctorAsync(showOnlyIfUnhealthy: false);
    }

    private async void OnDeleteProjectClick(object? sender, RoutedEventArgs e)
    {
        await DeleteSelectedProjectAsync();
    }

    private async System.Threading.Tasks.Task DeleteSelectedProjectAsync()
    {
        // The menu items are disabled in both cases; this guards the palette and shortcuts.
        if (ViewModel?.SelectedProject is not { IsGroupHeader: false } project || !ViewModel.CanModifyProject)
            return;

        // No confirmation: the folder goes to the Recycle Bin (Windows asks first if it can't),
        // and Blackbird keeps its settings so a restore brings everything back.
        try
        {
            ViewModel.StatusText = await ViewModel.MoveSelectedProjectToRecycleBinAsync()
                ? $"Moved {project.DisplayName} to the Recycle Bin. Restore it from there to bring it back."
                : $"Kept {project.DisplayName}. Nothing was deleted.";
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Couldn't delete the project", ErrorText.Describe("It couldn't be moved to the Recycle Bin.", ex));
        }

    }

    private async void OnCleanXPaksClick(object? sender, RoutedEventArgs e)
    {
        await CleanSelectedXPaksAsync();
    }

    private async System.Threading.Tasks.Task CleanSelectedXPaksAsync()
    {
        if (ViewModel?.SelectedProject is not { IsGroupHeader: false } project || !ViewModel.CanModifyProject)
            return;

        // The Recycle Bin is the undo, so this doesn't ask first.
        try
        {
            var moved = await ViewModel.MoveSelectedProjectXPaksToRecycleBinAsync();
            ViewModel.StatusText = moved switch
            {
                null => "Stopped cleaning XPaks. Nothing was deleted permanently.",
                0 => $"{project.DisplayName} has no XPaks to clean.",
                _ => $"Moved {moved} XPak{(moved == 1 ? "" : "s")} to the Recycle Bin.",

            };
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Couldn't clean XPaks", ErrorText.Describe("Some XPaks couldn't be moved to the Recycle Bin.", ex));
        }
    }

    // ===== Dropdown menus built when they open =====
    // These list things that change at runtime (presets, filters, languages, maps),
    // so they are built fresh on each open with the current choice ticked.

    private static MenuItem MenuEntry(string header, bool isChecked, Action onClick, bool isEnabled = true, string? gesture = null)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = isEnabled,
            Icon = new TextBlock
            {
                Text = "",
                FontFamily = Application.Current?.FindResource("IconFont") as FontFamily ?? FontFamily.Default,
                FontSize = 12,
                IsVisible = isChecked,
            },
        };
        if (gesture is not null)
            item.InputGesture = KeyGesture.Parse(gesture);
        item.Click += (_, _) => onClick();
        return item;
    }

    private static void ReplaceMenuItems(object? flyout, IEnumerable<Control> items)
    {
        if (flyout is not MenuFlyout menu)
            return;

        menu.Items.Clear();
        foreach (var item in items)
            menu.Items.Add(item);
    }

    /// <summary>Launch config menu: Dev or Ship, Offline/Online, build language, a mod's quick-launch map, and the full settings.</summary>
    private void OnLaunchConfigMenuOpening(object? sender, EventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        var items = new List<Control>
        {
            MenuEntry(LaunchConfig.Dev, vm.IsDevLaunchConfig, () => vm.LaunchConfigIndex = 0),
            MenuEntry(LaunchConfig.Ship, vm.IsShipLaunchConfig, () => vm.LaunchConfigIndex = 1),
            new Separator(),
            MenuEntry("Offline", !vm.LaunchOnline, () => vm.LaunchOnline = false),
            MenuEntry("Online through Steam", vm.LaunchOnline, () => vm.LaunchOnline = true),
            new Separator(),
        };

        var languages = new MenuItem { Header = "Build language" };
        for (var i = 0; i < MainWindowViewModel.BuildLanguageSettingValues.Length; i++)
        {
            var index = i;
            languages.Items.Add(MenuEntry(
                i == 0 ? vm.DefaultBuildLanguageOptionLabel : MainWindowViewModel.GetBuildLanguageLabel(i - 1),
                vm.SelectedBuildLanguageIndex == i,
                () => vm.SetActiveBuildLanguage(MainWindowViewModel.GetBuildLanguageSettingValue(index))));
        }
        items.Add(languages);

        if (vm.HasQuickLaunchMapOptions)
        {
            var maps = new MenuItem { Header = "Quick launch map" };
            foreach (var map in vm.QuickLaunchMaps)
            {
                var name = map;
                maps.Items.Add(MenuEntry(name, string.Equals(vm.SelectedQuickLaunchMap, name, StringComparison.OrdinalIgnoreCase),
                    () => vm.SelectedQuickLaunchMap = name));
            }
            items.Add(maps);
        }

        items.Add(new Separator());
        var edit = new MenuItem { Header = "Edit launch config…" };
        edit.Click += async (_, _) => await ShowLaunchSettingsAsync();
        items.Add(edit);

        ReplaceMenuItems(sender, items);
    }

    /// <summary>Build preset: apply one, tick everything, save the ticked steps, or delete presets.</summary>
    private void OnBuildPresetMenuOpening(object? sender, EventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        var matching = vm.MatchingBuildPreset();
        var items = new List<Control>();
        foreach (var preset in vm.BuildPresets)
        {
            var target = preset;
            var item = MenuEntry(preset.Name, ReferenceEquals(preset, matching), () => vm.ApplyBuildPreset(target));
            ToolTip.SetTip(item, preset.Summary);
            items.Add(item);
        }

        if (items.Count > 0)
            items.Add(new Separator());

        items.Add(MenuEntry(BuildPreset.AllStepsName, matching is null && vm.BuildPresetLabel == BuildPreset.AllStepsName, () => vm.EnableAllCommand.Execute(null)));
        items.Add(new Separator());

        var save = new MenuItem { Header = "Save steps as build preset…" };
        save.Click += async (_, _) => await SaveBuildPresetAsync();
        items.Add(save);

        if (vm.BuildPresets.Count > 0)
        {
            var delete = new MenuItem { Header = "Delete preset" };
            foreach (var preset in vm.BuildPresets)
            {
                var target = preset;
                var item = new MenuItem { Header = $"{preset.Name}…" };
                item.Click += async (_, _) => await DeleteBuildPresetAsync(target);
                delete.Items.Add(item);
            }
            items.Add(delete);
        }

        ReplaceMenuItems(sender, items);
    }

    private async System.Threading.Tasks.Task SaveBuildPresetAsync()
    {
        if (ViewModel is not { HasSelectedProject: true } vm)
            return;

        var suggested = vm.MatchingBuildPreset()?.Name ?? "";
        await TextPromptDialog.ShowAsync(
            this,
            "Save build preset",
            "Name",
            "Save",
            text => text.Length == 0 ? "Enter a name."
                : text.Length > 40 ? "Use 40 characters or fewer."
                : text is BuildPreset.AllStepsName or BuildPreset.CustomName ? "That name is taken by Blackbird. Choose another."
                : null,
            initialText: suggested,
            subtitle: "Saves which steps are ticked now. A preset with the same name is replaced.",
            maxLength: 40,
            commit: name =>
            {
                vm.SaveCurrentBuildPreset(name);
                vm.ShowTransientStatus($"Saved build preset “{name}”.");
                return System.Threading.Tasks.Task.CompletedTask;
            });
    }

    private async System.Threading.Tasks.Task DeleteBuildPresetAsync(BuildPreset preset)
    {
        if (ViewModel is null)
            return;

        if (await ShowConfirmAsync(
                $"Delete the build preset '{preset.Name}'?",
                "Your build steps stay as they are. The preset can't be restored.",
                "Delete",
                destructive: true) != ConfirmChoice.Confirm)
        {
            return;
        }

        ViewModel.DeleteBuildPreset(preset);
    }

    /// <summary>The picker's filter: scopes, then custom categories.</summary>
    private void OnProjectFilterMenuOpening(object? sender, EventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        var items = new List<Control>();
        for (var i = 0; i < vm.ProjectFilters.Count; i++)
        {
            var index = i;
            // Built-in scopes, then a divider before the user's own categories.
            if (i == MainWindowViewModel.BuiltInProjectFilterCount)
                items.Add(new Separator());
            items.Add(MenuEntry(vm.ProjectFilters[i], vm.SelectedProjectFilterIndex == i, () => vm.SelectedProjectFilterIndex = index));
        }

        ReplaceMenuItems(sender, items);
    }

    // ===== Categories, from a category header's right-click menu =====

    private static readonly string[] DefaultCategories = ["Maps", "Mods"];

    private void OnCategoryMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Moving projects back only means something for a category the user made.
        if (sender is ContextMenu menu
            && menu.Items.Count > 1
            && menu.Items[1] is MenuItem reset
            && reset.Tag is string category)
        {
            reset.IsVisible = !DefaultCategories.Contains(category, StringComparer.OrdinalIgnoreCase);
        }
    }

    private async void OnRenameCategoryClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || (sender as MenuItem)?.Tag is not string category)
            return;

        var existing = ViewModel.ProjectCatalog
            .Select(p => string.IsNullOrWhiteSpace(p.Category) ? p.DefaultCategory : p.Category.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var vm = ViewModel;
        await TextPromptDialog.ShowAsync(
            this,
            $"Rename {category}",
            "New name",
            "Rename",
            text => text.Length == 0 ? "Enter a name."
                : text.Length > 40 ? "Use 40 characters or fewer."
                : string.Equals(text, category, StringComparison.OrdinalIgnoreCase) ? "Choose a different name."
                : null,
            initialText: category,
            subtitle: "Using the name of another category merges the two.",
            maxLength: 40,
            commit: async target =>
            {
                var merging = existing.Contains(target, StringComparer.OrdinalIgnoreCase);
                var count = await vm.MoveProjectsInCategoryAsync(category, target, resetToDefaults: false);
                vm.ShowTransientStatus(merging
                    ? $"Merged {category} into {target} ({count} project{(count == 1 ? "" : "s")})."
                    : $"Renamed {category} to {target}.");
            });
    }

    private async void OnResetCategoryClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || (sender as MenuItem)?.Tag is not string category)
            return;

        var count = await ViewModel.MoveProjectsInCategoryAsync(category, "", resetToDefaults: true);
        ViewModel.ShowTransientStatus(
            $"Moved {count} project{(count == 1 ? "" : "s")} from {category} back to Maps and Mods.");
    }

    private async void OnProjectDetailsClick(object? sender, RoutedEventArgs e)
    {
        await ShowProjectDetailsAsync();
    }

    // Set from the first await until the dialog closes, so a second Ctrl+I or palette run
    // can't open another one while the Workshop details load.
    private bool _isOpeningProjectInfo;

    private async System.Threading.Tasks.Task ShowProjectDetailsAsync()
    {
        if (_isOpeningProjectInfo || ViewModel?.SelectedProject is not { IsGroupHeader: false } project)
            return;

        _isOpeningProjectInfo = true;
        try
        {
            await ShowProjectDetailsCoreAsync(project);
        }
        finally
        {
            _isOpeningProjectInfo = false;
        }
    }

    private async System.Threading.Tasks.Task ShowProjectDetailsCoreAsync(ProjectItem project)
    {
        if (ViewModel is null)
            return;

        var fileSystem = App.Services.GetRequiredService<IFileSystemService>();
        var workshopDetails = await System.Threading.Tasks.Task.Run(() => BuildProjectWorkshopDetails(fileSystem, project));

        // The user switched projects while the details loaded: these belong to the old one.
        if (ViewModel.SelectedProject?.Key != project.Key)
            return;

        var vm = new ProjectDetailsDialogViewModel
        {
            ProjectType = project.Type,
            DefaultCategory = project.DefaultCategory,
            HeaderName = project.DisplayName,
            ProjectName = project.Name,
            FolderPath = project.FolderPath,
            WorkshopSummary = workshopDetails.Summary,
            WorkshopVersions = workshopDetails.Versions,
            RenameBlockedReason = ViewModel.ModifyProjectBlockedReason,
            DisplayName = project.DisplayName == project.Name ? "" : project.DisplayName,
            Category = project.Category,
            Notes = project.Notes,
        };
        AddCategorySuggestions(vm.CategorySuggestions);

        var dialog = new ProjectDetailsDialog { DataContext = vm };
        vm.RenameFolderAsync = async () =>
        {
            if (await RenameSelectedProjectAsync(dialog) is { } renamed)
                vm.ApplyRename(renamed.Name, renamed.FolderPath);
        };

        // Runs while the dialog is open, so a failure keeps the edits. A folder rename inside
        // the dialog has already switched the selection to the renamed project, so these land
        // on the right one.
        vm.SaveAsync = () => ViewModel.UpdateSelectedProjectMetadataAsync(
            vm.DisplayName, vm.Category, ViewModel.SelectedProject?.IsFavorite ?? project.IsFavorite, vm.Notes);

        await dialog.ShowDialog<bool>(this);
    }

    private static ProjectWorkshopDetails BuildProjectWorkshopDetails(IFileSystemService fileSystem, ProjectItem project)
    {
        var workshopFolder = Path.Combine(project.FolderPath, "zone");
        var workshopJsonPath = Path.Combine(workshopFolder, "workshop.json");
        var hasWorkshopMetadata = File.Exists(workshopJsonPath) || WorkshopFiles.ExistingProfilesPath(workshopFolder) is not null;

        if (!hasWorkshopMetadata)
            return new ProjectWorkshopDetails("Not published to the Workshop.", []);

        var currentWorkshopJson = fileSystem.ReadWorkshopJson(workshopFolder);
        var profiles = fileSystem.ReadWorkshopProfiles(workshopFolder, currentWorkshopJson);
        profiles.Normalize(currentWorkshopJson);

        var rows = profiles.Profiles
            .Select(profile => new WorkshopVersionRow(
                profile.Name,
                string.Equals(profile.Id, profiles.ActiveProfileId, StringComparison.OrdinalIgnoreCase),
                profile.WorkshopJson.PublisherId.Trim()))
            .ToList();
        return new ProjectWorkshopDetails("", rows);
    }

    private void AddCategorySuggestions(ICollection<string> target)
    {
        if (ViewModel is null)
            return;

        var categories = ViewModel.ProjectCatalog
            .Where(project => !project.IsGroupHeader)
            .Select(project => string.IsNullOrWhiteSpace(project.Category) ? project.DefaultCategory : project.Category.Trim())
            .Concat(["Maps", "Mods"])
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase);

        foreach (var category in categories)
            target.Add(category);
    }

    private async void OnDuplicateProjectClick(object? sender, RoutedEventArgs e)
    {
        await DuplicateSelectedProjectAsync();
    }

    private async void OnAnalyzeProjectClick(object? sender, RoutedEventArgs e)
    {
        await AnalyzeSelectedProjectAsync();
    }

    private async System.Threading.Tasks.Task AnalyzeSelectedProjectAsync()
    {
        if (ViewModel?.SelectedProject is not { IsGroupHeader: false } project)
            return;

        // One analysis window. Analyzing again (this project or another) brings it
        // forward and re-runs for the current selection, since files may have changed.
        if (_projectAnalysisDialog is { DataContext: ProjectAnalysisDialogViewModel openVm } open)
        {
            ActivateToolWindow(open);
            await openVm.AnalyzeAsync(project);
            return;
        }

        var vm = new ProjectAnalysisDialogViewModel(App.Services.GetRequiredService<IProjectAnalysisService>());
        var dialog = new ProjectAnalysisDialog { DataContext = vm };
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_projectAnalysisDialog, dialog))
                _projectAnalysisDialog = null;
        };
        _projectAnalysisDialog = dialog;

        // The window opens at once and shows progress itself while the analysis runs.
        dialog.Show(this);
        await vm.AnalyzeAsync(project);
    }

    private ProjectAnalysisDialog? _projectAnalysisDialog;

    private async System.Threading.Tasks.Task DuplicateSelectedProjectAsync()
    {
        if (ViewModel?.SelectedProject is not { IsGroupHeader: false } project || !ViewModel.CanModifyProject)
            return;

        var vm = new DuplicateProjectDialogViewModel
        {
            SourceName = project.Name,
            SourceDisplayName = project.DisplayName,
            ProjectType = project.Type,
            DefaultCategory = project.Category,
            ExistingProjects = ViewModel.ProjectCatalog.ToList(),
            MapSourceConflict = name => ViewModel.MapSourceConflict(project.Type, project.Name, name),
        };
        AddCategorySuggestions(vm.CategorySuggestions);
        vm.SeedDefaults();

        // Runs while the dialog is open, so a failure keeps the user's input.
        vm.DuplicateAsync = async form =>
        {
            var newName = form.NewName.Trim();
            await ViewModel.DuplicateSelectedProjectAsync(newName, form.DisplayName, form.Category);
            ViewModel.StatusText = $"Duplicated {project.Name} as {newName}.";
        };

        await new DuplicateProjectDialog { DataContext = vm }.ShowDialog<bool>(this);
    }

    /// <summary>
    /// Renames the selected project's folder from a dialog over <paramref name="owner"/>.
    /// Returns the new name and folder, or null if nothing was renamed.
    /// </summary>
    private async System.Threading.Tasks.Task<(string Name, string FolderPath)?> RenameSelectedProjectAsync(Window owner)
    {
        if (ViewModel?.SelectedProject is not { IsGroupHeader: false } project || !ViewModel.CanModifyProject)
            return null;

        (string Name, string FolderPath)? renamed = null;
        MainWindowViewModel.PartialRenameException? partial = null;

        var vm = new RenameProjectDialogViewModel
        {
            CurrentName = project.Name,
            ProjectType = project.Type,
            FolderPath = project.FolderPath,
            ExistingProjects = ViewModel.ProjectCatalog.ToList(),
            MapSourceConflict = name => ViewModel.MapSourceConflict(project.Type, project.Name, name),
            NewName = project.Name,
        };

        vm.RenameAsync = async newName =>
        {
            // Pass the stored display name and category through unchanged; an unset
            // display name follows the folder name.
            var displayName = project.DisplayName == project.Name ? "" : project.DisplayName;
            try
            {
                var folder = await ViewModel.RenameSelectedProjectAsync(newName, displayName, project.Category);
                renamed = (newName, folder);
            }
            catch (MainWindowViewModel.PartialRenameException ex)
            {
                // The folder did move: the rename happened, with loose ends to report.
                partial = ex;
                renamed = (newName, Path.Combine(Path.GetDirectoryName(project.FolderPath) ?? "", newName));
            }

            ViewModel.StatusText = $"Renamed {project.Name} to {newName}.";
        };

        await new RenameProjectDialog { DataContext = vm }.ShowDialog<bool>(owner);

        if (partial is not null && renamed is { } done)
        {
            await MessageDialog.ShowAsync(owner, "Rename incomplete",
                $"The folder was renamed to {done.Name}, but some files inside it couldn't be updated, so they may still use "
                + $"the old name {project.Name}. {ErrorText.Describe(partial.InnerException ?? partial)}",
                folderPath: done.FolderPath);
        }

        return renamed;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (ViewModel is not null)
        {
            ViewModel.NewItemCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(OnNewItemAsync);
            ViewModel.PublishCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(OnPublishAsync);
        }

        try
        {
            if (ViewModel is not null)
                await ViewModel.InitializeAsync();

            var settingsLoadError = App.Services.GetRequiredService<ISettingsService>().LoadError;
            if (settingsLoadError is not null)
                await ShowMessageAsync("Settings couldn't be loaded", settingsLoadError);

            if (ViewModel?.HasSetupIssues == true)
                await ShowFirstRunAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Startup failed", ex);
            await ShowMessageAsync("Blackbird couldn't start", $"Loading your projects and settings didn't finish. Details are in {AppPaths.CrashLog}.");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Let a Workshop draft saved as the Publish dialog closed reach the disk. A failed
        // write was already shown in the dialog, so WhenAny keeps it from throwing here.
        System.Threading.Tasks.Task.WhenAny(_workshopWrites).Wait(TimeSpan.FromSeconds(2));
        SaveWindowBounds();
        base.OnClosed(e);
    }

    // Closing the app shuts Steam down, which abandons an upload in flight, so
    // closing mid-publish asks first.
    private bool _closeDuringPublishConfirmed;
    private bool _isOpeningPublish;
    private System.Threading.CancellationTokenSource? _publishCts;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
            return;

        // A GDT cleanup rewrites file after file; stopping between two would leave a project
        // half-cleaned, so the app closes once it finishes.

        if (_projectAnalysisDialog?.DataContext is ProjectAnalysisDialogViewModel { IsCleaning: true } cleaning)
        {
            e.Cancel = true;
            ViewModel?.ShowTransientStatus("Closing once the GDT cleanup finishes…");
            await cleaning.CleanupCompletion;
            Close();
            return;
        }

        if (ViewModel?.IsPublishing != true || _closeDuringPublishConfirmed)
        {
            // A build running when the window closes stops with it: the tools it started aren't left orphaned.
            ViewModel?.CancelBuildOnClose();
            return;
        }

        e.Cancel = true;
        var choice = await ShowConfirmAsync(
            PublishDialogViewModel.StopUploadTitle,
            PublishDialogViewModel.StopUploadMessage,
            PublishDialogViewModel.StopUploadButton,
            destructive: true);
        if (choice != ConfirmChoice.Confirm)
            return;

        _closeDuringPublishConfirmed = true;
        _publishCts?.Cancel();
        Close();
    }

    /// <summary>
    /// The settings-page view of the setup checks. Returns true if the user saved.
    /// </summary>
    /// <param name="owner">The window it opens over; the main window when null.</param>
    private async System.Threading.Tasks.Task<bool> ShowSetupDoctorAsync(bool showOnlyIfUnhealthy, Window? owner = null)
    {
        var fileSystem = App.Services.GetRequiredService<IFileSystemService>();
        var validation = await System.Threading.Tasks.Task.Run(() => fileSystem.ValidateSetup());

        if (showOnlyIfUnhealthy && validation.IsHealthy)
            return false;

        var vm = new SetupDoctorViewModel(fileSystem);
        vm.Load(validation);

        var dialog = new SetupDoctorDialog { DataContext = vm };
        var result = await dialog.ShowDialog<bool>(owner ?? this);

        if (result && ViewModel is not null)
            await ViewModel.RefreshSetupAsync();

        return result;
    }

    /// <summary>
    /// What a new user sees first: finds Black Ops III on its own and says plainly
    /// what it found. Cancelling leaves the main window's empty state to explain.
    /// </summary>
    private async System.Threading.Tasks.Task ShowFirstRunAsync()
    {
        var vm = new FirstRunViewModel(App.Services.GetRequiredService<IFileSystemService>());
        var dialog = new FirstRunDialog
        {
            DataContext = vm,
            OpenSetupDoctorAsync = owner => ShowSetupDoctorAsync(showOnlyIfUnhealthy: false, owner),
        };

        if (await dialog.ShowDialog<bool>(this) && ViewModel is not null)
            await ViewModel.RefreshSetupAsync();
    }

    private async void OnFindGameClick(object? sender, RoutedEventArgs e)
    {
        await ShowFirstRunAsync();
    }

    // The palette calls OnNewItemAsync directly, past NewItemCommand's own concurrency guard.
    private bool _isOpeningNewItem;

    private async System.Threading.Tasks.Task OnNewItemAsync()
    {
        if (_isOpeningNewItem || ViewModel is null)
            return;

        _isOpeningNewItem = true;
        try
        {
            await ShowNewItemCoreAsync();
        }
        finally
        {
            _isOpeningNewItem = false;
        }
    }

    private async System.Threading.Tasks.Task ShowNewItemCoreAsync()
    {
        if (ViewModel is null)
            return;

        var fileSystem = App.Services.GetRequiredService<IFileSystemService>();
        var templateService = App.Services.GetRequiredService<ITemplateService>();

        var templates = await System.Threading.Tasks.Task.Run(fileSystem.ScanMapTemplates);
        var vm = new NewItemDialogViewModel
        {
            TemplatesFolder = Path.Combine(fileSystem.ToolsPath, "rex", "templates"),
        };
        vm.ReplaceTemplates(templates);
        foreach (var project in ViewModel.ProjectCatalog.Where(p => !p.IsGroupHeader))
            vm.ExistingProjects.Add(project);
        AddCategorySuggestions(vm.CategorySuggestions);

        vm.OpenSetupDoctorAsync = async owner =>
        {
            await ShowSetupDoctorAsync(showOnlyIfUnhealthy: false, owner);
            return await System.Threading.Tasks.Task.Run(fileSystem.ScanMapTemplates);
        };

        // Runs while the dialog is open: a failure leaves it up with the error and
        // everything the user typed.
        vm.CreateAsync = async form =>
        {
            var name = form.TrimmedName;
            var type = form.ProjectType;
            if (type == ProjectType.Mod)
                await templateService.CreateModAsync(name, form.GetSelectedModZoneNames());
            else
                await templateService.CreateFromTemplateAsync(name, form.Templates[form.SelectedTemplateIndex]);

            var typeWord = type == ProjectType.Map ? "map" : "mod";
            var opened = await ViewModel.OpenCreatedProjectAsync(name, type, form.DisplayName, form.Category);
            ViewModel.StatusText = opened
                ? $"Created {typeWord} '{name}'."
                : $"Created {typeWord} '{name}'. Switch to it once the build finishes.";
        };

        await new NewItemDialog { DataContext = vm }.ShowDialog<bool>(this);
    }

    private async System.Threading.Tasks.Task OnPublishAsync()
    {
        // Every entry point is disabled without a project; this guards the shortcut.
        if (ViewModel?.SelectedProject is null || ViewModel.SelectedProject.IsGroupHeader)
            return;

        if (ViewModel.IsPublishing)
        {
            ViewModel.ShowTransientStatus("An upload to Steam Workshop is already running.");
            return;
        }

        if (_isOpeningPublish)
            return;

        var fileSystem = App.Services.GetRequiredService<IFileSystemService>();
        var steam = App.Services.GetRequiredService<ISteamWorkshopService>();
        var settings = App.Services.GetRequiredService<ISettingsService>();

        var project = ViewModel.SelectedProject;
        var (folder, type) = project.Type == ProjectType.Map
            ? ($"usermaps/{project.Name}", "map")
            : ($"mods/{project.Name}", "mod");
        var folderName = project.Name;
        var workshopFolder = Path.Combine(fileSystem.GamePath, folder, "zone");

        // Steam start-up, the Workshop files and the preflight walk of the project folder
        // all run off the UI thread; the dialog opens once they're in. Nothing here writes.
        WorkshopProfilesData profilesData;
        List<PublishPreflightCheck> checks;
        string? mediaFolder;
        _isOpeningPublish = true;
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (_isOpeningPublish)
                ViewModel?.ShowTransientStatus("Loading Workshop details…");
        }, TimeSpan.FromMilliseconds(150));
        try
        {
            await steam.InitializeAsync();
            var xpaks = await ViewModel.GetSelectedProjectXPakFilesAsync();
            var (lastPrep, shipLanguage) = ReadPublishSettings(settings, project.Key);
            (profilesData, checks, mediaFolder) = await System.Threading.Tasks.Task.Run(() =>
            {
                var currentWorkshopJson = fileSystem.ReadWorkshopJson(workshopFolder);
                return (
                    fileSystem.ReadWorkshopProfiles(workshopFolder, currentWorkshopJson),
                    PublishPreflightBuilder.Build(project, fileSystem, steam, workshopFolder, xpaks, lastPrep, shipLanguage),
                    WorkshopFiles.ExistingMediaFolder(workshopFolder));
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write("Opening Publish failed", ex);
            await ShowMessageAsync("Publish couldn't open",
                ErrorText.Describe("Blackbird couldn't read this project's Workshop details.", ex));
            return;
        }
        finally
        {
            _isOpeningPublish = false;
        }

        // The user switched projects while this was loading.
        if (ViewModel.SelectedProject?.Key != project.Key)
            return;

        var activeProfile = profilesData.GetActiveProfile();
        EnsureWorkshopIdentity(activeProfile.WorkshopJson, type, folderName);

        var existingData = activeProfile.WorkshopJson.Clone();

        ulong fileId = 0;
        if (ulong.TryParse(existingData.PublisherId, out var id))
            fileId = id;

        // Steam work started from the dialog stops waiting when it closes.
        using var dialogLifetime = new System.Threading.CancellationTokenSource();

        // Fetching the live Workshop item can take a while, so the dialog opens straight away
        // and says so in the footer; a row appears only if Steam's copy differs.
        System.Threading.Tasks.Task<WorkshopRemoteItemData?>? remoteFetch = null;
        if (fileId != 0 && steam.IsInitialized)
            remoteFetch = steam.GetCurrentItemAsync(fileId, dialogLifetime.Token);

        var pubVm = new PublishDialogViewModel
        {
            ProjectName = project.DisplayName,
            ProjectTypeLabel = project.Type == ProjectType.Map ? "Map" : "Mod",
            WorkshopMediaFolder = mediaFolder ?? "",
            EstimatedSizeLabel = string.IsNullOrWhiteSpace(project.SizeLabel)
                ? ""
                : $"{project.SizeLabel} on disk",
            IsBuildRunning = ViewModel.IsBuildRunning,
        };
        pubVm.LoadProfiles(profilesData, type, folderName);
        pubVm.LoadChecks(checks);
        if (remoteFetch is not null)
            pubVm.ShowStatus(WorkshopSyncStatus);

        pubVm.OnProfilesChanged = (data, activeData) =>
        {
            EnsureWorkshopIdentity(activeData, type, folderName);
            _ = SaveDraftAsync(SaveWorkshopFilesAsync(fileSystem, workshopFolder, data, activeData, pubVm));
        };

        async System.Threading.Tasks.Task SaveDraftAsync(System.Threading.Tasks.Task write)
        {
            try
            {
                await write;
            }
            catch (Exception ex)
            {
                pubVm.ShowError(DescribeWorkshopFileError("Couldn't save the Workshop draft.", ex), ex.Message);
            }
        }

        var dialog = new PublishDialog { DataContext = pubVm };
        var refreshChecksAfterBuild = false;
        var syncSuperseded = false;

        pubVm.OnCheckAction = (kind, payload) =>
        {
            switch (kind)
            {
                case "setup-doctor":
                    _ = RunSetupDoctorFromDialogAsync(pubVm, dialog, project, fileSystem, steam, settings, workshopFolder);
                    break;
                case "retry-steam":
                    _ = RetrySteamAsync();
                    break;
                case "build-now":
                    if (ViewModel is null)
                        break;
                    if (!ViewModel.CanStartBuildOnly)
                    {
                        pubVm.ShowError(ViewModel.IsBuildRunning
                            ? "A build is already running."
                            : ViewModel.HasSetupIssues
                                ? "Run Setup Doctor before building."
                                : ViewModel.BuildValidationMessage is { Length: > 0 } reason ? reason : "The build couldn't start.");
                        break;
                    }
                    ViewModel.BuildOnlyOnceCommand.Execute(null);
                    if (ViewModel.IsBuildRunning)
                    {
                        refreshChecksAfterBuild = true;
                        pubVm.ShowStatus("Building. The checks refresh when it finishes.");
                    }
                    break;
                case "open-folder":
                    if (!string.IsNullOrEmpty(payload) && Directory.Exists(payload)
                        && ShellLauncher.TryOpen(payload) is { } openError)
                    {
                        pubVm.ShowError("Couldn't open the folder.", openError);
                    }
                    break;
                case "open-file":
                    if (!string.IsNullOrEmpty(payload) && ShellLauncher.TryOpen(payload) is { } fileError)
                        pubVm.ShowError($"Couldn't open {Path.GetFileName(payload)}.", fileError);
                    break;
                case "clean-xpaks":
                    _ = HandleCleanXpaksFromDialogAsync(pubVm);
                    break;
                case "ready-for-publish":
                    _ = RunReadyForPublishFromDialogAsync(pubVm, project, fileSystem, steam, settings, workshopFolder);
                    break;
                case "pull-workshop":
                    _ = PullFromWorkshopAsync();
                    break;
                case "dismiss-info":
                    pubVm.RemoveCheckByName(payload);
                    break;
            }
        };

        async System.Threading.Tasks.Task RetrySteamAsync()
        {
            pubVm.ShowStatus("Connecting to Steam…");
            var connected = await steam.InitializeAsync();
            if (!dialog.IsVisible)
                return;

            await RefreshPublishPreflightChecksAsync(pubVm, project, fileSystem, steam, settings, workshopFolder);
            if (connected)
                pubVm.ShowStatus("Connected to Steam");
            else
                pubVm.ShowError("Steam still isn't running. Start Steam and sign in, then try again.", steam.InitializationDetail);
        }

        // Replacing the draft can't be undone, so it asks once. Images already on disk stay.
        async System.Threading.Tasks.Task PullFromWorkshopAsync()
        {
            if (pubVm.IsPulling || !pubVm.IsEditing || pubVm.SelectedProfile is not { } version)
                return;

            var replace = await MessageDialog.ConfirmAsync(
                dialog,
                "Replace this Workshop version's draft with the copy on Steam?",
                $"The title, description, tags, visibility, thumbnail, and gallery of “{version.Name}” are replaced with what's on Steam now. The changelog stays, and image files already on disk aren't touched.",
                "Replace draft",
                destructive: true);
            if (!replace || !dialog.IsVisible || pubVm.IsPulling || !pubVm.IsEditing
                || !ReferenceEquals(pubVm.SelectedProfile, version))
            {
                return;
            }

            syncSuperseded = true;
            pubVm.IsPulling = true;
            try
            {
                await HandlePullWorkshopAsync(pubVm, steam, fileSystem, workshopFolder, type, folderName,
                    () => !dialog.IsVisible, dialogLifetime.Token);
            }
            catch (OperationCanceledException)
            {
                // The dialog closed while Steam answered; nothing was applied.
            }
            catch (Exception ex)
            {
                pubVm.ShowError(DescribeWorkshopFileError("Couldn't save what was pulled from the Workshop.", ex), ex.Message);
            }
            finally
            {
                pubVm.IsPulling = false;
            }
        }

        pubVm.PublishHandler = () => RunPublishFromDialogAsync(
            pubVm, project, steam, fileSystem, workshopFolder, type, folderName, fileId, remoteFetch);
        pubVm.CancelUploadHandler = () => _publishCts?.Cancel();

        // Mirror the main window's build state so publishing locks while a build (e.g.
        // "Build now" or "Prepare now") runs behind the dialog, and refresh the checks
        // once a build started from the banner finishes.
        void OnMainVmPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(MainWindowViewModel.IsBuildRunning) || ViewModel is null)
                return;

            pubVm.IsBuildRunning = ViewModel.IsBuildRunning;
            if (!ViewModel.IsBuildRunning && refreshChecksAfterBuild && dialog.IsVisible)
            {
                refreshChecksAfterBuild = false;
                _ = RefreshChecksAfterBuildAsync();
            }
        }

        async System.Threading.Tasks.Task RefreshChecksAfterBuildAsync()
        {
            await RefreshPublishPreflightChecksAsync(pubVm, project, fileSystem, steam, settings, workshopFolder);
            pubVm.ShowStatus(ViewModel?.LastBuildOutcome == BuildOutcome.Succeeded
                ? "Build finished. The checks are up to date."
                : "The build didn't finish. Check the build log.");
        }

        ViewModel.PropertyChanged += OnMainVmPropertyChanged;

        // Steam can answer before the dialog is on screen, so the result is applied whenever it
        // lands, unless the dialog has closed or a pull has already replaced the draft.
        dialog.Closed += (_, _) => dialogLifetime.Cancel();
        if (remoteFetch is not null)
        {
            var openedVersion = pubVm.SelectedProfile;
            _ = ApplyWorkshopSyncResultAsync(remoteFetch, pubVm, existingData,
                () => dialogLifetime.IsCancellationRequested || syncSuperseded
                      || !ReferenceEquals(pubVm.SelectedProfile, openedVersion));
        }

        dialog.FitToScreenOf(this);
        try
        {
            await dialog.ShowDialog(this);
        }
        finally
        {
            ViewModel.PropertyChanged -= OnMainVmPropertyChanged;
        }
    }

    private const string WorkshopSyncStatus = "Checking Steam for changes made outside Blackbird…";

    /// <summary>The draft-save line: a Workshop file Blackbird won't overwrite says why, anything else in plain words.</summary>
    private static string DescribeWorkshopFileError(string what, Exception ex) =>
        ex is WorkshopFileCorruptException or WorkshopZoneException ? $"{what} {ex.Message}" : ErrorText.Describe(what, ex);

    private enum PublishOutcome { Failed, Published, Stopped }

    /// <summary>
    /// The upload, run with the Publish dialog open: it shows progress, a failure lands
    /// inline with the draft intact, and Cancel (or closing the dialog) stops it. The main
    /// window gets one line when it's over.
    /// </summary>
    private async System.Threading.Tasks.Task RunPublishFromDialogAsync(
        PublishDialogViewModel pubVm,
        ProjectItem project,
        ISteamWorkshopService steam,
        IFileSystemService fileSystem,
        string workshopFolder,
        string type,
        string folderName,
        ulong openedFileId,
        System.Threading.Tasks.Task<WorkshopRemoteItemData?>? remoteFetch)
    {
        if (ViewModel is null || ViewModel.IsPublishing)
            return;

        // Busy, and stoppable, before the first await: a second click can't start another
        // run, and Cancel or closing the dialog while Steam connects stops this one.
        ViewModel.IsPublishing = true;
        var creatingItem = false;
        var outcome = PublishOutcome.Failed;
        var title = pubVm.Title.Trim();
        using var publishCts = new System.Threading.CancellationTokenSource();
        _publishCts = publishCts;
        var publishToken = publishCts.Token;
        pubVm.BeginUpload();
        try
        {
            // A retry if Steam wasn't up when the dialog opened.
            if (!await steam.InitializeAsync())
            {
                pubVm.EndUploadWithError(
                    "Steam isn't running, so nothing was uploaded. Start Steam and sign in, then try again.",
                    steam.InitializationDetail);
                return;
            }

            publishToken.ThrowIfCancellationRequested();

            var workshopData = pubVm.GetActiveWorkshopData();
            if (!ulong.TryParse(workshopData.PublisherId, out var fileId))
                fileId = 0;

            // The sync check already fetched this item, so the upload needn't ask Steam again
            // which of its previews are images. A new item has none.
            IReadOnlyList<uint>? imagePreviewIndices = fileId == 0 ? []
                : fileId == openedFileId && remoteFetch is { IsCompletedSuccessfully: true, Result: { } remoteItem }
                    ? [.. remoteItem.AdditionalPreviews.Where(p => p.IsImage).Select(p => p.Index)]
                    : null;

            var needsLegalAgreement = false;
            if (fileId == 0)
            {
                creatingItem = true;
                var created = await steam.CreateItemAsync($"{project.Key}|{pubVm.SelectedProfile?.Id}", publishToken);
                creatingItem = false;
                if (created.FileId == 0)
                {
                    pubVm.EndUploadWithError(
                        $"Steam didn't create the Workshop item, so nothing was uploaded. {WorkshopResultText.Describe(created.Result)}");
                    return;
                }

                fileId = created.FileId;
                needsLegalAgreement = created.NeedsLegalAgreement;

                // Saved below before the upload: if the upload fails, the next try updates
                // this item instead of creating another.
                pubVm.ApplyPublishedFileId(fileId.ToString());
            }

            workshopData = pubVm.GetActiveWorkshopData();
            EnsureWorkshopIdentity(workshopData, type, folderName);
            await SaveWorkshopFilesAsync(fileSystem, workshopFolder, pubVm.ProfilesData, workshopData, pubVm);
            var movedAtUpload = await System.Threading.Tasks.Task.Run(() => WorkshopFiles.PrepareZoneForUpload(workshopFolder), publishToken);
            if (movedAtUpload.Count > 0)
                pubVm.RemapMediaPaths(movedAtUpload, workshopFolder);

            // Media moved out of zone by the save or just now has new paths.
            workshopData = pubVm.GetActiveWorkshopData();
            title = workshopData.Title.Trim();
            var progress = new Progress<WorkshopUploadProgress>(pubVm.ReportUploadProgress);
            var update = await steam.UpdateItemAsync(
                fileId, workshopData.Title, workshopData.Description, workshopData.Thumbnail,
                workshopFolder, [.. workshopData.GetTagList()], workshopData.GetVisibility(), workshopData.Changelog,
                [.. workshopData.GetPreviewImageList()],
                imagePreviewIndices,
                progress,
                publishToken);

            if (update.Succeeded)
            {
                outcome = PublishOutcome.Published;
                pubVm.CompleteUpload(fileId.ToString(), workshopData.GetVisibility(),
                    needsLegalAgreement || update.NeedsLegalAgreement);
                ViewModel.RefreshSelectedProjectDerivedMetadata();

                // The changelog went up with this upload; the next one starts empty.
                pubVm.ClearPublishedChangelog();
                var cleared = pubVm.GetActiveWorkshopData();
                EnsureWorkshopIdentity(cleared, type, folderName);
                try
                {
                    await SaveWorkshopFilesAsync(fileSystem, workshopFolder, pubVm.ProfilesData, cleared, pubVm);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The upload is done; only the emptied changelog didn't reach the disk.
                    CrashLog.Write("Saving the Workshop draft after publishing failed", ex);
                }
            }
            else if (update.TimedOut)
            {
                pubVm.EndUploadWithError(
                    "Steam hasn't confirmed the upload after 30 minutes. It may still finish: check the Workshop page before publishing again.");
            }
            else if (update.RejectedField is { } field)
            {
                pubVm.EndUploadWithError($"Steam refused the {field}, so nothing was uploaded. Check it, then try again.");
            }
            else
            {
                pubVm.EndUploadWithError(
                    $"The upload to Workshop item {fileId} didn't finish. {WorkshopResultText.Describe(update.Result)}",
                    update.Result.ToString());
            }
        }
        catch (OperationCanceledException) when (creatingItem)
        {
            outcome = PublishOutcome.Stopped;
            pubVm.EndUploadWithError(
                "Stopped while Steam was creating the Workshop item. It may still have been created; publishing this version again uses it instead of creating another.");
        }
        catch (OperationCanceledException)
        {
            // Steam can't abort a submitted upload; cancelling only stops waiting on it.
            outcome = PublishOutcome.Stopped;
            pubVm.EndUploadWithError("Upload stopped. Steam may still finish it.");
        }
        catch (TimeoutException ex) when (creatingItem)
        {
            pubVm.EndUploadWithError(ex.Message);
        }
        catch (WorkshopPreviewQueryException ex)
        {
            pubVm.EndUploadWithError(ex.Message);
        }
        catch (Exception ex)
        {
            pubVm.EndUploadWithError(DescribeWorkshopFileError("Nothing was uploaded.", ex), ex.Message);
        }
        finally
        {
            ViewModel.IsPublishing = false;
            _publishCts = null;

            var name = title.Length > 0 ? title : project.DisplayName;
            ViewModel.ShowTransientStatus(outcome switch
            {
                PublishOutcome.Published => $"Published {name} to the Workshop.",
                PublishOutcome.Stopped => $"Stopped publishing {name}.",
                _ => $"Couldn't publish {name} to the Workshop.",
            });
        }
    }

    private async System.Threading.Tasks.Task RunSetupDoctorFromDialogAsync(
        PublishDialogViewModel pubVm,
        Window owner,
        ProjectItem project,
        IFileSystemService fileSystem,
        ISteamWorkshopService steam,
        ISettingsService settings,
        string workshopFolder)
    {
        await ShowSetupDoctorAsync(showOnlyIfUnhealthy: false, owner);
        if (owner.IsVisible)
            await RefreshPublishPreflightChecksAsync(pubVm, project, fileSystem, steam, settings, workshopFolder);
    }

    private const string WorkshopSyncSection = "workshop-sync";

    private static async System.Threading.Tasks.Task ApplyWorkshopSyncResultAsync(
        System.Threading.Tasks.Task<WorkshopRemoteItemData?> remoteFetch,
        PublishDialogViewModel pubVm,
        WorkshopItemData local,
        Func<bool> isStale)
    {
        WorkshopRemoteItemData? remote;
        try
        {
            remote = await remoteFetch;
        }
        catch (Exception)
        {
            remote = null;
        }

        pubVm.ClearStatus(WorkshopSyncStatus);

        // Edits made meanwhile don't matter: the comparison is against the draft as it was
        // opened, which is what Steam-side changes would be overwritten from.
        if (isStale())
            return;

        if (remote is not null)
            pubVm.SetKnownRemoteVisibility(remote.Metadata.PublisherId, remote.Metadata.GetVisibility());

        var row = remote is null ? null : BuildWorkshopSyncRow(local, remote);
        if (row is not null)
            row.RelatedSection = WorkshopSyncSection;
        pubVm.SetSectionCheck(WorkshopSyncSection, row);
    }

    private static PublishPreflightCheck? BuildWorkshopSyncRow(WorkshopItemData? local, WorkshopRemoteItemData remote)
    {
        var differences = new List<string>();
        if (!string.Equals(local?.Title ?? "", remote.Metadata.Title, StringComparison.Ordinal))
            differences.Add("title");
        if (!string.Equals(local?.Description ?? "", remote.Metadata.Description, StringComparison.Ordinal))
            differences.Add("description");
        if (!string.Equals(DescribeTags(local?.GetTagList()), DescribeTags(remote.Metadata.GetTagList()), StringComparison.Ordinal))
            differences.Add("tags");
        if ((local?.GetVisibility() ?? WorkshopItemVisibility.Private) != remote.Metadata.GetVisibility())
            differences.Add("visibility");

        if (!string.IsNullOrWhiteSpace(remote.ThumbnailUrl)
            && (string.IsNullOrWhiteSpace(local?.Thumbnail) || !File.Exists(local.Thumbnail)))
        {
            differences.Add("thumbnail");
        }

        var localPreviewImages = local?.GetPreviewImageList() ?? [];
        var localPreviewNames = localPreviewImages.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remoteImages = remote.AdditionalPreviews.Where(p => p.IsImage).ToList();
        if (remoteImages.Count != localPreviewImages.Count
            || remoteImages.Any(p => !string.IsNullOrWhiteSpace(p.OriginalFileName)
                                     && !localPreviewNames.Contains(Path.GetFileName(p.OriginalFileName))))
        {
            differences.Add("gallery");
        }

        if (differences.Count == 0)
            return null;

        return new PublishPreflightCheck
        {
            Status = "Info",
            Check = "Workshop sync",
            Detail = $"Steam has a different {JoinWithAnd(differences)}. Pull from Workshop to replace this draft with Steam's copy.",
            PrimaryAction = new PublishCheckAction { Label = "Pull from Workshop", Kind = "pull-workshop" },
            SecondaryAction = new PublishCheckAction { Label = "Dismiss", Kind = "dismiss-info", Payload = "Workshop sync" },
        };
    }

    /// <summary>"a", "a and b", "a, b, and c".</summary>
    private static string JoinWithAnd(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))}, and {items[^1]}",
    };

    private async System.Threading.Tasks.Task HandleCleanXpaksFromDialogAsync(PublishDialogViewModel pubVm)
    {
        if (ViewModel is null) return;
        try
        {
            if (await ViewModel.MoveSelectedProjectXPaksToRecycleBinAsync() is not { } moved)
            {
                pubVm.ShowStatus("Stopped cleaning. Nothing was deleted permanently.");
                return;
            }

            pubVm.RemoveCheckByName("XPak cleanup");
            pubVm.ShowStatus($"Moved {moved:N0} XPak{(moved == 1 ? "" : "s")} to the Recycle Bin");
        }
        catch (Exception ex)
        {
            pubVm.ShowError(ErrorText.Describe("Couldn't move the XPaks to the Recycle Bin.", ex), ex.Message);
        }
    }

    private async System.Threading.Tasks.Task RunReadyForPublishFromDialogAsync(
        PublishDialogViewModel pubVm,
        ProjectItem project,
        IFileSystemService fileSystem,
        ISteamWorkshopService steam,
        ISettingsService settings,
        string workshopFolder)
    {
        if (ViewModel is null)
            return;

        if (ViewModel.IsBuildRunning)
        {
            pubVm.ShowError("A build is already running.");
            return;
        }

        pubVm.SetPublishPrepCheck(new PublishPreflightCheck
        {
            Status = "Info",
            Check = "Prepare for publish",
            Detail = "Running now. Publishing unlocks when the build finishes.",
            RelatedSection = "publish-prep",
        });
        pubVm.IsBannerExpanded = true;

        try
        {
            var result = await ViewModel.PrepareSelectedProjectForPublishAsync(cleanXpaks: true);
            await RefreshPublishPreflightChecksAsync(pubVm, project, fileSystem, steam, settings, workshopFolder);

            var movedText = result.MovedXpaks == 0
                ? "No XPaks moved."
                : $"Moved {result.MovedXpaks:N0} XPak{(result.MovedXpaks == 1 ? "" : "s")} to the Recycle Bin.";
            switch (result.Status)
            {
                case "publish prep succeeded":
                    pubVm.ShowStatus($"Prepare for publish finished. {movedText}");
                    break;
                case "publish prep failed":
                    pubVm.ShowError("Prepare for publish failed. Check the build log.");
                    break;
                default:
                    pubVm.ShowStatus("Prepare for publish was cancelled");
                    break;
            }
        }
        catch (Exception ex)
        {
            pubVm.SetPublishPrepCheck(PublishPreflightBuilder.BuildPublishPrepCheck(ReadPublishSettings(settings, project.Key).LastPrep));
            pubVm.IsBannerExpanded = true;
            pubVm.ShowError(ErrorText.Describe("Prepare for publish couldn't start.", ex), ex.Message);
        }
    }

    /// <summary>The settings the publish checks need, read here on the UI thread that owns them.</summary>
    private static (DateTime? LastPrep, string ShipLanguage) ReadPublishSettings(ISettingsService settings, string projectKey)
    {
        var projectSettings = settings.Projects.GetValueOrDefault(projectKey);
        return (projectSettings?.LastPublishPrepTimestamp, PublishPreflightBuilder.GetShipBuildLanguage(projectSettings));
    }

    private async System.Threading.Tasks.Task RefreshPublishPreflightChecksAsync(
        PublishDialogViewModel pubVm,
        ProjectItem project,
        IFileSystemService fileSystem,
        ISteamWorkshopService steam,
        ISettingsService settings,
        string workshopFolder)
    {
        if (ViewModel is null)
            return;

        try
        {
            var xpaks = await ViewModel.GetSelectedProjectXPakFilesAsync();
            var (lastPrep, shipLanguage) = ReadPublishSettings(settings, project.Key);
            var checks = await System.Threading.Tasks.Task.Run(() =>
                PublishPreflightBuilder.Build(project, fileSystem, steam, workshopFolder, xpaks, lastPrep, shipLanguage));

            // Taken after the walk so a sync result that landed meanwhile is kept.
            var workshopSync = pubVm.Checks.FirstOrDefault(c => c.Check == "Workshop sync");
            if (workshopSync is not null)
                checks.Add(workshopSync);

            pubVm.LoadChecks(checks);
        }
        catch (Exception ex)
        {
            pubVm.ShowError(ErrorText.Describe("Couldn't check the project again.", ex), ex.Message);
        }
    }

    private async System.Threading.Tasks.Task HandlePullWorkshopAsync(
        PublishDialogViewModel pubVm,
        ISteamWorkshopService steam,
        IFileSystemService fileSystem,
        string workshopFolder,
        string type,
        string folderName,
        Func<bool> isDialogClosed,
        System.Threading.CancellationToken ct)
    {
        var activeData = pubVm.GetActiveWorkshopData();
        if (!ulong.TryParse(activeData.PublisherId, out var fileId) || fileId == 0)
            return;

        // What's pulled belongs to this Workshop version; if another is showing by the time
        // Steam answers (or the dialog has closed), nothing is applied or written.
        var target = pubVm.SelectedProfile;
        const string pulling = "Pulling from the Workshop…";
        bool Abandoned()
        {
            if (!isDialogClosed() && ReferenceEquals(pubVm.SelectedProfile, target))
                return false;

            pubVm.ClearStatus(pulling);
            return true;
        }

        pubVm.ShowStatus(pulling);
        var remote = await steam.GetCurrentItemAsync(fileId, ct);
        if (Abandoned())
            return;

        if (remote is null)
        {
            pubVm.ShowError("Couldn't read the Workshop item. Check that Steam is running, then try again.");
            return;
        }

        var media = await DownloadWorkshopMediaAsync(remote, WorkshopFiles.MediaFolder(workshopFolder),
            activeData.Thumbnail, activeData.GetPreviewImageList(), ct);
        if (Abandoned())
            return;

        pubVm.SetKnownRemoteVisibility(remote.Metadata.PublisherId, remote.Metadata.GetVisibility());
        pubVm.ApplyPulled(remote.Metadata, media.Thumbnail, media.Gallery);
        pubVm.RemoveCheckByName("Workshop sync");
        var pulled = pubVm.GetActiveWorkshopData();
        EnsureWorkshopIdentity(pulled, type, folderName);
        await SaveWorkshopFilesAsync(fileSystem, workshopFolder, pubVm.ProfilesData, pulled, pubVm);

        if (media.Errors.Count == 0)
        {
            pubVm.ShowStatus("Pulled from the Workshop");
            return;
        }

        var kept = (media.ThumbnailKept, media.GalleryKept) switch
        {
            (true, true) => "the thumbnail and gallery weren't changed",
            (true, false) => "the thumbnail wasn't changed",
            _ => "the gallery wasn't changed",
        };
        pubVm.ShowError(
            $"Pulled from the Workshop, but {media.Errors.Count:N0} image{(media.Errors.Count == 1 ? "" : "s")} couldn't be downloaded, so {kept}.",
            string.Join("\n", media.Errors));
    }

    // Workshop files are written off the UI thread, one after another in the order the
    // edits were made, from a copy taken here (the form keeps editing the live objects).
    private System.Threading.Tasks.Task _workshopWrites = System.Threading.Tasks.Task.CompletedTask;

    /// <summary>
    /// Writes the versions file and workshop.json. The first save also moves media an older
    /// build left in zone to the project folder; the dialog's paths follow it there.
    /// </summary>
    private async System.Threading.Tasks.Task SaveWorkshopFilesAsync(
        IFileSystemService fileSystem,
        string workshopFolder,
        WorkshopProfilesData profiles,
        WorkshopItemData active,
        PublishDialogViewModel pubVm)
    {
        var profilesSnapshot = profiles.Clone();
        var activeSnapshot = active.Clone();
        var write = _workshopWrites.ContinueWith(_ =>
        {
            // Nothing moves while a Workshop file is corrupt; the writes below would refuse anyway.
            if (WorkshopFiles.FindCorruptFile(workshopFolder) is { } corrupt)
                throw new WorkshopFileCorruptException(corrupt);

            var moved = WorkshopFiles.MigrateLegacyMedia(workshopFolder);
            foreach (var profile in profilesSnapshot.Profiles)
                WorkshopFiles.RemapMediaPaths(profile.WorkshopJson, workshopFolder, moved);
            WorkshopFiles.RemapMediaPaths(activeSnapshot, workshopFolder, moved);

            fileSystem.WriteWorkshopProfiles(workshopFolder, profilesSnapshot);
            fileSystem.WriteWorkshopJson(workshopFolder, activeSnapshot);
            return (Moved: moved, MediaFolder: WorkshopFiles.ExistingMediaFolder(workshopFolder));
        }, System.Threading.Tasks.TaskScheduler.Default);
        _workshopWrites = write;

        // Also catches a path into zone's old media folder typed after an earlier save moved it.
        var (moved, mediaFolder) = await write;
        pubVm.RemapMediaPaths(moved, workshopFolder);
        pubVm.WorkshopMediaFolder = mediaFolder ?? "";
    }

    private static void EnsureWorkshopIdentity(WorkshopItemData data, string type, string folderName)
    {
        data.Type = type;
        data.FolderName = folderName;
    }

    private static string DescribeTags(IEnumerable<string>? tags) =>
        tags is null ? "" : string.Join(", ", tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase));

    private readonly record struct PulledMedia(
        string Thumbnail, List<string> Gallery, List<string> Errors, bool ThumbnailKept, bool GalleryKept);

    /// <summary>
    /// Downloads the item's thumbnail and image previews into the media folder, never over an
    /// existing file (an identical one is reused). Steam having none clears the local one; a
    /// download that fails keeps the local thumbnail or the whole local gallery as it was.
    /// </summary>
    private static async System.Threading.Tasks.Task<PulledMedia> DownloadWorkshopMediaAsync(
        WorkshopRemoteItemData remote,
        string mediaFolder,
        string localThumbnail,
        List<string> localGallery,
        System.Threading.CancellationToken ct)
    {
        var errors = new List<string>();

        var thumbnail = "";
        var thumbnailKept = false;
        if (!string.IsNullOrWhiteSpace(remote.ThumbnailUrl))
        {
            var path = await DownloadWorkshopImageAsync(remote.ThumbnailUrl, "", mediaFolder, "thumbnail", errors, ct);
            thumbnailKept = path is null;
            thumbnail = path ?? localThumbnail;
        }

        var gallery = new List<string>();
        var imagePreviews = remote.AdditionalPreviews.Where(preview => preview.IsImage && !string.IsNullOrWhiteSpace(preview.Url)).ToList();
        for (var i = 0; i < imagePreviews.Count; i++)
        {
            var preview = imagePreviews[i];
            var path = await DownloadWorkshopImageAsync(preview.Url, preview.OriginalFileName, mediaFolder, $"preview_{i + 1:D2}", errors, ct);
            if (path is not null)
                gallery.Add(path);
        }

        var galleryKept = gallery.Count < imagePreviews.Count;
        return new PulledMedia(thumbnail, galleryKept ? localGallery : gallery, errors, thumbnailKept, galleryKept);
    }

    private static async System.Threading.Tasks.Task<string?> DownloadWorkshopImageAsync(
        string url,
        string originalFileName,
        string mediaFolder,
        string fallbackName,
        List<string> errors,
        System.Threading.CancellationToken ct)
    {
        try
        {
            using var response = await HttpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);

            var extension = ResolveImageExtension(url, originalFileName, response.Content.Headers.ContentType?.MediaType);
            var fileName = MakeSafeFileName(string.IsNullOrWhiteSpace(originalFileName)
                ? fallbackName + extension
                : Path.GetFileNameWithoutExtension(originalFileName) + extension);

            return await System.Threading.Tasks.Task.Run(() =>
            {
                Directory.CreateDirectory(mediaFolder);
                var existing = Path.Combine(mediaFolder, fileName);
                if (File.Exists(existing) && File.ReadAllBytes(existing).AsSpan().SequenceEqual(bytes))
                    return existing;

                var destination = WorkshopFiles.UniqueFilePath(mediaFolder, fileName);
                using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                stream.Write(bytes);
                return destination;
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"{(string.IsNullOrWhiteSpace(originalFileName) ? fallbackName : originalFileName)}: {ErrorText.Describe(ex)} ({ex.Message})");
            return null;
        }
    }

    private static string ResolveImageExtension(string url, string originalFileName, string? mediaType)
    {
        var originalExtension = Path.GetExtension(originalFileName);
        if (PublishPreflightBuilder.IsWorkshopImage(originalFileName))
            return originalExtension.ToLowerInvariant();

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && PublishPreflightBuilder.IsWorkshopImage(uri.AbsolutePath))
            return Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();

        return mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            _ => ".jpg",
        };
    }

    private static string MakeSafeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = fileName.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }


    private async void OnAddToolClick(object? sender, RoutedEventArgs e)
    {
        await AddToolFromPickerAsync();
    }

    private static readonly IReadOnlyList<FilePickerFileType> ToolFileTypes =
    [
        new("Programs") { Patterns = ["*.exe"] },
    ];

    /// <summary>
    /// "+" and the palette: straight to the file picker. The name comes from the file;
    /// the tool's right-click menu renames it or sets arguments.
    /// </summary>
    private async System.Threading.Tasks.Task AddToolFromPickerAsync()
    {
        if (ViewModel is null)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a tool",
            AllowMultiple = false,
            FileTypeFilter = ToolFileTypes,
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        ViewModel.AddToolShortcut(path);

    }

    private async void OnRenameCustomToolClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || (sender as MenuItem)?.Tag is not ToolShortcutViewModel tool)
            return;

        var name = await TextPromptDialog.ShowAsync(
            this,
            "Rename tool",
            "Name",
            "Rename",
            text => text.Length == 0 ? "Enter a name." : null,
            initialText: tool.Name,
            subtitle: tool.ExePath,
            maxLength: 60);

        if (name is not null)
            ViewModel.UpdateToolShortcut(tool, name, tool.Arguments);
    }

    private async void OnEditCustomToolArgumentsClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || (sender as MenuItem)?.Tag is not ToolShortcutViewModel tool)
            return;

        var arguments = await TextPromptDialog.ShowAsync(
            this,
            $"Arguments for {tool.Name}",
            "Arguments",
            "Save",
            _ => null,
            initialText: tool.Arguments,
            subtitle: "Passed to the program every time it starts. Leave empty for none.",
            maxLength: 1000);

        if (arguments is not null)
            ViewModel.UpdateToolShortcut(tool, tool.Name, arguments);
    }

    private async void OnExport2RustClick(object? sender, RoutedEventArgs e)
    {
        await ShowExport2RustAsync();
    }

    private Export2RustDialog? _export2RustDialog;

    // Modeless and single-instance: a second open brings the existing window forward.
    private System.Threading.Tasks.Task ShowExport2RustAsync()
    {
        if (_export2RustDialog is { } open)
        {
            ActivateToolWindow(open);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        var fileSystem = App.Services.GetRequiredService<IFileSystemService>();
        var dialog = new Export2RustDialog { DataContext = new Export2RustDialogViewModel(fileSystem) };
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_export2RustDialog, dialog))
                _export2RustDialog = null;
        };
        _export2RustDialog = dialog;
        dialog.Show(this);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private static void ActivateToolWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

#pragma warning disable CS0618 // DragDrop API deprecation
    private void OnToolbarDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles()?.ToList();
            if (files is not null && files.Any(f =>
                f.Path.LocalPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                e.DragEffects = DragDropEffects.Copy;
                SetToolbarDropOverlay(true);
                e.Handled = true;
                return;
            }
        }

        e.DragEffects = DragDropEffects.None;
        SetToolbarDropOverlay(false);
        e.Handled = true;
    }

    private void OnToolbarDragLeave(object? sender, RoutedEventArgs e)
    {
        SetToolbarDropOverlay(false);
    }

    private void OnToolbarDrop(object? sender, DragEventArgs e)
    {
        SetToolbarDropOverlay(false);

        if (e.Data.Contains(DataFormats.Files) && ViewModel is not null)
        {
            var files = e.Data.GetFiles()?.ToList();
            if (files is null) return;

            foreach (var file in files)
            {
                var path = file.Path.LocalPath;
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    ViewModel.AddToolShortcut(path);
            }
        }

        e.Handled = true;
    }

    private void SetToolbarDropOverlay(bool visible)
    {
        if (this.FindControl<Border>("ToolbarDropOverlay") is { } overlay)
            overlay.IsVisible = visible;
    }
#pragma warning restore CS0618

    private bool _isReportingUnexpectedError;

    /// <summary>Tells the user an action failed after App logged the exception to crash.log.</summary>
    public async void ReportUnexpectedError()
    {
        if (_isReportingUnexpectedError || !IsVisible)
            return;

        _isReportingUnexpectedError = true;
        try
        {
            await ShowMessageAsync("That didn't work", $"The action stopped unexpectedly. Details are in {AppPaths.CrashLog}.");
        }
        finally
        {
            _isReportingUnexpectedError = false;
        }
    }

    private System.Threading.Tasks.Task ShowMessageAsync(string title, string message) =>
        MessageDialog.ShowAsync(this, title, message);

    /// <summary>Asks before acting, through the shared <see cref="MessageDialog"/>.</summary>
    private async System.Threading.Tasks.Task<ConfirmChoice> ShowConfirmAsync(
        string title,
        string message,
        string confirmText,
        string? folderPath = null,
        bool destructive = false)
    {
        var confirmed = await MessageDialog.ConfirmAsync(this, title, message, confirmText, destructive, folderPath);
        return confirmed ? ConfirmChoice.Confirm : ConfirmChoice.Cancel;
    }
}
