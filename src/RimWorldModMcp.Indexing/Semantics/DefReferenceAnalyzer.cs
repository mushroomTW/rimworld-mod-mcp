using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using RimWorldModMcp.Indexing.Model;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Semantics;

/// <summary>
/// 找出 Def 被引用的位置。
///
/// <para>
/// Python 版的作法是對每一行反編譯後的 C# 跑
/// <c>"([A-Z][A-Za-z0-9_]{2,})"</c> 這個 regex 抓字串字面量，再比對已知 defName。
/// 那個作法有兩個問題：會誤中註解與無關字串，而且完全抓不到
/// <c>ThingDefOf.Steel</c> 這種 RimWorld 最常見的引用形式——它根本不是字串。
/// </para>
/// <para>
/// 這裡改用兩條精確的路徑：符號表裡的 <c>*DefOf</c> 靜態欄位，以及 Def XML
/// 之間的交叉引用。兩者都是結構化資料，不需要猜。
/// </para>
/// </summary>
public sealed class DefReferenceAnalyzer
{
    /// <summary>
    /// 從符號表找出所有 <c>*DefOf</c> 類別的靜態欄位引用。
    ///
    /// <para>
    /// RimWorld 用 <c>[DefOf]</c> 標記的靜態類別（<c>ThingDefOf</c>、<c>JobDefOf</c>…）
    /// 在載入時由遊戲把同名的 Def 填進同名欄位。因此「欄位名」就是「defName」，
    /// 這是編譯期就確定的對應關係，不需要語意分析也不需要猜。
    /// </para>
    /// </summary>
    public IReadOnlyList<DefReference> FromDefOfFields(SqliteConnection connection, ISet<string> knownDefNames)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT parent_fqn, short_name, assembly
            FROM symbol
            WHERE kind = 'Field'
              AND is_static = 1
              AND parent_fqn LIKE '%DefOf'
            ORDER BY parent_fqn, short_name;
            """;

        var references = new List<DefReference>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var owner = reader.GetString(0);
            var fieldName = reader.GetString(1);

            if (!knownDefNames.Contains(fieldName))
            {
                continue;
            }

            references.Add(new DefReference(
                DefName: fieldName,
                FilePath: owner,
                Line: 0,
                SourceKind: DefReferenceSource.GameSource,
                Context: $"{owner}.{fieldName}",
                Confidence: DefReferenceConfidence.Exact));
        }

        return references;
    }

    /// <summary>
    /// 掃描 Def XML 之間的交叉引用。
    ///
    /// <para>
    /// Def 彼此以 defName 相互指涉——<c>&lt;costList&gt;&lt;Steel&gt;10&lt;/Steel&gt;</c>、
    /// <c>&lt;researchPrerequisites&gt;&lt;li&gt;Smithing&lt;/li&gt;</c>、
    /// <c>&lt;ParentName="BaseGun"&gt;</c> 等等。這些都是結構化位置，
    /// 元素名稱本身就是語境，不需要靠字串比對猜測。
    /// </para>
    /// </summary>
    public IReadOnlyList<DefReference> FromDefXml(string dataDirectory, ISet<string> knownDefNames)
    {
        var references = new List<DefReference>();

        foreach (var pack in Directory.GetDirectories(dataDirectory).Order())
        {
            var defsRoot = Path.Combine(pack, "Defs");

            if (!Directory.Exists(defsRoot))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(defsRoot, "*.xml", SearchOption.AllDirectories))
            {
                ScanFile(file, dataDirectory, knownDefNames, references);
            }
        }

        return references;
    }

    private static void ScanFile(string path, string relativeTo, ISet<string> knownDefNames, List<DefReference> references)
    {
        XDocument document;

        try
        {
            using var stream = File.OpenRead(path);
            document = XDocument.Load(stream, LoadOptions.SetLineInfo);
        }
        catch (Exception e) when (e is IOException or XmlException or UnauthorizedAccessException)
        {
            return;
        }

        if (document.Root is null || document.Root.Name.LocalName != "Defs")
        {
            return;
        }

        var filePath = Path.GetRelativePath(relativeTo, path).Replace('\\', '/');

        foreach (var element in document.Root.Descendants())
        {
            // 自己的 defName 宣告不算引用。
            if (element.Name.LocalName == "defName")
            {
                continue;
            }

            // 情況一：元素名稱本身就是 defName，例如 <costList><Steel>10</Steel>。
            if (knownDefNames.Contains(element.Name.LocalName))
            {
                Add(references, element.Name.LocalName, filePath, element, ElementPath(element));
            }

            // 情況二：元素的文字內容是 defName，例如 <li>Smithing</li>。
            if (!element.HasElements)
            {
                var value = element.Value.Trim();

                if (value.Length > 0 && knownDefNames.Contains(value))
                {
                    Add(references, value, filePath, element, ElementPath(element));
                }
            }

            // 情況三：ParentName 屬性指向抽象 Def 的 Name。
            var parentName = element.Attribute("ParentName")?.Value;

            if (parentName is not null && knownDefNames.Contains(parentName))
            {
                Add(references, parentName, filePath, element, "ParentName");
            }
        }
    }

    private static void Add(List<DefReference> references, string defName, string filePath, XElement element, string context)
    {
        var line = element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

        references.Add(new DefReference(
            DefName: defName,
            FilePath: filePath,
            Line: line,
            SourceKind: DefReferenceSource.DefXml,
            Context: context,
            Confidence: DefReferenceConfidence.Exact));
    }

    /// <summary>組出可讀的語境字串，例如 <c>ThingDef/costList/Steel</c>。</summary>
    private static string ElementPath(XElement element)
    {
        var parts = new List<string>();
        var current = element;
        var depth = 0;

        while (current is not null && depth++ < 4)
        {
            parts.Insert(0, current.Name.LocalName);

            current = current.Parent;

            if (current is null || current.Name.LocalName == "Defs")
            {
                break;
            }
        }

        return string.Join('/', parts);
    }
}
