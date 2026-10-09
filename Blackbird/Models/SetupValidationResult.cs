using System.Collections.Generic;
using System.Linq;

namespace Blackbird.Models;

public class SetupValidationResult
{
    public string GamePath { get; set; } = "";
    public string ToolsPath { get; set; } = "";
    public List<SetupCheck> Checks { get; set; } = [];

    public bool IsHealthy => Checks.Where(c => c.IsRequired).All(c => c.Passed);
    public int RequiredFailures => Checks.Count(c => c.IsRequired && !c.Passed);
}

