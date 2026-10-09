using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Blackbird.Models;
using Blackbird.ViewModels;

namespace Blackbird.Views;

/// <summary>The current launch config's run options and dvars. Choosing Dev/Ship, online and language lives in the launch config menu.</summary>
public partial class DvarsDialog : Window
{
    public DvarsDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            var first = ViewModel?.IsProjectScoped == true
                ? this.FindControl<TextBox>("RunOptionsBox")
                : this.FindControl<TextBox>("SearchBox");
            first?.Focus();
        };
    }

    private DvarsDialogViewModel? ViewModel => DataContext as DvarsDialogViewModel;

    // Esc clears a search before it cancels the dialog.
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || string.IsNullOrEmpty(ViewModel?.DvarSearchText))
            return;

        ViewModel.DvarSearchText = "";
        e.Handled = true;
    }

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.DvarSearchText = "";
        this.FindControl<TextBox>("SearchBox")?.Focus();
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Dvar dvar })
            ViewModel?.Remove(dvar);
    }

    // Cancel undoes this, so it doesn't ask first.
    private void OnResetClick(object? sender, RoutedEventArgs e) => ViewModel?.Reset();

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        // Values commit on lost focus; Enter saves without leaving the field, so move focus first.
        (sender as Button)?.Focus();
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
