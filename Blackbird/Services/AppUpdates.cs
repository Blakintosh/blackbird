using System.IO;
using System.Reflection;
using Gscode.Updates;

namespace Blackbird.Services;

/// <summary>What the shared updater (<see cref="Updater"/>) needs to know about Blackbird.</summary>
public static class AppUpdates
{
    /// <summary>The version in the csproj, without the build's commit hash.</summary>
    public static string CurrentVersion { get; } =
        typeof(AppUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppUpdates).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static UpdaterOptions Options { get; } = new()
    {
        ProductId = "blackbird",
        DisplayName = "Blackbird",
        Repository = "Blakintosh/blackbird",
        CurrentVersion = CurrentVersion,
        ExeBundlePath = "bin/modlauncher.exe",
        BundleFiles = ["bin/modlauncher.exe", "bin/steam/steam_api64.dll"],
        LogFile = Path.Combine(AppPaths.DataFolder, "updates.log"),
    };
}
