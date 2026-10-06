using System.Diagnostics;
using System.Text;

namespace RimWorldModMcp.Core.Platform;

/// <summary>
/// 解析 dotnet 主機的絕對路徑。
///
/// <para>
/// Windows 的 CreateProcess 搜尋順序包含「呼叫行程的目前目錄」，而本工具以
/// dotnet tool 安裝時，目前目錄常常是使用者的 Mod 資料夾——以裸名 "dotnet"
/// 啟動可能執行到那裡的同名檔案。
/// </para>
/// </summary>
public static class DotnetHost
{
    public static string Executable()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");

        if (!string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath))
        {
            return hostPath;
        }

        // 本工具自己就是 dotnet 程式，宿主通常是 dotnet.exe 本體。
        var processPath = Environment.ProcessPath;

        if (processPath is not null &&
            Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return "dotnet";
    }

    /// <summary>建置結果與標準輸出。</summary>
    public sealed record BuildExecution(bool Success, string Stdout, string Stderr, int ExitCode)
    {
        public string CombinedOutput
        {
            get
            {
                if (string.IsNullOrEmpty(Stdout))
                {
                    return Stderr;
                }

                if (string.IsNullOrEmpty(Stderr))
                {
                    return Stdout;
                }

                return Stdout + Environment.NewLine + Stderr;
            }
        }
    }

    /// <summary>
    /// 執行 dotnet build，包含雙管線平行讀取與逾時安全終止。
    /// </summary>
    public static BuildExecution RunBuild(
        string project,
        string? workingDirectory = null,
        string? managedDirectory = null,
        IEnumerable<string>? extraArgs = null,
        TimeSpan? timeout = null)
    {
        var timeoutValue = timeout ?? TimeSpan.FromMinutes(10);
        var startInfo = new ProcessStartInfo(Executable())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // dotnet build 一律輸出 UTF-8；不指定的話會用主控台字碼頁解碼（如 cp950），中文全變亂碼。
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // 診斷訊息是給 agent 讀的，固定用英文，不隨系統語系變動。
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--nologo");

        if (extraArgs is not null)
        {
            foreach (var arg in extraArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        if (managedDirectory is not null)
        {
            startInfo.Environment[Paths.EnvironmentVariables.ManagedDir] = managedDirectory;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet build.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutValue))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 行程剛好自行結束或無權限殺除，不影響逾時判定
            }

            throw new TimeoutException($"dotnet build did not finish within {timeoutValue.TotalMinutes:0} minutes and was terminated.");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        return new BuildExecution(process.ExitCode == 0, stdout, stderr, process.ExitCode);
    }
}
