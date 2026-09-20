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
/// Python 版必須為每個組件另外開一個 <c>ilspycmd</c> 子行程，解析它的 stdout／stderr，
/// 還要靠「有沒有產出 .cs」來判斷成敗。這裡直接在行程內呼叫函式庫，
/// 錯誤是真的例外，而且可以精確到「只反編譯這一個方法」。
/// </para>
/// </summary>
public sealed class MemberDecompiler(RimWorldLocator? locator = null) : IDisposable
{
    // CSharpDecompiler 不是 thread-safe，每個組件各自持有一個實例並在使用時上鎖。
    private readonly ConcurrentDictionary<string, Lazy<DecompilerHandle>> _handles = new(StringComparer.OrdinalIgnoreCase);

    public string? DecompileMember(string assemblyPath, int metadataToken)
    {
        var handle = MetadataTokens.EntityHandle(metadataToken);

        if (handle.IsNil)
        {
            return null;
        }

        var entry = Handle(assemblyPath);

        lock (entry.Gate)
        {
            try
            {
                return handle.Kind switch
                {
                    HandleKind.TypeDefinition
                        or HandleKind.MethodDefinition
                        or HandleKind.PropertyDefinition
                        or HandleKind.FieldDefinition
                        or HandleKind.EventDefinition => entry.Decompiler.DecompileAsString(handle),
                    _ => null,
                };
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // 個別成員反編譯失敗（泛型特化、混淆過的 IL 等）不應該讓整個查詢失敗。
                return null;
            }
        }
    }

    public IEnumerable<(string Path, string Text)> DecompileAll(string assemblyPath, CancellationToken cancellationToken = default)
    {
        var entry = Handle(assemblyPath);
        var reader = entry.File.Metadata;

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
                    continue;
                }
            }

            var ns = reader.GetString(type.Namespace);
            var path = string.IsNullOrEmpty(ns) ? $"{name}.cs" : $"{ns.Replace('.', '/')}/{name}.cs";

            yield return (path, text);
        }
    }

    public void Dispose()
    {
        foreach (var entry in _handles.Values)
        {
            if (entry.IsValueCreated)
            {
                entry.Value.Dispose();
            }
        }

        _handles.Clear();
    }

    private DecompilerHandle Handle(string assemblyPath)
    {
        // 快取鍵必須包含檔案的大小與時間戳。這個物件是 singleton、而且
        // PrefetchEntireImage 已把整個映像讀進記憶體——只用路徑當鍵的話，
        // 使用者更新遊戲或 Mod 之後仍會從快取住的「舊映像」反編譯出舊程式碼，
        // 連 force 重建都救不回來。
        var stamp = Stamp(assemblyPath);

        while (true)
        {
            var lazy = _handles.GetOrAdd(
                assemblyPath,
                path => new Lazy<DecompilerHandle>(() => DecompilerHandle.Create(path, stamp, locator?.Detect().ManagedDir)));

            var handle = lazy.Value;

            if (handle.FileStamp == stamp)
            {
                return handle;
            }

            // 檔案已被更新：丟掉舊映像重載，順便回收記憶體。
            if (_handles.TryRemove(new KeyValuePair<string, Lazy<DecompilerHandle>>(assemblyPath, lazy)))
            {
                lock (handle.Gate)
                {
                    handle.Dispose();
                }
            }
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
