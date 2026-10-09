namespace Blackbird.Models;

public class WorkshopRemotePreview
{
    /// <summary>The preview's index on Steam, which updates and removals address it by.</summary>
    public uint Index { get; set; }
    public string Url { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string PreviewType { get; set; } = "";

    public bool IsImage =>
        string.Equals(PreviewType, "Image", System.StringComparison.OrdinalIgnoreCase)
        || string.Equals(PreviewType, "k_EItemPreviewType_Image", System.StringComparison.OrdinalIgnoreCase);
}
