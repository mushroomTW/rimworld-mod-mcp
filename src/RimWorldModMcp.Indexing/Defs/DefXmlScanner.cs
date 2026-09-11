using System.Xml.Linq;
using RimWorldModMcp.Indexing.Model;

namespace RimWorldModMcp.Indexing.Defs;

/// <summary>解析 RimWorld 的 Def XML 元素。</summary>
internal static class DefXmlScanner
{
    /// <summary>
    /// 從已解析好的 <c>&lt;Defs&gt;</c> 根節點抽出所有 Def。
    /// 供單趟掃描（<see cref="DefDataScan"/>）使用。
    /// </summary>
    internal static IEnumerable<DefRecord> ParseElements(XElement root, string packName, string filePath)
    {
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
