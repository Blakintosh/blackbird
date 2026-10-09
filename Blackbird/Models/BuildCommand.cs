using System.Collections.Generic;

namespace Blackbird.Models;

public class BuildCommand
{
    public string Executable { get; }
    public List<string> Arguments { get; }
    public string Description { get; }

    /// <summary>Starts the game rather than building: launched on its own, never awaited as a build step.</summary>
    public bool LaunchesGame { get; init; }

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
