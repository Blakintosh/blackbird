namespace Blackbird.Models;

/// <summary>The two launch configs, and the keys their dvars and build language are saved under.</summary>
public static class LaunchConfig
{
    public const string Dev = "Dev";
    public const string Ship = "Ship";

    /// <summary>The saved build language meaning "the launcher's default" rather than a language.</summary>
    public const string DefaultBuildLanguage = "Default";
}
