using System.ComponentModel;
using ModelContextProtocol.Server;
using RimWorldModMcp.Core.Mods;
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

/// <summary>Tools for researching RimWorld Defs and the C# API.</summary>
[McpServerToolType]
public sealed class IndexTools(
    RimWorldLocator locator,
    IndexDatabase database,
    IndexBuilder builder,
    SourceIndexer sourceIndexer,
    SourceQueryService sourceQueries,
    MemberDecompiler decompiler,
    ModCatalog catalog,
    ModInspectionService inspection)
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
            ModsDir = paths.ModsDir,
            WorkshopDir = paths.WorkshopDir,
            PlayerLog = paths.PlayerLog,
            BridgePort = locator.BridgePort(),
            Index = new IndexStatusPayload
            {
                Fresh = status.Fresh,
                DefCount = status.DefCount,
                SymbolCount = status.SymbolCount,
                SourceIndexed = status.SourceIndexed,
                SourceIndex = new SourceIndexPayload
                {
                    Running = status.SourceIndex.Running,
                    IndexedFiles = status.SourceIndex.IndexedFiles,
                    Error = status.SourceIndex.Error,
                },
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
            Results = [.. hits.Select(h => ToSummary(h, SearchDescriptionChars))],
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
            Results = [.. hits.Select(h => ToSummary(h, int.MaxValue))],
            Count = hits.Count,
        };
    });

    [McpServerTool(Name = "read_symbol", UseStructuredContent = true, ReadOnly = true)]
    [Description("Look up a C# symbol: signature, full inheritance chain, and implemented interfaces, optionally with decompiled source. Exact name matches only whenever any exist (partial_count tells how many substring matches were skipped); substring matches otherwise. On zero hits, suggestions lists similar names. For the whole decompiled file use read_source_file.")]
    public ReadSymbolResult ReadSymbol(
        [Description("Short name or full name, e.g. ThingDef or Verse.ThingDef; a fragment such as ThingDefO matches by substring.")]
        string name,
        [Description("Include decompiled source.")]
        bool include_body = false,
        [Description("Byte limit for the source excerpt, 256-262144.")]
        int max_bytes = 4096,
        [Description("Maximum results, 1-100.")]
        int limit = 10,
        [Description("Substring of the assembly key to restrict results, e.g. Assembly-CSharp, mod:cj.rimtalk, or 1.6/ to pick one version of a multi-version mod.")]
        string? assembly = null) => ToolGuard.Run(() =>
    {
        using var connection = database.Open();
        var assemblyLike = AssemblyLike(assembly);
        var hits = SymbolRepository.Read(connection, name, limit, assemblyLike);

        if (hits.Count == 0)
        {
            // 空清單對 LLM 呼叫端毫無資訊：它分不出是名字記錯、改名了還是索引沒建。
            return new ReadSymbolResult { Results = [], Count = 0, Suggestions = SymbolRepository.Suggest(connection, name, 5) };
        }

        // 精確命中時才有「被略過的模糊命中」可言；模糊命中本身就是全部了。
        var isExact = hits[0].Fqn == name || hits[0].ShortName == name;
        int? partialCount = isExact ? SymbolRepository.CountPartial(connection, name, assemblyLike) : null;

        if (!include_body)
        {
            return new ReadSymbolResult { Results = [.. hits.Select(ToSummary)], Count = hits.Count, PartialCount = partialCount };
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

        return new ReadSymbolResult { Results = results, Count = results.Count, PartialCount = partialCount };
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
        int limit = 100)
    {
        string? kindName = null;

        if (!string.IsNullOrEmpty(kind))
        {
            if (!Enum.TryParse<RimWorldModMcp.Indexing.Model.SymbolKind>(kind, ignoreCase: true, out var parsed))
            {
                throw new ArgumentException($"Unknown symbol kind: {kind}", nameof(kind));
            }

            kindName = parsed.ToString();
        }

        return ToolGuard.Run(() =>
        {
            using var connection = database.Open();
            var assemblyLike = AssemblyLike(assembly);

            if (!SymbolRepository.ParentExists(connection, parent, assemblyLike))
            {
                // 「型別不存在」和「型別沒有成員」都回空清單的話，呼叫端只能瞎猜。
                var suggestions = SymbolRepository.Suggest(connection, parent, 5);
                var hint = suggestions.Count > 0 ? $" Similar: {string.Join(", ", suggestions)}." : string.Empty;
                throw new KeyNotFoundException($"Parent not found: {parent}.{hint}");
            }

            var hits = SymbolRepository.Children(connection, parent, kindName, assemblyLike, limit);
            var (sharedAssembly, rows) = ToBriefs(hits, h => RelativeName(h, parent));

            return new ListSymbolsResult
            {
                Parent = parent,
                Assembly = sharedAssembly,
                Results = rows,
                Count = hits.Count,
                LimitReached = hits.Count >= Math.Clamp(limit, 1, 500),
                ChildNamespaces = SymbolRepository.ChildNamespaces(connection, parent, assemblyLike),
            };
        });
    }

    [McpServerTool(Name = "read_source_file", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read a whole decompiled source file (game or installed mod) by the assembly and file values returned from search_source or list_symbols. Page with start_line = previous end_line + 1.")]
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
            ?? throw new FileNotFoundException($"File not in the index: {assembly} / {file}");

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
    [Description("List every class deriving from the given type, including indirect descendants. On zero hits, suggestions lists similar type names.")]
    public FindDescendantsResult FindDescendants(
        [Description("Base type: full name (Verse.ThingComp) or short name (ThingComp) when unambiguous.")]
        string base_type,
        [Description("Maximum results, 1-500.")]
        int limit = 100) => ToolGuard.Run(() =>
    {
        using var connection = database.Open();
        var resolved = ResolveTypeName(connection, base_type);
        var hits = SymbolRepository.Descendants(connection, resolved, limit);
        var (sharedAssembly, rows) = ToBriefs(hits, h => h.Fqn);

        return new FindDescendantsResult
        {
            BaseType = resolved,
            Assembly = sharedAssembly,
            Results = rows,
            Count = hits.Count,
            Suggestions = hits.Count == 0 ? SymbolRepository.Suggest(connection, base_type, 5) : null,
        };
    });

    /// <summary>
    /// 短名 → 唯一的型別 fqn。read_symbol 接受短名而 find_descendants 只收 fqn，
    /// 呼叫端得多走一趟；這裡補齊。多個同名型別時報錯列出候選，讓呼叫端挑。
    /// 已含 <c>.</c> 的輸入視為 fqn 原樣使用。
    /// </summary>
    private static string ResolveTypeName(Microsoft.Data.Sqlite.SqliteConnection connection, string name)
    {
        if (name.Contains('.'))
        {
            return name;
        }

        var candidates = SymbolRepository.Read(connection, name, 10)
            .Where(h => h.ShortName == name && h.Kind is "Class" or "Struct" or "Interface")
            .Select(h => h.Fqn)
            .Distinct()
            .ToList();

        return candidates.Count switch
        {
            0 => name,
            1 => candidates[0],
            _ => throw new ArgumentException($"Ambiguous type name '{name}'; use one of: {string.Join(", ", candidates)}", nameof(name)),
        };
    }

    [McpServerTool(Name = "search_source", UseStructuredContent = true)]
    [Description("Regex search (.NET syntax, case-insensitive) over decompiled source: the game by default, or one installed mod with package_id. Simple literals work best (e.g. CurTimeSpeed); a|b matches either branch. indexing=true means the index is building in the background; retry in a while. Read a hit with read_source_file.")]
    public SearchSourceResult SearchSource(
        [Description("Regular expression (.NET syntax), always case-insensitive.")]
        string pattern,
        [Description("packageId of an installed mod to search instead of the game. First use starts decompiling in the background.")]
        string? package_id = null,
        [Description("Game only: restrict to a file path pattern, e.g. RimWorld/*.cs.")]
        string? file_pattern = null,
        [Description("Mod only: substring of the assembly key, e.g. 1.6/ to search one version of a multi-version mod.")]
        string? assembly = null,
        [Description("Maximum results, 1-800.")]
        int limit = 50) => ToolGuard.Run(() =>
    {
        var effectiveLimit = Math.Clamp(limit, 1, 800);
        var modSearch = !string.IsNullOrWhiteSpace(package_id);

        // 「已知但在此模式無效」的參數不能靜默吞掉：呼叫端會以為結果已經過濾。
        if (modSearch && !string.IsNullOrEmpty(file_pattern))
        {
            throw new ArgumentException("file_pattern only applies to game searches; omit it when package_id is set.", nameof(file_pattern));
        }

        if (!modSearch && !string.IsNullOrEmpty(assembly))
        {
            throw new ArgumentException("assembly only applies to mod searches; set package_id as well.", nameof(assembly));
        }

        if (modSearch)
        {
            var mod = catalog.Find(package_id!);
            var result = inspection.TrySearchSource(mod.PackageId, mod.Path, pattern, limit, assembly);

            return new SearchSourceResult
            {
                Results = [.. result.Hits.Select(ToMatch)],
                Count = result.Hits.Count,
                LimitReached = result.Hits.Count >= effectiveLimit,
                SourceIndexed = result.SourceIndexed,
                Indexing = result.Indexing,
                IndexError = result.Error,
            };
        }

        using var connection = database.Open();
        var hits = sourceQueries.Search(connection, pattern, file_pattern, limit);
        var progress = sourceIndexer.Progress;

        return new SearchSourceResult
        {
            Results = [.. hits.Select(ToMatch)],
            Count = hits.Count,
            LimitReached = hits.Count >= effectiveLimit,
            SourceIndexed = IndexMetaRepository.Get(connection, "source_indexed") == "true",
            Indexing = progress.Running,
            IndexError = progress.Error,
        };
    });

    private static SourceMatch ToMatch(SourceHit h) => new()
    {
        Assembly = h.Assembly,
        File = h.File,
        Line = h.Line,
        Text = h.Text,
    };

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

    /// <summary>search_defs 的描述字元上限。ThingDef 的描述動輒兩三百字，25 筆清單裡大半 token 都花在這。</summary>
    private const int SearchDescriptionChars = 200;

    private static DefSummary ToSummary(RimWorldModMcp.Indexing.Model.DefHit hit, int descriptionChars) => new()
    {
        DefName = hit.DefName,
        DefType = hit.DefType,
        Label = hit.Label,
        Description = hit.Description.Length == 0 ? null
            : hit.Description.Length > descriptionChars ? hit.Description[..descriptionChars] + "…"
            : hit.Description,
        FilePath = hit.FilePath,
        Abstract = hit.Abstract ? true : null,
        InheritName = hit.InheritName,
        ParentName = hit.ParentName,
        Xml = hit.Xml,
        XmlTruncated = hit.XmlTruncated,
    };

    /// <summary>
    /// 瀏覽列表的組件欄位：所有列同一組件就提到結果層、列上省略；
    /// 混雜時（多版本 Mod）結果層為 null、各列自帶。100 列重複同一個 "Assembly-CSharp" 純屬浪費。
    /// </summary>
    private static (string? Shared, IReadOnlyList<SymbolBrief> Rows) ToBriefs(List<SymbolHit> hits, Func<SymbolHit, string> name)
    {
        var shared = hits.Count > 0 && hits.TrueForAll(h => h.Assembly == hits[0].Assembly) ? hits[0].Assembly : null;

        return (shared, [.. hits.Select(h => new SymbolBrief
        {
            Name = name(h),
            Kind = h.Kind,
            Assembly = shared is null ? h.Assembly : null,
            Signature = h.Signature,
        })]);
    }

    /// <summary>
    /// 去掉 parent 前綴。巢狀型別的 fqn 是 Outer+Inner，保留 "+Inner"：
    /// 呼叫端把 parent 與 name 用 "." 接回去就會得到不存在的 Outer.Inner。
    /// </summary>
    private static string RelativeName(SymbolHit hit, string parent)
    {
        if (parent.Length == 0 || hit.Fqn.Length <= parent.Length || !hit.Fqn.StartsWith(parent, StringComparison.Ordinal))
        {
            return hit.Fqn;
        }

        return hit.Fqn[parent.Length] switch
        {
            '.' => hit.Fqn[(parent.Length + 1)..],
            '+' => hit.Fqn[parent.Length..],
            _ => hit.Fqn,
        };
    }

    private static SymbolSummary ToSummary(SymbolHit hit) => new()
    {
        Fqn = hit.Fqn,
        Kind = hit.Kind,
        Assembly = hit.Assembly,
        ParentFqn = hit.ParentFqn,
        Signature = hit.Signature,
        BaseChain = hit.BaseChain.Count > 0 ? hit.BaseChain : null,
        Interfaces = hit.Interfaces.Count > 0 ? hit.Interfaces : null,
        Accessibility = hit.Accessibility,
        IsStatic = hit.IsStatic,
    };
}
