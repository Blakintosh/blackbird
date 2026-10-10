using System;
using System.IO;

namespace Blackbird.Services;

/// <summary>
/// bb-linker, the Rust linker. When <c>bb-linkerb-linker.exe</c> sits beside the launcher (or
/// <c>BLACKBIRD_BB_LINKER</c> names one) and Tools > Link with bb-linker is on, normal builds link with it and fall
/// back to the stock linker for anything it can't link yet.
/// </summary>
public static class BbLinker
{
    /// <summary>An input bb-linker can't convert yet; the stock linker should link instead.</summary>
    public const int ExitUnsupported = 0x0BB10001;

    /// <summary>bb-linker failed or crashed; the stock linker should link instead.</summary>
    public const int ExitInternal = 0x0BB10002;

    public static string? InstalledPath { get; } = FindInstalled();

    private static string? FindInstalled()
    {
        if (Environment.GetEnvironmentVariable("BLACKBIRD_BB_LINKER") is { Length: > 0 } path && File.Exists(path))
            return path;
        var beside = Path.Combine(AppContext.BaseDirectory, "bb-linker", "bb-linker.exe");
        return File.Exists(beside) ? beside : null;
    }

    public static bool MeansUseStock(int exitCode) => exitCode is ExitUnsupported or ExitInternal;
}
