using System;

namespace Blackbird.Models;

/// <summary>
/// BO3's ^N colour codes in build output and names: ^0–^9, where the Mod Tools print errors in
/// ^1 (red) and warnings in ^3 (yellow). The one place that decides what a line is and how a
/// code is removed, so the log view, the counts, exports and display names never disagree.
/// </summary>
public static class BuildLogCodes
{
    public static bool IsColorCode(char c) => c is >= '0' and <= '9';

    public static bool IsError(ReadOnlySpan<char> line) => line.Contains("^1", StringComparison.Ordinal);

    /// <summary>A ^3 line that isn't also an error: a line counts once, as its worst.</summary>
    public static bool IsWarning(ReadOnlySpan<char> line) =>
        !IsError(line) && line.Contains("^3", StringComparison.Ordinal);

    /// <summary><paramref name="raw"/> without its colour codes.</summary>
    public static string Strip(string raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.Contains('^'))
            return raw ?? "";

        var builder = new System.Text.StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '^' && i + 1 < raw.Length && IsColorCode(raw[i + 1]))
            {
                i++;
                continue;
            }

            builder.Append(raw[i]);
        }

        return builder.ToString();
    }
}
