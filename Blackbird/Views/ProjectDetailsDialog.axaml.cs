using Avalonia.Controls;
using Avalonia.Interactivity;
using Blackbird.ViewModels;

namespace Blackbird.Views;

public partial class ProjectDetailsDialog : Window
{
    public ProjectDetailsDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (this.FindControl<TextBox>("DisplayNameBox") is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        };
    }

    private ProjectDetailsDialogViewModel? ViewModel => DataContext as ProjectDetailsDialogViewModel;

    private void OnRestoreDefaultsClick(object? sender, RoutedEventArgs e) => ViewModel?.RestoreDefaults();

    private async void OnRenameFolderClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.RenameFolderAsync is { } rename)
            await rename();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (ViewModel?.IsBusy == true)
            e.Cancel = true;
        base.OnClosing(e);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { IsBusy: false } vm || vm.SaveAsync is null)
            return;

        if (await vm.RunBusyAsync(vm.SaveAsync))
            Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
