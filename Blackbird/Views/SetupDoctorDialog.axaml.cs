using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Blackbird.Services;
using Blackbird.ViewModels;

namespace Blackbird.Views;

/// <summary>
/// Setup Doctor. The owner hands it a view model already loaded off the UI thread; checks,
/// detection and saving all run in <see cref="SetupDoctorViewModel"/>, off the UI thread.
/// </summary>
public partial class SetupDoctorDialog : Window
{
    private SetupDoctorViewModel? ViewModel => DataContext as SetupDoctorViewModel;

    public SetupDoctorDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<TextBox>("GamePathBox")?.Focus();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // A save in flight finishes first, so the settings file is never left half-decided.
        if (ViewModel?.IsBusy == true)
            e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel?.Stop();
        base.OnClosed(e);
    }

    private async void OnBrowseGameClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && await PickFolderAsync("Choose the Black Ops III folder") is { } path)
            await vm.UseFoldersAsync(gamePath: path);
    }

    private async void OnBrowseToolsClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && await PickFolderAsync("Choose the Mod Tools folder") is { } path)
            await vm.UseFoldersAsync(toolsPath: path);
    }

    private async void OnDetectClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.DetectAsync();
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { IsBusy: false } vm && await vm.SaveAsync())
            Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>The chosen folder, or null when cancelled; a picker failure lands in the footer.</summary>
    private async Task<string?> PickFolderAsync(string title)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });

            return folders.FirstOrDefault()?.Path.LocalPath;
        }
        catch (Exception ex)
        {
            ViewModel?.ReportProblem(ErrorText.Describe("Couldn't open the folder picker.", ex), ex);
            return null;
        }
    }
}
