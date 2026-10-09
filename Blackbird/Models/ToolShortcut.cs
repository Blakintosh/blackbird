namespace Blackbird.Models;

public class ToolShortcut
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string? IconCachePath { get; set; }
}
