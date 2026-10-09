using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackbird.Services;

namespace Blackbird.ViewModels;

public partial class Export2RustDialogViewModel : ObservableObject
{
    private readonly string _workingDirectory;
    private CancellationTokenSource? _runCts;
    private Process? _currentProcess;
    private List<Export2RustJob> _batch = [];

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isStopping;

    /// <summary>The progress bar, shown only once a run passes 150 ms.</summary>
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private Export2RustJob? _selectedJob;
    [ObservableProperty] private string _selectedOutput = "";

    public JobQueue Jobs { get; } = [];

    public string ExporterPath { get; }
    public bool ExporterExists => File.Exists(ExporterPath);

    public int ProgressValue => _batch.Count == 0
        ? 0
        : (int)Math.Round(_batch.Count(job => job.Status != Export2RustJobStatus.Running) * 100d / _batch.Count);

    // Why the last step outside a run failed (the file picker, say); cleared by the next change.
    private string? _problem;

    public void ReportProblem(string message)
    {
        _problem = message;
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>One line for the footer: what is queued, running, or how the last run went.</summary>
    public string StatusText
    {
        get
        {
            if (_problem is not null)
                return _problem;
            if (!ExporterExists)
                return $"export2rust.exe isn't in {Path.GetDirectoryName(ExporterPath)}.";
            if (IsStopping)
                return "Stopping…";
            if (IsRunning)
                return _batch.Count == 1 ? $"Exporting {_batch[0].FileName}…" : $"Exporting {_batch.Count:N0} files…";

            var parts = new List<string>();
            AddPart(parts, Count(Export2RustJobStatus.Complete), "exported");
            AddPart(parts, Count(Export2RustJobStatus.Failed), "failed");
            AddPart(parts, Count(Export2RustJobStatus.Skipped), "skipped");
            AddPart(parts, Count(Export2RustJobStatus.Canceled), "stopped");
            var queued = Count(Export2RustJobStatus.Queued);
            if (queued > 0)
                parts.Insert(0, $"{queued:N0} file{(queued == 1 ? "" : "s")} queued");

            return parts.Count == 0 ? "" : string.Join(", ", parts);
        }
    }

    public bool HasError => !ExporterExists;
    public bool HasJobs => Jobs.Count > 0;
    public bool CanRun => !IsRunning && ExporterExists && Jobs.Any(job => job.Status != Export2RustJobStatus.Complete);
    public bool CanClear => !IsRunning && HasJobs;
    public bool CanRemoveSelectedJob => !IsRunning && SelectedJob is not null;

    /// <summary>Why Export is disabled, for its tooltip; null when it can run.</summary>
    public string? RunDisabledReason =>
        !ExporterExists ? "export2rust.exe wasn't found"
        : !HasJobs ? "Add files to export"
        : !CanRun && !IsRunning ? "Every file is already exported"
        : null;

    public Export2RustDialogViewModel(IFileSystemService fileSystem)
    {
        ExporterPath = Path.Combine(fileSystem.ToolsPath, "bin", "export2rust.exe");
        _workingDirectory = Path.GetDirectoryName(ExporterPath) ?? fileSystem.ToolsPath;
        Jobs.CollectionChanged += OnJobsCollectionChanged;
    }

    /// <summary>
    /// Queues the files that exist and aren't queued yet. The disk is checked off the UI thread,
    /// and the queue changes in one notification however many files were dropped.
    /// </summary>
    public async Task<int> AddFilesAsync(IEnumerable<string> paths)
    {
        _problem = null;
        if (IsRunning)
            return 0;

        var requested = paths.ToList();
        var found = await Task.Run(() => requested
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .Select(path => new Export2RustJob(path))
            .ToList());

        if (IsRunning)
            return 0;

        var existing = new HashSet<string>(Jobs.Select(job => job.FilePath), StringComparer.OrdinalIgnoreCase);
        var added = found.Where(job => existing.Add(job.FilePath)).ToList();
        Jobs.AddRange(added);

        if (added.Count > 0 && SelectedJob is null)
            SelectedJob = Jobs[0];

        return added.Count;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (!CanRun)
            return;

        _batch = Jobs.Where(job => job.Status != Export2RustJobStatus.Complete).ToList();
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        IsRunning = true;
        using var indicator = DispatcherTimer.RunOnce(() => ShowProgress = IsRunning, TimeSpan.FromMilliseconds(150));

        try
        {
            await RunBatchAsync(_batch, token);
        }
        finally
        {
            _currentProcess = null;
            _runCts.Dispose();
            _runCts = null;
            IsStopping = false;
            IsRunning = false;
            ShowProgress = false;
        }
    }

    /// <summary>Stops a run: the Stop button, and closing the window. Unfinished files stay queued as stopped.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (!IsRunning || IsStopping)
            return;

        IsStopping = true;
        _runCts?.Cancel();
        TryKill(_currentProcess);
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsRunning)
            return;

        foreach (var job in Jobs)
            job.PropertyChanged -= OnJobPropertyChanged;

        // One notification rather than a Remove per job.
        Jobs.Clear();
        _batch = [];
        SelectedJob = null;
        RefreshState();
    }

    [RelayCommand]
    private void RemoveSelectedJob()
    {
        if (IsRunning || SelectedJob is null)
            return;

        var index = Jobs.IndexOf(SelectedJob);
        Jobs.Remove(SelectedJob);
        _batch.Remove(SelectedJob!);

        SelectedJob = Jobs.Count == 0
            ? null
            : Jobs[Math.Clamp(index, 0, Jobs.Count - 1)];
    }

    partial void OnIsRunningChanged(bool value) => RefreshState();

    partial void OnIsStoppingChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    partial void OnSelectedJobChanged(Export2RustJob? value)
    {
        SelectedOutput = value?.Output ?? "";
        OnPropertyChanged(nameof(CanRemoveSelectedJob));
    }

    private async Task RunBatchAsync(List<Export2RustJob> batch, CancellationToken token)
    {
        var validJobs = new List<Export2RustJob>();
        var exists = await Task.Run(() => batch.Select(job => File.Exists(job.FilePath)).ToList());
        for (var i = 0; i < batch.Count; i++)
        {
            var job = batch[i];
            job.ResetForRun();
            if (!exists[i])
            {
                job.Status = Export2RustJobStatus.Skipped;
                job.Detail = "File not found";
                job.Output = $"Skipped because the file no longer exists:{Environment.NewLine}{job.FilePath}";
                continue;
            }

            job.Status = Export2RustJobStatus.Running;
            validJobs.Add(job);
        }

        if (validJobs.Count == 0)
            return;

        SelectedJob = validJobs[0];

        // One export2rust run per chunk, so a very large drop never passes Windows'
        // 32K command-line limit.
        foreach (var chunk in ChunkByCommandLine(validJobs))
        {
            if (token.IsCancellationRequested)
            {
                MarkStopped(chunk, TimeSpan.Zero, "");
                continue;
            }

            await RunProcessAsync(chunk, token);
        }
    }

    private const int MaxCommandLineLength = 30_000;

    private IEnumerable<List<Export2RustJob>> ChunkByCommandLine(List<Export2RustJob> jobs)
    {
        var chunk = new List<Export2RustJob>();
        var length = ExporterPath.Length + 3;
        foreach (var job in jobs)
        {
            var argument = job.FilePath.Length + 3; // quotes and a space
            if (chunk.Count > 0 && length + argument > MaxCommandLineLength)
            {
                yield return chunk;
                chunk = [];
                length = ExporterPath.Length + 3;
            }

            chunk.Add(job);
            length += argument;
        }

        if (chunk.Count > 0)
            yield return chunk;
    }

    private async Task RunProcessAsync(List<Export2RustJob> jobs, CancellationToken token)
    {
        // Map normalized input paths -> jobs so streamed "input -> output" lines
        // can update each job in place as the bulk process makes progress.
        var pathToJob = new Dictionary<string, Export2RustJob>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
            pathToJob[NormalizePath(job.FilePath)] = job;

        var aggregate = new StringBuilder();
        var aggregateLock = new object();
        var started = DateTimeOffset.Now;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ExporterPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _workingDirectory
            },
            EnableRaisingEvents = true
        };

        foreach (var job in jobs)
            process.StartInfo.ArgumentList.Add(job.FilePath);

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) stdoutClosed.TrySetResult();
            else HandleBatchLine(e.Data, isError: false, pathToJob, aggregate, aggregateLock);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) stderrClosed.TrySetResult();
            else HandleBatchLine(e.Data, isError: true, pathToJob, aggregate, aggregateLock);
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("export2rust.exe didn't start.");

            _currentProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Not WaitForExitAsync: with redirected output it also waits for the pipes to close,
            // which a child holding them open would stretch out. Wait for the exit, then give the
            // pipes a moment to drain, as BuildService does.
            await exited.Task.WaitAsync(token);
            var drained = Task.WhenAll(stdoutClosed.Task, stderrClosed.Task);
            if (await Task.WhenAny(drained, Task.Delay(OutputDrainTimeout)) != drained)
            {
                try { process.CancelOutputRead(); } catch (InvalidOperationException) { }
                try { process.CancelErrorRead(); } catch (InvalidOperationException) { }
            }

            // Streamed lines are posted to the dispatcher; let them land before
            // deciding which jobs never reported.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var duration = DateTimeOffset.Now - started;
            var perJobDuration = TimeSpan.FromTicks(duration.Ticks / Math.Max(1, jobs.Count));
            var aggregateText = SnapshotAggregate(aggregate, aggregateLock);

            foreach (var job in jobs)
            {
                job.ExitCode = process.ExitCode;
                job.Duration = perJobDuration;

                if (job.Status == Export2RustJobStatus.Running)
                {
                    if (process.ExitCode == 0)
                    {
                        job.Status = Export2RustJobStatus.Complete;
                    }
                    else
                    {
                        job.Status = Export2RustJobStatus.Failed;
                        job.Detail = $"Exit code {process.ExitCode}";
                    }
                }

                if (string.IsNullOrEmpty(job.Output))
                    job.Output = aggregateText;
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            MarkStopped(jobs, DateTimeOffset.Now - started, SnapshotAggregate(aggregate, aggregateLock));
        }
        catch (Exception ex)
        {
            var duration = DateTimeOffset.Now - started;
            foreach (var job in jobs)
            {
                if (job.Status == Export2RustJobStatus.Running)
                {
                    job.Duration = duration;
                    job.Status = Export2RustJobStatus.Failed;
                    job.Detail = "Couldn't start export2rust";
                    if (string.IsNullOrEmpty(job.Output))
                        job.Output = ex.Message;
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_currentProcess, process))
                _currentProcess = null;
        }
    }

    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>Jobs still running go back to the queue as stopped; nothing is lost.</summary>
    private static void MarkStopped(List<Export2RustJob> jobs, TimeSpan duration, string output)
    {
        foreach (var job in jobs)
        {
            if (job.Status != Export2RustJobStatus.Running)
                continue;

            job.Duration = duration;
            job.Status = Export2RustJobStatus.Canceled;
            job.Output = string.IsNullOrEmpty(output) ? "Stopped before export2rust reported this file." : output;
        }
    }

    private static void HandleBatchLine(
        string? line,
        bool isError,
        Dictionary<string, Export2RustJob> pathToJob,
        StringBuilder aggregate,
        object aggregateLock)
    {
        if (line is null)
            return;

        lock (aggregateLock)
        {
            if (isError) aggregate.Append("[stderr] ");
            aggregate.AppendLine(line);
        }

        if (isError) return;

        // export2rust prints "<input> -> <output>" per converted file.
        var arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
        if (arrow <= 0) return;

        var inputRaw = line[..arrow].Trim();
        string normalized;
        try { normalized = NormalizePath(inputRaw); }
        catch { return; }

        if (pathToJob.TryGetValue(normalized, out var job))
        {
            // Output handlers run on thread-pool threads; job properties are bound
            // to the UI, so their updates must happen on the dispatcher.
            Dispatcher.UIThread.Post(() =>
            {
                if (job.Status != Export2RustJobStatus.Running)
                    return;
                job.Output = line;
                job.Status = Export2RustJobStatus.Complete;
            });
        }
    }

    private static string SnapshotAggregate(StringBuilder builder, object syncRoot)
    {
        lock (syncRoot)
            return builder.ToString().TrimEnd();
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar);

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Best effort: the process may have exited between the check and the kill.
        }
    }

    private int Count(Export2RustJobStatus status) => Jobs.Count(job => job.Status == status);

    private static void AddPart(List<string> parts, int count, string word)
    {
        if (count > 0)
            parts.Add($"{count:N0} {word}");
    }

    private void OnJobsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (Export2RustJob job in e.OldItems)
                job.PropertyChanged -= OnJobPropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (Export2RustJob job in e.NewItems)
                job.PropertyChanged += OnJobPropertyChanged;
        }

        RefreshState();
    }

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is Export2RustJob job
            && ReferenceEquals(job, SelectedJob)
            && e.PropertyName == nameof(Export2RustJob.Output))
        {
            SelectedOutput = job.Output;
        }

        if (e.PropertyName == nameof(Export2RustJob.Status))
            RefreshState();
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(CanRemoveSelectedJob));
        OnPropertyChanged(nameof(RunDisabledReason));
    }
}

/// <summary>The export queue: adding a drop of files is one notification, not one per file.</summary>
public sealed class JobQueue : ObservableCollection<Export2RustJob>
{
    public void AddRange(IReadOnlyList<Export2RustJob> jobs)
    {
        if (jobs.Count == 0)
            return;

        CheckReentrancy();
        var start = Count;
        foreach (var job in jobs)
            Items.Add(job);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, jobs as System.Collections.IList ?? jobs.ToList(), start));
    }
}

public partial class Export2RustJob : ObservableObject
{
    [ObservableProperty] private Export2RustJobStatus _status = Export2RustJobStatus.Queued;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private int? _exitCode;
    [ObservableProperty] private TimeSpan? _duration;
    [ObservableProperty] private string _output = "";

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public string FolderName => Path.GetDirectoryName(FilePath) ?? "";
    public string SizeText { get; }
    public string DurationText => Duration is null ? "" : $"{Duration.Value.TotalSeconds:N1} s";
    public string StatusText => Status switch
    {
        Export2RustJobStatus.Queued => "Queued",
        Export2RustJobStatus.Running => "Exporting…",
        Export2RustJobStatus.Complete => "Exported",
        Export2RustJobStatus.Failed => "Failed",
        Export2RustJobStatus.Skipped => "Skipped",
        Export2RustJobStatus.Canceled => "Stopped",
        _ => Status.ToString()
    };

    /// <summary>The queue row's status glyph; its colour is <see cref="StatusBrush"/>.</summary>
    public string StatusGlyph => Status switch
    {
        Export2RustJobStatus.Running => "\uE895",
        Export2RustJobStatus.Complete => "\uE930",
        Export2RustJobStatus.Failed => "\uEA39",
        Export2RustJobStatus.Skipped => "\uE7BA",
        Export2RustJobStatus.Canceled => "\uE71A",
        _ => "\uE823",
    };

    /// <summary>The row's dim second line: what went wrong when something did, otherwise the folder.</summary>
    public string SecondLine => Detail.Length > 0 ? $"{StatusText} \u00B7 {Detail}" : FolderName;

    public IBrush? StatusBrush => ThemeBrush(Status switch
    {
        Export2RustJobStatus.Running => "AccentIndicator",
        Export2RustJobStatus.Complete => "SuccessGreen",
        Export2RustJobStatus.Failed => "ErrorRed",
        Export2RustJobStatus.Skipped => "WarningYellow",
        _ => "ForegroundTertiary"
    });

    public Export2RustJob(string filePath)
    {
        FilePath = filePath;
        SizeText = GetSizeText(filePath);
    }

    public void ResetForRun()
    {
        ExitCode = null;
        Duration = null;
        Output = "";
        Detail = "";
    }

    partial void OnStatusChanged(Export2RustJobStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(SecondLine));
    }

    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(SecondLine));

    partial void OnDurationChanged(TimeSpan? value) =>
        OnPropertyChanged(nameof(DurationText));

    private static IBrush? ThemeBrush(string key) =>
        Application.Current?.FindResource(key) as IBrush;

    private static string GetSizeText(string filePath)
    {
        try
        {
            var length = new FileInfo(filePath).Length;
            if (length < 1024)
                return $"{length:N0} B";
            if (length < 1024 * 1024)
                return $"{length / 1024d:N1} KB";
            return $"{length / (1024d * 1024d):N1} MB";
        }
        catch (Exception)
        {
            return "";
        }
    }
}

public enum Export2RustJobStatus
{
    Queued,
    Running,
    Complete,
    Failed,
    Skipped,
    Canceled
}
