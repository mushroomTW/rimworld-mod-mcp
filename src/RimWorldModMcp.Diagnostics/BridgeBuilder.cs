using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Diagnostics;

/// <summary>Bridge 準備結果。</summary>
public sealed record BridgeBuild(bool Success, string? ModDirectory, bool Rebuilt, string? Error);

/// <summary>
/// 準備可供 RimWorld 載入的 Bridge Mod。
///
/// <para>
/// Bridge 必須參考使用者本機安裝的 RimWorld 組件，所以無法預先編譯——
/// 只能在第一次測試前就地建置。建置產物放在快取目錄而不是工具的安裝位置，
/// 這樣以 dotnet tool 安裝時也不會去寫安裝目錄。
/// </para>
/// </summary>
public sealed class BridgeBuilder(StoreDirectories store, IRimWorldLocator locator)
{
    /// <summary>Bridge 需要的組件：自身加上它自帶的 Harmony。</summary>
    private static readonly string[] RequiredAssemblies = ["RimWorldModMcp.Bridge.dll", "0Harmony.dll"];

    /// <summary>
    /// 確保 Bridge 已建置且是最新的，回傳可直接連結的 Mod 目錄。
    /// </summary>
    /// <param name="sourceDirectory">Bridge 的原始碼目錄（含 About/ 與 Source/）。</param>
    public BridgeBuild Ensure(string sourceDirectory)
    {
        var managed = locator.Detect().ManagedDir;

        if (managed is null)
        {
            return new BridgeBuild(false, null, false, "找不到 RimWorld 的 Managed 目錄，無法建置 Bridge。");
        }

        var project = Path.Combine(sourceDirectory, "Source", "RimWorldModMcp.Bridge.csproj");

        if (!File.Exists(project))
        {
            return new BridgeBuild(false, null, false, $"找不到 Bridge 專案檔：{project}");
        }

        var modDirectory = Path.Combine(store.CacheHome, "bridge");
        var assemblies = Path.Combine(modDirectory, "Assemblies");
        var stampFile = Path.Combine(modDirectory, ".build-stamp");
        var stamp = SourceStamp(sourceDirectory, managed);

        // 原始碼與遊戲組件都沒變就沿用既有建置，省下每次測試幾秒鐘。
        if (ReadStamp(stampFile) == stamp && RequiredAssemblies.All(name => File.Exists(Path.Combine(assemblies, name))))
        {
            return new BridgeBuild(true, modDirectory, false, null);
        }

        Directory.CreateDirectory(assemblies);
        CopyMetadata(sourceDirectory, modDirectory);

        var (success, output) = RunBuild(project, managed);

        if (!success)
        {
            return new BridgeBuild(false, null, false, $"Bridge 建置失敗：{output}");
        }

        var built = FindOutput(project);

        if (built is null)
        {
            return new BridgeBuild(false, null, false, "Bridge 建置回報成功，但找不到輸出的 DLL。");
        }

        foreach (var file in Directory.GetFiles(built, "*.dll"))
        {
            File.Copy(file, Path.Combine(assemblies, Path.GetFileName(file)), overwrite: true);
        }

        var missing = RequiredAssemblies.Where(name => !File.Exists(Path.Combine(assemblies, name))).ToList();

        if (missing.Count > 0)
        {
            return new BridgeBuild(false, null, false, $"Bridge 建置後缺少組件：{string.Join("、", missing)}");
        }

        File.WriteAllText(stampFile, stamp, new UTF8Encoding(false));

        return new BridgeBuild(true, modDirectory, true, null);
    }

    private static (bool Success, string Output) RunBuild(string project, string managedDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--nologo");

        // 指向本機安裝的遊戲組件。沒有這個變數時 csproj 會退回公開的參考組件，
        // 那種組件只有簽章沒有實作，不能拿來實際執行。
        startInfo.Environment["RIMWORLD_MANAGED_DIR"] = managedDirectory;

        try
        {
            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return (false, "無法啟動 dotnet build。");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var combined = (stderr + Environment.NewLine + stdout).Trim();

            // 錯誤訊息取尾端就好，前面多半是還原套件的雜訊。
            return (process.ExitCode == 0, combined.Length > 2000 ? combined[^2000..] : combined);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, e.Message);
        }
    }

    /// <summary>找出建置輸出目錄。不假設 TFM 子目錄名稱，直接找含有 Bridge DLL 的那一層。</summary>
    private static string? FindOutput(string project)
    {
        var releaseRoot = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release");

        if (!Directory.Exists(releaseRoot))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(releaseRoot, "RimWorldModMcp.Bridge.dll", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(directory => directory is not null);
    }

    /// <summary>把 About/ 複製到建置目錄，讓那裡成為一個完整的 Mod 資料夾。</summary>
    private static void CopyMetadata(string sourceDirectory, string modDirectory)
    {
        var sourceAbout = Path.Combine(sourceDirectory, "About");
        var targetAbout = Path.Combine(modDirectory, "About");

        Directory.CreateDirectory(targetAbout);

        foreach (var file in Directory.GetFiles(sourceAbout))
        {
            File.Copy(file, Path.Combine(targetAbout, Path.GetFileName(file)), overwrite: true);
        }
    }

    /// <summary>
    /// 建置輸入的指紋：Bridge 原始碼加上遊戲組件。
    /// 遊戲更新後組件會變，Bridge 必須跟著重建才不會參考到舊簽章。
    /// </summary>
    private static string SourceStamp(string sourceDirectory, string managedDirectory)
    {
        var builder = new StringBuilder();

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order())
        {
            var info = new FileInfo(file);
            builder.Append(info.Name).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append('|');
        }

        foreach (var name in (ReadOnlySpan<string>)["Assembly-CSharp.dll", "UnityEngine.CoreModule.dll"])
        {
            var info = new FileInfo(Path.Combine(managedDirectory, name));

            if (info.Exists)
            {
                builder.Append(name).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append('|');
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string? ReadStamp(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
