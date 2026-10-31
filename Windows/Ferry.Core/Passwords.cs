using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Ferry.Core;

/// <summary>パスワードの保存先（テストでは差し替える）</summary>
public interface IPasswordStore
{
    string? GetPassword(Guid serverId);
    /// <summary>null で削除</summary>
    void SetPassword(Guid serverId, string? password);
}

/// <summary>
/// Windows の資格情報マネージャーに、ターゲット名 "Ferry:&lt;接続先の ID&gt;" で保存する。
/// 共有の接続先も ID で引くので、共有に移しても保存済みのパスワードはそのまま使える。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialPasswordStore : IPasswordStore
{
    public static string TargetName(Guid serverId) => "Ferry:" + FerryJson.IdString(serverId);

    public string? GetPassword(Guid serverId)
    {
        if (!CredRead(TargetName(serverId), CredTypeGeneric, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return "";
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void SetPassword(Guid serverId, string? password)
    {
        var target = TargetName(serverId);
        if (password is null)
        {
            CredDelete(target, CredTypeGeneric, 0);
            return;
        }
        var blob = Encoding.Unicode.GetBytes(password);
        var blobPointer = Marshal.AllocHGlobal(Math.Max(blob.Length, 1));
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPointer,
                Persist = CredPersistLocalMachine,
                UserName = "Ferry",
                Comment = "Ferry の接続先のパスワード",
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException(
                    $"パスワードを資格情報マネージャーに保存できませんでした（エラー {Marshal.GetLastWin32Error()}）");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPointer);
        }
    }

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);
}
