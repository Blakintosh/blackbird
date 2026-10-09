using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Steamworks;
using Blackbird.Models;

namespace Blackbird.Services;

public interface ISteamWorkshopService
{
    bool IsInitialized { get; }

    /// <summary>Why Steam isn't available, in Steam's terms (for a tooltip); empty before the first attempt.</summary>
    string InitializationDetail { get; }

    /// <summary>
    /// Starts the Steam API on a background thread. Concurrent callers share one
    /// attempt; after a failure a later call retries.
    /// </summary>
    Task<bool> InitializeAsync();
    void Shutdown();

    /// <summary>
    /// Creates a Workshop item; FileId is 0 when Steam refused. Cancelling (or Steam not answering in
    /// time, which throws <see cref="TimeoutException"/>) stops waiting but keeps listening: Steam may
    /// still create the item, and the next call with the same <paramref name="pendingKey"/> returns
    /// that item instead of creating a second one. Other keys never see it.
    /// </summary>
    /// <param name="pendingKey">The project and Workshop version the item is for.</param>
    Task<WorkshopCreateResult> CreateItemAsync(string pendingKey, CancellationToken ct = default);

    /// <param name="existingImagePreviewIndices">
    /// The indices of the item's image previews (videos and Sketchfab models are never touched), if
    /// already known; otherwise Steam is queried for them.
    /// </param>
    /// <param name="progress">Reported on the calling thread whenever the stage or whole percent changes.</param>
    /// <exception cref="WorkshopPreviewQueryException">Steam couldn't say which gallery previews the item has.</exception>
    Task<WorkshopUpdateResult> UpdateItemAsync(ulong fileId, string title, string description,
        string thumbnail, string contentFolder, string[] tags,
        WorkshopItemVisibility visibility, string changeNote, string[] additionalPreviewImages,
        IReadOnlyList<uint>? existingImagePreviewIndices = null, IProgress<WorkshopUploadProgress>? progress = null,
        CancellationToken ct = default);

    Task<WorkshopRemoteItemData?> GetCurrentItemAsync(ulong fileId, CancellationToken ct = default);
}

public enum WorkshopUploadStage { Preparing, Uploading, Finishing }

/// <param name="NeedsLegalAgreement">The user hasn't accepted the Workshop legal agreement, so the item stays hidden until they do.</param>
public readonly record struct WorkshopCreateResult(ulong FileId, bool NeedsLegalAgreement, EResult Result = EResult.k_EResultOK);

/// <param name="RejectedField">What Steam refused before anything was sent ("title", "thumbnail"…); null when it took them all.</param>
/// <param name="TimedOut">Steam never answered. The upload may still finish on Steam's side.</param>
public readonly record struct WorkshopUpdateResult(
    EResult Result,
    bool NeedsLegalAgreement = false,
    string? RejectedField = null,
    bool TimedOut = false)
{
    public bool Succeeded => Result == EResult.k_EResultOK && RejectedField is null && !TimedOut;
}

/// <summary>
/// Steam didn't answer which gallery previews an item has. Uploading anyway could duplicate or
/// overwrite them, so the upload stops instead.
/// </summary>
public sealed class WorkshopPreviewQueryException()
    : Exception("Steam didn't say which gallery images the Workshop item already has, so nothing was uploaded. Try again.");

/// <param name="Fraction">0–1 while bytes are moving and Steam knows the total; otherwise null.</param>
public readonly record struct WorkshopUploadProgress(WorkshopUploadStage Stage, double? Fraction);

/// <summary>One plain line for a Steam result code.</summary>
public static class WorkshopResultText
{
    public static string Describe(EResult result) => result switch
    {
        EResult.k_EResultAccessDenied => "Steam denied access. Check you're signed in to the account that owns this Workshop item.",
        EResult.k_EResultLimitExceeded => "A file is over Steam's size limit, or your Steam Cloud storage is full.",
        EResult.k_EResultFileNotFound => "Steam couldn't find the Workshop item or one of its files.",
        EResult.k_EResultTimeout => "Steam timed out. Try again.",
        EResult.k_EResultInsufficientPrivilege => "Your Steam account isn't allowed to publish to the Workshop right now.",
        EResult.k_EResultBusy => "Steam is still busy with an earlier upload of this item. Wait a minute, then try again.",
        EResult.k_EResultServiceUnavailable => "Steam Workshop is unavailable right now. Try again later.",
        EResult.k_EResultNotLoggedOn => "Steam isn't signed in. Sign in to Steam, then try again.",
        EResult.k_EResultIOFailure => "Steam lost the connection. Check your connection, then try again.",
        _ => $"Steam returned an error ({result.ToString().Replace("k_EResult", "", StringComparison.Ordinal)}).",
    };
}
