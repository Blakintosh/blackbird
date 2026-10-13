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
    public ComponentsDialogViewModel(ComponentsReport report, ComponentUpdater? updates = null)
    {
        Report = report;
        Updates = updates;
        if (updates is not null)
        {
            updates.PropertyChanged += OnUpdatesChanged;
            ApplyUpdates();
        }
    }

    /// <summary>Dragon and Stock Library updates; null in a view with no updater.</summary>
    public ComponentUpdater? Updates { get; }

    /// <summary>Stops listening to the updater; the view calls it when it closes.</summary>
    public void Detach()
    {
        if (Updates is not null)
            Updates.PropertyChanged -= OnUpdatesChanged;
    }

    private void OnUpdatesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComponentUpdater.Items) or nameof(ComponentUpdater.Phase))
            ApplyUpdates();
        if (e.PropertyName is nameof(ComponentUpdater.StatusText) or nameof(ComponentUpdater.Phase))
            OnPropertyChanged(nameof(UpdateStatus));
    }

    private void ApplyUpdates()
    {
        foreach (var row in Report.Rows)
            row.Update = Updates?.Find(row.Id);
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(HasUpdateStatus));
    }

    /// <summary>How the last Dragon and Stock Library check went, or what the update is doing; empty when there is nothing to say.</summary>
    public string UpdateStatus => Updates is null ? "" : Updates.Phase == ComponentPhase.Checking ? "Checking for updates…" : Updates.StatusText;
    public bool HasUpdateStatus => UpdateStatus.Length > 0;

    [RelayCommand]
    private void InstallUpdate(ComponentRow? row)
    {
        if (row?.Update is { } update && Updates is not null)
            _ = Updates.InstallAsync(update.Id);
    }

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
