using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Blackbird.Views;

/// <summary>The Components view. Everything it shows was read before it opened.</summary>
public partial class ComponentsDialog : Window
{
    public ComponentsDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<Button>("CloseButton")?.Focus();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
