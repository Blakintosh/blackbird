using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Blackbird.Models;
using Blackbird.ViewModels;

namespace Blackbird.Views;

/// <summary>Log actions that need the window: the clipboard status line, a save picker, a confirmation.</summary>
public enum LogViewAction
{
    CopyLog,
    CopyErrors,
    CopyWarnings,
    ExportLog,
    Clear,
}

public partial class ColoredLogView : UserControl
{
    public static readonly StyledProperty<BuildLog?> LogProperty =
        AvaloniaProperty.Register<ColoredLogView, BuildLog?>(nameof(Log));
    public static readonly StyledProperty<bool> CanClearLogProperty =
        AvaloniaProperty.Register<ColoredLogView, bool>(nameof(CanClearLog), defaultValue: true);

    // ^1-^7 map to LogColor1-7 in Palette.axaml. The brushes are shared and re-tinted in place when the
    // theme changes, so spans already laid out follow it; each view then redraws.
    private static readonly Dictionary<char, SolidColorBrush> ColorMap = "1234567".ToDictionary(
        code => code,
        code => new SolidColorBrush(ThemeColor("LogColor" + code, Colors.White)));
    private static readonly SolidColorBrush PlaceholderBrush = new(ThemeColor("ForegroundDimmed", Colors.Gray));

    static ColoredLogView()
    {
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += (_, _) =>
            {
                foreach (var (code, brush) in ColorMap)
                    brush.Color = ThemeColor("LogColor" + code, Colors.White);
                PlaceholderBrush.Color = ThemeColor("ForegroundDimmed", Colors.Gray);
            };
    }

    private static Color ThemeColor(string key, Color fallback) =>
        Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out var value)
            && value is ISolidColorBrush brush
            ? brush.Color
            : fallback;

    private static readonly string NoMatchesText =
        "No lines match. Press Esc to show the whole log.";
    private static readonly string EmptyLogText =
        "No build output yet. Build with Ctrl+B and the output appears here.";

    // Logs this long are rebuilt off the UI thread into a fresh document that's swapped in whole.
    private const int BackgroundRebuildChars = 256 * 1024;

    private TextDocument _document = NewDocument();
    private List<LogColorSpan> _colorSpans = [];
    private readonly LogColorizer _colorizer;
    private int _rebuildVersion;
    private bool _isRebuildingInBackground;
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private readonly TextEditor? _editor;
    private readonly TextBox? _searchBox;
    private readonly TextBlock? _summaryText;
    private readonly Button? _clearSearchButton;
    private readonly Button? _errorsChip;
    private readonly Button? _warningsChip;
    private readonly TextBlock? _errorsChipText;
    private readonly TextBlock? _warningsChipText;
    private BuildLog? _log;
    private string _appliedQuery = "";
    private LogSeverityFilter _severityFilter = LogSeverityFilter.All;

    // What the document currently shows. Only complete lines are committed; the
    // trailing partial line is drawn provisionally and redrawn once it completes.
    private string? _placeholder;
    private bool _documentHasLines;
    private string _pendingLine = "";
    private int _pendingDisplayStart = -1;
    private int _pendingSpanStart;
    private LineInfo _pendingInfo;
    private int _totalLines;
    private int _visibleLines;
    private int _errorLines;
    private int _warningLines;

    private enum LogSeverityFilter
    {
        All,
        Errors,
        Warnings,
    }

    private readonly record struct LogColorSpan(int Start, int End, IBrush Brush);

    private readonly record struct LineInfo(bool IsError, bool IsWarning, bool IsVisible);

    /// <summary>What a run of raw output renders to, before it touches the document.</summary>
    private sealed class Rendered
    {
        public StringBuilder Output { get; } = new();
        public List<LogColorSpan> Spans { get; } = [];
        public int Lines, Visible, Errors, Warnings;
        public string PendingLine = "";
        public LineInfo PendingInfo;
        /// <summary>Where the provisional partial line starts in <see cref="Output"/> and <see cref="Spans"/>; -1 when none is on screen.</summary>
        public int PendingOutputStart = -1;
        public int PendingSpanStart;
    }

    public BuildLog? Log
    {
        get => GetValue(LogProperty);
        set => SetValue(LogProperty, value);
    }

    /// <summary>False while a build is writing the log; the menu's Clear log is disabled with the reason.</summary>
    public bool CanClearLog
    {
        get => GetValue(CanClearLogProperty);
        set => SetValue(CanClearLogProperty, value);
    }

    /// <summary>Copy, export and clear, from the log's menu or context menu.</summary>
    public event EventHandler<LogViewAction>? ActionRequested;

    public ColoredLogView()
    {
        _colorizer = new LogColorizer(() => _colorSpans);

        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Loaded += OnLoaded;
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplySearchQuery(_searchBox?.Text?.Trim() ?? "");
        };

        _editor = this.FindControl<TextEditor>("LogEditor");
        _searchBox = this.FindControl<TextBox>("SearchBox");
        _summaryText = this.FindControl<TextBlock>("FilterSummaryText");
        _clearSearchButton = this.FindControl<Button>("ClearSearchButton");
        _errorsChip = this.FindControl<Button>("ErrorsFilterButton");
        _warningsChip = this.FindControl<Button>("WarningsFilterButton");
        _errorsChipText = this.FindControl<TextBlock>("ErrorsChipText");
        _warningsChipText = this.FindControl<TextBlock>("WarningsChipText");
        if (_editor is not null)
        {
            _editor.Document = _document;
            _editor.Options.AllowScrollBelowDocument = false;
            _editor.Options.EnableVirtualSpace = false;
            _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
            // Line numbers in a padded gutter of their own, without the dotted rule AvaloniaEdit draws beside them.
            foreach (var margin in _editor.TextArea.LeftMargins.ToList())
            {
                if (margin is AvaloniaEdit.Editing.LineNumberMargin numbers)
                    numbers.Margin = new Thickness(4, 0, 16, 0);
                else if (margin is Avalonia.Controls.Shapes.Shape)
                    _editor.TextArea.LeftMargins.Remove(margin);
            }
            ActualThemeVariantChanged += (_, _) => _editor.TextArea.TextView.Redraw();
        }

        Rebuild(scrollToEnd: true);
        UpdateSeverityButtons();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ScrollToBottom();
    }

    /// <summary>Ctrl+F: puts the cursor in the find field, with any query selected. Never hides it.</summary>
    public void FocusSearch()
    {
        _searchBox?.Focus();
        _searchBox?.SelectAll();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LogProperty)
        {
            if (_log is not null)
            {
                _log.Appended -= OnLogAppended;
                _log.Replaced -= OnLogReplaced;
            }

            _log = change.GetNewValue<BuildLog?>();
            if (_log is not null)
            {
                _log.Appended += OnLogAppended;
                _log.Replaced += OnLogReplaced;
            }

            Rebuild(scrollToEnd: true);
        }
    }

    // A background rebuild picks up whatever arrived meanwhile when it lands.
    private void OnLogAppended(string chunk)
    {
        if (!_isRebuildingInBackground)
            AppendRaw(chunk);
    }

    private void OnLogReplaced() => Rebuild(scrollToEnd: true);

    // Esc anywhere in the log clears the search and the severity filter and hands focus
    // back to the log. Ctrl+F is the window's, so it also works from outside the log.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None)
            return;

        var searchHadFocus = _searchBox?.IsFocused == true;
        if (!IsFiltered() && string.IsNullOrEmpty(_searchBox?.Text) && !searchHadFocus)
            return;

        ResetFindState();
        _editor?.Focus();
        e.Handled = true;
    }

    private void OnSeverityFilterClick(object? sender, RoutedEventArgs e)
    {
        var picked = ReferenceEquals(sender, _errorsChip) ? LogSeverityFilter.Errors : LogSeverityFilter.Warnings;

        // A chip toggles: clicking the active one shows the whole log again.
        _severityFilter = _severityFilter == picked ? LogSeverityFilter.All : picked;
        UpdateSeverityButtons();
        Rebuild(scrollToEnd: false);
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateClearSearchButton();
        _searchDebounce.Stop();

        var query = _searchBox?.Text?.Trim() ?? "";
        if (query.Length == 0)
            ApplySearchQuery("");
        else
            _searchDebounce.Start();
    }

    private void ApplySearchQuery(string query)
    {
        if (string.Equals(query, _appliedQuery, StringComparison.Ordinal))
            return;

        _appliedQuery = query;
        Rebuild(scrollToEnd: false);
    }

    /// <summary>Empties the search box without the rebuild its TextChanged would trigger.</summary>
    private void ClearSearchTextSilently()
    {
        _searchDebounce.Stop();
        _appliedQuery = "";
        if (_searchBox is not null)
            _searchBox.Text = "";
    }

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        if (_searchBox is null) return;

        _searchBox.Text = "";
        _searchBox.Focus();
    }

    private void ResetFindState()
    {
        var wasFiltered = IsFiltered();
        ClearSearchTextSilently();
        _severityFilter = LogSeverityFilter.All;
        UpdateSeverityButtons();
        if (wasFiltered)
            Rebuild(scrollToEnd: false);
    }

    private void OnLogActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<LogViewAction>(tag, out var action))
            ActionRequested?.Invoke(this, action);
    }

    private void OnLogMenuOpening(object? sender, EventArgs e)
    {
        if (this.FindControl<MenuItem>("ClearLogMenuItem") is { } clear)
        {
            clear.IsEnabled = CanClearLog;
            ToolTip.SetTip(clear, CanClearLog ? null : "Wait for the build to finish.");
        }
    }

    /// <summary>
    /// Redraws the whole log, only for a new log or a filter change, never per append. A long log
    /// is rendered into a new document off the UI thread; until it lands, a new log shows blank
    /// and a filter change keeps the old view.
    /// </summary>
    private async void Rebuild(bool scrollToEnd)
    {
        var version = ++_rebuildVersion;
        var shouldAutoScroll = scrollToEnd || ShouldAutoScroll();
        var source = _log?.Text ?? "";
        var query = _appliedQuery;
        var filter = _severityFilter;

        if (source.Length < BackgroundRebuildChars)
        {
            _isRebuildingInBackground = false;
            ResetView(NewDocument());
            AppendRaw(source, autoScroll: shouldAutoScroll);
            return;
        }

        if (scrollToEnd)
            ResetView(NewDocument());

        _isRebuildingInBackground = true;
        var uiThread = Thread.CurrentThread;
        var (rendered, document) = await Task.Run(() =>
        {
            var result = Render(source, 0, documentHasLines: false, query, filter);
            var built = NewDocument(result.Output.ToString());
            built.SetOwnerThread(uiThread);
            return (result, built);
        });

        if (version != _rebuildVersion)
            return;

        _isRebuildingInBackground = false;
        ResetView(document);
        Commit(rendered, appended: false);

        // Output that streamed in while the document was being built.
        if (_log is { } log && log.Length > source.Length)
            AppendRaw(log.Text[source.Length..], autoScroll: shouldAutoScroll);
        else
            Finish(shouldAutoScroll);
    }

    private static TextDocument NewDocument(string text = "")
    {
        // The log only ever grows by appends; an undo history would just hold a copy of it.
        var document = new TextDocument(text);
        document.UndoStack.SizeLimit = 0;
        return document;
    }

    private void ResetView(TextDocument document)
    {
        _document = document;
        _colorSpans = [];
        if (_editor is not null)
            _editor.Document = document;

        _placeholder = null;
        _documentHasLines = false;
        _pendingLine = "";
        _pendingInfo = default;
        _pendingDisplayStart = -1;
        _totalLines = 0;
        _visibleLines = 0;
        _errorLines = 0;
        _warningLines = 0;
    }

    /// <summary>
    /// Shows a chunk of raw output. Cost depends only on the chunk: complete lines are
    /// filtered and appended, and a trailing partial line is held until it completes.
    /// </summary>
    private void AppendRaw(string chunk, bool? autoScroll = null)
    {
        var shouldAutoScroll = autoScroll ?? ShouldAutoScroll();
        var text = _pendingLine.Length == 0 ? chunk : _pendingLine + chunk;
        RemovePendingDisplay();

        var baseOffset = _placeholder is null ? _document.TextLength : 0;
        var rendered = Render(text, baseOffset, _documentHasLines, _appliedQuery, _severityFilter);
        if (rendered.Output.Length > 0)
        {
            if (_placeholder is not null)
            {
                _document.Text = rendered.Output.ToString();
                _colorSpans.Clear();
                _placeholder = null;
            }
            else
            {
                _document.Insert(_document.TextLength, rendered.Output.ToString());
            }
        }

        Commit(rendered, appended: true);
        Finish(shouldAutoScroll);
    }

    /// <summary>Takes a render's spans, counts and partial line; its text is already in the document.</summary>
    private void Commit(Rendered rendered, bool appended)
    {
        var spanBase = _colorSpans.Count;
        _colorSpans.AddRange(rendered.Spans);
        _totalLines += rendered.Lines;
        _visibleLines += rendered.Visible;
        _errorLines += rendered.Errors;
        _warningLines += rendered.Warnings;
        if (rendered.Visible > 0)
            _documentHasLines = true;

        _pendingLine = rendered.PendingLine;
        _pendingInfo = rendered.PendingInfo;
        _pendingDisplayStart = rendered.PendingOutputStart < 0
            ? -1
            : (appended ? _document.TextLength - rendered.Output.Length : 0) + rendered.PendingOutputStart;
        _pendingSpanStart = spanBase + rendered.PendingSpanStart;
    }

    private void Finish(bool autoScroll)
    {
        UpdatePlaceholder();
        UpdateSummary();
        RedrawEditor();
        if (autoScroll)
            ScrollToBottom();
    }

    /// <summary>
    /// Classifies and colours raw output without touching the view, so a long log can be rendered
    /// off the UI thread. Spans are offset by <paramref name="baseOffset"/>, where the output will land.
    /// </summary>
    private static Rendered Render(string text, int baseOffset, bool documentHasLines, string query, LogSeverityFilter filter)
    {
        var rendered = new Rendered();
        var lineStart = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\n' && c != '\r')
                continue;

            // A CR at the very end may be the first half of a CRLF split across chunks.
            if (c == '\r' && i == text.Length - 1)
                break;

            var info = RenderLine(text[lineStart..i], rendered, baseOffset, documentHasLines, query, filter);
            rendered.Lines++;
            if (info.IsError) rendered.Errors++;
            if (info.IsWarning) rendered.Warnings++;
            if (info.IsVisible) rendered.Visible++;

            if (c == '\r' && text[i + 1] == '\n')
                i++;
            lineStart = i + 1;
        }

        rendered.PendingLine = text[lineStart..];
        var partial = rendered.PendingLine.TrimEnd('\r');
        if (partial.Length > 0)
        {
            var outputStart = rendered.Output.Length;
            var spanStart = rendered.Spans.Count;
            rendered.PendingInfo = RenderLine(partial, rendered, baseOffset, documentHasLines, query, filter);
            if (rendered.PendingInfo.IsVisible)
            {
                rendered.PendingOutputStart = outputStart;
                rendered.PendingSpanStart = spanStart;
            }
        }

        return rendered;
    }

    /// <summary>Classifies one line and, when it passes the filters, renders it into <paramref name="rendered"/>.</summary>
    private static LineInfo RenderLine(string line, Rendered rendered, int baseOffset, bool documentHasLines, string query, LogSeverityFilter filter)
    {
        var (isError, isWarning) = GetLineSeverity(line);
        var isVisible = PassesSeverityFilter(filter, isError, isWarning)
                        && (query.Length == 0
                            || BuildLogCodes.Strip(line).Contains(query, StringComparison.OrdinalIgnoreCase));

        if (isVisible)
        {
            if (documentHasLines || rendered.Output.Length > 0)
                rendered.Output.Append('\n');

            AppendColoredLine(line, rendered.Output, rendered.Spans, baseOffset);
        }

        return new LineInfo(isError, isWarning, isVisible);
    }

    private void RemovePendingDisplay()
    {
        if (_pendingDisplayStart < 0)
            return;

        _document.Remove(_pendingDisplayStart, _document.TextLength - _pendingDisplayStart);
        _colorSpans.RemoveRange(_pendingSpanStart, _colorSpans.Count - _pendingSpanStart);
        _pendingDisplayStart = -1;
    }

    private void UpdatePlaceholder()
    {
        if (_visibleLines > 0 || _pendingDisplayStart >= 0)
            return;

        var placeholder = _log is not { Length: > 0 }
            ? _log?.IsLoading == true ? "" : EmptyLogText
            : IsFiltered() ? NoMatchesText : "";

        if (string.Equals(placeholder, _placeholder, StringComparison.Ordinal))
            return;

        _document.Text = placeholder;
        _colorSpans.Clear();
        if (placeholder.Length > 0)
            _colorSpans.Add(new LogColorSpan(0, placeholder.Length, PlaceholderBrush));
        _placeholder = placeholder;
    }

    /// <summary>Appends a line with BO3 ^N colour codes removed, recording a span per coloured run.</summary>
    private static void AppendColoredLine(string line, StringBuilder text, List<LogColorSpan> spans, int documentOffset)
    {
        IBrush? currentBrush = null;
        var spanStart = -1;

        void FinishSpan()
        {
            if (currentBrush is not null && spanStart >= 0 && text.Length > spanStart)
                spans.Add(new LogColorSpan(documentOffset + spanStart, documentOffset + text.Length, currentBrush));
        }

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '^' && i + 1 < line.Length && BuildLogCodes.IsColorCode(line[i + 1]))
            {
                // ^0, ^8 and ^9 have no log colour of their own: the text goes back to the default.
                FinishSpan();
                currentBrush = ColorMap.GetValueOrDefault(line[i + 1]);
                spanStart = text.Length;
                i++;
                continue;
            }

            text.Append(c);
        }

        FinishSpan();
    }

    private static bool PassesSeverityFilter(LogSeverityFilter filter, bool isError, bool isWarning) =>
        filter switch
        {
            LogSeverityFilter.Errors => isError,
            LogSeverityFilter.Warnings => isWarning,
            _ => true,
        };

    private bool IsFiltered() =>
        _severityFilter != LogSeverityFilter.All || _appliedQuery.Length > 0;

    private static (bool IsError, bool IsWarning) GetLineSeverity(string line) =>
        (BuildLogCodes.IsError(line), BuildLogCodes.IsWarning(line));

    private void UpdateSummary()
    {
        var hasPartial = _pendingLine.TrimEnd('\r').Length > 0;
        var total = _totalLines + (hasPartial ? 1 : 0);
        var visible = _visibleLines + (_pendingInfo.IsVisible ? 1 : 0);
        var errors = _errorLines + (_pendingInfo.IsError ? 1 : 0);
        var warnings = _warningLines + (_pendingInfo.IsWarning ? 1 : 0);

        if (_errorsChipText is not null)
            _errorsChipText.Text = FormatCount(errors, "error");
        if (_warningsChipText is not null)
            _warningsChipText.Text = FormatCount(warnings, "warning");

        // An empty chip can't filter to anything, unless it is the active filter
        // (so it can still be switched off).
        if (_errorsChip is not null)
            _errorsChip.IsEnabled = errors > 0 || _severityFilter == LogSeverityFilter.Errors;
        if (_warningsChip is not null)
            _warningsChip.IsEnabled = warnings > 0 || _severityFilter == LogSeverityFilter.Warnings;

        if (_summaryText is not null)
            _summaryText.Text = IsFiltered() && total > 0 ? $"{visible:N0} of {total:N0} lines" : "";
    }

    private static string FormatCount(int count, string label) =>
        $"{count:N0} {label}{(count == 1 ? "" : "s")}";

    private void UpdateClearSearchButton()
    {
        if (_clearSearchButton is not null)
            _clearSearchButton.IsVisible = !string.IsNullOrEmpty(_searchBox?.Text);
    }

    private void UpdateSeverityButtons()
    {
        SetChipActive(_errorsChip, _severityFilter == LogSeverityFilter.Errors);
        SetChipActive(_warningsChip, _severityFilter == LogSeverityFilter.Warnings);
    }

    private static void SetChipActive(Button? chip, bool isActive)
    {
        if (chip is null)
            return;

        chip.Classes.Set("active", isActive);
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        _editor?.SelectAll();
        _editor?.Focus();
    }

    private async void OnCopySelectionClick(object? sender, RoutedEventArgs e)
    {
        var text = _editor?.SelectedText;
        if (string.IsNullOrEmpty(text)) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is not { } clipboard)
            return;

        var status = topLevel.DataContext as MainWindowViewModel;
        try
        {
            await clipboard.SetTextAsync(text);
            status?.ShowTransientStatus("Copied the selection.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            status?.ShowTransientStatus("Couldn't copy. Another program may be holding the clipboard.");
        }

    }

    private void RedrawEditor()
    {
        _editor?.TextArea.TextView.Redraw();
    }

    private bool ShouldAutoScroll()
    {
        if (_editor is null)
            return true;

        var maxOffset = GetMaxVerticalOffset(_editor);
        if (maxOffset <= 0 || _editor.VerticalOffset >= maxOffset - 20)
            return true;

        // Output can outgrow the view before our own scroll lands; the user hasn't left the end unless they moved the offset.
        return _scrollToBottomPending || _editor.VerticalOffset == _lastAutoScrollOffset;
    }

    private bool _scrollToBottomPending;
    private double _lastAutoScrollOffset = double.NaN;

    /// <summary>
    /// Scrolls to the end once layout has caught up, and again after the pass that scroll triggers
    /// (the extent can still grow). However many chunks arrive meanwhile, one scroll is pending.
    /// </summary>
    private void ScrollToBottom()
    {
        if (_editor is null || _scrollToBottomPending)
            return;

        _scrollToBottomPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            ScrollToBottomNow();
            Dispatcher.UIThread.Post(() =>
            {
                _scrollToBottomPending = false;
                ScrollToBottomNow();
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    private void ScrollToBottomNow()
    {
        if (_editor is null)
            return;

        _editor.ScrollToVerticalOffset(GetMaxVerticalOffset(_editor));
        _lastAutoScrollOffset = _editor.VerticalOffset;
    }

    private static double GetMaxVerticalOffset(TextEditor editor) =>
        Math.Max(0, editor.ExtentHeight - editor.ViewportHeight);


    /// <summary>Reads the view's current spans on each line, since a rebuild swaps the list in whole.</summary>
    private sealed class LogColorizer(Func<List<LogColorSpan>> currentSpans) : DocumentColorizingTransformer
    {
        private List<LogColorSpan> spans = [];

        protected override void ColorizeLine(DocumentLine line)
        {
            spans = currentSpans();
            if (spans.Count == 0)
                return;

            var lineStart = line.Offset;
            var lineEnd = line.EndOffset;
            var spanIndex = FindFirstIntersectingSpan(lineStart);

            for (var i = spanIndex; i < spans.Count; i++)
            {
                var span = spans[i];
                if (span.Start >= lineEnd)
                    break;

                if (span.End <= lineStart)
                    continue;

                ChangeLinePart(
                    Math.Max(span.Start, lineStart),
                    Math.Min(span.End, lineEnd),
                    element => element.TextRunProperties.SetForegroundBrush(span.Brush));
            }
        }

        private int FindFirstIntersectingSpan(int offset)
        {
            var low = 0;
            var high = spans.Count;

            while (low < high)
            {
                var mid = low + ((high - low) / 2);
                if (spans[mid].End <= offset)
                    low = mid + 1;
                else
                    high = mid;
            }

            return low;
        }
    }
}
