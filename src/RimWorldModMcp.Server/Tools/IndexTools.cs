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
    RimWorldLocator locator,
    IndexDatabase database,
    IndexBuilder builder,
    SourceIndexer sourceIndexer,
    SourceQueryService sourceQueries,
    MemberDecompiler decompiler)
{
    [McpServerTool(Name = "rimworld_status", UseStructuredContent = true, ReadOnly = true)]
    [Description("Call first: reports RimWorld installation detection and index status. If detected=false, ask the user to set RIMWORLD_MOD_MCP_GAME_PATH; if index.fresh=false, call rebuild_index.")]
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
    [Description("Call when the index is missing, stale, or the game was updated: rebuilds the Def and C# symbol index in seconds and it is queryable immediately; the source full-text index continues in the background.")]
    public RebuildIndexResult RebuildIndex(
        [Description("Also build the source full-text index in the background (required by search_source).")]
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
    [Description("Search Defs by name, label, or description; returns summaries. Use read_def for the full XML.")]
    public SearchDefsResult SearchDefs(
        [Description("Search term. A trailing * means prefix search, e.g. Gun*.")]
        string query,
        [Description("Restrict to a Def type, e.g. ThingDef or PawnKindDef. Omit for any type.")]
        string? def_type = null,
        [Description("Maximum results, 1-200.")]
        int limit = 25,
        [Description("Include truncated XML content.")]
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
    [Description("Read the full XML of a single Def.")]
    public ReadDefResult ReadDef(
        [Description("The defName, or the Name attribute of an abstract Def.")]
        string def_name,
        [Description("Restrict to a Def type. Omit for any type.")]
        string? def_type = null,
        [Description("Byte limit for the XML content, 1024-262144.")]
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
    [Description("Look up a C# symbol: signature, full inheritance chain, and implemented interfaces, optionally with decompiled source. For the whole decompiled file use read_source_file.")]
    public ReadSymbolResult ReadSymbol(
        [Description("Short name or part of the full name, e.g. ThingDef or Verse.ThingDef.")]
        string name,
        [Description("Include decompiled source.")]
        bool include_body = false,
        [Description("Byte limit for the source excerpt, 256-262144.")]
        int max_bytes = 4096,
        [Description("Maximum results, 1-100.")]
        int limit = 20,
        [Description("Substring of the assembly key to restrict results, e.g. Assembly-CSharp, mod:cj.rimtalk, or 1.6/ to pick one version of a multi-version mod.")]
        string? assembly = null) => ToolGuard.Run(() =>
    {
        using var connection = database.Open();
        var hits = SymbolRepository.Read(connection, name, limit, AssemblyLike(assembly));

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
                    body = Utf8Text.Truncate(source, Math.Clamp(max_bytes, 256, 262144), out var wasTruncated);
                    truncated = wasTruncated;
                }
            }

            results.Add(ToSummary(hit) with { Body = body, BodyTruncated = truncated });
        }

        return new ReadSymbolResult { Results = results, Count = results.Count };
    });

    [McpServerTool(Name = "list_symbols", UseStructuredContent = true, ReadOnly = true)]
    [Description("Browse the symbol tree without knowing a name: a namespace lists its top-level types and child namespaces; a type lists its members and nested types.")]
    public ListSymbolsResult ListSymbols(
        [Description("A namespace (e.g. Verse.AI) or a type's full name (e.g. Verse.ThingDef). Empty string lists the root namespaces.")]
        string parent = "",
        [Description("Restrict to a symbol kind: Class, Struct, Interface, Enum, Delegate, Method, Constructor, Property, Field, or Event. Omit for all.")]
        string? kind = null,
        [Description("Substring of the assembly key to restrict results, e.g. mod:cj.rimtalk or 1.6/.")]
        string? assembly = null,
        [Description("Maximum results, 1-500.")]
        int limit = 100) => ToolGuard.Run(() =>
    {
        string? kindName = null;

        if (!string.IsNullOrEmpty(kind))
        {
            if (!Enum.TryParse<RimWorldModMcp.Indexing.Model.SymbolKind>(kind, ignoreCase: true, out var parsed))
            {
                throw new ArgumentException($"未知的符號種類：{kind}", nameof(kind));
            }

            kindName = parsed.ToString();
        }

        using var connection = database.Open();
        var assemblyLike = AssemblyLike(assembly);
        var hits = SymbolRepository.Children(connection, parent, kindName, assemblyLike, limit);

        return new ListSymbolsResult
        {
            Parent = parent,
            Results = [.. hits.Select(ToSummary)],
            Count = hits.Count,
            LimitReached = hits.Count >= Math.Clamp(limit, 1, 500),
            ChildNamespaces = SymbolRepository.ChildNamespaces(connection, parent, assemblyLike),
        };
    });

    [McpServerTool(Name = "read_source_file", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read a whole decompiled source file (game or installed mod) by the assembly and file values returned from search_source, search_installed_mod_source, or list_symbols. Page with start_line = previous end_line + 1.")]
    public ReadSourceFileResult ReadSourceFile(
        [Description("Assembly key exactly as returned by a search, e.g. Assembly-CSharp or mod:cj.rimtalk:1.6/Assemblies/RimTalk.dll.")]
        string assembly,
        [Description("File path exactly as returned by a search, e.g. Verse/ThingDef.cs.")]
        string file,
        [Description("First line to return, 1-based.")]
        int start_line = 1,
        [Description("Byte limit for the excerpt, 1024-262144.")]
        int max_bytes = 65536) => ToolGuard.Run(() =>
    {
        using var connection = database.Open();
        var text = SourceFileRepository.Read(connection, assembly, file)
            ?? throw new FileNotFoundException($"索引裡沒有這個檔案：{assembly} / {file}");

        var excerpt = SourceExcerpt.Take(text, start_line, Math.Clamp(max_bytes, 1024, 262144));

        return new ReadSourceFileResult
        {
            Assembly = assembly,
            File = file,
            Text = excerpt.Text,
            StartLine = excerpt.StartLine,
            EndLine = excerpt.EndLine,
            TotalLines = excerpt.TotalLines,
            Truncated = excerpt.Truncated,
        };
    });

    /// <summary>把使用者給的組件鍵片段變成 LIKE 子字串樣式；沒給就不過濾。</summary>
    private static string? AssemblyLike(string? assembly)
        => string.IsNullOrEmpty(assembly) ? null : $"%{FtsQuery.LikeLiteral(assembly)}%";

    [McpServerTool(Name = "find_descendants", UseStructuredContent = true, ReadOnly = true)]
    [Description("List every class deriving from the given type, including indirect descendants.")]
    public FindDescendantsResult FindDescendants(
        [Description("Full name of the base type, e.g. Verse.ThingComp.")]
        string base_type,
        [Description("Maximum results, 1-500.")]
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
    [Description("Regex search over the decompiled game source. source_indexed=false in the result means the background index is still building; retry later.")]
    public SearchSourceResult SearchSource(
        [Description("Regular expression (.NET syntax), always case-insensitive.")]
        string pattern,
        [Description("Restrict to a file path pattern, e.g. RimWorld/*.cs. Omit for all files.")]
        string? file_pattern = null,
        [Description("Maximum results, 1-800.")]
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
    [Description("Find where a Def is referenced: cross-references in Def XML and DefOf static fields in C#.")]
    public FindDefUsagesResult FindDefUsages(
        [Description("The defName to look up.")]
        string def_name,
        [Description("Restrict to a source kind: def_xml, game_source, or mod_source. Omit for all.")]
        string? source_kind = null,
        [Description("Maximum results, 1-500.")]
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
        AssemblyPath = hit.AssemblyPath,
        ParentFqn = hit.ParentFqn,
        Signature = hit.Signature,
        BaseChain = hit.BaseChain,
        Interfaces = hit.Interfaces,
        Accessibility = hit.Accessibility,
        IsStatic = hit.IsStatic,
    };
}
