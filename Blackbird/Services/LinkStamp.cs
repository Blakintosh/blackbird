using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Blackbird.Services;

/// <summary>
/// Knows when a link would produce what is already on disk, so the build can skip it and show the last link's output.
/// The linker writes every input it read, with the time it saw, to <c>zone_source\&lt;lang&gt;\assetinfo\&lt;zone&gt;.deps</c>
/// (source files, GDT entries by hash, missing files as time 0). A stamp is that list re-read from disk, plus what the
/// list can't cover: the project folder (zone and zpkg files, scripts, sound aliases), the sound trees snd_convert
/// reads, the GDT database, the linker and its plugins in bin, this link's arguments and its outputs.
/// The main zone and each localized zone are stamped separately, so a script edit relinks only the main zone.
/// </summary>
/// <remarks>
/// A stamp is only written when every <c>.deps</c> time still matches what the linker saw and nothing in the project
/// folder changed while it ran, so an edit made mid-link always relinks next time. Checking compares the cheap parts
/// first and stops at the first difference. Stamps live under %APPDATA%\Blackbird\link-stamps, never in BO3.
/// </remarks>
public sealed class LinkStamp
{
    private const string Header = "blackbird link stamp 2";
    private const string OutputMarker = "---";

    private readonly LinkStampEnvironment _environment;
    private readonly string _toolsPath;
    private readonly string _projectFolder;
    private readonly string _zoneName;
    private readonly string? _language;
    private readonly string _key;
    private readonly string _stampFile;
    private string? _projectBefore;

    /// <param name="language">
    /// Null for the main zone (linked with <c>-skiploc</c>); otherwise the language of a localized link
    /// (<c>-spawnedchild -localized</c>), which reads the <c>loc</c> zone the main link generates.
    /// </param>
    public LinkStamp(LinkStampEnvironment environment, string projectFolder, string zoneName, string? language,
        string linkerCommandLine, string? stampFolder = null)
    {
        _environment = environment;
        _toolsPath = environment.ToolsPath;
        _projectFolder = projectFolder;
        _zoneName = zoneName;
        _language = language;
        _key = $"{linkerCommandLine}|{language ?? "main"}|{Environment.GetEnvironmentVariable("TA_GAME_PATH")}|{Environment.GetEnvironmentVariable("TA_TOOLS_PATH")}";
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_key.ToLowerInvariant())))[..24];
        _stampFile = Path.Combine(stampFolder ?? Path.Combine(AppPaths.DataFolder, "link-stamps"), name + ".stamp");
    }

    /// <summary>The last link's exit code and output when nothing it depends on has changed since; otherwise null.</summary>
    public (int ExitCode, string Output)? TryGetUpToDate(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_stampFile))
                return null;
            var text = File.ReadAllText(_stampFile);
            var split = text.IndexOf("\n" + OutputMarker + "\n", StringComparison.Ordinal);
            if (split < 0)
                return null;
            var saved = text[..split].Split('\n');
            if (saved.Length != 8 || saved[0] != Header || saved[1] != _key || !int.TryParse(saved[7], out var exitCode))
                return null;

            // Cheapest and likeliest to change first.
            if (saved[2] != ProjectFingerprint()
                || saved[3] != GdtDbGate.DatabaseFingerprint(_toolsPath)
                || saved[4] != OutputsFingerprint()
                || saved[5] != LocZoneFingerprint()
                || saved[6] != _environment.Value + "|" + SourcesFingerprint(ct, requireLinkerTimes: false))
                return null;
            return (exitCode, text[(split + OutputMarker.Length + 2)..]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Snapshots the project folder before the link, to catch edits made while it runs, and drops the old stamp so a
    /// cancelled or crashed link can't leave it behind.
    /// </summary>
    public void BeginLink()
    {
        _projectBefore = ProjectFingerprint();
        _ = _environment.Value;
        Forget();
    }

    /// <summary>Records a finished link; skipped when an input moved during the link or the linker left no deps.</summary>
    public void RecordLink(int exitCode, string output, CancellationToken ct)
    {
        try
        {
            var project = ProjectFingerprint();
            if (_projectBefore is null || project != _projectBefore)
                return;
            var sources = SourcesFingerprint(ct, requireLinkerTimes: true);
            if (sources is null)
                return;
            var stamp = string.Join('\n',
                Header, _key, project, GdtDbGate.DatabaseFingerprint(_toolsPath), OutputsFingerprint(), LocZoneFingerprint(),
                _environment.Value + "|" + sources, exitCode.ToString());
            Directory.CreateDirectory(Path.GetDirectoryName(_stampFile)!);
            File.WriteAllText(_stampFile, $"{stamp}\n{OutputMarker}\n{output}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
    }

    public void Forget()
    {
        try { File.Delete(_stampFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>This link's outputs: another link, Clean or Prepare for publish changes them.</summary>
    private string OutputsFingerprint()
    {
        var zoneDir = Path.Combine(_projectFolder, "zone");
        return Directory.Exists(zoneDir) ? TreeHash.Of(zoneDir, IsOwnOutput) : "-";
    }

    /// <summary>
    /// A localized link reads the loc zone the main link writes. The main link rewrites it every time, so its content
    /// is what counts, not its time.
    /// </summary>
    private string LocZoneFingerprint()
    {
        if (_language is null)
            return "-";
        var locZone = LocZonePath;
        return File.Exists(locZone) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(locZone))) : "none";
    }

    private string LocZonePath => Path.Combine(_projectFolder, "zone_source", "loc", _zoneName + ".zone");

    /// <summary>
    /// Every source the deps file lists, as it is now. Null when there are no deps, or (when recording) a source
    /// changed after the linker read it.
    /// </summary>
    private string? SourcesFingerprint(CancellationToken ct, bool requireLinkerTimes)
    {
        var deps = Path.Combine(_projectFolder, "zone_source", _language ?? "all", "assetinfo", _zoneName + ".deps");
        var depsInfo = new FileInfo(deps);
        if (!depsInfo.Exists)
            return null;

        var linkerTimes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var locZone = _language is null ? null : LocZonePath;
        foreach (var (path, seconds) in ReadDeps(deps))
            if (locZone is null || !string.Equals(path, locZone, StringComparison.OrdinalIgnoreCase))
                linkerTimes.TryAdd(path, seconds);

        // The linker records whole seconds, and 0 for a file that wasn't there. Some of its loaders (scripts,
        // techsets, images, anims) write local time rather than UTC, so either reading counts as unchanged.
        var localOffset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalSeconds;
        var moved = 0;
        var sum = new ConcurrentBag<(ulong Sum, ulong Xor)>();
        Parallel.ForEach(linkerTimes, new ParallelOptions { CancellationToken = ct },
            () => (Sum: 0UL, Xor: 0UL),
            (entry, _, acc) =>
            {
                var info = new FileInfo(entry.Key);
                var exists = info.Exists;
                var ticks = exists ? info.LastWriteTimeUtc.Ticks : 0;
                if (requireLinkerTimes)
                {
                    var utcSeconds = exists ? new DateTimeOffset(ticks, TimeSpan.Zero).ToUnixTimeSeconds() : 0;
                    var same = exists
                        ? entry.Value == utcSeconds || entry.Value == utcSeconds + localOffset
                        : entry.Value == 0;
                    if (!same)
                        Interlocked.Exchange(ref moved, 1);
                }
                var h = TreeHash.Entry(entry.Key, exists ? info.Length : -1, ticks);
                return (acc.Sum + h, acc.Xor ^ h);
            },
            acc => sum.Add(acc));

        if (moved != 0)
            return null;
        ulong total = 0, xor = 0;
        foreach (var (s, x) in sum)
        {
            total += s;
            xor ^= x;
        }
        return $"{depsInfo.Length}:{depsInfo.LastWriteTimeUtc.Ticks}:{linkerTimes.Count}:{total:x16}:{xor:x16}";
    }

    /// <summary>
    /// The project folder minus what a link writes into it: zone outputs, assetinfo/assetlist/scriptgdb, the generated
    /// localized zone, snd_convert's bank cache, staging files, and Lua bytecode compiled next to its source.
    /// </summary>
    private string ProjectFingerprint() => TreeHash.Of(_projectFolder, IsProjectInput);

    private bool IsProjectInput(string path)
    {
        var relative = Path.GetRelativePath(_projectFolder, path);
        var parts = relative.Split(Path.DirectorySeparatorChar);
        if (parts[0].Equals("zone", StringComparison.OrdinalIgnoreCase))
            return false;
        // snd_convert rewrites its bank cache (sound\zone\CachedBanks) on every link.
        if (parts.Length > 2 && parts[0].Equals("sound", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("zone", StringComparison.OrdinalIgnoreCase)
            && parts[2].Equals("CachedBanks", StringComparison.OrdinalIgnoreCase))
            return false;
        if (parts[0].Equals("zone_source", StringComparison.OrdinalIgnoreCase) && parts.Length > 2
            && (parts[1].Equals("loc", StringComparison.OrdinalIgnoreCase)
                || parts.Any(p => p.Equals("assetinfo", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("assetlist", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("scriptgdb", StringComparison.OrdinalIgnoreCase))))
            return false;
        if (path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.EndsWith(".luac", StringComparison.OrdinalIgnoreCase) && File.Exists(path[..^1]))
            return false;
        // A localized link never reads scripts or UI Lua (its deps hold only techsets, images, materials, models
        // and physics presets), so editing them leaves the localized zone as it is.
        if (_language is not null
            && (parts[0].Equals("scripts", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("ui", StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    /// <summary>
    /// The main zone writes <c>zone\&lt;zone&gt;.*</c> and <c>zone\snd\all</c>; a localized link writes
    /// <c>zone\&lt;lang&gt;_&lt;zone&gt;.*</c> and the other <c>zone\snd</c> folders.
    /// </summary>
    private bool IsOwnOutput(string file)
    {
        var name = Path.GetFileName(file);
        var parts = Path.GetRelativePath(Path.Combine(_projectFolder, "zone"), file).Split(Path.DirectorySeparatorChar);
        if (parts.Length > 2 && parts[0].Equals("snd", StringComparison.OrdinalIgnoreCase))
            return name.Contains(_zoneName, StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("all", StringComparison.OrdinalIgnoreCase) == (_language is null);
        if (parts.Length != 1)
            return false;
        return _language is null
            ? name.StartsWith(_zoneName + ".", StringComparison.OrdinalIgnoreCase)
            : name.Contains("_" + _zoneName + ".", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The <c>file,&lt;path&gt;,&lt;seconds&gt;[,hashes]</c> lines of a deps file.</summary>
    private static IEnumerable<(string Path, long Seconds)> ReadDeps(string depsFile)
    {
        foreach (var line in File.ReadLines(depsFile))
        {
            if (!line.StartsWith("\tfile,", StringComparison.Ordinal))
                continue;
            var rest = line.AsSpan(6);
            // The path runs up to the first ",<digits>" field (paths with a comma before digits don't occur in BO3 trees).
            for (var i = 0; i < rest.Length; i++)
            {
                if (rest[i] != ',')
                    continue;
                var j = i + 1;
                while (j < rest.Length && char.IsAsciiDigit(rest[j]))
                    j++;
                if (j > i + 1 && (j == rest.Length || rest[j] == ','))
                {
                    yield return (rest[..i].ToString(), long.Parse(rest[(i + 1)..j]));
                    break;
                }
            }
        }
    }
}

/// <summary>
/// What every link in one build reads besides its project and deps: the linker and its plugins (proxy DLLs) in bin,
/// and the sound trees snd_convert reads (aliases in share\raw\sound, the wavs they name in sound_assets). Taken
/// once, before the build's first link, so an edit made during the build still differs next time.
/// </summary>
public sealed class LinkStampEnvironment
{
    private readonly Lazy<string> _value;

    public LinkStampEnvironment(string gamePath, string toolsPath)
    {
        ToolsPath = toolsPath;
        _value = new Lazy<string>(() =>
        {
            var bin = TreeHash.Of(Path.Combine(toolsPath, "bin"), IsToolFile, recurse: false);
            var soundAssets = TreeHash.Of(Path.Combine(gamePath, "sound_assets"));
            var sharedSound = TreeHash.Of(Path.Combine(gamePath, "share", "raw", "sound"));
            return $"{bin}|{soundAssets}|{sharedSound}";
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string ToolsPath { get; }

    public string Value => _value.Value;

    private static bool IsToolFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cfg", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// An order-independent fingerprint of a folder's files (path, size, time): each file hashes to 64 bits and the
/// hashes are summed and xored, so nothing is sorted and an unfiltered walk builds no strings.
/// </summary>
internal static class TreeHash
{
    private static readonly EnumerationOptions TopOnly = new() { AttributesToSkip = 0, IgnoreInaccessible = true };
    private static readonly EnumerationOptions Recursive = new() { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = true };

    public static string Of(string root, Func<string, bool>? include = null, bool recurse = true)
    {
        if (!Directory.Exists(root))
            return "-";

        ulong sum = 0, xor = 0;
        long count = 0;
        var gate = new object();
        void Walk(string folder, EnumerationOptions options)
        {
            ulong s = 0, x = 0;
            long n = 0;
            var files = new FileSystemEnumerable<ulong>(folder, (ref FileSystemEntry e) =>
                {
                    ulong h = Seed;
                    h = Mix(h, e.Directory);
                    h = Mix(h, "\\");
                    h = Mix(h, e.FileName);
                    return Finish(h, e.Length, e.LastWriteTimeUtc.UtcTicks);
                }, options)
            {
                ShouldIncludePredicate = include is null
                    ? (ref FileSystemEntry e) => !e.IsDirectory
                    : (ref FileSystemEntry e) => !e.IsDirectory && include(e.ToFullPath()),
            };
            foreach (var h in files)
            {
                s += h;
                x ^= h;
                n++;
            }
            lock (gate)
            {
                sum += s;
                xor ^= x;
                count += n;
            }
        }

        Walk(root, TopOnly);
        if (recurse)
            Parallel.ForEach(Directory.EnumerateDirectories(root, "*", TopOnly), folder => Walk(folder, Recursive));
        return $"{count}:{sum:x16}:{xor:x16}";
    }

    public static ulong Entry(string path, long length, long ticks) => Finish(Mix(Seed, path), length, ticks);

    private const ulong Seed = 14695981039346656037UL;

    /// <summary>FNV-1a over the path, lower-cased, so the hash matches however the path was spelled.</summary>
    private static ulong Mix(ulong h, ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            h ^= char.ToLowerInvariant(c);
            h *= 1099511628211UL;
        }
        return h;
    }

    private static ulong Finish(ulong h, long length, long ticks)
    {
        h = SplitMix(h ^ (ulong)length);
        return SplitMix(h ^ (ulong)ticks);
    }

    private static ulong SplitMix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
