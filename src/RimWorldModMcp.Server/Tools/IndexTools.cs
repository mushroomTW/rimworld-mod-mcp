using System.ComponentModel;
using ModelContextProtocol.Server;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Indexing.Decompilation;
using RimWorldModMcp.Indexing.Pipeline;
using RimWorldModMcp.Indexing.Query;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Server.Tools;

// 參數名刻意寫成 snake_case：MCP SDK 由 C# 參數名推導 inputSchema，
// 而工具契約規定所有欄位一律 snake_case。這是零風險的作法——
// 不依賴序列化器的命名策略是否套用到參數上。
#pragma warning disable IDE1006 // 命名樣式

/// <summary>研究 RimWorld 的 Def 與 C# API 的工具。</summary>
[McpServerToolType]
public sealed class IndexTools(
    IRimWorldLocator locator,
    IndexDatabase database,
    IndexBuilder builder,
    SourceIndexer sourceIndexer,
    SourceQueryService sourceQueries,
    IMemberDecompiler decompiler)
{
    [McpServerTool(Name = "rimworld_status", UseStructuredContent = true, ReadOnly = true)]
    [Description("回報偵測到的 RimWorld 安裝路徑與索引狀態。開始任何工作前先呼叫這個確認環境就緒。")]
    public RimWorldStatusResult RimWorldStatus() => ToolGuard.Run(() =>
    {
        var paths = locator.Detect();
        var status = builder.Status();

        return new RimWorldStatusResult
        {
            Detected = paths.HasManagedAndData,
            InstallRoot = paths.InstallRoot,
            Executable = paths.Executable,
            ManagedDir = paths.ManagedDir,
            DataDir = paths.DataDir,
            ModsDir = paths.ModsDir,
            WorkshopDir = paths.WorkshopDir,
            PlayerLog = paths.PlayerLog,
            ModsConfig = paths.ModsConfig,
            PrefsXml = paths.PrefsXml,
            BridgePort = locator.BridgePort(),
            Index = new IndexStatusPayload
            {
                Fresh = status.Fresh,
                DefCount = status.DefCount,
                SymbolCount = status.SymbolCount,
                SourceIndexed = status.SourceIndexed,
                Fingerprint = status.Fingerprint,
            },
        };
    });

    [McpServerTool(Name = "rebuild_index", UseStructuredContent = true)]
    [Description("重建 Def 與 C# 符號索引。讀取遊戲的 IL metadata，數秒內完成。原始碼全文索引會在背景繼續進行。")]
    public RebuildIndexResult RebuildIndex(
        [Description("是否同時在背景建立原始碼全文索引（search_source 需要）。預設 true。")]
        bool index_source = true) => ToolGuard.Run(() =>
    {
        var result = builder.Rebuild();

        if (index_source)
        {
            sourceIndexer.StartInBackground();
        }

        return new RebuildIndexResult
        {
            DefCount = result.DefCount,
            SymbolCount = result.SymbolCount,
            ReferenceCount = result.ReferenceCount,
            Assemblies = result.Assemblies,
            ElapsedMilliseconds = result.ElapsedMilliseconds,
            SourceIndexingStarted = index_source,
        };
    });

    [McpServerTool(Name = "search_defs", UseStructuredContent = true, ReadOnly = true)]
    [Description("以名稱、標籤或描述搜尋已索引的 Def。預設不回傳 XML 內容以節省輸出；需要完整定義請用 read_def。")]
    public SearchDefsResult SearchDefs(
        [Description("搜尋關鍵字。詞尾加 * 表示前綴搜尋，例如 Gun*。")]
        string query,
        [Description("限定 Def 型別，例如 ThingDef、PawnKindDef。留空表示不限。")]
        string? def_type = null,
        [Description("最多回傳幾筆，範圍 1-200。")]
        int limit = 25,
        [Description("是否附上截斷過的 XML 內容。")]
        bool include_xml = false) => ToolGuard.Run(() =>
    {
        using var connection = database.Open();
        var hits = DefRepository.Search(connection, query, def_type, limit, include_xml);
        var effectiveLimit = Math.Clamp(limit, 1, 200);

        return new SearchDefsResult
        {
            Results = [.. hits.Select(ToSummary)],
            Count = hits.Count,
            LimitReached = hits.Count >= effectiveLimit,
        };
    });

    [McpServerTool(Name = "read_def", UseStructuredContent = true, ReadOnly = true)]
    [Description("讀取單一 Def 的完整 XML 定義。抽象 Def 請用它的 Name 屬性值查詢。")]
    public ReadDefResult ReadDef(
        [Description("Def 的 defName，或抽象 Def 的 Name 屬性值。")]
        string def_name,
        [Description("限定 Def 型別。留空表示不限。")]
        string? def_type = null,
        [Description("XML 內容的位元組上限，範圍 1024-262144。")]
        int max_bytes = 65536) => ToolGuard.Run(() =>
    {
        using var connection = database.Open();
        var hits = DefRepository.Read(connection, def_name, def_type, max_bytes);

        return new ReadDefResult
        {
            Results = [.. hits.Select(ToSummary)],
            Count = hits.Count,
        };
    });

    [McpServerTool(Name = "read_symbol", UseStructuredContent = true, ReadOnly = true)]
    [Description("查詢 C# 符號的簽章、完整繼承鏈與介面實作，可選擇一併反編譯出原始碼。")]
    public ReadSymbolResult ReadSymbol(
        [Description("符號的短名或完整名稱的一部分，例如 ThingDef 或 Verse.ThingDef。")]
        string name,
        [Description("是否反編譯出原始碼。首次呼叫會花數秒初始化反編譯器。")]
        bool include_body = false,
        [Description("原始碼節錄的位元組上限，範圍 256-32768。")]
        int max_bytes = 4096,
        [Description("最多回傳幾筆，範圍 1-100。")]
        int limit = 20) => ToolGuard.Run(() =>
    {
        using var connection = database.Open();
        var hits = SymbolRepository.Read(connection, name, limit);

        if (!include_body)
        {
            return new ReadSymbolResult { Results = [.. hits.Select(ToSummary)], Count = hits.Count };
        }

        var managed = locator.Detect().ManagedDir;
        var results = new List<SymbolSummary>(hits.Count);

        foreach (var hit in hits)
        {
            string? body = null;
            bool? truncated = null;

            // 優先用索引裡記錄的實際組件路徑——Mod 組件的 assembly 欄位是
            // mod:pkg:Name:hash 形式的索引鍵，拼 Managed\<key>.dll 出來的
            // 根本不是合法路徑。
            var assemblyPath = hit.AssemblyPath
                ?? (managed is not null ? Path.Combine(managed, hit.Assembly + ".dll") : null);

            if (assemblyPath is not null && File.Exists(assemblyPath))
            {
                var source = decompiler.DecompileMember(assemblyPath, hit.MetadataToken);

                if (source is not null)
                {
                    body = Utf8Text.Truncate(source, Utf8Text.Clamp(max_bytes, 256, 32768), out var wasTruncated);
                    truncated = wasTruncated;
                }
            }

            results.Add(ToSummary(hit) with { Body = body, BodyTruncated = truncated });
        }

        return new ReadSymbolResult { Results = results, Count = results.Count };
    });

    [McpServerTool(Name = "find_descendants", UseStructuredContent = true, ReadOnly = true)]
    [Description("找出所有繼承自指定型別的類別。用來回答「哪些類別繼承 ThingComp」這類問題。")]
    public FindDescendantsResult FindDescendants(
        [Description("基底型別的完整名稱，例如 Verse.ThingComp。")]
        string base_type,
        [Description("最多回傳幾筆，範圍 1-500。")]
        int limit = 100) => ToolGuard.Run(() =>
    {
        using var connection = database.Open();
        var hits = SymbolRepository.Descendants(connection, base_type, limit);

        return new FindDescendantsResult
        {
            BaseType = base_type,
            Results = [.. hits.Select(ToSummary)],
            Count = hits.Count,
        };
    });

    [McpServerTool(Name = "search_source", UseStructuredContent = true, ReadOnly = true)]
    [Description("以正規表示式搜尋反編譯後的遊戲原始碼。需要原始碼索引完成；未完成時 source_indexed 會是 false。")]
    public SearchSourceResult SearchSource(
        [Description("正規表示式（.NET 語法），一律不分大小寫。")]
        string pattern,
        [Description("限定檔案路徑樣式，例如 RimWorld/*.cs。留空表示不限。")]
        string? file_pattern = null,
        [Description("最多回傳幾筆，範圍 1-800。")]
        int limit = 200) => ToolGuard.Run(() =>
    {
        using var connection = database.Open();
        var hits = sourceQueries.Search(connection, pattern, file_pattern, limit);

        return new SearchSourceResult
        {
            Results = [.. hits.Select(h => new SourceMatch
            {
                Assembly = h.Assembly,
                File = h.File,
                Line = h.Line,
                Text = h.Text,
            })],
            Count = hits.Count,
            LimitReached = hits.Count >= Math.Clamp(limit, 1, 800),
            SourceIndexed = IndexMetaRepository.Get(connection, "source_indexed") == "true",
        };
    });

    [McpServerTool(Name = "find_def_usages", UseStructuredContent = true, ReadOnly = true)]
    [Description("找出一個 Def 被引用的位置，包含 Def XML 的交叉引用與 C# 的 DefOf 靜態欄位。")]
    public FindDefUsagesResult FindDefUsages(
        [Description("要查詢的 defName。")]
        string def_name,
        [Description("限定來源類別：def_xml、game_source 或 mod_source。留空表示不限。")]
        string? source_kind = null,
        [Description("最多回傳幾筆，範圍 1-500。")]
        int limit = 100) => ToolGuard.Run(() =>
    {
        using var connection = database.Open();
        var usages = DefReferenceRepository.Find(connection, def_name, source_kind, limit);

        return new FindDefUsagesResult
        {
            DefName = def_name,
            Results = [.. usages.Select(u => new DefUsageSummary
            {
                FilePath = u.FilePath,
                Line = u.Line,
                SourceKind = u.SourceKind,
                Context = u.Context,
                Confidence = u.Confidence,
            })],
            Count = usages.Count,
        };
    });

    private static DefSummary ToSummary(RimWorldModMcp.Indexing.Model.DefHit hit) => new()
    {
        DefName = hit.DefName,
        DefType = hit.DefType,
        Pack = hit.Pack,
        Label = hit.Label,
        Description = hit.Description,
        FilePath = hit.FilePath,
        Abstract = hit.Abstract,
        InheritName = hit.InheritName,
        ParentName = hit.ParentName,
        Xml = hit.Xml,
        XmlTruncated = hit.XmlTruncated,
    };

    private static SymbolSummary ToSummary(SymbolHit hit) => new()
    {
        Fqn = hit.Fqn,
        ShortName = hit.ShortName,
        Kind = hit.Kind,
        Assembly = hit.Assembly,
        ParentFqn = hit.ParentFqn,
        Signature = hit.Signature,
        BaseChain = hit.BaseChain,
        Interfaces = hit.Interfaces,
        Accessibility = hit.Accessibility,
        IsStatic = hit.IsStatic,
    };
}
