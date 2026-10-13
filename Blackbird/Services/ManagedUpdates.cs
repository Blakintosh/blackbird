using System.Diagnostics;

namespace Blackbird.Services;

/// <summary>
/// The ecosystem rule for who updates what (gscode-installer docs/release-contract.md): a host that manages updates sets
/// <c>BO3MT_UPDATES_MANAGED_BY=&lt;host&gt;</c> in the environment of every process it starts, and products with an updater of
/// their own (Ultrasound's background update) defer to it. Blackbird updates Ultrasound itself, in Tools > Components, so every
/// build, link and convert it starts carries the variable; the linker passes it on to <c>snd_convert.exe</c>.
/// </summary>
public static class ManagedUpdates
{
    public const string Variable = "BO3MT_UPDATES_MANAGED_BY";
    public const string Host = "blackbird";

    public static void Apply(ProcessStartInfo startInfo) => startInfo.Environment[Variable] = Host;
}
