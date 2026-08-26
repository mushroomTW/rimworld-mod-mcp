using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Core.Workspace;

namespace RimWorldModMcp.Core.Tests;

public sealed class WorkspaceRegistryTests : IDisposable
{
    private readonly string _root;
    private readonly StoreDirectories _store;
    private readonly WorkspaceRegistry _registry;

    public WorkspaceRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-ws-" + Guid.NewGuid().ToString("n")[..12]);
        _store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _registry = new WorkspaceRegistry(_store);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        // 測試會建立目錄連結，遞迴刪除前要先拆掉，否則會刪到連結目標。
        foreach (var entry in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Reverse())
        {
            var info = new DirectoryInfo(entry);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(entry);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private string MakeDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void ConfiguredWorkspaceAllowsPathsInsideIt()
    {
        var workspace = MakeDirectory("workspace");
        var mod = Path.Combine(workspace, "MyMod");
        Directory.CreateDirectory(mod);

        _registry.Configure(workspace);

        Assert.Equal(PathGuard.Canonicalize(mod), _registry.AllowedMod(mod));
    }

    [Fact]
    public void WorkspaceRootItselfIsAllowed()
    {
        var workspace = MakeDirectory("workspace");
        _registry.Configure(workspace);

        Assert.Equal(PathGuard.Canonicalize(workspace), _registry.AllowedMod(workspace));
    }

    [Fact]
    public void PathOutsideAnyWorkspaceIsRejected()
    {
        var workspace = MakeDirectory("workspace");
        var outside = MakeDirectory("elsewhere");

        _registry.Configure(workspace);

        var error = Assert.Throws<UnauthorizedAccessException>(() => _registry.AllowedMod(outside));
        Assert.Contains("已登記的工作區", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParentTraversalIsRejected()
    {
        var workspace = MakeDirectory("workspace");
        MakeDirectory("secrets");
        Directory.CreateDirectory(Path.Combine(workspace, "MyMod"));

        _registry.Configure(workspace);

        var traversal = Path.Combine(workspace, "MyMod", "..", "..", "secrets");
        Assert.Throws<UnauthorizedAccessException>(() => _registry.AllowedMod(traversal));
    }

    /// <summary>
    /// 工作區裡放一個指向外部的目錄連結，不能因此取得外部的寫入權。
    ///
    /// <para>
    /// <c>Path.GetFullPath</c> 只做字串層面的正規化，不解析連結，
    /// 所以單純的前綴比對會讓這個路徑通過。必須解析到真實目標之後再比對。
    /// </para>
    /// <para>
    /// 連結用 <see cref="DirectoryLink"/> 建立而不是 <c>Directory.CreateSymbolicLink</c>：
    /// 後者在 Windows 上需要特殊權限而會直接失敗，junction 則不需要——
    /// 這也正是這個逃逸路徑在 Windows 上真實可行的原因，任何使用者都建得出來。
    /// </para>
    /// </summary>
    [Fact]
    public void LinkEscapingTheWorkspaceIsRejected()
    {
        var workspace = MakeDirectory("workspace");
        var outside = MakeDirectory("outside");

        var escape = Path.Combine(workspace, "escape");
        new DirectoryLink().EnsureLink(escape, outside);

        _registry.Configure(workspace);

        Assert.Throws<UnauthorizedAccessException>(() => _registry.AllowedMod(escape));
    }

    [Fact]
    public void SiblingDirectoryWithSharedPrefixIsRejected()
    {
        var workspace = MakeDirectory("workspace");
        var sibling = MakeDirectory("workspace-evil");

        _registry.Configure(workspace);

        // 字串前綴比對會誤放這個目錄，必須以路徑分隔符為邊界。
        Assert.Throws<UnauthorizedAccessException>(() => _registry.AllowedMod(sibling));
    }

    [Fact]
    public void ConfigureIsIdempotentAndPersists()
    {
        var workspace = MakeDirectory("workspace");

        _registry.Configure(workspace);
        _registry.Configure(workspace);

        Assert.Single(_registry.Roots());

        // 換一個實例讀，確認真的落到磁碟上。
        Assert.Single(new WorkspaceRegistry(_store).Roots());
    }

    [Fact]
    public void MissingDirectoryIsRejected()
    {
        Assert.Throws<DirectoryNotFoundException>(() => _registry.Configure(Path.Combine(_root, "nope")));
    }
}
