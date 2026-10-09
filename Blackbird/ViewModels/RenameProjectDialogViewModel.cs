using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Blackbird.Models;
using Blackbird.Services;

namespace Blackbird.ViewModels;

/// <summary>Renames a project's folder, and the name wherever it appears inside it.</summary>
public partial class RenameProjectDialogViewModel : DialogViewModelBase
{
    public required string CurrentName { get; init; }
    public required ProjectType ProjectType { get; init; }
    public required string FolderPath { get; init; }
    public IReadOnlyList<ProjectItem> ExistingProjects { get; init; } = [];

    /// <summary>Why the map source can't take the new name (another map's .map is there); null when it can.</summary>
    public Func<string, string?>? MapSourceConflict { get; init; }

    /// <summary>Does the rename; runs while the dialog stays open. Set by the owner.</summary>
    public Func<string, Task>? RenameAsync { get; set; }

    public string Title => $"Rename {CurrentName}";

    public string Explanation =>
        $"Blackbird renames the folder, then replaces “{CurrentName}” with the new name in file names and text files "
        + $"wherever it appears as a whole name. Longer names that only start with it, like “{CurrentName}2”, are left alone. "
        + "Settings, logs, map source and Workshop metadata move with it.";

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string? _nameError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenameToolTip))]
    private bool _isUnchanged = true;

    /// <summary>Why Rename is disabled; null when it isn't.</summary>
    public string? RenameToolTip => IsUnchanged ? "Enter a different name." : null;

    private bool _showNameError;

    public string TrimmedName => NewName.Trim();

    public void RevealNameError()
    {
        _showNameError = true;
        RefreshNameError();
    }

    public bool ValidateAll()
    {
        RevealNameError();
        return NameError is null && !IsUnchanged;
    }

    partial void OnNewNameChanged(string value)
    {
        IsUnchanged = string.Equals(TrimmedName, CurrentName, StringComparison.Ordinal);
        RefreshNameError();
    }

    private void RefreshNameError()
    {
        if (!_showNameError || IsUnchanged)
        {
            NameError = null;
            return;
        }

        NameError = ProjectNameRules.Validate(
                TrimmedName,
                ProjectType,
                ProjectNameRules.MapPrefixOf(CurrentName),
                ExistingProjects,
                CurrentName)
            ?? MapSourceConflict?.Invoke(TrimmedName);
    }

    protected override string DescribeFailure(Exception ex) =>
        ErrorText.Describe("Couldn't rename the project.", ex);
}
