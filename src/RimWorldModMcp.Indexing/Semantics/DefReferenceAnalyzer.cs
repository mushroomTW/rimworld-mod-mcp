using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using RimWorldModMcp.Indexing.Model;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Semantics;

/// <summary>一筆尚未經名冊過濾的 Def XML 引用候選。</summary>
public sealed record DefReferenceCandidate(string DefName, string FilePath, int Line, string Context);

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
    /// 從已解析的 <c>&lt;Defs&gt;</c> 根節點收集 Def XML 交叉引用的<b>候選</b>。
    ///
    /// <para>
    /// Def 彼此以 defName 相互指涉——<c>&lt;costList&gt;&lt;Steel&gt;10&lt;/Steel&gt;</c>、
    /// <c>&lt;researchPrerequisites&gt;&lt;li&gt;Smithing&lt;/li&gt;</c>、
    /// <c>&lt;ParentName="BaseGun"&gt;</c> 等等。這些都是結構化位置，
    /// 元素名稱本身就是語境，不需要靠字串比對猜測。
    /// </para>
    /// <para>
    /// 這裡只做形狀過濾、不比對名冊：完整的 defName 名冊要等整個 Data/
    /// 掃完才存在，而單趟掃描的重點正是不重讀第二遍。候選由呼叫端寫進
    /// SQLite 暫存表，最後以一次 join 過濾（見
    /// <see cref="Storage.DefReferenceRepository.ResolveCandidates"/>）。
    /// </para>
    /// </summary>
    internal static void CollectCandidates(XElement root, string filePath, Action<DefReferenceCandidate> emit)
    {
        foreach (var element in root.Descendants())
        {
            // 自己的 defName 宣告不算引用。
            if (element.Name.LocalName == "defName")
            {
                continue;
            }

            // 情況一：元素名稱本身就是 defName，例如 <costList><Steel>10</Steel>。
            if (IsPlausibleDefName(element.Name.LocalName))
            {
                emit(new DefReferenceCandidate(element.Name.LocalName, filePath, Line(element), ElementPath(element)));
            }

            // 情況二：元素的文字內容是 defName，例如 <li>Smithing</li>。
            if (!element.HasElements)
            {
                var value = element.Value.Trim();

                if (value.Length > 0 && IsPlausibleDefName(value))
                {
                    emit(new DefReferenceCandidate(value, filePath, Line(element), ElementPath(element)));
                }
            }

            // 情況三：ParentName 屬性指向抽象 Def 的 Name。
            var parentName = element.Attribute("ParentName")?.Value;

            if (parentName is not null && IsPlausibleDefName(parentName))
            {
                emit(new DefReferenceCandidate(parentName, filePath, Line(element), "ParentName"));
            }
        }
    }

    private static int Line(XElement element)
        => element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

    /// <summary>
    /// defName 的形狀檢查：英數、底線、連字號，且至少含一個字母。
    /// 這只是把「10」「0.5」「true」與長句子擋在暫存表之外的粗篩，
    /// 精確過濾靠最後的名冊 join。
    /// </summary>
    private static bool IsPlausibleDefName(string value)
    {
        if (value.Length is < 2 or > 128)
        {
            return false;
        }

        var hasLetter = false;

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))
            {
                return false;
            }

            hasLetter |= char.IsAsciiLetter(c);
        }

        return hasLetter;
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
