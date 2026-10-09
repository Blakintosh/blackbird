using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Blackbird.Models;
using Blackbird.Services;

namespace Blackbird.ViewModels;

/// <summary>Project info: how a project appears in Blackbird, plus renaming its folder.</summary>
public partial class ProjectDetailsDialogViewModel : DialogViewModelBase
{
    public required ProjectType ProjectType { get; init; }
    public required string DefaultCategory { get; init; }
    public required string HeaderName { get; init; }
    /// <summary>One line when there are no Workshop versions to list ("Not published to the Workshop.").</summary>
    public string WorkshopSummary { get; init; } = "";
    public bool HasWorkshopSummary => WorkshopSummary.Length > 0;
    public IReadOnlyList<WorkshopVersionRow> WorkshopVersions { get; init; } = [];
    public bool HasWorkshopVersions => WorkshopVersions.Count > 0;
    public ObservableCollection<string> CategorySuggestions { get; } = [];

    /// <summary>Why Rename folder is unavailable (a build or upload is using the files), or null.</summary>
    public string? RenameBlockedReason { get; init; }
    public bool CanRenameFolder => RenameBlockedReason is null;

    /// <summary>Opens the rename dialog over Project info. Set by the owner.</summary>
    public Func<Task>? RenameFolderAsync { get; set; }

    /// <summary>Stores the edits; runs while the dialog stays open. Set by the owner.</summary>
    public Func<Task>? SaveAsync { get; set; }

    public string ProjectTypeLabel => ProjectType == ProjectType.Map ? "Map" : "Mod";
    public string Subtitle => $"{ProjectTypeLabel} · {ProjectName}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private string _projectName = "";

    [ObservableProperty] private string _folderPath = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _category = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string? _statusMessage;

    /// <summary>Display name and category go back to the project's own; Notes stay.</summary>
    public void RestoreDefaults()
    {
        DisplayName = "";
        Category = DefaultCategory;
        StatusMessage = null;
    }

    public void ApplyRename(string newName, string newFolderPath)
    {
        ProjectName = newName;
        FolderPath = newFolderPath;
        StatusMessage = $"Renamed the folder to {newName}.";
    }

    protected override string DescribeFailure(Exception ex) =>
        ErrorText.Describe("Couldn't save the project info.", ex);
}

/// <summary>A Workshop version in Project info: its name, whether it's the active one, and its item ID.</summary>
public sealed record WorkshopVersionRow(string Name, bool IsActive, string PublisherId)
{
    public bool IsPublished => PublisherId.Length > 0;
    public string IdText => IsPublished ? PublisherId : "Not published";
}
