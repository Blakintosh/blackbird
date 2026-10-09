using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Blackbird.Models;
using Blackbird.Services;

namespace Blackbird.ViewModels;

public enum PublishDialogState { Editing, Uploading, Published }

public partial class PublishDialogViewModel : ObservableObject
{
    public const int WorkshopVersionNameMaxLength = 40;

    // One question for stopping an upload, from the dialog and from closing the app alike.
    public const string StopUploadTitle = "Stop the upload?";
    public const string StopUploadMessage =
        "Blackbird stops waiting for Steam. Steam can't cancel an upload it has started, so it may still finish, or leave the Workshop item half-updated.";
    public const string StopUploadButton = "Stop upload";

    [ObservableProperty] private string _projectName = "";
    [ObservableProperty] private string _projectTypeLabel = "";
    [ObservableProperty] private string _estimatedSizeLabel = "";
    // Mirrors the main window's build state while the dialog is open — publishing
    // is locked out while any build (including Prepare for publish) is running.
    [ObservableProperty] private bool _isBuildRunning;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _thumbnail = "";
    [ObservableProperty] private string _changelog = "";
    [ObservableProperty] private int _selectedVisibilityIndex;
    private PublishProfileOption? _selectedProfile;
    [ObservableProperty] private Bitmap? _thumbnailPreview;

    /// <summary>Why the thumbnail file couldn't be shown (not an image, unreadable); null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThumbnailPlaceholderText))]
    private string? _thumbnailPreviewError;

    public string ThumbnailPlaceholderText => ThumbnailPreviewError ?? "No thumbnail";
    [ObservableProperty] private WorkshopPreviewImageItem? _selectedPreviewImage;
    [ObservableProperty] private bool _isBannerExpanded = true;
    [ObservableProperty] private string _draftStatusText = "";
    /// <summary>The project's Workshop media folder when it exists; empty otherwise (opening it never creates it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkshopMediaFolder), nameof(MediaFolderToolTip))]
    private string _workshopMediaFolder = "";
    [ObservableProperty] private PublishDialogState _state;
    [ObservableProperty] private string _uploadStatusText = "";

    /// <summary>Upload progress, 0-100, while Steam reports a fraction; otherwise the bar runs indeterminate.</summary>
    [ObservableProperty] private double _uploadPercent;
    [ObservableProperty] private bool _isUploadProgressKnown;
    // Inline footer error; the user's draft stays as it was.
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _errorDetail;
    private string _publishedUrl = "";
    // What Steam last reported for an item's visibility, keyed by Workshop ID, so updating
    // an item that is already public doesn't ask again.
    private readonly Dictionary<string, WorkshopItemVisibility> _knownRemoteVisibility = new(StringComparer.Ordinal);
    private WorkshopProfilesData _profilesData = WorkshopProfilesData.FromWorkshopJson(null);
    private PublishProfileOption? _loadedProfile;
    private bool _isLoadingProfile;
    private bool _isLoadingForm;
    private string _workshopType = "";
    private string _folderName = "";
    private CancellationTokenSource? _thumbnailPreviewCts;
    private readonly DispatcherTimer _localAutosaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    // The thumbnail check and preview touch the disk, so typing a path waits for a pause.
    private readonly DispatcherTimer _thumbnailTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private const int ThumbnailPreviewDecodeWidth = 520;

    public ObservableCollection<TagItem> Tags { get; } = [];
    public ObservableCollection<WorkshopPreviewImageItem> PreviewImages { get; } = [];
    public ObservableCollection<PublishPreflightCheck> Checks { get; } = [];
    public ObservableCollection<PublishProfileOption> PublishProfiles { get; } = [];
    public ObservableCollection<VisibilityOption> VisibilityOptions { get; } =
    [
        new("Private", WorkshopItemVisibility.Private),
        new("Friends only", WorkshopItemVisibility.FriendsOnly),
        new("Unlisted", WorkshopItemVisibility.Unlisted),
        new("Public", WorkshopItemVisibility.Public),
    ];

    /// <summary>
    /// Invoked when the user clicks an action button on a preflight row.
    /// Set by the dialog host (MainWindow). Args: (action kind, payload).
    /// </summary>
    public Action<string, string>? OnCheckAction { get; set; }

    /// <summary>
    /// Invoked when profile selection or profile metadata changes. Args:
    /// full sidecar data and the active profile materialized as workshop.json.
    /// </summary>
    public Action<WorkshopProfilesData, WorkshopItemData>? OnProfilesChanged { get; set; }

    /// <summary>Runs the upload with the dialog open. Set by the host (MainWindow).</summary>
    public Func<Task>? PublishHandler { get; set; }

    /// <summary>Stops waiting on an upload in flight. Set by the host (MainWindow).</summary>
    public Action? CancelUploadHandler { get; set; }

    public WorkshopProfilesData ProfilesData => _profilesData;

    public PublishProfileOption? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value))
                return;

            OnSelectedProfileChanged(value);
        }
    }

    public int BlockerCount => Checks.Count(c => c.IsBlocker);
    public int WarningCount => Checks.Count(c => c.IsWarning);
    public int InfoCount => Checks.Count(c => c.IsInfo);
    public bool HasBlockers => BlockerCount > 0;
    public bool HasWarnings => WarningCount > 0;
    public bool HasInfo => InfoCount > 0;
    public bool HasPublishPrepWarning => Checks.Any(c => c.RelatedSection == "publish-prep" && c.IsWarning);
    public bool CanPublish => !HasBlockers && !IsBuildRunning;
    public bool HasAnyChecks => Checks.Count > 0;

    public bool IsEditing => State == PublishDialogState.Editing;
    public bool IsUploading => State == PublishDialogState.Uploading;
    public bool IsPublished => State == PublishDialogState.Published;

    /// <summary>
    /// Pull from Workshop is replacing the form with the item's Steam copy: the form, the
    /// Workshop version picker and Publish stay locked until it lands.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormEnabled))]
    private bool _isPulling;

    public bool IsFormEnabled => IsEditing && !IsPulling;

    partial void OnIsPullingChanged(bool value) => RaiseCheckStateChanged();

    public bool IsPrimaryEnabled => State switch
    {
        PublishDialogState.Editing => CanPublish && !IsPulling,
        PublishDialogState.Published => _publishedUrl.Length > 0,
        _ => false,
    };

    public const string WorkshopAgreementUrl = "https://steamcommunity.com/sharedfiles/workshoplegalagreement";

    public string PrimaryButtonText => !IsPublished ? PublishButtonText
        : NeedsLegalAgreement ? "Open Workshop agreement"
        : "Open Workshop page";

    /// <summary>Where the success state's primary button goes: the agreement while Steam hides the item, else its page.</summary>
    public string PrimaryUrl => NeedsLegalAgreement ? WorkshopAgreementUrl : _publishedUrl;
    public string SecondaryButtonText => State switch
    {
        PublishDialogState.Uploading => "Cancel",
        PublishDialogState.Published => "Done",
        _ => "Close",
    };

    /// <summary>Why the primary button is disabled; null when it isn't.</summary>
    public string? PublishButtonToolTip =>
        !IsEditing ? null
        : IsPulling ? "Publishing unlocks once the pull from Workshop finishes."
        : IsBuildRunning ? "Publishing unlocks when the build finishes."
        : HasBlockers ? "Fix the issues above to publish."
        : null;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool ShowStatusText => !HasError && !IsPublished;
    public bool ShowDraftStatus => ShowStatusText && !IsUploading;
    public string FooterStatusText =>
        IsUploading ? UploadStatusText
        : !string.IsNullOrEmpty(DraftStatusText) ? DraftStatusText
        : "Edits save as a draft automatically";
    public string BannerToggleToolTip => IsBannerExpanded ? "Hide details" : "Show details";

    /// <summary>Drives banner color and icon. One of: blocker | warning | info | ok.</summary>
    public string BannerLevel =>
        HasBlockers ? "blocker"
        : HasInfo ? "info"
        : HasWarnings ? "warning"
        : "ok";

    public bool IsBannerLevelBlocker => BannerLevel == "blocker";
    public bool IsBannerLevelWarning => BannerLevel == "warning";
    public bool IsBannerLevelInfo => BannerLevel == "info";
    public bool IsBannerLevelOk => BannerLevel == "ok";

    public string BannerIcon => BannerLevel switch
    {
        "blocker" => "", // Error
        "warning" => "", // Warning
        "info" => "",    // Info
        _ => ""           // CheckMark
    };

    public string BannerSummary => string.Join(" · ", new[]
    {
        HasBlockers ? "Can't publish" : "Ready to publish",
        HasBlockers ? $"{BlockerCount:N0} issue{(BlockerCount == 1 ? "" : "s")} to fix" : null,
        HasWarnings ? $"{WarningCount:N0} warning{(WarningCount == 1 ? "" : "s")}" : null,
        HasInfo ? $"{InfoCount:N0} item{(InfoCount == 1 ? "" : "s")} to review" : null,
    }.OfType<string>());

    /// <summary>
    /// Banner is forced-expanded when there are blockers, info rows, or a missing
    /// publish-prep pass. Other pure-warning cases start collapsed.
    /// </summary>
    public bool IsBannerForcedExpanded => HasBlockers || HasInfo || HasPublishPrepWarning;
    public bool CanCollapseBanner => !IsBannerForcedExpanded && Checks.Count > 0;
    public bool ShowBannerRows => IsBannerForcedExpanded || (IsBannerExpanded && Checks.Count > 0);

    public bool IsThumbnailWarning => Checks.Any(c => c.RelatedSection == "thumbnail" && c.IsWarning);
    public bool IsThumbnailBlocker => Checks.Any(c => c.RelatedSection == "thumbnail" && c.IsBlocker);
    public bool IsTitleBlocker => Checks.Any(c => c.RelatedSection == "title" && c.IsBlocker);
    public bool IsDescriptionWarning => Checks.Any(c => c.RelatedSection == "description" && c.IsWarning);
    public bool IsDescriptionBlocker => Checks.Any(c => c.RelatedSection == "description" && c.IsBlocker);
    public bool IsChangelogBlocker => Checks.Any(c => c.RelatedSection == "changelog" && c.IsBlocker);
    public bool IsGalleryBlocker => Checks.Any(c => c.RelatedSection == "gallery" && c.IsBlocker);

    public string ChevronIcon => IsBannerExpanded ? "" : "";

    public string DescriptionCount => $"{Description.Length:N0} chars";
    public string ChangelogCount => $"{Changelog.Length:N0} chars";
    public string PreviewImageCount => $"{PreviewImages.Count:N0} image{(PreviewImages.Count == 1 ? "" : "s")}";
    public bool CanDeleteProfile => PublishProfiles.Count > 1;
    public string? DeleteProfileToolTip => CanDeleteProfile ? null : "A project needs at least one Workshop version.";
    public bool IsNewWorkshopItem => string.IsNullOrWhiteSpace(SelectedProfile?.Profile.WorkshopJson.PublisherId);
    public string PublishButtonText => IsNewWorkshopItem ? "Create Workshop item" : "Publish update";
    public string SelectedProfileDetail => SelectedProfile?.WorkshopIdLabel ?? "";
    public string HeaderSubtitle => string.Join(" · ",
        new[] { ProjectName, ProjectTypeLabel, EstimatedSizeLabel }.Where(part => !string.IsNullOrWhiteSpace(part)));
    public bool HasWorkshopMediaFolder => !string.IsNullOrWhiteSpace(WorkshopMediaFolder);
    public string MediaFolderToolTip => HasWorkshopMediaFolder
        ? "Open the Workshop media folder"
        : "This project has no workshop_media folder yet. Pulling from the Workshop creates it.";
    public string TagsSelectedCount
    {
        get
        {
            var n = Tags.Count(t => t.IsChecked);
            return $"{n:N0} selected";
        }
    }
    public bool HasSelectedPreviewImage => SelectedPreviewImage is not null;
    public WorkshopItemVisibility SelectedVisibility =>
        VisibilityOptions.Count == 0
            ? WorkshopItemVisibility.Private
            : VisibilityOptions[Math.Clamp(SelectedVisibilityIndex, 0, VisibilityOptions.Count - 1)].Value;

    public PublishDialogViewModel()
    {
        PreviewImages.CollectionChanged += OnPreviewImagesChanged;
        Checks.CollectionChanged += OnChecksChanged;
        PublishProfiles.CollectionChanged += OnPublishProfilesChanged;
        _localAutosaveTimer.Tick += (_, _) => FlushLocalAutosave();
        _thumbnailTimer.Tick += (_, _) => ApplyPendingThumbnail();
    }

    partial void OnEstimatedSizeLabelChanged(string value) => OnPropertyChanged(nameof(HeaderSubtitle));

    partial void OnProjectNameChanged(string value) => OnPropertyChanged(nameof(HeaderSubtitle));

    partial void OnStateChanged(PublishDialogState value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(IsFormEnabled));
        OnPropertyChanged(nameof(IsUploading));
        OnPropertyChanged(nameof(IsPublished));
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(SecondaryButtonText));
        OnPropertyChanged(nameof(ShowStatusText));
        OnPropertyChanged(nameof(ShowDraftStatus));
        OnPropertyChanged(nameof(FooterStatusText));
        RaiseCheckStateChanged();
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowStatusText));
        OnPropertyChanged(nameof(ShowDraftStatus));
    }

    partial void OnDraftStatusTextChanged(string value) => OnPropertyChanged(nameof(FooterStatusText));

    partial void OnUploadStatusTextChanged(string value) => OnPropertyChanged(nameof(FooterStatusText));

    /// <summary>A neutral footer message; replaces any error.</summary>
    public void ShowStatus(string message)
    {
        ErrorMessage = null;
        ErrorDetail = null;
        DraftStatusText = message;
    }

    /// <summary>Clears the footer message if it is still <paramref name="message"/> (nothing newer replaced it).</summary>
    public void ClearStatus(string message)
    {
        if (!HasError && DraftStatusText == message)
            DraftStatusText = "";
    }

    /// <summary>An inline footer error. <paramref name="detail"/> shows as its tooltip.</summary>
    public void ShowError(string message, string? detail = null)
    {
        ErrorMessage = message;
        ErrorDetail = string.IsNullOrWhiteSpace(detail) ? null : detail;
    }

    /// <summary>
    /// True when this publish would make the item public and Steam doesn't already show it
    /// as public (a new item never is).
    /// </summary>
    public bool MakesItemPublic =>
        SelectedVisibility == WorkshopItemVisibility.Public
        && (IsNewWorkshopItem
            || !_knownRemoteVisibility.TryGetValue(SelectedProfile!.Profile.WorkshopJson.PublisherId, out var known)
            || known != WorkshopItemVisibility.Public);

    public void SetKnownRemoteVisibility(string publisherId, WorkshopItemVisibility visibility)
    {
        if (!string.IsNullOrWhiteSpace(publisherId))
            _knownRemoteVisibility[publisherId] = visibility;
    }

    public void BeginUpload()
    {
        ErrorMessage = null;
        ErrorDetail = null;
        UploadStatusText = IsNewWorkshopItem ? "Creating Workshop item…" : "Preparing upload…";
        UploadPercent = 0;
        IsUploadProgressKnown = false;
        State = PublishDialogState.Uploading;
    }

    public void ReportUploadProgress(WorkshopUploadProgress progress)
    {
        if (!IsUploading)
            return;

        UploadStatusText = progress.Stage switch
        {
            WorkshopUploadStage.Uploading when progress.Fraction is { } fraction => $"Uploading · {fraction * 100:0}%",
            WorkshopUploadStage.Uploading => "Uploading…",
            WorkshopUploadStage.Finishing => "Finishing upload…",
            _ => "Preparing upload…",
        };
        IsUploadProgressKnown = progress.Stage == WorkshopUploadStage.Uploading && progress.Fraction is not null;
        UploadPercent = progress.Stage == WorkshopUploadStage.Finishing ? 100 : (progress.Fraction ?? 0) * 100;
    }

    /// <param name="needsLegalAgreement">Steam hides the item until the user accepts the Workshop agreement.</param>
    public void CompleteUpload(string publisherId, WorkshopItemVisibility visibility, bool needsLegalAgreement = false)
    {
        SetKnownRemoteVisibility(publisherId, visibility);
        _publishedUrl = $"https://steamcommunity.com/sharedfiles/filedetails/?id={publisherId}";
        DraftStatusText = "";
        NeedsLegalAgreement = needsLegalAgreement;
        State = PublishDialogState.Published;
    }

    /// <summary>Set when Steam says the Workshop agreement is still to accept; the item is hidden until then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublishedStatusText), nameof(PrimaryButtonText), nameof(PrimaryUrl))]
    private bool _needsLegalAgreement;

    public string PublishedStatusText => NeedsLegalAgreement
        ? "Published. Steam hides it until you accept the Workshop agreement."
        : "Published to Steam Workshop";

    /// <summary>Back to editing with the draft intact and the reason inline.</summary>
    public void EndUploadWithError(string message, string? detail = null)
    {
        State = PublishDialogState.Editing;
        ShowError(message, detail);
    }

    partial void OnIsBuildRunningChanged(bool value) => RaiseCheckStateChanged();

    partial void OnProjectTypeLabelChanged(string value) => OnPropertyChanged(nameof(HeaderSubtitle));

    /// <summary>Replaces the "Prepare for publish" freshness row (used while/after the prep build runs).</summary>
    public void SetPublishPrepCheck(PublishPreflightCheck? check) =>
        ReplaceSectionCheck("publish-prep", check);

    public void SetSectionCheck(string sectionTag, PublishPreflightCheck? check) =>
        ReplaceSectionCheck(sectionTag, check);

    public void LoadProfiles(WorkshopProfilesData profilesData, string workshopType, string folderName)
    {
        _isLoadingProfile = true;
        try
        {
            _workshopType = workshopType;
            _folderName = folderName;
            _profilesData = profilesData;
            _profilesData.Normalize();

            PublishProfiles.Clear();
            foreach (var profile in _profilesData.Profiles)
            {
                EnsureWorkshopDefaults(profile.WorkshopJson);
                PublishProfiles.Add(new PublishProfileOption(profile));
            }

            var active = PublishProfiles.FirstOrDefault(p =>
                string.Equals(p.Id, _profilesData.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
                ?? PublishProfiles.FirstOrDefault();

            SelectedProfile = active;
            _loadedProfile = active;
        }
        finally
        {
            _isLoadingProfile = false;
        }

        if (SelectedProfile is not null)
            LoadProfileIntoForm(SelectedProfile.Profile);

        NotifyProfileStateChanged();
    }

    public WorkshopItemData GetActiveWorkshopData()
    {
        SaveCurrentFormToActiveProfile();
        return SelectedProfile?.Profile.WorkshopJson.Clone() ?? BuildWorkshopData("");
    }

    public void SaveCurrentFormToActiveProfile()
    {
        if (_loadedProfile is null)
            return;

        SaveCurrentFormToProfile(_loadedProfile);
    }

    public string GetDefaultNewProfileName() =>
        GetUniqueProfileName(PublishProfiles.Any(p => string.Equals(p.Name, "Canary", StringComparison.OrdinalIgnoreCase))
            ? "Version"
            : "Canary");

    public string GetDefaultDuplicateProfileName() =>
        GetUniqueProfileName($"{SelectedProfile?.Name ?? "Version"} copy");

    /// <summary>Why <paramref name="name"/> can't name a Workshop version, or null when it can.</summary>
    public string? ValidateProfileNameMessage(string name, bool renamingSelected)
    {
        name = name.Trim();
        if (name.Length == 0)
            return "Enter a name.";
        if (name.Length > WorkshopVersionNameMaxLength)
            return $"Use {WorkshopVersionNameMaxLength} characters or fewer.";
        return ValidateProfileName(name, renamingSelected ? SelectedProfile?.Id : null)
            ? null
            : "Another Workshop version already has this name.";
    }

    public bool CreateProfile(string name)
    {
        name = name.Trim();
        if (!ValidateProfileName(name))
            return false;

        // A separate Workshop item: nothing carries over but what identifies the project.
        var workshopJson = new WorkshopItemData { Title = ProjectName, Type = _workshopType, FolderName = _folderName };
        workshopJson.SetVisibility(WorkshopItemVisibility.Private);

        return AddAndSelectProfile(name, workshopJson);
    }

    public bool DuplicateSelectedProfile(string name)
    {
        name = name.Trim();
        if (!ValidateProfileName(name))
            return false;

        var workshopJson = GetActiveWorkshopData();
        workshopJson.PublisherId = "";
        workshopJson.Changelog = "";
        workshopJson.SetVisibility(WorkshopItemVisibility.Private);

        return AddAndSelectProfile(name, workshopJson);
    }

    public bool RenameSelectedProfile(string name)
    {
        name = name.Trim();
        if (SelectedProfile is null)
            return false;
        if (!ValidateProfileName(name, SelectedProfile.Id))
            return false;

        SelectedProfile.Name = name;
        RaiseProfilesChanged();
        NotifyProfileStateChanged();
        return true;
    }

    public bool DeleteSelectedProfile()
    {
        if (SelectedProfile is null || PublishProfiles.Count <= 1)
            return false;

        var remove = SelectedProfile;
        var next = PublishProfiles.FirstOrDefault(p => p != remove);
        PublishProfiles.Remove(remove);
        _profilesData.Profiles.Remove(remove.Profile);
        SelectedProfile = next;
        RaiseProfilesChanged();
        NotifyProfileStateChanged();
        return true;
    }

    public void ApplyPublishedFileId(string publisherId)
    {
        SaveCurrentFormToActiveProfile();
        if (SelectedProfile is null)
            return;

        SelectedProfile.Profile.WorkshopJson.PublisherId = publisherId;
        SelectedProfile.Refresh();
        NotifyProfileStateChanged();
    }

    /// <summary>Replace the entire preflight check list (called once on dialog open).</summary>
    public void LoadChecks(IEnumerable<PublishPreflightCheck> checks)
    {
        Checks.Clear();
        foreach (var check in checks)
            Checks.Add(check);

        RefreshThumbnailCheck();
        RefreshTextChecks();
        RefreshGalleryCheck();

        // Auto-collapse the banner only when warnings are the only thing — blockers/info
        // need the rows visible to act on.
        IsBannerExpanded = IsBannerForcedExpanded;
    }

    /// <summary>Re-evaluates the thumbnail check against the current Thumbnail field.</summary>
    public void RefreshThumbnailCheck() =>
        ReplaceSectionCheck("thumbnail", PublishPreflightBuilder.BuildThumbnailCheck(Thumbnail));

    public void RefreshTextChecks()
    {
        ReplaceSectionCheck("title", PublishPreflightBuilder.BuildTitleCheck(Title));
        ReplaceSectionCheck("description", PublishPreflightBuilder.BuildDescriptionCheck(Description));
        ReplaceSectionCheck("changelog", PublishPreflightBuilder.BuildChangelogCheck(Changelog));
    }

    /// <summary>Re-evaluates the gallery check against the current PreviewImages list.</summary>
    public void RefreshGalleryCheck() =>
        ReplaceSectionCheck("gallery", PublishPreflightBuilder.BuildGalleryCheck(PreviewImages.Select(p => p.Path)));

    /// <summary>Removes any check matching the given Check name (e.g., to dismiss the workshop sync info row).</summary>
    public void RemoveCheckByName(string check)
    {
        var match = Checks.FirstOrDefault(c => c.Check == check);
        if (match is not null)
            Checks.Remove(match);
    }

    /// <summary>
    /// Keeps at most one row per section and patches it in place: an unchanged row is left
    /// alone (no collection change), a changed one is swapped at its index.
    /// </summary>
    private void ReplaceSectionCheck(string sectionTag, PublishPreflightCheck? newCheck)
    {
        var index = -1;
        for (var i = Checks.Count - 1; i >= 0; i--)
        {
            if (Checks[i].RelatedSection != sectionTag)
                continue;
            if (index >= 0)
                Checks.RemoveAt(index);
            index = i;
        }

        if (index < 0)
        {
            if (newCheck is not null)
                Checks.Add(newCheck);
        }
        else if (newCheck is null)
        {
            Checks.RemoveAt(index);
        }
        else if (!HasSameContent(Checks[index], newCheck))
        {
            Checks[index] = newCheck;
        }
    }

    private static bool HasSameContent(PublishPreflightCheck a, PublishPreflightCheck b) =>
        a.Status == b.Status
        && a.Check == b.Check
        && a.Detail == b.Detail
        && a.DetailToolTip == b.DetailToolTip
        && a.RelatedSection == b.RelatedSection
        && HasSameContent(a.PrimaryAction, b.PrimaryAction)
        && HasSameContent(a.SecondaryAction, b.SecondaryAction);

    private static bool HasSameContent(PublishCheckAction? a, PublishCheckAction? b) =>
        a is null || b is null
            ? a is null && b is null
            : a.Label == b.Label && a.Kind == b.Kind && a.Payload == b.Payload;

    // Everything derived from Checks, IsBuildRunning or IsBannerExpanded. Values are cached
    // so a change only notifies the properties whose value actually moved.
    private static readonly (string Name, Func<PublishDialogViewModel, object> Get)[] CheckStateProperties =
    [
        (nameof(BlockerCount), vm => vm.BlockerCount),
        (nameof(WarningCount), vm => vm.WarningCount),
        (nameof(InfoCount), vm => vm.InfoCount),
        (nameof(HasBlockers), vm => vm.HasBlockers),
        (nameof(HasWarnings), vm => vm.HasWarnings),
        (nameof(HasInfo), vm => vm.HasInfo),
        (nameof(HasPublishPrepWarning), vm => vm.HasPublishPrepWarning),
        (nameof(HasAnyChecks), vm => vm.HasAnyChecks),
        (nameof(CanPublish), vm => vm.CanPublish),
        (nameof(PublishButtonToolTip), vm => vm.PublishButtonToolTip ?? ""),
        (nameof(IsPrimaryEnabled), vm => vm.IsPrimaryEnabled),
        (nameof(BannerToggleToolTip), vm => vm.BannerToggleToolTip),
        (nameof(BannerLevel), vm => vm.BannerLevel),
        (nameof(IsBannerLevelBlocker), vm => vm.IsBannerLevelBlocker),
        (nameof(IsBannerLevelWarning), vm => vm.IsBannerLevelWarning),
        (nameof(IsBannerLevelInfo), vm => vm.IsBannerLevelInfo),
        (nameof(IsBannerLevelOk), vm => vm.IsBannerLevelOk),
        (nameof(BannerIcon), vm => vm.BannerIcon),
        (nameof(BannerSummary), vm => vm.BannerSummary),
        (nameof(IsBannerForcedExpanded), vm => vm.IsBannerForcedExpanded),
        (nameof(CanCollapseBanner), vm => vm.CanCollapseBanner),
        (nameof(ShowBannerRows), vm => vm.ShowBannerRows),
        (nameof(ChevronIcon), vm => vm.ChevronIcon),
        (nameof(IsThumbnailWarning), vm => vm.IsThumbnailWarning),
        (nameof(IsThumbnailBlocker), vm => vm.IsThumbnailBlocker),
        (nameof(IsTitleBlocker), vm => vm.IsTitleBlocker),
        (nameof(IsDescriptionWarning), vm => vm.IsDescriptionWarning),
        (nameof(IsDescriptionBlocker), vm => vm.IsDescriptionBlocker),
        (nameof(IsChangelogBlocker), vm => vm.IsChangelogBlocker),
        (nameof(IsGalleryBlocker), vm => vm.IsGalleryBlocker),
    ];

    private readonly object?[] _checkStateValues = new object?[CheckStateProperties.Length];

    private void RaiseCheckStateChanged()
    {
        for (var i = 0; i < CheckStateProperties.Length; i++)
        {
            var (name, get) = CheckStateProperties[i];
            var value = get(this);
            if (Equals(value, _checkStateValues[i]))
                continue;

            _checkStateValues[i] = value;
            OnPropertyChanged(name);
        }
    }

    private void OnChecksChanged(object? sender, NotifyCollectionChangedEventArgs e) => RaiseCheckStateChanged();

    partial void OnIsBannerExpandedChanged(bool value) => RaiseCheckStateChanged();

    partial void OnDescriptionChanged(string value)
    {
        OnPropertyChanged(nameof(DescriptionCount));
        RefreshTextChecks();
        QueueLocalAutosave();
    }

    partial void OnTitleChanged(string value)
    {
        RefreshTextChecks();
        QueueLocalAutosave();
    }

    partial void OnChangelogChanged(string value)
    {
        OnPropertyChanged(nameof(ChangelogCount));
        RefreshTextChecks();
        QueueLocalAutosave();
    }

    partial void OnSelectedVisibilityIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedVisibility));
        QueueLocalAutosave();
    }

    partial void OnSelectedPreviewImageChanged(WorkshopPreviewImageItem? value) =>
        OnPropertyChanged(nameof(HasSelectedPreviewImage));

    partial void OnThumbnailChanged(string value)
    {
        QueueLocalAutosave();

        // Loading a version or clearing the field applies at once; typing waits for a pause.
        _thumbnailTimer.Stop();
        if (_isLoadingForm || string.IsNullOrWhiteSpace(value))
            ApplyPendingThumbnail(force: true);
        else
            _thumbnailTimer.Start();
    }

    /// <summary>Checks the thumbnail path and loads its preview now if typing left that pending.</summary>
    public void ApplyPendingThumbnail() => ApplyPendingThumbnail(force: false);

    private void ApplyPendingThumbnail(bool force)
    {
        if (!force && !_thumbnailTimer.IsEnabled)
            return;

        _thumbnailTimer.Stop();
        _ = LoadThumbnailPreviewAsync(Thumbnail);
        RefreshThumbnailCheck();
    }

    private async Task LoadThumbnailPreviewAsync(string path)
    {
        _thumbnailPreviewCts?.Cancel();
        _thumbnailPreviewCts?.Dispose();
        var cts = new CancellationTokenSource();
        _thumbnailPreviewCts = cts;
        var token = cts.Token;

        ThumbnailPreview?.Dispose();
        ThumbnailPreview = null;
        ThumbnailPreviewError = null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var (preview, error) = await LoadBitmapPreviewAsync(path, ThumbnailPreviewDecodeWidth, token);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested || _thumbnailPreviewCts != cts)
            {
                preview?.Dispose();
                return;
            }

            ThumbnailPreview = preview;
            ThumbnailPreviewError = error;
        });
    }

    /// <summary>The decoded preview, or why there isn't one (null when cancelled).</summary>
    internal static async Task<(Bitmap? Preview, string? Error)> LoadBitmapPreviewAsync(string path, int decodeWidth, CancellationToken ct)
    {
        try
        {
            return (await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var stream = File.OpenRead(path);
                ct.ThrowIfCancellationRequested();
                return Bitmap.DecodeToWidth(stream, decodeWidth);
            }, ct), null);
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
        catch (Exception ex)
        {
            return (null, ex is IOException or UnauthorizedAccessException
                ? "Couldn't read this image."
                : "Couldn't show this image. Use a PNG, JPG, or GIF.");
        }
    }

    public void LoadTags(string[]? selectedTags = null)
    {
        foreach (var existing in Tags)
            existing.PropertyChanged -= OnTagItemChanged;
        Tags.Clear();

        foreach (var tag in MainWindowViewModel.WorkshopTags)
        {
            var isSelected = selectedTags is not null &&
                Array.Exists(selectedTags, t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
            var item = new TagItem(tag, isSelected);
            item.PropertyChanged += OnTagItemChanged;
            Tags.Add(item);
        }
        OnPropertyChanged(nameof(TagsSelectedCount));
    }

    private void OnTagItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TagItem.IsChecked))
        {
            OnPropertyChanged(nameof(TagsSelectedCount));
            QueueLocalAutosave();
        }
    }

    public void LoadPreviewImages(IEnumerable<string>? previewImages)
    {
        ClearPreviewImages();
        if (previewImages is not null)
            AddPreviewImages(previewImages);
    }

    public void AddPreviewImages(IEnumerable<string> previewImages)
    {
        foreach (var image in previewImages)
        {
            var path = image.Trim();
            if (path.Length == 0 || PreviewImages.Any(existing =>
                    string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            PreviewImages.Add(new WorkshopPreviewImageItem(path));
        }
    }

    public void RemoveSelectedPreviewImage()
    {
        if (SelectedPreviewImage is null)
            return;

        var item = SelectedPreviewImage;
        PreviewImages.Remove(item);
        item.Dispose();
        SelectedPreviewImage = null;
    }

    public void ClearPreviewImages()
    {
        foreach (var image in PreviewImages)
            image.Dispose();
        PreviewImages.Clear();
        SelectedPreviewImage = null;
    }

    public void DisposePreviewResources()
    {
        FlushLocalAutosave();
        _thumbnailTimer.Stop();
        _isLoadingForm = true;
        _thumbnailPreviewCts?.Cancel();
        _thumbnailPreviewCts?.Dispose();
        _thumbnailPreviewCts = null;
        ThumbnailPreview?.Dispose();
        ThumbnailPreview = null;
        ClearPreviewImages();
        _isLoadingForm = false;
    }

    public void LoadVisibility(WorkshopItemVisibility visibility)
    {
        var index = 0;
        for (var i = 0; i < VisibilityOptions.Count; i++)
        {
            if (VisibilityOptions[i].Value == visibility)
            {
                index = i;
                break;
            }
        }
        SelectedVisibilityIndex = index;
    }

    public string[] GetSelectedTags()
    {
        var selected = new System.Collections.Generic.List<string>();
        foreach (var tag in Tags)
            if (tag.IsChecked)
                selected.Add(tag.Name);
        return [.. selected];
    }

    public string[] GetPreviewImagePaths() =>
        [.. PreviewImages.Select(image => image.Path)];

    private void OnPreviewImagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(PreviewImageCount));
        RefreshGalleryCheck();
        QueueLocalAutosave();
    }

    // Switching only changes what the form shows; the choice reaches the disk with the next edit.
    private void OnSelectedProfileChanged(PublishProfileOption? value)
    {
        if (_isLoadingProfile)
            return;

        SaveCurrentFormToActiveProfile();

        if (value is not null)
        {
            _profilesData.ActiveProfileId = value.Id;
            _loadedProfile = value;
            LoadProfileIntoForm(value.Profile);
        }

        NotifyProfileStateChanged();
    }

    private bool AddAndSelectProfile(string name, WorkshopItemData workshopJson)
    {
        var profile = new WorkshopPublishProfile
        {
            Id = _profilesData.CreateUniqueProfileId(name),
            Name = name,
            WorkshopJson = workshopJson
        };
        EnsureWorkshopDefaults(profile.WorkshopJson);

        _profilesData.Profiles.Add(profile);
        var option = new PublishProfileOption(profile);
        PublishProfiles.Add(option);
        SelectedProfile = option;
        RaiseProfilesChanged();
        NotifyProfileStateChanged();
        return true;
    }

    private void LoadProfileIntoForm(WorkshopPublishProfile profile)
    {
        _isLoadingForm = true;
        try
        {
            EnsureWorkshopDefaults(profile.WorkshopJson);
            var data = profile.WorkshopJson;
            Title = data.Title;
            Description = data.Description;
            Thumbnail = data.Thumbnail;
            Changelog = data.Changelog;
            LoadTags(data.GetTagList().ToArray());
            LoadPreviewImages(data.GetPreviewImageList());
            LoadVisibility(data.GetVisibility());
        }
        finally
        {
            _isLoadingForm = false;
        }
    }

    private void SaveCurrentFormToProfile(PublishProfileOption option)
    {
        var publisherId = option.Profile.WorkshopJson.PublisherId;
        option.Profile.WorkshopJson = BuildWorkshopData(publisherId);
        option.Refresh();
    }

    private WorkshopItemData BuildWorkshopData(string publisherId)
    {
        var data = new WorkshopItemData
        {
            PublisherId = publisherId,
            Title = Title,
            Description = Description,
            Thumbnail = Thumbnail,
            Type = _workshopType,
            FolderName = _folderName,
            Changelog = Changelog,
        };
        data.SetTagList([.. GetSelectedTags()]);
        data.SetVisibility(SelectedVisibility);
        data.SetPreviewImageList(GetPreviewImagePaths());
        return data;
    }

    private void EnsureWorkshopDefaults(WorkshopItemData data)
    {
        if (string.IsNullOrWhiteSpace(data.Type))
            data.Type = _workshopType;
        if (string.IsNullOrWhiteSpace(data.FolderName))
            data.FolderName = _folderName;
        if (string.IsNullOrWhiteSpace(data.Visibility))
            data.SetVisibility(WorkshopItemVisibility.Private);
    }

    private string GetUniqueProfileName(string baseName)
    {
        var candidate = baseName.Trim();
        if (candidate.Length == 0)
            candidate = "Version";

        if (PublishProfiles.All(p => !string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            return candidate;

        var suffix = 2;
        while (PublishProfiles.Any(p => string.Equals(p.Name, $"{candidate} {suffix}", StringComparison.OrdinalIgnoreCase)))
            suffix++;

        return $"{candidate} {suffix}";
    }

    private bool ValidateProfileName(string name, string? existingProfileId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return PublishProfiles.All(p =>
            string.Equals(p.Id, existingProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private void RaiseProfilesChanged()
    {
        SaveCurrentFormToActiveProfile();
        _profilesData.Normalize();
        if (SelectedProfile is not null)
            _profilesData.ActiveProfileId = SelectedProfile.Id;

        OnProfilesChanged?.Invoke(_profilesData, SelectedProfile?.Profile.WorkshopJson.Clone() ?? BuildWorkshopData(""));
    }

    /// <summary>
    /// Replaces the form with what was pulled from Steam, without counting it as an edit (the
    /// caller saves it). The thumbnail and gallery are cleared when Steam has none.
    /// </summary>
    public void ApplyPulled(WorkshopItemData remote, string thumbnail, IEnumerable<string> gallery)
    {
        _localAutosaveTimer.Stop();
        _isLoadingForm = true;
        try
        {
            Title = remote.Title;
            Description = remote.Description;
            LoadTags([.. remote.GetTagList()]);
            LoadVisibility(remote.GetVisibility());
            Thumbnail = thumbnail;
            LoadPreviewImages(gallery);
        }
        finally
        {
            _isLoadingForm = false;
        }

        SaveCurrentFormToActiveProfile();
    }

    /// <summary>Clears the changelog once it has gone up with an upload, so the next publish doesn't resend it.</summary>
    public void ClearPublishedChangelog()
    {
        _isLoadingForm = true;
        try
        {
            Changelog = "";
        }
        finally
        {
            _isLoadingForm = false;
        }

        SaveCurrentFormToActiveProfile();
    }

    /// <summary>Points every version's thumbnail and gallery, and the form, at media moved out of zone.</summary>
    public void RemapMediaPaths(IReadOnlyDictionary<string, string> moved, string zoneFolder)
    {
        foreach (var profile in _profilesData.Profiles)
            WorkshopFiles.RemapMediaPaths(profile.WorkshopJson, zoneFolder, moved);

        var thumbnail = WorkshopFiles.RemapMediaPath(Thumbnail, zoneFolder, moved);
        var gallery = GetPreviewImagePaths().Select(path => WorkshopFiles.RemapMediaPath(path, zoneFolder, moved)).ToArray();
        if (thumbnail == Thumbnail && gallery.SequenceEqual(GetPreviewImagePaths()))
            return;

        _isLoadingForm = true;
        try
        {
            Thumbnail = thumbnail;
            LoadPreviewImages(gallery);
        }
        finally
        {
            _isLoadingForm = false;
        }
    }

    private void QueueLocalAutosave()
    {
        if (_isLoadingProfile || _isLoadingForm || _loadedProfile is null)
            return;

        // Editing moves on from whatever went wrong last.
        ErrorMessage = null;
        ErrorDetail = null;
        DraftStatusText = "Unsaved changes";
        _localAutosaveTimer.Stop();
        _localAutosaveTimer.Start();
    }

    public void FlushLocalAutosave()
    {
        if (!_localAutosaveTimer.IsEnabled)
            return;

        _localAutosaveTimer.Stop();
        RaiseProfilesChanged();
        DraftStatusText = $"Draft autosaved {DateTime.Now:t}";
    }

    private void OnPublishProfilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyProfileStateChanged();
    }

    private void NotifyProfileStateChanged()
    {
        OnPropertyChanged(nameof(CanDeleteProfile));
        OnPropertyChanged(nameof(DeleteProfileToolTip));
        OnPropertyChanged(nameof(IsNewWorkshopItem));
        OnPropertyChanged(nameof(PublishButtonText));
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(SelectedProfileDetail));
    }
}

public class VisibilityOption(string name, WorkshopItemVisibility value)
{
    public string Name { get; } = name;
    public WorkshopItemVisibility Value { get; } = value;
}

public partial class PublishProfileOption : ObservableObject
{
    public WorkshopPublishProfile Profile { get; }
    public string Id => Profile.Id;

    public string Name
    {
        get => Profile.Name;
        set
        {
            if (Profile.Name == value)
                return;

            Profile.Name = value;
            OnPropertyChanged();
        }
    }

    public string WorkshopIdLabel =>
        string.IsNullOrWhiteSpace(Profile.WorkshopJson.PublisherId)
            ? "New Workshop item"
            : $"Workshop ID {Profile.WorkshopJson.PublisherId}";

    public PublishProfileOption(WorkshopPublishProfile profile)
    {
        Profile = profile;
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(WorkshopIdLabel));
    }
}

public partial class TagItem : ObservableObject
{
    public string Name { get; }

    [ObservableProperty]
    private bool _isChecked;

    public TagItem(string name, bool isChecked = false)
    {
        Name = name;
        IsChecked = isChecked;
    }
}

public partial class WorkshopPreviewImageItem : ObservableObject, IDisposable
{
    private const int PreviewDecodeWidth = 120;
    private readonly CancellationTokenSource _previewCts = new();
    private bool _isDisposed;

    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    // Path never changes, so the file size is read once rather than on every binding read.
    private string? _detail;
    public string Detail => _detail ??= GetDetail();

    [ObservableProperty]
    private Bitmap? _preview;

    public WorkshopPreviewImageItem(string path)
    {
        Path = path;
        _ = LoadPreviewAsync();
    }

    public void Dispose()
    {
        _isDisposed = true;
        _previewCts.Cancel();
        _previewCts.Dispose();
        Preview?.Dispose();
        Preview = null;
    }

    private async Task LoadPreviewAsync()
    {
        if (!File.Exists(Path))
            return;

        var token = _previewCts.Token;
        var (preview, error) = await PublishDialogViewModel.LoadBitmapPreviewAsync(Path, PreviewDecodeWidth, token);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isDisposed || token.IsCancellationRequested)
            {
                preview?.Dispose();
                return;
            }

            Preview?.Dispose();
            Preview = preview;
            if (error is not null)
            {
                // In place of the size, so the empty square says why.
                _detail = error;
                OnPropertyChanged(nameof(Detail));
            }
        });
    }

    private string GetDetail()
    {
        if (!File.Exists(Path))
            return "Missing file";

        try
        {
            var info = new FileInfo(Path);
            return $"{info.Length / 1024d:N0} KB";
        }
        catch
        {
            return Path;
        }
    }
}
