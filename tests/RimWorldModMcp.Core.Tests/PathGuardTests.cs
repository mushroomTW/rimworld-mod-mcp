using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Core.Workspace;

namespace RimWorldModMcp.Core.Tests;

/// <summary>
/// 寫入邊界的核心不變式。
///
/// <para>
/// 只解析葉節點是不夠的：<c>工作區\link\子目錄</c> 這種「中間元件是連結」的
/// 路徑，葉節點本身不是 reparse point，會原樣通過前綴比對——而 junction 在
/// Windows 上不需要任何權限就建得出來。
/// </para>
/// </summary>
public sealed class PathGuardTests : IDisposable
{
    private readonly string _root;
    private readonly string _workspace;
    private readonly string _outside;

    public PathGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-guard-" + Guid.NewGuid().ToString("n")[..12]);
        _workspace = Path.Combine(_root, "Workspace");
        _outside = Path.Combine(_root, "Outside");

        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(Path.Combine(_outside, "Secret"));
    }

    public void Dispose()
    {
        // 先移除連結再遞迴刪除，避免刪除穿透連結。
        var link = Path.Combine(_workspace, "link");

        if (Directory.Exists(link))
        {
            Directory.Delete(link);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void LinkedIntermediateComponentResolvesToTheRealLocation()
    {
        var link = Path.Combine(_workspace, "link");
        new DirectoryLink().EnsureLink(link, _outside);

        var candidate = PathGuard.Canonicalize(Path.Combine(link, "Secret"));

        Assert.False(
            PathGuard.IsWithin(candidate, PathGuard.Canonicalize(_workspace)),
            "指向工作區外的連結底下的路徑，不可以被判定在工作區內");

        Assert.True(
            PathGuard.IsWithin(candidate, PathGuard.Canonicalize(_outside)),
            "解析後應落在連結的真實目標底下");
    }

    [Fact]
    public void PlainPathsStayInsideTheWorkspace()
    {
        var sub = Path.Combine(_workspace, "MyMod");
        Directory.CreateDirectory(sub);

        Assert.True(PathGuard.IsWithin(PathGuard.Canonicalize(sub), PathGuard.Canonicalize(_workspace)));
    }

    [Fact]
    public void SiblingWithSharedPrefixIsNotWithin()
    {
        var sibling = Path.Combine(_root, "Workspace-evil");
        Directory.CreateDirectory(sibling);

        Assert.False(PathGuard.IsWithin(PathGuard.Canonicalize(sibling), PathGuard.Canonicalize(_workspace)));
    }
}
