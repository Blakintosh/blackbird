using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Blackbird.Models;

namespace Blackbird.Services;

public partial class BuildService : IBuildService
{
    // The linker ends a run with "done: 0m46.40s" even when it reported asset errors, yet exits non-zero for them.
    [GeneratedRegex(@"^done:\s*\d+m\d+(\.\d+)?s\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LinkerDoneLine();

    private const int OutputFlushIntervalMs = 150;
    private const int OutputDrainTimeoutMs = 1500;

    public async Task<bool> RunBuildAsync(
        List<BuildCommand> commands,
        bool ignoreErrors,
        IProgress<string> progress,
        CancellationToken ct,
        IProgress<LinkerEvent>? linkerProgress = null)
    {
        // Link stamps are written while the next step runs; the build isn't over until they're on disk.
        var stampWrites = new List<Task>();
        try
        {
            return await RunCommandsAsync(commands, ignoreErrors, progress, stampWrites, ct, linkerProgress).ConfigureAwait(false);
        }
        finally
        {
            try { await Task.WhenAll(stampWrites).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task<bool> RunCommandsAsync(
        List<BuildCommand> commands,
        bool ignoreErrors,
        IProgress<string> progress,
        List<Task> stampWrites,
        CancellationToken ct,
        IProgress<LinkerEvent>? linkerProgress)
    {
        // A project that depends on stock carry is never linked by the stock linker: it would drop the carried assets.
        // Checked before anything runs, so no compile is wasted on a build that can't link.
        foreach (var step in commands)
        {
            var blocked = step.CarryBlockedReason;
            if (blocked is null && step.CarryMarker is not null && step.Fallback is not null)
            {
                var info = await BbLinker.ProbeAsync(step.Executable, step.Environment).ConfigureAwait(false);
                blocked = BbLinker.CarryBlockedReason(true, true, true,
                    info?.Has(step.StockDependency?.Capability ?? BbLinker.StockCarryCapability) == true, step.StockDependency);
            }
            if (blocked is null)
                continue;
            progress.Report($"\n[launcher] {step.Description}: {blocked}\n");
            step.OnLinked?.Invoke(new LinkOutcome(LinkerName(step), false, BbLinker.ExitStockCarry, Ran: false));
            return false;
        }

        bool allSucceeded = true;
        var queue = new List<BuildCommand>(commands);

        for (var index = 0; index < queue.Count; index++)
        {
            var command = queue[index];
            ct.ThrowIfCancellationRequested();

            if (command.GdtDbGate is { } gate && await Task.Run(() => gate.IsUpToDate(ct), ct).ConfigureAwait(false))
            {
                progress.Report("\n[launcher] No GDT changes since the last gdtdb update, so it was skipped.\n");
                continue;
            }

            // Every log says which linker linked: a coloured Dragon line from bb-linker itself, or the stock linker's plain one.
            var header = command.ReportsProgress
                ? await BbLinker.HeaderAsync(command.Executable, command.Environment).ConfigureAwait(false)
                : command.Header;
            if (header is not null)
                progress.Report($"\n{header}\n");

            if (command.LinkStamps.Count > 0)
            {
                // Skipped only when every zone the step writes is current; the first stamp holds the output.
                var stamps = command.LinkStamps;
                var last = await Task.Run(() => stamps[0].TryGetUpToDate(ct), ct).ConfigureAwait(false);
                for (var s = 1; last is not null && s < stamps.Count; s++)
                {
                    var stamp = stamps[s];
                    if (await Task.Run(() => stamp.TryGetUpToDate(ct), ct).ConfigureAwait(false) is null)
                        last = null;
                }
                if (last is { } up)
                {
                    progress.Report($"\n[launcher] Nothing this link reads has changed since it last ran, so it was skipped. Its last output:\n\n{up.Output}");
                    if (up.ExitCode != 0)
                    {
                        allSucceeded = false;
                        if (!ignoreErrors)
                            return false;
                    }
                    continue;
                }
                foreach (var stamp in stamps)
                    await Task.Run(stamp.BeginLink, ct).ConfigureAwait(false);
            }

            progress.Report($"\n{command}\n");

            var psi = new ProcessStartInfo
            {
                FileName = command.Executable,
                WorkingDirectory = Path.GetDirectoryName(command.Executable) ?? "",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            foreach (var arg in command.Arguments)
                psi.ArgumentList.Add(arg);
            if (command.Environment is { } environment)
                foreach (var (name, value) in environment)
                    psi.Environment[name] = value;

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) => exited.TrySetResult();
            var outputBuffer = new StringBuilder();
            var outputLock = new object();
            var tail = new LineTail();
            // The whole output of a stamped link is kept, to show again when the link is skipped next time.
            var captured = command.LinkStamps.Count == 0 ? null : new StringBuilder();
            using var outputFlushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var outputFlushTask = FlushOutputPeriodicallyAsync(
                outputBuffer,
                outputLock,
                progress,
                outputFlushCts.Token);
            var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    stdoutClosed.TrySetResult();
                else
                    AppendOutputLine(outputBuffer, outputLock, e.Data, tail, captured);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    stderrClosed.TrySetResult();
                else
                    AppendOutputLine(outputBuffer, outputLock, e.Data, tail, captured);
            };

            LinkerProgressListener? progressListener = null;
            try
            {
                if (command.ReportsProgress && linkerProgress is not null
                    && await BbLinker.SupportsAsync(command.Executable, "-progress", command.Environment).ConfigureAwait(false))
                {
                    try
                    {
                        // Anything but a progress tick is a boundary: the log up to it is shown before it is.
                        progressListener = new LinkerProgressListener(ev =>
                        {
                            if (ev is not LinkerProgress)
                                FlushOutput(outputBuffer, outputLock, progress);
                            linkerProgress.Report(ev);
                        });
                        psi.ArgumentList.Add("-progress");
                        psi.ArgumentList.Add(progressListener.Target);
                    }
                    catch (IOException)
                    {
                        // No pipe, no progress; the build itself doesn't need it.
                    }
                }

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (command.PatchUmbraWait)
                    _ = LinkerUmbraPatch.ApplyAsync(process, ct);

                using var registration = ct.Register(() =>
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is not InvalidOperationException) // already exited: nothing to stop
                    {
                        AppendOutputLine(outputBuffer, outputLock,
                            $"[launcher] Couldn't stop {Path.GetFileName(command.Executable)}: {ex.Message}");
                    }
                });

                // Not WaitForExitAsync: with async-redirected output it also waits for the pipes to close,
                // so a child that inherits them (snd_convert, say) would hang the build past the drain timeout.
                await exited.Task.WaitAsync(ct).ConfigureAwait(false);
                await DrainRedirectedOutputAsync(process, stdoutClosed.Task, stderrClosed.Task, progress)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (progressListener is not null)
                {
                    await progressListener.DisposeAsync().ConfigureAwait(false);
                    linkerProgress?.Report(LinkerStepEnded.Instance);
                }

                outputFlushCts.Cancel();
                try { await outputFlushTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                FlushOutput(outputBuffer, outputLock, progress);
            }

            ct.ThrowIfCancellationRequested();

            // A link that ran to the end ("done:") can vouch for its outputs; one that crashed or was stopped can't.
            // A step that fell back to the stock linker leaves its stamps unwritten (the fallback writes its own).
            // Only a bb-linker step has a say in falling back; it is told whether its project depends on stock carry.
            var next = command.Fallback is null
                ? BbLinker.Next.Accept
                : BbLinker.AfterStep(process.ExitCode, command.CarryMarker is not null);
            if (captured is not null && next == BbLinker.Next.Accept)
            {
                string output;
                lock (outputLock)
                    output = captured.ToString();
                var exitCode = process.ExitCode;
                if (output.Contains("done: ", StringComparison.Ordinal))
                    for (var s = 0; s < command.LinkStamps.Count; s++)
                    {
                        var ranStamp = command.LinkStamps[s];
                        var kept = s == 0 ? output : "";
                        stampWrites.Add(Task.Run(() => ranStamp.RecordLink(exitCode, kept, ct), ct));
                    }
            }

            // gdtdb exits 1 when it reported errors (duplicate assets, say); those builds keep running it.
            if (command.GdtDbGate is { } ranGate)
            {
                if (process.ExitCode == 0)
                    ranGate.RecordCleanUpdate();
                else
                    ranGate.Forget();
            }

            if (next == BbLinker.Next.UseStockLinker && command.Fallback is { } fallback)
            {
                progress.Report($"\n[launcher] bb-linker can't link this yet (exit 0x{process.ExitCode:X8}), so the stock linker is linking it.\n");
                queue.Insert(index + 1, fallback);
                continue;
            }

            if (next == BbLinker.Next.FailStockCarry)
            {
                progress.Report($"\n[launcher] {BbLinker.CarryFailureMessage(process.ExitCode, command.StockDependency)}\n");
                command.OnLinked?.Invoke(new LinkOutcome(LinkerName(command), false, process.ExitCode));
                allSucceeded = false;
                if (!ignoreErrors)
                    return false;
                continue;
            }

            command.OnLinked?.Invoke(new LinkOutcome(LinkerName(command), false, process.ExitCode));

            // Non-zero with the linker's closing "done:" line means it ran to the end and only reported errors;
            // those are counted from the log, so the build lands as "completed with errors", not "failed".
            if (process.ExitCode != 0 && !ReachedDone(tail, outputLock))
            {
                allSucceeded = false;
                if (!ignoreErrors)
                    return false;
            }
        }

        ct.ThrowIfCancellationRequested();
        return allSucceeded;
    }

    private static string LinkerName(BuildCommand command) => command.ReportsProgress ? "bb-linker" : "stock";

    private static async Task FlushOutputPeriodicallyAsync(
        StringBuilder outputBuffer,
        object outputLock,
        IProgress<string> progress,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(OutputFlushIntervalMs, ct).ConfigureAwait(false);
            FlushOutput(outputBuffer, outputLock, progress);
        }
    }

    private sealed class LineTail
    {
        public string? Value;
    }

    private static void AppendOutputLine(StringBuilder outputBuffer, object outputLock, string line, LineTail? tail = null, StringBuilder? captured = null)
    {
        lock (outputLock)
        {
            outputBuffer.AppendLine(line);
            if (tail is not null && !string.IsNullOrWhiteSpace(line))
                tail.Value = line;
        }
    }

    private static bool ReachedDone(LineTail tail, object outputLock)
    {
        lock (outputLock)
        {
            return tail.Value is { } last && LinkerDoneLine().IsMatch(last.Trim());
        }
    }

    private static void FlushOutput(StringBuilder outputBuffer, object outputLock, IProgress<string> progress)
    {
        string? chunk = null;
        lock (outputLock)
        {
            if (outputBuffer.Length > 0)
            {
                chunk = outputBuffer.ToString();
                outputBuffer.Clear();
            }
        }

        if (chunk is not null)
            progress.Report(chunk);
    }

    private static async Task DrainRedirectedOutputAsync(
        Process process,
        Task stdoutClosed,
        Task stderrClosed,
        IProgress<string> progress)
    {
        var streamsClosed = Task.WhenAll(stdoutClosed, stderrClosed);
        var completed = await Task.WhenAny(streamsClosed, Task.Delay(OutputDrainTimeoutMs))
            .ConfigureAwait(false);

        if (completed == streamsClosed)
            return;

        progress.Report(
            $"\n[launcher] Output pipe didn't close within {OutputDrainTimeoutMs:N0}ms after {Path.GetFileName(process.StartInfo.FileName)} exited. Continuing; a child codec/converter may have leaked a console handle.\n");

        try { process.CancelOutputRead(); } catch { }
        try { process.CancelErrorRead(); } catch { }
    }
}
