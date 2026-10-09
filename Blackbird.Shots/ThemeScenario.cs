using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Blackbird.Services;
using Blackbird.ViewModels;
using static Blackbird.Shots.Program;

namespace Blackbird.Shots;

/// <summary>
/// The three themes: the palette's dictionaries agree on their keys, File > Theme switches live with real clicks and
/// is remembered, and each theme's text, status and log colours clear their contrast floors on the card.
/// </summary>
internal static class ThemeScenario
{
    public static void Run(MainWindow main, MainWindowViewModel vm, ISettingsService settings)
    {
        var start = vm.Theme;
        PaletteKeysMatch();

        foreach (var (choice, header, card) in new[]
                 {
                     (ThemeChoice.Slate, "_Slate", "#111720"),
                     (ThemeChoice.Light, "_Light", "#FFFFFF"),
                     (ThemeChoice.Graphite, "_Graphite", "#1C1C1C"),
                 })
        {
            Pick(main, header);
            var layer = Token(main, "LayerFill");
            Check($"theme: File > Theme > {header.TrimStart('_')} applies it live (variant {main.ActualThemeVariant}, card {layer})",
                Application.Current!.RequestedThemeVariant == AppTheme.VariantOf(choice) && layer == Color.Parse(card));
            Check($"theme: {choice} is remembered ({settings.Theme}) and the menu marks it",
                settings.Theme == choice.ToString() && vm.Theme == choice
                && ThemeItems(main).Single(i => i.IsChecked).Header as string == header);
            Contrast(main, choice);
            Pump(200);
            Shot(main, $"theme-{choice.ToString().ToLowerInvariant()}");
        }

        Pick(main, "_Match Windows");
        Check($"theme: Match Windows hands the choice back to Windows ({Application.Current!.RequestedThemeVariant}, saved {settings.Theme})",
            Application.Current.RequestedThemeVariant == ThemeVariant.Default && settings.Theme == nameof(ThemeChoice.System));

        // The rest of the run shoots in the theme it started in.
        vm.SetThemeCommand.Execute(start);
        Pump(100);
    }

    /// <summary>Layer 2 of Palette.axaml is repeated per theme; a key missing from one would fall through to Fluent's stock value.</summary>
    private static void PaletteKeysMatch()
    {
        // The XAML compiler inlines the include, so it's either a ResourceInclude or the dictionary itself.
        var palette = Application.Current!.Resources.MergedDictionaries
            .Select(d => d is ResourceInclude i ? i.Loaded : d)
            .OfType<ResourceDictionary>()
            .First(d => d.ThemeDictionaries.Count > 0);
        var themes = palette.ThemeDictionaries.ToDictionary(t => t.Key.ToString()!, t => ((ResourceDictionary)t.Value).Keys.Select(k => k.ToString()!).ToHashSet());
        var all = themes.Values.SelectMany(k => k).ToHashSet();
        var missing = themes.SelectMany(t => all.Except(t.Value).Select(k => $"{t.Key} lacks {k}")).ToList();
        Check($"theme: the palette's {themes.Count} themes define the same {all.Count} keys ({(missing.Count == 0 ? "they do" : string.Join(", ", missing.Take(6)))})",
            themes.Count == 3 && missing.Count == 0);
    }

    private static MenuItem FileMenu(MainWindow main) =>
        Find<MenuItem>(main).First(m => m.Header as string == "_File");

    private static MenuItem ThemeMenu(MainWindow main) =>
        FileMenu(main).Items.OfType<MenuItem>().First(m => m.Header as string == "_Theme");

    private static List<MenuItem> ThemeItems(MainWindow main) => ThemeMenu(main).Items.OfType<MenuItem>().ToList();

    /// <summary>File, then Theme, then the item: three real clicks, each in the popup that holds it.</summary>
    private static void Pick(MainWindow main, string header)
    {
        Click(FileMenu(main));
        Pump(60);
        Click(ThemeMenu(main));
        Pump(60);
        Click(ThemeItems(main).First(i => i.Header as string == header));
        Pump(60);
        if (FileMenu(main).IsSubMenuOpen)
            FileMenu(main).Close();
        Pump();
    }

    private static Color? Token(Control anchor, string key) =>
        anchor.TryFindResource(key, anchor.ActualThemeVariant, out var v) && v is ISolidColorBrush b ? b.Color : null;

    private static void Contrast(Control anchor, ThemeChoice choice)
    {
        var pairs = new List<(string Fg, string Bg, double Min)>
        {
            ("ForegroundPrimary", "LayerFill", 7),
            ("ForegroundPrimary", "SidebarBackground", 7),
            ("ForegroundDimmed", "LayerFill", 4.5),
            ("ForegroundTertiary", "LayerFill", 4.5),
            ("ForegroundTertiary", "SidebarBackground", 4.5),
            ("ErrorRed", "LayerFill", 4.5),
            ("WarningYellow", "LayerFill", 4.5),
            ("SuccessGreen", "LayerFill", 4.5),
            ("TextOnAccent", "AccentBlue", 4.5),
            ("TextOnDanger", "DangerFill", 4.5),
            ("AccentIndicator", "LayerFill", 3),
        };
        for (var i = 1; i <= 7; i++)
            pairs.Add(($"LogColor{i}", "LayerFill", 4.5));
        var low = pairs.Select(p => (p, ratio: Ratio(Token(anchor, p.Fg), Token(anchor, p.Bg))))
            .Where(x => x.ratio < x.p.Min)
            .Select(x => $"{x.p.Fg} on {x.p.Bg} {x.ratio:0.0}:1 < {x.p.Min}")
            .ToList();
        Check($"theme: {choice} text, status and log colours clear their contrast floors ({(low.Count == 0 ? "all" : string.Join("; ", low))})",
            low.Count == 0);
    }

    private static double Ratio(Color? a, Color? b)
    {
        if (a is not { } fg || b is not { } bg)
            return 0;
        static double Lum(Color c)
        {
            static double Ch(byte v)
            {
                var s = v / 255.0;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        var (l1, l2) = (Lum(fg), Lum(bg));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
