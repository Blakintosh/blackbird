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
/// Decides whether <c>gdtdb /update</c> has anything to do. It takes 1.5-2 s even when nothing changed, most of it
/// regenerating asset definitions, so Blackbird fingerprints the same inputs gdtdb reads (the GDTs in the folders
/// listed by <c>converter_gdt_dirs_0.txt</c>, deffiles, gdtdb itself) together with the database files, in about
/// 0.1 s. When the fingerprint matches the one taken around the last clean update, the update is skipped.
/// </summary>
/// <remarks>
/// The inputs are fingerprinted before the update runs and the database after it, so a GDT saved during the update
/// still differs next time. Anything else that writes the database (Asset Property Editor, GdtDBTray, another
/// launcher) changes its size or time and forces an update. State lives under %APPDATA%\Blackbird, never in BO3.
/// </remarks>
public sealed class GdtDbGate
{
    private readonly string _gamePath;
    private readonly string _toolsPath;
    private readonly string _stateFile;
    private string? _pendingInputs;

    public GdtDbGate(string gamePath, string toolsPath, string? stateFile = null)
    {
        _gamePath = gamePath;
        _toolsPath = toolsPath;
        _stateFile = stateFile ?? Path.Combine(AppPaths.DataFolder, "gdtdb-state.txt");
    }

    /// <summary>True when the last clean update saw exactly the inputs and database that are there now.</summary>
    public bool IsUpToDate(CancellationToken ct)
    {
        _pendingInputs = InputsFingerprint(ct);
        var saved = ReadState();
        return saved is not null && _pendingInputs is not null
            && saved.Value.Inputs == _pendingInputs
            && saved.Value.Database == DatabaseFingerprint();
    }

    /// <summary>Records a clean update against the inputs seen just before it ran.</summary>
    public void RecordCleanUpdate()
    {
        if (_pendingInputs is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            File.WriteAllText(_stateFile, $"{_pendingInputs}\n{DatabaseFingerprint()}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A failed or cancelled update leaves the database in an unknown state: the next build runs it.</summary>
    public void Forget()
    {
        try { File.Delete(_stateFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private (string Inputs, string Database)? ReadState()
    {
        try
        {
            var lines = File.ReadAllLines(_stateFile);
            return lines.Length >= 2 ? (lines[0], lines[1]) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private string DatabaseFingerprint() => DatabaseFingerprint(_toolsPath);

    internal static string DatabaseFingerprint(string toolsPath)
    {
        var db = new FileInfo(Path.Combine(toolsPath, "gdtdb", "gdt.db"));
        if (!db.Exists)
            return "-";
        // Any reader (the linker included) creates an empty write-ahead log and -shm file just by opening the
        // database, so only a log that holds changes counts; checkpointing them moves the database's own time.
        var wal = new FileInfo(db.FullName + "-wal");
        var pending = wal.Exists && wal.Length > 0 ? $"{wal.Length}:{wal.LastWriteTimeUtc.Ticks}" : "empty";
        return $"{db.Length}:{db.LastWriteTimeUtc.Ticks};{pending}";
    }

    private string? InputsFingerprint(CancellationToken ct)
    {
        var dirList = Path.Combine(_toolsPath, "bin", "converter_gdt_dirs_0.txt");
        if (!File.Exists(dirList))
            return null;

        var entries = new ConcurrentBag<(string Path, long Length, long Ticks)>();
        void Add(string path)
        {
            var info = new FileInfo(path);
            if (info.Exists)
                entries.Add((path, info.Length, info.LastWriteTimeUtc.Ticks));
        }

        // gdtdb reads its paths from the environment, so a different TA_GAME_PATH is a different database input.
        var header = $"{Environment.GetEnvironmentVariable("TA_GAME_PATH")}|{Environment.GetEnvironmentVariable("TA_TOOLS_PATH")}|{File.ReadAllText(dirList)}";
        Add(Path.Combine(_toolsPath, "gdtdb", "gdtdb.exe"));
        Add(Path.Combine(_toolsPath, "bin", "AssetScript64r.dll"));

        var deffiles = Path.Combine(_gamePath, "deffiles");
        if (Directory.Exists(deffiles))
            foreach (var file in Directory.EnumerateFiles(deffiles, "*", AllFiles))
                Add(file);

        // Same walk as gdtdb: every subfolder of every listed root, hidden and system files included. The roots'
        // own subfolders are walked in parallel, which keeps usermaps and mods (large trees) to about 0.1 s.
        var subfolders = new List<string>();
        foreach (var dir in File.ReadAllLines(dirList).Where(line => line.Length > 0))
        {
            var root = Path.Combine(_gamePath, dir);
            if (!Directory.Exists(root))
                continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.gdt", AllFiles))
                Add(file);
            subfolders.AddRange(Directory.EnumerateDirectories(root, "*", AllFiles));
        }

        Parallel.ForEach(subfolders, new ParallelOptions { CancellationToken = ct }, folder =>
        {
            var gdts = new FileSystemEnumerable<(string, long, long)>(
                folder,
                (ref FileSystemEntry e) => (e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcTicks),
                Recursive)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) =>
                    !e.IsDirectory && e.FileName.EndsWith(".gdt", StringComparison.OrdinalIgnoreCase),
            };
            foreach (var gdt in gdts)
                entries.Add(gdt);
        });

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(header));
        Span<byte> number = stackalloc byte[8];
        foreach (var (path, length, ticks) in entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
            BitConverter.TryWriteBytes(number, length);
            hash.AppendData(number);
            BitConverter.TryWriteBytes(number, ticks);
            hash.AppendData(number);
        }
        return $"{entries.Count}:{Convert.ToHexString(hash.GetHashAndReset())}";
    }

    private static readonly EnumerationOptions AllFiles = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        MatchType = MatchType.Win32,
    };

    private static readonly EnumerationOptions Recursive = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
    };
}
