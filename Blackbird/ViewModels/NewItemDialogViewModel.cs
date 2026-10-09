using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Blackbird.Models;
using Blackbird.Services;

namespace Blackbird.ViewModels;

public partial class NewItemDialogViewModel : DialogViewModelBase
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _category = "Maps";
    [ObservableProperty] private int _selectedProjectKindIndex;
    [ObservableProperty] private int _selectedTemplateIndex;
    [ObservableProperty] private bool _createCoreZone = true;
    [ObservableProperty] private bool _createZombiesZone = true;
    [ObservableProperty] private bool _createMultiplayerZone;
    [ObservableProperty] private bool _createCampaignZone;
    [ObservableProperty] private string? _nameError;
    [ObservableProperty] private string? _zoneError;

    private bool _showNameError;

    public ObservableCollection<string> ProjectKinds { get; } = ["Map", "Mod"];
    public BulkObservableCollection<string> Templates { get; } = [];
    public ObservableCollection<ProjectItem> ExistingProjects { get; } = [];
    public ObservableCollection<string> CategorySuggestions { get; } = [];

    /// <summary>Where map templates are looked for, named when there are none.</summary>
    public string TemplatesFolder { get; init; } = "";

    /// <summary>Creates the project; runs while the dialog stays open. Set by the owner.</summary>
    public Func<NewItemDialogViewModel, Task>? CreateAsync { get; set; }

    /// <summary>Opens Setup Doctor over the dialog and returns the templates found afterwards.</summary>
    public Func<Window, Task<IReadOnlyList<string>>>? OpenSetupDoctorAsync { get; set; }

    public void ReplaceTemplates(IReadOnlyList<string> templates)
    {
        Templates.ReplaceAll(templates);

        var zombies = templates.ToList().FindIndex(t => ProjectNameRules.MapPrefixForTemplate(t) == "zm_");
        SelectedTemplateIndex = zombies >= 0 ? zombies : 0;
        NotifyTemplatesLoaded();
        RefreshNameError();
    }

    public bool IsMapMode => SelectedProjectKindIndex == 0;
    public bool IsModMode => !IsMapMode;
    public bool HasTemplates => Templates.Count > 0;
    public bool IsTemplateMissing => IsMapMode && !HasTemplates;
    public bool ShowTemplatePicker => IsMapMode && HasTemplates;
    public string TrimmedName => Name.Trim();
    public ProjectType ProjectType => IsMapMode ? ProjectType.Map : ProjectType.Mod;
    public bool CanCreate => !IsTemplateMissing;

    public string NameWatermark => IsModMode
        ? "my_mod"
        : (SelectedTemplatePrefix ?? "zm_") + "my_map";

    public string NameHint => IsModMode
        ? "Lowercase letters, numbers and underscores."
        : $"Maps from this template start with {SelectedTemplatePrefix ?? "zm_ or mp_"}.";

    private string? SelectedTemplatePrefix =>
        SelectedTemplateIndex >= 0 && SelectedTemplateIndex < Templates.Count
            ? ProjectNameRules.MapPrefixForTemplate(Templates[SelectedTemplateIndex])
            : null;

    /// <summary>The name with the template's prefix added or swapped in, when it's missing.</summary>
    public string? PrefixedName
    {
        get
        {
            var name = TrimmedName;
            if (!IsMapMode || SelectedTemplatePrefix is not { } prefix || name.Length == 0)
                return null;
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;

            var existing = ProjectNameRules.MapPrefixOf(name);
            return prefix + (existing is null ? name : name[existing.Length..]);
        }
    }

    public bool HasPrefixFix => PrefixedName is not null && NameError is not null;
    public string PrefixFixLabel => PrefixedName is { } fixedName ? $"Use {fixedName}" : "";

    public void ApplyPrefixFix()
    {
        if (PrefixedName is { } fixedName)
            Name = fixedName;
    }

    /// <summary>Called when the name field loses focus: from here on errors show live.</summary>
    public void RevealNameError()
    {
        if (Name.Length == 0)
            return;

        _showNameError = true;
        RefreshNameError();
    }

    /// <summary>Checks everything and shows every error. True when the form can be submitted.</summary>
    public bool ValidateAll()
    {
        _showNameError = true;
        RefreshNameError();
        RefreshZoneError(show: true);
        return CanCreate && NameError is null && ZoneError is null;
    }

    private string? ComputeNameError() => ProjectNameRules.Validate(
        TrimmedName,
        ProjectType,
        IsMapMode ? SelectedTemplatePrefix : null,
        ExistingProjects);

    private void RefreshNameError()
    {
        NameError = _showNameError ? ComputeNameError() : null;
        OnPropertyChanged(nameof(HasPrefixFix));
        OnPropertyChanged(nameof(PrefixFixLabel));
    }

    private void RefreshZoneError(bool show)
    {
        if (!show && ZoneError is null)
            return;

        ZoneError = IsModMode && GetSelectedModZoneNames().Length == 0
            ? "Choose at least one zone."
            : null;
    }

    public string[] GetSelectedModZoneNames()
    {
        return
        [
            .. new[]
            {
                (CreateCoreZone, "core_mod"),
                (CreateZombiesZone, "zm_mod"),
                (CreateMultiplayerZone, "mp_mod"),
                (CreateCampaignZone, "cp_mod"),
            }
            .Where(zone => zone.Item1)
            .Select(zone => zone.Item2)
        ];
    }

    public void NotifyTemplatesLoaded()
    {
        OnPropertyChanged(nameof(HasTemplates));
        OnPropertyChanged(nameof(IsTemplateMissing));
        OnPropertyChanged(nameof(ShowTemplatePicker));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(NameWatermark));
        OnPropertyChanged(nameof(NameHint));
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(TrimmedName));
        RefreshNameError();
    }

    partial void OnCreateCoreZoneChanged(bool value) => RefreshZoneError(show: false);
    partial void OnCreateZombiesZoneChanged(bool value) => RefreshZoneError(show: false);
    partial void OnCreateMultiplayerZoneChanged(bool value) => RefreshZoneError(show: false);
    partial void OnCreateCampaignZoneChanged(bool value) => RefreshZoneError(show: false);

    partial void OnSelectedTemplateIndexChanged(int value)
    {
        OnPropertyChanged(nameof(NameWatermark));
        OnPropertyChanged(nameof(NameHint));
        RefreshNameError();
    }

    partial void OnSelectedProjectKindIndexChanged(int value)
    {
        var oldDefault = IsMapMode ? "Mods" : "Maps";
        if (string.IsNullOrWhiteSpace(Category)
            || string.Equals(Category, oldDefault, StringComparison.OrdinalIgnoreCase))
        {
            Category = IsMapMode ? "Maps" : "Mods";
        }

        OnPropertyChanged(nameof(IsMapMode));
        OnPropertyChanged(nameof(IsModMode));
        OnPropertyChanged(nameof(IsTemplateMissing));
        OnPropertyChanged(nameof(ShowTemplatePicker));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(NameWatermark));
        OnPropertyChanged(nameof(NameHint));
        RefreshNameError();
        RefreshZoneError(show: false);
    }

    protected override string DescribeFailure(Exception ex) => ex is UserMessageException
        ? $"Couldn't create the project. {ex.Message}"
        : ErrorText.Describe("Couldn't create the project files.", ex);
}
