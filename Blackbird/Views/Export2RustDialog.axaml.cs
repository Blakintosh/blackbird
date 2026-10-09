using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Blackbird.Services;
using Blackbird.ViewModels;

namespace Blackbird.Views;

public partial class Export2RustDialog : Window
{
    private static readonly IReadOnlyList<FilePickerFileType> InputFileTypes =
    [
        new("All files") { Patterns = ["*"] }
    ];

    public Export2RustDialog()
    {
        InitializeComponent();

        DropZone.AddHandler(DragDrop.DragOverEvent, OnDropZoneDragOver);
        DropZone.AddHandler(DragDrop.DropEvent, OnDropZoneDrop);

        // A read-only log still takes Enter for caret movement; it must not start an export.
        OutputBox.AddHandler(KeyDownEvent, OnOutputKeyDown, RoutingStrategies.Tunnel);
        JobsList.KeyDown += OnJobsListKeyDown;

        Opened += (_, _) => AddFilesButton.Focus();
    }

    private Export2RustDialogViewModel? ViewModel => DataContext as Export2RustDialogViewModel;

    // Closing while an export runs does what Stop does: unfinished files stay queued as
    // stopped and the exported ones stay exported, so nothing is lost and nothing is asked.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && ViewModel is { IsRunning: true } vm)
            vm.CancelCommand.Execute(null);
    }

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add files to export",
                AllowMultiple = true,
                FileTypeFilter = InputFileTypes
            });

            AddFiles(files.Select(file => file.Path.LocalPath));
        }
        catch (Exception ex)
        {
            ViewModel?.ReportProblem(ErrorText.Describe("Couldn't open the file picker.", ex));
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnOutputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            e.Handled = true;
    }

    private void OnJobsListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && ViewModel is { CanRemoveSelectedJob: true } vm)
        {
            vm.RemoveSelectedJobCommand.Execute(null);
            e.Handled = true;
        }
    }

#pragma warning disable CS0618 // DragDrop API deprecation
    private void OnDropZoneDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) && ViewModel is { IsRunning: false }
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropZoneDrop(object? sender, DragEventArgs e)
    {
        var paths = e.Data.GetFiles()?.Select(file => file.Path.LocalPath);

        if (paths is not null)
            AddFiles(paths);

        e.Handled = true;
    }
#pragma warning restore CS0618

    private async void AddFiles(IEnumerable<string> paths)
    {
        if (ViewModel is { } vm)
            await vm.AddFilesAsync(paths);
    }
}
