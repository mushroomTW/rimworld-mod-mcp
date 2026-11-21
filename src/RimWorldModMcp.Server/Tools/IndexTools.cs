using System.ComponentModel;
using ModelContextProtocol.Server;
using RimWorldModMcp.Core.Mods;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Indexing.Decompilation;
using RimWorldModMcp.Indexing.Pipeline;
using RimWorldModMcp.Indexing.Query;
using RimWorldModMcp.Indexing.Semantics;
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
    MemberDecompiler decompiler,
    ModCatalog catalog,
    ModInspectionService inspection,
    IndexAutoRebuild autoRebuild)
{
    /// <summary>
    /// 查詢工具共用的外框：先確認索引可用（必要時自動重建）；查詢途中撞上損毀時，
    /// 讓下一次呼叫立刻重新檢查並重建，不必等滿檢查間隔。
    /// </summary>
    private T Query<T>(Func<T> action) => ToolGuard.Run(() =>
    {
        autoRebuild.EnsureReady();

        try
        {
            return action();
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) when (SqliteCorruption.IsCorrupt(e))
        {
            autoRebuild.Invalidate();
            throw;
        }
    });

    [McpServerTool(Name = "rimworld_status", UseStructuredContent = true, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Call first: reports RimWorld installation detection and index status. If unsupported is set, an older RimWorld is installed: tell the user this tool supports RimWorld 1.7 only and ask them to update the game; if detected=false, ask the user to set RIMWORLD_MOD_MCP_GAME_PATH; if index.busy=true another writer holds the database, so retry shortly. A stale or corrupt index is rebuilt automatically by the next query tool call, and the source full-text index then continues in the background.")]
    public RimWorldStatusResult RimWorldStatus() => ToolGuard.Run(() =>
    {
        var paths = locator.Detect();
        var status = builder.Status();

        return new RimWorldStatusResult
        {
            Detected = paths.HasManagedAndData,
            InstallRoot = paths.InstallRoot,
            Unsupported = paths.UnsupportedMessage,
            ModsDir = paths.ModsDir,
            WorkshopDir = paths.WorkshopDir,
            PlayerLog = paths.PlayerLog,
            BridgePort = locator.BridgePort(),
            Index = new IndexStatusPayload
            {
                Fresh = status.Fresh,
                Healthy = status.Healthy,
                Busy = status.Busy,
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

    [McpServerTool(Name = "search_defs", UseStructuredContent = true, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Search Core and DLC Defs by name, label, or description; returns summaries. Use read_def for the full XML. Installed mods' Defs are not indexed; read their XML from the mod path.")]
    public SearchDefsResult SearchDefs(
        [Description("Search term. A trailing * means prefix search, e.g. Gun*.")]
        string query,
        [Description("Restrict to a Def type, e.g. ThingDef or PawnKindDef. Omit for any type.")]
        string? def_type = null,
        [Description("Maximum results, 1-200.")]
        int limit = 25,
        [Description("Include truncated XML content.")]
        bool include_xml = false) => Query(() =>
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

    [McpServerTool(Name = "read_def", UseStructuredContent = true, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read the full XML of a single Def.")]
    public ReadDefResult ReadDef(
        [Description("The defName, or the Name attribute of an abstract Def.")]
        string def_name,
        [Description("Restrict to a Def type. Omit for any type.")]
        string? def_type = null,
        [Description("Byte limit for the XML content, 1024-262144.")]
        int max_bytes = 65536) => Query(() =>
    {
        using var connection = database.Open();
        var hits = DefRepository.Read(connection, def_name, def_type, max_bytes);

        return new ReadDefResult
        {
            Results = [.. hits.Select(h => ToSummary(h, int.MaxValue))],
            Count = hits.Count,
        };
    });

    [McpServerTool(Name = "read_symbol", UseStructuredContent = true, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Look up a C# symbol in the game or an indexed mod: signature, full inheritance chain, and implemented interfaces, optionally with decompiled source. Exact name matches only whenever any exist (partial_count tells how many substring matches were skipped); substring matches otherwise. On zero hits, suggestions lists similar names. For the whole decompiled file use read_source_file.")]
    public ReadSymbolResult ReadSymbol(
        [Description("Short name or full name, e.g. ThingDef or Verse.ThingDef; a fragment such as ThingDefO matches by substring.")]
        string name,
        [Description("Include decompiled source.")]
        bool include_body = false,
        [Description("Byte limit for the source excerpt, 256-262144.")]
        int max_bytes = 4096,
        [Description("Maximum results, 1-100.")]
        int limit = 10,
        [Description("Substring of the assembly key to restrict results, e.g. Assembly-CSharp, mod:cj.rimtalk, or 1.7/ to pick one version of a multi-version mod.")]
        string? assembly = null) => Query(() =>
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
        var isExact = string.Equals(hits[0].Fqn, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(hits[0].ShortName, name, StringComparison.OrdinalIgnoreCase);
        int? partialCount = isExact ? SymbolRepository.CountPartial(connection, name, assemblyLike) : null;

        if (!include_body)
        {
            return new ReadSymbolResult { Results = [.. hits.Select(ToSummary)], Count = hits.Count, PartialCount = partialCount };
        }

        var managed = locator.Detect().ManagedDir;
        var results = AttachBodies(hits, managed, max_bytes);

        return new ReadSymbolResult { Results = results, Count = results.Count, PartialCount = partialCount };
    });

    private List<SymbolSummary> AttachBodies(List<SymbolHit> hits, string? managed, int maxBytes)
    {
        var results = new List<SymbolSummary>(hits.Count);

        foreach (var hit in hits)
        {
            string? body = null;
            bool? truncated = null;
            string? note = null;

            // 優先用索引裡記錄的實際組件路徑——Mod 組件的 assembly 欄位是
            // mod:pkg:<DLL 相對路徑> 形式的索引鍵，拼 Managed\<key>.dll 出來的
            // 根本不是合法路徑。
            var assemblyPath = hit.AssemblyPath
                ?? (managed is not null ? Path.Combine(managed, hit.Assembly + ".dll") : null);

            if (assemblyPath is not null && File.Exists(assemblyPath))
            {
                var source = decompiler.DecompileMember(assemblyPath, hit.MetadataToken);

                if (source is null)
                {
                    // 沿用原行為：反編譯失敗不附 body。
                }
                else if (!BodyMatchesSymbol(source, hit))
                {
                    // 組件在索引建立後被更新時，索引記的 metadata token 會對到別的成員；
                    // 與其回傳相鄰成員的程式碼誤導呼叫端，不如明講索引過期。
                    // 遊戲組件由自動重建處理（讓下一次查詢立刻重新檢查）；Mod 組件只有 search_source 會重新索引。
                    note = StaleBodyNote(hit.Assembly);
                    if (!hit.Assembly.StartsWith("mod:", StringComparison.Ordinal))
                    {
                        autoRebuild.Invalidate();
                    }
                }
                else
                {
                    body = Utf8Text.Truncate(source, Math.Clamp(maxBytes, 256, 262144), out var wasTruncated);
                    truncated = wasTruncated;
                }
            }

            results.Add(ToSummary(hit) with { Body = body, BodyTruncated = truncated, BodyNote = note });
        }

        return results;
    }

    /// <summary>反編譯結果與索引對不上時的提示。Mod 組件鍵是 mod:＜packageId＞:＜DLL 相對路徑＞。</summary>
    internal static string StaleBodyNote(string assembly)
    {
        const string Mismatch = "Decompiled member does not match the indexed symbol; the index is stale";

        if (assembly.StartsWith("mod:", StringComparison.Ordinal))
        {
            var packageId = assembly.Split(':')[1];
            return $"{Mismatch}. Call search_source with package_id={packageId} to re-index the mod, then retry.";
        }

        return $"{Mismatch} and is rebuilt automatically on the next call, so retry.";
    }

    /// <summary>
    /// 反編譯結果是否仍是索引記的那個成員：原始碼中必須出現符號的識別字。
    /// 索引子（Item）、運算子（op_*）與顯式介面實作的名稱在反編譯輸出中會改寫，不做檢查。
    /// </summary>
    internal static bool BodyMatchesSymbol(string source, SymbolHit hit)
    {
        var name = hit.ShortName;
        var generic = name.IndexOf('<');
        if (generic >= 0)
        {
            name = name[..generic];
        }

        name = name[(name.LastIndexOf('.') + 1)..];

        if (name.Length == 0 || name.StartsWith("op_", StringComparison.Ordinal) || (hit.Kind == "Property" && name == "Item"))
        {
            return true;
        }

        return source.Contains(name, StringComparison.Ordinal);
    }

    [McpServerTool(Name = "list_symbols", UseStructuredContent = true, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Browse the symbol tree without knowing a name: a namespace lists its top-level types and child namespaces; a type lists its members and nested types. With assembly=mod:<packageId> and an empty parent it lists an indexed mod's namespaces.")]
    public ListSymbolsResult ListSymbols(
        [Description("A namespace (e.g. Verse.AI) or a type's full name (e.g. Verse.ThingDef). Empty string lists the root namespaces.")]
        string parent = "",
        [Description("Restrict to a symbol kind: Class, Struct, Interface, Enum, Delegate, Method, Constructor, Property, Field, or Event. Omit for all.")]
        string? kind = null,
        [Description("Substring of the assembly key to restrict results, e.g. mod:cj.rimtalk or 1.7/.")]
        string? assembly = null,
        [Description("Maximum results, 1-500.")]
        int limit = 100) => Query(() =>
        {
            string? kindName = null;

            // 要在 ToolGuard 之內：拋在外面的 ArgumentException 到呼叫端只剩「An error occurred」。
            if (!string.IsNullOrEmpty(kind))
            {
                if (!Enum.TryParse<RimWorldModMcp.Indexing.Model.SymbolKind>(kind, ignoreCase: true, out var parsed))
                {
                    throw new ArgumentException($"Unknown symbol kind: {kind}");
                }

                kindName = parsed.ToString();
            }

            using var connection = database.Open();
            var assemblyLike = AssemblyLike(assembly);

            // 大小寫不分地解析成索引裡的正式名稱（verse.ai → Verse.AI），之後一律用正式名稱。
            var resolved = SymbolRepository.ResolveParent(connection, parent, assemblyLike);

            if (resolved is null)
            {
                // 「型別不存在」和「型別沒有成員」都回空清單的話，呼叫端只能瞎猜。
                var suggestions = SymbolRepository.Suggest(connection, parent, 5);
                var hint = suggestions.Count > 0 ? $" Similar: {string.Join(", ", suggestions)}." : string.Empty;
                throw new KeyNotFoundException($"Parent not found: {parent}.{hint}");
            }

            var hits = SymbolRepository.Children(connection, resolved, kindName, assemblyLike, limit);
            var (sharedAssembly, rows) = ToBriefs(hits, h => RelativeName(h, resolved));

            return new ListSymbolsResult
            {
                Parent = resolved,
                Assembly = sharedAssembly,
                Results = rows,
                Count = hits.Count,
                LimitReached = hits.Count >= Math.Clamp(limit, 1, 500),
                ChildNamespaces = SymbolRepository.ChildNamespaces(connection, resolved, assemblyLike),
            };
        });

    [McpServerTool(Name = "read_source_file", UseStructuredContent = true, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read a decompiled source file (game or installed mod) one page at a time, by the assembly and file values returned from search_source or list_symbols. A page is max_bytes (16 KB by default, about 400 lines); truncated=true means more follows, so page with start_line = previous end_line + 1.")]
    public ReadSourceFileResult ReadSourceFile(
        [Description("Assembly key exactly as returned by a search, e.g. Assembly-CSharp or mod:cj.rimtalk:1.7/Assemblies/RimTalk.dll.")]
        string assembly,
        [Description("File path exactly as returned by a search, e.g. Verse/ThingDef.cs.")]
        string file,
        [Description("First line to return, 1-based.")]
        int start_line = 1,
        [Description("Byte limit for the excerpt, 1024-262144.")]
        int max_bytes = 16384) => Query(() =>
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

    [McpServerTool(Name = "find_descendants", UseStructuredContent = true, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List every class deriving from the given type, including indirect descendants within the same assembly; indexed mod types are found only when the given type is their direct base. On zero hits, suggestions lists similar type names.")]
    public FindDescendantsResult FindDescendants(
        [Description("Base type: full name (Verse.ThingComp) or short name (ThingComp) when unambiguous.")]
        string base_type,
        [Description("Maximum results, 1-500.")]
        int limit = 100) => Query(() =>
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
    /// 已含 . 的輸入視為 fqn 原樣使用。
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

    // Mod 搜尋按需寫入索引並清除過期組件；快取有效時重複搜尋不再觸發索引。
    [McpServerTool(Name = "search_source", UseStructuredContent = true, ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Regex search (.NET syntax, case-insensitive) over decompiled source: the game by default, or one installed mod with package_id (e.g. pattern=HarmonyPatch lists its patches). Simple literals work best (e.g. CurTimeSpeed); a|b matches either branch. indexing=true means the index is building in the background; retry in a while. budget_exceeded=true means the search ran out of time with candidate files left unscanned, so the result is incomplete — narrow it with file_pattern and run it again. Read a hit with read_source_file.")]
    public SearchSourceResult SearchSource(
        [Description("Regular expression (.NET syntax), always case-insensitive.")]
        string pattern,
        [Description("packageId of an installed mod to search instead of the game. First use starts decompiling in the background.")]
        string? package_id = null,
        [Description("Game only: restrict to a file path pattern, e.g. RimWorld/*.cs.")]
        string? file_pattern = null,
        [Description("Mod only: substring of the assembly key, e.g. 1.7/ to search one version of a multi-version mod.")]
        string? assembly = null,
        [Description("Maximum results, 1-800.")]
        int limit = 50) => Query(() =>
    {
        var effectiveLimit = Math.Clamp(limit, 1, 800);
        var modSearch = !string.IsNullOrWhiteSpace(package_id);

        // 「已知但在此模式無效」的參數不能靜默吞掉：呼叫端會以為結果已經過濾。
        if (modSearch && !string.IsNullOrEmpty(file_pattern))
        {
            throw new ArgumentException("file_pattern only applies to game searches; omit it when package_id is set.");
        }

        if (!modSearch && !string.IsNullOrEmpty(assembly))
        {
            throw new ArgumentException("assembly only applies to mod searches; set package_id as well.");
        }

        if (modSearch)
        {
            var mod = catalog.Find(package_id!);
            var result = inspection.TrySearchSource(mod.PackageId, mod.Path, pattern, limit, assembly);
            var (modAssembly, modFiles) = GroupByFile(result.Hits);

            return new SearchSourceResult
            {
                Assembly = modAssembly,
                Results = modFiles,
                Count = result.Hits.Count,
                LimitReached = result.Hits.Count >= effectiveLimit,
                SourceIndexed = result.SourceIndexed,
                Indexing = result.Indexing,
                IndexError = result.Error,
                BudgetExceeded = result.BudgetExceeded,
                IncompleteReason = result.IncompleteReason,
            };
        }

        using var connection = database.Open();
        var search = SourceQueryService.Search(connection, pattern, file_pattern, limit);
        var progress = sourceIndexer.Progress;
        var (sharedAssembly, files) = GroupByFile(search.Hits);

        return new SearchSourceResult
        {
            Assembly = sharedAssembly,
            Results = files,
            Count = search.Hits.Count,
            LimitReached = search.Hits.Count >= effectiveLimit,
            SourceIndexed = IndexMetaRepository.Get(connection, "source_indexed") == "true",
            Indexing = progress.Running,
            IndexError = progress.Error,
            BudgetExceeded = search.BudgetExceeded,
            IncompleteReason = search.IncompleteReason,
        };
    });

    /// <summary>
    /// 命中依檔案分組，組件全部相同時提到結果層（同 <see cref="ToBriefs"/>）。
    /// 50 筆命中逐列重複 "Assembly-CSharp" 與檔案路徑，佔了回傳的三到四成。
    /// </summary>
    private static (string? Shared, IReadOnlyList<SourceFileMatches> Files) GroupByFile(IReadOnlyList<SourceHit> hits)
    {
        var shared = hits.Count > 0 && hits.All(h => h.Assembly == hits[0].Assembly) ? hits[0].Assembly : null;

        return (shared, [.. hits.GroupBy(h => (h.Assembly, h.File)).Select(g => new SourceFileMatches
        {
            Assembly = shared is null ? g.Key.Assembly : null,
            File = g.Key.File,
            Hits = [.. g.Select(h => new SourceLine { Line = h.Line, Text = h.Text })],
        })]);
    }

    // 指定 Mod 時與 search_source 相同：首次查詢會在背景反編譯並寫入索引。
    [McpServerTool(Name = "find_def_usages", UseStructuredContent = true, ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Find where a Def is referenced, in the game by default or in one installed mod with package_id: Def XML cross-references (def_xml) and C# code using XxxDefOf.<defName> (csharp; for the game also the DefOf field declaration). The C# part searches decompiled source: indexing=true means that index is still building, so retry in a while.")]
    public FindDefUsagesResult FindDefUsages(
        [Description("The defName to look up (case-sensitive).")]
        string def_name,
        [Description("packageId of an installed mod to search instead of the game: its Def XML (scanned on demand) and its decompiled C#. First use starts decompiling in the background.")]
        string? package_id = null,
        [Description("Restrict to a source kind: def_xml or csharp. Omit for both.")]
        string? source_kind = null,
        [Description("Maximum results per source kind, 1-500.")]
        int limit = 100) => Query(() =>
    {
        var kind = ToolGuard.OneOf(source_kind, nameof(source_kind), "def_xml", "csharp");
        var effectiveLimit = Math.Clamp(limit, 1, 500);
        var mod = string.IsNullOrWhiteSpace(package_id) ? null : catalog.Find(package_id);
        var usages = new List<(string FilePath, string SourceKind, string? Assembly, DefUsageHit Hit)>();

        if (mod is null)
        {
            // 遊戲本體：索引裡的 XML 交叉引用與 DefOf 欄位宣告（存成 game_source）。
            using var connection = database.Open();
            var stored = kind switch
            {
                "def_xml" => DefReferenceRepository.Find(connection, def_name, "def_xml", effectiveLimit),
                "csharp" => DefReferenceRepository.Find(connection, def_name, "game_source", effectiveLimit),
                _ => DefReferenceRepository.Find(connection, def_name, null, effectiveLimit),
            };

            usages.AddRange(stored.Select(u => (
                u.FilePath,
                u.SourceKind == "def_xml" ? "def_xml" : "csharp",
                (string?)null,
                new DefUsageHit { Line = u.Line, Context = u.Context, Confidence = u.Confidence == "exact" ? null : u.Confidence })));
        }
        else if (kind is null or "def_xml")
        {
            usages.AddRange(DefReferenceAnalyzer.FindInModXml(mod.Path, def_name, effectiveLimit).Select(c => (
                c.FilePath,
                "def_xml",
                (string?)null,
                new DefUsageHit { Line = c.Line, Context = c.Context })));
        }

        CodeSearchStatus? code = null;

        if (kind is null or "csharp")
        {
            var pattern = DefReferenceAnalyzer.DefOfUsagePattern(def_name);
            IReadOnlyList<SourceHit> hits;

            if (mod is not null)
            {
                var result = inspection.TrySearchSource(mod.PackageId, mod.Path, pattern, effectiveLimit);
                hits = result.Hits;
                code = new CodeSearchStatus(result.SourceIndexed, result.Indexing, result.Error, result.IncompleteReason);
            }
            else
            {
                using var connection = database.Open();
                var search = SourceQueryService.Search(connection, pattern, null, effectiveLimit);
                var progress = sourceIndexer.Progress;
                hits = search.Hits;
                code = new CodeSearchStatus(
                    IndexMetaRepository.Get(connection, "source_indexed") == "true",
                    progress.Running,
                    progress.Error,
                    search.IncompleteReason);
            }

            // 原始碼搜尋不分大小寫；C# 的 DefOf 欄位名區分大小寫，這裡再精確比對一次。
            // 被截斷的長行看不到完整內容，比對不了，就保留 regex 的命中。
            usages.AddRange(hits
                .Where(h => h.Text.Length >= SourceQueryService.MaxLineLength || DefReferenceAnalyzer.IsDefOfUsage(h.Text, def_name))
                .Select(h => (h.File, "csharp", (string?)h.Assembly, new DefUsageHit { Line = h.Line, Context = h.Text.Trim() })));
        }

        return new FindDefUsagesResult
        {
            DefName = def_name,
            PackageId = mod?.PackageId,
            // 一個 Def 常在同一檔案被引用十幾次；逐列重複檔案路徑佔了一半篇幅。
            Results = [.. usages.GroupBy(u => (u.FilePath, u.SourceKind, u.Assembly)).Select(g => new DefUsageFile
            {
                FilePath = g.Key.FilePath,
                SourceKind = g.Key.SourceKind,
                Assembly = g.Key.Assembly,
                Hits = [.. g.Select(u => u.Hit)],
            })],
            Count = usages.Count,
            SourceIndexed = code?.SourceIndexed,
            Indexing = code?.Indexing,
            IndexError = code?.Error,
            IncompleteReason = code?.IncompleteReason,
        };
    });

    /// <summary>find_def_usages 的 C# 部分：原始碼索引的狀態，讓呼叫端分得出「沒有引用」與「還沒建好」。</summary>
    private sealed record CodeSearchStatus(bool SourceIndexed, bool Indexing, string? Error, string? IncompleteReason);

    /// <summary>
    /// search_defs 的描述字元上限。ThingDef 的描述動輒兩三百字，25 筆清單裡大半 token 都花在這；
    /// 80 字足以辨認是哪個 Def，要細節就 read_def。
    /// </summary>
    private const int SearchDescriptionChars = 80;

    private static string? FormatDescription(string description, int maxChars)
    {
        if (description.Length == 0)
        {
            return null;
        }

        if (description.Length > maxChars)
        {
            return description[..maxChars] + "…";
        }

        return description;
    }

    private static DefSummary ToSummary(RimWorldModMcp.Indexing.Model.DefHit hit, int descriptionChars) => new()
    {
        DefName = hit.DefName,
        DefType = hit.DefType,
        Label = hit.Label,
        Description = FormatDescription(hit.Description, descriptionChars),
        FilePath = hit.FilePath,
        Abstract = hit.Abstract ? true : null,
        InheritName = hit.InheritName,
        ParentName = hit.ParentName,
        Xml = hit.Xml,
        XmlTruncated = hit.XmlTruncated,
    };

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

    /// <summary>
    /// 瀏覽列表的組件欄位：所有列同一組件就提到結果層、列上省略；
    /// 混雜時（多版本 Mod）結果層為 null、各列自帶。100 列重複同一個 "Assembly-CSharp" 純屬浪費。
    /// 型別的 signature 已含 kind 與完整名稱，只留 signature；成員保留 name，kind 一律不列。
    /// </summary>
    private static (string? Shared, IReadOnlyList<SymbolBrief> Rows) ToBriefs(List<SymbolHit> hits, Func<SymbolHit, string> name)
    {
        var shared = hits.Count > 0 && hits.TrueForAll(h => h.Assembly == hits[0].Assembly) ? hits[0].Assembly : null;

        return (shared, [.. hits.Select(h => new SymbolBrief
        {
            Name = RimWorldModMcp.Indexing.Model.SymbolKinds.IsType(h.Kind) ? null : name(h),
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
}
