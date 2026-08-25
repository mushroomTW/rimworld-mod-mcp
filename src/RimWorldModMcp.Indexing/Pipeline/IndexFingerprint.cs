using System.Security.Cryptography;
using System.Text;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Indexing.Pipeline;

/// <summary>
/// 判斷索引是否還對得上目前的遊戲安裝。
///
/// <para>
/// Python 版只雜湊 <c>Version.txt</c>、組件 DLL 的大小與時間戳，以及各 pack 的
/// <b>目錄名稱</b>——Def XML 的內容完全不參與。手動編輯 <c>Data/Core/Defs/*.xml</c>
/// 或新增／刪除單一 Def 檔案之後，索引仍會回報 fresh。那是真正的正確性缺口，
/// 這裡把 Def 檔案也納入指紋。
/// </para>
/// </summary>
public sealed class IndexFingerprint
{
    /// <summary>計算目前安裝的指紋。偵測不到遊戲時回傳 <c>null</c>。</summary>
    public string? Compute(RimWorldPaths paths)
    {
        if (paths.ManagedDir is null || paths.DataDir is null)
        {
            return null;
        }

        var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        if (paths.InstallRoot is not null)
        {
            AppendFile(digest, Path.Combine(paths.InstallRoot, "Version.txt"));
        }

        foreach (var assembly in Directory.GetFiles(paths.ManagedDir, "Assembly-CSharp*.dll").Order())
        {
            AppendFile(digest, assembly);
        }

        foreach (var pack in Directory.GetDirectories(paths.DataDir).Order())
        {
            var defsRoot = Path.Combine(pack, "Defs");

            if (!Directory.Exists(defsRoot))
            {
                continue;
            }

            Append(digest, Path.GetFileName(pack));

            // 逐一納入 Def 檔案的相對路徑、大小與寫入時間。
            // 這樣新增、刪除或修改任何一個 Def 檔都會改變指紋。
            foreach (var file in Directory.EnumerateFiles(defsRoot, "*.xml", SearchOption.AllDirectories).Order())
            {
                var info = new FileInfo(file);
                Append(digest, $"{Path.GetRelativePath(paths.DataDir, file).Replace('\\', '/')}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
            }
        }

        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static void AppendFile(IncrementalHash digest, string path)
    {
        var info = new FileInfo(path);

        if (info.Exists)
        {
            Append(digest, $"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        }
    }

    private static void Append(IncrementalHash digest, string value)
        => digest.AppendData(Encoding.UTF8.GetBytes(value));
}
