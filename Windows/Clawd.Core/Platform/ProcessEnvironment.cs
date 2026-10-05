using System.Runtime.InteropServices;
using System.Text;

namespace Clawd.Core.Platform;

/// <summary>
/// Another process's environment variables. Orca starts each pane's processes with ORCA_PANE_KEY,
/// so reading it from a Claude Code process ties that process to its pane exactly.
///
/// Windows keeps the block in the process's PEB: PEB → RTL_USER_PROCESS_PARAMETERS → Environment,
/// read with ReadProcessMemory. That needs only the access a user has to their own processes.
/// The offsets below are those of 64-bit Windows 10 and 11; anything unexpected (a 32-bit target,
/// a protected process, an unreadable block) gives an empty result, and Clawd falls back to
/// matching by folder and last prompt.
/// </summary>
public static class ProcessEnvironment
{
    public static Dictionary<string, string> Read(int pid)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return result;
        try { ReadWindows(pid, result); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(() => $"environment of {pid}: {e.Message}");
        }
        return result;
    }

    private const int ParametersOffset = 0x20;        // PEB.ProcessParameters
    private const int EnvironmentOffset = 0x80;       // RTL_USER_PROCESS_PARAMETERS.Environment
    private const int EnvironmentSizeOffset = 0x3F0;  // RTL_USER_PROCESS_PARAMETERS.EnvironmentSize
    private const int MaxEnvironment = 1 << 20;

    private static void ReadWindows(int pid, Dictionary<string, string> result)
    {
        var process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (process == IntPtr.Zero) return;
        try
        {
            if (IsWow64Process(process, out var wow64) && wow64) return;   // 32-bit layout differs
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(process, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return;
            if (ReadPointer(process, info.PebBaseAddress + ParametersOffset) is not { } parameters || parameters == 0) return;
            if (ReadPointer(process, parameters + EnvironmentOffset) is not { } environment || environment == 0) return;
            var size = ReadPointer(process, parameters + EnvironmentSizeOffset) ?? 0;
            if (size <= 0 || size > MaxEnvironment) size = 32 * 1024;   // older builds: read a page and stop at the end marker
            var block = ReadBytes(process, environment, (int)size) ?? ReadBytes(process, environment, 4096);
            if (block is null) return;
            Parse(Encoding.Unicode.GetString(block), result);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>"NAME=value\0NAME=value\0\0". Entries starting with "=" are per-drive working folders.</summary>
    internal static void Parse(string block, Dictionary<string, string> result)
    {
        foreach (var entry in block.Split('\0'))
        {
            if (entry.Length == 0) break;
            var eq = entry.IndexOf('=', 1);
            if (eq <= 0 || entry[0] == '=') continue;
            result.TryAdd(entry[..eq], entry[(eq + 1)..]);
        }
    }

    private static long? ReadPointer(IntPtr process, long address)
    {
        var bytes = ReadBytes(process, address, 8);
        return bytes is null ? null : BitConverter.ToInt64(bytes);
    }

    private static byte[]? ReadBytes(IntPtr process, long address, int length)
    {
        var buffer = new byte[length];
        return ReadProcessMemory(process, (IntPtr)address, buffer, length, out var read) && read == length ? buffer : null;
    }

    private const int ProcessQueryInformation = 0x0400;
    private const int ProcessVmRead = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public long PebBaseAddress;
        public IntPtr AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool IsWow64Process(IntPtr process, out bool wow64);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, int size, out nint read);

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int length, out int returned);
}
