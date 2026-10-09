using System.Collections.Generic;

namespace Blackbird.Models;

public class WorkshopRemoteItemData
{
    public WorkshopItemData Metadata { get; set; } = new();
    public string ThumbnailUrl { get; set; } = "";
    public List<WorkshopRemotePreview> AdditionalPreviews { get; set; } = [];
}
