using System;
using System.IO;

namespace Blackbird.Services;

/// <summary>
/// One plain sentence for a failure, for the UI. The exception's own message is for a
/// tooltip or the crash log, never the line the user reads.
/// </summary>
public static class ErrorText
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    public static string Describe(Exception ex) => ex switch
    {
        UserMessageException or WorkshopFileCorruptException or WorkshopZoneException => ex.Message,
        UnauthorizedAccessException => "Windows denied access. Close anything using these files, or check the folder's permissions.",
        PathTooLongException => "A path is too long for Windows.",
        DirectoryNotFoundException => "A folder it needed isn't there any more.",
        FileNotFoundException { FileName: { Length: > 0 } name } => $"{Path.GetFileName(name)} isn't there any more.",
        FileNotFoundException => "A file it needed isn't there any more.",
        IOException { HResult: SharingViolation or LockViolation } => "A file is open in another program. Close it and try again.",
        IOException { HResult: DiskFull or HandleDiskFull } => "The disk is full.",
        IOException => "Windows couldn't read or write a file.",
        OperationCanceledException => "It was cancelled.",
        System.ComponentModel.Win32Exception => "Windows couldn't start the program.",
        System.Net.Http.HttpRequestException => "Steam's servers didn't answer. Check your connection and try again.",
        _ => "Something unexpected stopped it.",
    };

    /// <summary>"{what} {why}" as one line, e.g. "Couldn't rename the folder. A file is open in another program…".</summary>
    public static string Describe(string what, Exception ex) => $"{what} {Describe(ex)}";
}

/// <summary>A failure whose message is already one plain sentence for the user, shown as it is.</summary>
public sealed class UserMessageException(string message) : Exception(message);

