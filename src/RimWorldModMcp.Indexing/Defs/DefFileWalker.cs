namespace RimWorldModMcp.Indexing.Defs;

/// <summary>Defs/ 底下的一個 XML 檔與它的戳記。</summary>
internal sealed record DefFile(string AbsolutePath, string RelativePath, long Length, long LastWriteTicks);

/// <summary>一個 pack（Core 或 DLC）的 Def 檔清單。</summary>
internal sealed record DefPack(string Name, IReadOnlyList<DefFile> Files);

/// <summary>
/// Data/ 的單一走訪來源。
///
/// <para>
/// Def 掃描、引用分析與指紋計算過去各自走訪一次 Data/——兩萬個檔案
/// 走三遍、其中兩遍還各自解析一次 XML。現在三者都吃這裡的同一份清單：
/// 順序固定（pack 與檔案都排序），指紋的雜湊輸入才不會因走訪方式而漂移。
/// </para>
/// </summary>
internal static class DefFileWalker
{
    internal static IReadOnlyList<DefPack> Walk(string dataDirectory)
    {
        var packs = new List<DefPack>();

        foreach (var pack in Directory.GetDirectories(dataDirectory).Order())
        {
            var defsRoot = Path.Combine(pack, "Defs");

            if (!Directory.Exists(defsRoot))
            {
                continue;
            }

            var files = new List<DefFile>();

            foreach (var file in Directory.EnumerateFiles(defsRoot, "*.xml", SearchOption.AllDirectories).Order())
            {
                var info = new FileInfo(file);

                files.Add(new DefFile(
                    file,
                    Path.GetRelativePath(dataDirectory, file).Replace('\\', '/'),
                    info.Length,
                    info.LastWriteTimeUtc.Ticks));
            }

            packs.Add(new DefPack(Path.GetFileName(pack), files));
        }

        return packs;
    }
}
