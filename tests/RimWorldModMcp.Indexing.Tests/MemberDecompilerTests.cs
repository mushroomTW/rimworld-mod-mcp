using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Indexing.Decompilation;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// 反編譯器對檔案控制代碼的持有行為。
///
/// <para>
/// MCP Server 是常駐行程，反編譯器快取也跟著常駐。若參考組件在解析後仍被
/// memory-map 鎖住，使用者的 dotnet build 就會撞上 MSB3021／MSB3027。
/// </para>
/// </summary>
public sealed class MemberDecompilerTests : IDisposable
{
    private const string LibSource = """
        namespace Lib
        {
            public class Base
            {
                public virtual string Name() => "base";
            }
        }
        """;

    private const string AppSource = """
        namespace App
        {
            public class Derived : Lib.Base
            {
                public override string Name() => "derived";
            }
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rwmm-decomp-" + Guid.NewGuid().ToString("n")[..12]);
    private readonly MemberDecompiler _decompiler = new(new RimWorldLocator());

    public void Dispose()
    {
        _decompiler.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>反編譯器在 Windows 產出 CRLF；read_symbol 的 body 一律只回 \n。</summary>
    [Fact]
    public void DecompileMemberReturnsLfLineEndings()
    {
        var lib = SyntheticAssembly.Emit(LibSource, "Lib", _root);
        var type = new RimWorldModMcp.Indexing.Metadata.AssemblySymbolReader().Read(lib).Single(s => s.Fqn == "Lib.Base");

        var source = _decompiler.DecompileMember(lib, type.MetadataToken);

        Assert.NotNull(source);
        Assert.Contains("\n", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DecompileAll_DoesNotKeepReferencedAssemblyLocked()
    {
        var lib = SyntheticAssembly.Emit(LibSource, "Lib", _root);
        var app = SyntheticAssembly.Emit(AppSource, "App", _root, lib);

        // 反編譯 App 會透過 resolver 載入同目錄的 Lib，之後快取仍活著。
        var files = _decompiler.DecompileAll(app).ToList();
        Assert.Contains(files, f => f.Text.Contains(": Base", StringComparison.Ordinal));

        // 模擬 dotnet build 重新產出 Lib.dll：被鎖住的話這裡會丟 IOException／UnauthorizedAccessException。
        File.WriteAllBytes(lib, File.ReadAllBytes(lib));
        File.Delete(lib);

        Assert.False(File.Exists(lib));
    }

    /// <summary>
    /// Mod 的 Assemblies/ 底下混有原生程式庫或非 .NET 的檔案是常態。載入失敗必須
    /// 「每次都能重試」，不能被 Lazy 快取記住——否則那個路徑會被永久毒化，
    /// 使用者連 force 重建都救不回，只能重啟 server。
    /// </summary>
    [Fact]
    public void UnreadableAssembly_FailsEveryTimeWithoutThrowing()
    {
        Directory.CreateDirectory(_root);
        var broken = Path.Combine(_root, "NotAnAssembly.dll");
        File.WriteAllBytes(broken, "this is plainly not a PE file"u8.ToArray());

        // 三次都要得到同樣的結果。修正前：第一次拋 BadImageFormatException，
        // 而 Lazy 會把該例外快取住，之後每次都重拋同一個例外。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            string? reason = null;

            var files = _decompiler.DecompileAll(broken, onUnavailable: r => reason = r).ToList();

            Assert.Empty(files);
            Assert.NotNull(reason);
            Assert.Null(_decompiler.DecompileMember(broken, 0x02000001));
        }

        // 壞檔案不該影響同一台伺服器上的其他組件。
        var good = SyntheticAssembly.Emit(LibSource, "Lib", _root);
        Assert.NotEmpty(_decompiler.DecompileAll(good).ToList());
    }

    /// <summary>
    /// 真正的原生 DLL（合法的 PE，但沒有 CLI metadata）。這是 Assemblies/ 底下最常見的
    /// 「不是 .NET 組件」形態，而且它跟「不是 PE」走的是不同的失敗路徑。
    /// </summary>
    [Fact]
    public void NativeAssemblyWithoutMetadata_IsReportedNotThrown()
    {
        var native = FindNativeLibrary();
        Assert.NotNull(native);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            string? reason = null;

            Assert.Empty(_decompiler.DecompileAll(native, onUnavailable: r => reason = r).ToList());
            Assert.NotNull(reason);
        }
    }

    /// <summary>找一個隨 SQLite 原生相依一起複製到輸出目錄的原生程式庫（各平台檔名不同）。</summary>
    private static string? FindNativeLibrary()
    {
        var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");

        return Directory.Exists(runtimes)
            ? Directory.EnumerateFiles(runtimes, "e_sqlite3.*", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    /// <summary>
    /// 反編譯映像的快取必須有上限。
    ///
    /// <para>
    /// 每個條目常駐一份以 PrefetchEntireImage 讀進記憶體的完整組件映像
    ///（Assembly-CSharp 約 30MB），而 MemberDecompiler 是 singleton、
    /// 生命週期等於整個 server 會期。沒有上限的話，記憶體會隨著
    /// 「這個會期檢視過的 Mod 數」單調成長且永不回收。
    /// </para>
    /// </summary>
    [Fact]
    public void DecompiledAssembliesAreEvictedBeyondTheCacheLimit()
    {
        var assemblies = new List<string>();

        for (var i = 0; i < 14; i++)
        {
            var path = SyntheticAssembly.Emit(LibSource, $"Lib{i:00}", _root);
            assemblies.Add(path);

            Assert.NotEmpty(_decompiler.DecompileAll(path).ToList());
        }

        Assert.True(
            _decompiler.CachedAssemblyCount <= 8,
            $"快取應該被修剪到 8 個以內，實際 {_decompiler.CachedAssemblyCount}");

        // 被淘汰的組件仍必須能重新載入——淘汰是回收記憶體，不是讓它失效。
        Assert.NotEmpty(_decompiler.DecompileAll(assemblies[^1]).ToList());
        Assert.NotEmpty(_decompiler.DecompileAll(assemblies[0]).ToList());
    }
}
