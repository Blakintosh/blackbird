using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Blackbird.Models;
using Blackbird.Services;

namespace Blackbird.ViewModels;

/// <summary>
/// Setup Doctor: where Blackbird finds Black Ops III and the Mod Tools. Every check, detection
/// and save runs off the UI thread; only the newest one's answer is shown.
/// </summary>
public partial class SetupDoctorViewModel : DialogViewModelBase
{
    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan BusyIndicatorDelay = TimeSpan.FromMilliseconds(150);

    private readonly IFileSystemService _fileSystem;
    private readonly DispatcherTimer _recheckTimer = new() { Interval = TypingPause };

    // Bumped by every check, detection and edit: a slow answer never replaces a newer one,
    // or the folder the user typed meanwhile.
    private int _generation;
    private bool _settingPaths;

    [ObservableProperty] private string _gamePath = "";
    [ObservableProperty] private string _toolsPath = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveBlockedReason))]
    private bool _isHealthy;

    [ObservableProperty] private IReadOnlyList<SetupCheck> _checks = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDetect))]
    private bool _isDetecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetectLabel))]
    private bool _showDetecting;

    public SetupDoctorViewModel(IFileSystemService fileSystem)
    {
        _fileSystem = fileSystem;
        _recheckTimer.Tick += async (_, _) => await RecheckAsync();
    }

    public string? SaveBlockedReason => IsHealthy ? null : "Fix the missing items first.";
    public bool CanDetect => !IsDetecting && IsIdle;
    public string DetectLabel => ShowDetecting ? "Detecting…" : "Auto-detect";

    /// <summary>Shows a check's result, and its folders unless they're being typed.</summary>
    public void Load(SetupValidationResult result, bool updatePaths = true)
    {
        if (updatePaths)
            SetPaths(result.GamePath, result.ToolsPath);

        IsHealthy = result.IsHealthy;
        StatusText = result.IsHealthy
            ? "Everything Blackbird needs is in place."
            : result.RequiredFailures == 1
                ? "1 required item is missing."
                : $"{result.RequiredFailures} required items are missing.";

        Checks = result.Checks;
    }

    /// <summary>Checks the folders as they are now. True when everything required is there.</summary>
    public async Task<bool> RecheckAsync()
    {
        _recheckTimer.Stop();
        var generation = ++_generation;
        var (game, tools) = (GamePath, ToolsPath);
        var result = await Task.Run(() => _fileSystem.ValidateSetup(game, tools));
        if (generation == _generation)
            Load(result, updatePaths: false);
        return result.IsHealthy;
    }

    /// <summary>A folder chosen in the picker: shown and checked at once.</summary>
    public Task UseFoldersAsync(string? gamePath = null, string? toolsPath = null)
    {
        SetPaths(gamePath ?? GamePath, toolsPath ?? ToolsPath);
        return RecheckAsync();
    }

    /// <summary>Looks in Steam's libraries and the environment, then checks what it found.</summary>
    public async Task DetectAsync()
    {
        if (!CanDetect)
            return;

        _recheckTimer.Stop();
        var generation = ++_generation;
        ErrorMessage = null;
        IsDetecting = true;
        using var indicator = DispatcherTimer.RunOnce(() => ShowDetecting = IsDetecting, BusyIndicatorDelay);
        try
        {
            var (currentGame, currentTools) = (GamePath, ToolsPath);
            var (found, result) = await Task.Run(() =>
            {
                var detected = _fileSystem.DetectPaths();
                var game = string.IsNullOrWhiteSpace(detected.GamePath) ? currentGame : detected.GamePath;
                var tools = string.IsNullOrWhiteSpace(detected.ToolsPath) ? currentTools : detected.ToolsPath;
                return (!string.IsNullOrWhiteSpace(detected.GamePath), _fileSystem.ValidateSetup(game, tools));
            });

            if (generation != _generation)
                return;

            Load(result);
            if (!found)
                StatusText = "Couldn't find Black Ops III automatically. Choose the folder.";
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                ReportProblem(ErrorText.Describe("Couldn't look for Black Ops III.", ex), ex);
        }
        finally
        {
            IsDetecting = false;
            ShowDetecting = false;
        }
    }

    /// <summary>Checks once more, then saves. True when saved; on failure the reason is inline.</summary>
    public async Task<bool> SaveAsync()
    {
        var healthy = false;
        var saved = await RunBusyAsync(async () =>
        {
            healthy = await RecheckAsync();
            if (healthy)
                await _fileSystem.SetPathsAsync(GamePath, ToolsPath);
        });
        return saved && healthy;
    }

    public void ReportProblem(string message, Exception ex)
    {
        ErrorMessage = message;
        ErrorDetail = ex.Message;
    }

    public void Stop()
    {
        _recheckTimer.Stop();
        _generation++;
    }

    protected override string DescribeFailure(Exception ex) =>
        ErrorText.Describe("Couldn't save the folders.", ex);

    private void SetPaths(string game, string tools)
    {
        _settingPaths = true;
        GamePath = game;
        ToolsPath = tools;
        _settingPaths = false;
    }

    partial void OnGamePathChanged(string value) => OnPathEdited();
    partial void OnToolsPathChanged(string value) => OnPathEdited();

    // Typing re-checks once it pauses; anything still running for the old text is dropped.
    private void OnPathEdited()
    {
        if (_settingPaths)
            return;

        _generation++;
        ErrorMessage = null;
        _recheckTimer.Stop();
        _recheckTimer.Start();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsIdle))
            OnPropertyChanged(nameof(CanDetect));
    }
}
