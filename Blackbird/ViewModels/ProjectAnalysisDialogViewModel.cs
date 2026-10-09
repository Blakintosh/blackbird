using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Blackbird.Models;
using Blackbird.Services;

namespace Blackbird.ViewModels;

public partial class ProjectAnalysisDialogViewModel : ObservableObject
{
    private const string DuplicateGdtCheck = "Duplicate GDT asset";

    private readonly IProjectAnalysisService _analyzer;
    private ProjectItem? _project;
    private List<ProjectAnalysisIssue> _allIssues = [];

    // Bumped by every analysis and cleanup; a result from an older run never
    // replaces a newer one.
    private int _generation;
    private int _searchVersion;
    private Task _cleanup = Task.CompletedTask;
    private CancellationTokenSource? _analysisCts;

    [ObservableProperty] private string _projectName = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private bool _isProgressVisible;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string _footerStatus = "";
    [ObservableProperty] private int _selectedIssueFilterIndex;
    [ObservableProperty] private string _issueSearchText = "";
    [ObservableProperty] private IReadOnlyList<ProjectAnalysisIssue> _issues = [];

    public ProjectAnalysisDialogViewModel(IProjectAnalysisService analyzer)
    {
        _analyzer = analyzer;
    }

    public IReadOnlyList<string> IssueFilters { get; } = ["All", "Blockers", "Warnings", "Info"];

    public bool IsBusy => IsAnalyzing || IsCleaning;
    public bool HasError => ErrorText is not null;
    public bool IsListVisible => !IsProgressVisible && !HasError;
    public bool IsFilterEmpty => IsListVisible && _allIssues.Count > 0 && Issues.Count == 0;
    public bool IsNothingFound => IsListVisible && !IsBusy && _project is not null && _allIssues.Count == 0;
    public bool HasFilter => SelectedIssueFilterIndex != 0 || IssueSearchText.Length > 0;

    private bool HasDuplicateGdtFindings => _allIssues.Any(issue => issue.Check == DuplicateGdtCheck);

    public bool CanCleanGdtDuplicates => !IsBusy && _project is not null && HasDuplicateGdtFindings;
    public bool CanCopyReport => !IsBusy && Issues.Count > 0;

    /// <summary>Why Clean is disabled, for its tooltip; null when it can run.</summary>
    public string? CleanDisabledReason =>
        IsCleaning ? "Cleaning…"
        : IsAnalyzing ? "Analyzing…"
        : !HasDuplicateGdtFindings ? "No duplicate GDT assets found"
        : null;

    public string IssueFilterSummary =>
        Issues.Count == _allIssues.Count
            ? $"{Issues.Count:N0} finding{(Issues.Count == 1 ? "" : "s")}"
            : $"{Issues.Count:N0} of {_allIssues.Count:N0}";

    public string ReportText =>
        string.Join(
            "\n",
            Issues.Select(issue =>
            {
                var location = issue.HasLocation
                    ? $" [{issue.FilePath}{(issue.LineNumber > 0 ? ":" + issue.LineNumber : "")}]"
                    : "";
                return $"{issue.Status}: {issue.Check}{location} - {issue.Detail}";
            }));

    /// <summary>
    /// Analyzes <paramref name="project"/>, replacing whatever the window showed. Starting
    /// again (same project or another) supersedes an analysis still in flight, but never
    /// a GDT cleanup: its result is what the window must show next.
    /// </summary>
    public Task AnalyzeAsync(ProjectItem project)
    {
        if (IsCleaning)
            return Task.CompletedTask;

        var switching = _project is null || !string.Equals(_project.FolderPath, project.FolderPath, StringComparison.OrdinalIgnoreCase);
        _project = project;
        ProjectName = project.DisplayName;
        if (switching)
        {
            // Another project's findings under this name would be wrong, even briefly.
            Subtitle = project.DisplayName;
            SetIssues([]);
        }

        // The run this one supersedes stops walking the project rather than finishing unseen.
        _analysisCts?.Cancel();
        var cts = _analysisCts = new CancellationTokenSource();
        return RunAsync(
            $"Analyzing {project.DisplayName}…",
            () => _analyzer.AnalyzeAsync(project, cts.Token),
            analyzing: true);
    }

    /// <summary>Stops an analysis in flight (the window is closing). A GDT cleanup always finishes.</summary>
    public void CancelAnalysis() => _analysisCts?.Cancel();

    public Task RetryAsync() => _project is null ? Task.CompletedTask : AnalyzeAsync(_project);

    public Task CleanGdtDuplicatesAsync()
    {
        if (!CanCleanGdtDuplicates || _project is not { } project)
            return Task.CompletedTask;

        return _cleanup = RunAsync(
            "Removing duplicate GDT assets…",
            () => _analyzer.CleanDuplicateGdtAssetsAsync(project),
            analyzing: false);
    }

    /// <summary>
    /// Completes once a running GDT cleanup has written every file. Closing waits for it, so no
    /// GDT is left half-cleaned with its original not yet in the Recycle Bin.
    /// </summary>
    public Task CleanupCompletion => _cleanup;

    public void ReportCopied() => FooterStatus = "Report copied";

    public void ClearFilters()
    {
        SelectedIssueFilterIndex = 0;
        IssueSearchText = "";
    }

    private async Task RunAsync(string progressText, Func<Task<ProjectAnalysisResult>> work, bool analyzing)
    {
        var generation = ++_generation;
        ProgressText = progressText;
        ErrorText = null;
        FooterStatus = "";
        if (analyzing) IsAnalyzing = true; else IsCleaning = true;
        _ = RevealProgressAsync(generation);

        try
        {
            var result = await work();
            if (generation == _generation)
                Load(result);
        }
        catch (OperationCanceledException) when (analyzing)
        {
            // Superseded by a newer analysis, or the window closed: nothing to show.
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                SetIssues([]);
                ErrorText = analyzing
                    ? Services.ErrorText.Describe($"Couldn't analyze {ProjectName}.", ex)
                    : Services.ErrorText.Describe("Couldn't clean the GDT files.", ex);
            }
        }
        finally
        {
            // A cleanup always ends its own busy state (its writes are done either way);
            // an analysis only when no newer analysis has taken over.
            if (!analyzing)
                IsCleaning = false;
            else if (generation == _generation)
                IsAnalyzing = false;

            if (generation == _generation)
                IsProgressVisible = false;
        }
    }

    // Loading indicators appear only once work passes 150 ms; fast runs just swap the list.
    private async Task RevealProgressAsync(int generation)
    {
        await Task.Delay(150);
        if (generation == _generation && IsBusy)
            IsProgressVisible = true;
    }

    private void Load(ProjectAnalysisResult result)
    {
        Subtitle = $"{ProjectName} · {Summarize(result)}";
        var folder = _project?.FolderPath ?? "";
        foreach (var issue in result.Issues)
            issue.DisplayLocation = RelativeLocation(issue, folder);
        _allIssues =
        [
            .. result.Issues
                .OrderBy(SeveritySort)
                .ThenBy(issue => issue.Check)
                .ThenBy(issue => issue.FilePath)
                .ThenBy(issue => issue.LineNumber)
        ];

        ApplyFilters();
    }

    private void SetIssues(List<ProjectAnalysisIssue> issues)
    {
        _allIssues = issues;
        ApplyFilters();
    }

    /// <summary>"1 warning", "2 blockers · 1 warning", or "No issues".</summary>
    private static string Summarize(ProjectAnalysisResult result)
    {
        var parts = new List<string>();
        if (result.BlockerCount > 0)
            parts.Add($"{result.BlockerCount} blocker{(result.BlockerCount == 1 ? "" : "s")}");
        if (result.WarningCount > 0)
            parts.Add($"{result.WarningCount} warning{(result.WarningCount == 1 ? "" : "s")}");
        if (parts.Count == 0 && result.InfoCount > 0)
            parts.Add($"{result.InfoCount} info");
        return parts.Count == 0 ? "No issues" : string.Join(" · ", parts);
    }

    private static string RelativeLocation(ProjectAnalysisIssue issue, string projectFolder)
    {
        if (!issue.HasLocation)
            return "";

        var path = issue.FilePath;
        if (projectFolder.Length > 0)
        {
            var relative = System.IO.Path.GetRelativePath(projectFolder, path);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative))
                path = relative;
        }

        return issue.LineNumber > 0 ? $"{path}:{issue.LineNumber}" : path;
    }

    partial void OnSelectedIssueFilterIndexChanged(int value) => ApplyFilters();

    // Typing filters once it pauses for 150 ms; clearing the field applies at once.
    partial void OnIssueSearchTextChanged(string value)
    {
        var version = ++_searchVersion;
        if (value.Trim().Length == 0)
        {
            ApplyFilters();
            return;
        }

        _ = ApplySearchAfterPauseAsync(version);
    }

    private async Task ApplySearchAfterPauseAsync(int version)
    {
        await Task.Delay(150);
        if (version == _searchVersion)
            ApplyFilters();
    }

    partial void OnIsAnalyzingChanged(bool value) => OnBusyChanged();

    partial void OnIsCleaningChanged(bool value) => OnBusyChanged();

    partial void OnIsProgressVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsListVisible));
        OnPropertyChanged(nameof(IsFilterEmpty));
        OnPropertyChanged(nameof(IsNothingFound));
    }

    partial void OnErrorTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsListVisible));
        OnPropertyChanged(nameof(IsFilterEmpty));
        OnPropertyChanged(nameof(IsNothingFound));
    }

    private void OnBusyChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsNothingFound));
        OnPropertyChanged(nameof(CanCleanGdtDuplicates));
        OnPropertyChanged(nameof(CleanDisabledReason));
        OnPropertyChanged(nameof(CanCopyReport));
    }

    private void ApplyFilters()
    {
        // One swap, one notification.
        Issues = _allIssues.Where(MatchesFilter).Where(MatchesSearch).ToList();

        OnPropertyChanged(nameof(IssueFilterSummary));
        OnPropertyChanged(nameof(ReportText));
        OnPropertyChanged(nameof(IsFilterEmpty));
        OnPropertyChanged(nameof(IsNothingFound));
        OnPropertyChanged(nameof(HasFilter));
        OnPropertyChanged(nameof(CanCleanGdtDuplicates));
        OnPropertyChanged(nameof(CleanDisabledReason));
        OnPropertyChanged(nameof(CanCopyReport));
    }

    private bool MatchesFilter(ProjectAnalysisIssue issue) =>
        SelectedIssueFilterIndex switch
        {
            1 => issue.IsBlocker,
            2 => issue.IsWarning,
            3 => issue.IsInfo,
            _ => true,
        };

    private bool MatchesSearch(ProjectAnalysisIssue issue)
    {
        var query = IssueSearchText.Trim();
        if (query.Length == 0)
            return true;

        return Contains(issue.Status, query)
               || Contains(issue.Check, query)
               || Contains(issue.Detail, query)
               || Contains(issue.FilePath, query)
               || Contains(issue.LocationLabel, query);
    }

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static int SeveritySort(ProjectAnalysisIssue issue) =>
        issue.Status switch
        {
            "Blocker" => 0,
            "Warning" => 1,
            "Info" => 2,
            _ => 3,
        };
}
