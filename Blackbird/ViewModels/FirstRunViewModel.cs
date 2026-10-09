using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Blackbird.Models;
using Blackbird.Services;

namespace Blackbird.ViewModels;

/// <summary>
/// First run: finds Black Ops III and the Mod Tools without asking, then says what
/// it found in one line with one next step.
/// </summary>
public partial class FirstRunViewModel : DialogViewModelBase
{
    public enum State { Searching, Ready, ToolsMissing, NotFound }

    private readonly IFileSystemService _fileSystem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(IsReady), nameof(IsToolsMissing),
        nameof(IsNotFound), nameof(HasFolder), nameof(Headline), nameof(Detail), nameof(ContinueBlockedReason))]
    private State _current = State.Searching;

    // Bumped by every search or check: only the newest one's answer is shown, so a slow
    // auto-detect can never replace the folder the user chose meanwhile.
    private int _generation;

    /// <summary>Why the last step failed (the folder picker, say); shown in place of the hint.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string? _problem;

    [ObservableProperty] private string _gamePath = "";
    [ObservableProperty] private string _toolsPath = "";
    [ObservableProperty] private bool _showSearching;

    public FirstRunViewModel(IFileSystemService fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public bool IsSearching => Current == State.Searching;
    public bool IsReady => Current == State.Ready;
    public bool IsToolsMissing => Current == State.ToolsMissing;
    public bool IsNotFound => Current == State.NotFound;
    public bool HasFolder => Current is State.Ready or State.ToolsMissing;

    public string Headline => Current switch
    {
        State.Ready => "Black Ops III and the Mod Tools are ready",
        State.ToolsMissing => "Black Ops III found, but not the Mod Tools",
        State.NotFound => "Blackbird couldn't find Black Ops III",
        _ => "",
    };

    public string Detail => Problem ?? Current switch
    {
        State.ToolsMissing => "In Steam, open Library > Tools and install Call of Duty: Black Ops III - Mod Tools, then check again.",
        State.NotFound => "Choose the folder that contains BlackOps3.exe.",
        _ => "",
    };

    public string? ContinueBlockedReason => IsReady ? null : "Blackbird needs Black Ops III and the Mod Tools to continue.";

    /// <summary>Auto-detects from the saved settings, Steam and the environment.</summary>
    public async Task DetectAsync()
    {
        var generation = Begin();
        using var indicator = Avalonia.Threading.DispatcherTimer.RunOnce(
            () => ShowSearching = IsSearching, System.TimeSpan.FromMilliseconds(150));

        try
        {
            var (game, tools, result) = await Task.Run(() =>
            {
                var detected = _fileSystem.DetectPaths();
                var gamePath = detected.GamePath;
                // The Mod Tools install into the game folder; look there when detection didn't say.
                var toolsPath = string.IsNullOrWhiteSpace(detected.ToolsPath) ? gamePath : detected.ToolsPath;
                return (gamePath, toolsPath, _fileSystem.ValidateSetup(gamePath, toolsPath));
            });

            if (generation == _generation)
                Apply(game, tools, result);
        }
        catch (System.Exception ex)
        {
            // Detection itself failed (registry or Steam library unreadable): the user picks the folder.
            if (generation == _generation)
                Fail(ex);
        }
        finally
        {
            if (generation == _generation)
                ShowSearching = false;
        }
    }

    /// <summary>The user picked a folder: check it as both the game and the Mod Tools folder.</summary>
    public Task UseFolderAsync(string folder) => CheckAsync(folder, folder);

    private async Task CheckAsync(string game, string tools)
    {
        var generation = Begin();
        try
        {
            var result = await Task.Run(() => _fileSystem.ValidateSetup(game, tools));
            if (generation == _generation)
                Apply(game, tools, result);
        }
        catch (System.Exception ex)
        {
            if (generation == _generation)
                Fail(ex);
        }
    }

    public void ReportProblem(string message) => Problem = message;

    private int Begin()
    {
        Problem = null;
        ErrorMessage = null;
        Current = State.Searching;
        return ++_generation;
    }

    private void Fail(System.Exception ex)
    {
        GamePath = "";
        ToolsPath = "";
        Current = State.NotFound;
        Problem = ErrorText.Describe("Couldn't check for Black Ops III.", ex);
    }

    /// <summary>Checks the folders found so far again (the Mod Tools may have been installed since).</summary>
    public Task RecheckAsync() => HasFolder ? CheckAsync(GamePath, ToolsPath) : DetectAsync();

    /// <summary>Saves the folders; false (with the reason inline) when the settings couldn't be written.</summary>
    public Task<bool> SaveAsync() => RunBusyAsync(() => _fileSystem.SetPathsAsync(GamePath, ToolsPath));

    protected override string DescribeFailure(System.Exception ex) =>
        ErrorText.Describe("Couldn't save the folders.", ex);

    private void Apply(string game, string tools, SetupValidationResult result)
    {
        GamePath = game;
        ToolsPath = tools;

        var hasGame = !string.IsNullOrWhiteSpace(game) && File.Exists(Path.Combine(game, "BlackOps3.exe"));
        Current = result.IsHealthy ? State.Ready
            : hasGame ? State.ToolsMissing
            : State.NotFound;
    }
}
