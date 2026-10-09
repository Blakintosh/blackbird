using Avalonia.Controls;
using Avalonia.Interactivity;
using Blackbird.ViewModels;

namespace Blackbird.Views;

public partial class DuplicateProjectDialog : Window
{
    public DuplicateProjectDialog()
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

    private DuplicateProjectDialogViewModel? ViewModel => DataContext as DuplicateProjectDialogViewModel;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // A half-copied folder is worse than waiting; the dialog stays until the copy ends.
        if (ViewModel?.IsBusy == true)
            e.Cancel = true;
        base.OnClosing(e);
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e) => ViewModel?.RevealNameError();

    private async void OnDuplicateClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { IsBusy: false } vm || vm.DuplicateAsync is null)
            return;

        if (!vm.ValidateAll())
        {
            this.FindControl<TextBox>("NameBox")?.Focus();
            return;
        }

        if (await vm.RunBusyAsync(() => vm.DuplicateAsync(vm)))
            Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
