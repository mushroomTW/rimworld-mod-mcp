using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Core.Workspace;

namespace RimWorldModMcp.Core.Tests;

public sealed class WorkspaceRegistryTests : IDisposable
{
    private readonly string _root;

    public WorkspaceRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-ws-" + Guid.NewGuid().ToString("n")[..12]);
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
    public void TrustedWorkspaceAllowsPathsInsideIt()
    {
        var workspace = MakeDirectory("workspace");
        var mod = Path.Combine(workspace, "MyMod");
        Directory.CreateDirectory(mod);

        var registry = new WorkspaceRegistry([workspace]);

        Assert.Equal(PathGuard.Canonicalize(mod), registry.AllowedMod(mod).Value);
    }

    [Fact]
    public void WorkspaceRootItselfIsAllowed()
    {
        var workspace = MakeDirectory("workspace");
        var registry = new WorkspaceRegistry([workspace]);

        Assert.Equal(PathGuard.Canonicalize(workspace), registry.AllowedMod(workspace).Value);
    }

    [Fact]
    public void PathOutsideAnyWorkspaceIsRejected()
    {
        var workspace = MakeDirectory("workspace");
        var outside = MakeDirectory("elsewhere");

        var registry = new WorkspaceRegistry([workspace]);

        var error = Assert.Throws<UnauthorizedAccessException>(() => registry.AllowedMod(outside));
        Assert.Contains("--workspace", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoWorkspaceMeansNothingIsWritable()
    {
        var mod = MakeDirectory("mod");

        // 沒給 --workspace 就只剩唯讀工具能用，這是刻意的預設。
        var registry = new WorkspaceRegistry([]);

        Assert.Empty(registry.Roots());
        Assert.Throws<UnauthorizedAccessException>(() => registry.AllowedMod(mod));
    }

    [Fact]
    public void ParentTraversalIsRejected()
    {
        var workspace = MakeDirectory("workspace");
        MakeDirectory("secrets");
        Directory.CreateDirectory(Path.Combine(workspace, "MyMod"));

        var registry = new WorkspaceRegistry([workspace]);

        var traversal = Path.Combine(workspace, "MyMod", "..", "..", "secrets");
        Assert.Throws<UnauthorizedAccessException>(() => registry.AllowedMod(traversal));
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

        var registry = new WorkspaceRegistry([workspace]);

        Assert.Throws<UnauthorizedAccessException>(() => registry.AllowedMod(escape));
    }

    [Fact]
    public void SiblingDirectoryWithSharedPrefixIsRejected()
    {
        var workspace = MakeDirectory("workspace");
        var sibling = MakeDirectory("workspace-evil");

        var registry = new WorkspaceRegistry([workspace]);

        // 字串前綴比對會誤放這個目錄，必須以路徑分隔符為邊界。
        Assert.Throws<UnauthorizedAccessException>(() => registry.AllowedMod(sibling));
    }

    [Fact]
    public void DuplicateRootsCollapseToOne()
    {
        var workspace = MakeDirectory("workspace");

        // 同一個目錄給兩次（例如一次帶尾端分隔符）只算一條。
        var registry = new WorkspaceRegistry([workspace, workspace + Path.DirectorySeparatorChar]);

        Assert.Single(registry.Roots());
    }

    [Fact]
    public void MissingDirectoryIsRejectedAtConstruction()
    {
        // 設定打錯路徑要在啟動時就失敗，不能靜默變成「沒有工作區」。
        Assert.Throws<DirectoryNotFoundException>(() => new WorkspaceRegistry([Path.Combine(_root, "nope")]));
    }
}
