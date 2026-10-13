using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Gscode.Updates;

namespace Blackbird.Services;

/// <summary>A file a component's release bundle installs. Paths are relative to the Black Ops III folder.</summary>
/// <param name="RequiresFile">Installed only when this file (same base folder) exists: the Radiant plugin needs MTPlugins.</param>
/// <param name="KeepExisting">A settings file: written when it isn't there, never replaced.</param>
public sealed record ComponentFile(string Path, string? RequiresFile = null, bool KeepExisting = false);

/// <summary>
/// A closed-source component Blackbird keeps up to date for the user: where it installs, which release feed it reads and
/// how to ask it (its CLI) whether a newer release exists. Releases follow gscode-installer/docs/release-contract.md:
/// <c>&lt;id&gt;-X.Y.Z-win-x64.zip</c> laid out relative to the Black Ops III folder, verified by the asset's digest.
/// </summary>
public sealed record ComponentSpec(
    string Id,
    string DisplayName,
    string Repository,
    string ExePath,
    IReadOnlyList<ComponentFile> Files,
    IReadOnlyList<string> CheckArgs,
    IReadOnlyList<string> VerifyArgs,
    string VerifyField,
    IReadOnlyList<string> ProcessNames,
    string? PluginProcess = null)
{
    /// <summary>The plugin file, when this component has one (Radiant loads it).</summary>
    public ComponentFile? Plugin => Files.FirstOrDefault(f => f.RequiresFile is not null && !f.KeepExisting);
}

public static class ComponentSpecs
{
    public static readonly ComponentSpec Dragon = new(
        "dragon", "Dragon", "Blakintosh/dragon", "bin/bb-linker/bb-linker.exe",
        [new("bin/bb-linker/bb-linker.exe")],
        CheckArgs: ["-check-update", "-json"],
        VerifyArgs: ["-capabilities"], VerifyField: "version",
        ProcessNames: ["bb-linker"]);

    public static readonly ComponentSpec StockLibrary = new(
        "stocklib", "Stock Library", "Blakintosh/stock-library", "bin/stocklib/stocklib.exe",
        [
            new("bin/stocklib/stocklib.exe"),
            new("bin/plugins/StockLibrary/StockLibrary.dll", RequiresFile: "bin/plugins/RadiantPlugins.dll"),
            new("bin/plugins/StockLibrary/StockLibrary.ini", RequiresFile: "bin/plugins/RadiantPlugins.dll", KeepExisting: true),
        ],
        CheckArgs: ["update-check", "--json"],
        VerifyArgs: ["update-check", "--json"], VerifyField: "current",
        ProcessNames: ["stocklib"], PluginProcess: "radiant_modtools");

    public static readonly IReadOnlyList<ComponentSpec> All = [Dragon, StockLibrary];
}

/// <summary>What a component's CLI printed.</summary>
public sealed record CliResult(bool Started, int ExitCode, string Output);

/// <summary>What <c>bb-linker -check-update -json</c> / <c>stocklib update-check --json</c> answers.</summary>
public sealed record CheckInfo(string? Current, string? Latest, string? PageUrl, string? BundleUrl, string? Digest, string? Notes, string? Error);

public enum ComponentItemState { Available, Downloading, Ready, Installing, Installed, Failed }

public enum ComponentPhase { Idle, Checking, Downloading, Ready, Waiting, Installing, Installed, Failed }

/// <summary>One component with a newer release.</summary>
public sealed partial class ComponentUpdate : ObservableObject
{
    public required ComponentSpec Spec { get; init; }
    public string Id => Spec.Id;
    public string Name => Spec.DisplayName;
    public string? Current { get; init; }
    public required string Latest { get; init; }
    public string? Notes { get; init; }
    public string? PageUrl { get; init; }
    public required string BundleUrl { get; init; }
    public long BundleSize { get; init; }
    public required string Digest { get; init; }

    public string? BundlePath { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText), nameof(IsWorking), nameof(IsFailed), nameof(IsInstalled), nameof(CanInstallNow))]
    private ComponentItemState _state = ComponentItemState.Available;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText))]
    private double _progress;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText))]
    private string _error = "";

    /// <summary>The release page, from the item's notes link. Only https pages open.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenNotes()
    {
        if (PageUrl is { } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ShellLauncher.TryOpen(url);
    }

    public bool HasPage => PageUrl is not null;

    /// <summary>"Dragon 1.0.1".</summary>
    public string Title => $"{Name} {Latest}";
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    public bool IsWorking => State is ComponentItemState.Downloading or ComponentItemState.Installing;
    public bool IsFailed => State == ComponentItemState.Failed;
    public bool IsInstalled => State == ComponentItemState.Installed;

    /// <summary>The row's Install button: downloaded, and nothing is working on it.</summary>
    public bool CanInstallNow => BundlePath is not null && State is ComponentItemState.Ready or ComponentItemState.Failed;

    /// <summary>The badge on the component's row in Tools > Components: the version, or how the update is going.</summary>
    public string StateText => State switch
    {
        ComponentItemState.Downloading => $"Downloading… {(int)(Progress * 100)}%",
        ComponentItemState.Installing => "Installing…",
        ComponentItemState.Installed => $"Updated to {Latest}",
        ComponentItemState.Failed => Error,
        _ => Current is { Length: > 0 } ? $"{Latest} available · you have {Current}" : $"{Latest} available",
    };
}

public sealed class ComponentUpdaterOptions
{
    public required Func<string> GamePath { get; init; }
    public required Func<string> ToolsPath { get; init; }

    /// <summary>Whether the automatic check runs (the Help > Updates setting). A check the user asks for always runs.</summary>
    public Func<bool> Enabled { get; init; } = () => true;

    /// <summary>Why the components can't be replaced right now (stocklib open in Apex or Radiant, a link under way), or null.</summary>
    public Func<string, IReadOnlyList<ComponentSpec>, string?> InUseReason { get; init; } = ComponentUpdater.RunningProcessReason;

    /// <summary>Runs a component's CLI. Replaced by the screenshot harness.</summary>
    public Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, Task<CliResult>> Run { get; init; } = ComponentUpdater.RunCli;

    public string? LogFile { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan BusyPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Where bundles are downloaded.</summary>
    public string DownloadFolder { get; init; } = Path.Combine(Path.GetTempPath(), "gscode-updates");
}

/// <summary>
/// Keeps Dragon and the Stock Library up to date from inside Blackbird: a quiet daily check through each component's own
/// CLI (or straight against the release feed when the CLI is older than that contract), a quiet digest-verified download,
/// and one combined notification in the title bar with Install / Later. Installing swaps the files in with the
/// <c>.old</c> trick (a running exe can be renamed), checks the new exe runs and reports the version, and puts the old
/// files back if anything fails. It never runs during a build, and waits while Apex or Radiant has the Stock Library open.
/// One instance, driven from the UI thread; all disk and process work runs off it.
/// </summary>
public sealed partial class ComponentUpdater : ObservableObject
{
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly ComponentUpdaterOptions _options;
    private bool _snoozed;
    private bool _watching;
    private string _blocker = "";
    private string _failTitle = "";
    private string _failReason = "";

    public ComponentUpdater(ComponentUpdaterOptions options)
    {
        _options = options;
        InstallCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => _ = InstallAsync(), () => CanInstall);
        LaterCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(Later);
        CheckNowCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => _ = CheckAsync(userAsked: true), () => Phase != ComponentPhase.Checking);
    }

    public CommunityToolkit.Mvvm.Input.IRelayCommand InstallCommand { get; }
    public CommunityToolkit.Mvvm.Input.IRelayCommand LaterCommand { get; }
    public CommunityToolkit.Mvvm.Input.IRelayCommand CheckNowCommand { get; }

    /// <summary>Raised on the UI thread once files were replaced, so Blackbird can look at its tools again.</summary>
    public event EventHandler? Installed;

    /// <summary>Why nothing may touch the tools right now (a build, a link, a publish), as a phrase ("a build is running"), or null.</summary>
    public Func<string?> BusyReason { get; set; } = () => null;

    public IReadOnlyList<ComponentUpdate> Items { get; private set; } = [];
    public ComponentPhase Phase { get; private set; }

    /// <summary>One line on how the last check went, for the Components view; empty until one finished.</summary>
    public string StatusText { get; private set; } = "";

    public ComponentUpdate? Find(string id) => Items.FirstOrDefault(i => i.Id == id);

    // ── What the title bar shows ────────────────────────────────────────────

    /// <summary>The chip shows while there is something for the user: downloaded, waiting, installing, done or failed.</summary>
    public bool IsShown => !_snoozed && Phase is ComponentPhase.Downloading or ComponentPhase.Ready or ComponentPhase.Waiting
        or ComponentPhase.Installing or ComponentPhase.Installed or ComponentPhase.Failed;

    public bool IsPending => Phase is ComponentPhase.Ready or ComponentPhase.Waiting or ComponentPhase.Installed;
    public bool IsFailed => Phase == ComponentPhase.Failed;
    public bool ShowsArrow => !IsFailed;
    public bool IsDownloading => Phase == ComponentPhase.Downloading;
    public bool IsInstalling => Phase == ComponentPhase.Installing;
    public bool IsWorking => Phase is ComponentPhase.Downloading or ComponentPhase.Installing;

    public string ChipText => Phase switch
    {
        ComponentPhase.Downloading => "Downloading…",
        ComponentPhase.Ready => Shown.Count == 1 ? $"{Shown[0].Name} update" : $"{Shown.Count} component updates",
        ComponentPhase.Waiting => "Update waiting",
        ComponentPhase.Installing => "Installing…",
        ComponentPhase.Installed => "Components updated",
        ComponentPhase.Failed => "Update failed",
        _ => "",
    };

    /// <summary>"Dragon 1.0.1 and Stock Library 1.0.1".</summary>
    public string Names => Join(Shown.Select(i => i.Title));

    /// <summary>What the control talks about: what was just installed, or else what still waits.</summary>
    private List<ComponentUpdate> Shown => Phase == ComponentPhase.Installed ? Items.Where(i => i.IsInstalled).ToList() : Items.Where(i => !i.IsInstalled).ToList();

    public string Title => Phase switch
    {
        ComponentPhase.Downloading => $"Downloading {Names}…",
        ComponentPhase.Ready => $"{Names} {(Shown.Count == 1 ? "is" : "are")} available",
        ComponentPhase.Waiting => $"{Names} will install when it can",
        ComponentPhase.Installing => $"Installing {Names}…",
        ComponentPhase.Installed => $"{Names} {(Shown.Count == 1 ? "is" : "are")} installed",
        ComponentPhase.Failed => _failTitle,
        _ => "",
    };

    public string Detail => Phase switch
    {
        ComponentPhase.Downloading => "",
        ComponentPhase.Ready => string.IsNullOrEmpty(_blocker) ? $"You have {Join(Shown.Select(i => $"{i.Name} {i.Current ?? "(unknown)"}"))}." : _blocker,
        ComponentPhase.Waiting => _blocker,
        ComponentPhase.Installing => "",
        ComponentPhase.Installed => "Blackbird uses the new version from the next build.",
        ComponentPhase.Failed => _failReason,
        _ => "",
    };

    public bool HasDetail => Detail.Length > 0;
    public double Progress => Items.Count == 0 ? 0 : Items.Average(i => i.State is ComponentItemState.Ready or ComponentItemState.Installing or ComponentItemState.Installed ? 1 : i.Progress);
    public bool ShowsProgress => Phase is ComponentPhase.Downloading or ComponentPhase.Installing;
    public bool IsProgressIndeterminate => Phase == ComponentPhase.Installing;

    public bool ShowsNotes => Phase is ComponentPhase.Ready or ComponentPhase.Waiting;
    public bool CanInstall => Phase is ComponentPhase.Ready or ComponentPhase.Failed && Items.Any(i => !i.IsInstalled);
    public bool ShowsInstall => Phase is ComponentPhase.Ready or ComponentPhase.Failed;
    public bool ShowsLater => Phase is ComponentPhase.Ready or ComponentPhase.Waiting or ComponentPhase.Failed or ComponentPhase.Installed;
    public string InstallText => Phase == ComponentPhase.Failed ? "Try again" : string.IsNullOrEmpty(_blocker) ? "Install" : "Install when free";
    public string LaterText => Phase == ComponentPhase.Installed ? "Close" : Phase == ComponentPhase.Waiting ? "Don't wait" : "Later";

    /// <summary>The disabled Install button's tooltip: why it can't run yet.</summary>
    public string? InstallTip => string.IsNullOrEmpty(_blocker) ? null : _blocker;

    // ── Checking ────────────────────────────────────────────────────────────

    /// <summary>
    /// Looks for newer releases of every installed component and downloads them. <paramref name="userAsked"/>: the Components
    /// view's button, which runs even with the automatic check off and reports how it went in <see cref="StatusText"/>.
    /// </summary>
    public async Task CheckAsync(bool userAsked)
    {
        if (!userAsked && !_options.Enabled())
            return;
        if (Phase == ComponentPhase.Installed)
        {
            Items = Items.Where(i => !i.IsInstalled).ToList();
            Phase = Items.Count == 0 ? ComponentPhase.Idle : ComponentPhase.Ready;
        }

        if (Phase is ComponentPhase.Checking or ComponentPhase.Downloading or ComponentPhase.Installing or ComponentPhase.Waiting
            or ComponentPhase.Ready)
        {
            if (userAsked)
                _snoozed = false;
            Changed();
            return;
        }

        SetPhase(ComponentPhase.Checking);
        StatusText = userAsked ? "Checking for Dragon and Stock Library updates…" : "";
        Changed();

        // Never while a build or link runs: the CLI would compete with it for the disk.
        while (BusyReason() is { } busy)
        {
            if (userAsked)
            {
                StatusText = $"Waiting to check: {busy}";
                Changed();
            }
            await Task.Delay(_options.BusyPollInterval);
        }

        var root = Root();
        var game = _options.GamePath();
        var tools = await Task.Run(() => CheckAll(root, game, userAsked));
        var found = tools.Updates;
        StatusText = tools.Summary;
        if (found.Count == 0)
        {
            Items = [];
            SetPhase(ComponentPhase.Idle);
            return;
        }

        Items = found;
        _snoozed = false;
        await DownloadAllAsync();
    }

    private (List<ComponentUpdate> Updates, string Summary) CheckAll(string root, string game, bool userAsked)
    {
        CleanLeftovers(root);
        var updates = new List<ComponentUpdate>();
        var installed = new List<string>();
        var failed = new List<string>();
        foreach (var spec in ComponentSpecs.All)
        {
            var exe = Path.GetFullPath(Path.Combine(root, spec.ExePath));
            if (!File.Exists(exe))
                continue;
            installed.Add(spec.DisplayName);
            try
            {
                if (CheckOne(spec, exe, root, game) is { } update)
                    updates.Add(update);
            }
            catch (UpdateCheckException ex)
            {
                failed.Add(spec.DisplayName);
                Log($"{(userAsked ? "Check" : "Automatic check")} of {spec.DisplayName} failed: {ex.Message}");
            }
        }

        var summary = installed.Count == 0 ? "Dragon and Stock Library aren't installed."
            : updates.Count > 0 ? $"{Join(updates.Select(u => u.Title))} {(updates.Count == 1 ? "is" : "are")} available."
            : failed.Count == installed.Count ? $"Couldn't check for {Join(failed)} updates. Try again in a while."
            : failed.Count > 0 ? $"{Join(installed.Except(failed))} {(installed.Count - failed.Count == 1 ? "is" : "are")} up to date. Couldn't check {Join(failed)}."
            : $"{Join(installed)} {(installed.Count == 1 ? "is" : "are")} up to date.";
        return (updates, summary);
    }

    private ComponentUpdate? CheckOne(ComponentSpec spec, string exe, string root, string game)
    {
        var environment = BbLinker.InstallEnvironment(game, root);
        // The component's own check (it caches daily and knows its feed). A build that predates the flag answers with usage
        // text or an error that isn't JSON; then the same question is asked of the feed directly.
        var cli = _options.Run(exe, spec.CheckArgs, WithFeed(environment, spec)).GetAwaiter().GetResult();
        var info = cli.Started ? ParseCheck(cli.Output) : null;
        if (info is null)
            Log($"{spec.DisplayName} has no update check ({(cli.Started ? $"exit {cli.ExitCode}" : "didn't start")}); reading the release feed.");

        var current = info?.Current ?? CurrentVersionOf(spec, exe, root, game);
        if (current is null)
        {
            Log($"{spec.DisplayName}: its version can't be read, so no update is offered.");
            return null;
        }

        var latest = info?.Latest;
        if (info is not null)
        {
            // It always answers; trouble is part of the answer. It has asked the feed itself, so asking again would only repeat it.
            if (latest is null)
                return info.Error is { } why ? throw new UpdateCheckException(why) : null;
            if (Updater.CompareVersions(latest, current) <= 0)
                return null;
        }

        var (bundleUrl, digest, notes, page, size) = (info?.BundleUrl, info?.Digest, info?.Notes, info?.PageUrl, 0L);
        if (bundleUrl is null || digest is null)
        {
            // An answer without a bundle or its checksum can't be installed from; the feed's release has both.
            var release = FetchRelease(spec);
            if (latest is not null && Updater.CompareVersions(release.Version, latest) != 0)
                throw new UpdateCheckException($"the feed offers {release.Version}, not {latest}");
            latest = release.Version;
            if (release.BundleUrl.Length == 0)
                throw new UpdateCheckException($"release {latest} has no {spec.Id}-{latest}-win-x64.zip");
            bundleUrl = release.BundleUrl;
            digest = release.Digest;
            notes ??= release.Notes;
            page ??= release.PageUrl;
            size = release.BundleSize;
        }

        if (Updater.CompareVersions(latest!, current) <= 0)
            return null;
        if (digest is null)
            throw new UpdateCheckException($"release {latest} has no checksum, so it can't be installed");
        return new ComponentUpdate
        {
            Spec = spec,
            Current = current,
            Latest = latest!,
            Notes = notes,
            PageUrl = page ?? $"https://github.com/{spec.Repository}/releases/tag/v{latest}",
            BundleUrl = bundleUrl,
            BundleSize = size,
            Digest = digest,
        };
    }

    /// <summary>
    /// The check's JSON (<c>current</c>, <c>latest</c>, <c>update_available</c>, <c>url</c> the release page, <c>bundle_url</c>,
    /// <c>digest</c>, <c>notes</c>, <c>error</c>). Null for usage text or anything that isn't that object.
    /// </summary>
    public static CheckInfo? ParseCheck(string output)
    {
        var start = output.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
            return null;
        try
        {
            using var document = JsonDocument.Parse(output[start..], new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !(root.TryGetProperty("latest", out _) || root.TryGetProperty("current", out _)
                || root.TryGetProperty("update_available", out _)))
                return null;
            string? Text(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text.Trim() : null;
            var page = Text("url");
            var bundle = Text("bundle_url");
            // An earlier draft of the contract put the bundle in "url".
            if (bundle is null && page?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                (bundle, page) = (page, null);
            return new CheckInfo(Text("current"), Text("latest")?.TrimStart('v', 'V'), page, bundle, NormalizeDigest(Text("digest")),
                Preview(Text("notes")), Text("error"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeDigest(string? digest)
    {
        if (digest is null)
            return null;
        var hex = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..] : digest;
        hex = hex.Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    private string? CurrentVersionOf(ComponentSpec spec, string exe, string root, string game)
    {
        try
        {
            var text = spec.Id == "dragon" ? BbLinker.Probe(exe, BbLinker.InstallEnvironment(game, root))?.Version : null;
            text ??= FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0].Trim();
            if (string.IsNullOrEmpty(text) && File.Exists(ComponentPaths.Entry(game, spec.Id))
                && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ComponentPaths.Entry(game, spec.Id))) is System.Text.Json.Nodes.JsonObject entry
                && entry["version"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var registered)
                && (entry["path"] is not System.Text.Json.Nodes.JsonValue p || !p.TryGetValue<string>(out var path)
                    || string.Equals(Path.GetFullPath(path), exe, StringComparison.OrdinalIgnoreCase)))
                text = registered;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // ── The release feed ────────────────────────────────────────────────────

    /// <summary>The feed for a component: GSCODE_UPDATE_FEED with <c>{id}</c> replaced by the component's id, or GitHub's latest release.</summary>
    public static string FeedFor(ComponentSpec spec) =>
        Environment.GetEnvironmentVariable(Updater.FeedVariable) is { Length: > 0 } custom
            ? custom.Replace("{id}", spec.Id, StringComparison.Ordinal)
            : $"https://api.github.com/repos/{spec.Repository}/releases/latest";

    private static Dictionary<string, string> WithFeed(IReadOnlyDictionary<string, string> environment, ComponentSpec spec)
    {
        var copy = new Dictionary<string, string>(environment);
        if (Environment.GetEnvironmentVariable(Updater.FeedVariable) is { Length: > 0 })
            copy[Updater.FeedVariable] = FeedFor(spec);
        return copy;
    }

    private sealed record Release(string Version, string PageUrl, string? Notes, string BundleUrl, long BundleSize, string? Digest);

    private Release FetchRelease(ComponentSpec spec)
    {
        string json;
        try
        {
            var uri = new Uri(FeedFor(spec));
            if (uri.IsFile)
            {
                json = File.ReadAllText(uri.LocalPath);
            }
            else
            {
                using var timeout = new CancellationTokenSource(ApiTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd($"blackbird/{AppUpdates.CurrentVersion}");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = Http.SendAsync(request, timeout.Token).GetAwaiter().GetResult();
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new UpdateCheckException("no release has been published yet");
                if (!response.IsSuccessStatusCode)
                    throw new UpdateCheckException($"the feed answered {(int)response.StatusCode}");
                json = response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or UnauthorizedAccessException or UriFormatException)
        {
            throw new UpdateCheckException($"the feed can't be reached ({ex.GetType().Name})");
        }

        try
        {
            return ParseRelease(json, spec);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new UpdateCheckException("the feed's answer can't be read");
        }
    }

    private static Release ParseRelease(string json, ComponentSpec spec)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var version = root.GetProperty("tag_name").GetString()!.Trim().TrimStart('v', 'V');
        string? Text(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text ? text.Trim() : null;
        var bundleName = $"{spec.Id}-{version}-win-x64.zip";
        string url = "", digest = "";
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
                if (string.Equals(Text(asset, "name"), bundleName, StringComparison.OrdinalIgnoreCase))
                {
                    url = Text(asset, "browser_download_url") ?? "";
                    digest = NormalizeDigest(Text(asset, "digest")) ?? "";
                    size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                }

        return new Release(version, Text(root, "html_url") ?? $"https://github.com/{spec.Repository}/releases/tag/v{version}",
            Preview(Text(root, "body")), url, size, digest.Length == 0 ? null : digest);
    }

    /// <summary>The release notes' first few lines, with the Markdown taken out.</summary>
    private static string? Preview(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        var lines = body.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#')) // the headline repeats the version the notes are filed under
            .Select(l => Regex.Replace(l, @"\[([^\]]*)\]\([^)]*\)", "$1"))
            .Select(l => l.Replace("**", "").Replace("__", "").Replace("`", ""))
            .Select(l => Regex.Replace(l, @"^(#+|[-*+]|\d+\.)\s+", m => m.Value.TrimStart().StartsWith('#') ? "" : "• "))
            .Where(l => l.Length > 0)
            .Take(3)
            .ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    // ── Downloading ─────────────────────────────────────────────────────────

    private async Task DownloadAllAsync()
    {
        SetPhase(ComponentPhase.Downloading);
        foreach (var item in Items.Where(i => i.BundlePath is null).ToList())
        {
            item.Error = "";
            item.State = ComponentItemState.Downloading;
            var progress = new Progress<double>(p =>
            {
                item.Progress = p;
                Changed(nameof(Progress));
            });
            try
            {
                item.BundlePath = await Task.Run(() => DownloadAsync(item, progress));
                item.State = ComponentItemState.Ready;
            }
            catch (UpdateCheckException ex)
            {
                Log($"Download of {item.Title} failed: {ex.Message}");
                item.State = ComponentItemState.Failed;
                item.Error = $"Couldn't download {item.Title}. {Capitalize(ex.Message)}.";
                _failTitle = $"Couldn't download {item.Title}";
                _failReason = $"{Capitalize(ex.Message)}.";
            }
        }

        if (Items.Any(i => i.State == ComponentItemState.Failed))
        {
            SetPhase(ComponentPhase.Failed);
            return;
        }

        SetPhase(ComponentPhase.Ready);
        _ = WatchAsync();
    }

    private async Task<string> DownloadAsync(ComponentUpdate item, IProgress<double> progress)
    {
        var folder = Path.Combine(_options.DownloadFolder, item.Id);
        var path = Path.Combine(folder, $"{item.Id}-{item.Latest}-win-x64.zip");
        if (File.Exists(path) && HashOf(path) == item.Digest)
        {
            ValidateBundle(path, item.Spec);
            return path;
        }

        var part = path + ".part";
        try
        {
            Directory.CreateDirectory(folder);
            var uri = new Uri(item.BundleUrl);
            if (!(uri.IsFile || uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback))
                throw new UpdateCheckException("the download isn't offered over https");
            await using (var output = File.Create(part))
            {
                if (uri.IsFile)
                {
                    await using var input = File.OpenRead(uri.LocalPath);
                    await CopyAsync(input, output, input.Length, progress);
                }
                else
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.UserAgent.ParseAdd($"blackbird/{AppUpdates.CurrentVersion}");
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode)
                        throw new UpdateCheckException($"the server answered {(int)response.StatusCode}");
                    await using var input = await response.Content.ReadAsStreamAsync();
                    await CopyAsync(input, output, response.Content.Headers.ContentLength ?? item.BundleSize, progress);
                }
            }

            if (HashOf(part) != item.Digest)
                throw new UpdateCheckException("the download didn't match its checksum, so it wasn't kept");
            File.Move(part, path, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateCheckException("the download stopped; check your connection");
        }
        finally
        {
            TryDelete(part);
        }

        ValidateBundle(path, item.Spec);
        return path;
    }

    private static async Task CopyAsync(Stream input, Stream output, long total, IProgress<double> progress)
    {
        var step = Math.Max(total / 100, 65536);
        var buffer = new byte[81920];
        long received = 0, reported = 0;
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            received += read;
            if (received - reported >= step)
            {
                reported = received;
                progress.Report(total > 0 ? Math.Clamp((double)received / total, 0, 1) : 0);
            }
        }
        progress.Report(1);
    }

    private static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>A bundle holds the component's exe and only relative paths inside the Black Ops III folder.</summary>
    private static void ValidateBundle(string zipPath, ComponentSpec spec)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Where(e => e.Name.Length > 0).Select(e => Normalize(e.FullName)).ToList();
            if (names.Any(n => Path.IsPathRooted(n) || n.Split('/').Contains("..")))
                throw new UpdateCheckException("the download has files outside the Black Ops III folder");
            if (!names.Contains(Normalize(spec.ExePath), StringComparer.OrdinalIgnoreCase))
                throw new UpdateCheckException($"the download doesn't contain {spec.ExePath}");
        }
        catch (InvalidDataException)
        {
            throw new UpdateCheckException("the download isn't a readable zip");
        }
    }

    // ── Installing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Installs what is downloaded (all of it, or just <paramref name="id"/>'s). When a build is running or the Stock Library
    /// is open in Apex or Radiant it waits, and installs the moment that clears.
    /// </summary>
    public async Task InstallAsync(string? id = null)
    {
        if (Phase is not (ComponentPhase.Ready or ComponentPhase.Failed))
            return;
        // A download that failed is tried again first; the install is the click after it.
        if (Items.Any(i => i.BundlePath is null))
        {
            await DownloadAllAsync();
            return;
        }

        var targets = Items.Where(i => i.BundlePath is not null && !i.IsInstalled && (id is null || i.Id == id)).ToList();
        if (targets.Count == 0)
            return;
        foreach (var item in Items.Where(i => i.IsFailed && i.BundlePath is not null))
            item.State = ComponentItemState.Ready;

        var blocker = await BlockerAsync(targets);
        if (blocker is not null)
        {
            _blocker = blocker;
            StatusText = blocker;
            _pendingId = id;
            SetPhase(ComponentPhase.Waiting);
            _ = WatchAsync();
            return;
        }

        await ApplyAsync(targets);
    }

    private string? _pendingId;

    private Task<string?> BlockerAsync(IReadOnlyList<ComponentUpdate> targets)
    {
        var root = Root();
        var specs = targets.Select(t => t.Spec).ToList();
        return Task.Run(() => BusyReason() is { } busy ? $"{Capitalize(busy)}. It installs when that finishes." : _options.InUseReason(root, specs));
    }

    /// <summary>
    /// While an update is ready or waiting: keeps <see cref="InstallText"/> honest about whether Install runs now, and runs a
    /// waiting install the moment nothing blocks it. One loop at a time, off the UI thread except for the property changes.
    /// </summary>
    private async Task WatchAsync()
    {
        if (_watching)
            return;
        _watching = true;
        try
        {
            while (Phase is ComponentPhase.Ready or ComponentPhase.Waiting)
            {
                var targets = Items.Where(i => i.BundlePath is not null && !i.IsInstalled).ToList();
                var blocker = targets.Count == 0 ? null : await BlockerAsync(targets);
                if (Phase is not (ComponentPhase.Ready or ComponentPhase.Waiting))
                    break;
                if ((blocker ?? "") != _blocker)
                {
                    _blocker = blocker ?? "";
                    Changed();
                }

                if (Phase == ComponentPhase.Waiting && blocker is null)
                {
                    var only = _pendingId;
                    await ApplyAsync(Items.Where(i => i.BundlePath is not null && !i.IsInstalled && (only is null || i.Id == only)).ToList());
                    continue;
                }

                await Task.Delay(blocker is null && Phase == ComponentPhase.Ready ? _options.PollInterval * 3 : _options.PollInterval);
            }
        }
        finally
        {
            _watching = false;
        }
    }

    private async Task ApplyAsync(List<ComponentUpdate> targets)
    {
        _blocker = "";
        _pendingId = null;
        SetPhase(ComponentPhase.Installing);
        var root = Root();
        var game = _options.GamePath();
        var failures = 0;
        foreach (var item in targets)
        {
            item.State = ComponentItemState.Installing;
            Changed(nameof(Progress));
            var error = await Task.Run(() => Install(item, root, game));
            if (error is null)
            {
                item.State = ComponentItemState.Installed;
            }
            else
            {
                failures++;
                item.State = ComponentItemState.Failed;
                item.Error = error;
                _failTitle = $"Couldn't update {item.Name}";
                _failReason = error;
            }
        }

        var done = targets.Where(t => t.IsInstalled).ToList();
        if (done.Count > 0)
        {
            StatusText = $"{Join(done.Select(d => d.Title))} {(done.Count == 1 ? "is" : "are")} installed.";
            Installed?.Invoke(this, EventArgs.Empty);
        }

        if (failures > 0)
        {
            SetPhase(ComponentPhase.Failed);
        }
        else if (Items.Any(i => !i.IsInstalled))
        {
            // One was installed from Tools > Components; the other still waits for the user.
            Items = Items.Where(i => !i.IsInstalled).ToList();
            SetPhase(ComponentPhase.Ready);
            _ = WatchAsync();
        }
        else
        {
            SetPhase(ComponentPhase.Installed);
        }
    }

    /// <summary>Swaps the component in and checks the new exe runs; on any failure every file is put back. Null, or why in a sentence.</summary>
    private string? Install(ComponentUpdate item, string root, string game)
    {
        var spec = item.Spec;
        var exe = Path.GetFullPath(Path.Combine(root, spec.ExePath));
        var placed = new List<(string Target, string New, bool HadOld, bool Moved)>();
        try
        {
            using var zip = ZipFile.OpenRead(item.BundlePath!);
            var entries = new List<(ZipArchiveEntry Entry, string Target)>();
            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0))
            {
                var file = spec.Files.FirstOrDefault(f => string.Equals(Normalize(f.Path), Normalize(entry.FullName), StringComparison.OrdinalIgnoreCase));
                if (file is null)
                {
                    Log($"{item.Title}: ignoring {entry.FullName}, which {spec.DisplayName} doesn't install.");
                    continue;
                }
                var target = Path.GetFullPath(Path.Combine(root, Normalize(file.Path)));
                if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    return "The update has files outside the Black Ops III folder. Nothing was changed.";
                if (file.RequiresFile is { } needed && !File.Exists(Path.Combine(root, Normalize(needed))))
                    continue;
                if (file.KeepExisting && File.Exists(target))
                    continue;
                entries.Add((entry, target));
            }

            if (!entries.Any(e => string.Equals(e.Target, exe, StringComparison.OrdinalIgnoreCase)))
                return $"The update doesn't contain {spec.ExePath}. Nothing was changed.";

            foreach (var (entry, target) in entries)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var staged = target + ".new";
                entry.ExtractToFile(staged, overwrite: true);
                placed.Add((target, staged, File.Exists(target), false));
            }

            for (var i = 0; i < placed.Count; i++)
            {
                var (target, staged, hadOld, _) = placed[i];
                if (hadOld)
                {
                    if (File.Exists(target + ".old"))
                        File.Delete(target + ".old");
                    File.Move(target, target + ".old");
                }
                File.Move(staged, target);
                placed[i] = placed[i] with { Moved = true };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var rolledBack = RollBack(placed);
            Log($"Install of {item.Title} failed: {ex.GetType().Name}: {ex.Message}{(rolledBack ? "" : " (some files couldn't be put back)")}");
            if (!rolledBack)
                return $"Some files couldn't be put back. Reinstall {spec.DisplayName} from its release page.";
            return ex is UnauthorizedAccessException
                ? $"Blackbird can't write to {spec.DisplayName}'s folder. Nothing was changed."
                : $"A file was in use or couldn't be replaced. Nothing was changed.";
        }

        // The component's own daily cache still says the old version is installed.
        TryDelete(Path.Combine(ComponentPaths.Root, "updates", spec.Id + ".json"));

        if (VerifyAsync(item, exe, root, game).GetAwaiter().GetResult() is { } problem)
        {
            var rolledBack = RollBack(placed);
            Log($"{item.Title} didn't verify after install: {problem}{(rolledBack ? "; the old files were put back" : "; some files couldn't be put back")}");
            return rolledBack
                ? $"The new {spec.DisplayName} didn't run ({problem}). The old version was put back."
                : $"The new {spec.DisplayName} didn't run, and some files couldn't be put back. Reinstall it from its release page.";
        }

        foreach (var (target, _, hadOld, _) in placed.Where(p => p.HadOld))
            TryDelete(target + ".old");
        return null;
    }

    /// <summary>The new exe has to start and, if it says which version it is, say the one that was installed. Null, or what is wrong.</summary>
    private async Task<string?> VerifyAsync(ComponentUpdate item, string exe, string root, string game)
    {
        var result = await _options.Run(exe, item.Spec.VerifyArgs, WithFeed(BbLinker.InstallEnvironment(game, root), item.Spec));
        if (!result.Started)
            return "it doesn't start";
        var said = ReadField(result.Output, item.Spec.VerifyField);
        if (said is not null && Updater.CompareVersions(said, item.Latest) != 0)
            return $"it reports {said}, not {item.Latest}";
        if (said is null)
            Log($"{item.Title}: ran after install but reported no version ({item.Spec.VerifyField}).");
        return null;
    }

    private static string? ReadField(string output, string field)
    {
        var start = output.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
            return null;
        try
        {
            using var document = JsonDocument.Parse(output[start..], new JsonDocumentOptions { AllowTrailingCommas = true });
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text.Split('+')[0] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool RollBack(List<(string Target, string New, bool HadOld, bool Moved)> placed)
    {
        var ok = true;
        foreach (var (target, staged, hadOld, moved) in Enumerable.Reverse(placed))
        {
            try
            {
                if (moved)
                    File.Delete(target);
                if (hadOld && !File.Exists(target) && File.Exists(target + ".old"))
                    File.Move(target + ".old", target);
                TryDelete(staged);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>Removes the <c>.old</c> and <c>.new</c> files an earlier install left beside the components' files.</summary>
    private static void CleanLeftovers(string root)
    {
        foreach (var spec in ComponentSpecs.All)
            foreach (var file in spec.Files)
            {
                var target = Path.Combine(root, Normalize(file.Path));
                TryDelete(target + ".old");
                TryDelete(target + ".new");
            }
    }

    // ── Buttons ─────────────────────────────────────────────────────────────

    /// <summary>Later: the chip goes away until the next check (a waiting install stops waiting). Done after an install clears it.</summary>
    public void Later()
    {
        if (Phase == ComponentPhase.Installed)
        {
            Items = Items.Where(i => !i.IsInstalled).ToList();
            _snoozed = false;
            SetPhase(Items.Count == 0 ? ComponentPhase.Idle : ComponentPhase.Ready);
            return;
        }

        _snoozed = true;
        _blocker = "";
        if (Phase == ComponentPhase.Waiting)
            SetPhase(ComponentPhase.Ready);
        else
            Changed();
    }

    /// <summary>The flyout closed: a finished install is acknowledged, anything else stays where it is.</summary>
    public void Dismiss()
    {
        if (Phase == ComponentPhase.Installed)
            Later();
        else
            Changed();
    }

    // ── Plumbing ────────────────────────────────────────────────────────────

    private string Root() => _options.ToolsPath() is { Length: > 0 } tools ? tools : _options.GamePath();

    private void SetPhase(ComponentPhase phase)
    {
        Phase = phase;
        Changed();
    }

    private static readonly string[] Bound =
    [
        nameof(Items), nameof(Phase), nameof(StatusText), nameof(IsShown), nameof(IsPending), nameof(IsFailed), nameof(ShowsArrow),
        nameof(IsDownloading), nameof(IsInstalling), nameof(IsWorking), nameof(ChipText), nameof(Names), nameof(Title), nameof(Detail),
        nameof(HasDetail), nameof(Progress), nameof(ShowsProgress), nameof(IsProgressIndeterminate), nameof(ShowsNotes), nameof(CanInstall),
        nameof(ShowsInstall), nameof(ShowsLater), nameof(InstallText), nameof(LaterText), nameof(InstallTip),
    ];

    private void Changed(params string[] names)
    {
        foreach (var name in names.Length > 0 ? names : Bound)
            OnPropertyChanged(name);
        InstallCommand.NotifyCanExecuteChanged();
        CheckNowCommand.NotifyCanExecuteChanged();
    }

    private static string Join(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next start.
        }
    }

    private void Log(string message)
    {
        if (_options.LogFile is not { } file)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Components: {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best-effort.
        }
    }

    private sealed class UpdateCheckException(string message) : Exception(message);

    // ── Processes ───────────────────────────────────────────────────────────

    /// <summary>Runs a component's CLI with a timeout. A missing or unrunnable exe is "not started".</summary>
    public static async Task<CliResult> RunCli(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);
            foreach (var (name, value) in environment)
                startInfo.Environment[name] = value;
            using var process = Process.Start(startInfo);
            if (process is null)
                return new CliResult(false, -1, "");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(CliTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return new CliResult(true, -1, "");
            }
            return new CliResult(true, process.ExitCode, await stdout + await stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new CliResult(false, -1, "");
        }
    }

    /// <summary>
    /// Why the components can't be replaced now: Dragon is linking, or the Stock Library is open (Apex's <c>stocklib serve</c>,
    /// or Radiant, which has the plugin loaded). Only processes running from this install count.
    /// </summary>
    public static string? RunningProcessReason(string root, IReadOnlyList<ComponentSpec> specs)
    {
        foreach (var spec in specs)
        {
            foreach (var name in spec.ProcessNames)
                if (RunningFrom(name, root))
                    return spec.Id == "dragon"
                        ? "Dragon is linking right now. It installs when the link finishes."
                        : "Apex or Radiant has the Stock Library open. Close them to update it, or wait here.";
            if (spec.PluginProcess is { } radiant && spec.Plugin is { } plugin && File.Exists(Path.Combine(root, Normalize(plugin.RequiresFile!))) && RunningFrom(radiant, root))
                return "Radiant has the Stock Library plugin open. Close it to update, or wait here.";
        }
        return null;
    }

    private static bool RunningFrom(string name, string root)
    {
        Process[] found;
        try
        {
            found = Process.GetProcessesByName(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }

        try
        {
            foreach (var process in found)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    return true; // can't tell whose it is: treat it as in use
                }
            }
            return false;
        }
        finally
        {
            foreach (var process in found)
                process.Dispose();
        }
    }
}
