using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blackbird.Models;

/// <summary>Treyarch's workshop.json. Read leniently: a hand-edited file may carry nulls or a numeric PublisherID.</summary>
public class WorkshopItemData
{
    [JsonPropertyName("PublisherID")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string PublisherId { get; set => field = value ?? ""; } = "";

    [JsonPropertyName("Title")]
    public string Title { get; set => field = value ?? ""; } = "";

    [JsonPropertyName("Description")]
    public string Description { get; set => field = value ?? ""; } = "";

    [JsonPropertyName("Thumbnail")]
    public string Thumbnail { get; set => field = value ?? ""; } = "";

    [JsonPropertyName("Type")]
    public string Type { get; set => field = value ?? ""; } = "";

    [JsonPropertyName("FolderName")]
    public string FolderName { get; set => field = value ?? ""; } = "";

    [JsonPropertyName("Tags")]
    public string Tags { get; set => field = value ?? ""; } = "";

    [JsonPropertyName("Visibility")]
    public string Visibility { get; set => field = value ?? ""; } = WorkshopItemVisibility.Private.ToString();

    [JsonPropertyName("Changelog")]
    public string Changelog { get; set => field = value ?? ""; } = "";

    [JsonPropertyName("PreviewImages")]
    public List<string> PreviewImages { get; set => field = value ?? []; } = [];

    public List<string> GetTagList() =>
        string.IsNullOrEmpty(Tags)
            ? []
            : [.. Tags.Split(',').Select(tag => tag.Trim()).Where(tag => tag.Length > 0)];

    public void SetTagList(List<string> tags) =>
        Tags = string.Join(",", tags);

    public WorkshopItemVisibility GetVisibility() =>
        Enum.TryParse<WorkshopItemVisibility>(Visibility, ignoreCase: true, out var visibility)
            ? visibility
            : WorkshopItemVisibility.Private;

    public void SetVisibility(WorkshopItemVisibility visibility) =>
        Visibility = visibility.ToString();

    public List<string> GetPreviewImageList() => CleanPaths(PreviewImages);

    public void SetPreviewImageList(IEnumerable<string> previewImages) =>
        PreviewImages = CleanPaths(previewImages);

    private static List<string> CleanPaths(IEnumerable<string?> paths) =>
        [.. paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    public WorkshopItemData Clone()
    {
        var clone = new WorkshopItemData
        {
            PublisherId = PublisherId,
            Title = Title,
            Description = Description,
            Thumbnail = Thumbnail,
            Type = Type,
            FolderName = FolderName,
            Tags = Tags,
            Visibility = Visibility,
            Changelog = Changelog,
        };
        clone.SetPreviewImageList(PreviewImages);
        return clone;
    }
}

/// <summary>Reads a string, a number (as its digits) or null (as ""); always writes a string.</summary>
public sealed class LenientStringConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? "",
            JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(
                reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan),
            JsonTokenType.Null => "",
            _ => throw new JsonException($"Expected a string or number, not {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
