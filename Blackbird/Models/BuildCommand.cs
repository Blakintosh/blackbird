using System.Collections.Generic;

namespace Blackbird.Models;

public class BuildCommand
{
    public string Executable { get; }
    public List<string> Arguments { get; }
    public string Description { get; }

    /// <summary>Starts the game rather than building: launched on its own, never awaited as a build step.</summary>
    public bool LaunchesGame { get; init; }

    /// <summary>For <c>gdtdb /update</c>: skips the step when no GDT, deffile or database changed since the last clean update.</summary>
    public Services.GdtDbGate? GdtDbGate { get; init; }

    /// <summary>For a map link: removes the linker's one-second Umbra tome poll (see <see cref="Services.LinkerUmbraPatch"/>).</summary>
    public bool PatchUmbraWait { get; init; }

    /// <summary>
    /// For a link: skips it, showing the last output, when nothing any of its zones depends on changed since
    /// then. One stamp per zone the step writes (bb-linker links the main and localized zones in one process).
    /// </summary>
    public IReadOnlyList<Services.LinkStamp> LinkStamps { get; init; } = [];

    /// <summary>Runs instead when this step exits with one of bb-linker's "use the stock linker" codes.</summary>
    public BuildCommand? Fallback { get; init; }

    /// <summary>The step is bb-linker: it is given <c>-progress &lt;pipe&gt;</c> and its stage and percent are reported.</summary>
    public bool ReportsProgress { get; init; }

    /// <summary>Extra environment variables for the step's process.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public BuildCommand(string executable, List<string> arguments, string? description = null)
    {
        Executable = executable;
        Arguments = arguments;
        Description = description ?? System.IO.Path.GetFileName(executable);
    }

    public string ToCommandLine() => FormatCommand(Executable, Arguments);

    public static string FormatCommand(string executable, IEnumerable<string> arguments)
    {
        var parts = new List<string> { QuoteCommandPart(executable) };
        foreach (var argument in arguments)
            parts.Add(QuoteCommandPart(argument));

        return string.Join(" ", parts);
    }

    public override string ToString() => ToCommandLine();

    private static string QuoteCommandPart(string value)
    {
        if (value.Length == 0)
            return "\"\"";

        var escaped = value.Replace("\"", "\\\"", System.StringComparison.Ordinal);
        return NeedsQuotes(escaped) ? $"\"{escaped}\"" : escaped;
    }

    private static bool NeedsQuotes(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
                return true;
        }

        return false;
    }
}
