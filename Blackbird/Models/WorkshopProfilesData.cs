using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Blackbird.Models;

public class WorkshopProfilesData
{
    public const string DefaultProfileId = "stable";
    public const string DefaultProfileName = "Stable";

    [JsonPropertyName("activeProfileId")]
    public string ActiveProfileId { get; set; } = DefaultProfileId;

    [JsonPropertyName("profiles")]
    public List<WorkshopPublishProfile> Profiles { get; set; } = [];

    public static WorkshopProfilesData FromWorkshopJson(WorkshopItemData? data)
    {
        var profile = new WorkshopPublishProfile
        {
            Id = DefaultProfileId,
            Name = DefaultProfileName,
            WorkshopJson = data?.Clone() ?? new WorkshopItemData()
        };

        return new WorkshopProfilesData
        {
            ActiveProfileId = profile.Id,
            Profiles = [profile]
        };
    }

    public WorkshopPublishProfile GetActiveProfile()
    {
        Normalize();
        return Profiles.First(p => string.Equals(p.Id, ActiveProfileId, StringComparison.OrdinalIgnoreCase));
    }

    public void Normalize(WorkshopItemData? fallbackData = null)
    {
        Profiles ??= [];
        Profiles.RemoveAll(profile => profile is null);

        if (Profiles.Count == 0)
        {
            Profiles.Add(new WorkshopPublishProfile
            {
                Id = DefaultProfileId,
                Name = DefaultProfileName,
                WorkshopJson = fallbackData?.Clone() ?? new WorkshopItemData()
            });
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Profiles.Count; i++)
        {
            var profile = Profiles[i];
            if (string.IsNullOrWhiteSpace(profile.Name))
                profile.Name = i == 0 ? DefaultProfileName : $"Version {i + 1}";

            var id = string.IsNullOrWhiteSpace(profile.Id)
                ? MakeProfileId(profile.Name, usedIds)
                : profile.Id.Trim();

            if (!usedIds.Add(id))
                id = MakeProfileId(profile.Name, usedIds);

            profile.Id = id;
            profile.WorkshopJson ??= fallbackData?.Clone() ?? new WorkshopItemData();
        }

        if (string.IsNullOrWhiteSpace(ActiveProfileId)
            || Profiles.All(p => !string.Equals(p.Id, ActiveProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            ActiveProfileId = Profiles[0].Id;
        }
    }

    /// <summary>A deep copy, so a write can run off the UI thread while the form keeps editing this one.</summary>
    public WorkshopProfilesData Clone() => new()
    {
        ActiveProfileId = ActiveProfileId,
        Profiles =
        [
            .. Profiles.Select(profile => new WorkshopPublishProfile
            {
                Id = profile.Id,
                Name = profile.Name,
                WorkshopJson = profile.WorkshopJson.Clone(),
            })
        ],
    };

    public string CreateUniqueProfileId(string name)
    {
        var usedIds = Profiles.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return MakeProfileId(name, usedIds);
    }

    private static string MakeProfileId(string name, HashSet<string> usedIds)
    {
        var cleaned = new string((name ?? "")
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());

        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);

        cleaned = cleaned.Trim('-');
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "profile";

        var id = cleaned;
        var suffix = 2;
        while (!usedIds.Add(id))
            id = $"{cleaned}-{suffix++}";

        return id;
    }
}

public class WorkshopPublishProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("workshopJson")]
    public WorkshopItemData WorkshopJson { get; set; } = new();
}
