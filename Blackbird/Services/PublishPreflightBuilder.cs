using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blackbird.Models;

namespace Blackbird.Services;

/// <summary>
/// Builds the preflight check list shown as a banner inside the Publish dialog.
/// Validates project structure, build output, and workshop media; attaches
/// contextual action buttons to anything fixable.
/// </summary>
public static class PublishPreflightBuilder
{
    private const long SteamWorkshopPreviewMaxBytes = 1024 * 1024;
    // Steam's limits are UTF-8 bytes (k_cchPublishedDocumentTitleMax and friends), not characters:
    // accented letters take two bytes and emoji four.
    private const int SteamWorkshopTitleMaxBytes = 128;
    private const int SteamWorkshopDescriptionMaxBytes = 8000;
    private const int SteamWorkshopChangelogMaxBytes = 8000;

    private static int Utf8Bytes(string text) => System.Text.Encoding.UTF8.GetByteCount(text);

    public static List<PublishPreflightCheck> Build(
        ProjectItem project,
        IFileSystemService fileSystem,
        ISteamWorkshopService steam,
        string workshopFolder,
        string[] xpakFiles,
        DateTime? lastPublishPrep,
        string shipLanguage)
    {
        var checks = new List<PublishPreflightCheck>();
        var setup = fileSystem.ValidateSetup();

        if (!setup.IsHealthy)
        {
            checks.Add(new PublishPreflightCheck
            {
                Status = "Blocker",
                Check = "Setup",
                Detail = "Some of the Mod Tools Blackbird needs are missing.",
                PrimaryAction = new PublishCheckAction { Label = "Run Setup Doctor", Kind = "setup-doctor" },
            });
        }

        if (!steam.IsInitialized)
        {
            checks.Add(new PublishPreflightCheck
            {
                Status = "Blocker",
                Check = "Steam",
                Detail = "Steam isn't running. Start Steam and sign in to publish.",
                DetailToolTip = string.IsNullOrWhiteSpace(steam.InitializationDetail) ? null : steam.InitializationDetail,
                PrimaryAction = new PublishCheckAction { Label = "Try again", Kind = "retry-steam" },
            });
        }

        if (WorkshopFiles.FindCorruptFile(workshopFolder) is { } corruptFile)
        {
            var corrupt = Path.GetFullPath(corruptFile);
            checks.Add(new PublishPreflightCheck
            {
                Status = "Blocker",
                Check = "Workshop files",
                Detail = $"{Path.GetFileName(corrupt)} isn't valid JSON, so Blackbird can't tell which Workshop item this is. Fix the file, then open Publish again.",
                DetailToolTip = corrupt,
                PrimaryAction = new PublishCheckAction { Label = "Open file", Kind = "open-file", Payload = corrupt },
            });
        }

        if (!Directory.Exists(workshopFolder))
        {
            checks.Add(new PublishPreflightCheck
            {
                Status = "Blocker",
                Check = "Publish folder",
                Detail = "The zone folder is missing. Build the project first.",
                DetailToolTip = workshopFolder,
                PrimaryAction = new PublishCheckAction { Label = "Build now", Kind = "build-now" },
                SecondaryAction = new PublishCheckAction { Label = "Open project folder", Kind = "open-folder", Payload = project.FolderPath },
            });
        }
        else
        {
            var hasContent = Directory.EnumerateFiles(workshopFolder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(workshopFolder, f))
                .Any(f => !string.Equals(f, WorkshopFiles.WorkshopJsonName, StringComparison.OrdinalIgnoreCase)
                          && !WorkshopFiles.IsBlackbirdFile(f));

            if (!hasContent)
            {
                checks.Add(new PublishPreflightCheck
                {
                    Status = "Blocker",
                    Check = "Published output",
                    Detail = "The zone folder has nothing to upload. Build (link) the project first.",
                    PrimaryAction = new PublishCheckAction { Label = "Build now", Kind = "build-now" },
                    SecondaryAction = new PublishCheckAction { Label = "Open folder", Kind = "open-folder", Payload = workshopFolder },
                });
            }
        }

        checks.Add(BuildPublishPrepCheck(lastPublishPrep));

        if (shipLanguage != "All")
        {
            checks.Add(new PublishPreflightCheck
            {
                Status = "Warning",
                Check = "Ship language",
                Detail = $"The Ship launch config builds \u201C{shipLanguage}\u201D only. Public releases usually build all languages.",
            });
        }

        if (project.Type == ProjectType.Map)
            AddMapChecks(checks, project, fileSystem);
        else
            AddModChecks(checks, project);

        if (xpakFiles.Length > 0)
        {
            checks.Add(new PublishPreflightCheck
            {
                Status = "Warning",
                Check = "XPak cleanup",
                Detail = xpakFiles.Length == 1
                    ? "1 XPak from an earlier build is in the project folder. Clean it if it's outdated."
                    : $"{xpakFiles.Length:N0} XPaks from earlier builds are in the project folder. Clean them if they're outdated.",
                PrimaryAction = new PublishCheckAction { Label = "Clean XPaks", Kind = "clean-xpaks" },
                SecondaryAction = new PublishCheckAction { Label = "Open folder", Kind = "open-folder", Payload = project.FolderPath },
            });
        }

        return checks;
    }

    /// <summary>
    /// Freshness of the "Prepare for publish" pass. The timestamp is stamped when the
    /// prep build succeeds and cleared by any later build, so a missing value means
    /// the linked output may be stale, single-language, or carry dev XPaks.
    /// </summary>
    public static PublishPreflightCheck BuildPublishPrepCheck(DateTime? lastPublishPrep)
    {
        if (lastPublishPrep is DateTime prepTime)
        {
            return new PublishPreflightCheck
            {
                Status = "OK",
                Check = "Prepare for publish",
                Detail = $"Last completed {prepTime:d MMM yyyy 'at' HH:mm}.",
                RelatedSection = "publish-prep",
            };
        }

        return new PublishPreflightCheck
        {
            Status = "Warning",
            Check = "Prepare for publish",
            Detail = "Prepare for publish hasn't been run since the last build. It cleans generated XPaks and links all languages with the Ship launch config.",
            PrimaryAction = new PublishCheckAction { Label = "Prepare now", Kind = "ready-for-publish" },
            RelatedSection = "publish-prep",
        };
    }

    public static PublishPreflightCheck? BuildThumbnailCheck(string? thumbnail)
    {
        if (string.IsNullOrWhiteSpace(thumbnail))
        {
            return new PublishPreflightCheck
            {
                Status = "Warning",
                Check = "Thumbnail",
                Detail = "No thumbnail set. The Workshop shows a placeholder.",
                RelatedSection = "thumbnail",
            };
        }

        // A thumbnail Steam can't take fails the whole upload, so it blocks.
        var error = ValidateThumbnail(thumbnail);
        if (error is not null)
        {
            return new PublishPreflightCheck
            {
                Status = "Blocker",
                Check = "Thumbnail",
                Detail = error,
                RelatedSection = "thumbnail",
            };
        }

        return null; // OK — no row needed
    }

    public static PublishPreflightCheck? BuildTitleCheck(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new PublishPreflightCheck
            {
                Status = "Blocker",
                Check = "Title",
                Detail = "Workshop title is required.",
                RelatedSection = "title",
            };
        }

        if (Utf8Bytes(title) is var titleBytes && titleBytes > SteamWorkshopTitleMaxBytes)
        {
            return new PublishPreflightCheck
            {
                Status = "Blocker",
                Check = "Title",
                Detail = $"Workshop title is {titleBytes:N0} bytes. Steam allows {SteamWorkshopTitleMaxBytes:N0} (accented letters and symbols take more than one).",
                RelatedSection = "title",
            };
        }

        return null;
    }

    public static PublishPreflightCheck? BuildDescriptionCheck(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return new PublishPreflightCheck
            {
                Status = "Warning",
                Check = "Description",
                Detail = "Workshop description is empty.",
                RelatedSection = "description",
            };
        }

        if (Utf8Bytes(description) is var descriptionBytes && descriptionBytes > SteamWorkshopDescriptionMaxBytes)
        {
            return new PublishPreflightCheck
            {
                Status = "Blocker",
                Check = "Description",
                Detail = $"Workshop description is {descriptionBytes:N0} bytes. Steam allows {SteamWorkshopDescriptionMaxBytes:N0} (accented letters and symbols take more than one).",
                RelatedSection = "description",
            };
        }

        return null;
    }

    public static PublishPreflightCheck? BuildChangelogCheck(string? changelog)
    {
        var changelogBytes = string.IsNullOrEmpty(changelog) ? 0 : Utf8Bytes(changelog);
        if (changelogBytes <= SteamWorkshopChangelogMaxBytes)
            return null;

        return new PublishPreflightCheck
        {
            Status = "Blocker",
            Check = "Changelog",
            Detail = $"Upload changelog is {changelogBytes:N0} bytes. Steam allows {SteamWorkshopChangelogMaxBytes:N0} (accented letters and symbols take more than one).",
            RelatedSection = "changelog",
        };
    }

    public static PublishPreflightCheck? BuildGalleryCheck(IEnumerable<string> previewImages)
    {
        var images = previewImages.ToArray();
        if (images.Length == 0)
            return null;

        var errors = ValidateWorkshopPreviewImages(images);
        if (errors.Length == 0)
            return null;

        // Every row is an image Steam would reject, failing the whole upload.
        return new PublishPreflightCheck
        {
            Status = "Blocker",
            Check = "Gallery images",
            Detail = string.Join("\n", errors),
            RelatedSection = "gallery",
        };
    }

    public static string? ValidateThumbnail(string? thumbnail)
    {
        if (string.IsNullOrWhiteSpace(thumbnail))
            return null;

        if (!IsWorkshopImage(thumbnail))
            return "Use a PNG, JPG, or GIF for the thumbnail.";

        if (!File.Exists(thumbnail))
            return $"{Path.GetFileName(thumbnail)} isn't there any more.";

        return CheckPreviewSize(thumbnail, "Thumbnail");
    }

    public static string[] ValidateWorkshopPreviewImages(IEnumerable<string> previewImages)
    {
        var errors = new List<string>();
        foreach (var previewImage in previewImages)
        {
            var error = ValidateWorkshopPreviewImage(previewImage);
            if (error is not null)
                errors.Add(error);
        }

        return [.. errors];
    }

    public static string? ValidateWorkshopPreviewImage(string? previewImage)
    {
        if (string.IsNullOrWhiteSpace(previewImage))
            return null;

        if (!IsWorkshopImage(previewImage))
            return $"{Path.GetFileName(previewImage)} isn't a PNG, JPG, or GIF.";

        if (!File.Exists(previewImage))
            return $"{Path.GetFileName(previewImage)} isn't there any more.";

        return CheckPreviewSize(previewImage, $"Gallery image {Path.GetFileName(previewImage)}");
    }

    public static bool IsWorkshopImage(string path) =>
        WorkshopFiles.ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Steam rejects preview images (thumbnail and gallery alike) of 1 MB or more.</summary>
    private static string? CheckPreviewSize(string path, string label)
    {
        try
        {
            return new FileInfo(path).Length >= SteamWorkshopPreviewMaxBytes
                ? $"{label} must be under 1 MB."
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't read {Path.GetFileName(path)}. {ErrorText.Describe(ex)}";
        }
    }

    private static void AddMapChecks(List<PublishPreflightCheck> checks, ProjectItem project, IFileSystemService fileSystem)
    {
        var zonePath = project.ZoneFilePath;
        if (zonePath is null || !File.Exists(zonePath))
        {
            checks.Add(new PublishPreflightCheck
            {
                Status = "Blocker",
                Check = "Map zone source",
                Detail = "The map's zone source file is missing.",
                PrimaryAction = new PublishCheckAction { Label = "Open folder", Kind = "open-folder", Payload = project.FolderPath },
            });
        }

        var prefix = project.Name.Length >= 2 ? project.Name[..2] : project.Name;
        var mapSource = Path.Combine(fileSystem.GamePath, "map_source", prefix, $"{project.Name}.map");
        if (!File.Exists(mapSource))
        {
            checks.Add(new PublishPreflightCheck
            {
                Status = "Warning",
                Check = "Map source",
                Detail = $"{project.Name}.map isn't in map_source.",
                DetailToolTip = mapSource,
            });
        }

        var szc = Path.Combine(project.FolderPath, "sound", "zoneconfig", $"{project.Name}.szc");
        if (!File.Exists(szc))
        {
            checks.Add(new PublishPreflightCheck
            {
                Status = "Warning",
                Check = "Sound zone config",
                Detail = $"{project.Name}.szc isn't in sound\\zoneconfig.",
                DetailToolTip = szc,
            });
        }
    }

    private static void AddModChecks(List<PublishPreflightCheck> checks, ProjectItem project)
    {
        if (project.ZoneFiles.Count == 0)
        {
            checks.Add(new PublishPreflightCheck
            {
                Status = "Blocker",
                Check = "Mod zones",
                Detail = "No mod zone source files were found.",
                PrimaryAction = new PublishCheckAction { Label = "Open folder", Kind = "open-folder", Payload = project.FolderPath },
            });
            return;
        }

        var selectedZones = project.ZoneFiles.Where(z => z.IsChecked).ToList();
        if (selectedZones.Count == 0)
        {
            checks.Add(new PublishPreflightCheck
            {
                Status = "Warning",
                Check = "Selected zones",
                Detail = "No zones are selected for the build.",
            });
        }

        var missingSelectedZones = selectedZones
            .Select(z => Path.Combine(project.FolderPath, "zone_source", $"{z.ZoneName}.zone"))
            .Where(path => !File.Exists(path))
            .ToArray();
        if (missingSelectedZones.Length > 0)
        {
            checks.Add(new PublishPreflightCheck
            {
                Status = "Blocker",
                Check = "Selected zone files",
                Detail = $"Missing from zone_source: {string.Join(", ", missingSelectedZones.Select(Path.GetFileName))}",
            });
        }
    }

    /// <summary>The language Ship builds. Read on the UI thread, which owns the settings.</summary>
    public static string GetShipBuildLanguage(ProjectSettings? projectSettings)
    {
        if (projectSettings is not null
            && projectSettings.EnvironmentBuildLanguages.TryGetValue(LaunchConfig.Ship, out var language)
            && !string.IsNullOrWhiteSpace(language)
            && language != LaunchConfig.DefaultBuildLanguage)
        {
            return language;
        }

        // No explicit value or "launcher default" — Ship defaults to all languages.
        return "All";
    }
}
