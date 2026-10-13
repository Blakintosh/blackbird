using System.Collections.Generic;

namespace Blackbird.Models;

/// <summary>
/// How a link step ended: which linker ran, whether it ran because Dragon handed over, and its exit code.
/// <see cref="Ran"/> is false when the step was refused before any linker started (a carry project with no linker for it).
/// </summary>
public readonly record struct LinkOutcome(string Linker, bool FellBack, int ExitCode, bool Ran = true);

public class BuildCommand
{
    public string Executable { get; }
    public List<string> Arguments { get; }
    public string Description { get; }

    /// <summary>Starts the game rather than building: launched on its own, never awaited as a build step.</summary>
    public bool LaunchesGame { get; init; }

    /// <summary>For <c>gdtdb /update</c>: skips the step when no GDT, deffile or database changed since the last clean update.</summary>
    public Services.GdtDbGate? GdtDbGate { get; init; }

    /// <summary>Runs instead when this step exits with one of Dragon's "use the stock linker" codes.</summary>
    public BuildCommand? Fallback { get; init; }

    /// <summary>
    /// The project's <c>.stockcarry.json</c>, when it has one: the project depends on Dragon's stock carry, so the
    /// stock linker must never link it (it would drop the carried assets), whatever Dragon exits with.
    /// </summary>
    public string? CarryMarker { get; init; }

    /// <summary>What the marker says the map needs Dragon for (stock assets, or only stock sounds), for the messages.</summary>
    public Services.DragonLinker.StockDependency? StockDependency { get; init; }

    /// <summary>For a link that would use the stock linker for a carry project: why it can't. The step fails with this instead of running.</summary>
    public string? CarryBlockedReason { get; init; }

    /// <summary>For a link: told how the step ended, for the components registry's last builds.</summary>
    public System.Action<LinkOutcome>? OnLinked { get; init; }

    /// <summary>
    /// For a stock link: the line naming the linker, shown before the step runs (the first stock link of a build only).
    /// A Dragon step's line comes from the linker itself (<see cref="Services.DragonLinker.HeaderAsync"/>).
    /// </summary>
    public string? Header { get; init; }

    /// <summary>The step is Dragon: it is given <c>-progress &lt;pipe&gt;</c> and its stage and percent are reported.</summary>
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
