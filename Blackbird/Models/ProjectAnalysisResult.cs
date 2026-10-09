using System.Collections.Generic;
using System.Linq;

namespace Blackbird.Models;

public class ProjectAnalysisResult
{
    public string ProjectName { get; set; } = "";
    public string ProjectTypeLabel { get; set; } = "";
    public List<ProjectAnalysisIssue> Issues { get; set; } = [];

    public int BlockerCount => Issues.Count(issue => issue.IsBlocker);
    public int WarningCount => Issues.Count(issue => issue.IsWarning);
    public int InfoCount => Issues.Count(issue => issue.IsInfo);
    public bool IsHealthy => BlockerCount == 0 && WarningCount == 0;

    public string StatusText
    {
        get
        {
            if (Issues.Count == 0 || IsHealthy)
                return "No project issues found.";

            var parts = new List<string>();
            if (BlockerCount > 0)
                parts.Add($"{BlockerCount} blocker{(BlockerCount == 1 ? "" : "s")}");
            if (WarningCount > 0)
                parts.Add($"{WarningCount} warning{(WarningCount == 1 ? "" : "s")}");

            return string.Join(", ", parts) + " found.";
        }
    }
}

