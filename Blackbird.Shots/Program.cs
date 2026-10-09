using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Blackbird.Services;

namespace Blackbird.Shots;

/// <summary>
/// Renders Blackbird's real windows headlessly to PNGs over a fake Black Ops III install,
/// with fake Steam and fake builds, and drives a few interactions with real input.
/// Run: dotnet run -c Release --project Blackbird.Shots  (PNGs land in Blackbird.Shots/shots).
/// </summary>
public static partial class Program
{
    internal static readonly FakeSteam Steam = new();
    internal static readonly FakeBuild Build = new();

    private static string _outDir = "";
    private static int _uiThreadId;
    private static int _failures;

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public static async Task<int> Main(string[] args)
    {
        _outDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(ProjectFolder(), "shots"));
        if (Directory.Exists(_outDir))
            Directory.Delete(_outDir, recursive: true);
        Directory.CreateDirectory(_outDir);

        FakeInstall.Create();

        // Before anything reads AppPaths: settings, logs and icons go to the fake data folder.
        Environment.SetEnvironmentVariable("BLACKBIRD_DATA_DIR", FakeInstall.DataPath);
        Environment.SetEnvironmentVariable("TA_GAME_PATH", FakeInstall.GamePath);
        Environment.SetEnvironmentVariable("TA_TOOLS_PATH", FakeInstall.GamePath);
        App.ServiceOverrides = services =>
        {
            services.AddSingleton<ISteamWorkshopService>(Steam);
            services.AddSingleton<IBuildService>(Build);
            services.AddSingleton(UpdateScenario.CreateUpdater());
            services.AddSingleton<IFileSystemService>(sp =>
                new HarnessFileSystem(new FileSystemService(sp.GetRequiredService<ISettingsService>())).Register());
        };

        using var session = HeadlessUnitTestSession.StartNew(typeof(Program));
        await session.Dispatch(() =>
        {
            _uiThreadId = Environment.CurrentManagedThreadId;
            try
            {
                Scenarios.Run();
            }
            catch (Exception ex)
            {
                Check($"harness: ran to the end ({ex.GetType().Name}: {ex.Message})", false);
                Console.WriteLine(ex);
            }
        }, CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"Screenshots: {_outDir}");
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        Environment.Exit(_failures == 0 ? 0 : 1); // open windows keep the headless dispatcher alive
        return 0;
    }

    private static string ProjectFolder()
    {
        // bin/<config>/<tfm>/<rid>/ → the project folder, so shots land beside the source.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Blackbird.Shots.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    internal static int UiThreadId => _uiThreadId;

    // ── Output ──────────────────────────────────────────────────────────────

    internal static void Check(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}");
        if (!ok)
            _failures++;
    }

    /// <summary>Saves what the window last rendered, at 1x.</summary>
    internal static void Shot(TopLevel window, string name)
    {
        Pump(60);
        var frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            Check($"shot: {name} rendered", false);
            return;
        }

        var path = Path.Combine(_outDir, name + ".png");
        frame.Save(path);
        Console.WriteLine($"wrote {path}");
    }

    /// <summary>Renders the window's visual tree again at 1.5x (144 DPI), as on a 150 % display.</summary>
    internal static void Shot150(TopLevel window, string name)
    {
        Pump(60);
        const double scale = 1.5;
        var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale), (int)Math.Ceiling(window.Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        var path = Path.Combine(_outDir, name + ".png");
        bitmap.Save(path);
        Console.WriteLine($"wrote {path}");
    }

    // ── Dispatcher ──────────────────────────────────────────────────────────

    /// <summary>Runs pending work; with <paramref name="ms"/>, keeps the dispatcher (and its timers) running that long.</summary>
    internal static void Pump(int ms = 0)
    {
        Dispatcher.UIThread.RunJobs();
        if (ms <= 0)
            return;
        var frame = new DispatcherFrame();
        DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(ms));
        Dispatcher.UIThread.PushFrame(frame);
    }

    /// <summary>Pumps until <paramref name="done"/> holds, up to <paramref name="timeoutMs"/>. Returns whether it did.</summary>
    internal static bool WaitFor(Func<bool> done, int timeoutMs = 4000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            if (Environment.TickCount64 > deadline)
                return false;
            Pump(20);
        }
        return true;
    }

    // ── Input ───────────────────────────────────────────────────────────────

    internal static void Key(TopLevel window, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        window.KeyPress(key, mods, PhysicalKey.None, null);
        if (window is not Window { IsVisible: false }) // the press may have closed it (Enter, Esc)
            window.KeyRelease(key, mods, PhysicalKey.None, null);
        Pump();
    }

    /// <summary>A key press raised on one control, for controls in a popup (a flyout's list) that the window's input doesn't reach.</summary>
    internal static void KeyOn(Control target, Key key)
    {
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target });
        Pump();
    }

    /// <summary>Types <paramref name="text"/> one character at a time into whatever has focus.</summary>
    internal static void Type(TopLevel window, string text)
    {
        foreach (var ch in text)
        {
            window.KeyTextInput(ch.ToString());
            Pump();
        }
    }

    private static int _clickNudge;

    /// <summary>A real left click in the middle of <paramref name="control"/>, on the window that holds it.</summary>
    internal static void Click(Control control)
    {
        Pump();
        var window = TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException($"{control} isn't shown");
        var nudge = (_clickNudge++ % 5) - 2; // two clicks never pair into a double-click
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2 + nudge, control.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException($"{control} isn't in its window");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump();
    }

    // ── Finding things ──────────────────────────────────────────────────────

    /// <summary>The newest window of type <typeparamref name="T"/> owned by <paramref name="owner"/>, once it's open.</summary>
    internal static T Owned<T>(Window owner, int timeoutMs = 4000) where T : Window
    {
        WaitFor(() => owner.OwnedWindows.OfType<T>().Any(w => w.IsVisible), timeoutMs);
        var window = owner.OwnedWindows.OfType<T>().LastOrDefault(w => w.IsVisible)
                     ?? throw new InvalidOperationException($"No {typeof(T).Name} opened over {owner.Title}");
        Pump(80);
        return window;
    }

    internal static IEnumerable<T> Find<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    internal static Button ButtonNamed(Visual root, string automationName) =>
        Find<Button>(root).First(b => Avalonia.Automation.AutomationProperties.GetName(b) == automationName);

    internal static Button ButtonWithText(Visual root, string text) =>
        Find<Button>(root).First(b => b.IsEffectivelyVisible
            && (Equals(b.Content, text) || (b.Content as TextBlock)?.Text == text));

    /// <summary>Calls a private handler the way its menu item or button would (the harness can't reach every control).</summary>
    internal static void Invoke(object target, string method, params object?[] args)
    {
        var info = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                   ?? throw new MissingMethodException(target.GetType().Name, method);
        _ = info.Invoke(target, args);
        Pump();
    }

    /// <summary>
    /// Opens a button's flyout drawn into the window's overlay layer, so the window capture
    /// includes it (headless popups are otherwise separate, unrendered top levels).
    /// </summary>
    internal static FlyoutBase OpenFlyout(Button button)
    {
        var flyout = button.Flyout ?? throw new InvalidOperationException("The button has no flyout");
        var popup = typeof(PopupFlyoutBase).GetProperty("Popup", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(flyout) as Popup
                    ?? throw new InvalidOperationException("PopupFlyoutBase.Popup wasn't found");
        popup.ShouldUseOverlayLayer = true;
        flyout.ShowAt(button);
        Pump(120);
        return flyout;
    }
}
