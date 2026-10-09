namespace Blackbird.Models;

/// <summary>
/// One row in the publish preflight banner.
/// Status drives color/severity; RelatedSection is used by inline section indicators
/// (e.g., a "thumbnail"-tagged check shows a status icon next to the Thumbnail header).
/// </summary>
public class PublishPreflightCheck
{
    public string Status { get; set; } = "OK"; // OK | Warning | Blocker | Info
    public string Check { get; set; } = "";
    public string Detail { get; set; } = "";

    /// <summary>The technical detail behind <see cref="Detail"/> (a path, Steam's own words), shown on hover.</summary>
    public string? DetailToolTip { get; set; }
    public string? RelatedSection { get; set; }
    public PublishCheckAction? PrimaryAction { get; set; }
    public PublishCheckAction? SecondaryAction { get; set; }

    public bool IsBlocker => Status == "Blocker";
    public bool IsWarning => Status == "Warning";
    public bool IsInfo => Status == "Info";
    public bool IsOk => Status == "OK";

    /// <summary>Segoe MDL2 codepoint for the row's leading icon.</summary>
    public string StatusIcon => Status switch
    {
        "Blocker" => "\uE783",
        "Warning" => "\uE7BA",
        "Info" => "\uE946",
        _ => "\uE73E"
    };
}

/// <summary>
/// A button on a preflight row. Kind is dispatched by the host
/// (MainWindow), Payload carries any context (e.g., a folder path).
/// </summary>
public class PublishCheckAction
{
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "";
}
