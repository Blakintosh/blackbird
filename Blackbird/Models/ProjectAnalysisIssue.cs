using System.IO;

namespace Blackbird.Models;

public class ProjectAnalysisIssue
{
    public string Status { get; set; } = "Info"; // OK | Warning | Blocker | Info
    public string Check { get; set; } = "";
    public string Detail { get; set; } = "";
    public string FilePath
    {
        get => _filePath;
        set { _filePath = value; _locationLabel = null; }
    }

    public int LineNumber
    {
        get => _lineNumber;
        set { _lineNumber = value; _locationLabel = null; }
    }

    private string _filePath = "";
    private int _lineNumber;
    private string? _locationLabel;

    public bool IsBlocker => Status == "Blocker";
    public bool IsWarning => Status == "Warning";
    public bool IsInfo => Status == "Info";
    public bool IsOk => Status == "OK";
    public bool HasLocation => !string.IsNullOrWhiteSpace(FilePath);
    /// <summary>"file.gdt:12"; built once, since the analysis search matches on it per keystroke.</summary>
    public string LocationLabel => _locationLabel ??= !HasLocation
        ? ""
        : LineNumber > 0 ? $"{Path.GetFileName(FilePath)}:{LineNumber}" : Path.GetFileName(FilePath);

    /// <summary>Where the finding is, relative to the project folder; set by the analysis window.</summary>
    public string DisplayLocation { get; set; } = "";

    /// <summary>The full path and line, for the location's tooltip.</summary>
    public string FullLocation => !HasLocation ? "" : LineNumber > 0 ? $"{FilePath}:{LineNumber}" : FilePath;

    public string StatusIcon => Status switch
    {
        "Blocker" => "\uE783",
        "Warning" => "\uE7BA",
        "OK" => "\uE73E",
        _ => "\uE946",
    };
}

