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
}
