using Blackbird.Models;
using Blackbird.Services;

namespace Blackbird.Shots;

/// <summary>Steam Workshop without Steam: always signed in, uploads report progress and can be held mid-way.</summary>
internal sealed class FakeSteam : ISteamWorkshopService
{
    /// <summary>While set, an upload stops at 42 % until <see cref="ReleaseUpload"/>.</summary>
    public TaskCompletionSource? UploadGate { get; set; }

    public WorkshopItemData? RemoteMetadata { get; set; }

    public bool IsInitialized => true;
    public string InitializationDetail => "";
    public Task<bool> InitializeAsync() => Task.FromResult(true);
    public void Shutdown() { }

    /// <summary>What Steam says about the Workshop legal agreement after an upload.</summary>
    public bool NeedsLegalAgreement { get; set; }

    /// <summary>The zone folder as it was when the last upload started: what Steam would have shipped.</summary>
    public string[] LastUploadedFiles { get; private set; } = [];

    /// <summary>The changelog the last upload sent.</summary>
    public string? LastChangeNote { get; private set; }

    public Task<WorkshopCreateResult> CreateItemAsync(string pendingKey, CancellationToken ct = default) =>
        Task.FromResult(new WorkshopCreateResult(3301234567UL, NeedsLegalAgreement));

    public async Task<WorkshopUpdateResult> UpdateItemAsync(ulong fileId, string title, string description, string thumbnail,
        string contentFolder, string[] tags, WorkshopItemVisibility visibility, string changeNote,
        string[] additionalPreviewImages, IReadOnlyList<uint>? existingImagePreviewIndices = null,
        IProgress<WorkshopUploadProgress>? progress = null, CancellationToken ct = default)
    {
        LastUploadedFiles = [.. Directory.EnumerateFileSystemEntries(contentFolder, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(contentFolder, path))];
        LastChangeNote = changeNote;
        progress?.Report(new WorkshopUploadProgress(WorkshopUploadStage.Preparing, null));
        await Task.Delay(20, ct);
        progress?.Report(new WorkshopUploadProgress(WorkshopUploadStage.Uploading, 0.42));
        if (UploadGate is { } gate)
            await gate.Task.WaitAsync(ct);
        progress?.Report(new WorkshopUploadProgress(WorkshopUploadStage.Finishing, null));
        return new WorkshopUpdateResult(Steamworks.EResult.k_EResultOK, NeedsLegalAgreement);
    }

    public void ReleaseUpload() => UploadGate?.TrySetResult();

    /// <summary>While set, reading the Workshop item waits here (Pull from Workshop in flight).</summary>
    public TaskCompletionSource? FetchGate { get; set; }

    // Answers at once unless gated: the dialog must take a reply that lands before it is shown.
    public async Task<WorkshopRemoteItemData?> GetCurrentItemAsync(ulong fileId, CancellationToken ct = default)
    {
        if (FetchGate is { } gate)
            await gate.Task.WaitAsync(ct);

        return RemoteMetadata is null ? null : new WorkshopRemoteItemData
        {
            Metadata = RemoteMetadata.Clone(),
        };
    }
}

/// <summary>The Mod Tools without the Mod Tools: prints a realistic linker log, and can hold a build open.</summary>
internal sealed class FakeBuild : IBuildService
{
    /// <summary>While set, a build prints its first lines and waits here.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public int Runs { get; private set; }

    /// <summary>What the build prints after the gate; the log with errors when null.</summary>
    public string[]? Tail { get; set; }

    public async Task<bool> RunBuildAsync(List<BuildCommand> commands, bool ignoreErrors, IProgress<string> progress,
        CancellationToken ct)
    {
        Runs++;
        foreach (var line in FakeInstall.BuildLogHead)
            progress.Report(line + "\n");
        await Task.Delay(30, ct);

        if (Gate is { } gate)
            await gate.Task.WaitAsync(ct);

        foreach (var line in Tail ?? FakeInstall.BuildLogTail)
            progress.Report(line + "\n");
        await Task.Yield(); // the reports are queued on the UI thread; finish behind them, as a real process exit does
        return true;
    }
}

/// <summary>
/// The real file system service over the fake install, with two changes: detection is
/// scripted (it never finds the real Black Ops III on this machine), and setup checks and
/// Workshop writes record the thread they ran on.
/// </summary>
internal sealed class HarnessFileSystem(FileSystemService inner) : IFileSystemService
{
    public static HarnessFileSystem Instance { get; private set; } = null!;

    public HarnessFileSystem Register()
    {
        Instance = this;
        return this;
    }

    /// <summary>What detection finds; blocks while <see cref="DetectGate"/> is set.</summary>
    public SetupDetectionResult Detected { get; set; } = new();
    public ManualResetEventSlim? DetectGate { get; set; }

    /// <summary>Managed thread ids of every workshop.json / workshop.profiles.json write.</summary>
    public List<int> WorkshopWriteThreads { get; } = [];

    public string GamePath => inner.GamePath;
    public string ToolsPath => inner.ToolsPath;
    public List<MapItem> ScanMaps() => inner.ScanMaps();
    public List<ModItem> ScanMods() => inner.ScanMods();
    public List<string> ScanMapTemplates() => inner.ScanMapTemplates();

    public SetupDetectionResult DetectPaths()
    {
        DetectGate?.Wait(TimeSpan.FromSeconds(10));
        return new SetupDetectionResult { GamePath = Detected.GamePath, ToolsPath = Detected.ToolsPath, Source = Detected.Source };
    }

    /// <summary>Managed thread ids of every setup validation.</summary>
    public List<int> ValidateThreads { get; } = [];

    public SetupValidationResult ValidateSetup(string? gamePath = null, string? toolsPath = null)
    {
        lock (ValidateThreads)
            ValidateThreads.Add(Environment.CurrentManagedThreadId);
        return inner.ValidateSetup(gamePath, toolsPath);
    }

    public string DetectGameLanguage() => inner.DetectGameLanguage();
    public Task SetPathsAsync(string gamePath, string toolsPath) => inner.SetPathsAsync(gamePath, toolsPath);
    public WorkshopItemData? ReadWorkshopJson(string zoneFolderPath) => inner.ReadWorkshopJson(zoneFolderPath);

    public void WriteWorkshopJson(string zoneFolderPath, WorkshopItemData data)
    {
        lock (WorkshopWriteThreads)
            WorkshopWriteThreads.Add(Environment.CurrentManagedThreadId);
        inner.WriteWorkshopJson(zoneFolderPath, data);
    }

    public WorkshopProfilesData ReadWorkshopProfiles(string zoneFolderPath, WorkshopItemData? fallbackData = null) =>
        inner.ReadWorkshopProfiles(zoneFolderPath, fallbackData);

    public void WriteWorkshopProfiles(string zoneFolderPath, WorkshopProfilesData data)
    {
        lock (WorkshopWriteThreads)
            WorkshopWriteThreads.Add(Environment.CurrentManagedThreadId);
        inner.WriteWorkshopProfiles(zoneFolderPath, data);
    }
}
