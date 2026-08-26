using System.Xml;
using System.Xml.Linq;
using RimWorldModMcp.Indexing.Model;

namespace RimWorldModMcp.Indexing.Defs;

/// <summary>掃描並解析 RimWorld 的 Def XML。</summary>
public sealed class DefXmlScanner
{
    /// <summary>
    /// 掃描 <c>Data/</c> 底下所有 pack（Core 與各 DLC）的 Def 定義。
    /// </summary>
    public IEnumerable<DefRecord> Scan(string dataDirectory)
    {
        foreach (var pack in Directory.GetDirectories(dataDirectory).Order())
        {
            var defsRoot = Path.Combine(pack, "Defs");

            if (!Directory.Exists(defsRoot))
            {
                continue;
            }

            var packName = Path.GetFileName(pack);

            foreach (var file in Directory.EnumerateFiles(defsRoot, "*.xml", SearchOption.AllDirectories))
            {
                foreach (var record in ParseFile(file, dataDirectory, packName))
                {
                    yield return record;
                }
            }
        }
    }

    /// <summary>解析單一 XML 檔中的所有 Def。無法解析的檔案會被安靜略過。</summary>
    public IEnumerable<DefRecord> ParseFile(string path, string relativeTo, string packName)
    {
        XDocument document;

        try
        {
            // 用檔案串流讓 XML 宣告決定編碼——RimWorld 的 Def 檔有些帶 BOM、
            // 有些是 UTF-16，用 ReadAllText 再 Parse 會在那些檔案上失敗。
            using var stream = File.OpenRead(path);
            document = XDocument.Load(stream, LoadOptions.None);
        }
        catch (Exception e) when (e is IOException or XmlException or UnauthorizedAccessException)
        {
            yield break;
        }

        var root = document.Root;

        // 只有根節點是 <Defs> 的才是定義檔。Patches/ 底下的 PatchOperation、
        // About.xml、語言檔都會在這裡被擋掉。
        if (root is null || root.Name.LocalName != "Defs")
        {
            yield break;
        }

        var filePath = Path.GetRelativePath(relativeTo, path).Replace('\\', '/');

        foreach (var node in root.Elements())
        {
            yield return Parse(node, packName, filePath);
        }
    }

    private static DefRecord Parse(XElement node, string packName, string filePath)
    {
        // 元素標籤名就是 Def 的型別（ThingDef、PawnKindDef…）。
        var defType = node.Name.LocalName;

        var defName = node.Element("defName")?.Value.Trim();
        if (string.IsNullOrEmpty(defName))
        {
            defName = null;
        }

        // 抽象 Def 沒有 defName，改用 Name 屬性當繼承錨點。
        var inheritName = node.Attribute("Name")?.Value;
        var parentName = node.Attribute("ParentName")?.Value;

        var abstractValue = node.Attribute("Abstract")?.Value ?? "false";
        var isAbstract = string.Equals(abstractValue, "true", StringComparison.OrdinalIgnoreCase);

        return new DefRecord(
            Pack: packName,
            DefType: defType,
            DefName: defName,
            InheritName: inheritName,
            ParentName: parentName,
            Abstract: isAbstract,
            Label: node.Element("label")?.Value.Trim() ?? string.Empty,
            Description: node.Element("description")?.Value.Trim() ?? string.Empty,
            FilePath: filePath,
            Xml: node.ToString(SaveOptions.None));
    }
}
