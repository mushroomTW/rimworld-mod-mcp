using System.Text.RegularExpressions;
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
/// "([A-Z][A-Za-z0-9_]{2,})" 這個 regex 抓字串字面量，再比對已知 defName。
/// 那個作法有兩個問題：會誤中註解與無關字串，而且完全抓不到
/// ThingDefOf.Steel 這種 RimWorld 最常見的引用形式——它根本不是字串。
/// </para>
/// <para>
/// 這裡改用兩條精確的路徑：符號表裡的 *DefOf 靜態欄位，以及 Def XML
/// 之間的交叉引用。兩者都是結構化資料，不需要猜。
/// </para>
/// </summary>
public static class DefReferenceAnalyzer
{
    /// <summary>
    /// 從符號表找出所有 *DefOf 類別的靜態欄位引用。
    ///
    /// <para>
    /// RimWorld 用 [DefOf] 標記的靜態類別（ThingDefOf、JobDefOf…）
    /// 在載入時由遊戲把同名的 Def 填進同名欄位。因此「欄位名」就是「defName」，
    /// 這是編譯期就確定的對應關係，不需要語意分析也不需要猜。
    /// </para>
    /// </summary>
    public static IReadOnlyList<DefReference> FromDefOfFields(SqliteConnection connection, ISet<string> knownDefNames)
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
    /// 從已解析的 ＜Defs＞ 根節點收集 Def XML 交叉引用的候選。
    ///
    /// <para>
    /// Def 彼此以 defName 相互指涉——＜costList＞＜Steel＞10＜/Steel＞、
    /// ＜researchPrerequisites＞＜li＞Smithing＜/li＞、
    /// ＜ParentName="BaseGun"＞ 等等。這些都是結構化位置，
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

    /// <summary>
    /// 原始碼搜尋用的 regex：找 <c>XxxDefOf.＜defName＞</c> 形式的使用處。
    /// 原始碼搜尋一律不分大小寫，命中後要再以 <see cref="IsDefOfUsage"/> 精確比對。
    /// </summary>
    public static string DefOfUsagePattern(string defName) => $@"DefOf\.{Regex.Escape(defName)}\b";

    /// <summary>這一行是否以 <c>XxxDefOf.＜defName＞</c> 引用該 Def（區分大小寫，與 C# 一致）。</summary>
    public static bool IsDefOfUsage(string line, string defName)
        => Regex.IsMatch(line, $@"\b\w*DefOf\.{Regex.Escape(defName)}\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// 當場掃描一個 Mod 的 Def XML，找出引用 <paramref name="defName"/> 的位置。
    ///
    /// <para>
    /// Mod 的 Def 沒有建索引，所以不能靠名冊 join 過濾：直接比對要找的 defName。
    /// RimWorld 只從載入資料夾底下的 Defs/ 讀定義，所以只看路徑經過 Defs 資料夾的檔案
    /// （Languages、Patches 動輒上千個 XML，不必讀）；其中不含該字串的再跳過不解析，
    /// 根節點不是 ＜Defs＞ 的也不算。檔案路徑相對於 Mod 目錄，版本資料夾（例如 1.6/）從路徑就看得出來。
    /// </para>
    /// </summary>
    public static IReadOnlyList<DefReferenceCandidate> FindInModXml(string modPath, string defName, int limit)
    {
        var found = new List<DefReferenceCandidate>();

        foreach (var file in Directory.EnumerateFiles(modPath, "*.xml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(modPath, file).Replace('\\', '/');

            if (!relative.Split('/').SkipLast(1).Contains("Defs", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            XElement? root;

            try
            {
                var text = File.ReadAllText(file);

                if (!text.Contains(defName, StringComparison.Ordinal))
                {
                    continue;
                }

                root = XDocument.Parse(text, LoadOptions.SetLineInfo).Root;
            }
            catch (Exception e) when (e is IOException or XmlException or UnauthorizedAccessException)
            {
                continue;
            }

            if (root is null || root.Name.LocalName != "Defs")
            {
                continue;
            }

            CollectCandidates(root, relative, candidate =>
            {
                if (found.Count < limit && candidate.DefName == defName)
                {
                    found.Add(candidate);
                }
            });

            if (found.Count >= limit)
            {
                break;
            }
        }

        return found;
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

    /// <summary>組出可讀的語境字串，例如 ThingDef/costList/Steel。</summary>
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
