using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Workspace;

namespace RimWorldModMcp.Core.Tests;

public sealed class ModScaffolderTests : IDisposable
{
    private readonly string _root;
    private readonly string _workspace;
    private readonly WorkspaceRegistry _registry;
    private readonly ModScaffolder _scaffolder;

    public ModScaffolderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-scaffold-" + Guid.NewGuid().ToString("n")[..12]);
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);

        var store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _registry = new WorkspaceRegistry(store);
        _scaffolder = new ModScaffolder(_registry);

        _registry.Configure(_workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void CreatesTheStandardModLayout()
    {
        var mod = _scaffolder.Create(_workspace, "MyMod", "author.mymod", withCode: false);

        Assert.True(Directory.Exists(mod));

        foreach (var folder in (ReadOnlySpan<string>)["About", "Defs", "Patches", "Textures", "Sounds", "Source"])
        {
            Assert.True(Directory.Exists(Path.Combine(mod, folder)), $"缺少 {folder} 目錄");
        }

        var about = File.ReadAllText(Path.Combine(mod, "About", "About.xml"));
        Assert.Contains("<packageId>author.mymod</packageId>", about, StringComparison.Ordinal);
        Assert.Contains("<name>MyMod</name>", about, StringComparison.Ordinal);
    }

    [Fact]
    public void WithCodeGeneratesANet472Project()
    {
        var mod = _scaffolder.Create(_workspace, "CodeMod", "author.codemod", withCode: true);

        var project = Path.Combine(mod, "Source", "author.codemod.csproj");
        Assert.True(File.Exists(project));

        var content = File.ReadAllText(project);

        // net472 是硬性要求：RimWorld 跑在 Unity Mono 上，載入不了其他目標框架。
        Assert.Contains("<TargetFramework>net472</TargetFramework>", content, StringComparison.Ordinal);
        Assert.Contains("RIMWORLD_MANAGED_DIR", content, StringComparison.Ordinal);
    }

    [Fact]
    public void XmlOnlyModHasNoProject()
    {
        var mod = _scaffolder.Create(_workspace, "XmlMod", "author.xmlmod", withCode: false);

        Assert.Empty(Directory.GetFiles(Path.Combine(mod, "Source"), "*.csproj"));
    }

    [Fact]
    public void PackageIdIsNormalisedToLowercase()
    {
        var mod = _scaffolder.Create(_workspace, "CaseMod", "Author.CaseMod", withCode: false);

        var about = File.ReadAllText(Path.Combine(mod, "About", "About.xml"));
        Assert.Contains("<packageId>author.casemod</packageId>", about, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("")]
    [InlineData("has!bang")]
    public void InvalidPackageIdIsRejected(string packageId)
    {
        Assert.Throws<ArgumentException>(() => _scaffolder.Create(_workspace, "Mod", packageId, withCode: false));
    }

    [Fact]
    public void UnregisteredWorkspaceIsRejected()
    {
        var other = Path.Combine(_root, "not-registered");
        Directory.CreateDirectory(other);

        Assert.Throws<UnauthorizedAccessException>(() => _scaffolder.Create(other, "Mod", "author.mod", withCode: false));
    }

    /// <summary>workspace 參數必須是已登記的根目錄本身，不接受它底下的子目錄。</summary>
    [Fact]
    public void SubdirectoryOfARegisteredRootIsRejected()
    {
        var nested = Path.Combine(_workspace, "nested");
        Directory.CreateDirectory(nested);

        Assert.Throws<UnauthorizedAccessException>(() => _scaffolder.Create(nested, "Mod", "author.mod", withCode: false));
    }

    [Fact]
    public void ExistingTargetIsRejected()
    {
        _scaffolder.Create(_workspace, "Dupe", "author.dupe", withCode: false);

        Assert.Throws<IOException>(() => _scaffolder.Create(_workspace, "Dupe", "author.dupe2", withCode: false));
    }

    /// <summary>
    /// 重現端對端測試中遇到的情境：以工具鏈的實際路徑形式（Windows 反斜線、
    /// 可能帶大小寫差異）登記工作區後立刻建立 Mod。
    /// </summary>
    [Fact]
    public void WorksWithThePathFormTheToolLayerPasses()
    {
        var asPassed = _workspace.Replace('/', Path.DirectorySeparatorChar);

        var mod = _scaffolder.Create(asPassed, "ToolMod", "author.toolmod", withCode: true);

        Assert.True(Directory.Exists(mod));
    }
}
