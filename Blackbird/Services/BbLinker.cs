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

    /// <summary>The configured path (<c>BLACKBIRD_BB_LINKER</c>) or the default one, whether or not it exists.</summary>
    public static string ConfiguredPath(string? toolsPath = null)
    {
        if (Environment.GetEnvironmentVariable("BLACKBIRD_BB_LINKER") is { Length: > 0 } path)
            return path;
        var beside = Path.Combine(AppContext.BaseDirectory, "bb-linker", "bb-linker.exe");
        if (File.Exists(beside) || string.IsNullOrEmpty(toolsPath))
            return beside;
        return Path.Combine(toolsPath, "bin", "bb-linker", "bb-linker.exe");
    }

    /// <summary>The bb-linker to link with, or null when there is none.</summary>
    public static string? Locate(string? toolsPath = null) =>
        ConfiguredPath(toolsPath) is var path && File.Exists(path) ? path : null;

    /// <summary>What <c>bb-linker -capabilities</c> said about the exe (the install-dependent parts are read elsewhere).</summary>
    public sealed record Info(string? Version, IReadOnlyList<string> Capabilities)
    {
        public bool HasStockCarry => System.Linq.Enumerable.Contains(Capabilities, StockCarryCapability);
    }

    private static readonly ConcurrentDictionary<(string Path, long Size, DateTime Stamp), Info?> Probed = new();

    /// <summary>
    /// The linker's capabilities, or null when it has none to report (a build that predates <c>-capabilities</c>).
    /// Read once per exe build, keyed by size and modified time, like <see cref="SupportsAsync"/>.
    /// </summary>
    public static Info? Probe(string exe)
    {
        try
        {
            var file = new FileInfo(exe);
            if (!file.Exists)
                return null;
            return Probed.GetOrAdd((file.FullName, file.Length, file.LastWriteTimeUtc), key => ReadCapabilities(key.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Task<Info?> ProbeAsync(string exe) => Task.Run(() => Probe(exe));

    private static Info? ReadCapabilities(string exe)
    {
        var text = Run(exe, "-capabilities");
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
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new Info(version, capabilities);
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
    public static async Task<bool> SupportsAsync(string exe, string option)
    {
        var usage = await Usage.GetOrAdd((exe, File.GetLastWriteTimeUtc(exe)), key => Task.Run(() => Run(key.Path)))
            .ConfigureAwait(false);
        return usage.Contains(option, StringComparison.Ordinal);
    }

    private static string Run(string exe, params string[] args)
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
