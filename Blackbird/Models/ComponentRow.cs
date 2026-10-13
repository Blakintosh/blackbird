using System.Collections.Generic;
using Blackbird.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blackbird.Models;

public enum ComponentState
{
    /// <summary>Installed and doing its job.</summary>
    Ready,

    /// <summary>Installed or partly there, but something it needs is missing or old.</summary>
    Attention,

    /// <summary>Not installed.</summary>
    Missing,
}

/// <summary>One line of the Components view: a Mod Tools app or part, what state it is in, and what it unlocks.</summary>
public sealed partial class ComponentRow : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ComponentState State { get; init; }

    /// <summary>The state in a few words, with the version when known ("0.1.0 · index ready").</summary>
    public required string StateText { get; init; }

    /// <summary>What it unlocks, or what is needed to get it working.</summary>
    public required string Detail { get; init; }

    /// <summary>The link on the right ("Get MTPlugins"); installing from inside Blackbird comes later.</summary>
    public string? ActionLabel { get; init; }
    public string? ActionUrl { get; init; }

    /// <summary>A newer release of this component, when Blackbird found one (Dragon and the Stock Library). Set when the view opens and as a check finishes.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUpdate), nameof(HasAction))]
    private ComponentUpdate? _update;

    public bool HasUpdate => Update is not null;

    /// <summary>The row's Update button: the update is downloaded and nothing is working on it.</summary>
    public bool CanInstallUpdate => Update?.CanInstallNow == true;

    partial void OnUpdateChanging(ComponentUpdate? value)
    {
        if (Update is { } old)
            old.PropertyChanged -= OnUpdateItemChanged;
    }

    partial void OnUpdateChanged(ComponentUpdate? value)
    {
        if (value is not null)
            value.PropertyChanged += OnUpdateItemChanged;
        OnPropertyChanged(nameof(CanInstallUpdate));
    }

    private void OnUpdateItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComponentUpdate.State))
            OnPropertyChanged(nameof(CanInstallUpdate));
    }

    public bool IsReady => State == ComponentState.Ready;
    public bool NeedsAttention => State == ComponentState.Attention;
    public bool IsMissing => State == ComponentState.Missing;

    /// <summary>The "Get" link: only for something that isn't installed, and never beside an Update button.</summary>
    public bool HasAction => ActionLabel is not null && ActionUrl is not null && !HasUpdate;
}

/// <summary>The Components view's content for one install.</summary>
public sealed class ComponentsReport
{
    public required string InstallPath { get; init; }
    public required IReadOnlyList<ComponentRow> Rows { get; init; }

    /// <summary>Whether Blackbird can link stock assets by name, with no local copies.</summary>
    public required bool UseDirectly { get; init; }

    /// <summary>Why <see cref="UseDirectly"/> is what it is, in one sentence.</summary>
    public required string UseDirectlyReason { get; init; }

    public string UseDirectlyText => UseDirectly ? "Use directly is on" : "Use directly is off";
}
