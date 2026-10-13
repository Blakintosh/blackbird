using System.Text.Json;
using Avalonia.Controls;
using Blackbird.Models;
using Blackbird.Services;
using Blackbird.ViewModels;
using Gscode.Updates;
using static Blackbird.Shots.Program;

namespace Blackbird.Shots;

/// <summary>
/// Ultrasound as a component Blackbird updates: Treyarch's own snd_convert.exe is never taken for it, a registered Ultrasound is
/// checked through its <c>--check-update --json</c> (or the release feed when it predates the flag), badged in Tools > Components
/// with an Update button and no "Get Ultrasound" link beside it, installed (exe and libFLAC.dll from the game's sound folder, not
/// the tools' bin) when no build is running and nothing is converting, and rolled back when the new exe won't start or reports
/// another version. The stand-in Ultrasound is a text file: "ULTRASOUND:1.1.1" is Ultrasound 1.1.1.
/// </summary>
internal static partial class ComponentUpdateScenario
{
    private static Answer? _ultrasoundSays;
    private static string UsExe => Path.Combine(Bo3, "sound", "snd_convert.exe");
    private static string UsFlac => Path.Combine(Bo3, "sound", "libFLAC.dll");

    private static CliResult UltrasoundRun(IReadOnlyList<string> args, string text)
    {
        var version = text.Split(':')[1];
        switch (args[0])
        {
            case "--capabilities":
                return new CliResult(true, 0, JsonSerializer.Serialize(new { id = "ultrasound", version, features = new[] { "stock-sounds" } }));
            case "--check-update" when text.EndsWith(":old-cli", StringComparison.Ordinal):
                return new CliResult(true, 2, "error: unexpected argument '--check-update' found");
            case "--check-update":
                var say = _ultrasoundSays;
                var latest = say is null ? version : say.Latest;
                return new CliResult(true, 0, JsonSerializer.Serialize(new
                {
                    current = version,
                    latest,
                    update_available = latest is not null && latest != version,
                    url = latest is null ? null : $"https://github.com/Blakintosh/ultrasound/releases/tag/v{latest}",
                    digest = say is { Digest.Length: > 0 } ? "sha256:" + say.Digest : null,
                    notes = say?.Notes,
                    bundle_url = say is { BundleUrl.Length: > 0 } ? say.BundleUrl : null,
                    installer_url = (string?)null,
                    checked_at = 1791900000,
                    cached = false,
                    error = say?.Error,
                }));
            default:
                return new CliResult(true, 1, "");
        }
    }

    private static void SetUltrasoundRegistry(string version)
    {
        var path = ComponentPaths.Entry(Bo3, "ultrasound");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { id = "ultrasound", version, path = UsExe }));
    }

    private static string UsRelease(string version, string exeText, string flacText) =>
        Release("ultrasound", version, [("sound/snd_convert.exe", exeText), ("sound/libFLAC.dll", flacText)]);

    public static void RunUltrasound(MainWindow main, MainWindowViewModel vm, ISettingsService settings)
    {
        var updates = vm.ComponentUpdates;
        var chip = main.FindControl<Button>("ComponentUpdateButton")!;
        var priorLinker = Environment.GetEnvironmentVariable("BLACKBIRD_DRAGON");
        var priorFeed = Environment.GetEnvironmentVariable(Updater.FeedVariable);
        Environment.SetEnvironmentVariable("BLACKBIRD_DRAGON", null);
        Environment.SetEnvironmentVariable(Updater.FeedVariable, null);
        _bo3Override = Path.Combine(FakeInstall.Root, "components-update-us", "Call of Duty Black Ops III");
        _toolsOverride = Path.Combine(FakeInstall.Root, "components-update-us", "tools-elsewhere");
        var installs = 0;
        void CountInstall(object? sender, EventArgs e) => installs++;
        updates.Installed += CountInstall;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UsExe)!);
            File.WriteAllText(Path.Combine(Bo3, "BlackOps3.exe"), "game");
            Pump(60);
            // Idle, and nothing left over from the Dragon and Stock Library steps.
            WaitFor(() => updates.Phase == ComponentPhase.Idle);

            // 1. Treyarch's snd_convert.exe is not Ultrasound: no row, no check, nothing run.
            File.WriteAllText(UsExe, "TREYARCH-SND-CONVERT");
            File.WriteAllText(UsFlac, "FLAC:treyarch");
            var treyarch = ComponentScanner.Scan(Bo3, Bo3, true).Rows.First(r => r.Id == "ultrasound");
            Runs.Clear();
            _ = updates.CheckAsync(userAsked: true);
            WaitFor(() => updates.StatusText == "No updatable components are installed.", 8000);
            Check($"ultrasound update: Treyarch's converter is not Ultrasound ('{treyarch.StateText}', '{updates.StatusText}', {Runs.Count} runs)",
                treyarch.State == ComponentState.Missing && Runs.Count == 0 && updates.Phase == ComponentPhase.Idle && !chip.IsVisible);
            var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            if (File.Exists(notepad))
            {
                File.Copy(notepad, UsExe, overwrite: true);
                Check("ultrasound update: a real exe that is not Ultrasound and has no registry entry is not Ultrasound either",
                    !ComponentUpdater.IsInstalled(ComponentSpecs.Ultrasound, UsExe, Bo3));
            }

            // 2. A registered Ultrasound 1.1.0, and 1.1.1 on offer through its own --check-update --json.
            File.WriteAllText(UsExe, "ULTRASOUND:1.1.0");
            File.WriteAllText(UsFlac, "FLAC:1.1.0");
            SetUltrasoundRegistry("1.1.0");
            Check("ultrasound update: a registered copy is Ultrasound", ComponentUpdater.IsInstalled(ComponentSpecs.Ultrasound, UsExe, Bo3)
                && ComponentScanner.Scan(Bo3, Bo3, true).Rows.First(r => r.Id == "ultrasound").StateText == "1.1.0 · installed");
            var zip = UsRelease("1.1.1", "ULTRASOUND:1.1.1", "FLAC:1.1.1");
            _ultrasoundSays = new Answer("1.1.1", new Uri(zip).AbsoluteUri, Sha(zip), "## Ultrasound 1.1.1\n\n- Shipped sounds keep their loop points");
            Runs.Clear();
            _ = updates.CheckAsync(userAsked: true);
            WaitFor(() => updates.Phase == ComponentPhase.Ready, 8000);
            Pump(80);
            Check($"ultrasound update: its own check finds 1.1.1 ({string.Join("; ", Runs.Take(2))}, chip '{updates.ChipText}')",
                Runs.Contains("snd_convert.exe --check-update --json") && updates.Find("ultrasound") is { Latest: "1.1.1", Current: "1.1.0" }
                && chip.IsVisible && updates.ChipText == "Ultrasound update" && updates.Items.Count == 1);
            OpenFlyout(chip);
            Check($"ultrasound update: the flyout names it ('{PanelTitle(main)}', '{updates.Detail}')",
                PanelTitle(main) == "Ultrasound 1.1.1 is available" && updates.Detail == "You have Ultrasound 1.1.0.");
            Shot(main, "components-update-ultrasound-ready");
            Click(PanelButton(main, "Later"));
            Pump(60);

            // 3. Tools > Components: the badge and an Update button, and no Get link beside it.
            var dialog = ShowComponents(main, updates);
            var rows = ((ComponentsDialogViewModel)dialog.DataContext!).Rows.ToDictionary(r => r.Id);
            var visible = Find<Button>(dialog).Where(b => b.IsEffectivelyVisible).ToList();
            Check($"ultrasound update: the row is badged ('{rows["ultrasound"].Update?.StateText}')", rows["ultrasound"].Update?.StateText == "1.1.1 available · you have 1.1.0");
            Check("ultrasound update: it has an Update button", visible.Count(b => b.Name == "InstallUpdateButton") == 1);
            Check($"ultrasound update: no Get link beside Update, while a missing component keeps its link ({string.Join(", ", visible.Select(b => b.Content).OfType<string>().Where(c => c.StartsWith("Get", StringComparison.Ordinal)))})",
                !visible.Any(b => b.Content as string == "Get Ultrasound") && visible.Any(b => b.Content as string == "Get Dragon") && !rows["ultrasound"].HasAction);
            Shot(dialog, "components-update-ultrasound-badge");
            Shot150(dialog, "components-update-ultrasound-badge@1.5x");

            // 4. A build is running: the install waits, then goes; the files come from the game's sound folder.
            vm.IsBuildRunning = true;
            Click(visible.First(b => b.Name == "InstallUpdateButton"));
            WaitFor(() => updates.Phase == ComponentPhase.Waiting);
            Pump(80);
            var status = dialog.FindControl<TextBlock>("ComponentsStatus")!;
            Check($"ultrasound update: during a build Update waits and says why ('{status.Text}')",
                updates.Phase == ComponentPhase.Waiting && status.Text == "A build is running. It installs when that finishes." && File.ReadAllText(UsExe) == "ULTRASOUND:1.1.0");
            Shot(dialog, "components-update-ultrasound-waiting");
            vm.IsBuildRunning = false;
            WaitFor(() => updates.Phase == ComponentPhase.Idle || updates.Phase == ComponentPhase.Installed, 8000);
            Pump(80);
            Check($"ultrasound update: the build ends and Ultrasound installs ({updates.Phase}, {File.ReadAllText(UsExe)}, {File.ReadAllText(UsFlac)}, {installs} installs, '{status.Text}')",
                File.ReadAllText(UsExe) == "ULTRASOUND:1.1.1" && File.ReadAllText(UsFlac) == "FLAC:1.1.1" && installs == 1 && status.Text == "Ultrasound 1.1.1 is installed."
                && !File.Exists(UsExe + ".old") && !File.Exists(UsExe + ".new") && !File.Exists(UsFlac + ".old") && !File.Exists(UsFlac + ".new")
                && !Directory.Exists(Path.Combine(_toolsOverride!, "sound")) && !Directory.Exists(Path.Combine(Bo3, "bin")));
            Shot(dialog, "components-update-ultrasound-installed");
            Key(dialog, Avalonia.Input.Key.Escape);
            WaitFor(() => !dialog.IsVisible);
            SetUltrasoundRegistry("1.1.1");

            // 5. Something is converting: it waits for it, looking in the game folder, then installs.
            zip = UsRelease("1.1.2", "ULTRASOUND:1.1.2", "FLAC:1.1.2");
            _ultrasoundSays = new Answer("1.1.2", new Uri(zip).AbsoluteUri, Sha(zip));
            _ = updates.CheckAsync(userAsked: true);
            WaitFor(() => updates.Phase == ComponentPhase.Ready, 8000);
            OpenFlyout(chip);
            _inUse = "Ultrasound is converting sounds right now. It installs when that finishes.";
            WaitFor(() => updates.InstallText == "Install when free");
            Click(PanelButton(main, "Install when free"));
            WaitFor(() => updates.Phase == ComponentPhase.Waiting);
            Pump(80);
            Check($"ultrasound update: while snd_convert runs it waits ('{updates.Detail}', looked in '{Path.GetFileName(Path.GetDirectoryName(_inUseRoot))}')",
                updates.Phase == ComponentPhase.Waiting && updates.Detail == _inUse && _inUseRoot == Bo3 && File.ReadAllText(UsExe) == "ULTRASOUND:1.1.1");
            Shot(main, "components-update-ultrasound-blocked");
            _inUse = null;
            WaitFor(() => updates.Phase == ComponentPhase.Installed, 8000);
            Check($"ultrasound update: it installs the moment nothing is converting ({File.ReadAllText(UsExe)})", File.ReadAllText(UsExe) == "ULTRASOUND:1.1.2" && installs == 2);
            Click(PanelButton(main, "Close"));
            SetUltrasoundRegistry("1.1.2");

            // 6. A new Ultrasound that won't start: the old one, libFLAC.dll included, is put back.
            zip = UsRelease("1.1.3", "BAD:1.1.3", "FLAC:1.1.3");
            _ultrasoundSays = new Answer("1.1.3", new Uri(zip).AbsoluteUri, Sha(zip));
            _ = updates.CheckAsync(userAsked: true);
            WaitFor(() => updates.Phase == ComponentPhase.Ready, 8000);
            OpenFlyout(chip);
            Click(PanelButton(main, "Install"));
            WaitFor(() => updates.Phase == ComponentPhase.Failed, 8000);
            Pump(80);
            Check($"ultrasound update: a new Ultrasound that doesn't start is rolled back ('{PanelTitle(main)}', '{updates.Detail}')",
                PanelTitle(main) == "Couldn't update Ultrasound" && updates.Detail == "The new Ultrasound didn't start. The old version was put back."
                && File.ReadAllText(UsExe) == "ULTRASOUND:1.1.2" && File.ReadAllText(UsFlac) == "FLAC:1.1.2"
                && !File.Exists(UsExe + ".old") && !File.Exists(UsExe + ".new") && !File.Exists(UsFlac + ".new") && installs == 2);
            Shot(main, "components-update-ultrasound-failed");
            Click(PanelButton(main, "Later"));

            // 6b. One that starts but reports another version.
            zip = UsRelease("1.1.4", "ULTRASOUND:9.9.9", "FLAC:1.1.4");
            _ultrasoundSays = new Answer("1.1.4", new Uri(zip).AbsoluteUri, Sha(zip));
            _ = updates.CheckAsync(userAsked: true);
            WaitFor(() => updates.Phase == ComponentPhase.Ready, 8000);
            OpenFlyout(chip);
            Click(PanelButton(main, "Install"));
            WaitFor(() => updates.Phase == ComponentPhase.Failed, 8000);
            Pump(80);
            Check($"ultrasound update: one that reports another version is rolled back too ('{updates.Detail}')",
                updates.Detail == "The new Ultrasound reports 9.9.9, not 1.1.4. The old version was put back."
                && File.ReadAllText(UsExe) == "ULTRASOUND:1.1.2" && File.ReadAllText(UsFlac) == "FLAC:1.1.2");
            Click(PanelButton(main, "Later"));

            // 7. An Ultrasound that predates --check-update: the feed answers (GSCODE_UPDATE_FEED with {id}).
            File.WriteAllText(UsExe, "ULTRASOUND:1.0.0:old-cli");
            SetUltrasoundRegistry("1.0.0");
            UsRelease("1.1.5", "ULTRASOUND:1.1.5", "FLAC:1.1.5");
            Environment.SetEnvironmentVariable(Updater.FeedVariable, new Uri(Path.Combine(Feeds, "@@", "latest.json")).AbsoluteUri.Replace("@@", "{id}", StringComparison.Ordinal));
            Runs.Clear();
            _ = updates.CheckAsync(userAsked: true);
            WaitFor(() => updates.Phase == ComponentPhase.Ready, 8000);
            Check($"ultrasound update: an old build falls back to the release feed ({string.Join("; ", Runs.Take(2))}, {updates.Find("ultrasound")?.Latest})",
                Runs.Contains("snd_convert.exe --check-update --json") && updates.Find("ultrasound") is { Latest: "1.1.5", Current: "1.0.0" });
            OpenFlyout(chip);
            Click(PanelButton(main, "Install"));
            WaitFor(() => updates.Phase == ComponentPhase.Installed, 8000);
            Check($"ultrasound update: installed from the feed's bundle ({File.ReadAllText(UsExe)})", File.ReadAllText(UsExe) == "ULTRASOUND:1.1.5" && installs == 3);
            Click(PanelButton(main, "Close"));
            SetUltrasoundRegistry("1.1.5");
            Environment.SetEnvironmentVariable(Updater.FeedVariable, null);

            // 8. Current: one calm line.
            _ultrasoundSays = null;
            var final = ShowComponents(main, updates);
            Click(ButtonWithText(final, "Check for updates"));
            WaitFor(() => updates.StatusText == "Ultrasound is up to date.", 8000);
            Pump(80);
            Check($"ultrasound update: when nothing is newer the view says so ('{updates.StatusText}')", updates.StatusText == "Ultrasound is up to date." && !chip.IsVisible);
            Shot(final, "components-update-ultrasound-up-to-date");
            Key(final, Avalonia.Input.Key.Escape);
            WaitFor(() => !final.IsVisible);
        }
        finally
        {
            updates.Installed -= CountInstall;
            vm.IsBuildRunning = false;
            _inUse = null;
            _ultrasoundSays = null;
            _bo3Override = null;
            _toolsOverride = null;
            Environment.SetEnvironmentVariable("BLACKBIRD_DRAGON", priorLinker);
            Environment.SetEnvironmentVariable(Updater.FeedVariable, priorFeed);
            Pump(100);
        }
    }
}
