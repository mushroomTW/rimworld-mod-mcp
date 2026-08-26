using System.Security.Cryptography;
using System.Text;
using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Indexing.Decompilation;
using RimWorldModMcp.Indexing.Metadata;
using RimWorldModMcp.Indexing.Query;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Pipeline;

/// <summary>一個已安裝 Mod 的組件資訊。</summary>
public sealed record ModAssemblyInfo(string Name, string Path, int SymbolCount, int SourceFileCount, bool FromCache);

/// <summary>
/// 按需檢視已安裝 Mod 的組件。
///
/// <para>
/// 反編譯結果與符號都存進同一個索引資料庫，用 <c>mod:&lt;packageId&gt;:&lt;assembly&gt;</c>
/// 當作組件鍵與遊戲本體的索引區隔開。這樣 Mod 的原始碼搜尋可以直接沿用
/// 既有的 FTS 索引，不需要另一套檔案快取。
/// </para>
/// </summary>
public sealed class ModInspectionService(
    IndexDatabase database,
    IMemberDecompiler decompiler,
    CriticalSectionLock locks,
    SourceQueryService sourceQueries)
{
    private readonly AssemblySymbolReader _symbolReader = new();

    /// <summary>
    /// 找出 Mod 的所有組件並在必要時建立索引。
    ///
    /// <para>
    /// 遞迴掃描整個 Mod 目錄，因此 <c>Assemblies/1.5/*.dll</c> 這種常見的
    /// 多版本佈局也能抓到——Python 版只看 <c>Assemblies/</c> 頂層而漏掉它們。
    /// </para>
    /// </summary>
    public IReadOnlyList<ModAssemblyInfo> Inspect(string packageId, string modPath, bool force = false)
    {
        var assemblies = FindAssemblies(modPath);

        if (assemblies.Count == 0)
        {
            return [];
        }

        // 併發呼叫會同時反編譯並寫入同一組鍵，必須序列化。
        using var _ = locks.Hold("mod-index", new Dictionary<string, string> { ["package_id"] = packageId });

        var results = new List<ModAssemblyInfo>(assemblies.Count);
        using var connection = database.Open();

        foreach (var assembly in assemblies)
        {
            var key = AssemblyKey(packageId, assembly);
            var stamp = Stamp(assembly);
            var cached = IndexMetaRepository.Get(connection, $"mod_stamp:{key}");

            if (!force && cached == stamp)
            {
                results.Add(new ModAssemblyInfo(
                    Path.GetFileNameWithoutExtension(assembly),
                    assembly,
                    CountSymbols(connection, key),
                    CountSourceFiles(connection, key),
                    FromCache: true));

                continue;
            }

            using var transaction = connection.BeginTransaction();

            ClearAssembly(connection, key);

            var symbols = _symbolReader.Read(assembly)
                .Select(s => s with { Assembly = key })
                .ToList();

            SymbolRepository.Insert(connection, symbols);

            var sourceCount = 0;

            foreach (var (path, text) in decompiler.DecompileAll(assembly))
            {
                SourceFileRepository.Insert(connection, key, path, text);
                sourceCount++;
            }

            SymbolRepository.RebuildFts(connection);
            IndexMetaRepository.Set(connection, $"mod_stamp:{key}", stamp);

            transaction.Commit();

            results.Add(new ModAssemblyInfo(
                Path.GetFileNameWithoutExtension(assembly),
                assembly,
                symbols.Count,
                sourceCount,
                FromCache: false));
        }

        return results;
    }

    /// <summary>搜尋一個已安裝 Mod 的原始碼，必要時先建立索引。</summary>
    public IReadOnlyList<SourceHit> SearchSource(string packageId, string modPath, string pattern, int limit)
    {
        Inspect(packageId, modPath);

        using var connection = database.Open();
        var prefix = $"mod:{packageId}:";

        return [.. sourceQueries
            .Search(connection, pattern, null, limit * 2)
            .Where(hit => hit.Assembly.StartsWith(prefix, StringComparison.Ordinal))
            .Take(Math.Clamp(limit, 1, 800))];
    }

    /// <summary>清除一個 Mod 的所有索引資料。</summary>
    public void Forget(string packageId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var prefix = $"mod:{packageId}:";

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM symbol WHERE assembly LIKE $prefix ESCAPE '\';
                DELETE FROM source_file WHERE assembly LIKE $prefix ESCAPE '\';
                DELETE FROM index_meta WHERE key LIKE $metaPrefix ESCAPE '\';
                """;
            command.Parameters.AddWithValue("$prefix", FtsQuery.LikeLiteral(prefix) + "%");
            command.Parameters.AddWithValue("$metaPrefix", "mod\\_stamp:" + FtsQuery.LikeLiteral(prefix) + "%");
            command.ExecuteNonQuery();
        }

        SymbolRepository.RebuildFts(connection);
        SourceFileRepository.Clear(connection);

        transaction.Commit();
    }

    /// <summary>遞迴找出 Mod 目錄下所有 .NET 組件。</summary>
    private static List<string> FindAssemblies(string modPath)
    {
        if (!Directory.Exists(modPath))
        {
            return [];
        }

        return [.. Directory
            .EnumerateFiles(modPath, "*.dll", SearchOption.AllDirectories)
            // Harmony 之類的相依函式庫不是 Mod 自己的程式碼，索引它們只是噪音。
            .Where(path => !IsKnownDependency(Path.GetFileNameWithoutExtension(path)))
            .Order()];
    }

    private static bool IsKnownDependency(string name)
        => name is "0Harmony" or "HarmonyLib" or "Newtonsoft.Json"
            || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase);

    private static string AssemblyKey(string packageId, string assemblyPath)
    {
        // 同一個 Mod 可能在不同版本目錄下有同名組件，用相對位置的雜湊區分。
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(assemblyPath)))[..8];
        return $"mod:{packageId}:{Path.GetFileNameWithoutExtension(assemblyPath)}:{digest}";
    }

    private static string Stamp(string assemblyPath)
    {
        var info = new FileInfo(assemblyPath);
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    private static void ClearAssembly(Microsoft.Data.Sqlite.SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM symbol WHERE assembly = $key;
            DELETE FROM source_file WHERE assembly = $key;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private static int CountSymbols(Microsoft.Data.Sqlite.SqliteConnection connection, string key)
        => CountWhere(connection, "SELECT COUNT(*) FROM symbol WHERE assembly = $key;", key);

    private static int CountSourceFiles(Microsoft.Data.Sqlite.SqliteConnection connection, string key)
        => CountWhere(connection, "SELECT COUNT(*) FROM source_file WHERE assembly = $key;", key);

    private static int CountWhere(Microsoft.Data.Sqlite.SqliteConnection connection, string sql, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
