using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Blackbird.Models;

namespace Blackbird.ViewModels;

/// <summary>
/// One launch config's dvars. With no search the list shows only the dvars this config sets;
/// searching reaches the whole catalog, and anything given a value there joins the set
/// list when the search clears.
/// </summary>
public partial class DvarsDialogViewModel : ObservableObject
{
    private readonly List<Dvar> _allDvars = [];
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private Dictionary<string, string> _resetValues = [];

    // Swapped whole on every change so the list sees one reset instead of a
    // remove/add per row. Rows are the shared Dvar objects, so edits survive filtering.
    [ObservableProperty] private IReadOnlyList<Dvar> _dvars = [];

    /// <summary>The launch config these apply to ("Castle Redux · Dev"), shown under the title.</summary>
    public string ScopeLabel { get; init; } = "";

    /// <summary>False when no project is selected: only the defaults are editable.</summary>
    public bool IsProjectScoped { get; init; }

    /// <summary>"Reset to Dev defaults", "Reset to Ship defaults" or "Clear all".</summary>
    public string ResetLabel { get; private set; } = "Clear all";

    [ObservableProperty] private string _runOptions = "";
    [ObservableProperty] private string _dvarSearchText = "";
    [ObservableProperty] private string _emptyMessage = "";
    [ObservableProperty] private bool _isEmpty;

    public string SearchWatermark => $"Search {_allDvars.Count:N0} dvars to add or change one";

    private bool IsSearching => !string.IsNullOrWhiteSpace(DvarSearchText);

    public DvarsDialogViewModel()
    {
        _searchTimer.Tick += (_, _) => RebuildVisibleDvars();
    }

    /// <param name="resetValues">What Reset returns the config to.</param>
    /// <param name="resetLabel">The Reset button's label; null means "Clear all".</param>
    public void Load(Dictionary<string, string> savedValues, Dictionary<string, string> resetValues, string? resetLabel)
    {
        _resetValues = resetValues;
        ResetLabel = resetLabel ?? "Clear all";

        _allDvars.Clear();
        foreach (var def in Dvar.Defaults)
        {
            var dvar = new Dvar(def.Name, def.Description, def.Type, def.MinValue, def.MaxValue, def.IsCmd, def.Kind);
            if (savedValues.TryGetValue(def.Name, out var saved))
                dvar.ValueText = saved;
            else
                dvar.Clear();
            _allDvars.Add(dvar);
        }

        OnPropertyChanged(nameof(SearchWatermark));
        RebuildVisibleDvars();
    }

    partial void OnDvarSearchTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            RebuildVisibleDvars();
            return;
        }

        _searchTimer.Stop();
        _searchTimer.Start();
    }

    public void Reset()
    {
        foreach (var dvar in _allDvars)
        {
            if (_resetValues.TryGetValue(dvar.Name, out var value))
                dvar.ValueText = value;
            else
                dvar.Clear();
        }

        DvarSearchText = "";
        RebuildVisibleDvars();
    }

    /// <summary>Unsets a dvar. In the set list its row goes; in search results it stays, cleared.</summary>
    public void Remove(Dvar dvar)
    {
        dvar.Clear();
        if (!IsSearching)
            RebuildVisibleDvars();
    }

    public Dictionary<string, string> GetValues() =>
        _allDvars.Where(d => d.IsSet).ToDictionary(d => d.Name, d => d.ValueText);

    public List<string> BuildRunDvars() => Dvar.BuildCommandArgs(GetValues());

    private void RebuildVisibleDvars()
    {
        _searchTimer.Stop();
        var query = DvarSearchText.Trim();

        Dvars = query.Length == 0
            ? _allDvars.Where(d => d.IsSet).ToList()
            : _allDvars.Where(d =>
                d.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        IsEmpty = Dvars.Count == 0;
        EmptyMessage = query.Length == 0
            ? "No dvars set. Search above to add one."
            : $"No dvars match “{query}”.";
    }
}
