using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Steamworks;
using Blackbird.Models;

namespace Blackbird.Services;

public class SteamWorkshopService : ISteamWorkshopService
{
    private const uint WorkshopAppId = 311210;
    private const string ModToolsAppId = "455130";

    private static bool _dllResolverRegistered;
    private static string? _steamNativePath;
    private static readonly object ResolverLock = new();

    private readonly object _initLock = new();
    private Task<bool>? _initTask;
    private volatile bool _isInitialized;
    private volatile string _initializationDetail = "";

    public bool IsInitialized => _isInitialized;
    public string InitializationDetail => _initializationDetail;

    private bool Initialize()
    {
        if (_isInitialized)
            return true;

        lock (_initLock)
        {
            if (_isInitialized)
                return true;

            var runtimeDetail = "";
            try
            {
                runtimeDetail = PrepareSteamRuntime();
                var initialized = SteamAPI.Init();
                _initializationDetail = initialized
                    ? $"Steam API initialized. {runtimeDetail}"
                    : $"SteamAPI.Init() returned false. {runtimeDetail}";
                _isInitialized = initialized;
                return initialized;
            }
            catch (Exception ex)
            {
                _initializationDetail = string.IsNullOrWhiteSpace(runtimeDetail)
                    ? $"Steam initialization failed: {ex.Message}"
                    : $"Steam initialization failed: {ex.Message} {runtimeDetail}";
                _isInitialized = false;
                return false;
            }
        }
    }

    public Task<bool> InitializeAsync()
    {
        if (_isInitialized)
            return Task.FromResult(true);

        lock (_initLock)
        {
            // Concurrent callers share one attempt; a finished, failed attempt may be retried.
            if (_initTask is null || (_initTask.IsCompleted && !_isInitialized))
                _initTask = Task.Run(Initialize);
            return _initTask;
        }
    }

    public void Shutdown()
    {
        if (IsInitialized)
            SteamAPI.Shutdown();
    }

    private void RunCallbacks()
    {
        if (IsInitialized)
            SteamAPI.RunCallbacks();
    }

    // CreateItem calls nobody is waiting on any more (cancelled or timed out), by project and
    // Workshop version. Steam may still create the item, so the callback stays registered and the
    // next create for the same version returns its ID.
    private readonly Dictionary<string, (TaskCompletionSource<WorkshopCreateResult> Created, CallResult<CreateItemResult_t> Call)> _pendingCreates =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<WorkshopCreateResult> CreateItemAsync(string pendingKey, CancellationToken ct = default)
    {
        if (!IsInitialized)
            return new WorkshopCreateResult(0, false, EResult.k_EResultNotLoggedOn);

        TaskCompletionSource<WorkshopCreateResult> pending;
        lock (_pendingCreates)
        {
            if (_pendingCreates.TryGetValue(pendingKey, out var existing))
            {
                pending = existing.Created;
            }
            else
            {
                var created = new TaskCompletionSource<WorkshopCreateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var call = SteamUGC.CreateItem(new AppId_t(WorkshopAppId), EWorkshopFileType.k_EWorkshopFileTypeCommunity);
                var callResult = CallResult<CreateItemResult_t>.Create((result, ioFailure) =>
                    created.TrySetResult(ioFailure || result.m_eResult != EResult.k_EResultOK
                        ? new WorkshopCreateResult(0, false, ioFailure ? EResult.k_EResultIOFailure : result.m_eResult)
                        : new WorkshopCreateResult(
                            result.m_nPublishedFileId.m_PublishedFileId,
                            result.m_bUserNeedsToAcceptWorkshopLegalAgreement)));
                callResult.Set(call);
                _pendingCreates[pendingKey] = (created, callResult);
                pending = created;
            }
        }

        var timeoutAt = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (!pending.Task.IsCompleted && !ct.IsCancellationRequested && DateTime.UtcNow < timeoutAt)
        {
            RunCallbacks();
            await Task.WhenAny(pending.Task, Task.Delay(50, ct));
        }

        if (!pending.Task.IsCompleted)
        {
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(
                "Steam didn't confirm the new Workshop item in time. It may still have been created; publishing this version again uses it instead of creating another.");
        }

        lock (_pendingCreates)
            _pendingCreates.Remove(pendingKey);
        return await pending.Task;
    }

    public async Task<WorkshopUpdateResult> UpdateItemAsync(ulong fileId, string title, string description,
        string thumbnail, string contentFolder, string[] tags,
        WorkshopItemVisibility visibility, string changeNote, string[] additionalPreviewImages,
        IReadOnlyList<uint>? existingImagePreviewIndices = null, IProgress<WorkshopUploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsInitialized)
            return new WorkshopUpdateResult(EResult.k_EResultNotLoggedOn);

        var imageIndices = existingImagePreviewIndices
            ?? await GetImagePreviewIndicesAsync(fileId, ct)
            ?? throw new WorkshopPreviewQueryException();
        ct.ThrowIfCancellationRequested();
        var previewImages = NormalizePreviewImages(additionalPreviewImages);
        var updateHandle = SteamUGC.StartItemUpdate(new AppId_t(WorkshopAppId), new PublishedFileId_t(fileId));

        // Each setter only checks its argument (length, file there); false means it was refused.
        // Tags are always set, so unticking every tag clears them on Steam too.
        var rejected =
            !SteamUGC.SetItemTitle(updateHandle, title) ? "title"
            : !SteamUGC.SetItemDescription(updateHandle, description) ? "description"
            : !string.IsNullOrEmpty(thumbnail) && !SteamUGC.SetItemPreview(updateHandle, thumbnail) ? "thumbnail"
            : !SteamUGC.SetItemContent(updateHandle, contentFolder) ? "zone folder"
            : !SteamUGC.SetItemTags(updateHandle, tags) ? "tags"
            : !SteamUGC.SetItemVisibility(updateHandle, ToSteamVisibility(visibility)) ? "visibility"
            : !ApplyAdditionalPreviewFiles(updateHandle, previewImages, imageIndices) ? "gallery images"
            : null;
        if (rejected is not null)
            return new WorkshopUpdateResult(EResult.k_EResultInvalidParam, RejectedField: rejected);

        var call = SteamUGC.SubmitItemUpdate(updateHandle, changeNote ?? "");
        WorkshopUploadProgress? lastReported = null;
        return await WaitForCallResultAsync<SubmitItemUpdateResult_t, WorkshopUpdateResult>(
            call,
            (result, ioFailure) => new WorkshopUpdateResult(
                ioFailure ? EResult.k_EResultIOFailure : result.m_eResult,
                !ioFailure && result.m_bUserNeedsToAcceptWorkshopLegalAgreement),
            new WorkshopUpdateResult(EResult.k_EResultTimeout, TimedOut: true),
            TimeSpan.FromMinutes(30),
            ct,
            progress is null ? null : () =>
            {
                if (ReadUpdateProgress(updateHandle) is { } current && current != lastReported)
                {
                    lastReported = current;
                    progress.Report(current);
                }
            });
    }

    /// <summary>The upload's stage, with the fraction rounded to whole percent so polling only reports real change.</summary>
    private static WorkshopUploadProgress? ReadUpdateProgress(UGCUpdateHandle_t updateHandle)
    {
        var status = SteamUGC.GetItemUpdateProgress(updateHandle, out var processed, out var total);
        return status switch
        {
            EItemUpdateStatus.k_EItemUpdateStatusPreparingConfig
                or EItemUpdateStatus.k_EItemUpdateStatusPreparingContent =>
                new WorkshopUploadProgress(WorkshopUploadStage.Preparing, null),
            EItemUpdateStatus.k_EItemUpdateStatusUploadingContent
                or EItemUpdateStatus.k_EItemUpdateStatusUploadingPreviewFile =>
                new WorkshopUploadProgress(
                    WorkshopUploadStage.Uploading,
                    total == 0 ? null : Math.Floor(Math.Min(processed, total) * 100d / total) / 100d),
            EItemUpdateStatus.k_EItemUpdateStatusCommittingChanges =>
                new WorkshopUploadProgress(WorkshopUploadStage.Finishing, null),
            _ => null,
        };
    }

    /// <summary>The indices of the item's image previews, or null when Steam didn't answer (never a guessed empty list).</summary>
    private async Task<IReadOnlyList<uint>?> GetImagePreviewIndicesAsync(ulong fileId, CancellationToken ct)
    {
        if (fileId == 0)
            return [];
        if (!IsInitialized)
            return null;

        var fileIds = new[] { new PublishedFileId_t(fileId) };
        var queryHandle = SteamUGC.CreateQueryUGCDetailsRequest(fileIds, (uint)fileIds.Length);
        SteamUGC.SetReturnAdditionalPreviews(queryHandle, true);

        var released = false;
        try
        {
            var call = SteamUGC.SendQueryUGCRequest(queryHandle);
            return await WaitForCallResultAsync<SteamUGCQueryCompleted_t, IReadOnlyList<uint>?>(
                call,
                (result, ioFailure) =>
                {
                    try
                    {
                        return ioFailure || result.m_eResult != EResult.k_EResultOK || result.m_unNumResultsReturned == 0
                            ? null
                            : [.. GetQueryAdditionalPreviews(queryHandle).Where(p => p.IsImage).Select(p => p.Index)];
                    }
                    finally
                    {
                        if (!released)
                        {
                            SteamUGC.ReleaseQueryUGCRequest(queryHandle);
                            released = true;
                        }
                    }
                },
                null,
                ct: ct);
        }
        finally
        {
            if (!released)
            {
                released = true;
                SteamUGC.ReleaseQueryUGCRequest(queryHandle);
            }
        }
    }

    private static string[] NormalizePreviewImages(string[] additionalPreviewImages) =>
        [
            .. additionalPreviewImages
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];

    /// <summary>
    /// Replaces the item's image previews with <paramref name="previewImages"/>, in order. Only image
    /// indices are updated or removed (highest first, so lower ones keep their place): the item's
    /// videos and Sketchfab models stay as they are.
    /// </summary>
    private static bool ApplyAdditionalPreviewFiles(
        UGCUpdateHandle_t updateHandle,
        string[] previewImages,
        IReadOnlyList<uint> imageIndices)
    {
        var indices = imageIndices.Order().ToArray();
        var replacements = Math.Min(indices.Length, previewImages.Length);
        var ok = true;

        for (var i = 0; i < replacements; i++)
            ok &= SteamUGC.UpdateItemPreviewFile(updateHandle, indices[i], previewImages[i]);

        for (var i = indices.Length - 1; i >= previewImages.Length; i--)
            ok &= SteamUGC.RemoveItemPreview(updateHandle, indices[i]);

        for (var i = indices.Length; i < previewImages.Length; i++)
            ok &= SteamUGC.AddItemPreviewFile(updateHandle, previewImages[i], EItemPreviewType.k_EItemPreviewType_Image);

        return ok;
    }

    public async Task<WorkshopRemoteItemData?> GetCurrentItemAsync(ulong fileId, CancellationToken ct = default)
    {
        if (!IsInitialized || fileId == 0)
            return null;

        var fileIds = new[] { new PublishedFileId_t(fileId) };
        var queryHandle = SteamUGC.CreateQueryUGCDetailsRequest(fileIds, (uint)fileIds.Length);
        SteamUGC.SetReturnLongDescription(queryHandle, true);
        SteamUGC.SetReturnAdditionalPreviews(queryHandle, true);

        var released = false;
        try
        {
            var call = SteamUGC.SendQueryUGCRequest(queryHandle);
            return await WaitForCallResultAsync<SteamUGCQueryCompleted_t, WorkshopRemoteItemData?>(
                call,
                (result, ioFailure) =>
                {
                    try
                    {
                        if (ioFailure || result.m_eResult != EResult.k_EResultOK || result.m_unNumResultsReturned == 0)
                            return null;

                        if (!SteamUGC.GetQueryUGCResult(queryHandle, 0, out var details)
                            || details.m_eResult != EResult.k_EResultOK)
                        {
                            return null;
                        }

                        var metadata = new WorkshopItemData
                        {
                            PublisherId = fileId.ToString(),
                            Title = details.m_rgchTitle,
                            Description = details.m_rgchDescription,
                            Visibility = FromSteamVisibility(details.m_eVisibility).ToString(),
                        };
                        metadata.SetTagList(GetQueryTags(queryHandle));

                        return new WorkshopRemoteItemData
                        {
                            Metadata = metadata,
                            ThumbnailUrl = GetQueryPreviewUrl(queryHandle),
                            AdditionalPreviews = GetQueryAdditionalPreviews(queryHandle),
                        };
                    }
                    finally
                    {
                        if (!released)
                        {
                            SteamUGC.ReleaseQueryUGCRequest(queryHandle);
                            released = true;
                        }
                    }
                },
                null,
                ct: ct);
        }
        finally
        {
            if (!released)
            {
                released = true;
                SteamUGC.ReleaseQueryUGCRequest(queryHandle);
            }
        }
    }

    private static string GetQueryPreviewUrl(UGCQueryHandle_t queryHandle)
    {
        return SteamUGC.GetQueryUGCPreviewURL(queryHandle, 0, out var url, 4096)
            ? url
            : "";
    }

    private static List<string> GetQueryTags(UGCQueryHandle_t queryHandle)
    {
        var tags = new List<string>();
        var tagCount = SteamUGC.GetQueryUGCNumTags(queryHandle, 0);
        for (var i = 0U; i < tagCount; i++)
        {
            if (SteamUGC.GetQueryUGCTag(queryHandle, 0, i, out var tag, 255)
                && !string.IsNullOrWhiteSpace(tag))
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    private static List<WorkshopRemotePreview> GetQueryAdditionalPreviews(UGCQueryHandle_t queryHandle)
    {
        var previews = new List<WorkshopRemotePreview>();
        var previewCount = SteamUGC.GetQueryUGCNumAdditionalPreviews(queryHandle, 0);
        for (var i = 0U; i < previewCount; i++)
        {
            // One Steam won't describe is kept with no type, so nothing treats it as an image.
            if (!SteamUGC.GetQueryUGCAdditionalPreview(
                    queryHandle,
                    0,
                    i,
                    out var urlOrVideoId,
                    4096,
                    out var originalFileName,
                    260,
                    out var previewType))
            {
                previews.Add(new WorkshopRemotePreview { Index = i });
                continue;
            }

            previews.Add(new WorkshopRemotePreview
            {
                Index = i,
                Url = urlOrVideoId,
                OriginalFileName = originalFileName,
                PreviewType = previewType.ToString().Replace("k_EItemPreviewType_", "", StringComparison.OrdinalIgnoreCase),
            });
        }

        return previews;
    }

    /// <summary>
    /// Pumps Steam callbacks until <paramref name="call"/> completes, times out (returns
    /// <paramref name="fallback"/>) or <paramref name="ct"/> is cancelled (throws
    /// <see cref="OperationCanceledException"/>). Cancelling only stops waiting: Steam has no
    /// API to abort a submitted call, so a CreateItem or SubmitItemUpdate upload may still
    /// complete on Steam's side. <paramref name="poll"/> runs after each callback pump.
    /// </summary>
    private async Task<TResult> WaitForCallResultAsync<TCallback, TResult>(
        SteamAPICall_t call,
        Func<TCallback, bool, TResult> resolve,
        TResult fallback,
        TimeSpan? timeout = null,
        CancellationToken ct = default,
        Action? poll = null)
        where TCallback : struct
    {
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callResult = CallResult<TCallback>.Create((result, ioFailure) =>
        {
            tcs.TrySetResult(resolve(result, ioFailure));
        });
        callResult.Set(call);

        var timeoutAt = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
        while (!tcs.Task.IsCompleted && !ct.IsCancellationRequested && DateTime.UtcNow < timeoutAt)
        {
            RunCallbacks();
            if (!tcs.Task.IsCompleted)
                poll?.Invoke();
            await Task.WhenAny(tcs.Task, Task.Delay(50, ct));
        }

        if (!tcs.Task.IsCompleted)
        {
            // Cancel before resolving the fallback so a late Steam callback can
            // never fire into stale state (e.g. releasing an already-released
            // UGC query handle that Steam may have re-used).
            callResult.Cancel();
            ct.ThrowIfCancellationRequested();
            tcs.TrySetResult(fallback);
        }

        var value = await tcs.Task;
        GC.KeepAlive(callResult);
        return value;
    }

    private static ERemoteStoragePublishedFileVisibility ToSteamVisibility(WorkshopItemVisibility visibility) =>
        visibility switch
        {
            WorkshopItemVisibility.Public => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic,
            WorkshopItemVisibility.FriendsOnly => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly,
            WorkshopItemVisibility.Unlisted => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityUnlisted,
            _ => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate,
        };

    private static WorkshopItemVisibility FromSteamVisibility(ERemoteStoragePublishedFileVisibility visibility) =>
        visibility switch
        {
            ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic => WorkshopItemVisibility.Public,
            ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly => WorkshopItemVisibility.FriendsOnly,
            ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityUnlisted => WorkshopItemVisibility.Unlisted,
            _ => WorkshopItemVisibility.Private,
        };

    private static string PrepareSteamRuntime()
    {
        // Blackbird's own steam_api64.dll. The copy that ships in BO3/bin predates the
        // entry points Steamworks.NET calls.
        var nativePath = Path.Combine(AppContext.BaseDirectory, "steam", "steam_api64.dll");
        if (!File.Exists(nativePath))
            return $"steam_api64.dll is missing from {Path.GetDirectoryName(nativePath)}. Reinstall Blackbird.";

        RegisterDllResolver(nativePath);

        // Steam must see Blackbird as the Mod Tools, never as the game.
        Environment.SetEnvironmentVariable("SteamAppId", ModToolsAppId);
        Environment.SetEnvironmentVariable("SteamGameId", ModToolsAppId);

        return $"Using {nativePath}.";
    }

    private static void RegisterDllResolver(string nativePath)
    {
        lock (ResolverLock)
        {
            _steamNativePath = nativePath;
            if (_dllResolverRegistered)
                return;

            NativeLibrary.SetDllImportResolver(typeof(SteamAPI).Assembly, ResolveSteamNativeLibrary);
            _dllResolverRegistered = true;
        }
    }

    private static IntPtr ResolveSteamNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (_steamNativePath is not null
            && libraryName.Contains("steam_api", StringComparison.OrdinalIgnoreCase))
        {
            return NativeLibrary.Load(_steamNativePath);
        }

        return IntPtr.Zero;
    }
}
