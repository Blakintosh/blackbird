using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Blackbird.ViewModels;

namespace Blackbird.Views;

public partial class CommandPaletteDialog : Window
{
    public CommandPaletteDialog()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            this.FindControl<TextBox>("SearchBox")?.Focus();
        };

        KeyDown += OnGlobalKeyDown;

        // Like a flyout: clicking back into the main window dismisses it.
        Deactivated += (_, _) =>
        {
            if (!_isClosing)
                Close(null);
        };
        Closing += (_, _) => _isClosing = true;
    }

    private const int PageSize = 8;
    private bool _isClosing;


    private CommandPaletteDialogViewModel? Vm => DataContext as CommandPaletteDialogViewModel;

    // Esc and Enter work wherever focus is, including after Tab into the list.
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ConfirmSelection();
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null) return;

        switch (e.Key)
        {
            case Key.Down:
                Vm.MoveSelection(1);
                ScrollSelectionIntoView();
                e.Handled = true;
                break;
            case Key.Up:
                Vm.MoveSelection(-1);
                ScrollSelectionIntoView();
                e.Handled = true;
                break;
            case Key.PageDown:
                Vm.MoveSelection(PageSize);
                ScrollSelectionIntoView();
                e.Handled = true;
                break;
            case Key.PageUp:
                Vm.MoveSelection(-PageSize);
                ScrollSelectionIntoView();
                e.Handled = true;
                break;
        }
    }

    // One click runs a row; clicks on the scroll bar or padding don't.
    private void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)
            is { DataContext: CommandPaletteEntry entry })
        {
            Close(entry);
        }
    }

    private void ConfirmSelection()
    {
        var picked = Vm?.SelectedItem;
        if (picked is null) return;
        Close(picked);
    }

    private void ScrollSelectionIntoView()
    {
        if (Vm?.SelectedItem is { } entry
            && this.FindControl<ListBox>("ResultsList") is { } list)
        {
            list.ScrollIntoView(entry);
        }
    }
}
