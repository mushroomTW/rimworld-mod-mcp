using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RimWorldModMcp.Core.Platform;

/// <summary>
/// 讀取其他程序的命令列。BCL 沒有這個 API，各平台各自實作。
///
/// <para>
/// 用途是辨識「遊戲自己重開的測試場次」：RimWorld 的 GenCommandLine.Restart
/// 以相同參數啟動新程序後自行結束，PID 換掉了，但 -savedatafolder 仍指向本場次的暫存目錄。
/// </para>
/// </summary>
public static partial class ProcessCommandLine
{
    /// <summary>讀不到（程序不存在、權限不足、平台不支援）時回傳 null。</summary>
    public static string? Read(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ReadWindows(processId);
            }

            if (OperatingSystem.IsLinux())
            {
                // /proc/<pid>/cmdline 以 NUL 分隔參數。
                var raw = File.ReadAllText($"/proc/{processId}/cmdline");
                return raw.Length == 0 ? null : raw.TrimEnd('\0').Replace('\0', ' ');
            }

            return ReadWithPs(processId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>
    /// NtQueryInformationProcess(ProcessCommandLineInformation) 直接回傳 UNICODE_STRING，
    /// 不必讀對方的 PEB；只要 PROCESS_QUERY_LIMITED_INFORMATION，同使用者的程序都開得起來。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? ReadWindows(int processId)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);

        if (handle.IsInvalid)
        {
            return null;
        }

        var length = 1024;

        while (true)
        {
            // 用非受控記憶體：回傳的 UNICODE_STRING.Buffer 指向這塊緩衝區內部，
            // 受控陣列在呼叫結束後可能被 GC 搬動。
            var buffer = Marshal.AllocHGlobal(length);

            try
            {
                var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out var needed);

                if (status == StatusInfoLengthMismatch && needed > length)
                {
                    length = needed;
                    continue;
                }

                if (status != 0)
                {
                    return null;
                }

                // UNICODE_STRING：USHORT Length（位元組）、USHORT MaximumLength、對齊到指標大小後的 PWSTR Buffer。
                var bytes = Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return Marshal.PtrToStringUni(text, (ushort)bytes / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>macOS 沒有 /proc，改問 ps（-ww 取消欄寬截斷）。</summary>
    private static string? ReadWithPs(int processId)
    {
        var startInfo = new ProcessStartInfo("/bin/ps")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("-ww");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("args=");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(processId.ToString());

        using var ps = Process.Start(startInfo);

        if (ps is null)
        {
            return null;
        }

        var output = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();
        return ps.ExitCode == 0 && output.Length > 0 ? output : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(SafeProcessHandle processHandle, int processInformationClass, IntPtr processInformation, int processInformationLength, out int returnLength);
}
