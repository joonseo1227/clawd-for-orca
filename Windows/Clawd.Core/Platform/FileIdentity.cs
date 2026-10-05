using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Clawd.Core.Platform;

/// <summary>
/// Tells a file that was replaced at the same path (a new file, a rewrite) from one that only grew.
/// Windows has a file ID for that; elsewhere the creation time stands in. The first bytes back
/// both up: NTFS "tunnels" creation times to a file recreated under the same name within seconds.
/// </summary>
public sealed record FileIdentity(ulong Id, byte[] Head)
{
    private const int HeadLength = 256;

    public static FileIdentity Of(FileStream stream)
    {
        ulong id = 0;
        if (OperatingSystem.IsWindows() && GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            id = ((ulong)info.VolumeSerialNumber << 32) ^ ((ulong)info.FileIndexHigh << 32 | info.FileIndexLow);
        else
            id = (ulong)File.GetCreationTimeUtc(stream.Name).Ticks;
        var head = new byte[Math.Min(HeadLength, stream.Length)];
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            stream.ReadExactly(head);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException) { head = []; }
        return new FileIdentity(id, head);
    }

    /// <summary>Same file as <paramref name="earlier"/>: same ID, and the bytes it started with are still there.</summary>
    public bool SameFile(FileIdentity? earlier)
    {
        if (earlier is null || earlier.Id != Id) return false;
        var n = Math.Min(earlier.Head.Length, Head.Length);
        return earlier.Head.Length <= Head.Length && earlier.Head.AsSpan(0, n).SequenceEqual(Head.AsSpan(0, n));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        // FILETIMEs: pairs of DWORDs, 4-byte aligned (a long here would add padding).
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
}
