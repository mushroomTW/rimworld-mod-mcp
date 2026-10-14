using ICSharpCode.Decompiler.Metadata;

namespace RimWorldModMcp.Indexing.Decompilation;

/// <summary>
/// 把找不到的 Unity 組件參考導向 1.7 的 UnityStubs.dll。
///
/// <para>
/// 1.7 不再隨附 UnityEngine.*.dll，遊戲執行期以 RimWorld.runtimeconfig.json 的
/// WaylithClr.AssemblyNameRedirects 把這些名稱導向 UnityStubs。對著 1.6 編譯的 Mod
/// 仍參考 UnityEngine.CoreModule 等名稱，反編譯時不照做的話，所有 Unity 型別都解析不到。
/// 名稱規則與遊戲相同：UnityEngine*、Unity.*（Unity.Newtonsoft.Json 除外）與 Assembly-CSharp-firstpass。
/// </para>
/// <para>
/// 一律先交給原本的 resolver，找不到才用 UnityStubs：Mod 自帶的真正組件（例如 Unity.Mathematics.dll）
/// 有 UnityStubs 沒有的型別，必須優先。
/// </para>
/// </summary>
internal sealed class UnityStubsResolver(IAssemblyResolver inner, string stubsPath) : IAssemblyResolver
{
    internal const string StubsFileName = "UnityStubs.dll";

    // 同一個 Mod 常參考多個 Unity 模組，全部導向同一份映像，型別系統才不會看到重複的組件。
    private readonly Lazy<MetadataFile?> _stubs = new(() => Load(stubsPath));

    /// <summary>遊戲的受控組件目錄有 UnityStubs.dll 時包一層，否則原樣回傳。</summary>
    internal static IAssemblyResolver Wrap(IAssemblyResolver resolver, string? managedDirectory)
    {
        var stubs = managedDirectory is null ? null : Path.Combine(managedDirectory, StubsFileName);

        return stubs is not null && File.Exists(stubs) ? new UnityStubsResolver(resolver, stubs) : resolver;
    }

    public MetadataFile? Resolve(IAssemblyReference reference)
        => inner.Resolve(reference) ?? Fallback(reference);

    public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName)
        => inner.ResolveModule(mainModule, moduleName);

    public async Task<MetadataFile?> ResolveAsync(IAssemblyReference reference)
        => await inner.ResolveAsync(reference).ConfigureAwait(false) ?? Fallback(reference);

    public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName)
        => inner.ResolveModuleAsync(mainModule, moduleName);

    internal static bool IsRedirected(string name)
        => name.StartsWith("UnityEngine", StringComparison.Ordinal)
            || (name.StartsWith("Unity.", StringComparison.Ordinal) && name != "Unity.Newtonsoft.Json")
            || name == "Assembly-CSharp-firstpass";

    private MetadataFile? Fallback(IAssemblyReference reference)
        => IsRedirected(reference.Name) ? _stubs.Value : null;

    private static MetadataFile? Load(string path)
    {
        try
        {
            return MemberDecompiler.OpenImage(path);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 失敗型別取決於 ILSpy 內部（壞檔拋的是它自訂的例外），與 MemberDecompiler.TryCreate 同樣廣泛捕捉。
            // 結果與找不到參考相同：反編譯照做，只是 Unity 型別解析不到。
            return null;
        }
    }
}
