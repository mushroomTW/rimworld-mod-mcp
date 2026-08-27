using System.Xml;
using System.Xml.Linq;
using RimWorldModMcp.Indexing.Model;
using RimWorldModMcp.Indexing.Semantics;

namespace RimWorldModMcp.Indexing.Defs;

/// <summary>
/// <c>Data/</c> 的單趟內容掃描：每個檔案讀一次、解析一次，同時產出
/// Def 記錄與引用候選。
///
/// <para>
/// 過去 Def 掃描與引用分析各自走訪並解析同一批 XML。引用分析需要完整的
/// defName 名冊才能過濾，而名冊要等掃描結束才存在——這裡的解法是候選
/// 先不過濾（經 <paramref name="emitCandidate"/> 落進 SQLite 暫存表），
/// 最後用一次 join 收斂，因此內容只需要過一遍。
/// </para>
/// </summary>
internal static class DefDataScan
{
    /// <summary>
    /// Def 記錄以串流 yield（呼叫端邊列舉邊寫入 DB，不用整批留在記憶體）；
    /// 每個檔案 yield 完 Def 之後立即收集它的引用候選。
    /// </summary>
    internal static IEnumerable<DefRecord> Run(
        IReadOnlyList<DefPack> packs,
        Action<DefReferenceCandidate> emitCandidate)
    {
        foreach (var pack in packs)
        {
            foreach (var file in pack.Files)
            {
                XDocument document;

                try
                {
                    // 用檔案串流讓 XML 宣告決定編碼——RimWorld 的 Def 檔有些帶 BOM、
                    // 有些是 UTF-16。SetLineInfo 供引用候選記錄行號。
                    using var stream = File.OpenRead(file.AbsolutePath);
                    document = XDocument.Load(stream, LoadOptions.SetLineInfo);
                }
                catch (Exception e) when (e is IOException or XmlException or UnauthorizedAccessException)
                {
                    // 無法解析的檔案安靜略過，與逐檔掃描的既有行為一致。
                    continue;
                }

                var root = document.Root;

                // 只有根節點是 <Defs> 的才是定義檔。
                if (root is null || root.Name.LocalName != "Defs")
                {
                    continue;
                }

                foreach (var record in DefXmlScanner.ParseElements(root, pack.Name, file.RelativePath))
                {
                    yield return record;
                }

                DefReferenceAnalyzer.CollectCandidates(root, file.RelativePath, emitCandidate);
            }
        }
    }
}
