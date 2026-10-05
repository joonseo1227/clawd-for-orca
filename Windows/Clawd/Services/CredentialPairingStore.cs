using System.Runtime.InteropServices;
using Clawd.Core;
using Clawd.Core.Orca;

namespace Clawd.Services;

/// <summary>
/// The Orca pairing in the Windows Credential Manager (Control Panel › Credential Manager ›
/// Windows Credentials, "Clawd/orca-pairing"), encrypted by Windows for this user and kept on
/// this PC only (local-machine persistence never roams with the profile).
/// </summary>
public sealed class CredentialPairingStore : IPairingStore, ICredentialStore
{
    string? ICredentialStore.Load() => Load() is { } p ? System.Text.Encoding.UTF8.GetString(p.ToJson()) : null;

    bool ICredentialStore.Save(string json) => OrcaPairing.FromJson(System.Text.Encoding.UTF8.GetBytes(json)) is { } p && Save(p);

    private const string Target = "Clawd/orca-pairing";
    private const int Generic = 1, PersistLocalMachine = 2;

    public bool Exists()
    {
        if (!CredRead(Target, Generic, 0, out var cred)) return false;
        CredFree(cred);
        return true;
    }

    public OrcaPairing? Load()
    {
        if (!CredRead(Target, Generic, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            var blob = new byte[cred.BlobSize];
            Marshal.Copy(cred.Blob, blob, 0, blob.Length);
            return OrcaPairing.FromJson(blob);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public bool Save(OrcaPairing pairing)
    {
        var blob = pairing.ToJson();
        var buffer = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, buffer, blob.Length);
            var cred = new Credential
            {
                Type = Generic,
                TargetName = Target,
                Comment = "Clawd for Orca: live terminal pairing",
                BlobSize = (uint)blob.Length,
                Blob = buffer,
                Persist = PersistLocalMachine,
                UserName = "orca",
            };
            // CredWrite replaces an existing entry in place.
            if (CredWrite(ref cred, 0)) return true;
            Log.Error($"pairing save failed: {Marshal.GetLastWin32Error()}");
            return false;
        }
        finally
        {
            // The secret doesn't linger in freed memory.
            Marshal.Copy(new byte[blob.Length], 0, buffer, blob.Length);
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Clear() => CredDelete(Target, Generic, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
