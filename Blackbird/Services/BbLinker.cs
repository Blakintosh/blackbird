using System;
using System.IO;

namespace Blackbird.Services;

/// <summary>
/// Test hook for bb-linker, the Rust linker: set <c>BLACKBIRD_BB_LINKER</c> to its <c>bb-linker.exe</c> and normal
/// builds link with it, falling back to the stock linker for anything it can't link yet. Not a setting: it exists
/// to try bb-linker on real projects before it becomes the linker.
/// </summary>
public static class BbLinker
{
    /// <summary>An input bb-linker can't convert yet; the stock linker should link instead.</summary>
    public const int ExitUnsupported = 0x0BB10001;

    /// <summary>bb-linker failed or crashed; the stock linker should link instead.</summary>
    public const int ExitInternal = 0x0BB10002;

    public static string? ExecutablePath =>
        Environment.GetEnvironmentVariable("BLACKBIRD_BB_LINKER") is { Length: > 0 } path && File.Exists(path)
            ? path
            : null;

    public static bool MeansUseStock(int exitCode) => exitCode is ExitUnsupported or ExitInternal;
}
