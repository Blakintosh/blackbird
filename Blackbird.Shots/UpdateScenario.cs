using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Blackbird.Services;
using Blackbird.Views;
using Gscode.Updates;
using static Blackbird.Shots.Program;

namespace Blackbird.Shots;

/// <summary>
/// Updates over fake releases (GSCODE_UPDATE_FEED) and a fake Black Ops III folder: Help > Updates through each
/// outcome of Check now (the menu stays open and its line follows the check), the palette's check in the status bar,
/// and the title bar control, which appears only for an update, with real clicks on its flyout's buttons and the files
/// swapped by Install now. Apex.Shots shoots the same states of its sibling menu and control.
/// </summary>
internal static class UpdateScenario
{
    /// <summary>The fake BO3 folder whose bin the app's updater thinks it runs from.</summary>
    public static string Bo3 => Path.Combine(FakeInstall.Root, "updates", "Call of Duty Black Ops III");
    private static string Bin => Path.Combine(Bo3, "bin");
    private static string Feeds => Path.Combine(FakeInstall.Root, "updates", "feeds");
    private static readonly string Next = NextVersion(AppUpdates.CurrentVersion);

    /// <summary>The updater the app gets in the harness: Blackbird's own options, run from the fake BO3 bin.</summary>
    public static Updater CreateUpdater()
    {
        Directory.CreateDirectory(Path.Combine(Bin, "steam"));
        Directory.CreateDirectory(Path.Combine(Bo3, "share"));
        File.WriteAllText(Path.Combine(Bo3, "BlackOps3.exe"), "game");
        File.WriteAllText(Path.Combine(Bin, "modlauncher.exe"), "OLD-EXE");
        File.WriteAllText(Path.Combine(Bin, "steam", "steam_api64.dll"), "OLD-DLL");
        // Downloads go under the run's folder, never the user's own %TEMP%\gscode-updates.
        var temp = Path.Combine(FakeInstall.Root, "updates", "temp");
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TMP", temp);
        Environment.SetEnvironmentVariable("TEMP", temp);
        return new Updater(Options(Path.Combine(Bin, "modlauncher.exe")));
    }

    private static UpdaterOptions Options(string exe) => AppUpdates.Options with
    {
        ExePath = exe,
        LogFile = Path.Combine(FakeInstall.DataPath, "updates.log"),
    };

    public static void Run(MainWindow main, Updater updates)
    {
        var vm = (Blackbird.ViewModels.MainWindowViewModel)main.DataContext!;
        var button = main.FindControl<Button>("UpdateButton")!;
        var flyout = (Flyout)button.Flyout!;
        // Drawn in the window's overlay layer so the window capture includes it (as Program.OpenFlyout does).
        var popup = (Popup)typeof(PopupFlyoutBase).GetProperty("Popup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(flyout)!;
        popup.ShouldUseOverlayLayer = true;
        var status = main.FindControl<MenuItem>("UpdateStatusItem")!;
        var checkNow = main.FindControl<MenuItem>("CheckNowItem")!;
        using var server = new ReleaseServer();

        // Before any check: the title bar has nothing, and Help > Updates says which version this is.
        Check($"updates: nothing in the title bar before a check ({button.IsVisible})", !button.IsVisible);
        OpenUpdatesMenu(main);
        Check($"updates: Help > Updates opens on the version and Check now ('{status.Header}', '{checkNow.Header}', line enabled {status.IsEnabled})",
            UpdatesMenu(main).IsSubMenuOpen && status.Header as string == $"Version {AppUpdates.CurrentVersion}"
            && checkNow.Header as string == "Check now" && checkNow.IsEffectivelyVisible && !status.IsEnabled);
        Shot(main, "update-menu-version");

        // Check now, with real clicks: the menu stays open and the line follows the check in place.
        server.HoldFeed();
        Environment.SetEnvironmentVariable(Updater.FeedVariable, server.Url("feed"));
        ClickInUpdatesMenu(main, checkNow);
        WaitFor(() => updates.State == UpdateState.Checking);
        Check($"updates: Check now keeps the menu open and says it's checking ('{status.Header}', open {UpdatesMenu(main).IsSubMenuOpen}, Check now enabled {checkNow.IsEffectivelyEnabled})",
            UpdatesMenu(main).IsSubMenuOpen && HelpMenu(main).IsSubMenuOpen && status.Header as string == "Checking…"
            && !checkNow.IsEffectivelyEnabled && !button.IsVisible);
        Shot(main, "update-menu-checking");
        Feed("same", AppUpdates.CurrentVersion);
        server.Serve(File.ReadAllText(Path.Combine(Feeds, "same", "latest.json")), []);
        Environment.SetEnvironmentVariable(Updater.FeedVariable, server.Url("feed"));
        WaitFor(() => updates.State == UpdateState.UpToDate);
        Pump(60);
        Check($"updates: nothing newer: the line says up to date in place, the menu stays open, the title bar stays empty ('{status.Header}', chip {button.IsVisible})",
            UpdatesMenu(main).IsSubMenuOpen && status.Header as string == $"Up to date · {AppUpdates.CurrentVersion}"
            && checkNow.Header as string == "Check now" && checkNow.IsEffectivelyEnabled && !button.IsVisible);
        Shot(main, "update-menu-up-to-date");
        // From the keyboard too: Enter on Check now checks, and the menu stays open.
        var keyed = false;
        void OnKeyed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
            keyed |= e.PropertyName == nameof(Updater.State) && updates.State == UpdateState.Checking;
        updates.PropertyChanged += OnKeyed;
        checkNow.Focus(Avalonia.Input.NavigationMethod.Directional);
        KeyOn(checkNow, Avalonia.Input.Key.Enter);
        WaitFor(() => keyed && updates.State == UpdateState.UpToDate);
        updates.PropertyChanged -= OnKeyed;
        Check($"updates: Enter on Check now checks again and keeps the menu open (checked {keyed}, open {UpdatesMenu(main).IsSubMenuOpen}, '{status.Header}')",
            keyed && UpdatesMenu(main).IsSubMenuOpen && HelpMenu(main).IsSubMenuOpen && status.Header as string == $"Up to date · {AppUpdates.CurrentVersion}");

        // Couldn't check: GitHub answers 404 while the repo is private. The line says why in brief, the tooltip in full.
        Environment.SetEnvironmentVariable(Updater.FeedVariable, server.Url("missing"));
        ClickInUpdatesMenu(main, checkNow);
        WaitFor(() => updates.State == UpdateState.CheckFailed);
        Pump(60);
        Check($"updates: a failed check reads as a calm line with Retry, and no title bar control ('{status.Header}', '{checkNow.Header}', tip '{ToolTip.GetTip(status)}', chip {button.IsVisible})",
            UpdatesMenu(main).IsSubMenuOpen && status.Header as string == "Couldn't check · no release published yet"
            && checkNow.Header as string == "Retry" && ToolTip.GetTip(status) as string == "No release has been published yet."
            && !button.IsVisible);
        Shot(main, "update-menu-couldnt-check");
        CloseMenu(main);

        // The palette's Check for updates reports through the status bar; the title bar stays empty.
        Feed("same", AppUpdates.CurrentVersion);
        Invoke(main, "CheckForUpdatesAsync");
        WaitFor(() => vm.StatusLine == $"You're up to date · {AppUpdates.CurrentVersion}");
        Check($"updates: the palette's check says up to date in the status bar ('{vm.StatusLine}', chip {button.IsVisible})",
            vm.StatusLine == $"You're up to date · {AppUpdates.CurrentVersion}" && !button.IsVisible && updates.State == UpdateState.UpToDate);
        Environment.SetEnvironmentVariable(Updater.FeedVariable, server.Url("missing"));
        Invoke(main, "CheckForUpdatesAsync");
        WaitFor(() => updates.State == UpdateState.CheckFailed);
        Pump(60);
        Check($"updates: the palette's failed check says why in the status bar ('{vm.StatusLine}', chip {button.IsVisible})",
            vm.StatusLine == "Couldn't check for updates. No release has been published yet." && !button.IsVisible);

        // Retry finds a newer release whose bundle doesn't match its checksum: the menu closes, and the control appears
        // and stays to say the download failed, with Retry and the installer link.
        Feed("corrupt", Next, digest: new string('0', 64));
        OpenUpdatesMenu(main);
        ClickInUpdatesMenu(main, checkNow);
        WaitFor(() => updates.State == UpdateState.DownloadFailed);
        Pump(60);
        Check($"updates: a newer release closes the menu, and a failed download stays in the title bar ({updates.State}, chip '{updates.ChipText}', menu open {HelpMenu(main).IsSubMenuOpen})",
            !HelpMenu(main).IsSubMenuOpen && button.IsVisible && updates.ChipText == "Update failed" && !flyout.IsOpen);
        Shot(main, "update-chip-failed");
        // The menu's hover delay from the pointer's last move in it (to Retry) runs out before it opens again.
        Pump(1000);
        OpenUpdatesMenu(main);
        Check($"updates: Help > Updates names the failed download ('{status.Header}', enabled {status.IsEnabled}, Check now shown {checkNow.IsVisible})",
            status.Header as string == $"Couldn't download Blackbird {Next}" && status.IsEnabled && !checkNow.IsVisible);
        Shot(main, "update-menu-couldnt-download");
        ClickInUpdatesMenu(main, status);
        WaitFor(() => flyout.IsOpen);
        Check($"updates: clicking the line closes the menu and opens the control's flyout ('{Title(main)}', flyout {flyout.IsOpen}, menu {HelpMenu(main).IsSubMenuOpen})",
            flyout.IsOpen && !HelpMenu(main).IsSubMenuOpen && Title(main) == "Couldn't download the update" && updates.HasInstallerLink);
        Shot(main, "update-couldnt-download");
        var retried = false;
        void OnRetry(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
            retried |= e.PropertyName == nameof(Updater.State) && updates.State == UpdateState.Downloading;
        updates.PropertyChanged += OnRetry;
        Click(FlyoutButton(main, "Retry"));
        WaitFor(() => retried && updates.State == UpdateState.DownloadFailed);
        updates.PropertyChanged -= OnRetry;
        Check($"updates: the flyout's Retry downloads the same release again, without a check (downloaded again {retried}, {updates.State})",
            retried && updates.State == UpdateState.DownloadFailed);
        flyout.Hide();
        Pump(60);
        Check($"updates: closing the flyout puts a failed download away ({updates.State}, chip {button.IsVisible}, line '{status.Header}')",
            updates.State == UpdateState.Idle && !button.IsVisible && status.Header as string == $"Version {AppUpdates.CurrentVersion}");

        // Check now again, and the release is good: the control downloads it (the bundle comes halfway and waits).
        var zip = Feed("next", Next, bundleUrl: server.Url("zip"));
        server.HoldFeed();
        server.Serve(File.ReadAllText(Path.Combine(Feeds, "next", "latest.json")), File.ReadAllBytes(zip));
        Environment.SetEnvironmentVariable(Updater.FeedVariable, server.Url("feed"));
        OpenUpdatesMenu(main);
        ClickInUpdatesMenu(main, checkNow);
        WaitFor(() => updates.State == UpdateState.Downloading && updates.Progress > 0.4);
        Pump(60);
        Check($"updates: a newer release closes the menu and shows the control downloading ({updates.State}, chip '{updates.ChipText}', menu open {HelpMenu(main).IsSubMenuOpen})",
            !HelpMenu(main).IsSubMenuOpen && button.IsVisible && updates.ChipText == "Downloading…" && !flyout.IsOpen);
        Shot(main, "update-chip-downloading");
        // The menu's hover delay from the pointer's last move in it (to Check now) runs out before it opens again.
        Pump(1000);
        OpenUpdatesMenu(main);
        Check($"updates: while it downloads the line names it and Check now steps aside ('{status.Header}', enabled {status.IsEnabled}, Check now shown {checkNow.IsVisible})",
            status.Header as string == $"Downloading Blackbird {Next}…" && status.IsEnabled && !checkNow.IsVisible);
        Shot(main, "update-menu-downloading");
        ClickInUpdatesMenu(main, status);
        WaitFor(() => flyout.IsOpen);
        Check($"updates: the line opens the downloading flyout too (flyout {flyout.IsOpen}, menu {HelpMenu(main).IsSubMenuOpen})",
            flyout.IsOpen && !HelpMenu(main).IsSubMenuOpen);
        Check($"updates: downloading shows how far ({updates.Detail}, {updates.Progress:P0})", updates.State == UpdateState.Downloading && updates.Progress is > 0.4 and < 0.6);
        Shot(main, "update-downloading");
        server.ReleaseZip();
        WaitFor(() => updates.State == UpdateState.Ready, 8000);
        Check($"updates: the verified download is ready ('{Title(main)}', chip '{updates.ChipText}')",
            updates.State == UpdateState.Ready && Title(main) == $"Blackbird {Next} is ready" && updates.ChipText == "Update ready");
        Shot(main, "update-ready");

        Click(FlyoutButton(main, "Install on close"));
        Check($"updates: Install on close schedules it ({updates.State}, chip '{updates.ChipText}') and writes nothing yet",
            updates.State == UpdateState.InstallOnClose && updates.ChipText == "Updates on close" && File.ReadAllText(Path.Combine(Bin, "modlauncher.exe")) == "OLD-EXE");
        Shot(main, "update-install-on-close");
        Click(FlyoutButton(main, "Don't install on close"));
        Check($"updates: Don't install on close takes it back ({updates.State})", updates.State == UpdateState.Ready);

        flyout.Hide();
        Pump(60);
        Check($"updates: a ready update keeps its control in the title bar ({button.IsVisible})", button.IsVisible && updates.State == UpdateState.Ready);
        Shot(main, "update-chip-ready");
        OpenUpdatesMenu(main);
        Check($"updates: Help > Updates names the ready update ('{status.Header}')",
            status.Header as string == $"Blackbird {Next} is ready" && status.IsEnabled && !checkNow.IsVisible);
        Shot(main, "update-menu-ready");
        CloseMenu(main);
        Click(button);
        Pump(120);
        Check("updates: clicking the control opens its flyout", flyout.IsOpen);

        Click(FlyoutButton(main, "Install now"));
        WaitFor(() => updates.State != UpdateState.Ready);
        Check($"updates: Install now swaps the files in and keeps the old ones as .old ({updates.State})",
            updates.State == UpdateState.Installed
            && File.ReadAllText(Path.Combine(Bin, "modlauncher.exe")).StartsWith("NEW-EXE") && File.ReadAllText(Path.Combine(Bin, "modlauncher.exe.old")) == "OLD-EXE"
            && File.ReadAllText(Path.Combine(Bin, "steam", "steam_api64.dll")).StartsWith("NEW-DLL") && File.ReadAllText(Path.Combine(Bin, "steam", "steam_api64.dll.old")) == "OLD-DLL");
        // The harness has no desktop lifetime, so nothing closes: the panel shows what it would if the close were cancelled.
        Shot(main, "update-installed");
        Updater.FinishPreviousUpdate(Options(Path.Combine(Bin, "modlauncher.exe")));
        Check("updates: the next start removes the .old files", !File.Exists(Path.Combine(Bin, "modlauncher.exe.old"))
            && !File.Exists(Path.Combine(Bin, "steam", "steam_api64.dll.old")));

        // Couldn't install: the bin folder can't be written (BO3 under Program Files). Its own updater in the same panel.
        var readOnly = Path.Combine(FakeInstall.Root, "updates", "readonly", "bin");
        CopyBo3(readOnly);
        var user = Environment.UserName;
        Icacls(readOnly, "/deny", $"{user}:(W,D,DC)");
        try
        {
            var blocked = ShowOther(main, Options(Path.Combine(readOnly, "modlauncher.exe")), "next");
            Click(FlyoutButton(main, "Install now"));
            WaitFor(() => blocked.State == UpdateState.InstallFailed);
            Check($"updates: a folder it can't write to says so, with the installer link ('{blocked.Title}', '{blocked.Detail}')",
                blocked.Title == "Couldn't install the update" && blocked.HasInstallerLink && File.ReadAllText(Path.Combine(readOnly, "modlauncher.exe")) == "OLD-EXE");
            Shot(main, "update-couldnt-install");
        }
        finally
        {
            Icacls(readOnly, "/remove:d", user);
        }

        // A dev build: the update shows, install is off and says why.
        var dev = ShowOther(main, Options(Path.Combine(AppContext.BaseDirectory, "modlauncher.exe")), "next");
        var install = FlyoutButton(main, "Install now");
        Check($"updates: outside a BO3 bin folder install is off and says why ('{dev.BlockedReason}', enabled {install.IsEffectivelyEnabled})",
            dev.IsInstallBlocked && !install.IsEffectivelyEnabled && ToolTip.GetTip(install) as string == dev.BlockedReason);
        Shot(main, "update-ready-dev-build");
        flyout.Hide();
        Pump(60);
        Environment.SetEnvironmentVariable(Updater.FeedVariable, null);
    }

    private static MenuItem HelpMenu(MainWindow main) => Find<MenuItem>(main).First(m => m.Header as string == "_Help");

    private static MenuItem UpdatesMenu(MainWindow main) => main.FindControl<MenuItem>("UpdatesMenu")!;

    /// <summary>Help, then Updates: two real clicks, each submenu drawn in the window's overlay layer so the shot includes it.</summary>
    private static void OpenUpdatesMenu(MainWindow main)
    {
        // Starting from closed, and clicking only what isn't open yet, so a menu the pointer's hover already
        // opened (or left open) is never toggled shut by the click meant to open it.
        CloseMenu(main);
        var help = HelpMenu(main);
        DrawInWindow(help);
        if (!help.IsSubMenuOpen) Click(help);
        WaitFor(() => help.IsSubMenuOpen && UpdatesMenu(main).IsEffectivelyVisible);
        var updates = UpdatesMenu(main);
        DrawInWindow(updates);
        if (!updates.IsSubMenuOpen) Click(updates);
        WaitFor(() => updates.IsSubMenuOpen);
        Pump(60);
    }

    /// <summary>Clicks an item of the Updates submenu, opening the menu again first if the submenu closed itself
    /// since it was opened (its hover delay can shut it between two steps).</summary>
    private static void ClickInUpdatesMenu(MainWindow main, MenuItem item)
    {
        if (!item.IsEffectivelyVisible || Avalonia.Controls.TopLevel.GetTopLevel(item) is null || !UpdatesMenu(main).IsSubMenuOpen)
            OpenUpdatesMenu(main);
        Click(item);
    }

    private static void DrawInWindow(MenuItem item)
    {
        if (Find<Popup>(item).FirstOrDefault() is { } popup)
            popup.ShouldUseOverlayLayer = true;
    }

    private static void CloseMenu(MainWindow main)
    {
        main.FindControl<Menu>("TitleMenu")!.Close();
        Pump(60);
    }

    private static string Title(MainWindow main) =>
        Find<UpdatePanel>(main).FirstOrDefault()?.FindControl<TextBlock>("UpdateTitle")?.Text ?? "";

    private static Button FlyoutButton(MainWindow main, string text) =>
        Find<UpdatePanel>(main).SelectMany(Find<Button>).First(b => b.IsEffectivelyVisible && b.Content as string == text);

    /// <summary>Shows another updater's state in the open flyout (the app's own is spent once installed).</summary>
    private static Updater ShowOther(MainWindow main, UpdaterOptions options, string feed)
    {
        Environment.SetEnvironmentVariable(Updater.FeedVariable, new Uri(Path.Combine(Feeds, feed, "latest.json")).AbsoluteUri);
        var other = new Updater(options);
        var check = other.CheckAsync(userAsked: true);
        WaitFor(() => check.IsCompleted, 8000);
        var button = main.FindControl<Button>("UpdateButton")!;
        if (!button.Flyout!.IsOpen)
            button.Flyout.ShowAt(button);
        Pump(120);
        Find<UpdatePanel>(main).First().DataContext = other;
        Pump(120);
        return other;
    }

    private static void CopyBo3(string bin)
    {
        Directory.CreateDirectory(Path.Combine(bin, "steam"));
        File.WriteAllText(Path.Combine(bin, "..", "BlackOps3.exe"), "game");
        File.WriteAllText(Path.Combine(bin, "modlauncher.exe"), "OLD-EXE");
        File.WriteAllText(Path.Combine(bin, "steam", "steam_api64.dll"), "OLD-DLL");
    }

    private static void Icacls(string path, string verb, string who)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls", [path, verb, who])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        p.WaitForExit();
    }

    /// <summary>Writes a release (latest.json and its bundle) and points GSCODE_UPDATE_FEED at it. Returns the zip.</summary>
    private static string Feed(string name, string version, string? bundleUrl = null, string? digest = null)
    {
        var folder = Path.Combine(Feeds, name);
        Directory.CreateDirectory(folder);
        var zipName = $"blackbird-{version}-win-x64.zip";
        var zipPath = Path.Combine(folder, zipName);
        File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (entry, text) in new[] { ("bin/modlauncher.exe", "NEW-EXE"), ("bin/steam/steam_api64.dll", "NEW-DLL") })
            {
                using var s = zip.CreateEntry(entry).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
                // Enough bytes (that don't compress) for the download to have a middle to stop in.
                var filler = new byte[512 * 1024];
                new Random(1).NextBytes(filler);
                s.Write(filler);
            }
        }
        var json = JsonSerializer.Serialize(new
        {
            tag_name = "v" + version,
            html_url = $"https://github.com/Blakintosh/blackbird/releases/tag/v{version}",
            body = "## What's new\n\n- Build presets remember their launch config\n- The project list opens faster on large installs\n"
                   + "- The log keeps its place when a build restarts\n- Fixes for Workshop thumbnails",
            assets = new object[]
            {
                new
                {
                    name = zipName,
                    browser_download_url = bundleUrl ?? new Uri(zipPath).AbsoluteUri,
                    size = new FileInfo(zipPath).Length,
                    digest = "sha256:" + (digest ?? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zipPath)))),
                },
                new { name = "blackbird-setup.exe", browser_download_url = $"https://github.com/Blakintosh/blackbird/releases/download/v{version}/blackbird-setup.exe", size = 1 },
            },
        });
        File.WriteAllText(Path.Combine(folder, "latest.json"), json);
        Environment.SetEnvironmentVariable(Updater.FeedVariable, new Uri(Path.Combine(folder, "latest.json")).AbsoluteUri);
        return zipPath;
    }

    private static string NextVersion(string version)
    {
        var parts = version.Split('-')[0].Split('.').Select(int.Parse).ToArray();
        return $"{parts[0]}.{parts[1]}.{parts[2] + 1}";
    }

    /// <summary>
    /// A local stand-in for GitHub that can hold its answers: /feed waits until <see cref="Serve"/>, /zip sends half the
    /// bundle and waits for <see cref="ReleaseZip"/>, anything else is 404.
    /// </summary>
    private sealed class ReleaseServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private TaskCompletionSource<(string Feed, byte[] Zip)> _content = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _zipGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ReleaseServer()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{path}";
        public void HoldFeed() => _content = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Serve(string feed, byte[] zip) => _content.TrySetResult((feed, zip));
        public void ReleaseZip() => _zipGate.TrySetResult();

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    var path = Encoding.ASCII.GetString(buffer, 0, read).Split(' ')[1];
                    if (path == "/feed")
                    {
                        var (feed, _) = await _content.Task.WaitAsync(_stop.Token);
                        await Send(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(feed));
                    }
                    else if (path == "/zip")
                    {
                        var (_, zip) = await _content.Task.WaitAsync(_stop.Token);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: {zip.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                        await stream.WriteAsync(zip.AsMemory(0, zip.Length / 2), _stop.Token);
                        await stream.FlushAsync(_stop.Token);
                        await _zipGate.Task.WaitAsync(_stop.Token);
                        await stream.WriteAsync(zip.AsMemory(zip.Length / 2), _stop.Token);
                    }
                    else
                    {
                        await Send(stream, "404 Not Found", "application/json", "{\"message\":\"Not Found\"}"u8.ToArray());
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
                {
                    // The harness moved on.
                }
            }
        }

        private async Task Send(NetworkStream stream, string status, string type, byte[] body)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
            await stream.WriteAsync(body, _stop.Token);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
