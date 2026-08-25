using System.ComponentModel;
using System.Diagnostics;

namespace RimWorldModMcp.Core.Platform;

/// <summary>程序啟動、存活判定與終止。抽成介面是為了讓測試能用假實作。</summary>
public interface IProcessHost
{
    /// <summary>
    /// 這個 PID 是否還活著。
    /// <b>判定不確定時一律回傳 <c>true</c>。</b>理由見實作註解。
    /// </summary>
    bool IsAlive(int processId);

    /// <summary>終止指定程序。成功送出終止要求回傳 true。</summary>
    bool Terminate(int processId);

    /// <summary>取得程序啟動時間，用於配合 PID 判斷是否為同一個程序。</summary>
    DateTime? StartTimeUtc(int processId);
}

/// <inheritdoc cref="IProcessHost"/>
public sealed class ProcessHost : IProcessHost
{
    public bool IsAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !HasExited(process);
        }
        catch (ArgumentException)
        {
            // 找不到這個 PID：確定已經不存在。
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            // 其他任何狀況（權限不足、平台限制）一律當成「還活著」。
            //
            // 這個方向是刻意的 fail-safe：把活的判成死，會讓另一個程序搶走鎖並同時操作
            // 同一份狀態；把死的判成活，最壞只是要等殘骸鎖逾時。前者的後果嚴重得多。
            // Python 版對 PermissionError 也是回傳 True，這裡保持一致。
            return true;
        }
    }

    public bool Terminate(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (HasExited(process))
            {
                return false;
            }

            process.Kill(entireProcessTree: false);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public DateTime? StartTimeUtc(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            // 拿不到啟動時間就回 null，呼叫端會退回「只比對 PID」的行為。
            return null;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Win32Exception)
        {
            // 存取被拒代表程序存在但不屬於我們——是活的。
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
