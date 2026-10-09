using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Blackbird.Services;
using Blackbird.ViewModels;

namespace Blackbird.Views;

public partial class FirstRunDialog : Window
{
    public FirstRunDialog()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (ViewModel is null)
                return;

            await ViewModel.DetectAsync();
            FocusNextStep();
        };
    }

    /// <summary>Opens Setup Doctor over this sheet; true if the user saved there.</summary>
    public Func<Window, Task<bool>>? OpenSetupDoctorAsync { get; init; }

    private FirstRunViewModel? ViewModel => DataContext as FirstRunViewModel;

    /// <summary>Focus on the one next step: Continue, Check again, or Choose folder.</summary>
    private void FocusNextStep()
    {
        var next = ViewModel switch
        {
            { IsReady: true } => "ContinueButton",
            { IsToolsMissing: true } => "CheckAgainButton",
            { IsNotFound: true } => "ChooseFolderButton",
            _ => null,
        };
        if (next is not null)
            this.FindControl<Button>(next)?.Focus();
    }

    private async void OnChangeClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose the Black Ops III folder",
                AllowMultiple = false,
            });

            if (folders.FirstOrDefault()?.Path.LocalPath is { } folder)
            {
                await ViewModel.UseFolderAsync(folder);
                FocusNextStep();
            }
        }
        catch (Exception ex)
        {
            ViewModel.ReportProblem(ErrorText.Describe("Couldn't open the folder picker.", ex));
        }
    }

    private async void OnRecheckClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        await ViewModel.RecheckAsync();
        FocusNextStep();
    }

    private async void OnSetupDoctorClick(object? sender, RoutedEventArgs e)
    {
        // Setup Doctor saves the paths and refreshes the project list itself, so
        // this sheet closes without asking its owner to do either again.
        if (OpenSetupDoctorAsync is not null && await OpenSetupDoctorAsync(this))
            Close(false);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (ViewModel?.IsBusy == true)
            e.Cancel = true;
        base.OnClosing(e);
    }

    private async void OnContinueClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { IsReady: true, IsBusy: false } vm && await vm.SaveAsync())
            Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
