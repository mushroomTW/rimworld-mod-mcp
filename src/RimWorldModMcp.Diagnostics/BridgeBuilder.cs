using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Diagnostics;

/// <summary>Bridge preparation result. <paramref name="Origin"/> 是 <c>prebuilt</c> 或 <c>built</c>。</summary>
public sealed record BridgeBuild(bool Success, string? ModDirectory, bool Rebuilt, string? Error, string? Origin = null);

/// <summary>
/// 準備可供 RimWorld 載入的 Bridge Mod。
///
/// <para>
/// 兩條路：工具隨附的預編譯 DLL（用公開參考組件編的，遊戲 major.minor 相同就能直接載入，
/// 不需要本機 .NET SDK），或是遊戲版本不同時參考使用者本機的遊戲組件就地建置。
/// 兩者都複製到快取目錄再連結，以 dotnet tool 安裝時就不會去寫安裝目錄。
/// </para>
/// </summary>
public sealed class BridgeBuilder(StoreDirectories store, RimWorldLocator locator)
{
    /// <summary>Bridge 需要的組件：自身加上它自帶的 Harmony。</summary>
    private static readonly string[] RequiredAssemblies = ["RimWorldModMcp.Bridge.dll", "0Harmony.dll"];
    private const string SourceDir = "Source";
    private const string PrebuiltDir = "Prebuilt";
    private const string PrebuiltVersionFile = "game-version.txt";

    /// <summary>
    /// 確保 Bridge 已就緒且是最新的，回傳可直接連結的 Mod 目錄。
    /// </summary>
    /// <param name="sourceDirectory">Bridge 目錄（含 About/、Source/，通常還有 Prebuilt/）。</param>
    public BridgeBuild Ensure(string sourceDirectory)
    {
        var paths = locator.Detect();
        var managed = paths.ManagedDir;

        if (managed is null)
        {
            return new BridgeBuild(false, null, false, "RimWorld Managed directory not found; cannot prepare the Bridge.");
        }

        var modDirectory = Path.Combine(store.CacheHome, "bridge");
        var assemblies = Path.Combine(modDirectory, "Assemblies");
        var stampFile = Path.Combine(modDirectory, ".build-stamp");

        var gameVersion = GameVersion.ReadMajorMinor(paths.InstallRoot);
        var prebuilt = Path.Combine(sourceDirectory, PrebuiltDir);
        var prebuiltVersion = ReadStamp(Path.Combine(prebuilt, PrebuiltVersionFile));
        var prebuiltUsable = prebuiltVersion is not null
            && RequiredAssemblies.All(name => File.Exists(Path.Combine(prebuilt, name)));

        // 預編譯版本只看 major.minor：Mod 的相容性粒度就是這樣，1.6.x 之間的
        // 修訂版不會改動 Bridge 用到的那幾個 API。
        if (prebuiltUsable && gameVersion is not null && gameVersion == prebuiltVersion)
        {
            return EnsurePrebuilt(sourceDirectory, prebuilt, modDirectory, assemblies, stampFile);
        }

        // 走不了預編譯的原因要留下來：就地建置又失敗時，使用者得知道為什麼會走到這一步。
        var fallbackReason = !prebuiltUsable
            ? "no prebuilt Bridge shipped with this tool"
            : gameVersion is null
                ? $"the game version could not be read from Version.txt (prebuilt Bridge targets {prebuiltVersion})"
                : $"the prebuilt Bridge targets RimWorld {prebuiltVersion} but the installed game is {gameVersion}";

        var project = Path.Combine(sourceDirectory, SourceDir, "RimWorldModMcp.Bridge.csproj");

        if (!File.Exists(project))
        {
            return new BridgeBuild(false, null, false, $"Bridge project file not found: {project} ({fallbackReason}).");
        }

        var stamp = SourceStamp(sourceDirectory, managed);

        // 原始碼與遊戲組件都沒變就沿用既有建置，省下每次測試幾秒鐘。
        if (ReadStamp(stampFile) == stamp && RequiredAssemblies.All(name => File.Exists(Path.Combine(assemblies, name))))
        {
            return new BridgeBuild(true, modDirectory, false, null, "built");
        }

        var build = BuildFromSource(sourceDirectory, project, managed, modDirectory, assemblies, stampFile, stamp);

        return build.Success
            ? build
            : build with { Error = $"{build.Error} (Building from source because {fallbackReason}; a local .NET SDK is required for that.)" };
    }

    /// <summary>把預編譯的 DLL 與 About/ 複製進快取目錄。檔案沒變就不重複複製。</summary>
    internal static BridgeBuild EnsurePrebuilt(string sourceDirectory, string prebuilt, string modDirectory, string assemblies, string stampFile)
    {
        var stamp = "prebuilt:" + FileStamp(Directory.EnumerateFiles(prebuilt).Concat(Directory.EnumerateFiles(Path.Combine(sourceDirectory, "About"))));

        if (ReadStamp(stampFile) == stamp && RequiredAssemblies.All(name => File.Exists(Path.Combine(assemblies, name))))
        {
            return new BridgeBuild(true, modDirectory, false, null, "prebuilt");
        }

        try
        {
            Directory.CreateDirectory(assemblies);
            CopyMetadata(sourceDirectory, modDirectory);

            foreach (var file in Directory.GetFiles(prebuilt, "*.dll"))
            {
                File.Copy(file, Path.Combine(assemblies, Path.GetFileName(file)), overwrite: true);
            }

            File.WriteAllText(stampFile, stamp, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new BridgeBuild(false, null, false, $"Failed to copy the prebuilt Bridge: {e.Message}");
        }

        return new BridgeBuild(true, modDirectory, true, null, "prebuilt");
    }

    private static BridgeBuild BuildFromSource(
        string sourceDirectory,
        string project,
        string managed,
        string modDirectory,
        string assemblies,
        string stampFile,
        string stamp)
    {
        Directory.CreateDirectory(assemblies);

        // 原始碼先複製進快取目錄再建置，bin/obj 才會落在快取而不是工具的
        // 安裝位置——以 dotnet tool 全域安裝時，安裝目錄不一定可寫。
        var cachedProject = Path.Combine(modDirectory, SourceDir, "RimWorldModMcp.Bridge.csproj");

        try
        {
            CopyMetadata(sourceDirectory, modDirectory);
            CopySources(sourceDirectory, modDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // About/ 不存在或檔案被鎖住時，要以設計好的降級訊息回報，
            // 不能讓原始例外逸出成使用者看到的整包堆疊。
            return new BridgeBuild(false, null, false, $"Failed to copy Bridge sources: {e.Message}");
        }

        var (success, output) = RunBuild(cachedProject, managed);

        if (!success)
        {
            return new BridgeBuild(false, null, false, $"Bridge build failed: {output}");
        }

        var built = FindOutput(cachedProject);

        if (built is null)
        {
            return new BridgeBuild(false, null, false, "Bridge build reported success but no output DLL was found.");
        }

        try
        {
            foreach (var file in Directory.GetFiles(built, "*.dll"))
            {
                File.Copy(file, Path.Combine(assemblies, Path.GetFileName(file)), overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new BridgeBuild(false, null, false, $"Failed to copy Bridge assemblies: {e.Message}");
        }

        var missing = RequiredAssemblies.Where(name => !File.Exists(Path.Combine(assemblies, name))).ToList();

        if (missing.Count > 0)
        {
            return new BridgeBuild(false, null, false, $"Bridge build is missing assemblies: {string.Join(", ", missing)}");
        }

        File.WriteAllText(stampFile, stamp, new UTF8Encoding(false));

        return new BridgeBuild(true, modDirectory, true, null, "built");
    }

    private static (bool Success, string Output) RunBuild(string project, string managedDirectory)
    {
        try
        {
            var execution = DotnetHost.RunBuild(project, managedDirectory: managedDirectory);
            var combined = (execution.Stderr + Environment.NewLine + execution.Stdout).Trim();

            // 錯誤訊息取尾端就好，前面多半是還原套件的雜訊。
            return (execution.Success, combined.Length > 2000 ? combined[^2000..] : combined);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
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

        // 可能同時存在多個 TFM 輸出（歷史殘留）與 ref/ 目錄；取最新寫入的那一份，
        // 並排除 reference assembly——那種組件只有簽章沒有實作。
        return Directory
            .EnumerateFiles(releaseRoot, "RimWorldModMcp.Bridge.dll", SearchOption.AllDirectories)
            .Where(path =>
            {
                var directory = Path.GetFileName(Path.GetDirectoryName(path));
                return !string.Equals(directory, "ref", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(directory, "refint", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
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

    /// <summary>把 Source/（排除 bin/obj）複製到快取目錄，建置在那裡進行。</summary>
    private static void CopySources(string sourceDirectory, string modDirectory)
    {
        var sourceRoot = Path.Combine(sourceDirectory, SourceDir);
        var targetRoot = Path.Combine(modDirectory, SourceDir);

        Directory.CreateDirectory(targetRoot);

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);

            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
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

    /// <summary>一組檔案的指紋：名稱、大小、修改時間。預編譯檔或 About/ 更新時就會變。</summary>
    private static string FileStamp(IEnumerable<string> files)
    {
        var builder = new StringBuilder();

        foreach (var file in files.Order())
        {
            var info = new FileInfo(file);
            builder.Append(info.Name).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append('|');
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
