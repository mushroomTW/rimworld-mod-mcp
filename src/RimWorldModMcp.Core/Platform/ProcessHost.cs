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

    /// <summary>
    /// 終止指定程序。成功送出終止要求回傳 true。
    ///
    /// <para>
    /// 提供 <paramref name="expectedStartUtc"/> 時會先比對程序啟動時間：
    /// 對不上代表這個 PID 已被作業系統重用給別的程序，拒絕終止——
    /// 否則會殺掉一個無關的程序。記錄過啟動時間的呼叫端都應該傳。
    /// </para>
    /// </summary>
    bool Terminate(int processId, DateTime? expectedStartUtc = null);

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

    public bool Terminate(int processId, DateTime? expectedStartUtc = null)
    {
        if (processId <= 0)
        {
            return false;
        }

        // PID 重用防護：啟動時間對不上就代表原程序早已結束，這個 PID 現在
        // 屬於別人。與 CriticalSectionLock.IsStale 用同一套判準（容忍一秒精度差）。
        if (expectedStartUtc is not null)
        {
            var actual = StartTimeUtc(processId);

            if (actual is not null && Math.Abs((expectedStartUtc.Value - actual.Value).TotalSeconds) > 1)
            {
                return false;
            }
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
        catch (Exception e) when (
            e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException or IOException)
        {
            // 拿不到啟動時間（不存在、已結束、權限不足、平台限制）就回 null，
            // 呼叫端會退回「只比對 PID」的行為。裸 catch 會連程式錯誤一起吞掉。
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
