using System.Reflection;
using System.Text;

namespace RimWorldModMcp.Diagnostics;

/// <summary>
/// 內嵌在 Server 組件裡的 Bridge（About/、Source/、Prebuilt/），資源名稱為 <c>bridge/&lt;相對路徑&gt;</c>。
/// Release 只發佈單一執行檔，旁邊沒有 bridge/ 目錄；run_test_cycle 需要時才解到快取目錄。
/// </summary>
internal static class EmbeddedBridge
{
    internal const string StampFile = ".embedded-stamp";
    private const string Prefix = "bridge/";

    /// <summary>把組件內嵌的 Bridge 解到 <paramref name="targetDirectory"/> 並回傳它；組件沒有內嵌 Bridge 時回傳 null。</summary>
    internal static string? Extract(Assembly? assembly, string targetDirectory)
    {
        if (assembly is null)
        {
            return null;
        }

        // RecursiveDir 在 Windows 上帶反斜線，資源名稱因平台而異，一律正規化成斜線再比對。
        var resources = assembly.GetManifestResourceNames()
            .Select(name => (Name: name, Relative: name.Replace('\\', '/')))
            .Where(resource => resource.Relative.StartsWith(Prefix, StringComparison.Ordinal))
            .ToList();

        if (resources.Count == 0)
        {
            return null;
        }

        // MVID 每次編譯都不同：同一個執行檔沿用已解出的檔案，換了版本才整個重解，不留舊版殘檔。
        var stamp = assembly.ManifestModule.ModuleVersionId.ToString("N");
        var stampFile = Path.Combine(targetDirectory, StampFile);

        if (File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp)
        {
            return targetDirectory;
        }

        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
        }

        foreach (var (name, relative) in resources)
        {
            var path = Path.Combine(targetDirectory, relative[Prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var resource = assembly.GetManifestResourceStream(name)!;
            using var file = File.Create(path);
            resource.CopyTo(file);
        }

        // 戳記最後才寫：中途失敗的解壓下次會重來，不會被當成完整的一份沿用。
        File.WriteAllText(stampFile, stamp, new UTF8Encoding(false));

        return targetDirectory;
    }
}
