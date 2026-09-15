using System.Text.Json.Serialization;

namespace RimWorldModMcp.Server.Tools;

// MCP 工具的回傳一律用具名 record，而不是裸的陣列。
// 具名型別才能產生有意義的 outputSchema，也才有地方放 total_matched／truncated
// 這類讓呼叫端知道「有沒有被截斷」的欄位。
//
// 所有欄位名以 JsonPropertyName 明確標成 snake_case，不依賴序列化器的命名策略——
// schema 與實際輸出必須一致，這是工具契約的一部分。

/// <summary>Detected RimWorld paths.</summary>
public sealed record RimWorldStatusResult
{
    [JsonPropertyName("detected")]
    public required bool Detected { get; init; }

    [JsonPropertyName("install_root")]
    public string? InstallRoot { get; init; }

    [JsonPropertyName("mods_dir")]
    public string? ModsDir { get; init; }

    [JsonPropertyName("workshop_dir")]
    public string? WorkshopDir { get; init; }

    [JsonPropertyName("player_log")]
    public string? PlayerLog { get; init; }

    [JsonPropertyName("bridge_port")]
    public required int BridgePort { get; init; }

    [JsonPropertyName("index")]
    public required IndexStatusPayload Index { get; init; }
}

/// <summary>Index status.</summary>
public sealed record IndexStatusPayload
{
    [JsonPropertyName("fresh")]
    public required bool Fresh { get; init; }

    [JsonPropertyName("def_count")]
    public required long DefCount { get; init; }

    [JsonPropertyName("symbol_count")]
    public required long SymbolCount { get; init; }

    /// <summary>
    /// Whether the decompiled source is fully indexed.
    /// When false, <c>search_source</c> has nothing to search yet — that layer builds in the background.
    /// </summary>
    [JsonPropertyName("source_indexed")]
    public required bool SourceIndexed { get; init; }

    /// <summary>Why <c>source_indexed</c> is false: still running, failed with <c>error</c>, or never started (neither).</summary>
    [JsonPropertyName("source_index")]
    public required SourceIndexPayload SourceIndex { get; init; }
}

/// <summary>Progress of the background source index.</summary>
public sealed record SourceIndexPayload
{
    [JsonPropertyName("running")]
    public required bool Running { get; init; }

    [JsonPropertyName("indexed_files")]
    public required int IndexedFiles { get; init; }

    /// <summary>Last failure reason; call rebuild_index to retry.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>Result of rebuilding the index.</summary>
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

/// <summary>Def search result.</summary>
public sealed record SearchDefsResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<DefSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    /// <summary>Whether the hit count reached the limit (more may exist).</summary>
    [JsonPropertyName("limit_reached")]
    public required bool LimitReached { get; init; }
}

/// <summary>Summary of a single Def.</summary>
public sealed record DefSummary
{
    [JsonPropertyName("def_name")]
    public string? DefName { get; init; }

    [JsonPropertyName("def_type")]
    public required string DefType { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    /// <summary>Omitted when empty. search_defs truncates it; read_def returns it in full.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>Relative to the Data directory; the first segment is the content pack (Core, Royalty, ...).</summary>
    [JsonPropertyName("file_path")]
    public required string FilePath { get; init; }

    /// <summary>Only present (true) for abstract Defs.</summary>
    [JsonPropertyName("abstract")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Abstract { get; init; }

    /// <summary>Inheritance anchor name of an abstract Def.</summary>
    [JsonPropertyName("inherit_name")]
    public string? InheritName { get; init; }

    [JsonPropertyName("parent_name")]
    public string? ParentName { get; init; }

    [JsonPropertyName("xml")]
    public string? Xml { get; init; }

    [JsonPropertyName("xml_truncated")]
    public bool? XmlTruncated { get; init; }
}

/// <summary>Full Def definition.</summary>
public sealed record ReadDefResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<DefSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

/// <summary>Symbol lookup result.</summary>
public sealed record ReadSymbolResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<SymbolSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    /// <summary>Only when count is 0: symbols whose short name shares a word with the query.</summary>
    [JsonPropertyName("suggestions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Suggestions { get; init; }

    /// <summary>
    /// Results are exact name matches only whenever any exist; this is how many looser
    /// (substring) matches were left out. Absent when results are already substring matches.
    /// </summary>
    [JsonPropertyName("partial_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PartialCount { get; init; }
}

/// <summary>A symbol as listed by browse tools: enough to pick one and read it, nothing more.</summary>
public sealed record SymbolBrief
{
    /// <summary>Name relative to the listed parent (list_symbols) or the full name (find_descendants).</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Only present when the result's rows span several assemblies; otherwise see the result-level assembly.</summary>
    [JsonPropertyName("assembly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Assembly { get; init; }

    [JsonPropertyName("signature")]
    public required string Signature { get; init; }
}

/// <summary>A single symbol.</summary>
public sealed record SymbolSummary
{
    [JsonPropertyName("fqn")]
    public required string Fqn { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("assembly")]
    public required string Assembly { get; init; }

    [JsonPropertyName("parent_fqn")]
    public string? ParentFqn { get; init; }

    /// <summary>Real type signature composed from IL metadata.</summary>
    [JsonPropertyName("signature")]
    public required string Signature { get; init; }

    /// <summary>Full base-type chain, nearest first. Omitted when empty.</summary>
    [JsonPropertyName("base_chain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? BaseChain { get; init; }

    /// <summary>Omitted when empty.</summary>
    [JsonPropertyName("interfaces")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Interfaces { get; init; }

    [JsonPropertyName("accessibility")]
    public required string Accessibility { get; init; }

    [JsonPropertyName("is_static")]
    public required bool IsStatic { get; init; }

    /// <summary>Decompiled source. Only populated on request.</summary>
    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("body_truncated")]
    public bool? BodyTruncated { get; init; }
}

/// <summary>Inheritance lookup result.</summary>
public sealed record FindDescendantsResult
{
    [JsonPropertyName("base_type")]
    public required string BaseType { get; init; }

    /// <summary>The assembly shared by every row; null when rows span several assemblies (each row then carries its own).</summary>
    [JsonPropertyName("assembly")]
    public string? Assembly { get; init; }

    [JsonPropertyName("results")]
    public required IReadOnlyList<SymbolBrief> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    /// <summary>Only when count is 0: type names that share a word with base_type.</summary>
    [JsonPropertyName("suggestions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Suggestions { get; init; }
}

/// <summary>Symbol browse result.</summary>
public sealed record ListSymbolsResult
{
    [JsonPropertyName("parent")]
    public required string Parent { get; init; }

    /// <summary>The assembly shared by every row; null when rows span several assemblies (each row then carries its own).</summary>
    [JsonPropertyName("assembly")]
    public string? Assembly { get; init; }

    [JsonPropertyName("results")]
    public required IReadOnlyList<SymbolBrief> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("limit_reached")]
    public required bool LimitReached { get; init; }

    /// <summary>When parent is a namespace, its immediate child namespaces.</summary>
    [JsonPropertyName("child_namespaces")]
    public required IReadOnlyList<string> ChildNamespaces { get; init; }
}

/// <summary>One excerpt of a decompiled file.</summary>
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

    /// <summary>Last complete line in the excerpt; the next page starts at end_line + 1.</summary>
    [JsonPropertyName("end_line")]
    public required int EndLine { get; init; }

    [JsonPropertyName("total_lines")]
    public required int TotalLines { get; init; }

    [JsonPropertyName("truncated")]
    public required bool Truncated { get; init; }
}

/// <summary>Source search result.</summary>
public sealed record SearchSourceResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<SourceMatch> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("limit_reached")]
    public required bool LimitReached { get; init; }

    /// <summary>False while the source index is still building; results are then necessarily incomplete.</summary>
    [JsonPropertyName("source_indexed")]
    public required bool SourceIndexed { get; init; }

    /// <summary>True when indexing is running in the background; retry in a while.</summary>
    [JsonPropertyName("indexing")]
    public required bool Indexing { get; init; }

    /// <summary>Why the last indexing attempt failed. Mod searches are not retried automatically; call inspect_installed_mod with force=true to retry.</summary>
    [JsonPropertyName("index_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexError { get; init; }
}

/// <summary>One matching line in source.</summary>
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

/// <summary>Def reference lookup result.</summary>
public sealed record FindDefUsagesResult
{
    [JsonPropertyName("def_name")]
    public required string DefName { get; init; }

    [JsonPropertyName("results")]
    public required IReadOnlyList<DefUsageSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

/// <summary>One place a Def is referenced.</summary>
public sealed record DefUsageSummary
{
    [JsonPropertyName("file_path")]
    public required string FilePath { get; init; }

    [JsonPropertyName("line")]
    public required int Line { get; init; }

    /// <summary><c>def_xml</c>, <c>game_source</c>, or <c>mod_source</c>.</summary>
    [JsonPropertyName("source_kind")]
    public required string SourceKind { get; init; }

    /// <summary>Context where the reference appears, e.g. <c>ThingDef/costList/Steel</c> or <c>RimWorld.ThingDefOf.Steel</c>.</summary>
    [JsonPropertyName("context")]
    public string? Context { get; init; }

    /// <summary><c>exact</c> (structured source) or <c>heuristic</c> (string-match guess).</summary>
    [JsonPropertyName("confidence")]
    public required string Confidence { get; init; }
}
