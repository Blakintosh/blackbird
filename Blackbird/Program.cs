using Avalonia;
using Avalonia.Win32;
using Blackbird.Services;
using Gscode.Updates;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Blackbird;

internal static class Program
{
    private const uint LoadLibrarySearchUserDirs = 0x400;
    private const uint LoadLibrarySearchSystem32 = 0x800;

    // Escape hatch for GPUs or drivers that can't run Avalonia's hardware renderer.
    private const string SoftwareRenderFlag = "--software-render";

    private static bool _softwareRender;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    /// <summary>
    /// The process is ending on an error nothing caught (at startup, or on a background thread): say so, and where the
    /// log is, so Blackbird never just fails to appear or vanishes. Once per run.
    /// </summary>
    private static void ReportFatal(string source, object? exception)
    {
        CrashLog.Write(source, exception);
        if (Interlocked.Exchange(ref _fatalReported, 1) == 1)
            return;
        try
        {
            MessageBoxW(IntPtr.Zero,
                $"Blackbird ran into a problem it can't recover from and has to close.\n\nThe details are in:\n{AppPaths.CrashLog}",
                "Blackbird", 0x10 /* MB_ICONERROR */ | 0x2000 /* MB_TASKMODAL */);
        }
        catch (Exception)
        {
            // Nothing left to tell the user with; the log is written.
        }
    }

    private static int _fatalReported;

    private const int SwRestore = 9;

    // One Blackbird per user session: two would race each other's settings and build logs.
    private const string SingleInstanceMutexName = @"Local\Blackbird.SingleInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        // Blackbird ships as <BO3>\bin\modlauncher.exe, and installs often keep other copies of
        // dxgi.dll and d3d11.dll there for the Mod Tools (DXVK, or old Windows 10 builds).
        // Rendering through those crashes, so Windows' search skips the exe folder, and .NET's
        // own probe of it, which runs first for [DllImport], goes to System32 for Avalonia.
        // Libraries loaded by full path (steam_api64.dll) are unaffected.
        SetDefaultDllDirectories(LoadLibrarySearchSystem32 | LoadLibrarySearchUserDirs);

        // Before the single-instance check: after Install now, the version that started this one may still be exiting.
        Updater.FinishPreviousUpdate(AppUpdates.Options);

        using var singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            ActivateRunningInstance();
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(Win32PlatformOptions).Assembly, LoadFromSystem32);

        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal("Unhandled exception", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        try
        {
            AppPaths.MigrateLegacyData();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("Moving settings from %APPDATA%/Typhoon failed", ex);
        }

        _softwareRender = args.Contains(SoftwareRenderFlag, StringComparer.OrdinalIgnoreCase);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            ReportFatal("Fatal exception", ex);
        }
    }

    /// <summary>Brings the Blackbird that's already running to the front; does nothing if it has no window yet.</summary>
    private static void ActivateRunningInstance()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName(current.ProcessName))
            {
                using (other)
                {
                    if (other.Id == current.Id || other.MainWindowHandle == IntPtr.Zero)
                        continue;

                    if (IsIconic(other.MainWindowHandle))
                        ShowWindow(other.MainWindowHandle, SwRestore);
                    SetForegroundWindow(other.MainWindowHandle);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The other instance is exiting or inaccessible; this one just leaves quietly.
        }
    }

    private static IntPtr LoadFromSystem32(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (Path.IsPathRooted(libraryName))
            return IntPtr.Zero;

        var fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? libraryName : libraryName + ".dll";
        var systemPath = Path.Combine(Environment.SystemDirectory, fileName);
        return File.Exists(systemPath) && NativeLibrary.TryLoad(systemPath, out var handle) ? handle : IntPtr.Zero;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .LogToTrace();

        if (_softwareRender)
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software]
            });
        }

        return builder.UsePlatformDetect();
    }
}
