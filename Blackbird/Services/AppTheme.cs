using System;
using Avalonia;
using Avalonia.Styling;

namespace Blackbird.Services;

/// <summary>The theme the user picked. <see cref="System"/> follows the Windows light/dark setting, live.</summary>
public enum ThemeChoice
{
    System,
    Graphite,
    Slate,
    Light,
}

/// <summary>
/// The three themes in Styles/Palette.axaml and how a <see cref="ThemeChoice"/> maps onto them. Graphite is the Dark
/// variant and Light the Light one, so following Windows needs nothing here; Slate is its own variant that inherits
/// Dark, so stock Fluent and AvaloniaEdit resources fall back to their dark values.
/// </summary>
public static class AppTheme
{
    public static readonly ThemeVariant Slate = new("Slate", ThemeVariant.Dark);

    public static ThemeVariant VariantOf(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Graphite => ThemeVariant.Dark,
        ThemeChoice.Slate => Slate,
        ThemeChoice.Light => ThemeVariant.Light,
        _ => ThemeVariant.Default,
    };

    public static ThemeChoice Parse(string? value) =>
        Enum.TryParse<ThemeChoice>(value, true, out var choice) ? choice : ThemeChoice.System;

    public static void Apply(ThemeChoice choice)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = VariantOf(choice);
    }
}
