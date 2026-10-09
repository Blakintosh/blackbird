using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Blackbird.Models;
using Blackbird.Services;
using Blackbird.ViewModels;

namespace Blackbird.Views;

public partial class ProjectAnalysisDialog : Window
{
    public ProjectAnalysisDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<TextBox>("SearchBox")?.Focus();
    }

    private ProjectAnalysisDialogViewModel? ViewModel => DataContext as ProjectAnalysisDialogViewModel;

    private void OnOpenIssueClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProjectAnalysisIssue issue })
            return;

        var path = issue.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (File.Exists(path) && TryOpenIssueInVsCode(issue))
            return;

        var target = File.Exists(path) || Directory.Exists(path)
            ? path
            : Path.GetDirectoryName(path);

        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target) && !File.Exists(target))
            return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception)
        {
            // No handler for this file type; nothing useful to add over Explorer's own error.
        }
    }

    private static bool TryOpenIssueInVsCode(ProjectAnalysisIssue issue)
    {
        try
        {
            var arguments = issue.LineNumber > 0
                ? $"--goto \"{issue.FilePath}:{issue.LineNumber}\""
                : $"\"{issue.FilePath}\"";

            return Process.Start(new ProcessStartInfo
            {
                FileName = "code",
                Arguments = arguments,
                UseShellExecute = true,
            }) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async void OnCopyReportClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is null || ViewModel is not { } vm)
            return;

        try
        {
            await Clipboard.SetTextAsync(vm.ReportText);
            vm.ReportCopied();
        }
        catch (Exception ex)
        {
            vm.FooterStatus = ErrorText.Describe("Couldn't copy the report.", ex);
        }
    }

    // No confirmation: the cleanup sends each original GDT to the Recycle Bin before
    // writing the cleaned copy, so it can be undone from there.
    private async void OnCleanGdtDuplicatesClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.CleanGdtDuplicatesAsync();
    }

    private async void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.RetryAsync();
    }

    private void OnClearFiltersClick(object? sender, RoutedEventArgs e) => ViewModel?.ClearFilters();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // Esc clears a search before it closes the window.
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || string.IsNullOrEmpty(ViewModel?.IssueSearchText))
            return;

        ViewModel.IssueSearchText = "";
        e.Handled = true;
    }

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.IssueSearchText = "";
        this.FindControl<TextBox>("SearchBox")?.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel?.CancelAnalysis();
        base.OnClosed(e);
    }

    private bool _closeAfterCleanup;

    // A cleanup in flight finishes first, so no GDT is left cleaned with its original not yet
    // in the Recycle Bin. The window closes once it's done.
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || ViewModel is not { IsCleaning: true } vm)
            return;

        e.Cancel = true;
        if (_closeAfterCleanup)
            return;

        _closeAfterCleanup = true;
        vm.FooterStatus = "Closing once the cleanup finishes…";
        await vm.CleanupCompletion;
        Close();
    }
}
