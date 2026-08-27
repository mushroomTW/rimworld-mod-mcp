using System.Security.Cryptography;
using System.Text;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Indexing.Defs;

namespace RimWorldModMcp.Indexing.Pipeline;

/// <summary>
/// 判斷索引是否還對得上目前的遊戲安裝。
///
/// <para>
/// Python 版只雜湊 <c>Version.txt</c>、組件 DLL 的大小與時間戳，以及各 pack 的
/// <b>目錄名稱</b>——Def XML 的內容完全不參與。手動編輯 <c>Data/Core/Defs/*.xml</c>
/// 或新增／刪除單一 Def 檔案之後，索引仍會回報 fresh。那是真正的正確性缺口，
/// 這裡把 Def 檔案也納入指紋。
/// </para>
/// </summary>
public sealed class IndexFingerprint
{
    private readonly Lock _cacheGate = new();
    private string? _cachedValue;
    private string? _cachedKey;
    private DateTime _cachedAtUtc;

    /// <summary>
    /// 指紋計算要對兩萬個 Def XML 各做一次 stat，是幾百毫秒到數秒的操作，
    /// 而 <c>rimworld_status</c> 每次呼叫都會算一次。短 TTL 記憶化。
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    /// <summary>計算目前安裝的指紋。偵測不到遊戲時回傳 <c>null</c>。</summary>
    public string? Compute(RimWorldPaths paths)
    {
        if (paths.ManagedDir is null || paths.DataDir is null)
        {
            return null;
        }

        var cacheKey = paths.ManagedDir + "\n" + paths.DataDir;

        lock (_cacheGate)
        {
            if (_cachedValue is not null
                && _cachedKey == cacheKey
                && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
            {
                return _cachedValue;
            }
        }

        return ComputeFrom(paths, DefFileWalker.Walk(paths.DataDir));
    }

    /// <summary>
    /// 用已走訪好的清單計算指紋，讓 Rebuild 不必為指紋再掃一次 Data/。
    /// 清單必須來自 <see cref="DefFileWalker.Walk"/>——兩條路徑共用同一個
    /// 走訪來源，雜湊輸入才保證一致。
    /// </summary>
    internal string? ComputeFrom(RimWorldPaths paths, IReadOnlyList<DefPack> packs)
    {
        if (paths.ManagedDir is null || paths.DataDir is null)
        {
            return null;
        }

        var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        if (paths.InstallRoot is not null)
        {
            AppendFile(digest, Path.Combine(paths.InstallRoot, "Version.txt"));
        }

        foreach (var assembly in Directory.GetFiles(paths.ManagedDir, "Assembly-CSharp*.dll").Order())
        {
            AppendFile(digest, assembly);
        }

        foreach (var pack in packs)
        {
            Append(digest, pack.Name);

            // 逐一納入 Def 檔案的相對路徑、大小與寫入時間。
            // 這樣新增、刪除或修改任何一個 Def 檔都會改變指紋。
            foreach (var file in pack.Files)
            {
                Append(digest, $"{file.RelativePath}:{file.Length}:{file.LastWriteTicks}");
            }
        }

        var computed = Convert.ToHexStringLower(digest.GetHashAndReset());

        lock (_cacheGate)
        {
            _cachedValue = computed;
            _cachedKey = paths.ManagedDir + "\n" + paths.DataDir;
            _cachedAtUtc = DateTime.UtcNow;
        }

        return computed;
    }

    private static void AppendFile(IncrementalHash digest, string path)
    {
        var info = new FileInfo(path);

        if (info.Exists)
        {
            Append(digest, $"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        }
        else
        {
            // 「檔案不存在」也要留下痕跡：否則刪掉 Version.txt 與
            // 從未有過 Version.txt 會得到相同的指紋。
            Append(digest, $"{Path.GetFileName(path)}:missing");
        }
    }

    private static void Append(IncrementalHash digest, string value)
    {
        // 各段之間以 NUL 分隔，避免相鄰片段串接後構造出相同的位元組序列。
        digest.AppendData(Encoding.UTF8.GetBytes(value));
        digest.AppendData("\0"u8);
    }
}
