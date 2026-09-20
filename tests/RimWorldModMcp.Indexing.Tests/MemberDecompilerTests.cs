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
}
