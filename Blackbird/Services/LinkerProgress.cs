using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Blackbird.Services;

/// <summary>One thing bb-linker's <c>-progress</c> stream (docs/progress.md in bb-linker) or the build around it reports.</summary>
public abstract record LinkerEvent;

/// <summary>The linker step is over (it exited, or was stopped): whatever it last reported no longer holds.</summary>
public sealed record LinkerStepEnded : LinkerEvent
{
    public static LinkerStepEnded Instance { get; } = new();
}

/// <summary>
/// Where a bb-linker run is: the whole run's estimated percent, the stage of the zone being linked, and how far
/// through the assets it is.
/// </summary>
public sealed record LinkerProgress(
    double Percent,
    string Label,
    string Zone,
    int ZoneIndex,
    int ZoneCount,
    long Done,
    long? Total,
    double? EtaSeconds) : LinkerEvent
{
    /// <summary>"41% · Converting assets 812 of 1,900 · zm_map (german) · 17s left"; the parts it has nothing for are left out.</summary>
    public string Describe()
    {
        var parts = new System.Collections.Generic.List<string> { $"{(int)Math.Floor(Percent)}%" };

        if (Label.Length > 0)
        {
            var step = Label;
            if (Done > 0)
                step += Total is { } total && Done <= total
                    ? string.Create(CultureInfo.CurrentCulture, $" {Done:N0} of {total:N0}")
                    : string.Create(CultureInfo.CurrentCulture, $" {Done:N0}");
            parts.Add(step);
        }

        if (ZoneCount > 1 && Zone.Length > 0)
            parts.Add(Zone);

        if (EtaSeconds is >= 3 and < 3600)
            parts.Add($"{FormatEta(EtaSeconds.Value)} left");

        return string.Join(" \u00b7 ", parts);
    }

    private static string FormatEta(double seconds)
    {
        var rounded = (int)Math.Round(seconds);
        return rounded < 60 ? $"{rounded}s" : $"{rounded / 60}:{rounded % 60:00}";
    }
}

/// <summary>Folds bb-linker's JSON-line events into <see cref="LinkerEvent"/>s.</summary>
internal sealed class LinkerProgressState
{
    private string _zone = "";
    private int _zoneIndex;
    private int _zoneCount = 1;
    private LinkerProgress? _last;

    /// <summary>The event this line amounts to, or null when it changed nothing worth showing (or isn't an event).</summary>
    public LinkerEvent? Apply(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("event", out var kind))
                return null;

            switch (kind.GetString())
            {
                case "run":
                    _zoneCount = Math.Max(1, Int(root, "zones") ?? 1);
                    return null;

                case "zone":
                    var name = Str(root, "zone");
                    var language = Str(root, "language");
                    var unit = Str(root, "unit");
                    _zone = language.Length > 0 ? $"{name} ({language})" : unit.Length > 0 ? $"{name} {unit}" : name;
                    _zoneIndex = Int(root, "index") ?? 0;
                    _zoneCount = Math.Max(1, Int(root, "count") ?? _zoneCount);
                    return null;

                case "progress":
                    return _last = new LinkerProgress(
                        Pct(root),
                        Str(root, "label"),
                        _zone,
                        _zoneIndex,
                        _zoneCount,
                        Long(root, "done") ?? 0,
                        Long(root, "total"),
                        Number(root, "eta"));

                case "end":
                    return _last = (_last ?? new LinkerProgress(0, "", _zone, _zoneIndex, _zoneCount, 0, null, null))
                        with { Percent = 100, Label = "", Done = 0, Total = null, EtaSeconds = null };

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null; // a partial or foreign line: progress is best-effort
        }
    }

    private static double Pct(JsonElement e) => Math.Clamp(Number(e, "pct") ?? 0, 0, 100);

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static int? Int(JsonElement e, string name) => Long(e, name) is { } n ? (int)Math.Clamp(n, 0, int.MaxValue) : null;
}

/// <summary>
/// The launcher's end of bb-linker's <c>-progress</c> pipe: creates the pipe before the linker starts (it connects as a
/// client and writes) and reports each event until the linker closes it. Reading never blocks the build;
/// a linker that never connects costs nothing but this object.
/// </summary>
internal sealed class LinkerProgressListener : IAsyncDisposable
{
    private static readonly TimeSpan DrainGrace = TimeSpan.FromMilliseconds(500);

    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;
    private bool _connected;

    /// <summary>The value for the linker's <c>-progress</c> argument.</summary>
    public string Target { get; }

    public LinkerProgressListener(Action<LinkerEvent> onProgress)
    {
        var name = $"blackbird-link-{Environment.ProcessId}-{Guid.NewGuid():N}";
        Target = $@"\\.\pipe\{name}";
        _pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _reader = Task.Run(() => ReadAsync(onProgress, _cts.Token));
    }

    private async Task ReadAsync(Action<LinkerEvent> onProgress, CancellationToken ct)
    {
        try
        {
            await _pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _connected, true);
            using var reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            var state = new LinkerProgressState();
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                if (state.Apply(line) is { } ev)
                    onProgress(ev);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Cancelled, or the linker went away mid-line.
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The linker has exited by now, so what it wrote is already in the pipe: give the reader a moment to take it.
        // A linker that never connected has nothing to drain.
        if (Volatile.Read(ref _connected))
            await Task.WhenAny(_reader, Task.Delay(DrainGrace)).ConfigureAwait(false);
        _cts.Cancel();
        await _reader.ConfigureAwait(false);
        _pipe.Dispose();
        _cts.Dispose();
    }
}
