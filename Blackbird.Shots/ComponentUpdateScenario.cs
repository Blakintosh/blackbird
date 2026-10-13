using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Blackbird.Models;
using Blackbird.Services;
using Blackbird.ViewModels;
using Blackbird.Views;
using Gscode.Updates;
using Microsoft.Extensions.DependencyInjection;
using static Blackbird.Shots.Program;

namespace Blackbird.Shots;

/// <summary>
/// Dragon and Stock Library updates over a fake Black Ops III folder and fake components: the check through a component's
/// own CLI (Dragon) and straight against the feed when the CLI predates the contract (Stock Library, then Dragon with an
/// unknown flag), the combined title bar notification, Tools > Components' badges and Update buttons, an install that waits
/// for a build and for Apex or Radiant, the .old swap, and every rollback (a new exe that won't run, a locked file, a bad
/// checksum). The CLIs are stand-ins that answer by reading the exe's text, so a "new exe" is just different text.
/// </summary>
internal static partial class ComponentUpdateScenario
{
    private static string? _bo3Override;
    private static string? _toolsOverride;
    private static string? _inUseRoot;
    private static string Bo3 => _bo3Override ?? Path.Combine(FakeInstall.Root, "components-update", "Call of Duty Black Ops III");
    private static string Feeds => Path.Combine(FakeInstall.Root, "components-update", "feeds");
    private static string DragonExe => Path.Combine(Bo3, "bin", "bb-linker", "bb-linker.exe");
    private static string StockExe => Path.Combine(Bo3, "bin", "stocklib", "stocklib.exe");
    private static string PluginDll => Path.Combine(Bo3, "bin", "plugins", "StockLibrary", "StockLibrary.dll");
    private static string PluginIni => Path.Combine(Bo3, "bin", "plugins", "StockLibrary", "StockLibrary.ini");
    private static string Cache(string id) => Path.Combine(ComponentPaths.Root, "updates", id + ".json");

    // What the stand-in CLIs answer, set by each step.
    private sealed record Answer(string? Latest, string BundleUrl, string Digest, string Notes = "", string? Error = null);
    private static Answer? _dragonSays;
    private static Answer? _stockSays;
    private static string? _inUse;
    private static readonly List<string> Runs = [];

    public static ComponentUpdater CreateUpdater() => new(new ComponentUpdaterOptions
    {
        GamePath = () => Bo3,
        ToolsPath = () => _toolsOverride ?? Bo3,
        Enabled = () => App.Services.GetRequiredService<ISettingsService>().CheckComponentUpdates,
        InUseReason = (root, _) =>
        {
            _inUseRoot = root;
            return _inUse;
        },
        Run = FakeRun,
        LogFile = Path.Combine(FakeInstall.DataPath, "updates.log"),
        PollInterval = TimeSpan.FromMilliseconds(60),
        BusyPollInterval = TimeSpan.FromMilliseconds(60),
        DownloadFolder = Path.Combine(FakeInstall.Root, "components-update", "downloads"),
    });

    public static void Run(MainWindow main, MainWindowViewModel vm, ISettingsService settings)
    {
        var updates = vm.ComponentUpdates;
        var chip = main.FindControl<Button>("ComponentUpdateButton")!;
        var priorLinker = Environment.GetEnvironmentVariable("BLACKBIRD_BB_LINKER");
        var priorFeed = Environment.GetEnvironmentVariable(Updater.FeedVariable);
        Environment.SetEnvironmentVariable("BLACKBIRD_BB_LINKER", null);
        using var server = new UpdateScenario.ReleaseServer();
        var installs = 0;
        updates.Installed += (_, _) => installs++;
        try
        {
            ParsesTheCli();
            Prepare();
            Check("components update: nothing is shown before a check", !chip.IsVisible && updates.Phase == ComponentPhase.Idle);

            // The setting: off means the automatic check does nothing; a check the user asks for still runs.
            var item = main.FindControl<MenuItem>("ComponentUpdatesItem")!;
            Check($"components update: Help > Updates has the switch, on by default ({item.IsChecked}, setting {settings.CheckComponentUpdates})",
                item.IsChecked == true && vm.CheckComponentUpdates);
            var dragonZip = Release("dragon", "1.0.1", [("bin/bb-linker/bb-linker.exe", "DRAGON:1.0.1")], filler: 512 * 1024);
            Release("stocklib", "1.0.1", [("bin/stocklib/stocklib.exe", "STOCKLIB:1.0.1"), ("bin/plugins/StockLibrary/StockLibrary.dll", "PLUGIN:1.0.1"),
                ("bin/plugins/StockLibrary/StockLibrary.ini", "DEFAULTS")]);
            Environment.SetEnvironmentVariable(Updater.FeedVariable, new Uri(Path.Combine(Feeds, "@@", "latest.json")).AbsoluteUri.Replace("@@", "{id}", StringComparison.Ordinal));
            vm.CheckComponentUpdates = false;
            Check("components update: the switch is the setting", !settings.CheckComponentUpdates);
            Runs.Clear();
            var off = updates.CheckAsync(userAsked: false);
            WaitFor(() => off.IsCompleted);
            Check($"components update: with the check off, the automatic check runs nothing ({Runs.Count} runs, {updates.Phase})",
                Runs.Count == 0 && updates.Phase == ComponentPhase.Idle && !chip.IsVisible);
            vm.CheckComponentUpdates = true;

            // Never during a build: the check waits for it, then runs.
            vm.IsBuildRunning = true;
            var held = updates.CheckAsync(userAsked: false);
            Pump(300);
            Check($"components update: during a build the check waits and runs nothing ({Runs.Count} runs, {updates.Phase})",
                updates.Phase == ComponentPhase.Checking && Runs.Count == 0 && !held.IsCompleted);
            Check($"components update: the view model tells the updater about the build ('{updates.BusyReason()}')", updates.BusyReason() == "a build is running");

            // Dragon answers through its CLI; its bundle comes from a server that stops halfway, to shoot the progress.
            server.HoldFeed();
            server.Serve("", File.ReadAllBytes(dragonZip));
            _dragonSays = new Answer("1.0.1", server.Url("dragon-1.0.1-win-x64.zip"), Sha(dragonZip), "## Dragon 1.0.1\n\n- Links stock sounds into the zone\n- Fixes a crash with empty LOD tables");
            vm.IsBuildRunning = false;
            WaitFor(() => updates.Phase == ComponentPhase.Downloading && updates.Items.Any(i => i.Id == "dragon" && i.Progress > 0.3), 8000);
            Pump(60);
            Check($"components update: Dragon's own check finds 1.0.1 and the control shows the download ({updates.Phase}, chip '{updates.ChipText}', {updates.Progress:P0})",
                updates.Phase == ComponentPhase.Downloading && chip.IsVisible && updates.ChipText == "Downloading…");
            Shot(main, "components-update-chip-downloading");
            var flyout = OpenFlyout(chip);
            Check($"components update: the flyout shows the download ('{PanelTitle(main)}')", PanelTitle(main) == "Downloading Dragon 1.0.1 and Stock Library 1.0.1…");
            Shot(main, "components-update-downloading");
            server.ReleaseZip();
            WaitFor(() => updates.Phase == ComponentPhase.Ready, 8000);
            Pump(60);
            Check($"components update: Stock Library's CLI predates the contract, so the feed answers for it ({string.Join("; ", Runs.Take(2))})",
                Runs.Contains("stocklib.exe update-check --json") && updates.Find("stocklib") is { Latest: "1.0.1", Current: "1.0.0" }
                && updates.Find("dragon") is { Latest: "1.0.1", Current: "1.0.0" });
            Check($"components update: one combined message names both ('{PanelTitle(main)}', chip '{updates.ChipText}', '{updates.Detail}')",
                PanelTitle(main) == "Dragon 1.0.1 and Stock Library 1.0.1 are available" && updates.ChipText == "2 component updates"
                && updates.Detail == "You have Dragon 1.0.0 and Stock Library 1.0.0.");
            Check("components update: both bundles are downloaded and verified, and nothing is installed yet",
                updates.Items.All(i => i.BundlePath is not null && File.Exists(i.BundlePath))
                && File.ReadAllText(DragonExe) == "DRAGON:1.0.0" && File.ReadAllText(StockExe) == "STOCKLIB:1.0.0:old-cli");
            Check($"components update: the flyout offers Later and Install ({string.Join(", ", PanelButtons(main))})", PanelButtons(main).SequenceEqual(["Later", "Install"]));
            Shot(main, "components-update-ready");
            Shot150(main, "components-update-ready@1.5x");

            // Later: the control goes away; the badges stay.
            Click(PanelButton(main, "Later"));
            Pump(60);
            Check($"components update: Later hides the control and keeps the updates ({chip.IsVisible}, {updates.Items.Count} updates)", !chip.IsVisible && updates.Items.Count == 2 && !flyout.IsOpen);

            // Tools > Components: a badge on each row and an Update button.
            var dialog = ShowComponents(main, updates);
            var rows = ((ComponentsDialogViewModel)dialog.DataContext!).Rows.ToDictionary(r => r.Id);
            Check($"components update: Tools > Components badges Dragon and Stock Library ('{rows["dragon"].Update?.StateText}', '{rows["stocklib"].Update?.StateText}')",
                rows["dragon"].Update?.StateText == "1.0.1 available · you have 1.0.0" && rows["stocklib"].Update is not null
                && rows["apex"].Update is null && rows["ultrasound"].Update is null);
            Check("components update: each updatable row has an Update button", Find<Button>(dialog).Count(b => b.Name == "InstallUpdateButton" && b.IsEffectivelyVisible) == 2);
            Shot(dialog, "components-update-badges");
            Shot150(dialog, "components-update-badges@1.5x");

            // Update on Dragon's row during a build: it waits, says so, and installs the moment the build is over.
            vm.IsBuildRunning = true;
            Click(Find<Button>(dialog).First(b => b.Name == "InstallUpdateButton" && b.IsEffectivelyVisible));
            WaitFor(() => updates.Phase == ComponentPhase.Waiting);
            Pump(80);
            var status = dialog.FindControl<TextBlock>("ComponentsStatus")!;
            Check($"components update: during a build Update waits and the view says why ('{status.Text}', {updates.Phase})",
                updates.Phase == ComponentPhase.Waiting && status.Text == "A build is running. It installs when that finishes."
                && File.ReadAllText(DragonExe) == "DRAGON:1.0.0");
            Shot(dialog, "components-update-waiting-build");
            vm.IsBuildRunning = false;
            WaitFor(() => installs == 1 && updates.Phase == ComponentPhase.Ready, 8000);
            Pump(80);
            Check($"components update: the build ends and Dragon installs, Stock Library still waits for the user ({updates.Phase}, {installs} installs, '{status.Text}')",
                File.ReadAllText(DragonExe) == "DRAGON:1.0.1" && !File.Exists(DragonExe + ".old") && !File.Exists(DragonExe + ".new")
                && !Directory.Exists(Path.Combine(Bo3, "docs")) && updates.Items.Select(i => i.Id).SequenceEqual(["stocklib"]) && status.Text == "Dragon 1.0.1 is installed.");
            Shot(dialog, "components-update-dragon-installed");
            Key(dialog, Avalonia.Input.Key.Escape);
            WaitFor(() => !dialog.IsVisible);

            // Back in the title bar: Stock Library waits for Apex or Radiant to let go of it.
            SetRegistry("dragon", "1.0.1");
            _ = updates.CheckAsync(userAsked: true);
            Pump(60);
            Check($"components update: a check the user asks for brings the control back for what is left ('{updates.ChipText}')", chip.IsVisible && updates.ChipText == "Stock Library update");
            OpenFlyout(chip);
            _inUse = "Apex or Radiant has the Stock Library open. Close them to update it, or wait here.";
            WaitFor(() => updates.InstallText == "Install when free");
            Check($"components update: while Apex has it open the button says it waits and the flyout says why ('{PanelTitle(main)}', '{updates.Detail}')",
                PanelTitle(main) == "Stock Library 1.0.1 is available" && updates.Detail == _inUse && PanelButton(main, "Install when free").IsEffectivelyEnabled);
            Shot(main, "components-update-blocked-apex");
            Click(PanelButton(main, "Install when free"));
            WaitFor(() => updates.Phase == ComponentPhase.Waiting);
            Pump(60);
            Check($"components update: Install when free waits ({updates.Phase}, chip '{updates.ChipText}', '{PanelTitle(main)}')",
                updates.Phase == ComponentPhase.Waiting && updates.ChipText == "Update waiting" && PanelTitle(main) == "Stock Library 1.0.1 will install when it can"
                && File.ReadAllText(StockExe) == "STOCKLIB:1.0.0:old-cli");
            Shot(main, "components-update-waiting");
            Directory.CreateDirectory(Path.GetDirectoryName(Cache("stocklib"))!);
            File.WriteAllText(Cache("stocklib"), "{\"current\":\"1.0.0\"}");
            File.WriteAllText(PluginIni, "KEEP-ME");
            _inUse = null;
            WaitFor(() => updates.Phase == ComponentPhase.Installed, 8000);
            Pump(80);
            Check($"components update: Apex lets go and it installs ({updates.Phase}, {installs} installs, '{PanelTitle(main)}')",
                updates.Phase == ComponentPhase.Installed && installs == 2 && PanelTitle(main) == "Stock Library 1.0.1 is installed");
            Check($"components update: the exe and the Radiant plugin are replaced, the plugin's settings file is not, no .old is left ({File.ReadAllText(StockExe)}, {File.ReadAllText(PluginDll)}, {File.ReadAllText(PluginIni)})",
                File.ReadAllText(StockExe) == "STOCKLIB:1.0.1" && File.ReadAllText(PluginDll) == "PLUGIN:1.0.1" && File.ReadAllText(PluginIni) == "KEEP-ME"
                && !File.Exists(StockExe + ".old") && !File.Exists(PluginDll + ".old") && !File.Exists(StockExe + ".new"));
            Check("components update: the component's own cache of the old version is cleared", !File.Exists(Cache("stocklib")));
            Shot(main, "components-update-installed");
            Click(PanelButton(main, "Close"));
            Pump(60);
            Check($"components update: Close puts the finished update away ({updates.Phase}, {chip.IsVisible})", updates.Phase == ComponentPhase.Idle && !chip.IsVisible && updates.Items.Count == 0);
            SetRegistry("stocklib", "1.0.1");

            // A new Dragon that won't run: the old one is put back.
            var bad = Release("dragon", "1.0.2", [("bin/bb-linker/bb-linker.exe", "BAD:1.0.2")]);
            _dragonSays = new Answer("1.0.2", new Uri(bad).AbsoluteUri, Sha(bad));
            _stockSays = null;
            _ = updates.CheckAsync(userAsked: true);
            WaitFor(() => updates.Phase == ComponentPhase.Ready, 8000);
            OpenFlyout(chip);
            Click(PanelButton(main, "Install"));
            WaitFor(() => updates.Phase == ComponentPhase.Failed, 8000);
            Pump(80);
            Check($"components update: a new Dragon that doesn't run is rolled back ('{PanelTitle(main)}', '{updates.Detail}')",
                PanelTitle(main) == "Couldn't update Dragon" && updates.Detail == "The new Dragon didn't start. The old version was put back."
                && File.ReadAllText(DragonExe) == "DRAGON:1.0.1" && !File.Exists(DragonExe + ".old") && !File.Exists(DragonExe + ".new") && installs == 2);
            Check($"components update: the failure offers Try again and Later ({string.Join(", ", PanelButtons(main))})", PanelButtons(main).SequenceEqual(["Later", "Try again"]));
            Shot(main, "components-update-failed");

            // A file Radiant holds: the swap fails half way and everything is put back.
            Click(PanelButton(main, "Later"));
            Release("stocklib", "1.0.2", [("bin/stocklib/stocklib.exe", "STOCKLIB:1.0.2"), ("bin/plugins/StockLibrary/StockLibrary.dll", "PLUGIN:1.0.2")]);
            var stockZip = Path.Combine(Feeds, "stocklib", "stocklib-1.0.2-win-x64.zip");
            _stockSays = new Answer("1.0.2", new Uri(stockZip).AbsoluteUri, Sha(stockZip));
            _dragonSays = new Answer("1.0.1", "", "");
            _ = updates.CheckAsync(userAsked: true);
            WaitFor(() => updates.Phase == ComponentPhase.Ready && updates.Items.Count == 1, 8000);
            OpenFlyout(chip);
            Check($"components update: Stock Library's own check finds 1.0.2 now that it has one ('{PanelTitle(main)}', {Runs.Count(r => r == "stocklib.exe update-check --json")} asks)",
                PanelTitle(main) == "Stock Library 1.0.2 is available");
            using (new FileStream(PluginDll, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Click(PanelButton(main, "Install"));
                WaitFor(() => updates.Phase == ComponentPhase.Failed, 8000);
                Pump(80);
                Check($"components update: a file in use puts every file back ('{PanelTitle(main)}', '{updates.Detail}')",
                    PanelTitle(main) == "Couldn't update Stock Library" && updates.Detail == "A file was in use or couldn't be replaced. Nothing was changed."
                    && File.ReadAllText(StockExe) == "STOCKLIB:1.0.1" && File.ReadAllText(PluginDll) == "PLUGIN:1.0.1"
                    && !File.Exists(StockExe + ".old") && !File.Exists(StockExe + ".new") && !File.Exists(PluginDll + ".new"));
                Shot(main, "components-update-failed-locked");
            }

            Click(PanelButton(main, "Try again"));
            WaitFor(() => updates.Phase == ComponentPhase.Installed, 8000);
            Pump(60);
            Check($"components update: Try again installs it once the file is free ({updates.Phase}, {File.ReadAllText(StockExe)}, {File.ReadAllText(PluginDll)})",
                File.ReadAllText(StockExe) == "STOCKLIB:1.0.2" && File.ReadAllText(PluginDll) == "PLUGIN:1.0.2" && installs == 3);
            Click(PanelButton(main, "Close"));
            SetRegistry("stocklib", "1.0.2");

            // A download that doesn't match its checksum is never kept or installed.
            var corrupt = Release("dragon", "1.0.3", [("bin/bb-linker/bb-linker.exe", "DRAGON:1.0.3")]);
            _dragonSays = new Answer("1.0.3", new Uri(corrupt).AbsoluteUri, new string('0', 64));
            _stockSays = null;
            _ = updates.CheckAsync(userAsked: true);
            WaitFor(() => updates.Phase == ComponentPhase.Failed, 8000);
            OpenFlyout(chip);
            Pump(80);
            Check($"components update: a download that doesn't match its checksum is refused ('{PanelTitle(main)}', '{updates.Detail}')",
                PanelTitle(main) == "Couldn't download Dragon 1.0.3" && updates.Detail == "The download didn't match its checksum, so it wasn't kept."
                && File.ReadAllText(DragonExe) == "DRAGON:1.0.1" && !Directory.EnumerateFiles(Path.Combine(FakeInstall.Root, "components-update", "downloads", "dragon"), "*1.0.3*").Any());
            Shot(main, "components-update-failed-download");
            Click(PanelButton(main, "Later"));

            // A Dragon whose CLI predates the contract answers "unknown flag": the feed is read directly.
            var viaFeed = Release("dragon", "1.0.4", [("bin/bb-linker/bb-linker.exe", "DRAGON:1.0.4")]);
            _dragonSays = null;
            SetRegistry("dragon", "1.0.1");
            Runs.Clear();
            _ = updates.CheckAsync(userAsked: true);
            WaitFor(() => updates.Phase == ComponentPhase.Ready, 8000);
            Check($"components update: an unknown flag falls back to the release feed ({string.Join("; ", Runs.Take(2))}, {updates.Find("dragon")?.Latest})",
                Runs.Contains("bb-linker.exe -check-update -json") && updates.Find("dragon") is { Latest: "1.0.4", Current: "1.0.1" } d
                && d.PageUrl == "https://github.com/Blakintosh/dragon/releases/tag/v1.0.4" && d.BundleUrl == new Uri(viaFeed).AbsoluteUri);
            OpenFlyout(chip);
            Click(PanelButton(main, "Install"));
            WaitFor(() => updates.Phase == ComponentPhase.Installed, 8000);
            Click(PanelButton(main, "Close"));
            SetRegistry("dragon", "1.0.4");
            Check($"components update: installed from the feed's bundle ({File.ReadAllText(DragonExe)})", File.ReadAllText(DragonExe) == "DRAGON:1.0.4" && installs == 4);

            // The CLI answers but couldn't reach the feed: no update, no second request, a calm line.
            _dragonSays = new Answer(null, "", "", Error: "the feed can't be reached");
            Runs.Clear();
            _ = updates.CheckAsync(userAsked: true);
            WaitFor(() => updates.Phase == ComponentPhase.Idle && updates.StatusText.StartsWith("Stock Library is up to date", StringComparison.Ordinal), 8000);
            Check($"components update: a check that couldn't reach the feed says so without asking again ('{updates.StatusText}', {Runs.Count(r => r.StartsWith("bb-linker", StringComparison.Ordinal))} Dragon runs)",
                updates.StatusText == "Stock Library is up to date. Couldn't check Dragon." && !chip.IsVisible);
            _dragonSays = null;

            // Everything current: the Components view says so in one line.
            Release("dragon", "1.0.4", [("bin/bb-linker/bb-linker.exe", "DRAGON:1.0.4")]);
            var final = ShowComponents(main, updates);
            Click(ButtonWithText(final, "Check for updates"));
            WaitFor(() => updates.StatusText == "Dragon and Stock Library are up to date.", 8000);
            Pump(80);
            Check($"components update: when nothing is newer the view says so ('{updates.StatusText}', {updates.Phase})",
                updates.StatusText == "Dragon and Stock Library are up to date." && updates.Phase == ComponentPhase.Idle && !chip.IsVisible);
            Shot(final, "components-update-up-to-date");
            Key(final, Avalonia.Input.Key.Escape);
            WaitFor(() => !final.IsVisible);
        }
        finally
        {
            vm.IsBuildRunning = false;
            _inUse = null;
            Environment.SetEnvironmentVariable("BLACKBIRD_BB_LINKER", priorLinker);
            Environment.SetEnvironmentVariable(Updater.FeedVariable, priorFeed);
            Pump(100);
        }
    }

    // ── The stand-ins ───────────────────────────────────────────────────────

    private static void Prepare()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DragonExe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(StockExe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(PluginDll)!);
        File.WriteAllText(Path.Combine(Bo3, "BlackOps3.exe"), "game");
        File.WriteAllText(Path.Combine(Bo3, "bin", "plugins", "RadiantPlugins.dll"), "mtplugins");
        File.WriteAllText(DragonExe, "DRAGON:1.0.0");
        File.WriteAllText(StockExe, "STOCKLIB:1.0.0:old-cli");
        File.WriteAllText(PluginDll, "PLUGIN:1.0.0");
        File.WriteAllText(PluginIni, "KEEP-ME");
        SetRegistry("dragon", "1.0.0");
        SetRegistry("stocklib", "1.0.0");
    }

    /// <summary>What Dragon and the Stock Library write on every run: the version and where they are.</summary>
    private static void SetRegistry(string id, string version)
    {
        var path = ComponentPaths.Entry(Bo3, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { id, version, path = id == "dragon" ? DragonExe : StockExe }));
    }

    /// <summary>
    /// The components' CLIs. An exe's text is its identity: "DRAGON:1.0.1" is Dragon 1.0.1, "BAD:" won't start, and a
    /// Stock Library marked ":old-cli" answers update-check with an error, as one built before the contract does.
    /// </summary>
    private static Task<CliResult> FakeRun(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment)
    {
        Runs.Add($"{Path.GetFileName(exe)} {string.Join(' ', args)}");
        // Like the real components, which delete their own <file>.old at the start of every run: an install must not rely on it for its rollback.
        if (File.Exists(exe + ".old"))
            File.Delete(exe + ".old");
        var text = File.Exists(exe) ? File.ReadAllText(exe) : "";
        if (text.StartsWith("BAD", StringComparison.Ordinal) || text.Length == 0)
            return Task.FromResult(new CliResult(false, -1, ""));
        if (text.StartsWith("ULTRASOUND:", StringComparison.Ordinal))
            return Task.FromResult(UltrasoundRun(args, text));
        var version = text.Split(':')[1];
        // The contract's answer: it always exits 0, and trouble (error, latest null) is part of the JSON.
        string Json(Answer? say)
        {
            var latest = say is null ? version : say.Latest;
            return JsonSerializer.Serialize(new
            {
                current = version,
                latest,
                update_available = latest is not null && latest != version,
                url = latest is null ? null : $"https://github.com/Blakintosh/{(version.Length > 0 && text.StartsWith("DRAGON", StringComparison.Ordinal) ? "dragon" : "stock-library")}/releases/tag/v{latest}",
                digest = say is { Digest.Length: > 0 } ? "sha256:" + say.Digest : null,
                notes = say?.Notes,
                bundle_url = say is { BundleUrl.Length: > 0 } ? say.BundleUrl : null,
                installer_url = (string?)null,
                checked_at = 1791900000,
                cached = false,
                error = say?.Error,
            });
        }
        switch (args[0])
        {
            case "-capabilities":
                return Task.FromResult(new CliResult(true, 0, JsonSerializer.Serialize(new { id = "dragon", version, capabilities = new[] { "stock-carry" } })));
            case "-check-update":
                return Task.FromResult(_dragonSays is { } says
                    ? new CliResult(true, 0, Json(says))
                    : new CliResult(true, 2, "unknown option: -check-update\nusage: bb-linker [-progress pipe] -modsource zone"));
            case "update-check" when text.EndsWith(":old-cli", StringComparison.Ordinal):
                return Task.FromResult(new CliResult(true, 2, "error: unrecognized subcommand 'update-check'"));
            case "update-check":
                return Task.FromResult(new CliResult(true, 0, Json(_stockSays)));
            default:
                return Task.FromResult(new CliResult(true, 1, ""));
        }
    }

    /// <summary>Writes a GitHub-shaped release (feeds/&lt;id&gt;/latest.json) and its bundle. Returns the zip.</summary>
    private static string Release(string id, string version, (string Path, string Text)[] files, int filler = 0)
    {
        var folder = Path.Combine(Feeds, id);
        Directory.CreateDirectory(folder);
        var zipName = $"{id}-{version}-win-x64.zip";
        var zipPath = Path.Combine(folder, zipName);
        File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (path, text) in files)
            {
                using var stream = zip.CreateEntry(path).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }

            if (filler > 0)
            {
                // A file the component doesn't install, with enough bytes (that don't compress) for the download to have a
                // middle to stop in. It is never written to the install.
                var bytes = new byte[filler];
                new Random(1).NextBytes(bytes);
                using var stream = zip.CreateEntry("docs/notes.bin").Open();
                stream.Write(bytes);
            }
        }

        var repo = id switch { "dragon" => "Blakintosh/dragon", "ultrasound" => "Blakintosh/ultrasound", _ => "Blakintosh/stock-library" };
        File.WriteAllText(Path.Combine(folder, "latest.json"), JsonSerializer.Serialize(new
        {
            tag_name = "v" + version,
            html_url = $"https://github.com/{repo}/releases/tag/v{version}",
            body = $"## {version}\n\n- A fix\n- Another fix",
            assets = new object[]
            {
                new
                {
                    name = zipName,
                    browser_download_url = new Uri(zipPath).AbsoluteUri,
                    size = new FileInfo(zipPath).Length,
                    digest = "sha256:" + Sha(zipPath),
                },
            },
        }));
        return zipPath;
    }

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    // ── The CLI's answer ────────────────────────────────────────────────────

    private static void ParsesTheCli()
    {
        var good = ComponentUpdater.ParseCheck("""{"current":"1.0.0","latest":"v1.1.0","update_available":true,"url":"https://github.com/Blakintosh/dragon/releases/tag/v1.1.0","digest":"sha256:ABCD","notes":"## 1.1.0\n- one","bundle_url":"https://x/dragon-1.1.0-win-x64.zip","installer_url":null,"error":null}""");
        Check($"components update: the check JSON is read (latest {good?.Latest}, bundle {good?.BundleUrl}, notes '{good?.Notes}')",
            good is { Current: "1.0.0", Latest: "1.1.0", PageUrl: "https://github.com/Blakintosh/dragon/releases/tag/v1.1.0", BundleUrl: "https://x/dragon-1.1.0-win-x64.zip", Notes: "• one", Error: null }
            && good.Digest is null);
        var hex = new string('a', 64);
        Check("components update: a digest is lower-cased and loses its sha256: prefix",
            ComponentUpdater.ParseCheck($$"""{"latest":"1.0.1","digest":"sha256:{{hex.ToUpperInvariant()}}"}""")?.Digest == hex);
        var trouble = ComponentUpdater.ParseCheck("""{"current":"1.0.0","latest":null,"update_available":false,"error":"the feed can't be reached"}""");
        Check("components update: trouble is an answer too (latest null, an error)", trouble is { Latest: null, Error: "the feed can't be reached" });
        Check("components update: usage text and other JSON are not an answer",
            ComponentUpdater.ParseCheck("usage: bb-linker [-progress pipe]") is null && ComponentUpdater.ParseCheck("""{"id":"dragon","version":"1.0.0"}""") is null
            && ComponentUpdater.ParseCheck("") is null);
    }

    // ── Looking at it ───────────────────────────────────────────────────────

    private static ComponentsDialog ShowComponents(MainWindow main, ComponentUpdater updates)
    {
        var report = ComponentScanner.Scan(Bo3, Bo3, true);
        var dialog = new ComponentsDialog { DataContext = new ComponentsDialogViewModel(report, updates) };
        _ = dialog.ShowDialog(main);
        var shown = Owned<ComponentsDialog>(main);
        Pump(150);
        return shown;
    }

    private static string PanelTitle(MainWindow main) =>
        Find<ComponentUpdatePanel>(main).FirstOrDefault()?.FindControl<TextBlock>("ComponentUpdateTitle")?.Text ?? "";

    private static IEnumerable<string> PanelButtons(MainWindow main) =>
        Find<ComponentUpdatePanel>(main).SelectMany(Find<Button>).Where(b => b.IsEffectivelyVisible && b.Content is string text && text != "Release notes")
            .Select(b => (string)b.Content!);

    private static Button PanelButton(MainWindow main, string text) =>
        Find<ComponentUpdatePanel>(main).SelectMany(Find<Button>).First(b => b.IsEffectivelyVisible && b.Content as string == text);
}
