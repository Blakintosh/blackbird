using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Blackbird.Models;

namespace Blackbird.Services;

public class BuildService : IBuildService
{
    private const int OutputFlushIntervalMs = 150;
    private const int OutputDrainTimeoutMs = 1500;

    public async Task<bool> RunBuildAsync(
        List<BuildCommand> commands,
        bool ignoreErrors,
        IProgress<string> progress,
        CancellationToken ct)
    {
        bool allSucceeded = true;

        foreach (var command in commands)
        {
            ct.ThrowIfCancellationRequested();

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

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) => exited.TrySetResult();
            var outputBuffer = new StringBuilder();
            var outputLock = new object();
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
                    AppendOutputLine(outputBuffer, outputLock, e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    stderrClosed.TrySetResult();
                else
                    AppendOutputLine(outputBuffer, outputLock, e.Data);
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

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
                outputFlushCts.Cancel();
                try { await outputFlushTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                FlushOutput(outputBuffer, outputLock, progress);
            }

            ct.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                allSucceeded = false;
                if (!ignoreErrors)
                    return false;
            }
        }

        ct.ThrowIfCancellationRequested();
        return allSucceeded;
    }

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

    private static void AppendOutputLine(StringBuilder outputBuffer, object outputLock, string line)
    {
        lock (outputLock)
        {
            outputBuffer.AppendLine(line);
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
