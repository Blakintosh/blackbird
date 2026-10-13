using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Blackbird.Services;

/// <summary>
/// bb-linker, the Rust linker (Dragon in the Components list). When <c>bb-linker\bb-linker.exe</c> sits beside the
/// launcher (or <c>BLACKBIRD_BB_LINKER</c> names one) and Tools > Link with bb-linker is on, normal builds link with it
/// and fall back to the stock linker for anything it can't link yet, except for projects that depend on stock carry.
/// </summary>
public static class BbLinker
{
    /// <summary>An input bb-linker can't convert yet; the stock linker should link instead.</summary>
    public const int ExitUnsupported = 0x0BB10001;

    /// <summary>bb-linker failed or crashed; the stock linker should link instead.</summary>
    public const int ExitInternal = 0x0BB10002;

    /// <summary>
    /// A carried stock asset (or one of its dependencies) failed, or the link carried stock assets and also hit an
    /// unsupported case. The stock linker can't link stock-only assets, so this is never a fallback.
    /// </summary>
    public const int ExitStockCarry = 0x0BB10003;

    /// <summary>The capability <c>bb-linker -capabilities</c> lists when it can carry stock assets.</summary>
    public const string StockCarryCapability = "stock-carry";

    /// <summary>What the launcher says next to every refusal to link a carry project with the stock linker.</summary>
    public const string CarryHint = "Create a local copy in Apex, or turn on Link with bb-linker.";

    /// <summary>The marker bb-linker writes beside a zone's source when a link carried stock assets.</summary>
    public static string CarryMarkerPath(string projectFolder, string zoneName) =>
        Path.Combine(projectFolder, "zone_source", zoneName + ".stockcarry.json");

    /// <summary>
    /// The marker bb-linker writes beside a zone's source when a link carried stock sound aliases (Ultrasound reads it
    /// for their audio). The stock linker can't link those aliases either, so it counts exactly like a carry marker.
    /// </summary>
    public static string SoundMarkerPath(string projectFolder, string zoneName) =>
        Path.Combine(projectFolder, "zone_source", zoneName + ".stocksound.json");

    /// <summary>The project's stock marker (carry first, then sound), or null when it depends on neither.</summary>
    public static string? StockMarker(string projectFolder, string zoneName) =>
        CarryMarkerPath(projectFolder, zoneName) is var carry && File.Exists(carry) ? carry
        : SoundMarkerPath(projectFolder, zoneName) is var sound && File.Exists(sound) ? sound
        : null;

    /// <summary>The capability a bb-linker needs to link a project with this marker (stock-sound for a sound-only one).</summary>
    public static string CapabilityFor(string marker) =>
        marker.EndsWith(".stocksound.json", StringComparison.OrdinalIgnoreCase) ? StockSoundCapability : StockCarryCapability;

    /// <summary>The capability <c>bb-linker -capabilities</c> lists when it can carry stock sound aliases.</summary>
    public const string StockSoundCapability = "stock-sound";

    /// <summary>
    /// The install a bb-linker run is for, as the stock launcher passes it to the Mod Tools: without these, Dragon falls
    /// back to its own default install, which isn't the one Blackbird is building.
    /// </summary>
    public static Dictionary<string, string> InstallEnvironment(string? gamePath, string? toolsPath)
    {
        static string? Folder(string? path) =>
            string.IsNullOrWhiteSpace(path) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        var environment = new Dictionary<string, string>();
        var game = Folder(gamePath);
        var tools = Folder(toolsPath) ?? game;
        if (game is not null)
            environment["TA_GAME_PATH"] = game;
        if (tools is not null)
            environment["TA_TOOLS_PATH"] = tools;
        return environment;
    }

    /// <summary>The installed bb-linker's place, ignoring <see cref="OverrideVariable"/>, whether or not it exists.</summary>
    public static string InstalledPath(string? toolsPath = null)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "bb-linker", "bb-linker.exe");
        if (File.Exists(beside) || string.IsNullOrEmpty(toolsPath))
            return beside;
        return Path.Combine(toolsPath, "bin", "bb-linker", "bb-linker.exe");
    }

    /// <summary>Names a bb-linker to use instead of the installed one (a developer's own build).</summary>
    public const string OverrideVariable = "BLACKBIRD_BB_LINKER";

    /// <summary>The bb-linker <see cref="OverrideVariable"/> names, or null when it isn't set.</summary>
    public static string? OverridePath =>
        Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } path ? path : null;

    /// <summary>The configured path (<c>BLACKBIRD_BB_LINKER</c>) or the default one, whether or not it exists.</summary>
    public static string ConfiguredPath(string? toolsPath = null)
    {
        return OverridePath ?? InstalledPath(toolsPath);
    }

    /// <summary>The bb-linker to link with, or null when there is none.</summary>
    public static string? Locate(string? toolsPath = null) =>
        ConfiguredPath(toolsPath) is var path && File.Exists(path) ? path : null;

    /// <summary>
    /// Why Tools > Link with bb-linker can't be turned on, or null when bb-linker is there. Not an error: the stock
    /// linker links everything without it.
    /// </summary>
    public static string? MissingReason(string? toolsPath = null)
    {
        var path = ConfiguredPath(toolsPath);
        if (File.Exists(path))
            return null;
        return OverridePath is not null
            ? $"{OverrideVariable} names {path}, which doesn't exist. The stock linker links every build."
            : $"bb-linker isn't installed (Blackbird looks for {path}). The stock linker links every build.";
    }

    /// <summary>Whether <paramref name="exe"/> is the one <see cref="OverrideVariable"/> names, not the installed one.</summary>
    public static bool IsOverride(string exe) =>
        OverridePath is { } path && string.Equals(Path.GetFullPath(path), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The coloured line a build log shows before bb-linker's output: <c>Dragon 0.1.0 (bb-linker, built 12 Oct 2026 21:04)</c>,
    /// with the commit when the linker reports one and the path when it is a developer's build named by
    /// <see cref="OverrideVariable"/>. Runs <c>-capabilities</c> once per exe build.
    /// </summary>
    public static async Task<string> HeaderAsync(string exe, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = await ProbeAsync(exe, environment).ConfigureAwait(false);
        DateTime? written = null;
        try
        {
            if (File.Exists(exe))
                written = File.GetLastWriteTime(exe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Header(exe, info, IsOverride(exe), written);
    }

    public static string Header(string exe, Info? info, bool devBuild, DateTime? written)
    {
        var details = new List<string> { "bb-linker" };
        if (devBuild)
            details.Add($"dev build: {exe}");
        if (info?.Commit is { Length: > 0 } commit)
            details.Add(commit);
        if (info?.Built is { Length: > 0 } built)
            details.Add($"built {built}");
        else if (written is { } stamp)
            details.Add($"built {stamp:d MMM yyyy HH:mm}");
        var version = info?.Version is { Length: > 0 } v ? v : "(version unknown)";
        return $"^4Dragon {version} ({string.Join(", ", details)})";
    }

    /// <summary>The note on the stock linker's line when bb-linker is there but the switch is off.</summary>
    public const string SwitchedOffNote = "bb-linker is installed, but Tools > Link with bb-linker is off.";

    /// <summary>The note on the stock linker's line for Prepare for publish, which always links with it.</summary>
    public const string PublishNote = "Prepare for publish always links with it.";

    /// <summary>The plain line a build log shows before the stock linker's first link of a build.</summary>
    public static string StockHeader(string linkerExe, string? note) =>
        $"[launcher] Linking with the stock linker ({Path.GetFileName(linkerExe)}).{(note is null ? "" : " " + note)}";

    /// <summary>What <c>bb-linker -capabilities</c> said about the exe (the install-dependent parts are read elsewhere).</summary>
    public sealed record Info(string? Version, IReadOnlyList<string> Capabilities, string? Commit = null, string? Built = null, int? CarryContract = null)
    {
        public bool HasStockCarry => Has(StockCarryCapability);

        public bool Has(string capability) => System.Linq.Enumerable.Contains(Capabilities, capability);
    }

    /// <summary>The stock-carry contract this launcher and the Stock Library understand (bb-linker's <c>carry_contract</c>).</summary>
    public const int CarryContract = 1;

    private static readonly ConcurrentDictionary<(string Path, long Size, DateTime Stamp), Info?> Probed = new();

    /// <summary>
    /// The linker's capabilities, or null when it has none to report (a build that predates <c>-capabilities</c>).
    /// Read once per exe build, keyed by size and modified time, like <see cref="SupportsAsync"/>. <paramref name="environment"/>
    /// names the install (<see cref="InstallEnvironment"/>); the parts read here don't depend on it, so the cache doesn't either.
    /// </summary>
    public static Info? Probe(string exe, IReadOnlyDictionary<string, string>? environment = null)
    {
        try
        {
            var file = new FileInfo(exe);
            if (!file.Exists)
                return null;
            return Probed.GetOrAdd((file.FullName, file.Length, file.LastWriteTimeUtc), key => ReadCapabilities(key.Path, environment));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Task<Info?> ProbeAsync(string exe, IReadOnlyDictionary<string, string>? environment = null) =>
        Task.Run(() => Probe(exe, environment));

    private static Info? ReadCapabilities(string exe, IReadOnlyDictionary<string, string>? environment)
    {
        var text = Run(exe, environment, "-capabilities");
        var start = text.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
            return null;
        try
        {
            using var document = JsonDocument.Parse(text[start..], new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var capabilities = new List<string>();
            if (root.TryGetProperty("capabilities", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
                        capabilities.Add(name);
            string? Text(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            // "commit" and "built" are read when a linker reports them; 0.1.0 reports neither.
            int? contract = root.TryGetProperty("carry_contract", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : null;
            return new Info(Text("version"), capabilities, Text("commit"), Text("built"), contract);
        }
        catch (JsonException)
        {
            return null; // the usage text of a linker that predates -capabilities
        }
    }

    /// <summary>What happens after a bb-linker step ends.</summary>
    public enum Next
    {
        /// <summary>Its result stands (success, or errors the log shows).</summary>
        Accept,

        /// <summary>The stock linker links this instead.</summary>
        UseStockLinker,

        /// <summary>The build fails here: the stock linker would drop the carried assets.</summary>
        FailStockCarry,
    }

    /// <summary>
    /// The one fallback rule. <c>0x0BB10003</c> is never a fallback. The two "use the stock linker" codes are one only
    /// for a project that doesn't depend on stock carry; for one that does (it has a <c>.stockcarry.json</c> marker) the
    /// stock linker can't link it, so the build fails with bb-linker's own message instead.
    /// </summary>
    public static Next AfterStep(int exitCode, bool projectDependsOnCarry)
    {
        if (exitCode == ExitStockCarry)
            return Next.FailStockCarry;
        if (!MeansUseStock(exitCode))
            return Next.Accept;
        return projectDependsOnCarry ? Next.FailStockCarry : Next.UseStockLinker;
    }

    /// <summary>
    /// Why a project that depends on stock carry can't be linked with the linker about to run, or null when it can.
    /// <paramref name="useBbLinker"/> is Tools > Link with bb-linker; the other two describe the bb-linker that would run.
    /// </summary>
    public static string? CarryBlockedReason(bool projectDependsOnCarry, bool useBbLinker, bool linkerFound, bool hasStockCarry)
    {
        if (!projectDependsOnCarry)
            return null;
        const string head = "This project uses stock assets that only bb-linker can link";
        if (!useBbLinker)
            return $"{head}, and Link with bb-linker is off, so the stock linker would drop them. {CarryHint}";
        if (!linkerFound)
            return $"{head}, and bb-linker isn't installed, so the stock linker would drop them. {CarryHint}";
        if (!hasStockCarry)
            return $"{head}, and this bb-linker can't carry stock assets, so the stock linker would drop them. {CarryHint}";
        return null;
    }

    /// <summary>The line after a failed link of a carry project (bb-linker's own lines above it name the assets).</summary>
    public static string CarryFailureMessage(int exitCode) =>
        exitCode == ExitStockCarry
            ? $"bb-linker couldn't carry every stock asset this project uses (exit 0x{exitCode:X8}). The stock linker can't link them, so it wasn't used. {CarryHint}"
            : $"bb-linker couldn't link this project (exit 0x{exitCode:X8}), and it uses stock assets only bb-linker can link, so the stock linker wasn't used. {CarryHint}";

    private static readonly ConcurrentDictionary<(string Path, DateTime Stamp), Task<string>> Usage = new();

    /// <summary>
    /// Whether this bb-linker takes <paramref name="option"/> (<c>-progress</c>): its usage text, printed
    /// when run bare, lists the options it knows. Read once per exe build, so a linker from before an option still
    /// links, just without it.
    /// </summary>
    public static async Task<bool> SupportsAsync(string exe, string option, IReadOnlyDictionary<string, string>? environment = null)
    {
        var usage = await Usage.GetOrAdd((exe, File.GetLastWriteTimeUtc(exe)), key => Task.Run(() => Run(key.Path, environment)))
            .ConfigureAwait(false);
        return usage.Contains(option, StringComparison.Ordinal);
    }

    private static string Run(string exe, IReadOnlyDictionary<string, string>? environment, params string[] args)
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
            if (environment is not null)
                foreach (var (name, value) in environment)
                    startInfo.Environment[name] = value;
            using var process = Process.Start(startInfo);
            if (process is null)
                return "";
            var output = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return "";
            }
            return output.Result + stdout.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "";
        }
    }

    public static bool MeansUseStock(int exitCode) => exitCode is ExitUnsupported or ExitInternal;
}
