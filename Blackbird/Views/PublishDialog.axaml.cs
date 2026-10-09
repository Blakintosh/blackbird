using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Blackbird.Models;
using Blackbird.Services;
using Blackbird.ViewModels;

namespace Blackbird.Views;

public partial class PublishDialog : Window
{
    private PublishDialogViewModel? _viewModel;
    // Set once the user has agreed to stop an upload by closing the window.
    private bool _closeDuringUploadConfirmed;
    private bool _isConfirmingStop;
    private readonly DispatcherTimer _descriptionPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    // The thumbnail and the gallery take the same images: what Steam accepts.
    private static readonly IReadOnlyList<FilePickerFileType> ImageFileTypes =
    [
        new("Image files") { Patterns = [.. WorkshopFiles.ImageExtensions.Select(extension => "*" + extension)] },
        new("All files") { Patterns = ["*"] }
    ];

    private const string LinkUrlPlaceholder = "https://";

    public PublishDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Opened += (_, _) => this.FindControl<TextBox>("TitleTextBox")?.Focus();
        _descriptionPreviewTimer.Tick += (_, _) =>
        {
            _descriptionPreviewTimer.Stop();
            RenderDescriptionPreview(_viewModel?.Description);
        };

        // Tunnelling, so the formatting shortcuts reach us before the TextBox sees the keys.
        GetDescriptionBox()?.AddHandler(KeyDownEvent, OnDescriptionKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RenderDescriptionPreview(_viewModel?.Description);
    }

    /// <summary>
    /// Shrinks the opening height to the owner's screen so the dialog never opens taller
    /// than the work area (a 1080p laptop at 150 % has about 690 px). Call before showing.
    /// </summary>
    public void FitToScreenOf(Window owner)
    {
        if (owner.Screens.ScreenFromWindow(owner) is not { } screen)
            return;

        var available = screen.WorkingArea.Height / screen.Scaling - 48;
        if (Height > available)
            Height = Math.Max(MinHeight, available);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _viewModel is not { IsUploading: true } || _closeDuringUploadConfirmed)
            return;

        // Closing mid-upload is the same as Cancel: ask once, then stop.
        e.Cancel = true;
        if (await ConfirmStopUploadAsync())
        {
            _closeDuringUploadConfirmed = true;
            Close();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as PublishDialogViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            RenderDescriptionPreview(_viewModel.Description);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _descriptionPreviewTimer.Stop();
        if (_viewModel is not null)
        {
            // Closing from the caption bar skips OnSecondaryClick; don't drop a pending autosave.
            _viewModel.FlushLocalAutosave();
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.DisposePreviewResources();
            _viewModel = null;
        }

        base.OnClosed(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PublishDialogViewModel.Description)
            && sender is PublishDialogViewModel vm)
        {
            ScheduleDescriptionPreview(vm.Description);
        }
    }

    /// <summary>Rebuilds the preview once typing pauses; clearing the field applies immediately.</summary>
    private void ScheduleDescriptionPreview(string description)
    {
        _descriptionPreviewTimer.Stop();
        if (string.IsNullOrWhiteSpace(description))
            RenderDescriptionPreview(description);
        else
            _descriptionPreviewTimer.Start();
    }

    private async void OnBrowseThumbnail(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a thumbnail",
                AllowMultiple = false,
                FileTypeFilter = ImageFileTypes
            });

            if (files.Count > 0 && DataContext is PublishDialogViewModel vm)
                vm.Thumbnail = files[0].Path.LocalPath;
        }
        catch (Exception ex)
        {
            (DataContext as PublishDialogViewModel)?.ShowError(ErrorText.Describe("Couldn't open the file picker.", ex), ex.Message);
        }
    }

    private async void OnAddPreviewImages(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add gallery images",
                AllowMultiple = true,
                FileTypeFilter = ImageFileTypes
            });

            if (files.Count > 0 && DataContext is PublishDialogViewModel vm)
                vm.AddPreviewImages(files.Select(file => file.Path.LocalPath));
        }
        catch (Exception ex)
        {
            (DataContext as PublishDialogViewModel)?.ShowError(ErrorText.Describe("Couldn't open the file picker.", ex), ex.Message);
        }
    }

    private void OnRemovePreviewImage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PublishDialogViewModel vm)
            vm.RemoveSelectedPreviewImage();
    }

    private void OnOpenMediaFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PublishDialogViewModel { HasWorkshopMediaFolder: true } vm
            && ShellLauncher.TryOpen(vm.WorkshopMediaFolder) is { } error)
        {
            vm.ShowError("Couldn't open the Workshop media folder.", error);
        }
    }

    private async void OnNewProfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PublishDialogViewModel vm)
            return;

        var name = await PromptForVersionNameAsync(vm, "New Workshop version", "Create",
            vm.GetDefaultNewProfileName(), renaming: false,
            "A separate Workshop item, starting from a blank page.");
        if (name is not null)
            vm.CreateProfile(name);
    }

    private async void OnDuplicateProfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PublishDialogViewModel vm)
            return;

        var name = await PromptForVersionNameAsync(vm, "Duplicate Workshop version", "Duplicate",
            vm.GetDefaultDuplicateProfileName(), renaming: false,
            "A separate Workshop item that starts with this version's details.");
        if (name is not null)
            vm.DuplicateSelectedProfile(name);
    }

    private async void OnRenameProfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PublishDialogViewModel vm || vm.SelectedProfile is null)
            return;

        var name = await PromptForVersionNameAsync(vm, "Rename Workshop version", "Rename",
            vm.SelectedProfile.Name, renaming: true, subtitle: null);
        if (name is not null)
            vm.RenameSelectedProfile(name);
    }

    private async void OnDeleteProfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PublishDialogViewModel vm || vm.SelectedProfile is null || !vm.CanDeleteProfile)
            return;

        var confirmed = await MessageDialog.ConfirmAsync(
            this,
            "Delete Workshop version?",
            $"“{vm.SelectedProfile.Name}” and its saved details are removed from this project. Nothing is deleted from Steam Workshop.",
            "Delete",
            destructive: true);
        if (confirmed)
            vm.DeleteSelectedProfile();
    }

    private System.Threading.Tasks.Task<string?> PromptForVersionNameAsync(
        PublishDialogViewModel vm,
        string title,
        string confirmText,
        string initialName,
        bool renaming,
        string? subtitle) =>
        TextPromptDialog.ShowAsync(
            this,
            title,
            "Name",
            confirmText,
            name => vm.ValidateProfileNameMessage(name, renaming),
            initialName,
            subtitle,
            PublishDialogViewModel.WorkshopVersionNameMaxLength);

    private void OnPreviewImagesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || DataContext is not PublishDialogViewModel vm)
            return;

        vm.RemoveSelectedPreviewImage();
        e.Handled = true;
    }

    private void OnDescriptionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control)
            return;

        var tag = e.Key switch
        {
            Key.B => "b",
            Key.I => "i",
            Key.U => "u",
            _ => null,
        };
        if (tag is null)
            return;

        WrapSelection($"[{tag}]", $"[/{tag}]");
        e.Handled = true;
    }

    private void OnBoldClick(object? sender, RoutedEventArgs e) =>
        WrapSelection("[b]", "[/b]");

    private void OnItalicClick(object? sender, RoutedEventArgs e) =>
        WrapSelection("[i]", "[/i]");

    private void OnUnderlineClick(object? sender, RoutedEventArgs e) =>
        WrapSelection("[u]", "[/u]");

    private void OnHeadingClick(object? sender, RoutedEventArgs e) =>
        WrapSelection("[h1]", "[/h1]");

    // The URL placeholder comes up selected, so typing replaces it.
    private void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        var selected = GetSelectedDescriptionText();
        var text = string.IsNullOrWhiteSpace(selected) ? "link text" : selected;
        ReplaceSelection($"[url={LinkUrlPlaceholder}]{text}[/url]", "[url=".Length, LinkUrlPlaceholder.Length);
    }

    private void OnListClick(object? sender, RoutedEventArgs e) =>
        ReplaceSelection("[list]\n[*] item\n[/list]");

    private void OnQuoteClick(object? sender, RoutedEventArgs e) =>
        WrapSelection("[quote]", "[/quote]");

    private void OnCodeClick(object? sender, RoutedEventArgs e) =>
        WrapSelection("[code]", "[/code]");

    private async void OnCopyDescriptionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PublishDialogViewModel vm)
            await CopyTextToClipboardAsync(vm.Description, "description");
    }

    private async void OnCopyChangelogClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PublishDialogViewModel vm)
            await CopyTextToClipboardAsync(vm.Changelog, "changelog");
    }

    private async System.Threading.Tasks.Task CopyTextToClipboardAsync(string text, string label)
    {
        if (DataContext is not PublishDialogViewModel vm)
            return;

        if (string.IsNullOrWhiteSpace(text))
        {
            vm.ShowStatus($"The {label} is empty");
            return;
        }

        if (Clipboard is null)
        {
            vm.ShowError("The clipboard isn't available.");
            return;
        }

        try
        {
            await Clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // Another program holding the clipboard is common on Windows; say so instead of failing loudly.
            vm.ShowError("The clipboard is busy. Try Copy again.");
            return;
        }
        vm.ShowStatus($"Copied the {label}");
    }

    private TextBox? GetDescriptionBox() =>
        this.FindControl<TextBox>("DescriptionTextBox");

    private string GetSelectedDescriptionText()
    {
        var box = GetDescriptionBox();
        if (box?.Text is null)
            return "";

        var start = Math.Min(box.SelectionStart, box.SelectionEnd);
        var end = Math.Max(box.SelectionStart, box.SelectionEnd);
        return end > start ? box.Text[start..end] : "";
    }

    private void WrapSelection(string openTag, string closeTag)
    {
        var selected = GetSelectedDescriptionText();
        ReplaceSelection($"{openTag}{selected}{closeTag}", openTag.Length, selected.Length);
    }

    // The binding carries the new text to the view model.
    private void ReplaceSelection(string replacement, int selectionOffset = 0, int selectionLength = 0)
    {
        var box = GetDescriptionBox();
        if (box is null)
            return;

        var text = box.Text ?? "";
        var start = Math.Min(box.SelectionStart, box.SelectionEnd);
        var end = Math.Max(box.SelectionStart, box.SelectionEnd);
        box.Text = text[..start] + replacement + text[end..];
        box.SelectionStart = start + selectionOffset;
        box.SelectionEnd = box.SelectionStart + selectionLength;
        box.Focus();
    }

    // ── Description preview: the BBCode Steam renders on a Workshop page ──

    private sealed class PreviewState
    {
        // One entry per open [list] or [olist]: whether it's numbered, and the next number.
        public readonly Stack<(bool Ordered, int Next)> Lists = new();
        public bool InQuote;
        public bool InCode;
        public bool InNoparse;
    }

    private static readonly string[] BlockTags =
        ["[list]", "[/list]", "[olist]", "[/olist]", "[quote]", "[/quote]", "[code]", "[/code]",
         "[h1]", "[/h1]", "[h2]", "[/h2]", "[h3]", "[/h3]", "[hr]", "[/hr]"];

    private void RenderDescriptionPreview(string? description)
    {
        _descriptionPreviewTimer.Stop();
        var panel = this.FindControl<StackPanel>("DescriptionPreviewPanel");
        if (panel is null)
            return;

        panel.Children.Clear();

        if (string.IsNullOrWhiteSpace(description))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Preview appears here",
                TextWrapping = TextWrapping.Wrap,
                Foreground = GetBrush("ForegroundDimmed")
            });
            return;
        }

        var state = new PreviewState();
        foreach (var rawLine in description.Replace("\r\n", "\n").Split('\n'))
        {
            var control = BuildPreviewLine(rawLine, state);
            if (control is not null)
                panel.Children.Add(control);
        }
    }

    private Control? BuildPreviewLine(string rawLine, PreviewState state)
    {
        // Tags inside [noparse] are text; only what's outside it can open or close a block.
        var segments = SplitNoparse(rawLine, ref state.InNoparse);
        var markup = string.Concat(segments.Where(s => !s.Literal).Select(s => s.Text));
        var hasLiteral = segments.Any(s => s.Literal && s.Text.Length > 0);

        var heading = ContainsTag(markup, "[h1]") ? 1 : ContainsTag(markup, "[h2]") ? 2 : ContainsTag(markup, "[h3]") ? 3 : 0;
        var opensQuote = ContainsTag(markup, "[quote]");
        var closesQuote = ContainsTag(markup, "[/quote]");
        var opensCode = ContainsTag(markup, "[code]");
        var closesCode = ContainsTag(markup, "[/code]");
        var isQuoteLine = state.InQuote || opensQuote || closesQuote;
        var isCodeLine = state.InCode || opensCode || closesCode;

        if (!hasLiteral && IsOnly(markup, "[hr]", "[/hr]") && ContainsTag(markup, "[hr]"))
        {
            return new Border
            {
                Height = 1,
                Background = GetBrush("DividerStroke"),
                Margin = new Thickness(0, 6),
            };
        }

        // Lists open and close in the order their tags appear on the line.
        foreach (var tag in ListTagsIn(markup))
        {
            if (tag.Opens)
                state.Lists.Push((tag.Ordered, 1));
            else if (state.Lists.Count > 0)
                state.Lists.Pop();
        }

        if (opensQuote) state.InQuote = true;
        if (closesQuote) state.InQuote = false;
        if (opensCode) state.InCode = true;
        if (closesCode) state.InCode = false;

        if (!hasLiteral && !string.IsNullOrWhiteSpace(markup) && IsOnly(markup, BlockTags))
            return null;

        segments = [.. segments.Select(s => s.Literal ? s : (BlockTags.Aggregate(s.Text, RemoveTag), false))];

        var prefix = "";
        var first = segments.FindIndex(s => s.Text.Length > 0);
        if (first >= 0 && !segments[first].Literal
            && segments[first].Text.TrimStart().StartsWith("[*]", StringComparison.Ordinal))
        {
            segments[first] = (segments[first].Text.TrimStart()[3..].TrimStart(), false);
            if (state.Lists.TryPop(out var list))
            {
                prefix = list.Ordered ? $"{list.Next}. " : "• ";
                state.Lists.Push((list.Ordered, list.Next + 1));
            }
            else
            {
                prefix = "• ";
            }
        }

        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = heading switch { 1 => 18, 2 => 16, 3 => 14, _ => 13 },
            FontWeight = heading switch { 1 => FontWeight.Bold, 0 => FontWeight.Normal, _ => FontWeight.SemiBold },
            FontFamily = isCodeLine ? GetFont("MonoFont") : FontFamily.Default,
            Foreground = isQuoteLine ? GetBrush("ForegroundDimmed") : GetBrush("ForegroundPrimary"),
            Margin = new Thickness(12 * state.Lists.Count, heading > 0 ? 4 : 0, 0, heading > 0 ? 2 : 0)
        };

        var inlines = textBlock.Inlines ??= [];
        if (prefix.Length > 0)
            inlines.Add(new Run(prefix) { Foreground = GetBrush("ForegroundDimmed") });
        AddFormattedRuns(textBlock, segments.All(s => s.Text.Length == 0) ? [(" ", true)] : segments);

        if (isCodeLine)
        {
            return new Border
            {
                Background = GetBrush("LayerFill"),
                BorderBrush = GetBrush("BorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 4),
                Margin = new Thickness(0, 2),
                Child = textBlock
            };
        }

        if (isQuoteLine)
        {
            return new Border
            {
                BorderBrush = GetBrush("AccentBlue"),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(8, 2, 0, 2),
                Margin = new Thickness(0, 2),
                Child = textBlock
            };
        }

        return textBlock;
    }

    /// <summary>The line as (text, literal) pieces around [noparse]…[/noparse], which may span lines.</summary>
    private static List<(string Text, bool Literal)> SplitNoparse(string line, ref bool inNoparse)
    {
        var segments = new List<(string, bool)>();
        var position = 0;
        while (position <= line.Length)
        {
            var tag = inNoparse ? "[/noparse]" : "[noparse]";
            var at = line.IndexOf(tag, position, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                segments.Add((line[position..], inNoparse));
                break;
            }

            segments.Add((line[position..at], inNoparse));
            position = at + tag.Length;
            inNoparse = !inNoparse;
        }

        return segments;
    }

    private static IEnumerable<(bool Opens, bool Ordered)> ListTagsIn(string markup)
    {
        for (var i = markup.IndexOf('['); i >= 0; i = markup.IndexOf('[', i + 1))
        {
            var rest = markup.AsSpan(i);
            if (rest.StartsWith("[list]", StringComparison.OrdinalIgnoreCase)) yield return (true, false);
            else if (rest.StartsWith("[olist]", StringComparison.OrdinalIgnoreCase)) yield return (true, true);
            else if (rest.StartsWith("[/list]", StringComparison.OrdinalIgnoreCase)
                     || rest.StartsWith("[/olist]", StringComparison.OrdinalIgnoreCase)) yield return (false, false);
        }
    }

    /// <summary>True when <paramref name="text"/> holds nothing but whitespace and these tags.</summary>
    private static bool IsOnly(string text, params string[] tags) =>
        string.IsNullOrWhiteSpace(tags.Aggregate(text, RemoveTag));

    private void AddFormattedRuns(TextBlock textBlock, IEnumerable<(string Text, bool Literal)> segments)
    {
        var bold = false;
        var italic = false;
        var underline = false;
        var strike = false;
        var spoiler = false;
        var link = false;
        var buffer = new System.Text.StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0)
                return;

            var run = new Run(buffer.ToString());
            if (bold)
                run.FontWeight = FontWeight.Bold;
            if (italic)
                run.FontStyle = FontStyle.Italic;
            if (underline || link || strike)
            {
                var decorations = new TextDecorationCollection();
                if (underline || link)
                    decorations.AddRange(TextDecorations.Underline);
                if (strike)
                    decorations.AddRange(TextDecorations.Strikethrough);
                run.TextDecorations = decorations;
            }
            if (link)
                run.Foreground = GetBrush("AccentIndicator");
            if (spoiler)
            {
                run.Background = GetBrush("ControlFillHover");
                run.Foreground = GetBrush("ForegroundTertiary");
            }

            (textBlock.Inlines ??= []).Add(run);
            buffer.Clear();
        }

        foreach (var (text, literal) in segments)
        {
            if (literal)
            {
                buffer.Append(text);
                continue;
            }

            for (var i = 0; i < text.Length; i++)
            {
                if (TryReadTag(text, i, out var tag))
                {
                    var normalized = tag.ToLowerInvariant();
                    bool? set = normalized switch
                    {
                        "[b]" or "[i]" or "[u]" or "[strike]" or "[spoiler]" or "[url]" => true,
                        "[/b]" or "[/i]" or "[/u]" or "[/strike]" or "[/spoiler]" or "[/url]" => false,
                        _ when normalized.StartsWith("[url=", StringComparison.Ordinal) => true,
                        _ => null,
                    };

                    if (set is { } on)
                    {
                        Flush();
                        switch (normalized.TrimStart('[', '/').TrimEnd(']').Split('=')[0])
                        {
                            case "b": bold = on; break;
                            case "i": italic = on; break;
                            case "u": underline = on; break;
                            case "strike": strike = on; break;
                            case "spoiler": spoiler = on; break;
                            case "url": link = on; break;
                        }
                        i += tag.Length - 1;
                        continue;
                    }
                }

                buffer.Append(text[i]);
            }
        }

        Flush();
    }

    private static bool TryReadTag(string text, int start, out string tag)
    {
        tag = "";
        if (text[start] != '[')
            return false;

        var end = text.IndexOf(']', start);
        if (end <= start)
            return false;

        tag = text[start..(end + 1)];
        return true;
    }

    private static bool ContainsTag(string text, string tag) =>
        text.Contains(tag, StringComparison.OrdinalIgnoreCase);

    private static string RemoveTag(string text, string tag) =>
        text.Replace(tag, "", StringComparison.OrdinalIgnoreCase);

    private IBrush? GetBrush(string key) =>
        Application.Current?.FindResource(key) as IBrush;

    private FontFamily GetFont(string key) =>
        Application.Current?.FindResource(key) as FontFamily ?? FontFamily.Default;

    private async void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PublishDialogViewModel vm)
            return;

        // A thumbnail path typed a moment ago is checked before it can be published.
        vm.ApplyPendingThumbnail();
        if (!vm.IsPrimaryEnabled)
            return;

        if (vm.IsPublished)
        {
            if (ShellLauncher.TryOpen(vm.PrimaryUrl) is { } error)
                vm.ShowError(vm.NeedsLegalAgreement ? "Couldn't open the Workshop agreement." : "Couldn't open the Workshop page.", error);
            return;
        }

        if (vm.PublishHandler is null || !await ConfirmPublishAsync(vm))
            return;

        // The confirmation is modal, but a build may have started from the banner meanwhile.
        if (!vm.IsEditing || !vm.CanPublish || vm.IsPulling)
            return;

        vm.FlushLocalAutosave();
        await vm.PublishHandler();
    }

    /// <summary>
    /// Only going public is asked about: a private, friends-only or unlisted item can be
    /// changed or removed before anyone finds it, a public one can't be taken back.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> ConfirmPublishAsync(PublishDialogViewModel vm)
    {
        if (!vm.MakesItemPublic)
            return true;

        var title = string.IsNullOrWhiteSpace(vm.Title) ? "this project" : $"“{vm.Title.Trim()}”";
        var (heading, message) = vm.IsNewWorkshopItem
            ? ("Create a public Workshop item?",
                $"Blackbird creates a new item on Steam Workshop under your account and uploads {title} to it. "
                + "It's public straight away: anyone on Steam can find and subscribe to it.")
            : ("Make this item public?",
                $"After this update, anyone on Steam can find and subscribe to {title}.");

        return await MessageDialog.ConfirmAsync(this, heading, message, vm.PublishButtonText);
    }

    private async void OnSecondaryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PublishDialogViewModel vm)
        {
            Close();
            return;
        }

        if (vm.IsUploading)
        {
            await ConfirmStopUploadAsync();
            return;
        }

        // Edits autosave as they're made, so closing never discards anything.
        vm.FlushLocalAutosave();
        Close();
    }

    /// <summary>
    /// Asks once; true when the user chose to stop. The stop is only requested if the upload is
    /// still running by then, but a close that asked still goes ahead when it finished meanwhile.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> ConfirmStopUploadAsync()
    {
        if (_isConfirmingStop || _viewModel is not { IsUploading: true } vm)
            return false;

        _isConfirmingStop = true;
        try
        {
            var stop = await MessageDialog.ConfirmAsync(
                this,
                PublishDialogViewModel.StopUploadTitle,
                PublishDialogViewModel.StopUploadMessage,
                PublishDialogViewModel.StopUploadButton,
                destructive: true);

            if (!stop)
                return false;

            // The upload may have finished while the question was open: nothing left to stop.
            if (vm.IsUploading)
                vm.CancelUploadHandler?.Invoke();
            return true;
        }
        finally
        {
            _isConfirmingStop = false;
        }
    }

    private void OnBannerToggleClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PublishDialogViewModel { CanCollapseBanner: true } vm)
            vm.IsBannerExpanded = !vm.IsBannerExpanded;
    }

    // Every row action runs with the dialog open; the host refreshes the banner when it's done.
    private void OnPreflightActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PublishCheckAction action } && DataContext is PublishDialogViewModel vm)
            vm.OnCheckAction?.Invoke(action.Kind, action.Payload);
    }
}
