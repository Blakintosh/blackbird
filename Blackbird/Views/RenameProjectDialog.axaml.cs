using Avalonia.Controls;
using Avalonia.Interactivity;
using Blackbird.ViewModels;

namespace Blackbird.Views;

/// <summary>
/// Renames a project folder. The explanation in the dialog is the one confirmation:
/// pressing Rename is the decision, so no second prompt follows.
/// </summary>
public partial class RenameProjectDialog : Window
{
    public RenameProjectDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (this.FindControl<TextBox>("NameBox") is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        };
    }

    private RenameProjectDialogViewModel? ViewModel => DataContext as RenameProjectDialogViewModel;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Stopping halfway would leave some files renamed and others not.
        if (ViewModel?.IsBusy == true)
            e.Cancel = true;
        base.OnClosing(e);
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e) => ViewModel?.RevealNameError();

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { IsBusy: false } vm || vm.RenameAsync is null)
            return;

        if (!vm.ValidateAll())
        {
            this.FindControl<TextBox>("NameBox")?.Focus();
            return;
        }

        if (await vm.RunBusyAsync(() => vm.RenameAsync(vm.TrimmedName)))
            Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
