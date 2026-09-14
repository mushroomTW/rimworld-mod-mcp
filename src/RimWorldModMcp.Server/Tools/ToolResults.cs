using System.Text.Json.Serialization;

namespace RimWorldModMcp.Server.Tools;

// MCP 工具的回傳一律用具名 record，而不是裸的陣列。
// 具名型別才能產生有意義的 outputSchema，也才有地方放 total_matched／truncated
// 這類讓呼叫端知道「有沒有被截斷」的欄位。
//
// 所有欄位名以 JsonPropertyName 明確標成 snake_case，不依賴序列化器的命名策略——
// schema 與實際輸出必須一致，這是工具契約的一部分。

/// <summary>偵測到的 RimWorld 路徑。</summary>
public sealed record RimWorldStatusResult
{
    [JsonPropertyName("detected")]
    public required bool Detected { get; init; }

    [JsonPropertyName("install_root")]
    public string? InstallRoot { get; init; }

    [JsonPropertyName("executable")]
    public string? Executable { get; init; }

    [JsonPropertyName("managed_dir")]
    public string? ManagedDir { get; init; }

    [JsonPropertyName("data_dir")]
    public string? DataDir { get; init; }

    [JsonPropertyName("mods_dir")]
    public string? ModsDir { get; init; }

    [JsonPropertyName("workshop_dir")]
    public string? WorkshopDir { get; init; }

    [JsonPropertyName("player_log")]
    public string? PlayerLog { get; init; }

    [JsonPropertyName("mods_config")]
    public string? ModsConfig { get; init; }

    [JsonPropertyName("prefs_xml")]
    public string? PrefsXml { get; init; }

    [JsonPropertyName("bridge_port")]
    public required int BridgePort { get; init; }

    [JsonPropertyName("index")]
    public required IndexStatusPayload Index { get; init; }
}

/// <summary>索引狀態。</summary>
public sealed record IndexStatusPayload
{
    [JsonPropertyName("fresh")]
    public required bool Fresh { get; init; }

    [JsonPropertyName("def_count")]
    public required long DefCount { get; init; }

    [JsonPropertyName("symbol_count")]
    public required long SymbolCount { get; init; }

    /// <summary>
    /// 反編譯後的原始碼是否已索引完成。
    /// 為 false 時 <c>search_source</c> 沒有東西可查——那一層是背景執行的。
    /// </summary>
    [JsonPropertyName("source_indexed")]
    public required bool SourceIndexed { get; init; }

    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }
}

/// <summary>索引重建的結果。</summary>
public sealed record RebuildIndexResult
{
    [JsonPropertyName("def_count")]
    public required int DefCount { get; init; }

    [JsonPropertyName("symbol_count")]
    public required int SymbolCount { get; init; }

    [JsonPropertyName("reference_count")]
    public required long ReferenceCount { get; init; }

    [JsonPropertyName("assemblies")]
    public required IReadOnlyList<string> Assemblies { get; init; }

    [JsonPropertyName("elapsed_ms")]
    public required long ElapsedMilliseconds { get; init; }

    [JsonPropertyName("source_indexing_started")]
    public required bool SourceIndexingStarted { get; init; }
}

/// <summary>Def 搜尋結果。</summary>
public sealed record SearchDefsResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<DefSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    /// <summary>命中數是否達到上限（代表可能還有更多）。</summary>
    [JsonPropertyName("limit_reached")]
    public required bool LimitReached { get; init; }
}

/// <summary>單筆 Def 的摘要。</summary>
public sealed record DefSummary
{
    [JsonPropertyName("def_name")]
    public string? DefName { get; init; }

    [JsonPropertyName("def_type")]
    public required string DefType { get; init; }

    [JsonPropertyName("pack")]
    public required string Pack { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("file_path")]
    public required string FilePath { get; init; }

    [JsonPropertyName("abstract")]
    public required bool Abstract { get; init; }

    /// <summary>抽象 Def 的繼承錨點名稱。</summary>
    [JsonPropertyName("inherit_name")]
    public string? InheritName { get; init; }

    [JsonPropertyName("parent_name")]
    public string? ParentName { get; init; }

    [JsonPropertyName("xml")]
    public string? Xml { get; init; }

    [JsonPropertyName("xml_truncated")]
    public bool? XmlTruncated { get; init; }
}

/// <summary>完整 Def 定義。</summary>
public sealed record ReadDefResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<DefSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

/// <summary>符號查詢結果。</summary>
public sealed record ReadSymbolResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<SymbolSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

/// <summary>單一符號。</summary>
public sealed record SymbolSummary
{
    [JsonPropertyName("fqn")]
    public required string Fqn { get; init; }

    [JsonPropertyName("short_name")]
    public required string ShortName { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("assembly")]
    public required string Assembly { get; init; }

    /// <summary>組件檔案的實際路徑；Mod 符號靠它辨認是哪個版本目錄下的 DLL。</summary>
    [JsonPropertyName("assembly_path")]
    public string? AssemblyPath { get; init; }

    [JsonPropertyName("parent_fqn")]
    public string? ParentFqn { get; init; }

    /// <summary>由 IL metadata 組出的真實型別簽章。</summary>
    [JsonPropertyName("signature")]
    public required string Signature { get; init; }

    /// <summary>由近到遠的完整基底型別鏈。</summary>
    [JsonPropertyName("base_chain")]
    public required IReadOnlyList<string> BaseChain { get; init; }

    [JsonPropertyName("interfaces")]
    public required IReadOnlyList<string> Interfaces { get; init; }

    [JsonPropertyName("accessibility")]
    public required string Accessibility { get; init; }

    [JsonPropertyName("is_static")]
    public required bool IsStatic { get; init; }

    /// <summary>反編譯出的原始碼。只有在要求時才會填入。</summary>
    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("body_truncated")]
    public bool? BodyTruncated { get; init; }
}

/// <summary>繼承查詢結果。</summary>
public sealed record FindDescendantsResult
{
    [JsonPropertyName("base_type")]
    public required string BaseType { get; init; }

    [JsonPropertyName("results")]
    public required IReadOnlyList<SymbolSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

/// <summary>符號瀏覽結果。</summary>
public sealed record ListSymbolsResult
{
    [JsonPropertyName("parent")]
    public required string Parent { get; init; }

    [JsonPropertyName("results")]
    public required IReadOnlyList<SymbolSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("limit_reached")]
    public required bool LimitReached { get; init; }

    /// <summary>parent 是 namespace 時，其下一層的子 namespace。</summary>
    [JsonPropertyName("child_namespaces")]
    public required IReadOnlyList<string> ChildNamespaces { get; init; }
}

/// <summary>反編譯檔的一段內容。</summary>
public sealed record ReadSourceFileResult
{
    [JsonPropertyName("assembly")]
    public required string Assembly { get; init; }

    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("start_line")]
    public required int StartLine { get; init; }

    /// <summary>節錄裡最後一個完整行；下一頁從 end_line + 1 開始。</summary>
    [JsonPropertyName("end_line")]
    public required int EndLine { get; init; }

    [JsonPropertyName("total_lines")]
    public required int TotalLines { get; init; }

    [JsonPropertyName("truncated")]
    public required bool Truncated { get; init; }
}

/// <summary>原始碼搜尋結果。</summary>
public sealed record SearchSourceResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<SourceMatch> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("limit_reached")]
    public required bool LimitReached { get; init; }

    /// <summary>原始碼索引尚未完成時為 false，此時結果必然不完整。</summary>
    [JsonPropertyName("source_indexed")]
    public required bool SourceIndexed { get; init; }
}

/// <summary>原始碼中的一行命中。</summary>
public sealed record SourceMatch
{
    [JsonPropertyName("assembly")]
    public required string Assembly { get; init; }

    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("line")]
    public required int Line { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>Def 引用查詢結果。</summary>
public sealed record FindDefUsagesResult
{
    [JsonPropertyName("def_name")]
    public required string DefName { get; init; }

    [JsonPropertyName("results")]
    public required IReadOnlyList<DefUsageSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

/// <summary>Def 被引用的一個位置。</summary>
public sealed record DefUsageSummary
{
    [JsonPropertyName("file_path")]
    public required string FilePath { get; init; }

    [JsonPropertyName("line")]
    public required int Line { get; init; }

    /// <summary><c>def_xml</c>、<c>game_source</c> 或 <c>mod_source</c>。</summary>
    [JsonPropertyName("source_kind")]
    public required string SourceKind { get; init; }

    /// <summary>引用出現的語境，例如 <c>ThingDef/costList/Steel</c> 或 <c>RimWorld.ThingDefOf.Steel</c>。</summary>
    [JsonPropertyName("context")]
    public string? Context { get; init; }

    /// <summary><c>exact</c>（結構化來源）或 <c>heuristic</c>（字串比對推測）。</summary>
    [JsonPropertyName("confidence")]
    public required string Confidence { get; init; }
}
