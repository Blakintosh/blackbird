using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Blackbird.Services;
using Blackbird.ViewModels;
using Gscode.Updates;

namespace Blackbird;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Runs just before the container is built; the screenshot harness swaps in fakes (no Steam, no real builds).</summary>
    internal static Action<IServiceCollection>? ServiceOverrides { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();

        // Services
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IBuildService, BuildService>();
        services.AddSingleton<ISteamWorkshopService, SteamWorkshopService>();
        services.AddSingleton<ITemplateService, TemplateService>();
        services.AddSingleton<IProjectAnalysisService, ProjectAnalysisService>();
        services.AddSingleton(_ => new Updater(AppUpdates.Options));

        // ViewModels
        services.AddTransient<MainWindowViewModel>();

        ServiceOverrides?.Invoke(services);
        Services = services.BuildServiceProvider();

        // Load settings
        var settings = Services.GetRequiredService<ISettingsService>();
        settings.Load();
        // Before any window exists, so the first frame is already in the saved theme.
        AppTheme.Apply(AppTheme.Parse(settings.Theme));

        // Steam starts on first Workshop use; its call results pump their own callbacks.
        var steam = Services.GetRequiredService<ISteamWorkshopService>();

        // Every window asks for Mica (see the Window style in Theme.axaml).
        // Mica only shows through a see-through window, so each window goes
        // transparent once Windows actually grants it, and keeps the solid
        // theme background otherwise (Windows 10, transparency effects off,
        // battery saver) so it never renders as black glass.
        TopLevel.ActualTransparencyLevelProperty.Changed.AddClassHandler<Window>(
            (window, _) => ApplyBackdrop(window));
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => ApplyBackdrop(window));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = Services.GetRequiredService<MainWindowViewModel>();
            var mainWindow = new MainWindow { DataContext = vm };
            desktop.MainWindow = mainWindow;

            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                CrashLog.Write("Unhandled UI exception", e.Exception);
                e.Handled = true;
                mainWindow.ReportUnexpectedError();
            };

            // Install now swaps the files, then closes the app; it starts again from OnExit.
            var updater = Services.GetRequiredService<Updater>();
            updater.RestartRequested += (_, _) => mainWindow.Close();
            if (Updater.IsCheckDue(settings.LastUpdateCheckUtc))
            {
                settings.LastUpdateCheckUtc = DateTime.UtcNow;
                // After the first frame: the check and its download run in the background and only ever show a ready update.
                mainWindow.Opened += (_, _) => Dispatcher.UIThread.Post(() => _ = updater.CheckAsync(userAsked: false), DispatcherPriority.Background);
            }

            // Exit follows every shutdown path, after the main window has recorded its bounds.
            desktop.Exit += (_, _) =>
            {
                vm.SaveSettingsOnExit();
                steam.Shutdown();
                updater.OnExit();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ApplyBackdrop(Window window)
    {
        // Over Mica the window paints its theme colour at ~90% (MicaTint), so Mica grains the
        // surface without deciding its colour: Slate stays blue-black, Light stays pale.
        if (window.ActualTransparencyLevel == WindowTransparencyLevel.Mica)
            window[!TemplatedControl.BackgroundProperty] = window.GetResourceObservable("MicaTint").ToBinding();
        else
            window.ClearValue(TemplatedControl.BackgroundProperty);
    }
}
