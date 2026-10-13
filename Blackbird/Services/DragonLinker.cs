using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Blackbird.Services;

/// <summary>
/// Dragon, the Rust linker. When <c>bin\dragon.exe</c> is in the Mod Tools folder (or <c>BLACKBIRD_DRAGON</c> names one)
/// and Tools > Link with Dragon is on, normal builds link with it
/// and fall back to the stock linker for anything it can't link yet, except for projects that depend on stock carry.
/// </summary>
public static class DragonLinker
{
    /// <summary>An input Dragon can't convert yet; the stock linker should link instead.</summary>
    public const int ExitUnsupported = 0x0BB10001;

    /// <summary>Dragon failed or crashed; the stock linker should link instead.</summary>
    public const int ExitInternal = 0x0BB10002;

    /// <summary>
    /// A carried stock asset (or one of its dependencies) failed, or the link carried stock assets and also hit an
    /// unsupported case. The stock linker can't link stock-only assets, so this is never a fallback.
    /// </summary>
    public const int ExitStockCarry = 0x0BB10003;

    /// <summary>The capability <c>dragon -capabilities</c> lists when it can carry stock assets.</summary>
    public const string StockCarryCapability = "stock-carry";

    /// <summary>What the launcher says next to every refusal to link a carry project with the stock linker.</summary>
    public const string CarryHint = "Create a local copy in Apex, or turn on Link with Dragon.";

    /// <summary>The marker Dragon writes beside a zone's source when a link carried stock assets.</summary>
    public static string CarryMarkerPath(string projectFolder, string zoneName) =>
        Path.Combine(projectFolder, "zone_source", zoneName + ".stockcarry.json");

    /// <summary>
    /// The marker Dragon writes beside a zone's source when a link carried stock sound aliases (Ultrasound reads it
    /// for their audio). The stock linker can't link those aliases either, so it counts exactly like a carry marker.
    /// </summary>
    public static string SoundMarkerPath(string projectFolder, string zoneName) =>
        Path.Combine(projectFolder, "zone_source", zoneName + ".stocksound.json");

    /// <summary>
    /// What a map needs Dragon for, read from its marker. <see cref="Assets"/> and <see cref="SoundAliases"/> are counts,
    /// or -1 when the marker doesn't say (an older Dragon, or a marker that can't be read: still a dependency).
    /// </summary>
    public sealed record StockDependency(string Marker, int Assets, int SoundAliases)
    {
        /// <summary>Only stock sounds: no carried asset, at least one carried alias (or an older Dragon's sound request).</summary>
        public bool SoundOnly => Assets == 0 && SoundAliases != 0;

        public string Capability => SoundOnly ? StockSoundCapability : StockCarryCapability;

        /// <summary>"stock assets", or "3 stock sounds" for a sound-only map.</summary>
        public string What => !SoundOnly ? "stock assets"
            : SoundAliases > 0 ? $"{SoundAliases} stock sound{(SoundAliases == 1 ? "" : "s")}"
            : "stock sounds";

        /// <summary>How to build without Dragon: local copies in Apex (its Stock sounds view for sounds), or the switch.</summary>
        public string Hint => SoundOnly ? SoundHint : CarryHint;
    }

    /// <summary>The hint next to a refusal for a map that uses only stock sounds.</summary>
    public const string SoundHint = "Apex's Stock sounds view can make local copies of them, or turn on Link with Dragon.";

    /// <summary>
    /// What the map's zone needs Dragon for, or null when nothing. <c>.stockcarry.json</c> is the dependency marker: it
    /// counts when its <c>carried</c> list isn't empty or its <c>sound_aliases</c> is above 0 (Dragon 1.0.0 writes one
    /// with an empty list for a map that uses only stock sounds). An older Dragon marked a sound-only map with
    /// <c>.stocksound.json</c> alone, which still counts. Reads one small file; fine on the UI thread.
    /// </summary>
    public static StockDependency? StockDependencyOf(string projectFolder, string zoneName)
    {
        var carry = CarryMarkerPath(projectFolder, zoneName);
        if (File.Exists(carry))
        {
            var (assets, aliases) = ReadCarryMarker(carry);
            if (assets != 0 || aliases != 0)
                return new StockDependency(carry, assets, aliases);
        }
        var sound = SoundMarkerPath(projectFolder, zoneName);
        return File.Exists(sound) ? new StockDependency(sound, 0, ReadSoundRequest(sound)) : null;
    }

    private static (int Assets, int Aliases) ReadCarryMarker(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (-1, 0);
            var assets = root.TryGetProperty("carried", out var carried) && carried.ValueKind == JsonValueKind.Array
                ? carried.GetArrayLength() : -1;
            var aliases = root.TryGetProperty("sound_aliases", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var count)
                ? count : 0;
            return (assets, aliases);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (-1, 0); // a marker being rewritten, or not JSON: still a dependency
        }
    }

    private static int ReadSoundRequest(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("aliases", out var aliases))
            {
                if (aliases.ValueKind == JsonValueKind.Array && aliases.GetArrayLength() > 0)
                    return aliases.GetArrayLength();
                if (aliases.ValueKind == JsonValueKind.Number && aliases.TryGetInt32(out var count) && count > 0)
                    return count;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return -1;
    }

    /// <summary>The capability <c>dragon -capabilities</c> lists when it can carry stock sound aliases.</summary>
    public const string StockSoundCapability = "stock-sound";

    /// <summary>
    /// The install a Dragon run is for, as the stock launcher passes it to the Mod Tools: without these, Dragon falls
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

    /// <summary>The installed Dragon's place, ignoring <see cref="OverrideVariable"/>, whether or not it exists: <c>bin\dragon.exe</c>.</summary>
    public static string InstalledPath(string? toolsPath = null) =>
        string.IsNullOrEmpty(toolsPath)
            ? Path.Combine(AppContext.BaseDirectory, "dragon.exe")
            : Path.Combine(toolsPath, "bin", "dragon.exe");

    /// <summary>Names a Dragon to use instead of the installed one (a developer's own build).</summary>
    public const string OverrideVariable = "BLACKBIRD_DRAGON";

    /// <summary>The Dragon <see cref="OverrideVariable"/> names, or null when it isn't set.</summary>
    public static string? OverridePath =>
        Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } path ? path : null;

    /// <summary>The configured path (<c>BLACKBIRD_DRAGON</c>) or the default one, whether or not it exists.</summary>
    public static string ConfiguredPath(string? toolsPath = null)
    {
        return OverridePath ?? InstalledPath(toolsPath);
    }

    /// <summary>The Dragon to link with, or null when there is none.</summary>
    public static string? Locate(string? toolsPath = null) =>
        ConfiguredPath(toolsPath) is var path && File.Exists(path) ? path : null;

    /// <summary>
    /// Why Tools > Link with Dragon can't be turned on, or null when Dragon is there. Not an error: the stock
    /// linker links everything without it.
    /// </summary>
    public static string? MissingReason(string? toolsPath = null)
    {
        var path = ConfiguredPath(toolsPath);
        if (File.Exists(path))
            return null;
        return OverridePath is not null
            ? $"{OverrideVariable} names {path}, which doesn't exist. The stock linker links every build."
            : $"Dragon isn't installed (Blackbird looks for {path}). The stock linker links every build.";
    }

    /// <summary>Whether <paramref name="exe"/> is the one <see cref="OverrideVariable"/> names, not the installed one.</summary>
    public static bool IsOverride(string exe) =>
        OverridePath is { } path && string.Equals(Path.GetFullPath(path), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The coloured line a build log shows before Dragon's output: <c>Dragon 1.0.0 (a1b2c3d4e, built 12 Oct 2026 21:04)</c>,
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
        var details = new List<string>();
        if (devBuild)
            details.Add($"dev build: {exe}");
        if (info?.Commit is { Length: > 0 } commit)
            details.Add(commit.Length > 12 ? commit[..9] : commit);
        if (info?.Built is { Length: > 0 } built)
            details.Add($"built {built}");
        else if (written is { } stamp)
            details.Add($"built {stamp:d MMM yyyy HH:mm}");
        var version = info?.Version is { Length: > 0 } v ? v : "(version unknown)";
        return $"^4Dragon {version} ({string.Join(", ", details)})";
    }

    /// <summary>The note on the stock linker's line when Dragon is there but the switch is off.</summary>
    public const string SwitchedOffNote = "Dragon is installed, but Tools > Link with Dragon is off.";

    /// <summary>The note on the stock linker's line for Prepare for publish, which always links with it.</summary>
    public const string PublishNote = "Prepare for publish always links with it.";

    /// <summary>
    /// The line before a carry map's link when the user chose to link it with the stock linker anyway: a warning, so the
    /// build counts it and the log says what is missing.
    /// </summary>
    public static string StockAnywayHeader(string linkerExe, StockDependency? dependency = null) =>
        $"^3[launcher] Linking with the stock linker ({Path.GetFileName(linkerExe)}) anyway: the {dependency?.What ?? "stock assets"} Dragon carried for this map are left out.";

    /// <summary>The question before linking a map that needs Dragon with the stock linker.</summary>
    public static string StockAnywayMessage(StockDependency? dependency) =>
        $"This map uses {dependency?.What ?? "stock assets"} carried by Dragon. Install Dragon or switch it on; linking with the stock linker will leave {(dependency?.SoundOnly == true ? "those sounds" : "those assets")} out."
        + (dependency?.SoundOnly == true ? " Apex's Stock sounds view can make local copies of them." : "");

    /// <summary>The plain line a build log shows before the stock linker's first link of a build.</summary>
    public static string StockHeader(string linkerExe, string? note) =>
        $"[launcher] Linking with the stock linker ({Path.GetFileName(linkerExe)}).{(note is null ? "" : " " + note)}";

    /// <summary>What <c>dragon -capabilities</c> said about the exe (the install-dependent parts are read elsewhere).</summary>
    public sealed record Info(string? Version, IReadOnlyList<string> Capabilities, string? Commit = null, string? Built = null, int? CarryContract = null)
    {
        public bool HasStockCarry => Has(StockCarryCapability);

        public bool Has(string capability) => System.Linq.Enumerable.Contains(Capabilities, capability);
    }

    /// <summary>The stock-carry contract this launcher and the Stock Library understand (Dragon's <c>carry_contract</c>).</summary>
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
            // "commit" and "build_date" (or "built") are read when a linker reports them (Dragon 1.0.0); 0.1.0 reports
            // neither, and the header falls back to the exe's date.
            int? contract = root.TryGetProperty("carry_contract", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : null;
            return new Info(Text("version"), capabilities, Text("commit"), Text("build_date") ?? Text("built"), contract);
        }
        catch (JsonException)
        {
            return null; // the usage text of a linker that predates -capabilities
        }
    }

    /// <summary>What happens after a Dragon step ends.</summary>
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
    /// stock linker can't link it, so the build fails with Dragon's own message instead.
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
    /// <paramref name="useDragon"/> is Tools > Link with Dragon; the other two describe the Dragon that would run.
    /// </summary>
    public static string? CarryBlockedReason(bool projectDependsOnCarry, bool useDragon, bool linkerFound, bool hasStockCarry,
        StockDependency? dependency = null)
    {
        if (!projectDependsOnCarry)
            return null;
        var what = dependency?.What ?? "stock assets";
        var hint = dependency?.Hint ?? CarryHint;
        var head = $"This project uses {what} that only Dragon can link";
        if (!useDragon)
            return $"{head}, and Link with Dragon is off, so the stock linker would drop them. {hint}";
        if (!linkerFound)
            return $"{head}, and Dragon isn't installed, so the stock linker would drop them. {hint}";
        if (!hasStockCarry)
            return $"{head}, and this Dragon can't carry {(dependency?.SoundOnly == true ? "stock sounds" : "stock assets")}, so the stock linker would drop them. {hint}";
        return null;
    }

    /// <summary>The line after a failed link of a carry project (Dragon's own lines above it name the assets).</summary>
    public static string CarryFailureMessage(int exitCode, StockDependency? dependency = null)
    {
        var hint = dependency?.Hint ?? CarryHint;
        if (dependency?.SoundOnly == true)
            return exitCode == ExitStockCarry
                ? $"Dragon couldn't carry every stock sound this project uses (exit 0x{exitCode:X8}). The stock linker can't link them, so it wasn't used. {hint}"
                : $"Dragon couldn't link this project (exit 0x{exitCode:X8}), and it uses {dependency.What} only Dragon can link, so the stock linker wasn't used. {hint}";
        return exitCode == ExitStockCarry
            ? $"Dragon couldn't carry every stock asset this project uses (exit 0x{exitCode:X8}). The stock linker can't link them, so it wasn't used. {hint}"
            : $"Dragon couldn't link this project (exit 0x{exitCode:X8}), and it uses stock assets only Dragon can link, so the stock linker wasn't used. {hint}";
    }

    private static readonly ConcurrentDictionary<(string Path, DateTime Stamp), Task<string>> Usage = new();

    /// <summary>
    /// Whether this Dragon takes <paramref name="option"/> (<c>-progress</c>): its usage text, printed
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
