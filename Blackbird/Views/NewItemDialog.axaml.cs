using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Blackbird.ViewModels;

namespace Blackbird.Views;

public partial class NewItemDialog : Window
{
    public NewItemDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<TextBox>("NameBox")?.Focus();
    }

    private NewItemDialogViewModel? ViewModel => DataContext as NewItemDialogViewModel;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The files are half-written while Create runs; the dialog stays until it ends.
        if (ViewModel?.IsBusy == true)
            e.Cancel = true;
        base.OnClosing(e);
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e) => ViewModel?.RevealNameError();

    private void OnPrefixFixClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        ViewModel.ApplyPrefixFix();
        if (this.FindControl<TextBox>("NameBox") is { } box)
        {
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        }
    }

    private async void OnOpenSetupDoctorClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.OpenSetupDoctorAsync is { } open)
            ViewModel.ReplaceTemplates(await open(this));
    }

    private async void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { IsBusy: false } vm || vm.CreateAsync is null)
            return;

        if (!vm.ValidateAll())
        {
            if (vm.NameError is not null)
                this.FindControl<TextBox>("NameBox")?.Focus();
            return;
        }

        if (await vm.RunBusyAsync(() => vm.CreateAsync(vm)))
            Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
