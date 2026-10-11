using System.Collections.Generic;
using Blackbird.Models;
using Blackbird.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackbird.ViewModels;

/// <summary>
/// The Components view: the Mod Tools apps and parts installed for this Black Ops III install, what each unlocks,
/// and whether stock assets can be used directly. Read-only; the only actions are product links for now.
/// </summary>
public sealed partial class ComponentsDialogViewModel : ObservableObject
{
    public ComponentsDialogViewModel(ComponentsReport report) => Report = report;

    public ComponentsReport Report { get; }
    public IReadOnlyList<ComponentRow> Rows => Report.Rows;
    public string InstallPath => Report.InstallPath;

    /// <summary>The banner's tone: green while stock assets can be used directly, amber while they can't.</summary>
    public bool UseDirectly => Report.UseDirectly;
    public bool UseDirectlyOff => !Report.UseDirectly;
    public string UseDirectlyText => Report.UseDirectlyText;
    public string UseDirectlyReason => Report.UseDirectlyReason;

    [ObservableProperty] private string? _errorMessage;

    // TODO(installer): when Blackbird can run the Stock Library / Dragon / Ultrasound installers, an install or
    // update goes here instead of opening the product page. Rows already carry an id to key it on.
    [RelayCommand]
    private void OpenAction(ComponentRow? row)
    {
        if (row?.ActionUrl is not { } url)
            return;
        ErrorMessage = ShellLauncher.TryOpen(url) is { } why ? $"Couldn't open {url}: {why}" : null;
    }
}
