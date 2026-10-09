using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Blackbird.Services;

/// <summary>
/// The linker hands a map's Umbra tome to worker threads that finish in a few milliseconds, then checks on them with
/// <c>Sleep(1000)</c> between polls, so every map link waits about a second for nothing. Once the running linker has
/// unpacked itself, this rewrites that one immediate to <c>Sleep(1)</c> in memory. The file on disk is never touched,
/// and nothing is written unless the surrounding instructions match the shipped linker (changelist 3421953) exactly.
/// The loop still waits for every worker, so the linked output is the same.
/// </summary>
public static class LinkerUmbraPatch
{
    // RVA of "cmp r13, r14" in the tome converter's poll loop; the patched immediate is the 1000 in "mov ecx, 1000".
    private const int SignatureRva = 0x355968;
    private const int ImmediateOffset = 11;
    private static readonly byte[] Signature = Convert.FromHexString(
        "4D3BEE" +        // cmp r13, r14
        "7505" +          // jne +5
        "4C3BFE" +        // cmp r15, rsi
        "7430" +          // je  +0x30
        "B9E8030000" +    // mov ecx, 1000
        "FF15");          // call [Sleep]

    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(15);
    private const int PollMs = 5;

    /// <summary>Patches <paramref name="linker"/> in the background; returns once patched, or once that's not possible.</summary>
    public static Task ApplyAsync(Process linker, CancellationToken ct) => Task.Run(() => Apply(linker, ct), ct);

    private static void Apply(Process linker, CancellationToken ct)
    {
        var handle = OpenProcess(ProcessVmRead | ProcessVmWrite | ProcessVmOperation | ProcessQueryInformation, false, linker.Id);
        if (handle == IntPtr.Zero)
            return;

        try
        {
            var deadline = Stopwatch.StartNew();
            var buffer = new byte[Signature.Length];
            IntPtr imageBase = IntPtr.Zero;

            // The image is packed on disk and decrypted in place shortly after start; poll until the code is readable.
            while (deadline.Elapsed < GiveUpAfter && !ct.IsCancellationRequested && !linker.HasExited)
            {
                if (imageBase == IntPtr.Zero)
                    imageBase = MainModuleBase(handle);

                if (imageBase != IntPtr.Zero
                    && ReadProcessMemory(handle, imageBase + SignatureRva, buffer, buffer.Length, out var read)
                    && read == buffer.Length
                    && buffer.AsSpan().SequenceEqual(Signature))
                {
                    WriteImmediate(handle, imageBase + SignatureRva + ImmediateOffset, 1);
                    return;
                }

                Thread.Sleep(PollMs);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static void WriteImmediate(IntPtr process, IntPtr address, uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (!VirtualProtectEx(process, address, bytes.Length, PageExecuteReadWrite, out var oldProtect))
            return;
        WriteProcessMemory(process, address, bytes, bytes.Length, out _);
        VirtualProtectEx(process, address, bytes.Length, oldProtect, out _);
        FlushInstructionCache(process, address, bytes.Length);
    }

    private static IntPtr MainModuleBase(IntPtr process)
    {
        var modules = new IntPtr[1];
        return EnumProcessModulesEx(process, modules, IntPtr.Size, out var needed, ListModulesAll) && needed > 0
            ? modules[0]
            : IntPtr.Zero;
    }

    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint ListModulesAll = 0x03;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out int written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr process, IntPtr address, int size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, int size);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, int size, out int needed, uint filter);
}
