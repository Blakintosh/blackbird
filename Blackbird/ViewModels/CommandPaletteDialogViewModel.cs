using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Blackbird.Models;

namespace Blackbird.ViewModels;

public partial class CommandPaletteDialogViewModel : ObservableObject
{
    private readonly List<ProjectItem> _allProjects;
    private readonly List<CommandPaletteAction> _actions;
    private readonly Dictionary<string, int> _recentActionOrder;
    private readonly HashSet<string> _recentSet;
    private readonly List<string> _recentOrder;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private CommandPaletteEntry? _selectedItem;

    // Swapped whole on every keystroke so the list sees one reset, not a remove/add per row.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private IReadOnlyList<CommandPaletteEntry> _items = [];

    public bool HasNoResults => Items.Count == 0;

    public CommandPaletteDialogViewModel(
        IEnumerable<ProjectItem> projects,
        IEnumerable<string> recentProjectKeys,
        IEnumerable<string>? recentActionKeys = null,
        IEnumerable<CommandPaletteAction>? actions = null)
    {
        _allProjects = projects.Where(p => !p.IsGroupHeader).ToList();
        _actions = actions?.ToList() ?? [];
        _recentActionOrder = (recentActionKeys ?? [])
            .Select((key, index) => (key, index))
            .Where(t => !string.IsNullOrWhiteSpace(t.key))
            .GroupBy(t => t.key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(t => t.index), StringComparer.OrdinalIgnoreCase);
        _recentOrder = recentProjectKeys.ToList();
        _recentSet = new HashSet<string>(_recentOrder, StringComparer.OrdinalIgnoreCase);

        Recompute();
    }

    partial void OnSearchQueryChanged(string value) => Recompute();

    /// <summary>Up and Down wrap around the ends; a page jump stops at them.</summary>
    public void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;

        var index = SelectedItem is null ? -1 : IndexOf(SelectedItem);
        index = Math.Abs(delta) == 1
            ? (index + delta + Items.Count) % Items.Count
            : Math.Clamp(index + delta, 0, Items.Count - 1);
        SelectedItem = Items[index];
    }


    private int IndexOf(CommandPaletteEntry entry)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (ReferenceEquals(Items[i], entry))
                return i;
        }

        return -1;
    }

    private void Recompute()
    {
        var query = (SearchQuery ?? "").Trim();
        var items = query.Length == 0 ? BrowseList() : SearchList(query);
        Items = items;
        SelectedItem = items.FirstOrDefault();
    }

    /// <summary>Nothing typed: recent and then likely actions, then recent, favorite and remaining projects.</summary>
    private List<CommandPaletteEntry> BrowseList()
    {
        var items = _actions
            .OrderBy(action => _recentActionOrder.TryGetValue(action.Key, out var recent) ? recent : action.Priority + 100)
            .ThenBy(action => action.Name, StringComparer.OrdinalIgnoreCase)
            .Select(BuildEntry)
            .ToList();

        var recents = _recentOrder
            .Select(key => _allProjects.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)))
            .OfType<ProjectItem>();
        items.AddRange(recents.Select(p => BuildEntry(p, isRecent: true)));

        items.AddRange(_allProjects
            .Where(p => !_recentSet.Contains(p.Key))
            .OrderByDescending(p => p.IsFavorite)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(p => BuildEntry(p, isRecent: false)));
        return items;
    }

    /// <summary>
    /// Typing: actions and projects in one list, best match first. A name that starts with the
    /// query beats one that merely contains it, and either beats a keyword or detail hit, so
    /// "castle" puts Castle Redux above any action that happens to mention castles.
    /// </summary>
    private List<CommandPaletteEntry> SearchList(string query)
    {
        var scored = new List<(int Score, int Tie, CommandPaletteEntry Entry)>();

        foreach (var action in _actions)
        {
            var score = Best(NameScore(action.Name, query), TextScore(action.Detail, query), TextScore(action.SearchKeywords, query));
            if (score >= 0)
                scored.Add((score, 1, BuildEntry(action)));
        }

        foreach (var project in _allProjects)
        {
            var score = Best(
                NameScore(project.DisplayName, query),
                NameScore(project.Name, query),
                TextScore(project.Category, query),
                TextScore(project.PublishedLabel, query),
                TextScore(project.Notes, query));
            if (score >= 0)
                scored.Add((score, 0, BuildEntry(project, _recentSet.Contains(project.Key))));
        }

        return scored
            .OrderBy(t => t.Score)
            .ThenBy(t => t.Tie)
            .ThenBy(t => t.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Entry)
            .ToList();
    }

    private static int Best(params int[] scores) => scores.Where(s => s >= 0).DefaultIfEmpty(-1).Min();

    /// <summary>Lower is better, -1 is no match: exact, then prefix, then a word start, then anywhere, then the letters in order.</summary>
    private static int NameScore(string? name, string query)
    {
        if (string.IsNullOrEmpty(name))
            return -1;

        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;

        var index = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index > 0)
            return IsWordStart(name, index) ? 2 : 10 + index;

        // The query's letters in order ("cr" for Castle Redux), scored by how tightly they sit.
        int from = 0, first = -1, last = -1;
        foreach (var ch in query)
        {
            var pos = name.IndexOf(ch.ToString(), from, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) return -1;
            if (first < 0) first = pos;
            last = pos;
            from = pos + 1;
        }

        return 200 + (last - first) + first;
    }

    /// <summary>Details and keywords match only where a word starts, never as scattered letters.</summary>
    private static int TextScore(string? text, string query)
    {
        if (string.IsNullOrEmpty(text))
            return -1;

        for (var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
             index >= 0;
             index = text.IndexOf(query, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (IsWordStart(text, index))
                return 50;
        }

        return -1;
    }

    private static bool IsWordStart(string text, int index) =>
        index == 0 || !char.IsLetterOrDigit(text[index - 1]);

    private static CommandPaletteEntry BuildEntry(ProjectItem project, bool isRecent) => new()
    {
        Project = project,
        ActionKey = "",
        Name = project.DisplayName,
        Detail = project.HasCustomDisplayName
            ? $"{project.Name} · {project.Category}"
            : project.Category,
        TypeLabel = project.TypeShortLabel,
        IsRecent = isRecent,
        IsFavorite = project.IsFavorite,
    };

    private static CommandPaletteEntry BuildEntry(CommandPaletteAction action) => new()
    {
        Project = null,
        ActionKey = action.Key,
        Name = action.Name,
        Detail = action.Detail,
        TypeLabel = action.TypeLabel.ToUpperInvariant(),
        Shortcut = action.Shortcut,
        IsRecent = false,
    };
}

public class CommandPaletteEntry
{
    public ProjectItem? Project { get; init; }
    public required string ActionKey { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
    /// <summary>MAP, MOD or TOOL; empty for built-in actions, which need no badge.</summary>
    public required string TypeLabel { get; init; }
    public bool HasTypeLabel => TypeLabel.Length > 0;
    /// <summary>The keyboard shortcut that runs the same action, shown as a key hint.</summary>
    public string Shortcut { get; init; } = "";
    public bool HasShortcut => Shortcut.Length > 0;
    public IReadOnlyList<string> ShortcutKeys => Shortcut.Split('+');

    /// <summary>What a screen reader announces for the row (a list item without a name reads its ToString).</summary>
    public override string ToString() => Detail.Length > 0 ? $"{Name}, {Detail}" : Name;
    public bool IsRecent { get; init; }
    public bool IsFavorite { get; init; }
    public bool HasStatusLabel => IsRecent || IsFavorite;
    public string StatusLabel => (IsRecent, IsFavorite) switch
    {
        (true, true) => "Recent · Favorite",
        (true, false) => "Recent",
        (false, true) => "Favorite",
        _ => ""
    };
}

public class CommandPaletteAction
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public string TypeLabel { get; init; } = "";
    public string Shortcut { get; init; } = "";
    public string SearchKeywords { get; init; } = "";
    public int Priority { get; init; } = 100;
}
