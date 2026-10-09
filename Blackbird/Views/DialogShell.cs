using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Blackbird.Views;

/// <summary>
/// The one dialog anatomy: header (title and optional subtitle), body, and a footer
/// with status on the left and buttons on the right. The look lives in the
/// DialogShell ControlTheme in Styles/Theme.axaml; windows that use it carry
/// <c>Classes="dialog"</c> so the system title bar gives way to this header.
/// </summary>
public class DialogShell : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DialogShell, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<DialogShell, string?>(nameof(Subtitle));

    public static readonly StyledProperty<object?> FooterProperty =
        AvaloniaProperty.Register<DialogShell, object?>(nameof(Footer));

    public static readonly StyledProperty<object?> StatusProperty =
        AvaloniaProperty.Register<DialogShell, object?>(nameof(Status));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>Buttons, right-aligned: primary first, then Cancel.</summary>
    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    /// <summary>Inline status or error, left of the buttons.</summary>
    public object? Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    private Border? _dragBar;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_dragBar is not null)
            _dragBar.PointerPressed -= OnDragBarPointerPressed;

        _dragBar = e.NameScope.Find<Border>("PART_DragBar");
        if (_dragBar is not null)
            _dragBar.PointerPressed += OnDragBarPointerPressed;
    }

    private void OnDragBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window
            && e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
        {
            window.BeginMoveDrag(e);
        }
    }
}
