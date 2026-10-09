using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Blackbird.Models;
using Blackbird.Services;

namespace Blackbird.ViewModels;

public partial class DuplicateProjectDialogViewModel : DialogViewModelBase
{
    public required string SourceName { get; init; }
    public required string SourceDisplayName { get; init; }
    public required ProjectType ProjectType { get; init; }
    public required string DefaultCategory { get; init; }
    public IReadOnlyList<ProjectItem> ExistingProjects { get; init; } = [];

    /// <summary>Why the map source can't be copied to the new name (another map's .map is there); null when it can.</summary>
    public Func<string, string?>? MapSourceConflict { get; init; }
    public ObservableCollection<string> CategorySuggestions { get; } = [];

    /// <summary>Copies the project; runs while the dialog stays open. Set by the owner.</summary>
    public Func<DuplicateProjectDialogViewModel, Task>? DuplicateAsync { get; set; }

    public string Subtitle => $"Copies {SourceName}. Generated XPaks are skipped and Workshop IDs are cleared.";

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _category = "";
    [ObservableProperty] private string? _nameError;

    private bool _showNameError;

    public void SeedDefaults()
    {
        if (!string.IsNullOrWhiteSpace(NewName))
            return;

        NewName = BuildSuggestedName(SourceName);
        DisplayName = SourceDisplayName == SourceName
            ? ""
            : $"{SourceDisplayName} copy";
        Category = DefaultCategory;
    }

    public void RevealNameError()
    {
        _showNameError = true;
        RefreshNameError();
    }

    public bool ValidateAll()
    {
        RevealNameError();
        return NameError is null;
    }

    partial void OnNewNameChanged(string value) => RefreshNameError();

    private void RefreshNameError()
    {
        NameError = _showNameError
            ? ProjectNameRules.Validate(
                NewName.Trim(),
                ProjectType,
                ProjectNameRules.MapPrefixOf(SourceName),
                ExistingProjects)
              ?? MapSourceConflict?.Invoke(NewName.Trim())
            : null;
    }

    protected override string DescribeFailure(Exception ex) =>
        ErrorText.Describe("Couldn't duplicate the project.", ex);

    private static string BuildSuggestedName(string sourceName) =>
        sourceName.EndsWith("_copy", StringComparison.OrdinalIgnoreCase)
            ? sourceName + "2"
            : sourceName + "_copy";
}
