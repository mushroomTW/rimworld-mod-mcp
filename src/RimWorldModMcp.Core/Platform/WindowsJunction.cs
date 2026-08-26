using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RimWorldModMcp.Core.Platform;

/// <summary>
/// Windows directory junction（mount point 類型的 reparse point）的建立。
///
/// <para>
/// <b>為什麼不用 <c>Directory.CreateSymbolicLink</c></b>：那建立的是 symbolic link，
/// 在 Windows 上需要 SeCreateSymbolicLinkPrivilege——也就是管理員權限或已開啟開發者模式。
/// junction 則是一般使用者就能建立。Python 版刻意選 junction 正是為了這一點，
/// 這個決策不可以在改寫時被「簡化」成 symlink，否則沒有管理員權限的使用者會完全無法測試 Mod。
/// </para>
/// <para>
/// BCL 沒有提供建立 junction 的 API，因此只能自行對
/// <c>DeviceIoControl(FSCTL_SET_REPARSE_POINT)</c> 做 P/Invoke。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsJunction
{
    private const uint IoReparseTagMountPoint = 0xA000_0003;
    private const uint FsctlSetReparsePoint = 0x0009_00A4;

    private const uint GenericWrite = 0x4000_0000;
    private const uint FileShareRead = 0x0000_0001;
    private const uint FileShareWrite = 0x0000_0002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;

    /// <summary>reparse buffer 前 16 byte 是固定欄位，其後才是路徑內容。</summary>
    private const int HeaderBytes = 16;

    /// <summary>
    /// 在 <paramref name="linkPath"/> 建立指向 <paramref name="targetPath"/> 的 junction。
    /// 呼叫端必須先確認 <paramref name="linkPath"/> 不存在。
    /// </summary>
    internal static void Create(string linkPath, string targetPath)
    {
        // junction 本體是一個「帶有 reparse point 的空目錄」，所以要先把目錄建出來。
        Directory.CreateDirectory(linkPath);

        try
        {
            WriteReparsePoint(linkPath, targetPath);
        }
        catch
        {
            // 掛上 reparse point 失敗時不要留下一個空目錄冒充連結，
            // 否則後續的 owned-link 判定會看到一個「名稱對但不是連結」的目錄。
            try
            {
                Directory.Delete(linkPath);
            }
            catch (IOException)
            {
                // 清理失敗不應遮蔽原本的錯誤。
            }

            throw;
        }
    }

    private static void WriteReparsePoint(string linkPath, string targetPath)
    {
        // SubstituteName 必須是 NT 命名空間路徑（\??\C:\...），PrintName 則是給人看的一般路徑。
        // 兩者都省略的話 Explorer 與 dir 指令會顯示不出目標。
        var substituteName = @"\??\" + targetPath;
        var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(targetPath);

        // PathBuffer 佈局：SubstituteName + '\0' + PrintName + '\0'
        var pathBufferBytes = substituteBytes.Length + 2 + printBytes.Length + 2;
        var buffer = new byte[HeaderBytes + pathBufferBytes];

        var span = buffer.AsSpan();
        BitConverter.TryWriteBytes(span[..4], IoReparseTagMountPoint);

        // ReparseDataLength 只計「標頭之後」的長度：四個 USHORT（8 byte）加上 PathBuffer。
        BitConverter.TryWriteBytes(span.Slice(4, 2), (ushort)(8 + pathBufferBytes));
        BitConverter.TryWriteBytes(span.Slice(6, 2), (ushort)0); // Reserved

        BitConverter.TryWriteBytes(span.Slice(8, 2), (ushort)0);
        BitConverter.TryWriteBytes(span.Slice(10, 2), (ushort)substituteBytes.Length);
        BitConverter.TryWriteBytes(span.Slice(12, 2), (ushort)(substituteBytes.Length + 2));
        BitConverter.TryWriteBytes(span.Slice(14, 2), (ushort)printBytes.Length);

        substituteBytes.CopyTo(buffer, HeaderBytes);
        printBytes.CopyTo(buffer, HeaderBytes + substituteBytes.Length + 2);

        using var handle = CreateFile(
            linkPath,
            GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            // BACKUP_SEMANTICS 才能開啟目錄；OPEN_REPARSE_POINT 確保開到連結本身而非目標。
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"無法開啟 junction 目錄以寫入 reparse point：{linkPath}");
        }

        var ok = DeviceIoControl(
            handle,
            FsctlSetReparsePoint,
            ref MemoryMarshal.GetReference(span),
            (uint)buffer.Length,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);

        if (!ok)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"設定 junction reparse point 失敗：{linkPath} -> {targetPath}");
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref byte lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
