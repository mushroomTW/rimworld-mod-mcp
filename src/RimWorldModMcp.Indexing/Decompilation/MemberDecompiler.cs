using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Indexing.Decompilation;

/// <summary>
/// 依需求把單一成員或整個組件反編譯成 C# 原始碼。
/// 以 ICSharpCode.Decompiler 實作的反編譯器。
///
/// <para>
/// Python 版必須為每個組件另外開一個 ilspycmd 子行程，解析它的 stdout／stderr，
/// 還要靠「有沒有產出 .cs」來判斷成敗。這裡直接在行程內呼叫函式庫，
/// 錯誤是真的例外，而且可以精確到「只反編譯這一個方法」。
/// </para>
/// </summary>
public sealed class MemberDecompiler(RimWorldLocator locator) : IDisposable
{
    // CSharpDecompiler 不是 thread-safe，每個組件各自持有一個實例並在使用時上鎖。
    //
    // 值不是例外而是 Attempt：Lazy<T> 的預設模式（ExecutionAndPublication）會把
    // 工廠拋出的例外永久快取起來，之後每次 .Value 都重拋同一個例外。Mod 目錄下的
    // Assemblies/ 常混有原生程式庫或非 .NET 的 DLL，讓例外進去就等於把那個路徑
    // 永久毒化——使用者連 inspect_installed_mod(force=true) 都救不回，只能重啟 server。
    // 把失敗表示成值就沒有這個問題：條目會被移除，下次呼叫重新嘗試。
    private readonly ConcurrentDictionary<string, Lazy<Attempt>> _handles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>一次載入嘗試的結果：成功時 <see cref="Handle"/> 非 null，失敗時 <see cref="Error"/> 說明原因。</summary>
    private sealed record Attempt(DecompilerHandle? Handle, string? Error);

    /// <summary>
    /// 同時保留的反編譯映像數上限。
    ///
    /// <para>
    /// 每個條目持有以 <c>PrefetchEntireImage</c> 讀進記憶體的**完整組件映像**
    ///（Assembly-CSharp 約 30MB），而這個類別是 singleton、生命週期等於整個
    /// server 會期。沒有上限的話，記憶體會隨著「這個會期檢視過的 Mod 數」
    /// 單調成長且永不回收。
    /// </para>
    /// <para>
    /// 8 個足夠涵蓋典型工作集（遊戲本體 + 正在看的一兩個 Mod），
    /// 淘汰最久沒被取用的那一個。
    /// </para>
    /// </summary>
    private const int MaxCachedAssemblies = 8;

    /// <summary>目前快取的組件數（含載入失敗、即將被移除的條目）。測試用。</summary>
    internal int CachedAssemblyCount => _handles.Count;

    public string? DecompileMember(string assemblyPath, int metadataToken)
    {
        var handle = MetadataTokens.EntityHandle(metadataToken);

        if (handle.IsNil)
        {
            return null;
        }

        if (TryHandle(assemblyPath).Handle is not { } entry)
        {
            return null;
        }

        lock (entry.Gate)
        {
            try
            {
                var source = handle.Kind switch
                {
                    HandleKind.TypeDefinition
                        or HandleKind.MethodDefinition
                        or HandleKind.PropertyDefinition
                        or HandleKind.FieldDefinition
                        or HandleKind.EventDefinition => entry.Decompiler.DecompileAsString(handle),
                    _ => null,
                };

                // 反編譯器在 Windows 產出 CRLF；只回 \n，與 read_source_file 一致，也省下每行一個跳脫字元。
                return source?.Replace("\r\n", "\n", StringComparison.Ordinal);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // 個別成員反編譯失敗（泛型特化、混淆過的 IL 等）不應該讓整個查詢失敗。
                return null;
            }
        }
    }

    /// <summary>
    /// 反編譯整個組件。結果會物化成清單後一次回傳（呼叫端本來就會 <c>ToList()</c>）。
    ///
    /// <para>
    /// 組件無法載入時（原生 DLL、損壞的檔案）不拋例外，而是回空序列並透過
    /// <paramref name="onUnavailable"/> 回報原因——呼叫端據此把「這一顆 DLL 反編譯不了」
    /// 記成單一組件的問題，而不是讓整個 Mod 的索引失敗。
    /// </para>
    /// <para>
    /// 列舉期間會釘選控制代碼（<c>PinCount</c>），<see cref="TrimCache"/> 不會淘汰它：
    /// 否則另一執行緒載入第 9 個組件時，正在被列舉（大型組件可達分鐘級）的條目會因
    /// <c>LastUsedTicks</c> 最舊而被處置，列舉中途拋 <c>ObjectDisposedException</c>
    /// 並使整個 Mod 索引被記成永久失敗。物化前就釘選也關掉了「回傳迭代器後、
    /// 第一次 MoveNext 前被淘汰」的空隙。
    /// </para>
    /// </summary>
    /// <param name="onUnavailable">組件無法載入時呼叫一次，參數是給人看的原因。</param>
    /// <param name="onTypeSkipped">個別型別反編譯失敗被跳過時呼叫一次，參數是型別全名（F12 的略過統計用）。</param>
    public IEnumerable<(string Path, string Text)> DecompileAll(
        string assemblyPath,
        Action<string>? onUnavailable = null,
        Action<string>? onTypeSkipped = null,
        CancellationToken cancellationToken = default)
    {
        var attempt = TryHandle(assemblyPath);

        if (attempt.Handle is not { } entry)
        {
            onUnavailable?.Invoke(attempt.Error ?? $"Could not load the assembly: {assemblyPath}");
            return [];
        }

        Interlocked.Increment(ref entry.PinCount);

        try
        {
            var results = new List<(string Path, string Text)>();
            var reader = entry.File.Metadata;

            try
            {
                foreach (var typeHandle in reader.TypeDefinitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var type = reader.GetTypeDefinition(typeHandle);
                    var name = reader.GetString(type.Name);

                    // 巢狀型別會跟著外層一起輸出；編譯器產生的型別沒有閱讀價值。
                    if (type.IsNested || name.StartsWith('<') || name == "<Module>")
                    {
                        continue;
                    }

                    string text;

                    lock (entry.Gate)
                    {
                        try
                        {
                            text = entry.Decompiler.DecompileTypeAsString(
                                new FullTypeName(Metadata.MetadataNames.FullName(reader, type)));
                        }
                        catch (Exception e) when (e is not OutOfMemoryException)
                        {
                            // ILSpy 對個別型別失敗是常態，跳過就好——這對應 Python 版
                            // 「returncode 非 0 但有產出 .cs 就算成功」的寬容規則。
                            // 並行淘汰造成的 ObjectDisposedException 也在這裡被吃掉，
                            // 變成該型別的靜默跳過，而不是整個組件失敗。
                            try
                            {
                                onTypeSkipped?.Invoke(Metadata.MetadataNames.FullName(reader, type));
                            }
                            catch
                            {
                                // 統計回呼不可影響反編譯主流程。
                            }

                            continue;
                        }
                    }

                    var ns = reader.GetString(type.Namespace);
                    var path = string.IsNullOrEmpty(ns) ? $"{name}.cs" : $"{ns.Replace('.', '/')}/{name}.cs";

                    results.Add((path, text));
                }
            }
            catch (ObjectDisposedException)
            {
                // 鎖之外的 reader 存取（列舉 TypeDefinitions、讀取定義）在控制代碼
                // 被並行處置時拋出——實務上只剩「檔案在列舉期間被更新」的舊映像路徑，
                // TrimCache 已因釘選而略過。回傳已收集的部分而不是讓例外逃出，
                // 否則整個 Mod 索引失敗並被記成永久錯誤。
            }

            return results;
        }
        finally
        {
            Interlocked.Decrement(ref entry.PinCount);
        }
    }

    public void Dispose()
    {
        foreach (var attempt in _handles.Values)
        {
            if (attempt.IsValueCreated && attempt.Value.Handle is { } handle)
            {
                handle.Dispose();
            }
        }

        _handles.Clear();
    }

    /// <summary>
    /// 取得組件的反編譯控制代碼。**不拋例外，且失敗不會被快取**——呼叫端拿到的
    /// <see cref="Attempt"/> 帶著失敗原因，下次呼叫會重新嘗試。
    /// </summary>
    private Attempt TryHandle(string assemblyPath)
    {
        // 快取鍵必須包含檔案的大小與時間戳。這個物件是 singleton、而且
        // PrefetchEntireImage 已把整個映像讀進記憶體——只用路徑當鍵的話，
        // 使用者更新遊戲或 Mod 之後仍會從快取住的「舊映像」反編譯出舊程式碼，
        // 連 force 重建都救不回來。
        var stamp = Stamp(assemblyPath);

        // 迴圈只會在「檔案在讀取期間被換掉」時多跑一輪；上限是防禦性的。
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var lazy = _handles.GetOrAdd(
                assemblyPath,
                path => new Lazy<Attempt>(() => TryCreate(path, locator.Detect().ManagedDir)));

            var result = lazy.Value;

            // 載入失敗：移除條目，讓下一次呼叫重新嘗試，然後把原因往上傳。
            // 這一步就是「失敗不被永久記住」的關鍵。
            if (result.Handle is null)
            {
                _handles.TryRemove(new KeyValuePair<string, Lazy<Attempt>>(assemblyPath, lazy));
                return result;
            }

            if (result.Handle.FileStamp == stamp)
            {
                Volatile.Write(ref result.Handle.LastUsedTicks, Environment.TickCount64);
                TrimCache();
                return result;
            }

            // 檔案已被更新：丟掉舊映像重載，順便回收記憶體。
            // 釘選中（DecompileAll 物化中）不處置舊映像：列舉仍拿著它的 reader，
            // 處置會讓列舉拋 ObjectDisposedException。只從字典移除，舊映像由
            // 列舉的參考維持，GC 會回收；漏一個舊映像比整個索引失敗便宜。
            if (_handles.TryRemove(new KeyValuePair<string, Lazy<Attempt>>(assemblyPath, lazy))
                && Volatile.Read(ref result.Handle.PinCount) == 0)
            {
                lock (result.Handle.Gate)
                {
                    result.Handle.Dispose();
                }
            }
        }

        return new Attempt(null, $"Could not load the assembly after repeated attempts (the file keeps changing): {assemblyPath}");
    }

    /// <summary>
    /// 把快取修剪到 <see cref="MaxCachedAssemblies"/> 以內，淘汰最久沒被取用的組件。
    ///
    /// <para>
    /// 淘汰是必要的而不是最佳化：每個條目都常駐一份完整映像，而這個類別的生命週期
    /// 等於整個 server 會期。上限很小（8），所以線性掃描找最舊的成本可忽略。
    /// </para>
    /// </summary>
    private void TrimCache()
    {
        while (_handles.Count > MaxCachedAssemblies)
        {
            var victim = _handles
                .Where(entry => entry.Value.IsValueCreated
                    && entry.Value.Value.Handle is not null
                    && Volatile.Read(ref entry.Value.Value.Handle.PinCount) == 0)
                .OrderBy(entry => Volatile.Read(ref entry.Value.Value.Handle!.LastUsedTicks))
                .FirstOrDefault();

            // 沒有可淘汰的條目，或競態下被別人先移除：停手，避免無限迴圈。
            if (victim.Value is null || !_handles.TryRemove(victim))
            {
                return;
            }

            var handle = victim.Value.Value.Handle!;

            lock (handle.Gate)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// 建立控制代碼，失敗時把原因包成 <see cref="Attempt"/> 回傳而不是拋出去。
    ///
    /// <para>
    /// 這裡刻意**廣泛捕捉**：輸入是使用者 Mod 目錄裡任意一個 .dll，失敗型別取決於
    /// 反編譯器內部的實作細節（實測原生 DLL 拋的是 ILSpy 自訂的
    /// <c>MetadataFileNotSupportedException</c>，既不是 <c>BadImageFormatException</c>
    /// 也不是 <c>NotSupportedException</c>）。逐一列舉型別會漏，而漏掉的代價是
    /// 例外進入 <c>Lazy</c> 而被永久快取——正是這個類別要修的問題。
    /// 失敗不會被吞掉：原因透過 <c>onUnavailable</c> 回報給呼叫端，
    /// 最終出現在 <c>inspect_installed_mod</c> 的組件清單裡。
    /// </para>
    /// </summary>
    private static Attempt TryCreate(string assemblyPath, string? managedDirectory)
    {
        try
        {
            return new Attempt(DecompilerHandle.Create(assemblyPath, Stamp(assemblyPath), managedDirectory), null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new Attempt(null, $"Not a loadable .NET assembly ({e.GetType().Name}): {e.Message}");
        }
    }

    internal static string Stamp(string assemblyPath)
    {
        var info = new FileInfo(assemblyPath);
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    private sealed class DecompilerHandle : IDisposable
    {
        private DecompilerHandle(PEFile file, CSharpDecompiler decompiler, string fileStamp)
        {
            File = file;
            Decompiler = decompiler;
            FileStamp = fileStamp;
        }

        internal PEFile File { get; }

        internal CSharpDecompiler Decompiler { get; }

        /// <summary>載入當下的檔案大小與時間戳，用於失效判定。</summary>
        internal string FileStamp { get; }

        /// <summary>最後一次被取用的單調毫秒（<see cref="Environment.TickCount64"/>）。淘汰時挑最舊的。</summary>
        internal long LastUsedTicks;

        /// <summary>進行中的 <c>DecompileAll</c> 物化數。非 0 時 <see cref="TrimCache"/> 不得淘汰。</summary>
        internal int PinCount;

        internal Lock Gate { get; } = new();

        internal static DecompilerHandle Create(string assemblyPath, string stamp, string? managedDirectory)
        {
            // PrefetchEntireImage 讀完就放掉檔案控制代碼。預設的 memory-map 會鎖住 DLL，
            // 使用者中途更新遊戲時會留下卡住的 handle。
            const System.Reflection.PortableExecutable.PEStreamOptions streamOptions =
                System.Reflection.PortableExecutable.PEStreamOptions.PrefetchEntireImage;

            var file = new PEFile(assemblyPath, System.IO.File.OpenRead(assemblyPath), streamOptions);

            // resolver 必須指向 Managed 目錄，否則跨組件的型別全都解析成 ??。
            // 參考組件也要用同樣的 streamOptions：resolver 預設會 memory-map 同目錄的 DLL
            // 並跟著這個常駐快取一起活著，使用者的 dotnet build 會撞上 MSB3021／MSB3027。
            var resolver = new UniversalAssemblyResolver(
                assemblyPath,
                throwOnError: false,
                file.DetectTargetFrameworkId(),
                streamOptions: streamOptions);

            // Mod 組件放在自己的 Assemblies/ 底下，resolver 預設只搜那裡與目標框架的
            // 參考目錄，Assembly-CSharp 與 UnityEngine 全都解析不到——反編譯結果會塞滿
            // 「Unknown result type (might be due to invalid IL or missing references)」。
            // 把遊戲的 Managed 目錄加進搜尋路徑；沒有 Mono 的平台上連 mscorlib 也靠它。
            if (managedDirectory is not null && Directory.Exists(managedDirectory))
            {
                resolver.AddSearchDirectory(managedDirectory);
            }

            var settings = new DecompilerSettings
            {
                ThrowOnAssemblyResolveErrors = false,
                RemoveDeadCode = false,
                ShowXmlDocumentation = false,
            };

            return new DecompilerHandle(file, new CSharpDecompiler(file, resolver, settings), stamp);
        }

        public void Dispose() => File.Dispose();
    }
}
