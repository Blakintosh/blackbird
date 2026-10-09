using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Blackbird.Services;

/// <summary>
/// Sends files and folders to the Recycle Bin through the shell, the way Explorer does.
/// When something is too big for the Recycle Bin, Windows asks before deleting it for good;
/// if the user says no, nothing is deleted and the call reports it as cancelled.
/// </summary>
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400; // failures come back as the IOException below, shown by the caller
    private const ushort FOF_WANTNUKEWARNING = 0x4000;
    private const int ERROR_CANCELLED = 0x4C7;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    /// <summary>
    /// Moves <paramref name="paths"/> to the Recycle Bin in one shell operation.
    /// Returns false when the user declined a permanent-delete warning.
    /// </summary>
    public static Task<bool> SendAsync(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
            return Task.FromResult(true);

        // The shell resolves a relative path against the current directory, which could be anywhere.
        foreach (var path in paths)
        {
            if (!Path.IsPathFullyQualified(path))
                throw new ArgumentException($"Only full paths can go to the Recycle Bin: '{path}'.", nameof(paths));
        }

        // The shell's file operations need an STA thread; the thread pool is MTA.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(Send(paths));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Recycle Bin",
        };
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Blocking form of <see cref="SendAsync"/>, for code already off the UI thread.</summary>
    public static bool SendBlocking(IReadOnlyCollection<string> paths) =>
        SendAsync(paths).GetAwaiter().GetResult();

    private static bool Send(IReadOnlyCollection<string> paths)
    {
        var fileOp = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // Null-separated, double-null-terminated (the marshaller adds the last null).
            pFrom = string.Join('\0', paths) + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT | FOF_NOERRORUI,
        };

        var result = SHFileOperation(ref fileOp);
        if (fileOp.fAnyOperationsAborted || result == ERROR_CANCELLED)
            return false;
        if (result != 0)
            throw new IOException($"Windows couldn't move it to the Recycle Bin (error 0x{result:X}).");
        return true;
    }
}
