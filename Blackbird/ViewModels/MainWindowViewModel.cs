using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackbird.Models;
using Blackbird.Services;
using Gscode.Updates;

namespace Blackbird.ViewModels;

public enum BuildOutcome { None, Succeeded, CompletedWithErrors, Failed, Cancelled }

public partial class MainWindowViewModel : ObservableObject
{
    private const string NoQuickLaunchMapLabel = "No map";
    private const string GsCodeLibraryUrl = "https://gscode.net/library";
    private const string Bo3SourceExplorerUrl = "https://bo3explorer.zeroy.com/";
    private const string BlackOps3SteamAppId = "311210";

    private readonly IFileSystemService _fileSystem;
    private readonly IBuildService _buildService;
    private readonly ISettingsService _settings;

    private CancellationTokenSource? _buildCts;

    /// <summary>The window is closing: stop a running build so its child processes go with it.</summary>
    public void CancelBuildOnClose()
    {
        try { _buildCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    private Task? _buildCancelTask;
    private FileSystemWatcher? _selectedProjectWatcher;
    private bool _isLoadingProject;
    private bool _isInitialized;
    private int _projectRefreshVersion;
    private List<ProjectItem> _allProjects = [];

    private string _diagnosticCarry = "";
    private int _logLoadVersion;
    private Task _logFileQueue = Task.CompletedTask;
    private readonly DispatcherTimer _settingsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _projectSearchDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly ConcurrentDictionary<string, (long Stamp, long Size)> _projectSizeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private int _capabilityVersion;
    private ProjectCapabilities _capabilities = ProjectCapabilities.None;
    private int _toolAvailabilityVersion;
    private string? _toolIconsPath;
    private string? _steamExecutablePath;
    private readonly DispatcherTimer _buildActivityTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _transientStatusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private string? _transientStatusMessage;
    private string _statusTextBeforeTransient = "Ready";
    private readonly Stopwatch _buildActivityStopwatch = new();

    private sealed record ToolAvailability(
        bool AssetEditorAvailable,
        bool RadiantAvailable,
        bool ApexAvailable,
        bool Export2RustAvailable,
        bool GameExecutableAvailable,
        string? SteamExecutablePath,
        bool VsCodeAvailable,
        string ToolsPath,
        string GameLanguage,
        Avalonia.Media.Imaging.Bitmap? AssetEditorIcon,
        Avalonia.Media.Imaging.Bitmap? RadiantIcon,
        Avalonia.Media.Imaging.Bitmap? ApexIcon);

    /// <summary>Folder and Workshop facts about the selected project, read off the UI thread once per selection.</summary>
    private sealed record ProjectCapabilities(string ProjectKey)
    {
        public static readonly ProjectCapabilities None = new("");
        public bool HasScriptsFolder { get; init; }
        public bool HasUiFolder { get; init; }
        public bool HasZoneSourceFolder { get; init; }
        public bool HasZoneFile { get; init; }
        public bool HasMapSourceFile { get; init; }
        public bool HasMapSourceFolder { get; init; }
        public bool HasSzcFile { get; init; }
        public bool HasContentFolder { get; init; }
        public bool HasWorkshopJson { get; init; }
        public ulong WorkshopFileId { get; init; }
    }

    private sealed record ProjectRefreshResult(
        List<ProjectItem> Projects,
        bool IsSetupHealthy,
        ToolAvailability Tools);

    public const int BuiltInProjectFilterCount = 8;

    private static readonly string[] BuiltInProjectFilters =
    [
        "All", "Recent", "Favorites", "ZM maps", "MP maps", "Mods", "Published", "Unpublished"
    ];
    private static readonly string[] DefaultProjectCategories = ["Maps", "Mods"];

    // Languages matching the original
    public static readonly string[] Languages =
    [
        "english", "french", "italian", "spanish", "german", "portuguese",
        "russian", "polish", "japanese", "traditionalchinese", "simplifiedchinese", "englisharabic"
    ];

    public static readonly string[] BuildLanguageValues = ["All", .. Languages];
    public static readonly string[] BuildLanguageLabels =
    [
        "All languages",
        "English",
        "French",
        "Italian",
        "Spanish",
        "German",
        "Portuguese",
        "Russian",
        "Polish",
        "Japanese",
        "Traditional Chinese",
        "Simplified Chinese",
        "English Arabic"
    ];

    /// <summary>
    /// Sentinel stored per-environment meaning "the default": Ship links all languages,
    /// Dev links only the language Steam runs the game in.
    /// </summary>
    public const string DefaultBuildLanguageSetting = LaunchConfig.DefaultBuildLanguage;

    public static readonly string[] BuildLanguageSettingValues =
        [DefaultBuildLanguageSetting, .. BuildLanguageValues];

    // Read from the game's Steam appmanifest during the tool scan; English until then.
    private string _gameLanguage = "english";

    /// <summary>The Default entry in the language menu, e.g. "Default (Dev: English, Ship: all)".</summary>
    public string DefaultBuildLanguageOptionLabel =>
        $"Default (Dev: {GetBuildLanguageLabel(GetBuildLanguageIndex(_gameLanguage))}, Ship: all)";

    public static readonly string[] WorkshopTags =
    [
        "Animation", "Audio", "Character", "Map", "Mod", "Mode", "Model",
        "Multiplayer", "Scorestreak", "Skin", "Specialist", "Texture",
        "UI", "Vehicle", "Visual Effect", "Weapon", "WIP", "Zombies"
    ];

    // Project list for the dropdown
    public BulkObservableCollection<ProjectItem> Projects { get; } = [];
    public IReadOnlyList<ProjectItem> ProjectCatalog => _allProjects.Count > 0
        ? _allProjects
        : [.. Projects.Where(p => !p.IsGroupHeader)];
    public BulkObservableCollection<string> QuickLaunchMaps { get; } = [];

    /// <summary>The filter menu's entries: the built-in scopes, then the user's own categories. Read when the menu opens.</summary>
    public IReadOnlyList<string> ProjectFilters { get; private set; } = BuiltInProjectFilters;

    // Custom tool shortcuts
    public ObservableCollection<ToolShortcutViewModel> CustomTools { get; } = [];
    public BulkObservableCollection<BuildPreset> BuildPresets { get; } = [];

    // Built-in tool icons
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _assetEditorIcon;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _radiantIcon;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _apexIcon;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenAssetEditorCommand))]
    private bool _isAssetEditorAvailable;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenRadiantCommand))]
    private bool _isRadiantAvailable;
    /// <summary>Apex isn't part of the Mod Tools; its button shows only once it's in the tools' bin folder.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenApexCommand))]
    private bool _isApexAvailable;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Export2RustToolTip))]
    private bool _isExport2RustAvailable;
    public string? Export2RustToolTip => IsExport2RustAvailable ? null : "export2rust.exe isn't in the Mod Tools' bin folder.";

    [ObservableProperty] private bool _isGameExecutableAvailable;
    [ObservableProperty] private bool _isSteamAvailable;
    [ObservableProperty] private bool _isVsCodeAvailable;
    [ObservableProperty] private bool _isProjectScanRunning = true;
    [ObservableProperty] private bool _isBuildCancelling;
    [ObservableProperty] private string _buildActivityText = "";
    [ObservableProperty] private string _projectSearchText = "";
    [ObservableProperty] private int _selectedProjectFilterIndex;
    [ObservableProperty] private string _selectedQuickLaunchMap = NoQuickLaunchMapLabel;

    [ObservableProperty] private ProjectItem? _selectedProject;

    /// <summary>
    /// The picker ListBox's selection. Kept separate from SelectedProject so
    /// rebuilding the list (search, filter, favorites, group collapse) never
    /// clears or re-loads the active project.
    /// </summary>
    [ObservableProperty] private ProjectItem? _pickerSelection;
    private bool _syncingPickerSelection;

    /// <summary>
    /// True while the picker is being re-pointed at the active project
    /// programmatically; the view uses it to tell real user picks apart.
    /// </summary>
    public bool IsSyncingPickerSelection => _syncingPickerSelection;

    // Computed properties for UI visibility
    public bool IsMapSelected => SelectedProject is { IsGroupHeader: false, Type: ProjectType.Map };
    public bool IsModSelected => SelectedProject is { IsGroupHeader: false, Type: ProjectType.Mod };
    public bool HasSelectedProject => SelectedProject is { IsGroupHeader: false };
    public bool HasAnyProjects => _allProjects.Count > 0 || Projects.Any(p => !p.IsGroupHeader);
    /// <summary>The picker has rows to show. Headers only appear for groups with matches.</summary>
    public bool HasVisibleProjects => Projects.Count > 0;
    public string PickerEmptyText =>
        ProjectSearchText.Trim() is { Length: > 0 } query ? $"No projects match \u201c{query}\u201d."
        : HasActiveProjectFilter ? $"No projects in {ActiveProjectFilterLabel}."
        : "No projects yet.";
    public bool HasActiveProjectFilter => SelectedProjectFilterIndex > 0;
    public string ActiveProjectFilterLabel => HasActiveProjectFilter ? GetSelectedProjectFilterLabel() : "";
    public string FavoriteActionLabel => SelectedProject is { IsFavorite: true }
        ? "Remove from favorites"
        : "Add to favorites";
    public bool HasScriptsFolder => CurrentCapabilities.HasScriptsFolder;
    public bool HasUiFolder => CurrentCapabilities.HasUiFolder;
    public bool CanOpenScriptsInVsCode => HasScriptsFolder && IsVsCodeAvailable;
    public bool CanOpenLuiInVsCode => HasUiFolder && IsVsCodeAvailable;
    public bool HasZoneSourceFolder => CurrentCapabilities.HasZoneSourceFolder;
    public bool HasZoneFile => CurrentCapabilities.HasZoneFile;
    public bool HasMapSourceFile => CurrentCapabilities.HasMapSourceFile;
    public bool HasMapSourceFolder => CurrentCapabilities.HasMapSourceFolder;
    public bool HasSzcFile => CurrentCapabilities.HasSzcFile;
    public bool HasContentFolder => CurrentCapabilities.HasContentFolder;
    public bool HasWorkshopJson => CurrentCapabilities.HasWorkshopJson;
    public string? ScriptsInVsCodeBlockedReason =>
        !IsVsCodeAvailable ? VsCodeMissingReason : HasScriptsFolder ? null : "This project has no scripts folder.";
    public string? LuiInVsCodeBlockedReason =>
        !IsVsCodeAvailable ? VsCodeMissingReason : HasUiFolder ? null : "This project has no ui folder.";
    private const string VsCodeMissingReason = "VS Code's code command isn't on PATH.";
    public bool CanLaunchSelectedProject => LaunchOnline ? IsSteamAvailable : IsGameExecutableAvailable;
    // Launching is blocked mid-build (the linker may still be writing files). Closing
    // the game lives on the status bar's "Game running" pill, never on Run.
    public bool CanRun => !IsGameRunning && !IsBuildRunning && HasSelectedProject && CanLaunchSelectedProject;
    public string RunToolTip =>
        !HasSelectedProject ? "Choose a project first."
        : IsGameRunning ? "Black Ops III is already running. Close it from the status bar."
        : IsBuildRunning ? "Wait for the build to finish."
        : !CanLaunchSelectedProject ? (LaunchOnline
            ? "Steam.exe wasn't found. Switch Launch config to Offline, or install Steam."
            : "BlackOps3.exe wasn't found. Open Setup Doctor from the title bar.")
        : BuildModeIndex == 1 ? "Run the game (F5)" : "Run the game without building";
    public bool CanEditBuildOptions => !IsBuildRunning && !IsBuildCancelling;
    public bool HasQuickLaunchMapOptions => IsModSelected && QuickLaunchMaps.Count > 1;
    public bool CanOpenWorkshopPage => CurrentCapabilities.WorkshopFileId != 0;
    public bool CanCopyRunCommand => HasSelectedProject && CanLaunchSelectedProject;
    /// <summary>The Launch config button's face: the launch config and how the game starts, "Dev · Offline".</summary>
    public string LaunchConfigSummary =>
        $"{ValueAtOrFirst(LaunchConfigs, LaunchConfigIndex)} · {(LaunchOnline ? "Online" : "Offline")}";

    /// <summary>Everything the Launch config menu sets, including what the face leaves out.</summary>
    public string LaunchConfigToolTip
    {
        get
        {
            var lines = new List<string>
            {
                "Launch config",
                LaunchOnline
                    ? "Online: launches through Steam so online services start."
                    : "Offline: launches BlackOps3.exe directly for fast local iteration.",
                BuildLanguageToolTip,
            };
            var quickMap = GetSelectedQuickLaunchMapName();
            if (IsModSelected && quickMap.Length > 0)
                lines.Add($"Quick launch map: {quickMap}");
            return string.Join("\n", lines);
        }
    }
    [ObservableProperty] private bool _isSetupHealthy = true;
    public bool HasSetupIssues => !IsSetupHealthy;
    /// <summary>
    /// Drives the one setup warning in the title bar: a tool missing from an install that otherwise works.
    /// A broken game or Mod Tools folder is the empty state's to explain, so the warning stays out of it.
    /// Hidden until the tools have been checked once, and steady through later rescans.
    /// </summary>
    public bool HasToolHealthIssues => _toolsChecked && !HasSetupIssues
        && (!IsAssetEditorAvailable || !IsRadiantAvailable || !IsGameExecutableAvailable);
    private bool _toolsChecked;
    public string ToolHealthMessage
    {
        get
        {
            var issues = new List<string>();
            if (!IsAssetEditorAvailable) issues.Add("Asset Editor is missing");
            if (!IsRadiantAvailable) issues.Add("Radiant is missing");
            if (!IsGameExecutableAvailable) issues.Add("BlackOps3.exe is missing");
            return issues.Count == 0
                ? "Setup looks healthy."
                : "Open Setup Doctor: " + string.Join(", ", issues) + ".";
        }
    }
    public string AssetEditorToolTip => IsAssetEditorAvailable
        ? "Asset Editor (Ctrl+Shift+A)"
        : "Asset Editor wasn't found. Open Setup Doctor from the title bar.";
    public string RadiantToolTip => IsRadiantAvailable
        ? "Radiant (Ctrl+Shift+R)"
        : "Radiant wasn't found. Open Setup Doctor from the title bar.";
    // Zone summary for mod pipeline row (e.g. "2 of 4")
    public string ZoneSummary
    {
        get
        {
            if (SelectedProject is not { Type: ProjectType.Mod }) return "";
            var total = SelectedProject.ZoneFiles.Count;
            var checkedCount = SelectedProject.ZoneFiles.Count(z => z.IsChecked);
            return $"{checkedCount} of {total}";
        }
    }

    public bool CanStartBuild => (IsBuildRunning && !IsBuildCancelling)
        || (!IsProjectScanRunning && !IsPublishing && !IsPreparingForPublish && HasValidBuildRequest());
    public bool CanStartBuildOnly => !IsProjectScanRunning
        && !IsBuildRunning
        && !IsPreparingForPublish
        && !IsPublishing
        && HasSelectedProject
        && !HasSetupIssues
        && HasBuildStepsSelected();
    public bool CanStartBuildAndRun => CanStartBuildOnly && CanLaunchSelectedProject;

    /// <summary>Rename, duplicate, delete and clean touch files a build or upload may be reading.</summary>
    public bool CanModifyProject => HasSelectedProject && !IsBuildRunning && !IsBuildCancelling && !IsPublishing && !IsPreparingForPublish;
    public string? ModifyProjectBlockedReason =>
        IsPublishing ? "Wait for the Workshop upload to finish."
        : IsBuildRunning || IsBuildCancelling || IsPreparingForPublish ? "Wait for the build to finish."
        : null;

    /// <summary>A running build's log and result belong to the project it started on.</summary>
    public bool CanSwitchProject => !IsBuildRunning && !IsBuildCancelling;
    public string SwitchProjectBlockedReason => "Finish or cancel the build to switch projects.";

    /// <summary>Shown inline in the build steps row: only problems the row itself can fix.</summary>
    public bool HasBuildValidationMessage => !string.IsNullOrEmpty(BuildValidationMessage);

    /// <summary>
    /// What's wrong with the chosen steps or launch target. Setup, scanning and uploads
    /// are reported by <see cref="BuildBlockedReason"/>, not here.
    /// </summary>
    public string BuildValidationMessage
    {
        get
        {
            if (!HasSelectedProject || HasSetupIssues || HasValidBuildRequest())
                return "";

            if (BuildModeIndex != 1 && !CanLaunchSelectedProject)
                return LaunchOnline ? "Steam.exe wasn't found." : "BlackOps3.exe wasn't found.";

            return StepsValidationMessage();
        }
    }

    private string StepsValidationMessage()
    {
        if (HasBuildStepsSelected())
            return "";

        if (IsMapSelected)
            return "Select Compile, Light or Link to build.";

        if (IsModSelected)
            return IsLinkChecked
                ? "Select at least one zone to build."
                : "Select Link and at least one zone to build.";

        return "";
    }

    /// <summary>Why Build can't start right now, or empty when it can.</summary>
    public string BuildBlockedReason
    {
        get
        {
            if (!HasSelectedProject)
                return "Choose a project first.";
            if (IsBuildRunning)
                return "";
            if (IsPreparingForPublish)
                return "Preparing for publish\u2026";
            if (IsProjectScanRunning)
                return "Scanning projects\u2026";
            if (IsPublishing)
                return "Wait for the Workshop upload to finish.";
            if (HasSetupIssues)
                return "Setup needs attention. Open Setup Doctor from the Tools menu.";
            return BuildValidationMessage;
        }
    }

    public string BuildToolTip =>
        IsBuildRunning ? (IsBuildCancelling ? "Cancelling the build" : "Cancel the build")
        : BuildBlockedReason is { Length: > 0 } reason ? reason
        : BuildModeIndex switch
        {
            1 => "Build (Ctrl+B)",
            2 => "Build, then run even if the build reports errors (F5)",
            _ => "Build, then run the game (F5). Ctrl+B builds without running.",
        };

    public string LinkStepToolTip => IsModSelected ? "Link the selected zones" : "Link compiled assets";

    public string EmptyStateTitle
    {
        get
        {
            if (HasSetupIssues)
                return "Blackbird needs your Black Ops III folder";

            if (IsProjectScanRunning)
                return "Finding projects";

            return HasAnyProjects
                ? "Choose a project"
                : "No maps or mods yet";
        }
    }

    public string EmptyStateMessage
    {
        get
        {
            if (HasSetupIssues)
                return "Point it at your Black Ops III install to find your maps and mods.";

            if (IsProjectScanRunning)
                return "Scanning maps and mods in your Black Ops III folder.";

            return HasAnyProjects
                ? "Pick a map or mod to build, run and publish it."
                : "Create a map from a template, or a mod with the zones you need.";
        }
    }

    public bool ShowChooseProjectAction => !HasSetupIssues && !IsProjectScanRunning && HasAnyProjects;
    public bool ShowNewProjectAction => !HasSetupIssues && !IsProjectScanRunning && !HasAnyProjects;

    // Build options
    [ObservableProperty] private bool _isCompileChecked;
    [ObservableProperty] private int _compileModeIndex = 1; // 0=Ents, 1=Full
    [ObservableProperty] private bool _isLightChecked;
    [ObservableProperty] private int _lightQualityIndex = 1; // 0=Low, 1=Medium, 2=High
    [ObservableProperty] private bool _isLinkChecked;
    [ObservableProperty] private string _runOptions = "";
    [ObservableProperty] private bool _launchOnline;
    [ObservableProperty] private int _selectedBuildLanguageIndex;

    // Build mode: controls what the main button does
    // 0 = Build & Run, 1 = Build, 2 = Build & Run (Ignore Errors)
    [ObservableProperty] private int _buildModeIndex = 1;

    public string BuildModeLabel => BuildModeIndex switch
    {
        1 => "Build",
        2 => "Build & run (ignore errors)",
        _ => "Build & run"
    };
    public bool IsBuildOnlyMode => BuildModeIndex == 1;
    public bool IsBuildAndRunMode => BuildModeIndex == 0;
    public bool IsBuildAndRunIgnoreErrorsMode => BuildModeIndex == 2;

    public string BuildModeIcon => BuildModeIndex switch
    {
        1 => "\uEA3C",
        2 => "\uE7BA",
        _ => "\uE768"
    };

    partial void OnBuildModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(BuildModeLabel));
        OnPropertyChanged(nameof(BuildModeIcon));
        OnPropertyChanged(nameof(IsBuildOnlyMode));
        OnPropertyChanged(nameof(IsBuildAndRunMode));
        OnPropertyChanged(nameof(IsBuildAndRunIgnoreErrorsMode));
        OnPropertyChanged(nameof(RunToolTip));
        if (!IsBuildRunning)
            BuildButtonText = BuildModeLabel;
        NotifyBuildStateChanged();
        SaveProjectSettings();
    }

    // Build state
    public BuildLog Output { get; } = new();
    [ObservableProperty] private bool _hasOutput;
    [ObservableProperty] private bool _isBuildRunning;
    [ObservableProperty] private BuildOutcome _lastBuildOutcome;

    /// <summary>A Workshop create or upload is in flight.</summary>
    [ObservableProperty] private bool _isPublishing;
    [ObservableProperty] private string _buildButtonText = "Build"; // updated by BuildModeIndex
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _buildTimestamp = "";
    [ObservableProperty] private string _buildDurationText = "";

    /// <summary>
    /// The status bar's one line: the running build's elapsed time while it runs, unless a
    /// short-lived message (a copy, an export) is showing; otherwise the status text.
    /// </summary>
    public string StatusLine => IsBuildRunning && _transientStatusMessage is null && BuildActivityText.Length > 0
        ? BuildActivityText
        : StatusText;

    /// <summary>"Last build 3 May 2026 at 14:02 · 0:42", or empty when there's no result.</summary>
    public string LastBuildSummary =>
        BuildTimestamp.Length == 0 ? ""
        : BuildDurationText.Length == 0 ? $"Last build {BuildTimestamp}"
        : $"Last build {BuildTimestamp} · {BuildDurationText}";

    /// <summary>The status line the last build outcome wrote; the outcome glyph shows only while that line does.</summary>
    private string? _outcomeStatusText;

    /// <summary>
    /// The status bar's outcome glyph: "error", "warning", "success", or "" when the line isn't a
    /// build outcome (a copy, a launch message, Ready). A running build shows the progress line instead.
    /// </summary>
    public string StatusKind
    {
        get
        {
            if (IsBuildRunning)
                return "";

            if (StatusText != _outcomeStatusText)
                return "";
            if (LastBuildOutcome is BuildOutcome.Failed or BuildOutcome.CompletedWithErrors || ErrorCount > 0)
                return LastBuildOutcome == BuildOutcome.Cancelled ? "" : "error";
            if (LastBuildOutcome == BuildOutcome.Succeeded)
                return WarningCount > 0 ? "warning" : "success";
            return "";
        }
    }

    partial void OnLastBuildOutcomeChanged(BuildOutcome value) => OnPropertyChanged(nameof(StatusKind));

    partial void OnBuildTimestampChanged(string value) => OnPropertyChanged(nameof(LastBuildSummary));
    partial void OnBuildDurationTextChanged(string value) => OnPropertyChanged(nameof(LastBuildSummary));
    partial void OnBuildActivityTextChanged(string value) => OnPropertyChanged(nameof(StatusLine));

    // Game running state
    private Process? _gameProcess;
    private int _steamLaunchId;

    // The Steam launch the user closed before its game appeared: when it does appear, it's closed too.
    private int _steamLaunchClosedEarly;
    private Task? _gameStopTask;
    [ObservableProperty] private bool _isGameRunning;
    [ObservableProperty] private bool _isGameStopping;
    [ObservableProperty] private int _gameProcessId;
    public bool CanStopGame => IsGameRunning && !IsGameStopping;
    public string CloseGameText => IsGameStopping ? "Closing\u2026" : "Close";

    partial void OnIsGameRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunToolTip));
        OnPropertyChanged(nameof(CanStopGame));
    }

    partial void OnIsGameStoppingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStopGame));
        OnPropertyChanged(nameof(CloseGameText));
    }

    partial void OnIsGameExecutableAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunchSelectedProject));
        OnPropertyChanged(nameof(CanCopyRunCommand));
        NotifyToolHealthChanged();
        NotifyBuildStateChanged();
    }

    partial void OnIsSteamAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunchSelectedProject));
        OnPropertyChanged(nameof(CanCopyRunCommand));
        NotifyBuildStateChanged();
    }

    partial void OnIsAssetEditorAvailableChanged(bool value) => NotifyToolHealthChanged();
    partial void OnIsRadiantAvailableChanged(bool value) => NotifyToolHealthChanged();

    partial void OnIsVsCodeAvailableChanged(bool value)
    {
        NotifyProjectCapabilityChanged();
    }


    partial void OnIsBuildRunningChanged(bool value)
    {
        NotifyBuildStateChanged();
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(StatusKind));
    }

    partial void OnIsBuildCancellingChanged(bool value) => NotifyBuildStateChanged();
    partial void OnIsPublishingChanged(bool value) => NotifyBuildStateChanged();

    /// <summary>
    /// Prepare for publish is under way, from its first await (recycling XPaks) until its link
    /// finishes. Builds stay locked the whole time, not just once the link has started.
    /// </summary>
    [ObservableProperty] private bool _isPreparingForPublish;

    partial void OnIsPreparingForPublishChanged(bool value) => NotifyBuildStateChanged();

    /// <summary>The log holds errors.</summary>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>The log holds warnings.</summary>
    public bool HasWarnings => WarningCount > 0;

    partial void OnErrorCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(StatusKind));
    }

    partial void OnWarningCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(StatusKind));
    }

    partial void OnStatusTextChanged(string value)
    {
        // A non-transient status update (build state, launch progress, ...)
        // takes over: abandon any pending transient revert so it can't
        // clobber the new status later.
        if (_transientStatusMessage is not null && value != _transientStatusMessage)
        {
            _transientStatusMessage = null;
            _transientStatusTimer.Stop();
        }
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(StatusKind));
    }

    partial void OnIsProjectScanRunningChanged(bool value)
    {
        NotifyEmptyStateChanged();
        NotifyBuildStateChanged();
    }

    private void NotifyEmptyStateChanged()
    {
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
        OnPropertyChanged(nameof(ShowChooseProjectAction));
        OnPropertyChanged(nameof(ShowNewProjectAction));
    }

    partial void OnProjectSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(PickerEmptyText));
        _projectSearchDebounce.Stop();
        if (string.IsNullOrWhiteSpace(value))
            RebuildProjectList(preserveSelection: true, projectsChanged: false);
        else
            _projectSearchDebounce.Start();
    }

    partial void OnSelectedProjectFilterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(HasActiveProjectFilter));
        OnPropertyChanged(nameof(ActiveProjectFilterLabel));
        OnPropertyChanged(nameof(PickerEmptyText));
        _projectSearchDebounce.Stop();
        RebuildProjectList(preserveSelection: true, projectsChanged: false);
    }

    partial void OnIsCompileCheckedChanged(bool value)
    {
        NotifyBuildStateChanged();
        SaveProjectSettings();
    }

    partial void OnCompileModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CompileModeLabel));
        OnPropertyChanged(nameof(IsCompileEntsMode));
        OnPropertyChanged(nameof(IsCompileFullMode));
        NotifyBuildStateChanged();
        SaveProjectSettings();
    }

    partial void OnIsLightCheckedChanged(bool value)
    {
        NotifyBuildStateChanged();
        SaveProjectSettings();
    }

    partial void OnLightQualityIndexChanged(int value)
    {
        OnPropertyChanged(nameof(LightQualityLabel));
        OnPropertyChanged(nameof(IsLightLowQuality));
        OnPropertyChanged(nameof(IsLightMediumQuality));
        OnPropertyChanged(nameof(IsLightHighQuality));
        NotifyBuildStateChanged();
        SaveProjectSettings();
    }

    partial void OnIsLinkCheckedChanged(bool value)
    {
        NotifyBuildStateChanged();
        SaveProjectSettings();
    }

    partial void OnRunOptionsChanged(string value) => SaveProjectSettings();

    partial void OnLaunchOnlineChanged(bool value)
    {
        OnPropertyChanged(nameof(LaunchConfigSummary));
        OnPropertyChanged(nameof(LaunchConfigToolTip));
        OnPropertyChanged(nameof(CanLaunchSelectedProject));
        OnPropertyChanged(nameof(CanCopyRunCommand));
        NotifyBuildStateChanged();
        SaveProjectSettings();
    }

    partial void OnSelectedBuildLanguageIndexChanged(int value)
    {
        if (value < 0 || value >= BuildLanguageSettingValues.Length)
        {
            SelectedBuildLanguageIndex = 0;
            return;
        }

        NotifyBuildLanguageLabelsChanged();
        NotifyBuildStateChanged();
        SaveProjectSettings();
    }

    private void NotifyBuildLanguageLabelsChanged()
    {
        OnPropertyChanged(nameof(BuildLanguageLabel));
        OnPropertyChanged(nameof(BuildLanguageToolTip));
        OnPropertyChanged(nameof(LaunchConfigSummary));
        OnPropertyChanged(nameof(LaunchConfigToolTip));
    }

    partial void OnSelectedQuickLaunchMapChanged(string value)
    {
        OnPropertyChanged(nameof(LaunchConfigToolTip));
        NotifyBuildStateChanged();
        SaveProjectSettings();
    }

    partial void OnIsSetupHealthyChanged(bool value)
    {
        OnPropertyChanged(nameof(HasSetupIssues));
        NotifyToolHealthChanged();
        NotifyEmptyStateChanged();
        NotifyBuildStateChanged();
    }

    // Dvars for runtime
    private List<string> _runDvars = [];

    // These commands are set by the MainWindow code-behind since they require dialog interaction
    private IAsyncRelayCommand? _newItemCommand;
    public IAsyncRelayCommand? NewItemCommand
    {
        get => _newItemCommand;
        set => SetProperty(ref _newItemCommand, value);
    }

    private IAsyncRelayCommand? _publishCommand;
    public IAsyncRelayCommand? PublishCommand
    {
        get => _publishCommand;
        set => SetProperty(ref _publishCommand, value);
    }

    public string[] CompileModes { get; } = ["Ents", "Full"];
    public string[] LightQualities { get; } = ["Low", "Medium", "High"];
    public string CompileModeLabel => ValueAtOrFirst(CompileModes, CompileModeIndex);
    public string LightQualityLabel => ValueAtOrFirst(LightQualities, LightQualityIndex);
    public string BuildLanguageLabel =>
        GetBuildLanguageLabel(GetBuildLanguageIndex(GetEffectiveBuildLanguage()));
    public string BuildLanguageToolTip =>
        GetSelectedBuildLanguageValue() == DefaultBuildLanguageSetting
            ? $"Build language: {BuildLanguageLabel} (default)"
            : $"Build language: {BuildLanguageLabel}";
    public bool IsCompileEntsMode => CompileModeIndex == 0;
    public bool IsCompileFullMode => CompileModeIndex == 1;
    public bool IsLightLowQuality => LightQualityIndex == 0;
    public bool IsLightMediumQuality => LightQualityIndex == 1;
    public bool IsLightHighQuality => LightQualityIndex == 2;
    public string[] LaunchConfigs { get; } = [LaunchConfig.Dev, LaunchConfig.Ship];
    [ObservableProperty] private int _launchConfigIndex;
    public bool IsDevLaunchConfig => LaunchConfigIndex == 0;
    public bool IsShipLaunchConfig => LaunchConfigIndex == 1;

    private static readonly Dictionary<string, string> DevPresets = new()
    {
        ["developer"] = "2",
        ["scr_mod_enable_devblock"] = "true",
        ["com_clientfieldsDebug"] = "true",
    };

    private static readonly Dictionary<string, string> ShipPresets = new()
    {
        ["developer"] = "0",
        ["scr_mod_enable_devblock"] = "false",
        ["com_clientfieldsDebug"] = "false",
    };

    /// <summary>The dvars a launch config starts with, and what Reset returns it to.</summary>
    public static Dictionary<string, string> GetLaunchConfigPreset(string configName) =>
        new(configName == LaunchConfig.Ship ? ShipPresets : DevPresets);

    partial void OnLaunchConfigIndexChanged(int value)
    {
        OnPropertyChanged(nameof(LaunchConfigSummary));
        OnPropertyChanged(nameof(IsDevLaunchConfig));
        OnPropertyChanged(nameof(IsShipLaunchConfig));
        if (_isLoadingProject) return;
        ApplyEnvironmentDvars();
        RefreshSelectedBuildLanguage();
        SaveProjectSettings();
    }

    /// <summary>
    /// Load the active environment's dvars for the current project, seeding from defaults if needed.
    /// </summary>
    private void ApplyEnvironmentDvars()
    {
        if (SelectedProject is null || SelectedProject.IsGroupHeader) return;

        var ps = GetOrCreateProjectSettings(SelectedProject.Key);
        var configName = LaunchConfigs[LaunchConfigIndex];

        if (!ps.EnvironmentDvars.TryGetValue(configName, out var dvars))
        {
            // Seed from default presets on first use
            dvars = GetLaunchConfigPreset(configName);
            ps.EnvironmentDvars[configName] = dvars;
        }

        if (!ps.EnvironmentBuildLanguages.ContainsKey(configName))
            ps.EnvironmentBuildLanguages[configName] = DefaultBuildLanguageSetting;

        _runDvars = Dvar.BuildCommandArgs(dvars);
    }

    /// <summary>What "Default" resolves to: Ship links all languages, Dev just the game's.</summary>
    private string GetDefaultBuildLanguage(string configName) =>
        configName == LaunchConfig.Ship ? "All" : _gameLanguage;

    private static bool IsKnownBuildLanguageSetting(string language) =>
        BuildLanguageSettingValues.Contains(language);

    public static int GetBuildLanguageIndex(string? language)
    {
        var index = Array.FindIndex(
            BuildLanguageValues,
            value => string.Equals(value, language, StringComparison.OrdinalIgnoreCase));

        return index >= 0 ? index : 0;
    }

    public static int GetBuildLanguageSettingIndex(string? language)
    {
        var index = Array.FindIndex(
            BuildLanguageSettingValues,
            value => string.Equals(value, language, StringComparison.OrdinalIgnoreCase));

        return index >= 0 ? index : 0;
    }

    public static string GetBuildLanguageLabel(int index) =>
        ValueAtOrFirst(BuildLanguageLabels, index);

    public static string GetBuildLanguageSettingValue(int index) =>
        ValueAtOrFirst(BuildLanguageSettingValues, index);

    private string GetSelectedBuildLanguageValue() =>
        GetBuildLanguageSettingValue(SelectedBuildLanguageIndex);

    private void RefreshSelectedBuildLanguage()
    {
        var language = GetActiveBuildLanguage();
        SelectedBuildLanguageIndex = GetBuildLanguageSettingIndex(language);
        NotifyBuildLanguageLabelsChanged();
    }

    /// <summary>The stored per-environment language setting; may be the "Default" sentinel.</summary>
    private string GetActiveBuildLanguage()
    {
        if (SelectedProject is null || SelectedProject.IsGroupHeader)
            return DefaultBuildLanguageSetting;

        var ps = GetOrCreateProjectSettings(SelectedProject.Key);
        var configName = LaunchConfigs[LaunchConfigIndex];

        if (!ps.EnvironmentBuildLanguages.TryGetValue(configName, out var language)
            || !IsKnownBuildLanguageSetting(language))
        {
            language = DefaultBuildLanguageSetting;
            ps.EnvironmentBuildLanguages[configName] = language;
        }

        return language;
    }

    /// <summary>The concrete language a build will use, with "Default" resolved per config.</summary>
    public string GetEffectiveBuildLanguage()
    {
        var setting = GetActiveBuildLanguage();
        return setting == DefaultBuildLanguageSetting
            ? GetDefaultBuildLanguage(ValueAtOrFirst(LaunchConfigs, LaunchConfigIndex))
            : setting;
    }

    /// <summary>
    /// The picker's highlighted row moves with the arrow keys and never switches project by
    /// itself; Enter or a click commits it. Enter on a group header folds the group instead.
    /// </summary>
    public bool CommitPickerSelection(ProjectItem? row = null)
    {
        row ??= PickerSelection;
        if (row is null)
            return false;

        if (row.IsGroupHeader)
        {
            ToggleProjectGroup(row.GroupName);
            return false;
        }

        if (TrySelectProject(row))
            return true;

        SyncPickerSelection();
        return false;
    }

    private void SyncPickerSelection()
    {
        _syncingPickerSelection = true;
        try
        {
            PickerSelection = SelectedProject is not null && Projects.Contains(SelectedProject)
                ? SelectedProject
                : null;
        }
        finally
        {
            _syncingPickerSelection = false;
        }
    }

    private static bool IsSameProject(ProjectItem? a, ProjectItem? b) =>
        a is not null
        && b is not null
        && a.Type == b.Type
        && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Switches the active project unless a build is running, since the
    /// build's log and result belong to the project it started on.
    /// </summary>
    public bool TrySelectProject(ProjectItem project)
    {
        if (project.IsGroupHeader)
            return false;

        if (!CanSwitchProject && !IsSameProject(SelectedProject, project))
        {
            StatusText = SwitchProjectBlockedReason;
            return false;
        }


        SelectedProject = project;
        return true;
    }

    partial void OnSelectedProjectChanged(ProjectItem? oldValue, ProjectItem? newValue)
    {
        // Reject group header selection — revert to previous
        if (newValue is { IsGroupHeader: true })
        {
            SelectedProject = oldValue;
            return;
        }

        SyncPickerSelection();

        // Unsubscribe from old mod zone changes
        if (oldValue is { Type: ProjectType.Mod })
        {
            foreach (var zone in oldValue.ZoneFiles)
                zone.PropertyChanged -= OnZoneFilePropertyChanged;
        }

        StopWatchingSelectedProject();

        // Subscribe to new mod zone changes
        if (newValue is { Type: ProjectType.Mod })
        {
            foreach (var zone in newValue.ZoneFiles)
                zone.PropertyChanged += OnZoneFilePropertyChanged;
        }

        if (newValue is { IsGroupHeader: false })
            WatchSelectedProject(newValue.FolderPath);

        OnPropertyChanged(nameof(IsMapSelected));
        OnPropertyChanged(nameof(IsModSelected));
        OnPropertyChanged(nameof(HasSelectedProject));
        OnPropertyChanged(nameof(FavoriteActionLabel));
        OnPropertyChanged(nameof(HasQuickLaunchMapOptions));
        OnPropertyChanged(nameof(ZoneSummary));
        OnPropertyChanged(nameof(LaunchConfigSummary));
        NotifyProjectCapabilityChanged();
        _ = RefreshProjectCapabilitiesAsync();
        NotifyEmptyStateChanged();
        NotifyBuildStateChanged();

        // Load persisted project settings. A rescan swaps in a fresh instance of the
        // same project; the log and counts on screen are already its own, so keep them.
        if (newValue is not null && !newValue.IsGroupHeader)
            LoadProjectSettings(newValue, restoreBuildState: !IsSameProject(oldValue, newValue));
        else
        {
            BuildPresets.ReplaceAll([]);
            QuickLaunchMaps.ReplaceAll([]);
            SelectedQuickLaunchMap = NoQuickLaunchMapLabel;
            SelectedBuildLanguageIndex = 0;
            ResetOutputText();
            ErrorCount = 0;
            LastBuildOutcome = BuildOutcome.None;
            WarningCount = 0;
            StatusText = "Ready";
            BuildTimestamp = "";
            BuildDurationText = "";
        }

        // Persist selection
        if (newValue is not null)
        {
            _settings.LastActiveProject = newValue.Name;
            _settings.LastActiveProjectType = newValue.Type == ProjectType.Map ? "map" : "mod";
            UpdateRecentProjects(newValue.Key);
            QueueSettingsSave();
        }
    }

    private const int RecentProjectsMax = 8;

    private void UpdateRecentProjects(string key)
    {
        var recents = _settings.RecentProjects;
        recents.RemoveAll(r => string.Equals(r, key, StringComparison.OrdinalIgnoreCase));
        recents.Insert(0, key);
        if (recents.Count > RecentProjectsMax)
            recents.RemoveRange(RecentProjectsMax, recents.Count - RecentProjectsMax);
    }

    private void OnZoneFilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModZoneFile.IsChecked))
        {
            _ = RefreshProjectCapabilitiesAsync();
            OnPropertyChanged(nameof(ZoneSummary));
            NotifyBuildStateChanged();
            SaveProjectSettings();
        }
    }

    [RelayCommand]
    private void EnableAll()
    {
        if (IsMapSelected)
        {
            IsCompileChecked = true;
            IsLightChecked = true;
        }
        if (IsModSelected && SelectedProject is not null)
        {
            foreach (var zone in SelectedProject.ZoneFiles)
                zone.IsChecked = true;
        }
        IsLinkChecked = true;
    }

    /// <summary>The update check and the title bar's update control.</summary>
    public Updater Updates { get; }

    public MainWindowViewModel(
        IFileSystemService fileSystem,
        IBuildService buildService,
        ISettingsService settings,
        Updater updates)
    {
        Updates = updates;
        _fileSystem = fileSystem;
        _buildService = buildService;
        _settings = settings;

        QuickLaunchMaps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQuickLaunchMapOptions));
        _settingsSaveTimer.Tick += (_, _) => _ = SaveSettingsInBackgroundAsync();
        _projectSearchDebounce.Tick += (_, _) =>
        {
            _projectSearchDebounce.Stop();
            RebuildProjectList(preserveSelection: true, projectsChanged: false);
        };
        _buildActivityTimer.Tick += (_, _) => UpdateBuildActivityText();
        _transientStatusTimer.Tick += (_, _) => RestoreStatusAfterTransient();
    }

    /// <summary>
    /// Shows a short-lived status message (e.g. "Copied to clipboard.") that
    /// reverts to the previous status after a few seconds, unless another
    /// status update replaces it first.
    /// </summary>
    public void ShowTransientStatus(string message)
    {
        if (_transientStatusMessage is null)
            _statusTextBeforeTransient = StatusText;

        _transientStatusMessage = message;
        StatusText = message;
        _transientStatusTimer.Stop();
        _transientStatusTimer.Start();
    }

    private void RestoreStatusAfterTransient()
    {
        _transientStatusTimer.Stop();
        if (_transientStatusMessage is not null && StatusText == _transientStatusMessage)
            StatusText = _statusTextBeforeTransient;
        _transientStatusMessage = null;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        _isInitialized = true;
        Dvar.WarmDefaultsInBackground();
        LoadCustomTools();
        LoadCachedProjects();
        await RefreshSetupAsync();
    }

    public Task RefreshSetupAsync() => PopulateFileListAsync(restoreLastProject: true);

    public async Task PopulateFileListAsync(bool restoreLastProject = false)
    {
        var refreshVersion = Interlocked.Increment(ref _projectRefreshVersion);
        IsProjectScanRunning = true;
        if (!IsBuildRunning)
            StatusText = "Scanning projects\u2026";

        var loadToolIcons = AssetEditorIcon is null
                            || RadiantIcon is null
                            || (IsApexAvailable && ApexIcon is null)
                            || !string.Equals(_toolIconsPath, _fileSystem.ToolsPath, StringComparison.OrdinalIgnoreCase);
        ProjectRefreshResult result;
        try
        {
            result = await Task.Run(() => BuildProjectRefreshResult(loadToolIcons));
        }
        catch (Exception ex)
        {
            if (refreshVersion == _projectRefreshVersion)
            {
                // Keep the list from the last good scan rather than leave the picker half-cleared.
                CrashLog.Write("Project scan failed", ex);
                IsProjectScanRunning = false;
                StatusText = ErrorText.Describe("Couldn't read the project folders.", ex);
                OnPropertyChanged(nameof(HasAnyProjects));
                NotifyEmptyStateChanged();
                NotifyBuildStateChanged();
            }
            return;
        }

        if (refreshVersion != _projectRefreshVersion)
        {
            DisposeToolIcons(result.Tools);
            return;
        }

        // Sizes arrive in the background; until then keep showing the last known ones.
        var knownSizes = _allProjects
            .GroupBy(project => project.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().SizeBytes, StringComparer.OrdinalIgnoreCase);
        foreach (var project in result.Projects)
        {
            if (knownSizes.TryGetValue(project.FolderPath, out var size))
                project.SizeBytes = size;
        }

        ApplyProjectList(result.Projects, restoreLastProject);
        SaveProjectCache(result.Projects);

        IsSetupHealthy = result.IsSetupHealthy;
        ApplyToolAvailability(result.Tools, updateIcons: loadToolIcons);
        _ = RefreshProjectCapabilitiesAsync();

        IsProjectScanRunning = false;
        if (!HasSelectedProject && !IsBuildRunning)
            StatusText = "Ready";

        OnPropertyChanged(nameof(HasAnyProjects));
        NotifyEmptyStateChanged();
        NotifyBuildStateChanged();

        _ = RefreshProjectSizesAsync(refreshVersion);
    }

    private ProjectRefreshResult BuildProjectRefreshResult(bool loadToolIcons)
    {
        var projects = _fileSystem.ScanMaps()
            .Select(map => new ProjectItem(map))
            .Concat(_fileSystem.ScanMods().Select(mod => new ProjectItem(mod)))
            .ToList();

        foreach (var project in projects)
            ApplyProjectDerivedMetadata(project);

        return new ProjectRefreshResult(
            projects,
            _fileSystem.ValidateSetup().IsHealthy,
            BuildToolAvailability(loadToolIcons));
    }

    /// <summary>
    /// Measures every project folder in parallel off the UI thread and applies the sizes,
    /// unless a newer scan has replaced the list in the meantime.
    /// </summary>
    private async Task RefreshProjectSizesAsync(int refreshVersion)
    {
        var folders = _allProjects
            .Select(project => project.FolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sizes = await Task.Run(() =>
        {
            var measured = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Parallel.ForEach(
                folders,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4) },
                folder => measured[folder] = GetCachedDirectorySize(folder));
            return measured;
        });

        if (refreshVersion != _projectRefreshVersion)
            return;

        var changed = false;
        foreach (var project in _allProjects)
        {
            if (sizes.TryGetValue(project.FolderPath, out var size) && project.SizeBytes != size)
            {
                project.SizeBytes = size;
                changed = true;
            }
        }

        if (changed)
            SaveProjectCache(_allProjects);
    }

    /// <summary>
    /// A folder's size, reused while the folder and its immediate subfolders keep their
    /// last-write times. Files rewritten in place don't touch those, so builds invalidate explicitly.
    /// </summary>
    private long GetCachedDirectorySize(string folderPath)
    {
        var stamp = GetFolderStamp(folderPath);
        if (_projectSizeCache.TryGetValue(folderPath, out var cached) && cached.Stamp == stamp)
            return cached.Size;

        var size = CalculateDirectorySize(folderPath);
        _projectSizeCache[folderPath] = (stamp, size);
        return size;
    }

    private static long GetFolderStamp(string folderPath)
    {
        try
        {
            var root = new DirectoryInfo(folderPath);
            if (!root.Exists)
                return 0;

            var stamp = root.LastWriteTimeUtc.Ticks;
            foreach (var child in root.EnumerateDirectories())
                stamp = unchecked(stamp * 31 + child.LastWriteTimeUtc.Ticks);
            return stamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void InvalidateProjectSize(ProjectItem project) =>
        _projectSizeCache.TryRemove(project.FolderPath, out _);

    private void LoadCachedProjects()
    {
        var cachedProjects = _settings.CachedProjects
            .Select(CreateProjectItemFromCache)
            .Where(project => project is not null)
            .Cast<ProjectItem>()
            .ToList();

        if (cachedProjects.Count == 0)
            return;

        ApplyProjectList(cachedProjects, restoreLastProject: true);
        OnPropertyChanged(nameof(HasAnyProjects));
        NotifyEmptyStateChanged();
        NotifyBuildStateChanged();
    }

    private void ApplyProjectList(List<ProjectItem> projects, bool restoreLastProject)
    {
        _allProjects = projects.Where(project => !project.IsGroupHeader).ToList();
        MigrateNameOnlyProjectKeys();
        foreach (var project in projects)
            ApplyProjectMetadata(project);

        RefreshProjectFilters();
        RebuildProjectList(
            restoreLastProject: restoreLastProject,
            preserveSelection: !restoreLastProject);
    }

    /// <summary>
    /// Settings, recents and logs used to be keyed by name alone, so a map and a mod
    /// with the same name shared them. Each old entry goes to whichever project
    /// exists under that name, the map when both do.
    /// </summary>
    private void MigrateNameOnlyProjectKeys()
    {
        string? CurrentKey(string name) =>
            (_allProjects.FirstOrDefault(p => p.Type == ProjectType.Map && p.Name == name)
             ?? _allProjects.FirstOrDefault(p => p.Type == ProjectType.Mod && p.Name == name))?.Key;

        var migrated = false;
        foreach (var name in _settings.Projects.Keys.Where(key => !key.Contains(':')).ToList())
        {
            if (CurrentKey(name) is not { } key)
                continue;

            var legacy = _settings.Projects[name];
            if (!_settings.Projects.TryGetValue(key, out var current) || PreferLegacySettings(legacy, current))
                _settings.Projects[key] = legacy;
            _settings.Projects.Remove(name);
            MoveBuildLog(name, key);
            migrated = true;
        }

        for (var i = 0; i < _settings.RecentProjects.Count; i++)
        {
            if (!_settings.RecentProjects[i].Contains(':') && CurrentKey(_settings.RecentProjects[i]) is { } key)
            {
                _settings.RecentProjects[i] = key;
                migrated = true;
            }
        }

        if (migrated)
            QueueSettingsSave();
    }

    /// <summary>
    /// When an old name-only entry and a new keyed one both exist, keeps the one built most recently;
    /// with no builds to compare, the one that holds anything over an empty one, else the new one.
    /// </summary>
    private static bool PreferLegacySettings(ProjectSettings legacy, ProjectSettings current)
    {
        var legacyBuilt = legacy.LastBuildTimestamp ?? DateTime.MinValue;
        var currentBuilt = current.LastBuildTimestamp ?? DateTime.MinValue;
        if (legacyBuilt != currentBuilt)
            return legacyBuilt > currentBuilt;

        static bool IsEmpty(ProjectSettings settings) =>
            settings.DisplayName.Length == 0
            && settings.Category.Length == 0
            && settings.Notes.Length == 0
            && settings.EnvironmentDvars.Values.All(dvars => dvars.Count == 0)
            && settings.BuildPresets.Count == 0
            && settings.RunOptions.Length == 0
            && settings.QuickLaunchMap.Length == 0;

        return IsEmpty(current) && !IsEmpty(legacy);
    }

    private void RefreshProjectFilters()
    {
        var selectedFilter = GetSelectedProjectFilterLabel();
        ProjectFilters = [.. BuiltInProjectFilters, .. GetCustomProjectCategories()];

        // A category that no longer exists falls back to All.
        var selectedIndex = ProjectFilters
            .Select((filter, index) => (filter, index))
            .FirstOrDefault(item => string.Equals(item.filter, selectedFilter, StringComparison.OrdinalIgnoreCase),
                (filter: "", index: 0))
            .index;

        SelectedProjectFilterIndex = selectedIndex;

    }

    private IEnumerable<string> GetCustomProjectCategories()
    {
        return _allProjects
            .Select(project => project.Category?.Trim() ?? "")
            .Where(category => category.Length > 0)
            .Where(category => !DefaultProjectCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
            .Where(category => !BuiltInProjectFilters.Contains(category, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True while RebuildProjectList is re-populating Projects. The picker
    /// flyout uses this to ignore programmatic re-selection (filter/search
    /// changes, group collapse) so it only closes on a real user pick.
    /// </summary>
    public bool IsRebuildingProjectList { get; private set; }

    /// <param name="projectsChanged">False when only search, filter, favorites or grouping changed, so the project set is the same.</param>
    private void RebuildProjectList(bool restoreLastProject = false, bool preserveSelection = false, bool projectsChanged = true)
    {
        IsRebuildingProjectList = true;
        try
        {
            var previous = preserveSelection ? SelectedProject : null;
            var visibleProjects = FilterProjects(_allProjects).ToList();
            var rows = new List<ProjectItem>(visibleProjects.Count + 8);

            foreach (var group in visibleProjects
                     .GroupBy(p => p.Category)
                     .OrderBy(g => CategorySort(g.Key))
                     .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var groupProjects = SortProjectsForCurrentFilter(group).ToList();
                // A search shows every match, folded groups included.
                var isCollapsed = ProjectSearchText.Trim().Length == 0 && IsProjectGroupCollapsed(group.Key);
                rows.Add(ProjectItem.CreateHeader(group.Key, groupProjects.Count, isCollapsed));

                if (!isCollapsed)
                    rows.AddRange(groupProjects);
            }

            Projects.ReplaceAll(rows);

            // The active project stays selected even when search, filters or a
            // collapsed group hide it from the picker. It is only replaced when
            // a rescan produced a new instance, or cleared when it is gone.
            if (restoreLastProject)
                RestoreLastProject();
            else if (previous is not null)
                SelectedProject = _allProjects.FirstOrDefault(p => IsSameProject(p, previous));

            if (projectsChanged)
            {
                OnPropertyChanged(nameof(ProjectCatalog));
                RefreshQuickLaunchMapOptions();
                OnPropertyChanged(nameof(HasAnyProjects));
            }

            OnPropertyChanged(nameof(HasVisibleProjects));
            OnPropertyChanged(nameof(PickerEmptyText));
        }
        finally
        {
            SyncPickerSelection();
            IsRebuildingProjectList = false;
        }
    }

    private void RefreshQuickLaunchMapOptions(string? preferredMap = null)
    {
        var previousSelection = preferredMap ?? NormalizeQuickLaunchMapForStorage(SelectedQuickLaunchMap);

        if (SelectedProject is not { IsGroupHeader: false, Type: ProjectType.Mod })
        {
            QuickLaunchMaps.ReplaceAll([NoQuickLaunchMapLabel]);
            SelectedQuickLaunchMap = NoQuickLaunchMapLabel;
            return;
        }

        var mapNames = _allProjects
            .Where(project => project.Type == ProjectType.Map)
            .Select(project => project.Name)
            .Concat(ProjectNameRules.BuiltInMaps)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

        QuickLaunchMaps.ReplaceAll([NoQuickLaunchMapLabel, .. mapNames]);

        SelectedQuickLaunchMap = QuickLaunchMaps.Contains(previousSelection, StringComparer.OrdinalIgnoreCase)
            ? QuickLaunchMaps.First(map => string.Equals(map, previousSelection, StringComparison.OrdinalIgnoreCase))
            : NoQuickLaunchMapLabel;
    }

    private static string NormalizeQuickLaunchMapForStorage(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        return string.Equals(trimmed, NoQuickLaunchMapLabel, StringComparison.OrdinalIgnoreCase)
            ? ""
            : trimmed;
    }

    private string GetSelectedQuickLaunchMapName() =>
        NormalizeQuickLaunchMapForStorage(SelectedQuickLaunchMap);

    private void SaveProjectCache(List<ProjectItem> projects)
    {
        _settings.CachedProjects =
        [
            .. projects
                .Where(project => !project.IsGroupHeader)
                .Select(ToCachedProjectData)
        ];
        QueueSettingsSave();
    }

    private IEnumerable<ProjectItem> FilterProjects(IEnumerable<ProjectItem> projects)
    {
        var filtered = projects.Where(project => !project.IsGroupHeader);

        if (SelectedProjectFilterIndex >= BuiltInProjectFilterCount)
        {
            var category = GetSelectedProjectFilterLabel();
            filtered = filtered.Where(project =>
                string.Equals(project.Category, category, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            filtered = SelectedProjectFilterIndex switch
            {
                1 => SortByRecent(filtered.Where(IsRecentProject)),
                2 => filtered.Where(project => project.IsFavorite),
                3 => filtered.Where(project => project.Type == ProjectType.Map
                                               && project.Name.StartsWith("zm_", StringComparison.OrdinalIgnoreCase)),
                4 => filtered.Where(project => project.Type == ProjectType.Map
                                               && project.Name.StartsWith("mp_", StringComparison.OrdinalIgnoreCase)),
                5 => filtered.Where(project => project.Type == ProjectType.Mod),
                6 => filtered.Where(project => project.IsPublished),
                7 => filtered.Where(project => !project.IsPublished),
                _ => filtered,
            };
        }

        var query = ProjectSearchText.Trim();
        if (query.Length == 0)
            return filtered;

        return filtered.Where(project => ProjectMatchesQuery(project, query));
    }

    private string GetSelectedProjectFilterLabel()
    {
        if (ProjectFilters.Count == 0)
            return BuiltInProjectFilters[0];

        var index = Math.Clamp(SelectedProjectFilterIndex, 0, ProjectFilters.Count - 1);
        return ProjectFilters[index];
    }

    private IEnumerable<ProjectItem> SortProjectsForCurrentFilter(IEnumerable<ProjectItem> projects)
    {
        if (SelectedProjectFilterIndex == 1)
            return SortByRecent(projects);

        return projects
            .OrderByDescending(project => project.IsFavorite)
            .ThenBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<ProjectItem> SortByRecent(IEnumerable<ProjectItem> projects)
    {
        var recentOrder = _settings.RecentProjects
            .Select((key, index) => (key, index))
            .GroupBy(item => item.key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Min(item => item.index), StringComparer.OrdinalIgnoreCase);

        return projects
            .Where(project => recentOrder.ContainsKey(project.Key))
            .OrderBy(project => recentOrder[project.Key])
            .ThenBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    private bool IsRecentProject(ProjectItem project) =>
        _settings.RecentProjects.Contains(project.Key, StringComparer.OrdinalIgnoreCase);

    private static bool ProjectMatchesQuery(ProjectItem project, string query)
    {
        return Contains(project.DisplayName, query)
               || Contains(project.Name, query)
               || Contains(project.Category, query)
               || Contains(project.PublishedLabel, query)
               || Contains(project.Notes, query);
    }

    /// <summary>The project Enter in the picker's search opens: a name that starts with the query beats one that only contains it.</summary>
    public ProjectItem? BestPickerMatch()
    {
        // Enter can land inside the typing pause: filter for what's typed now, not the last pause.
        if (_projectSearchDebounce.IsEnabled)
        {
            _projectSearchDebounce.Stop();
            RebuildProjectList(preserveSelection: true, projectsChanged: false);
        }

        var query = ProjectSearchText.Trim();
        var rows = Projects.Where(p => !p.IsGroupHeader).ToList();
        if (query.Length == 0)
            return rows.FirstOrDefault();

        static bool StartsWith(string? value, string query) =>
            value?.StartsWith(query, StringComparison.OrdinalIgnoreCase) == true;

        return rows.FirstOrDefault(p => string.Equals(p.DisplayName, query, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(p.Name, query, StringComparison.OrdinalIgnoreCase))
               ?? rows.FirstOrDefault(p => StartsWith(p.DisplayName, query) || StartsWith(p.Name, query))
               ?? rows.FirstOrDefault();
    }

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static CachedProjectData ToCachedProjectData(ProjectItem project) => new()
    {
        Name = project.Name,
        FolderPath = project.FolderPath,
        ZoneFilePath = project.ZoneFilePath ?? "",
        Type = project.Type == ProjectType.Map ? "map" : "mod",
        ZoneFiles = project.Type == ProjectType.Mod
            ? [.. project.ZoneFiles.Select(zone => zone.ZoneName)]
            : [],
        SizeBytes = project.SizeBytes,
        IsPublished = project.IsPublished,
    };

    private static ProjectItem? CreateProjectItemFromCache(CachedProjectData cache)
    {
        if (string.IsNullOrWhiteSpace(cache.Name)
            || string.IsNullOrWhiteSpace(cache.FolderPath))
        {
            return null;
        }

        if (string.Equals(cache.Type, "map", StringComparison.OrdinalIgnoreCase))
        {
            return new ProjectItem(new MapItem(cache.Name, cache.FolderPath, cache.ZoneFilePath))
            {
                SizeBytes = cache.SizeBytes,
                IsPublished = cache.IsPublished,
            };
        }

        if (string.Equals(cache.Type, "mod", StringComparison.OrdinalIgnoreCase))
        {
            var mod = new ModItem(cache.Name, cache.FolderPath);
            foreach (var zone in cache.ZoneFiles.Where(z => !string.IsNullOrWhiteSpace(z)))
                mod.ZoneFiles.Add(new ModZoneFile(zone, cache.Name));
            return new ProjectItem(mod)
            {
                SizeBytes = cache.SizeBytes,
                IsPublished = cache.IsPublished,
            };
        }

        return null;
    }

    /// <summary>Replaces the log. Any saved-log read still in flight is abandoned.</summary>
    private void ResetOutputText(string text = "")
    {
        _logLoadVersion++;
        _diagnosticCarry = "";
        Output.Replace(text);
        HasOutput = text.Length > 0;
    }

    private void AppendOutputText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        _logLoadVersion++;
        Output.Append(text);
        HasOutput = true;
    }

    /// <summary>
    /// Shows the project's saved log once it has been read off the UI thread. A switch,
    /// build or clear that happens first wins; the stale read is dropped.
    /// </summary>
    private async Task LoadSavedBuildLogAsync(string projectKey)
    {
        _logLoadVersion++;
        var version = _logLoadVersion;
        _diagnosticCarry = "";
        Output.Replace(isLoading: true);
        HasOutput = false;

        var logPath = _settings.GetBuildLogPath(projectKey);
        var text = await QueueLogFileWork(() =>
        {
            try
            {
                return File.Exists(logPath) ? File.ReadAllText(logPath) : "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "";
            }
        });

        if (version == _logLoadVersion)
            ResetOutputText(text);
    }

    /// <param name="log">From <see cref="BuildLog.Snapshot"/>: joined into one string off the UI thread.</param>
    private void SaveBuildLog(string projectKey, string[] log)
    {
        var logPath = _settings.GetBuildLogPath(projectKey);
        _ = QueueLogFileWork(() =>
        {
            try
            {
                JsonSettingsService.WriteAtomic(logPath, string.Concat(log));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: the build result itself is already recorded in settings.
            }

            return true;
        });
    }

    /// <summary>
    /// Runs log-file IO on the thread pool one operation at a time, in the order it was
    /// queued, so a save, move or delete always lands before a later read of the same file.
    /// </summary>
    private Task<T> QueueLogFileWork<T>(Func<T> work)
    {
        var next = _logFileQueue.ContinueWith(
            _ => work(),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        _logFileQueue = next;
        return next;
    }

    private void StartBuildActivityTimer()
    {
        _buildActivityStopwatch.Restart();
        UpdateBuildActivityText();
        _buildActivityTimer.Start();
    }

    private void StopBuildActivityTimer()
    {
        _buildActivityTimer.Stop();
        _buildActivityStopwatch.Stop();
        BuildActivityText = "";
    }

    private void UpdateBuildActivityText()
    {
        if (!IsBuildRunning)
        {
            BuildActivityText = "";
            return;
        }

        var label = IsBuildCancelling ? "Cancelling" : "Building";
        BuildActivityText = $"{label} \u00b7 {FormatElapsed(_buildActivityStopwatch.Elapsed)}";
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"m\:ss");

    /// <summary>The theme: System follows Windows. Applied at startup by App, and here when the user picks one.</summary>
    public ThemeChoice Theme => AppTheme.Parse(_settings.Theme);
    public bool IsThemeSystem => Theme == ThemeChoice.System;
    public bool IsThemeGraphite => Theme == ThemeChoice.Graphite;
    public bool IsThemeSlate => Theme == ThemeChoice.Slate;
    public bool IsThemeLight => Theme == ThemeChoice.Light;

    [RelayCommand]
    private void SetTheme(ThemeChoice choice)
    {
        if (choice != Theme)
        {
            _settings.Theme = choice.ToString();
            AppTheme.Apply(choice);
            QueueSettingsSave();
        }
        // Raised even when unchanged: a radio menu item that was clicked must show the truth again.
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeGraphite));
        OnPropertyChanged(nameof(IsThemeSlate));
        OnPropertyChanged(nameof(IsThemeLight));
    }

    /// <summary>Saves settings shortly after the last change, writing off the UI thread.</summary>
    public void QueueSettingsSave()
    {
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private async Task SaveSettingsInBackgroundAsync()
    {
        _settingsSaveTimer.Stop();
        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception ex)
        {
            // Nothing awaits this save, so report here rather than lose the failure.
            CrashLog.Write("Settings save failed", ex);
            StatusText = ErrorText.Describe("Couldn't save Blackbird's settings.", ex);
        }
    }

    /// <summary>The one synchronous settings write, made as the app exits.</summary>
    public void SaveSettingsOnExit()
    {
        _settingsSaveTimer.Stop();
        _settings.Save();

        // Let a just-finished build's log land, but never hold up exit for long.
        try
        {
            _logFileQueue.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            CrashLog.Write("Saving the build log on exit failed", ex);
        }
    }

    public Task<string[]> GetSelectedProjectXPakFilesAsync()
    {
        if (SelectedProject is not { IsGroupHeader: false } project)
            return Task.FromResult<string[]>([]);

        var folder = project.FolderPath;
        return Task.Run(() => Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.xpak", SearchOption.AllDirectories)
            : []);
    }

    /// <summary>False when the user declined Windows' permanent-delete warning; the project stays.</summary>
    public async Task<bool> MoveSelectedProjectToRecycleBinAsync()
    {
        if (SelectedProject is not { IsGroupHeader: false } project)
            return false;

        var folder = project.FolderPath;
        if (!await Task.Run(() => Directory.Exists(folder)))
            throw new DirectoryNotFoundException(folder);

        if (!await RecycleBin.SendAsync([folder]) && Directory.Exists(folder))
            return false;

        if (IsSameProject(SelectedProject, project))
            SelectedProject = null;
        _allProjects.RemoveAll(item => IsSameProject(item, project));
        InvalidateProjectSize(project);
        RebuildProjectList();

        // The project's settings stay: restoring the folder from the Recycle Bin brings them back with it.
        if (_settings.LastActiveProject
 == project.Name
            && _settings.LastActiveProjectType == (project.Type == ProjectType.Map ? "map" : "mod"))
        {
            _settings.LastActiveProject = "";
            _settings.LastActiveProjectType = "";
        }
        QueueSettingsSave();
        return true;
    }

    /// <summary>How many .xpak files went to the Recycle Bin; null when the user declined Windows' permanent-delete warning.</summary>
    public async Task<int?> MoveSelectedProjectXPaksToRecycleBinAsync()
    {
        if (SelectedProject is not { IsGroupHeader: false } project)
            return 0;

        var xpaks = await GetSelectedProjectXPakFilesAsync();
        var completed = await RecycleBin.SendAsync(xpaks);

        InvalidateProjectSize(project);
        return completed ? xpaks.Length : null;
    }

    public async Task<(int MovedXpaks, string Status)> PrepareSelectedProjectForPublishAsync(bool cleanXpaks)
    {
        if (SelectedProject is not { IsGroupHeader: false } project)
            throw new UserMessageException("Choose a project first.");

        if (IsBuildRunning || IsPreparingForPublish)
            throw new UserMessageException("A build is already running.");

        if (IsPublishing)
            throw new UserMessageException("Wait for the Workshop upload to finish.");

        if (HasSetupIssues)
            throw new UserMessageException("Open Setup Doctor before preparing for publish.");

        // A link-only pass: all languages for this run only. Dev/Ship dvars only
        // affect launching, so the project's saved settings stay as they are.
        var commands = BuildCommandList(
            forcedBuildLanguage: "All",
            forceRun: false,
            linkOnly: true,
            linkAllModZones: false);

        if (commands.Count == 0)
        {
            throw new UserMessageException(project.Type == ProjectType.Mod
                ? "Select at least one mod zone before preparing for publish."
                : "Couldn't create a publish-ready link command.");
        }

        IsPreparingForPublish = true;
        try
        {
            var movedXpaks = cleanXpaks ? await MoveSelectedProjectXPaksToRecycleBinAsync() : 0;
            if (movedXpaks is not { } moved)
                throw new UserMessageException("The XPaks weren't cleaned, so nothing was built.");

            var status = await RunPublishReadyBuildAsync(commands, moved);
            return (moved, status);
        }
        finally
        {
            IsPreparingForPublish = false;
        }
    }

    private async Task<string> RunPublishReadyBuildAsync(List<BuildCommand> commands, int movedXpaks)
    {
        IsBuildRunning = true;
        IsBuildCancelling = false;
        BuildButtonText = "Cancel";
        ResetOutputText();
        ErrorCount = 0;
        LastBuildOutcome = BuildOutcome.None;
        WarningCount = 0;
        StatusText = "Preparing for publish\u2026";
        BuildTimestamp = "";
        BuildDurationText = "";

        AppendOutputText("Prepare for publish\n");
        AppendOutputText(movedXpaks == 0
            ? "No generated XPaks needed cleanup.\n"
            : $"Moved {movedXpaks} generated XPak file{(movedXpaks == 1 ? "" : "s")} to the Recycle Bin.\n");
        AppendOutputText("Linking all languages without launching the game.\n");

        _buildCts = new CancellationTokenSource();
        var buildToken = _buildCts.Token;
        _buildCancelTask = null;
        var buildTimer = Stopwatch.StartNew();
        StartBuildActivityTimer();

        var progress = new Progress<string>(text =>
        {
            AppendOutputText(text);
            CountDiagnostics(text);
        });

        string buildStatus;
        try
        {
            var success = await _buildService.RunBuildAsync(commands, ignoreErrors: false, progress, buildToken);
            buildToken.ThrowIfCancellationRequested();

            if (success)
            {
                AppendOutputText("\nPrepare for publish completed successfully.\n");
                buildStatus = "publish prep succeeded";
            }
            else
            {
                AppendOutputText("\nPrepare for publish failed.\n");
                buildStatus = "publish prep failed";
            }
        }
        catch (OperationCanceledException)
        {
            AppendOutputText("\nPrepare for publish cancelled.\n");
            buildStatus = "publish prep cancelled";
        }
        catch (Exception ex)
        {
            // Same as RunBuildAsync: a tool that fails to start must still
            // produce a terminal status instead of an unobserved exception.
            AppendOutputText($"\nPrepare for publish failed to run: {ex.Message}\n");
            buildStatus = "publish prep failed";
        }
        finally
        {
            buildTimer.Stop();
            if (_buildCancelTask is not null)
            {
                await _buildCancelTask;
                _buildCancelTask = null;
            }

            StopBuildActivityTimer();
            IsBuildRunning = false;
            IsBuildCancelling = false;
            BuildButtonText = BuildModeLabel;
            _buildCts?.Dispose();
            _buildCts = null;
        }

        var now = DateTime.Now;
        ApplyBuildStatus(buildStatus);
        BuildTimestamp = now.ToString("d MMM yyyy 'at' HH:mm");
        BuildDurationText = FormatDuration(buildTimer.Elapsed);

        if (SelectedProject is { IsGroupHeader: false } project)
        {
            var ps = GetOrCreateProjectSettings(project.Key);
            ps.LastBuildStatus = buildStatus;
            ps.LastBuildErrorCount = ErrorCount;
            ps.LastBuildWarningCount = WarningCount;
            ps.LastBuildTimestamp = now;
            ps.LastBuildDurationMs = (long)buildTimer.Elapsed.TotalMilliseconds;
            // Only a clean prep counts — a failed/cancelled one leaves partial output.
            ps.LastPublishPrepTimestamp = buildStatus == "publish prep succeeded" ? now : null;
            QueueSettingsSave();
            SaveBuildLog(project.Key, Output.Snapshot());
            InvalidateProjectSize(project);
        }

        return buildStatus;
    }

    public async Task<string> DuplicateSelectedProjectAsync(
        string newName,
        string displayName,
        string category)
    {
        if (SelectedProject is not { IsGroupHeader: false } project)
            throw new UserMessageException("Choose a project first.");

        newName = newName.Trim();
        displayName = string.IsNullOrWhiteSpace(displayName) ? newName : displayName.Trim();
        category = string.IsNullOrWhiteSpace(category) ? project.DefaultCategory : category.Trim();

        var sourceName = project.Name;
        var projectType = project.Type;
        var sourceFolder = project.FolderPath;
        var parentFolder = Path.GetDirectoryName(sourceFolder)
            ?? throw new InvalidOperationException("Couldn't resolve the project parent folder.");
        var targetFolder = Path.Combine(parentFolder, newName);

        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException(sourceFolder);
        if (Directory.Exists(targetFolder))
            throw new IOException($"A project folder already exists at {targetFolder}.");

        var sourceSettings = _settings.Projects.TryGetValue(project.Key, out var existingSettings)
            ? CloneProjectSettings(existingSettings)
            : new ProjectSettings();

        await Task.Run(() =>
        {
            if (Directory.Exists(targetFolder))
                throw new IOException($"A project folder already exists at {targetFolder}.");
            if (MapSourceConflict(projectType, sourceName, newName) is { } mapSourceConflict)
                throw new IOException(mapSourceConflict);

            var tokens = new ProjectTokenReplacer(sourceName, newName);
            string? copiedMapSource = null;
            Directory.CreateDirectory(targetFolder);
            try
            {
                CopyDirectoryWithProjectRename(sourceFolder, targetFolder, tokens);

                if (projectType == ProjectType.Map)
                    copiedMapSource = CopyMapSourceWithProjectRename(sourceName, newName, tokens);

                ResetCopiedWorkshopIdentity(targetFolder, projectType, newName, displayName);
            }
            catch
            {
                // This call created the folder and map source, so a half-finished copy is ours to remove.
                try { Directory.Delete(targetFolder, recursive: true); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                try
                {
                    if (copiedMapSource is not null)
                        File.Delete(copiedMapSource);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                throw;
            }
        });

        sourceSettings.DisplayName = NormalizeDisplayNameForStorage(displayName, newName);
        sourceSettings.Category = NormalizeCategoryForStorage(category, project.DefaultCategory);
        sourceSettings.Notes = "";
        sourceSettings.LastBuildStatus = null;
        sourceSettings.LastBuildErrorCount = 0;
        sourceSettings.LastBuildWarningCount = 0;
        sourceSettings.LastBuildTimestamp = null;
        sourceSettings.LastBuildDurationMs = 0;
        sourceSettings.LastPublishPrepTimestamp = null;
        _settings.Projects[ProjectItem.KeyFor(projectType, newName)] = sourceSettings;
        QueueSettingsSave();

        await PopulateFileListAsync();
        SelectProjectByName(newName, projectType);
        return targetFolder;
    }

    public async Task<string> RenameSelectedProjectAsync(
        string newName,
        string displayName,
        string category)
    {
        if (SelectedProject is not { IsGroupHeader: false } project)
            throw new UserMessageException("Choose a project first.");

        if (ModifyProjectBlockedReason is { } blockedReason)
            throw new UserMessageException(blockedReason);

        newName = newName.Trim();
        var oldName = project.Name;
        displayName = string.IsNullOrWhiteSpace(displayName) ? newName : displayName.Trim();
        category = string.IsNullOrWhiteSpace(category) ? project.DefaultCategory : category.Trim();

        var projectType = project.Type;
        var sourceFolder = project.FolderPath;
        var parentFolder = Path.GetDirectoryName(sourceFolder)
            ?? throw new InvalidOperationException("Couldn't resolve the project parent folder.");
        var targetFolder = Path.Combine(parentFolder, newName);

        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            await UpdateSelectedProjectMetadataAsync(displayName, category, project.IsFavorite, project.Notes);
            return sourceFolder;
        }

        // "zm_Test" → "zm_test" is a real rename: Windows sees one folder, so it moves through a temporary name.
        var caseOnly = string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException(sourceFolder);
        if (!caseOnly && Directory.Exists(targetFolder))
            throw new IOException($"A project folder already exists at {targetFolder}.");

        // Rename moves the project's settings as they are: build result, publish prep and all.
        var settings = _settings.Projects.TryGetValue(project.Key, out var existingSettings)
            ? existingSettings
            : new ProjectSettings();

        // Once the folder has moved the project *is* renamed, so a failure in the
        // follow-up steps must not skip the settings/history/log bookkeeping below.
        Exception? referenceFailure = null;
        await Task.Run(() =>
        {
            // Checked before anything moves: the map source must never land on another map's .map.
            if (MapSourceConflict(projectType, oldName, newName) is { } mapSourceConflict)
                throw new IOException(mapSourceConflict);

            MovePath(sourceFolder, targetFolder, isDirectory: true);
            try
            {
                var tokens = new ProjectTokenReplacer(oldName, newName);
                RenameProjectReferencesInPlace(targetFolder, tokens);

                if (projectType == ProjectType.Map)
                    RenameMapSourceInPlace(oldName, newName, tokens);

                UpdateRenamedWorkshopIdentity(targetFolder, projectType, newName, displayName);
            }
            catch (Exception ex)
            {
                referenceFailure = ex;
            }
        });

        settings.DisplayName = NormalizeDisplayNameForStorage(displayName, newName);
        settings.Category = NormalizeCategoryForStorage(category, project.DefaultCategory);
        var newKey = ProjectItem.KeyFor(projectType, newName);
        _settings.Projects.Remove(project.Key);
        _settings.Projects[newKey] = settings;

        if (projectType == ProjectType.Map)
            UpdateQuickLaunchMapReferences(oldName, newName);

        UpdateRenamedProjectHistory(project.Key, newKey, oldName, newName, projectType);
        MoveBuildLog(project.Key, newKey);
        QueueSettingsSave();

        await PopulateFileListAsync();
        SelectProjectByName(newName, projectType);

        if (referenceFailure is not null)
            throw new PartialRenameException(oldName, newName, targetFolder, referenceFailure);

        return targetFolder;
    }

    /// <summary>The folder moved, but some files inside still use the old name. <see cref="Exception.InnerException"/> says why.</summary>
    public sealed class PartialRenameException(string oldName, string newName, string folder, Exception inner)
        : Exception($"Renamed {oldName} to {newName} in {folder}, but some files inside still use the old name.", inner);

    private static ProjectSettings CloneProjectSettings(ProjectSettings source) => new()
    {
        DisplayName = source.DisplayName,
        Category = source.Category,
        IsFavorite = source.IsFavorite,
        Notes = source.Notes,
        LaunchConfigIndex = source.LaunchConfigIndex,
        EnvironmentDvars = source.EnvironmentDvars.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, string>(pair.Value),
            StringComparer.OrdinalIgnoreCase),
        EnvironmentBuildLanguages = new Dictionary<string, string>(source.EnvironmentBuildLanguages, StringComparer.OrdinalIgnoreCase),
        IsCompileChecked = source.IsCompileChecked,
        CompileModeIndex = source.CompileModeIndex,
        IsLightChecked = source.IsLightChecked,
        LightQualityIndex = source.LightQualityIndex,
        IsLinkChecked = source.IsLinkChecked,
        BuildPresets = [.. source.BuildPresets.Select(CloneBuildPreset)],
        BuildModeIndex = source.BuildModeIndex,
        CheckedZones = source.CheckedZones is null ? null : [.. source.CheckedZones],
        RunOptions = source.RunOptions,
        QuickLaunchMap = source.QuickLaunchMap,
        LaunchOnline = source.LaunchOnline,
    };

    private static void CopyDirectoryWithProjectRename(
        string sourceFolder,
        string targetFolder,
        ProjectTokenReplacer tokens)
    {
        Directory.CreateDirectory(targetFolder);

        foreach (var folder in Directory.EnumerateDirectories(sourceFolder))
        {
            if (ShouldSkipDuplicateFolder(folder))
                continue;

            var targetChild = Path.Combine(targetFolder, tokens.Replace(Path.GetFileName(folder)));
            CopyDirectoryWithProjectRename(folder, targetChild, tokens);
        }

        foreach (var file in Directory.EnumerateFiles(sourceFolder))
        {
            if (ShouldSkipDuplicateFile(file))
                continue;

            var targetFile = Path.Combine(targetFolder, tokens.Replace(Path.GetFileName(file)));
            CopyFileWithProjectRename(file, targetFile, tokens);
        }
    }

    private string MapSourcePath(string projectName) => ProjectNameRules.MapSourcePath(_fileSystem.GamePath, projectName);

    /// <summary>
    /// Why a map's source can't follow it to <paramref name="newName"/>: another map's .map already sits
    /// where it would go. Null when there's nothing to move or the way is clear.
    /// </summary>
    public string? MapSourceConflict(ProjectType type, string currentName, string newName)
    {
        newName = newName.Trim();
        if (type != ProjectType.Map || newName.Length == 0 || string.IsNullOrEmpty(_fileSystem.GamePath))
            return null;

        var source = MapSourcePath(currentName);
        var target = MapSourcePath(newName);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || !File.Exists(source) || !File.Exists(target))
            return null;

        return $"map_source\\{ProjectNameRules.MapPrefixFolder(newName)}\\{newName}.map already exists. Move or rename that file first.";
    }

    /// <summary>Copies the map source under its new name; returns the file it created, or null when there was none.</summary>
    private string? CopyMapSourceWithProjectRename(string sourceName, string targetName, ProjectTokenReplacer tokens)
    {
        var sourceFile = MapSourcePath(sourceName);
        if (!File.Exists(sourceFile))
            return null;

        var targetFile = MapSourcePath(targetName);
        CopyFileWithProjectRename(sourceFile, targetFile, tokens);
        return targetFile;
    }

    private void ResetCopiedWorkshopIdentity(
        string targetFolder,
        ProjectType projectType,
        string targetName,
        string displayName)
    {
        // The copy never takes the Workshop versions file (see ShouldSkipDuplicateFile), so its only
        // Workshop identity is workshop.json: cleared here so publishing it makes a new item.
        var zoneFolder = Path.Combine(targetFolder, "zone");
        if (!Directory.Exists(zoneFolder))
            return;

        var data = _fileSystem.ReadWorkshopJson(zoneFolder);
        if (data is null)
        {
            // Unreadable (corrupt or locked in the original): it may carry the original's ID. The copy
            // is a file this duplicate just made, so removing it loses nothing.
            var copied = Path.Combine(zoneFolder, WorkshopFiles.WorkshopJsonName);
            if (File.Exists(copied))
                File.Delete(copied);
            return;
        }

        data.PublisherId = "";
        data.FolderName = targetName;
        data.Type = projectType == ProjectType.Map ? "map" : "mod";
        if (!string.IsNullOrWhiteSpace(displayName))
            data.Title = displayName;
        _fileSystem.WriteWorkshopJson(zoneFolder, data);
    }

    private void UpdateRenamedWorkshopIdentity(
        string targetFolder,
        ProjectType projectType,
        string targetName,
        string displayName)
    {
        var zoneFolder = Path.Combine(targetFolder, "zone");
        if (!Directory.Exists(zoneFolder))
            return;

        var currentData = _fileSystem.ReadWorkshopJson(zoneFolder);
        var profiles = _fileSystem.ReadWorkshopProfiles(zoneFolder, currentData);
        var type = projectType == ProjectType.Map ? "map" : "mod";

        foreach (var profile in profiles.Profiles)
        {
            profile.WorkshopJson.FolderName = targetName;
            profile.WorkshopJson.Type = type;
            if (!string.IsNullOrWhiteSpace(displayName))
                profile.WorkshopJson.Title = displayName;
        }

        var active = profiles.GetActiveProfile().WorkshopJson.Clone();
        _fileSystem.WriteWorkshopProfiles(zoneFolder, profiles);
        _fileSystem.WriteWorkshopJson(zoneFolder, active);
    }

    private static void RenameProjectReferencesInPlace(string projectFolder, ProjectTokenReplacer tokens)
    {
        var entries = new DirectoryInfo(projectFolder).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).ToList();
        var files = entries.OfType<FileInfo>().Select(file => file.FullName).ToList();
        var folders = entries.OfType<DirectoryInfo>().Select(folder => folder.FullName).ToList();

        foreach (var file in files)
        {
            if (!IsTextLikeFile(file))
                continue;

            var (text, encoding) = TextFiles.Read(file);
            var replaced = tokens.Replace(text);
            if (!string.Equals(text, replaced, StringComparison.Ordinal))
                TextFiles.Write(file, replaced, encoding);
        }

        // Deepest first: renaming an entry only changes the paths below it, which are already done.
        foreach (var file in files.OrderByDescending(path => path.Length))
            MovePath(file, Path.Combine(Path.GetDirectoryName(file)!, tokens.Replace(Path.GetFileName(file))), isDirectory: false);

        foreach (var folder in folders.OrderByDescending(path => path.Length))
            MovePath(folder, Path.Combine(Path.GetDirectoryName(folder)!, tokens.Replace(Path.GetFileName(folder))), isDirectory: true);
    }

    /// <summary>Moves a file or folder; a case-only change goes through a temporary name, since Windows sees one entry.</summary>
    private static void MovePath(string source, string target, bool isDirectory)
    {
        if (string.Equals(source, target, StringComparison.Ordinal))
            return;

        void Move(string from, string to)
        {
            if (isDirectory)
                Directory.Move(from, to);
            else
                File.Move(from, to);
        }

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            var temp = Path.Combine(Path.GetDirectoryName(source)!, $"{Path.GetFileName(source)}.{Guid.NewGuid():N}.renaming");
            Move(source, temp);
            Move(temp, target);
            return;
        }

        Move(source, target);
    }

    private void RenameMapSourceInPlace(string sourceName, string targetName, ProjectTokenReplacer tokens)
    {
        var sourceFile = MapSourcePath(sourceName);
        if (!File.Exists(sourceFile))
            return;

        var targetFile = MapSourcePath(targetName);
        if (string.Equals(sourceFile, targetFile, StringComparison.OrdinalIgnoreCase))
        {
            var (text, encoding) = TextFiles.Read(sourceFile);
            var replaced = tokens.Replace(text);
            if (!string.Equals(text, replaced, StringComparison.Ordinal))
                TextFiles.Write(sourceFile, replaced, encoding);
            MovePath(sourceFile, targetFile, isDirectory: false);
            return;
        }

        CopyFileWithProjectRename(sourceFile, targetFile, tokens);
        File.Delete(sourceFile);

        var sourceFolder = Path.GetDirectoryName(sourceFile);
        if (!string.IsNullOrWhiteSpace(sourceFolder)
            && Directory.Exists(sourceFolder)
            && !Directory.EnumerateFileSystemEntries(sourceFolder).Any())
        {
            Directory.Delete(sourceFolder);
        }
    }

    private void UpdateQuickLaunchMapReferences(string oldName, string newName)
    {
        foreach (var settings in _settings.Projects.Values)
        {
            if (string.Equals(settings.QuickLaunchMap, oldName, StringComparison.OrdinalIgnoreCase))
                settings.QuickLaunchMap = newName;
        }
    }

    private void UpdateRenamedProjectHistory(string oldKey, string newKey, string oldName, string newName, ProjectType type)
    {
        if (string.Equals(_settings.LastActiveProject, oldName, StringComparison.OrdinalIgnoreCase))
        {
            _settings.LastActiveProject = newName;
            _settings.LastActiveProjectType = type == ProjectType.Map ? "map" : "mod";
        }

        for (var i = 0; i < _settings.RecentProjects.Count; i++)
        {
            if (string.Equals(_settings.RecentProjects[i], oldKey, StringComparison.OrdinalIgnoreCase))
                _settings.RecentProjects[i] = newKey;
        }
    }

    private void MoveBuildLog(string oldKey, string newKey)
    {
        var oldLog = _settings.GetBuildLogPath(oldKey);
        var newLog = _settings.GetBuildLogPath(newKey);
        _ = QueueLogFileWork(() =>
        {
            try
            {
                if (File.Exists(oldLog) && !File.Exists(newLog))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newLog)!);
                    File.Move(oldLog, newLog);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: the project rename itself is more important than log housekeeping.
            }

            return true;
        });
    }

    /// <summary>Copies a file under its new name, never over an existing one; text keeps its encoding.</summary>
    private static void CopyFileWithProjectRename(string sourceFile, string targetFile, ProjectTokenReplacer tokens)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);

        if (!IsTextLikeFile(sourceFile))
        {
            File.Copy(sourceFile, targetFile);
            return;
        }

        var (text, encoding) = TextFiles.Read(sourceFile);
        TextFiles.Write(targetFile, tokens.Replace(text), encoding, FileMode.CreateNew);
    }

    private static bool ShouldSkipDuplicateFolder(string folderPath) =>
        string.Equals(Path.GetFileName(folderPath), WorkshopFiles.MediaFolderName, StringComparison.OrdinalIgnoreCase);

    // Generated output, logs, and the Workshop versions file: a copy that kept the original's
    // Workshop item IDs would publish over the original.
    private static bool ShouldSkipDuplicateFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return extension.Equals(".xpak", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
               || string.Equals(Path.GetFileName(filePath), WorkshopFiles.ProfilesName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextLikeFile(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ""
            or ".arena"
            or ".bat"
            or ".cfg"
            or ".csv"
            or ".csc"
            or ".gdt"
            or ".gsc"
            or ".ini"
            or ".json"
            or ".lua"
            or ".map"
            or ".menu"
            or ".script"
            or ".str"
            or ".szc"
            or ".txt"
            or ".xml"
            or ".zone";
    }

    /// <summary>
    /// Replaces the project name only where it stands on its own, so renaming
    /// zm_test touches zm_test.gsc and zm_test_fx but not zm_testing_util.
    /// Underscores and punctuation count as boundaries; letters and digits don't.
    /// An all-caps occurrence (a #define, say) stays all-caps. One per rename or duplicate.
    /// </summary>
    internal sealed class ProjectTokenReplacer(string sourceName, string targetName)
    {
        private readonly Regex _pattern = new(
            $"(?<![A-Za-z0-9]){Regex.Escape(sourceName)}(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly string _upperTarget = targetName.ToUpperInvariant();
        private readonly bool _targetIsUpper = IsAllUpper(targetName);

        public string Replace(string value) =>
            _pattern.Replace(value, match => IsAllUpper(match.Value) && !_targetIsUpper ? _upperTarget : targetName);
    }

    private static bool IsAllUpper(string value) =>
        value.Any(char.IsLetter) && !value.Any(char.IsLower);


    private void RestoreLastProject()
    {
        if (string.IsNullOrEmpty(_settings.LastActiveProject))
        {
            SelectedProject = null;
            return;
        }

        var lastType = _settings.LastActiveProjectType;
        SelectedProject = _allProjects.FirstOrDefault(p =>
            p.Name == _settings.LastActiveProject
            && (string.IsNullOrWhiteSpace(lastType)
                || (lastType == "map" && p.Type == ProjectType.Map)
                || (lastType == "mod" && p.Type == ProjectType.Mod)));
    }

    [RelayCommand]
    private async Task BuildOrCancel()
    {
        if (IsBuildRunning)
        {
            if (IsBuildCancelling)
                return;

            IsBuildCancelling = true;
            BuildButtonText = "Cancelling\u2026";
            StatusText = "Cancelling the build\u2026";
            UpdateBuildActivityText();
            await RequestBuildCancellationAsync();
            return;
        }

        if (!CanStartBuild)
        {
            StatusText = BuildBlockedReason;
            return;
        }

        // Determine run/ignore behavior from current build mode
        bool runAfter = BuildModeIndex is 0 or 2; // Build & Run, or Build & Run (Ignore Errors)
        bool ignoreErr = BuildModeIndex == 2; // Build & Run (Ignore Errors)
        _ = RunBuildAsync(runAfter, ignoreErr);
    }

    /// <summary>Ctrl+B and the palette: build without launching, whatever the build mode.</summary>
    [RelayCommand]
    private void BuildOnlyOnce()
    {
        if (!CanStartBuildOnly)
        {
            StatusText = IsBuildRunning
                ? "A build is already running. Use Cancel to stop it."
                : BuildBlockedReason is { Length: > 0 } reason ? reason : StepsValidationMessage();
            return;
        }

        _ = RunBuildAsync(runAfterBuild: false, ignoreErrors: false);
    }

    [RelayCommand]
    private void BuildAndRunOnce()
    {
        if (!CanStartBuildAndRun)
        {
            StatusText = BuildAndRunBlockedReason();
            return;
        }

        _ = RunBuildAsync(runAfterBuild: true, ignoreErrors: false);
    }

    [RelayCommand]
    private void BuildAndRunIgnoreErrorsOnce()
    {
        if (!CanStartBuildAndRun)
        {
            StatusText = BuildAndRunBlockedReason();
            return;
        }

        _ = RunBuildAsync(runAfterBuild: true, ignoreErrors: true);
    }

    private string BuildAndRunBlockedReason() =>
        IsBuildRunning ? "A build is already running. Use Cancel to stop it."
        : !CanLaunchSelectedProject ? (LaunchOnline ? "Steam.exe wasn't found." : "BlackOps3.exe wasn't found.")
        : BuildBlockedReason is { Length: > 0 } reason ? reason
        : StepsValidationMessage();

    /// <summary>
    /// F5: whatever the Build button's mode says to do with the game. Build & run
    /// modes build then launch; Build mode just runs the game.
    /// </summary>
    [RelayCommand]
    private async Task RunForMode()
    {
        if (BuildModeIndex == 1)
        {
            await RunProject();
            return;
        }

        if (IsBuildRunning)
        {
            StatusText = "A build is already running. Use Cancel to stop it.";
            return;
        }

        await BuildOrCancel();
    }

    private Task RequestBuildCancellationAsync()
    {
        var cts = _buildCts;
        if (cts is null)
            return Task.CompletedTask;

        if (_buildCancelTask is not null)
            return _buildCancelTask;

        _buildCancelTask = Task.Run(() =>
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                CrashLog.Write("Cancelling the build", ex);
            }
        });

        return _buildCancelTask;
    }

    [RelayCommand]
    private void SetBuildMode(string mode)
    {
        BuildModeIndex = int.TryParse(mode, out var i) ? i : 0;
    }

    [RelayCommand]
    private void SetCompileMode(string mode)
    {
        if (int.TryParse(mode, out var i) && i >= 0 && i < CompileModes.Length)
            CompileModeIndex = i;
    }

    [RelayCommand]
    private void SetLightQuality(string quality)
    {
        if (int.TryParse(quality, out var i) && i >= 0 && i < LightQualities.Length)
            LightQualityIndex = i;
    }

    private async Task RunBuildAsync(bool runAfterBuild, bool ignoreErrors)
    {
        var commands = BuildCommandList(forceRun: runAfterBuild);

        // The game launch rides at the end of the list; it starts on its own once the build is done.
        BuildCommand? runGameCommand = null;
        if (commands.Count > 0 && commands[^1].LaunchesGame)
        {
            runGameCommand = commands[^1];
            commands.RemoveAt(commands.Count - 1);
        }

        if (commands.Count == 0 && runGameCommand is null)
        {
            StatusText = "Select a project and at least one build step.";
            return;
        }

        // Build & Run with no steps ticked is just a run: leave the last build's
        // log and result alone instead of recording an empty "successful" build.
        if (commands.Count == 0)
        {
            if (IsGameRunning)
                StatusText = "No build steps selected, and the game is already running.";
            else
                LaunchGameCommand(runGameCommand!);
            return;
        }

        var buildProject = SelectedProject;

        IsBuildRunning = true;
        IsBuildCancelling = false;
        BuildButtonText = "Cancel";
        ResetOutputText();
        ErrorCount = 0;
        LastBuildOutcome = BuildOutcome.None;
        WarningCount = 0;
        StatusText = "Building\u2026";
        BuildTimestamp = "";
        BuildDurationText = "";
        _buildCts = new CancellationTokenSource();
        var buildToken = _buildCts.Token;
        _buildCancelTask = null;
        var buildTimer = Stopwatch.StartNew();
        StartBuildActivityTimer();

        var progress = new Progress<string>(text =>
        {
            AppendOutputText(text);
            CountDiagnostics(text);
        });

        string buildStatus;
        try
        {
            var success = await _buildService.RunBuildAsync(commands, ignoreErrors, progress, buildToken);
            buildToken.ThrowIfCancellationRequested();

            // The mod tools sometimes exit 0 despite printing errors, so a clean
            // exit code alone isn't "succeeded" — check the diagnostic count too.
            if (success && ErrorCount == 0)
            {
                AppendOutputText("\nBuild completed successfully.\n");
                buildStatus = "succeeded";
            }
            else if (success)
            {
                AppendOutputText("\nBuild completed, but errors were reported in the log.\n");
                buildStatus = "completed with errors";
            }
            else if (ignoreErrors)
            {
                AppendOutputText("\nBuild finished, but at least one step failed.\n");
                buildStatus = "completed with errors";
            }
            else
            {
                AppendOutputText("\nBuild failed.\n");
                buildStatus = "failed";
            }
        }
        catch (OperationCanceledException)
        {
            AppendOutputText("\nBuild cancelled.\n");
            buildStatus = "cancelled";
        }
        catch (Exception ex)
        {
            // A build tool that fails to start (missing exe, access denied, ...)
            // must still land in a terminal status — otherwise the UI is left
            // saying "Building..." with the exception unobserved.
            AppendOutputText($"\nBuild failed to run: {ex.Message}\n");
            buildStatus = "failed";
        }
        finally
        {
            buildTimer.Stop();
            if (_buildCancelTask is not null)
            {
                await _buildCancelTask;
                _buildCancelTask = null;
            }

            StopBuildActivityTimer();
            IsBuildRunning = false;
            IsBuildCancelling = false;
            BuildButtonText = BuildModeLabel;
            _buildCts?.Dispose();
            _buildCts = null;
        }

        // Format status and timestamp separately
        var now = DateTime.Now;
        ApplyBuildStatus(buildStatus);
        BuildTimestamp = now.ToString("d MMM yyyy 'at' HH:mm");
        BuildDurationText = FormatDuration(buildTimer.Elapsed);

        // Launch game after build if applicable. Reported errors block the
        // launch unless the user picked "Ignore Errors", which exists for that.
        if (runGameCommand is not null && buildStatus == "completed with errors" && !ignoreErrors)
        {
            AppendOutputText("\nGame not launched because the build reported errors. Use Build & run (ignore errors) to launch anyway.\n");
        }
        else if (runGameCommand is not null && buildStatus is "succeeded" or "completed with errors")
        {
            // Read now, not when the build started: the game may have been closed (or started) meanwhile.
            if (IsGameRunning)
                AppendOutputText("\nGame is already running; skipped launch step.\n");
            else
                LaunchGameCommand(runGameCommand, writeToLog: true);
        }

        // Persist build results against the project the build started on
        if (buildProject is { IsGroupHeader: false })
        {
            var ps = GetOrCreateProjectSettings(buildProject.Key);
            ps.LastBuildStatus = buildStatus;
            ps.LastBuildErrorCount = ErrorCount;
            ps.LastBuildWarningCount = WarningCount;
            ps.LastBuildTimestamp = now;
            ps.LastBuildDurationMs = (long)buildTimer.Elapsed.TotalMilliseconds;
            // Any normal build invalidates a previous "Prepare for publish" pass.
            ps.LastPublishPrepTimestamp = null;
            QueueSettingsSave();
            SaveBuildLog(buildProject.Key, Output.Snapshot());
            InvalidateProjectSize(buildProject);
        }
    }

    /// <summary>Shows a stored build status as the outcome and its status-bar line.</summary>
    private void ApplyBuildStatus(string? status)
    {
        var (outcome, label) = DescribeBuildStatus(status);
        _outcomeStatusText = label;
        LastBuildOutcome = outcome;
        StatusText = label;
        OnPropertyChanged(nameof(StatusKind));
    }

    /// <summary>Maps the status stored with a build result to its outcome and status-bar label.</summary>
    private static (BuildOutcome Outcome, string Label) DescribeBuildStatus(string? status) => status switch
    {
        "succeeded" => (BuildOutcome.Succeeded, "Build succeeded"),
        "completed with errors" => (BuildOutcome.CompletedWithErrors, "Build completed with errors"),
        "failed" => (BuildOutcome.Failed, "Build failed"),
        "cancelled" => (BuildOutcome.Cancelled, "Build cancelled"),
        "publish prep succeeded" => (BuildOutcome.Succeeded, "Prepare for publish complete"),
        "publish prep failed" => (BuildOutcome.Failed, "Prepare for publish failed"),
        "publish prep cancelled" => (BuildOutcome.Cancelled, "Prepare for publish cancelled"),
        _ => (BuildOutcome.None, "Ready"),
    };

    /// <summary>
    /// Counts ^1 (error) and ^3 (warning) lines in a chunk of build output. A line split
    /// across chunks is held back until it completes, and each count changes once per chunk.
    /// </summary>
    private void CountDiagnostics(string chunk)
    {
        var text = _diagnosticCarry.Length == 0 ? chunk : _diagnosticCarry + chunk;
        var lastBreak = text.LastIndexOf('\n');
        if (lastBreak < 0)
        {
            _diagnosticCarry = text;
            return;
        }

        _diagnosticCarry = text[(lastBreak + 1)..];

        int errors = 0, warnings = 0;
        foreach (var line in text.AsSpan(0, lastBreak).EnumerateLines())
        {
            if (BuildLogCodes.IsError(line))
                errors++;
            else if (BuildLogCodes.IsWarning(line))
                warnings++;
        }

        if (errors > 0)
            ErrorCount += errors;
        if (warnings > 0)
            WarningCount += warnings;
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";

        return $"{Math.Max(1, (int)Math.Round(elapsed.TotalSeconds))}s";
    }

    private List<BuildCommand> BuildCommandList(
        string? forcedBuildLanguage = null,
        bool forceRun = true,
        bool linkOnly = false,
        bool linkAllModZones = false)
    {
        if (SelectedProject is null || SelectedProject.IsGroupHeader)
            return [];

        var commands = new List<BuildCommand>();
        bool updateAdded = false;
        var shouldRun = forceRun;

        void AddUpdateDb()
        {
            if (updateAdded) return;
            commands.Add(new BuildCommand(
                Path.Combine(_fileSystem.ToolsPath, "gdtdb", "gdtdb.exe"),
                ["/update"], "Update GDT Database"));
            updateAdded = true;
        }

        var languageArgs = new List<string>();
        var buildLanguage = forcedBuildLanguage ?? GetEffectiveBuildLanguage();
        if (buildLanguage != "All")
        {
            languageArgs.AddRange(["-language", buildLanguage]);
        }
        else
        {
            foreach (var lang in Languages)
                languageArgs.AddRange(["-language", lang]);
        }

        if (SelectedProject.Type == ProjectType.Map)
        {
            var mapName = SelectedProject.Name;
            var prefix = ProjectNameRules.MapPrefixFolder(mapName);

            if (!linkOnly && IsCompileChecked)
            {
                AddUpdateDb();
                var args = new List<string> { "-platform", "pc" };
                if (CompileModeIndex == 0)
                    args.Add("-onlyents");
                else
                    args.AddRange(["-navmesh", "-navvolume"]);

                args.AddRange([
                    "-loadFrom",
                    MapSourcePath(mapName),
                    Path.Combine(_fileSystem.GamePath, "share", "raw", "maps", prefix, $"{mapName}.d3dbsp")
                ]);

                commands.Add(new BuildCommand(
                    Path.Combine(_fileSystem.ToolsPath, "bin", "cod2map64.exe"), args, $"Compile {mapName}"));
            }

            if (!linkOnly && IsLightChecked)
            {
                AddUpdateDb();
                var args = new List<string> { "-ledSilent" };
                args.Add(LightQualityIndex switch
                {
                    0 => "+low",
                    2 => "+high",
                    _ => "+medium"
                });
                args.AddRange(["+localprobes", "+forceclean", "+recompute", MapSourcePath(mapName)]);

                commands.Add(new BuildCommand(
                    Path.Combine(_fileSystem.ToolsPath, "bin", "radiant_modtools.exe"), args, $"Light {mapName}"));
            }

            if (linkOnly || IsLinkChecked)
            {
                AddUpdateDb();
                var args = new List<string>(languageArgs) { "-modsource", mapName };

                commands.Add(new BuildCommand(
                    Path.Combine(_fileSystem.ToolsPath, "bin", "linker_modtools.exe"), args, $"Link {mapName}"));
            }

            // Run game with this map
            if (shouldRun)
            {
                var args = new List<string>();
                if (_runDvars.Count > 0)
                    args.AddRange(_runDvars);
                args.AddRange(["+set", "fs_game", mapName, "+devmap", mapName]);
                AddRunOptionsArgs(args);

                commands.Add(BuildGameLaunchCommand(args));
            }
        }
        else // Mod
        {
            var modName = SelectedProject.Name;

            if (linkOnly || IsLinkChecked)
            {
                var zones = linkAllModZones
                    ? SelectedProject.ZoneFiles
                    : SelectedProject.ZoneFiles.Where(z => z.IsChecked);

                foreach (var zone in zones)
                {
                    AddUpdateDb();
                    var args = new List<string>(languageArgs)
                    {
                        "-fs_game", zone.ModName, "-modsource", zone.ZoneName
                    };

                    commands.Add(new BuildCommand(
                        Path.Combine(_fileSystem.ToolsPath, "bin", "linker_modtools.exe"),
                        args, $"Link {zone.ModName}/{zone.ZoneName}"));
                }
            }

            // Run game with this mod
            if (shouldRun)
            {
                var args = new List<string>();
                if (_runDvars.Count > 0)
                    args.AddRange(_runDvars);
                args.AddRange(["+set", "fs_game", modName]);
                AddQuickLaunchMapArgs(args);
                AddRunOptionsArgs(args);

                commands.Add(BuildGameLaunchCommand(args));
            }
        }

        return commands;
    }

    private BuildCommand BuildGameLaunchCommand(List<string> gameArguments)
    {
        if (LaunchOnline)
        {
            var steamExe = _steamExecutablePath ?? "steam.exe";
            var args = new List<string> { "-applaunch", BlackOps3SteamAppId };
            args.AddRange(gameArguments);
            return new BuildCommand(steamExe, args, "Run game") { LaunchesGame = true };
        }

        return new BuildCommand(
            Path.Combine(_fileSystem.GamePath, "BlackOps3.exe"),
            gameArguments,
            "Run game") { LaunchesGame = true };

    }

    private bool HasValidBuildRequest()
    {
        if (!HasSelectedProject)
            return false;

        if (HasSetupIssues)
            return false;

        var wantsRun = BuildModeIndex != 1;
        if (wantsRun && !CanLaunchSelectedProject)
            return false;

        if (wantsRun)
            return true;

        return HasBuildStepsSelected();
    }

    private bool HasBuildStepsSelected()
    {
        if (IsMapSelected)
            return IsCompileChecked || IsLightChecked || IsLinkChecked;

        if (IsModSelected)
            return IsLinkChecked && SelectedProject is not null && SelectedProject.ZoneFiles.Any(z => z.IsChecked);

        return false;
    }

    private void NotifyBuildStateChanged()
    {
        OnPropertyChanged(nameof(CanStartBuild));
        OnPropertyChanged(nameof(CanStartBuildOnly));
        OnPropertyChanged(nameof(CanStartBuildAndRun));
        OnPropertyChanged(nameof(CanModifyProject));
        OnPropertyChanged(nameof(ModifyProjectBlockedReason));
        OnPropertyChanged(nameof(CanSwitchProject));
        OnPropertyChanged(nameof(CanEditBuildOptions));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunToolTip));
        OnPropertyChanged(nameof(BuildValidationMessage));
        OnPropertyChanged(nameof(HasBuildValidationMessage));
        OnPropertyChanged(nameof(BuildBlockedReason));
        OnPropertyChanged(nameof(BuildToolTip));
        OnPropertyChanged(nameof(LinkStepToolTip));
        OnPropertyChanged(nameof(BuildPresetLabel));
        OnPropertyChanged(nameof(CanClearLog));
        ClearLogCommand.NotifyCanExecuteChanged();
    }

    private void NotifyToolHealthChanged()
    {
        OnPropertyChanged(nameof(HasToolHealthIssues));
        OnPropertyChanged(nameof(ToolHealthMessage));
        OnPropertyChanged(nameof(AssetEditorToolTip));
        OnPropertyChanged(nameof(RadiantToolTip));
    }

    private ProjectCapabilities CurrentCapabilities =>
        SelectedProject is { IsGroupHeader: false } project && _capabilities.ProjectKey == project.Key
            ? _capabilities
            : ProjectCapabilities.None;

    /// <summary>
    /// Re-reads the selected project's folders and Workshop files in the background.
    /// Call on selection, rescan, publish, or when the watcher sees scripts/ui change.
    /// </summary>
    public async Task RefreshProjectCapabilitiesAsync()
    {
        var version = ++_capabilityVersion;
        if (SelectedProject is not { IsGroupHeader: false } project)
        {
            _capabilities = ProjectCapabilities.None;
            NotifyProjectCapabilityChanged();
            return;
        }

        var key = project.Key;
        var folder = project.FolderPath;
        var zoneFile = GetSelectedZoneFilePath();
        var mapSourceFile = GetSelectedMapSourceFilePath();
        var szcFile = GetSelectedSzcFilePath();
        ProjectCapabilities capabilities;
        try
        {
            capabilities = await Task.Run(() => new ProjectCapabilities(key)
            {
                HasScriptsFolder = Directory.Exists(Path.Combine(folder, "scripts")),
                HasUiFolder = Directory.Exists(Path.Combine(folder, "ui")),
                HasZoneSourceFolder = Directory.Exists(Path.Combine(folder, "zone_source")),
                HasZoneFile = zoneFile is not null && File.Exists(zoneFile),
                HasMapSourceFile = mapSourceFile is not null && File.Exists(mapSourceFile),
                HasMapSourceFolder = mapSourceFile is not null && Directory.Exists(Path.GetDirectoryName(mapSourceFile)),
                HasSzcFile = szcFile is not null && File.Exists(szcFile),
                HasContentFolder = Directory.Exists(Path.Combine(folder, "zone")),
                HasWorkshopJson = File.Exists(Path.Combine(folder, "zone", "workshop.json")),
                WorkshopFileId = ReadWorkshopFileId(folder),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            capabilities = new ProjectCapabilities(key);
        }

        if (version != _capabilityVersion)
            return;

        _capabilities = capabilities;
        NotifyProjectCapabilityChanged();
    }

    private static string ValueAtOrFirst(string[] values, int index)
    {
        if (values.Length == 0)
            return "";

        return index >= 0 && index < values.Length ? values[index] : values[0];
    }

    private void NotifyProjectCapabilityChanged()
    {
        OnPropertyChanged(nameof(HasScriptsFolder));
        OnPropertyChanged(nameof(HasUiFolder));
        OnPropertyChanged(nameof(HasZoneSourceFolder));
        OnPropertyChanged(nameof(HasZoneFile));
        OnPropertyChanged(nameof(HasMapSourceFile));
        OnPropertyChanged(nameof(HasMapSourceFolder));
        OnPropertyChanged(nameof(HasSzcFile));
        OnPropertyChanged(nameof(HasContentFolder));
        OnPropertyChanged(nameof(HasWorkshopJson));
        OnPropertyChanged(nameof(ScriptsInVsCodeBlockedReason));
        OnPropertyChanged(nameof(LuiInVsCodeBlockedReason));
        OnPropertyChanged(nameof(CanOpenScriptsInVsCode));
        OnPropertyChanged(nameof(CanOpenLuiInVsCode));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunToolTip));
        OnPropertyChanged(nameof(CanOpenWorkshopPage));
        OnPropertyChanged(nameof(CanCopyRunCommand));
    }

    [RelayCommand(CanExecute = nameof(IsAssetEditorAvailable))]
    private void OpenAssetEditor()
    {
        var exe = Path.Combine(_fileSystem.ToolsPath, "bin", "AssetEditor_modtools.exe");
        if (!File.Exists(exe))
        {
            StatusText = "Asset Editor wasn't found. Open Setup Doctor to check Mod Tools.";
            UpdateToolAvailability();
            return;
        }

        StartOrReport(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)
        }, "Asset Editor");
    }

    /// <summary>Starts a tool or opens a file; a failure lands in the status line.</summary>
    private void StartOrReport(ProcessStartInfo startInfo, string label)
    {
        if (ShellLauncher.TryStart(startInfo) is { } error)
            StatusText = $"Couldn't open {label}: {error}";
    }

    [RelayCommand(CanExecute = nameof(IsRadiantAvailable))]
    private void OpenRadiant()
    {
        var exe = Path.Combine(_fileSystem.ToolsPath, "bin", "radiant_modtools.exe");
        if (!File.Exists(exe))
        {
            StatusText = "Radiant wasn't found. Open Setup Doctor to check Mod Tools.";
            UpdateToolAvailability();
            return;
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)
        };

        if (SelectedProject is { Type: ProjectType.Map })
        {
            var mapSourceFile = GetSelectedMapSourceFilePath();
            if (mapSourceFile is not null)
                psi.Arguments = $"\"{mapSourceFile}\"";
        }

        StartOrReport(psi, "Radiant");
    }

    [RelayCommand(CanExecute = nameof(IsApexAvailable))]
    private void OpenApex()
    {
        var exe = Path.Combine(_fileSystem.ToolsPath, "bin", "Apex.exe");
        if (!File.Exists(exe))
        {
            StatusText = "Apex isn't in the Mod Tools' bin folder any more.";
            UpdateToolAvailability();
            return;
        }

        StartOrReport(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)
        }, "Apex");
    }

    [RelayCommand]
    private void OpenGsCodeLibrary()
    {
        OpenExternalUrl(GsCodeLibraryUrl, "GSCode Library");
    }

    [RelayCommand]
    private void OpenBo3SourceExplorer()
    {
        OpenExternalUrl(Bo3SourceExplorerUrl, "BO3 Source Explorer");
    }

    private void OpenExternalUrl(string url, string label)
    {
        if (ShellLauncher.TryOpen(url) is { } error)
            StatusText = $"Couldn't open {label}: {error}";
    }

    [RelayCommand]
    private void LaunchCustomTool(ToolShortcutViewModel? tool)
    {
        if (tool is null)
            return;

        if (!File.Exists(tool.ExePath))
        {
            StatusText = $"{tool.Name} isn't at {tool.ExePath} any more. Right-click it to remove it.";
            return;
        }

        var psi = new ProcessStartInfo(tool.ExePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(tool.ExePath)
        };
        if (!string.IsNullOrWhiteSpace(tool.Arguments))
            psi.Arguments = tool.Arguments;
        StartOrReport(psi, tool.Name);
    }

#pragma warning disable CA1416 // Windows-only app
    [RelayCommand]
    private void RemoveCustomTool(ToolShortcutViewModel? tool)
    {
        if (tool is null) return;

        CustomTools.Remove(tool);

        // Delete cached icon
        var shortcut = _settings.ToolShortcuts.FirstOrDefault(s => s.ExePath == tool.ExePath);
        if (shortcut is not null)
        {
            Services.IconExtractorService.DeleteCachedIcon(Services.IconExtractorService.GetCachePath(shortcut.ExePath));
            _settings.ToolShortcuts.Remove(shortcut);
        }

        tool.Icon?.Dispose();
        QueueSettingsSave();
    }

    /// <summary>Renames a tool shortcut or changes its arguments, from its right-click menu.</summary>
    public void UpdateToolShortcut(ToolShortcutViewModel tool, string name, string arguments)
    {
        var shortcut = _settings.ToolShortcuts.FirstOrDefault(s => s.ExePath == tool.ExePath);
        if (shortcut is null)
            return;

        shortcut.Name = name;
        shortcut.Arguments = arguments;
        tool.Name = name;
        tool.Arguments = arguments;
        QueueSettingsSave();
    }

    /// <summary>Pins a program to the title bar, from the picker or a drop. A program already there says so instead.</summary>
    public void AddToolShortcut(string exePath, string? name = null, string arguments = "")
    {
        if (_settings.ToolShortcuts.Any(s => string.Equals(s.ExePath, exePath, StringComparison.OrdinalIgnoreCase)))
        {
            ShowTransientStatus($"{Path.GetFileNameWithoutExtension(exePath)} is already in the title bar.");
            return;
        }


        var shortcut = new Models.ToolShortcut
        {
            Name = name ?? Path.GetFileNameWithoutExtension(exePath),
            ExePath = exePath,
            Arguments = arguments,
        };

        _settings.ToolShortcuts.Add(shortcut);
        QueueSettingsSave();

        var tool = new ToolShortcutViewModel
        {
            Name = shortcut.Name,
            ExePath = shortcut.ExePath,
            Arguments = shortcut.Arguments,
        };
        CustomTools.Add(tool);
        _ = LoadToolIconsAsync([tool], onLoaded: () =>
        {
            shortcut.IconCachePath = Services.IconExtractorService.GetCachePath(exePath);
            QueueSettingsSave();
        });
    }

    private void LoadCustomTools()
    {
        CustomTools.Clear();
        foreach (var shortcut in _settings.ToolShortcuts)
        {
            CustomTools.Add(new ToolShortcutViewModel
            {
                Name = shortcut.Name,
                ExePath = shortcut.ExePath,
                Arguments = shortcut.Arguments,
            });
        }

        _ = LoadToolIconsAsync([.. CustomTools]);
    }

    /// <summary>Extracts or decodes toolbar icons off the UI thread; a tool removed meanwhile gets none.</summary>
    private async Task LoadToolIconsAsync(IReadOnlyList<ToolShortcutViewModel> tools, Action? onLoaded = null)
    {
        var paths = tools.Select(tool => tool.ExePath).ToArray();
        var icons = await Task.Run(() => paths.Select(Services.IconExtractorService.ExtractIcon).ToArray());

        for (var i = 0; i < tools.Count; i++)
        {
            if (CustomTools.Contains(tools[i]))
                tools[i].Icon = icons[i];
            else
                icons[i]?.Dispose();
        }

        onLoaded?.Invoke();
    }

    /// <summary>Re-checks tools, Steam and VS Code off the UI thread, e.g. after a launch finds something missing.</summary>
    private async void UpdateToolAvailability()
    {
        var version = ++_toolAvailabilityVersion;
        var tools = await Task.Run(() => BuildToolAvailability(loadIcons: false));
        if (version == _toolAvailabilityVersion)
            ApplyToolAvailability(tools, updateIcons: false);
    }

    private ToolAvailability BuildToolAvailability(bool loadIcons)
    {
        var toolsPath = _fileSystem.ToolsPath;
        var assetEditorExe = Path.Combine(toolsPath, "bin", "AssetEditor_modtools.exe");
        var radiantExe = Path.Combine(toolsPath, "bin", "radiant_modtools.exe");
        var apexExe = Path.Combine(toolsPath, "bin", "Apex.exe");
        var isApexAvailable = File.Exists(apexExe);
        var isAssetEditorAvailable = File.Exists(assetEditorExe);
        var isRadiantAvailable = File.Exists(radiantExe);

        return new ToolAvailability(
            isAssetEditorAvailable,
            isRadiantAvailable,
            isApexAvailable,
            File.Exists(Path.Combine(toolsPath, "bin", "export2rust.exe")),
            File.Exists(Path.Combine(_fileSystem.GamePath, "BlackOps3.exe")),
            ResolveSteamExecutablePath(),
            IsCommandAvailable("code"),
            toolsPath,
            _fileSystem.DetectGameLanguage(),
            loadIcons && isAssetEditorAvailable ? Services.IconExtractorService.ExtractIcon(assetEditorExe) : null,
            loadIcons && isRadiantAvailable ? Services.IconExtractorService.ExtractIcon(radiantExe) : null,
            loadIcons && isApexAvailable ? Services.IconExtractorService.ExtractIcon(apexExe) : null);
    }

    private void ApplyToolAvailability(ToolAvailability tools, bool updateIcons)
    {
        // A full rescan supersedes any quick re-check still in flight.
        _toolAvailabilityVersion++;
        _toolsChecked = true;
        _steamExecutablePath = tools.SteamExecutablePath;
        IsAssetEditorAvailable = tools.AssetEditorAvailable;
        IsRadiantAvailable = tools.RadiantAvailable;
        IsApexAvailable = tools.ApexAvailable;
        IsExport2RustAvailable = tools.Export2RustAvailable;
        IsGameExecutableAvailable = tools.GameExecutableAvailable;
        IsSteamAvailable = tools.SteamExecutablePath is not null;
        IsVsCodeAvailable = tools.VsCodeAvailable;
        if (_gameLanguage != tools.GameLanguage)
        {
            _gameLanguage = tools.GameLanguage;
            NotifyBuildLanguageLabelsChanged();
        }

        if (updateIcons || !tools.AssetEditorAvailable)
        {
            AssetEditorIcon?.Dispose();
            AssetEditorIcon = updateIcons ? tools.AssetEditorIcon : null;
        }

        if (updateIcons || !tools.RadiantAvailable)
        {
            RadiantIcon?.Dispose();
            RadiantIcon = updateIcons ? tools.RadiantIcon : null;
        }

        if (updateIcons || !tools.ApexAvailable)
        {
            ApexIcon?.Dispose();
            ApexIcon = updateIcons ? tools.ApexIcon : null;
        }

        if (updateIcons)
            _toolIconsPath = tools.ToolsPath;

        NotifyToolHealthChanged();
        NotifyProjectCapabilityChanged();

        NotifyBuildStateChanged();
    }

    private static void DisposeToolIcons(ToolAvailability tools)
    {
        tools.AssetEditorIcon?.Dispose();
        tools.RadiantIcon?.Dispose();
        tools.ApexIcon?.Dispose();
    }

    private static bool IsCommandAvailable(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [""];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? command + extension : command);
                if (File.Exists(candidate))
                    return true;
            }
        }

        return false;
    }

    private static string? ResolveSteamExecutablePath()
    {
        foreach (var candidate in GetSteamExecutableCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> GetSteamExecutableCandidates()
    {
        var registryPath = GetSteamPathFromRegistry();
        if (!string.IsNullOrWhiteSpace(registryPath))
            yield return Path.Combine(registryPath, "steam.exe");

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            yield return Path.Combine(programFilesX86, "Steam", "steam.exe");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return Path.Combine(programFiles, "Steam", "steam.exe");
    }

    private static string? GetSteamPathFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return (key?.GetValue("SteamExe") as string) is { Length: > 0 } steamExe
                ? Path.GetDirectoryName(steamExe.Replace('/', Path.DirectorySeparatorChar))
                : key?.GetValue("SteamPath") as string;
        }
        catch
        {
            return null;
        }
    }
#pragma warning restore CA1416

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(GetLogFolder());
        OpenDirectory(GetLogFolder(), "logs folder");
    }

    [RelayCommand]
    private void OpenGameRoot() => OpenDirectory(_fileSystem.GamePath, "game folder");

    [RelayCommand]
    private void OpenToolsRoot() => OpenDirectory(_fileSystem.ToolsPath, "Mod Tools folder");

    [RelayCommand]
    private void OpenZoneSourceFolder()
    {
        if (SelectedProject is { IsGroupHeader: false } project)
            OpenDirectory(Path.Combine(project.FolderPath, "zone_source"), "zone_source folder");
    }

    [RelayCommand]
    private void OpenMapSourceFolder()
    {
        if (Path.GetDirectoryName(GetSelectedMapSourceFilePath()) is { Length: > 0 } folder)
            OpenDirectory(folder, "map source folder");
    }

    [RelayCommand]
    private void OpenWorkshopContentFolder()
    {
        if (SelectedProject is { IsGroupHeader: false } project)
            OpenDirectory(Path.Combine(project.FolderPath, "zone"), "Workshop content folder");
    }

    [RelayCommand]
    private void EditWorkshopJsonFile()
    {
        if (SelectedProject is { IsGroupHeader: false } project)
            OpenFile(Path.Combine(project.FolderPath, "zone", "workshop.json"));
    }

    [RelayCommand]
    private void OpenWorkshopPage()
    {
        var url = GetSelectedProjectWorkshopUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText = "No Workshop ID is saved for the selected project.";
            return;
        }

        if (ShellLauncher.TryOpen(url) is { } error)
            StatusText = $"Couldn't open the Workshop page: {error}";
    }

    public string GetSelectedProjectWorkshopUrl()
    {
        if (SelectedProject is not { IsGroupHeader: false } project)
            return "";

        // The cache fills in the background after a switch; read directly if it hasn't yet.
        var fileId = _capabilities.ProjectKey == project.Key
            ? _capabilities.WorkshopFileId
            : ReadWorkshopFileId(project.FolderPath);
        return fileId != 0 ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={fileId}" : "";
    }

    /// <summary>The active profile's Workshop ID, else workshop.json's, else any published profile's; 0 when none.</summary>
    private ulong ReadWorkshopFileId(string projectFolder)
    {
        var workshopFolder = Path.Combine(projectFolder, "zone");
        var currentWorkshopJson = _fileSystem.ReadWorkshopJson(workshopFolder);
        var profilesData = _fileSystem.ReadWorkshopProfiles(workshopFolder, currentWorkshopJson);

        if (TryGetPublishedFileId(profilesData.GetActiveProfile().WorkshopJson.PublisherId, out var fileId)
            || TryGetPublishedFileId(currentWorkshopJson?.PublisherId, out fileId))
        {
            return fileId;
        }

        var publishedProfile = profilesData.Profiles.FirstOrDefault(profile =>
            HasPublishedFileId(profile.WorkshopJson.PublisherId));
        return publishedProfile is not null && TryGetPublishedFileId(publishedProfile.WorkshopJson.PublisherId, out fileId)
            ? fileId
            : 0;
    }

    public enum LogExportKind
    {
        Full,
        Errors,
        Warnings,
    }

    public string GetLogExportText(LogExportKind kind)
    {
        var outputText = Output.Text;

        if (kind == LogExportKind.Full)
            return outputText;

        var lines = NormalizeLineEndings(outputText)
            .Split('\n')
            .Where(line => LogLineMatchesKind(line, kind))
            .Select(BuildLogCodes.Strip)
            .ToArray();

        return lines.Length == 0
            ? ""
            : string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public string GetSuggestedLogExportFileName(LogExportKind kind)
    {
        var safeProjectName = SelectedProject is { IsGroupHeader: false }
            ? MakeSafeFileName(SelectedProject.Name)
            : "build";
        var label = kind switch
        {
            LogExportKind.Errors => "errors",
            LogExportKind.Warnings => "warnings",
            _ => "build",
        };
        return $"{safeProjectName}_{label}_{DateTime.Now:yyyyMMdd_HHmmss}.log";
    }

    private static bool LogLineMatchesKind(string line, LogExportKind kind) =>
        kind switch
        {
            LogExportKind.Errors => BuildLogCodes.IsError(line),
            LogExportKind.Warnings => BuildLogCodes.IsWarning(line),
            _ => true,
        };

    public string GetLogExportStartFolder()
    {
        if (SelectedProject is { IsGroupHeader: false })
        {
            if (Directory.Exists(SelectedProject.FolderPath))
                return SelectedProject.FolderPath;
        }

        return GetLogFolder();
    }

    private static string GetLogFolder() => AppPaths.LogsFolder;

    /// <summary>Opens a folder in Explorer. Never creates it: a missing folder says so instead.</summary>
    private void OpenDirectory(string folderPath, string label)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            StatusText = $"Couldn't find the {label}.";
            _ = RefreshProjectCapabilitiesAsync();
            return;
        }

        if (ShellLauncher.TryOpen(folderPath) is { } error)
            StatusText = $"Couldn't open the {label}: {error}";
    }

    private static string MakeSafeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = fileName.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    private static string NormalizeLineEndings(string raw) =>
        raw.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Clearing mid-build would delete the log file the build is still writing.</summary>
    public bool CanClearLog => !IsBuildRunning && !IsBuildCancelling;

    [RelayCommand(CanExecute = nameof(CanClearLog))]
    private void ClearLog()
    {
        if (!CanClearLog)
            return;

        ResetOutputText();
        ErrorCount = 0;
        LastBuildOutcome = BuildOutcome.None;
        WarningCount = 0;
        StatusText = "Ready";
        BuildTimestamp = "";
        BuildDurationText = "";

        // Delete persisted log file and clear build metadata
        if (SelectedProject is { IsGroupHeader: false })
        {
            var ps = GetOrCreateProjectSettings(SelectedProject.Key);
            ps.LastBuildStatus = null;
            ps.LastBuildTimestamp = null;
            ps.LastBuildErrorCount = 0;
            ps.LastBuildWarningCount = 0;
            ps.LastBuildDurationMs = 0;
            QueueSettingsSave();
            var logPath = _settings.GetBuildLogPath(SelectedProject.Key);
            _ = QueueLogFileWork(() =>
            {
                try
                {
                    File.Delete(logPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort: the result is already cleared in settings.
                }

                return true;
            });
        }
    }

    [RelayCommand]
    private Task StopGame() => StopGameAsync();

    /// <summary>
    /// The Run button and the palette: launch only. Closing a running game is the
    /// status bar's job, so this never kills it. The launch isn't written into the
    /// last build's log; the status line and the "Game running" pill report it.
    /// </summary>
    [RelayCommand]
    private Task RunProject()
    {
        if (SelectedProject is null || SelectedProject.IsGroupHeader)
            return Task.CompletedTask;

        if (IsGameRunning)
        {
            StatusText = "Black Ops III is already running.";
            return Task.CompletedTask;
        }

        if (IsBuildRunning)
        {
            StatusText = "Wait for the build to finish before running the game.";
            return Task.CompletedTask;
        }

        var args = BuildRunArgumentsForSelectedProject();

        var launchCommand = BuildGameLaunchCommand(args);
        if (!File.Exists(launchCommand.Executable))
        {
            StatusText = LaunchOnline
                ? "Steam.exe wasn't found. Install Steam, or switch Launch config to Offline."
                : "BlackOps3.exe wasn't found. Open Setup Doctor to check the game path.";
            UpdateToolAvailability();
            return Task.CompletedTask;
        }

        LaunchGameCommand(launchCommand);
        return Task.CompletedTask;
    }

    public string GetRunCommandText()
    {
        if (SelectedProject is null || SelectedProject.IsGroupHeader)
            return "";

        var args = BuildRunArgumentsForSelectedProject();
        return BuildGameLaunchCommand(args).ToCommandLine();
    }

    public string GetBuildCommandText()
    {
        if (SelectedProject is null || SelectedProject.IsGroupHeader)
            return "";

        var commands = BuildCommandList(forceRun: BuildModeIndex != 1);
        return string.Join(
            Environment.NewLine + Environment.NewLine,
            commands.Select(command =>
                $"# {command.Description}{Environment.NewLine}{command.ToCommandLine()}"));
    }

    private List<string> BuildRunArgumentsForSelectedProject()
    {
        var args = new List<string>();
        if (_runDvars.Count > 0)
            args.AddRange(_runDvars);

        if (SelectedProject?.Type == ProjectType.Map)
        {
            args.AddRange(["+set", "fs_game", SelectedProject.Name, "+devmap", SelectedProject.Name]);
        }
        else if (SelectedProject?.Type == ProjectType.Mod)
        {
            args.AddRange(["+set", "fs_game", SelectedProject.Name]);
            AddQuickLaunchMapArgs(args);
        }

        AddRunOptionsArgs(args);
        return args;
    }

    private void AddQuickLaunchMapArgs(List<string> args)
    {
        var quickMap = GetSelectedQuickLaunchMapName();
        if (SelectedProject is { Type: ProjectType.Mod } && !string.IsNullOrWhiteSpace(quickMap))
            args.AddRange(["+devmap", quickMap]);
    }

    private void AddRunOptionsArgs(List<string> args)
    {
        if (!string.IsNullOrEmpty(RunOptions))
            args.AddRange(SplitCommandLineArguments(RunOptions));
    }

    private static IReadOnlyList<string> SplitCommandLineArguments(string value)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (ch == '\\' && i + 1 < value.Length && value[i + 1] == '"')
            {
                current.Append('"');
                i++;
                hasToken = true;
                continue;
            }

            current.Append(ch);
            hasToken = true;
        }

        if (hasToken)
            args.Add(current.ToString());

        return args;
    }

    private async Task StopGameAsync()
    {
        if (!IsGameRunning || IsGameStopping)
            return;

        var process = _gameProcess;
        if (process is null)
        {
            // Still waiting for Steam to start the game. The wait goes on, so a game that
            // appears late is closed as soon as it does instead of running untracked.
            _steamLaunchClosedEarly = _steamLaunchId;
            ClearTrackedGame(null);
            return;
        }

        IsGameStopping = true;

        _gameStopTask = Task.Run(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                throw;
            }
        });

        try
        {
            await _gameStopTask;
        }
        catch
        {
            _gameStopTask = null;
            IsGameStopping = false;
            StatusText = "Couldn't close the game.";
        }
    }

    /// <summary>Starts the game and tracks it; false when Windows started it without handing back a process to track.</summary>
    private bool LaunchAndTrackGame(ProcessStartInfo psi)
    {
        var proc = Process.Start(psi);
        if (proc is null)
            return false;

        TrackGameProcess(proc);
        return true;
    }

    /// <summary>
    /// Steam starts the game itself, so the game counts as running from the handoff
    /// until BlackOps3.exe appears and exits, or Steam never starts it.
    /// </summary>
    private async Task TrackSteamLaunchAsync(DateTime requestedAt)
    {
        var launchId = ++_steamLaunchId;
        IsGameRunning = true;
        IsGameStopping = false;

        var proc = await Task.Run(() => WaitForGameProcess(requestedAt, TimeSpan.FromSeconds(90)));

        // Close game was pressed while Steam was still starting it: close it now it's here.
        if (_steamLaunchClosedEarly == launchId)
        {
            if (proc is not null)
            {
                _ = Task.Run(() =>
                {
                    using (proc)
                    {
                        try { proc.Kill(entireProcessTree: true); }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    }
                });
            }

            return;
        }

        // A newer launch, or a game already tracked, supersedes this one.
        if (launchId != _steamLaunchId || !IsGameRunning || _gameProcess is not null)
        {
            proc?.Dispose();
            return;
        }

        if (proc is null)
        {
            ClearTrackedGame(null);
            StatusText = "Black Ops III didn't start from Steam. Check Steam for a prompt or an update.";
            return;
        }

        TrackGameProcess(proc);
    }

    private static Process? WaitForGameProcess(DateTime startedAfter, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Process? match = null;
            foreach (var candidate in Process.GetProcessesByName("BlackOps3"))
            {
                try
                {
                    if (match is null && candidate.StartTime >= startedAfter)
                    {
                        match = candidate;
                        continue;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Exited or not ours to inspect.
                }

                candidate.Dispose();
            }

            if (match is not null)
                return match;

            Thread.Sleep(1000);
        }

        return null;
    }

    private void TrackGameProcess(Process proc)
    {
        if (_gameProcess is { } previous && !ReferenceEquals(previous, proc))
            previous.Dispose();

        _gameProcess = proc;
        GameProcessId = proc.Id;
        IsGameRunning = true;
        IsGameStopping = false;

        _ = Task.Run(async () =>
        {
            try { await proc.WaitForExitAsync(); } catch { }
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                ClearTrackedGame(proc);
            });
        });
    }

    private void ClearTrackedGame(Process? process)
    {
        if (process is not null && _gameProcess is not null && !ReferenceEquals(_gameProcess, process))
            return;

        _gameProcess?.Dispose();
        _gameProcess = null;
        _gameStopTask = null;
        IsGameRunning = false;
        IsGameStopping = false;
        GameProcessId = 0;
    }

    private bool LaunchGameCommand(BuildCommand command, bool writeToLog = false)
    {
        if (!File.Exists(command.Executable))
        {
            var missing = $"Launch executable wasn't found: {command.Executable}";
            StatusText = missing;
            if (writeToLog)
                AppendOutputText($"\n[launcher] {missing}\n");
            return false;
        }

        if (LaunchOnline)
        {
            if (writeToLog)
            {
                AppendOutputText("\n[launcher] Online launch requested.\n");
                AppendOutputText($"[launcher] Handing launch to Steam: {command.ToCommandLine()}\n");
            }

            try
            {
                var steamStartInfo = new ProcessStartInfo(command.Executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(command.Executable) ?? "",
                };
                foreach (var arg in command.Arguments) steamStartInfo.ArgumentList.Add(arg);
                var requestedAt = DateTime.Now;
                Process.Start(steamStartInfo)?.Dispose();
                StatusText = "Launch handed to Steam.";
                _ = TrackSteamLaunchAsync(requestedAt);
                if (writeToLog)
                    AppendOutputText("[launcher] Steam handoff complete. Waiting for Black Ops III to start.\n");
                return true;
            }
            catch (Exception ex)
            {
                StatusText = ErrorText.Describe("Couldn't hand the launch to Steam.", ex);
                if (writeToLog)
                    AppendOutputText($"[launcher] Steam launch failed: {ex.Message}\n");
                return false;
            }
        }

        if (writeToLog)
        {
            AppendOutputText("\n[launcher] Offline launch requested.\n");
            AppendOutputText($"[launcher] Starting game: {command.ToCommandLine()}\n");
        }

        try
        {
            var psi = new ProcessStartInfo(command.Executable)
            {
                UseShellExecute = true,
                // The game folder, as when it's started from Explorer or Steam; not Blackbird's bin folder.
                WorkingDirectory = Path.GetDirectoryName(command.Executable) ?? "",
            };
            foreach (var arg in command.Arguments) psi.ArgumentList.Add(arg);
            if (!LaunchAndTrackGame(psi))
            {
                StatusText = "Black Ops III was started, but Blackbird can't track it. Close it from the game.";
                if (writeToLog)
                    AppendOutputText("[launcher] Game started, but Windows didn't hand back a process to track.\n");
                return true;
            }

            if (writeToLog)
                AppendOutputText("[launcher] Game process started and is being tracked.\n");
            return true;
        }
        catch (Exception ex)
        {
            StatusText = ErrorText.Describe("Couldn't start Black Ops III.", ex);

            if (writeToLog)
                AppendOutputText($"[launcher] Launch failed: {ex.Message}\n");
            return false;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedProject is null || SelectedProject.IsGroupHeader) return;

        OpenDirectory(SelectedProject.FolderPath, "project folder");
    }

    [RelayCommand]
    private void OpenScriptsInVsCode()
    {
        if (SelectedProject is null || SelectedProject.IsGroupHeader) return;

        var scriptsFolder = Path.Combine(SelectedProject.FolderPath, "scripts");
        OpenFolderInVsCode(scriptsFolder, "scripts");
    }

    [RelayCommand]
    private void OpenLuiInVsCode()
    {
        if (SelectedProject is null || SelectedProject.IsGroupHeader) return;

        var uiFolder = Path.Combine(SelectedProject.FolderPath, "ui");
        OpenFolderInVsCode(uiFolder, "ui");
    }

    [RelayCommand]
    private void EditZoneFile() => OpenFile(GetSelectedZoneFilePath());

    [RelayCommand]
    private void EditMapSourceFile() => OpenFile(GetSelectedMapSourceFilePath());

    [RelayCommand]
    private void EditSzcFile() => OpenFile(GetSelectedSzcFilePath());

    /// <summary>A map's zone file, or a mod's first ticked zone (its first zone when none is ticked).</summary>
    private string? GetSelectedZoneFilePath()
    {
        if (SelectedProject is not { IsGroupHeader: false } project)
            return null;

        if (project.Type == ProjectType.Map)
            return project.ZoneFilePath;

        var zone = project.ZoneFiles.FirstOrDefault(z => z.IsChecked) ?? project.ZoneFiles.FirstOrDefault();
        return zone is null ? null : Path.Combine(project.FolderPath, "zone_source", $"{zone.ZoneName}.zone");
    }

    private string? GetSelectedSzcFilePath() =>
        SelectedProject is { IsGroupHeader: false, Type: ProjectType.Map } project
            ? Path.Combine(project.FolderPath, "sound", "zoneconfig", $"{project.Name}.szc")
            : null;

    /// <summary>Opens a project file in its associated editor. The menu disables missing files; this covers one deleted since.</summary>
    private void OpenFile(string? path)
    {
        if (path is null)
            return;

        if (!File.Exists(path))
        {
            StatusText = $"Couldn't find {Path.GetFileName(path)}.";
            _ = RefreshProjectCapabilitiesAsync();
            return;
        }

        StartOrReport(new ProcessStartInfo(path) { UseShellExecute = true }, Path.GetFileName(path));
    }

    public void SetRunDvars(List<string> dvars)
    {
        _runDvars = dvars;
    }

    public void SetActiveBuildLanguage(string language)
    {
        SelectedBuildLanguageIndex = GetBuildLanguageSettingIndex(language);
        SaveProjectSettings();
    }

    private string? GetSelectedMapSourceFilePath()
    {
        if (SelectedProject is not { IsGroupHeader: false, Type: ProjectType.Map } project)
            return null;

        return MapSourcePath(project.Name);

    }

    private ProjectSettings GetOrCreateProjectSettings(string key)
    {
        if (!_settings.Projects.TryGetValue(key, out var ps))
        {
            ps = new ProjectSettings();
            _settings.Projects[key] = ps;
        }
        return ps;
    }

    private void LoadProjectSettings(ProjectItem project, bool restoreBuildState = true)
    {
        _isLoadingProject = true;
        try
        {
            var ps = GetOrCreateProjectSettings(project.Key);

            BuildPresets.ReplaceAll(ps.BuildPresets
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(CloneBuildPreset));
            OnPropertyChanged(nameof(BuildPresetLabel));

            // Build step checkboxes
            IsCompileChecked = ps.IsCompileChecked;
            CompileModeIndex = ps.CompileModeIndex;
            IsLightChecked = ps.IsLightChecked;
            LightQualityIndex = ps.LightQualityIndex;
            IsLinkChecked = ps.IsLinkChecked;

            // Build mode
            BuildModeIndex = ps.BuildModeIndex;

            // Run options
            RunOptions = ps.RunOptions;
            LaunchOnline = ps.LaunchOnline;
            RefreshQuickLaunchMapOptions(ps.QuickLaunchMap);

            // Environment config
            LaunchConfigIndex = ps.LaunchConfigIndex;

            // Mod zone selections
            if (project.Type == ProjectType.Mod && ps.CheckedZones is not null)
            {
                foreach (var zone in project.ZoneFiles)
                    zone.IsChecked = ps.CheckedZones.Contains(zone.ZoneName);
            }

            // Load environment dvars
            ApplyEnvironmentDvars();
            RefreshSelectedBuildLanguage();

            // Restore last build state (skipped to keep a running build's output)
            if (restoreBuildState)
                RestoreLastBuildState(project, ps);

            OnPropertyChanged(nameof(ZoneSummary));
        }
        finally
        {
            _isLoadingProject = false;
        }
    }

    private void RestoreLastBuildState(ProjectItem project, ProjectSettings ps)
    {
        if (ps.LastBuildTimestamp.HasValue)
        {
            ErrorCount = ps.LastBuildErrorCount;
            WarningCount = ps.LastBuildWarningCount;
            ApplyBuildStatus(ps.LastBuildStatus);
            BuildTimestamp = ps.LastBuildTimestamp.Value.ToString("d MMM yyyy 'at' HH:mm");
            BuildDurationText = ps.LastBuildDurationMs > 0
                ? FormatDuration(TimeSpan.FromMilliseconds(ps.LastBuildDurationMs))
                : "";

            _ = LoadSavedBuildLogAsync(project.Key);
        }
        else
        {
            ResetOutputText();
            ErrorCount = 0;
            LastBuildOutcome = BuildOutcome.None;
            WarningCount = 0;
            StatusText = "Ready";
            BuildTimestamp = "";
            BuildDurationText = "";
        }
    }

    private void SaveProjectSettings()
    {
        if (_isLoadingProject) return;
        if (SelectedProject is null || SelectedProject.IsGroupHeader) return;

        var ps = GetOrCreateProjectSettings(SelectedProject.Key);

        ps.LaunchConfigIndex = LaunchConfigIndex;
        ps.IsCompileChecked = IsCompileChecked;
        ps.CompileModeIndex = CompileModeIndex;
        ps.IsLightChecked = IsLightChecked;
        ps.LightQualityIndex = LightQualityIndex;
        ps.IsLinkChecked = IsLinkChecked;
        ps.BuildModeIndex = BuildModeIndex;
        ps.RunOptions = RunOptions;
        ps.LaunchOnline = LaunchOnline;
        ps.QuickLaunchMap = NormalizeQuickLaunchMapForStorage(SelectedQuickLaunchMap);
        ps.EnvironmentBuildLanguages[ValueAtOrFirst(LaunchConfigs, LaunchConfigIndex)] =
            GetSelectedBuildLanguageValue();

        // Save mod zone selections
        if (SelectedProject.Type == ProjectType.Mod)
            ps.CheckedZones = SelectedProject.ZoneFiles.Where(z => z.IsChecked).Select(z => z.ZoneName).ToList();

        QueueSettingsSave();
    }

    public void SaveCurrentBuildPreset(string name)
    {
        if (SelectedProject is null || SelectedProject.IsGroupHeader) return;

        name = name.Trim();
        if (name.Length == 0) return;

        var preset = CreateCurrentBuildPreset(name);
        var existing = BuildPresets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            BuildPresets.Remove(existing);

        BuildPresets.Add(preset);
        SortBuildPresets();
        PersistBuildPresets();
        OnPropertyChanged(nameof(BuildPresetLabel));
    }

    /// <summary>
    /// The build preset button's label: the saved preset whose steps match what's
    /// ticked now, "All steps" when everything is, otherwise "Custom". The build mode
    /// is not part of a preset's identity.
    /// </summary>
    public string BuildPresetLabel =>
        MatchingBuildPreset()?.Name ?? (AreAllBuildStepsSelected() ? BuildPreset.AllStepsName : BuildPreset.CustomName);

    public BuildPreset? MatchingBuildPreset() =>
        SelectedProject is { IsGroupHeader: false }
            ? BuildPresets.FirstOrDefault(PresetMatchesCurrentSteps)
            : null;

    private bool PresetMatchesCurrentSteps(BuildPreset preset)
    {
        if (preset.IsLinkChecked != IsLinkChecked)
            return false;

        if (IsMapSelected)
        {
            return preset.IsCompileChecked == IsCompileChecked
                   && preset.IsLightChecked == IsLightChecked
                   && (!IsCompileChecked || preset.CompileModeIndex == CompileModeIndex)
                   && (!IsLightChecked || preset.LightQualityIndex == LightQualityIndex);
        }

        if (IsModSelected && SelectedProject is not null)
        {
            var presetZones = preset.CheckedZones ?? [];
            return SelectedProject.ZoneFiles.All(zone =>
                zone.IsChecked == presetZones.Contains(zone.ZoneName, StringComparer.OrdinalIgnoreCase));
        }

        return false;
    }

    private bool AreAllBuildStepsSelected()
    {
        if (!IsLinkChecked)
            return false;
        if (IsMapSelected)
            return IsCompileChecked && IsLightChecked;
        return IsModSelected && SelectedProject is not null && SelectedProject.ZoneFiles.All(z => z.IsChecked);
    }

    public void ApplyBuildPreset(BuildPreset? preset)
    {
        if (preset is null || SelectedProject is null || SelectedProject.IsGroupHeader)
            return;

        _isLoadingProject = true;
        try
        {
            IsCompileChecked = preset.IsCompileChecked;
            CompileModeIndex = preset.CompileModeIndex;
            IsLightChecked = preset.IsLightChecked;
            LightQualityIndex = preset.LightQualityIndex;
            IsLinkChecked = preset.IsLinkChecked;

            if (SelectedProject.Type == ProjectType.Mod && preset.CheckedZones is not null)
            {
                foreach (var zone in SelectedProject.ZoneFiles)
                    zone.IsChecked = preset.CheckedZones.Contains(zone.ZoneName, StringComparer.OrdinalIgnoreCase);
            }
        }
        finally
        {
            _isLoadingProject = false;
        }

        OnPropertyChanged(nameof(ZoneSummary));
        NotifyBuildStateChanged();
        SaveProjectSettings();
    }

    public void DeleteBuildPreset(BuildPreset? preset)
    {
        if (preset is null)
            return;

        BuildPresets.Remove(preset);
        PersistBuildPresets();
        OnPropertyChanged(nameof(BuildPresetLabel));
    }

    private BuildPreset CreateCurrentBuildPreset(string name) => new()
    {
        Name = name,
        IsCompileChecked = IsCompileChecked,
        CompileModeIndex = CompileModeIndex,
        IsLightChecked = IsLightChecked,
        LightQualityIndex = LightQualityIndex,
        IsLinkChecked = IsLinkChecked,
        CheckedZones = SelectedProject is { Type: ProjectType.Mod }
            ? [.. SelectedProject.ZoneFiles.Where(z => z.IsChecked).Select(z => z.ZoneName)]
            : null
    };

    private void PersistBuildPresets()
    {
        if (SelectedProject is null || SelectedProject.IsGroupHeader) return;

        var ps = GetOrCreateProjectSettings(SelectedProject.Key);
        ps.BuildPresets = [.. BuildPresets.Select(CloneBuildPreset)];
        QueueSettingsSave();
    }

    private void SortBuildPresets()
    {
        BuildPresets.ReplaceAll(BuildPresets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static BuildPreset CloneBuildPreset(BuildPreset preset) => new()
    {
        Name = preset.Name,
        IsCompileChecked = preset.IsCompileChecked,
        CompileModeIndex = preset.CompileModeIndex,
        IsLightChecked = preset.IsLightChecked,
        LightQualityIndex = preset.LightQualityIndex,
        IsLinkChecked = preset.IsLinkChecked,
        CheckedZones = preset.CheckedZones is null ? null : [.. preset.CheckedZones],
    };

    // Searches every project, not just the picker's visible rows, so a new or
    // renamed project is selected even when a filter or collapsed group hides it.
    public void SelectProjectByName(string name, ProjectType type)
    {
        var match = _allProjects.FirstOrDefault(p => p.Name == name && p.Type == type);
        if (match is not null)
            TrySelectProject(match);
    }

    public void RefreshSelectedProjectDerivedMetadata()
    {
        if (SelectedProject is not { IsGroupHeader: false } project)
            return;

        var name = project.Name;
        var type = project.Type;
        foreach (var item in _allProjects.Where(item => !item.IsGroupHeader
                                                        && item.Type == type
                                                        && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ApplyProjectDerivedMetadata(item);
        }

        RebuildProjectList(preserveSelection: true, projectsChanged: false);
        SelectProjectByName(name, type);
        _ = RefreshProjectCapabilitiesAsync();
    }

    public async Task UpdateSelectedProjectMetadataAsync(
        string displayName,
        string category,
        bool? isFavorite = null,
        string? notes = null)
    {
        if (SelectedProject is not { IsGroupHeader: false } project) return;

        var ps = GetOrCreateProjectSettings(project.Key);
        ps.DisplayName = NormalizeDisplayNameForStorage(displayName, project.Name);
        ps.Category = NormalizeCategoryForStorage(category, project.DefaultCategory);
        if (isFavorite.HasValue)
            ps.IsFavorite = isFavorite.Value;
        if (notes is not null)
            ps.Notes = notes.Trim();
        QueueSettingsSave();

        var name = project.Name;
        var type = project.Type;
        await PopulateFileListAsync();
        SelectProjectByName(name, type);
    }

    /// <summary>
    /// Stores the details picked when creating a project, rescans and switches to it.
    /// Returns false when a running build kept the current project selected.
    /// </summary>
    public async Task<bool> OpenCreatedProjectAsync(
        string name,
        ProjectType type,
        string displayName,
        string category)
    {
        // Fresh settings: a deleted project of the same name may have left its own behind.
        var ps = new ProjectSettings();
        _settings.Projects[ProjectItem.KeyFor(type, name)] = ps;
        ps.DisplayName = NormalizeDisplayNameForStorage(displayName, name);
        ps.Category = NormalizeCategoryForStorage(category, type == ProjectType.Map ? "Maps" : "Mods");

        QueueSettingsSave();

        await PopulateFileListAsync();
        SelectProjectByName(name, type);
        return SelectedProject is { } selected && selected.Type == type && selected.Name == name;
    }

    public async Task<int> MoveProjectsInCategoryAsync(string sourceCategory, string targetCategory, bool resetToDefaults)
    {
        sourceCategory = sourceCategory.Trim();
        targetCategory = targetCategory.Trim();
        if (sourceCategory.Length == 0)
            return 0;

        var selectedName = SelectedProject is { IsGroupHeader: false } ? SelectedProject.Name : "";
        var selectedType = SelectedProject?.Type;
        var matches = ProjectCatalog
            .Where(project => !project.IsGroupHeader)
            .Where(project =>
            {
                var category = string.IsNullOrWhiteSpace(project.Category)
                    ? project.DefaultCategory
                    : project.Category.Trim();
                return string.Equals(category, sourceCategory, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        foreach (var project in matches)
        {
            var ps = GetOrCreateProjectSettings(project.Key);
            ps.Category = resetToDefaults
                ? ""
                : NormalizeCategoryForStorage(targetCategory, project.DefaultCategory);
        }

        var collapsed = _settings.CollapsedProjectGroups.FirstOrDefault(category =>
            string.Equals(category, sourceCategory, StringComparison.OrdinalIgnoreCase));
        if (collapsed is not null)
            _settings.CollapsedProjectGroups.Remove(collapsed);

        QueueSettingsSave();
        await PopulateFileListAsync();
        if (!string.IsNullOrWhiteSpace(selectedName) && selectedType.HasValue)
            SelectProjectByName(selectedName, selectedType.Value);

        return matches.Count;
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (SelectedProject is not { IsGroupHeader: false } project)
            return;

        var ps = GetOrCreateProjectSettings(project.Key);
        ps.IsFavorite = !ps.IsFavorite;
        project.IsFavorite = ps.IsFavorite;
        QueueSettingsSave();

        var name = project.Name;
        var type = project.Type;
        RebuildProjectList(preserveSelection: true, projectsChanged: false);
        SelectProjectByName(name, type);
        OnPropertyChanged(nameof(FavoriteActionLabel));
    }

    private void ApplyProjectMetadata(ProjectItem project)
    {
        if (!_settings.Projects.TryGetValue(project.Key, out var ps))
            return;

        var displayName = BuildLogCodes.Strip(ps.DisplayName ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(displayName))
            project.DisplayName = displayName;

        var category = ps.Category?.Trim();
        project.Category = string.IsNullOrWhiteSpace(category)
            ? project.DefaultCategory
            : category;

        project.IsFavorite = ps.IsFavorite;
        project.Notes = ps.Notes?.Trim() ?? "";
    }

    private void ApplyProjectDerivedMetadata(ProjectItem project)
    {
        project.IsPublished = HasPublishedWorkshopItem(project);
    }

    private static int CategorySort(string category) =>
        category switch
        {
            "Maps" => 0,
            "Mods" => 1,
            _ => 2
        };

    private bool IsProjectGroupCollapsed(string? groupName) =>
        !string.IsNullOrWhiteSpace(groupName)
        && _settings.CollapsedProjectGroups.Contains(groupName, StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private void ClearProjectFilter() => SelectedProjectFilterIndex = 0;

    [RelayCommand]
    private void ToggleProjectGroup(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return;

        var existing = _settings.CollapsedProjectGroups.FirstOrDefault(group =>
            string.Equals(group, groupName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            _settings.CollapsedProjectGroups.Add(groupName);
        else
            _settings.CollapsedProjectGroups.Remove(existing);

        QueueSettingsSave();
        RebuildProjectList(preserveSelection: true, projectsChanged: false);
    }

    private bool HasPublishedWorkshopItem(ProjectItem project)
    {
        var workshopFolder = Path.Combine(project.FolderPath, "zone");
        var data = _fileSystem.ReadWorkshopJson(workshopFolder);
        var profiles = _fileSystem.ReadWorkshopProfiles(workshopFolder, data);
        if (profiles.Profiles.Any(profile => HasPublishedFileId(profile.WorkshopJson.PublisherId)))
            return true;

        return HasPublishedFileId(data?.PublisherId);
    }

    private static bool HasPublishedFileId(string? publisherId) =>
        ulong.TryParse(publisherId, out var id) && id != 0;

    private static bool TryGetPublishedFileId(string? publisherId, out ulong fileId) =>
        ulong.TryParse(publisherId, out fileId) && fileId != 0;

    private static long CalculateDirectorySize(string folderPath)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(folderPath).EnumerateFiles("*", options))
                total += file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder may disappear mid-scan; a partial size is fine for an estimate.
        }

        return total;
    }

    private static string NormalizeDisplayNameForStorage(string displayName, string projectName)
    {
        var trimmed = BuildLogCodes.Strip(displayName).Trim();
        return string.Equals(trimmed, projectName, StringComparison.Ordinal) ? "" : trimmed;
    }

    private static string NormalizeCategoryForStorage(string category, string defaultCategory)
    {
        var trimmed = category.Trim();
        return string.Equals(trimmed, defaultCategory, StringComparison.Ordinal) ? "" : trimmed;
    }

    private void WatchSelectedProject(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return;

        _selectedProjectWatcher = new FileSystemWatcher(folderPath)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };
        _selectedProjectWatcher.Created += OnSelectedProjectFolderChanged;
        _selectedProjectWatcher.Deleted += OnSelectedProjectFolderChanged;
        _selectedProjectWatcher.Renamed += OnSelectedProjectFolderRenamed;
    }

    private void StopWatchingSelectedProject()
    {
        if (_selectedProjectWatcher is null)
            return;

        _selectedProjectWatcher.Created -= OnSelectedProjectFolderChanged;
        _selectedProjectWatcher.Deleted -= OnSelectedProjectFolderChanged;
        _selectedProjectWatcher.Renamed -= OnSelectedProjectFolderRenamed;
        _selectedProjectWatcher.Dispose();
        _selectedProjectWatcher = null;
    }

    private void OnSelectedProjectFolderChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsProjectCapabilityFolder(e.Name))
            return;

        Dispatcher.UIThread.Post(() => _ = RefreshProjectCapabilitiesAsync());
    }

    private void OnSelectedProjectFolderRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsProjectCapabilityFolder(e.Name) && !IsProjectCapabilityFolder(e.OldName))
            return;

        Dispatcher.UIThread.Post(() => _ = RefreshProjectCapabilitiesAsync());
    }

    private static bool IsProjectCapabilityFolder(string? name) =>
        string.Equals(name, "scripts", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "ui", StringComparison.OrdinalIgnoreCase);

    private void OpenFolderInVsCode(string folderPath, string label)
    {
        if (!Directory.Exists(folderPath))
        {
            StatusText = $"Couldn't find the {label} folder.";
            _ = RefreshProjectCapabilitiesAsync();
            return;
        }

        if (!IsVsCodeAvailable)
        {
            StatusText = VsCodeMissingReason;
            UpdateToolAvailability();
            return;
        }

        var psi = new ProcessStartInfo { FileName = "code", UseShellExecute = true };
        psi.ArgumentList.Add(folderPath);
        if (ShellLauncher.TryStart(psi) is { } error)
            StatusText = $"Couldn't open the {label} folder in VS Code: {error}";

    }
}
