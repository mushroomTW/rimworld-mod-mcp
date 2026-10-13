using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Workspace;

namespace RimWorldModMcp.Core.Tests;

/// <summary>
/// 本工具只支援 1.7 的 .NET 安裝結構：受控組件在安裝根目錄、日誌在 Logs/。
/// 以假安裝目錄與假家目錄驗證偵測結果。
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class RimWorldLocatorTests : IDisposable
{
    private static readonly string[] Variables = ["RIMWORLD_MOD_MCP_GAME_PATH", "RIMWORLD_MOD_MCP_PLAYER_LOG", "USERPROFILE", "HOME", "XDG_CONFIG_HOME"];

    private readonly string _root;
    private readonly string _install;
    private readonly string _home;
    private readonly Dictionary<string, string?> _previous;

    public RimWorldLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-locator-" + Guid.NewGuid().ToString("n")[..12]);
        _install = Path.Combine(_root, "RimWorld");
        _home = Path.Combine(_root, "home");

        Directory.CreateDirectory(Path.Combine(_install, "Data", "Core", "Defs"));
        Directory.CreateDirectory(_home);

        _previous = Variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);

        Environment.SetEnvironmentVariable("RIMWORLD_MOD_MCP_GAME_PATH", _install);
        Environment.SetEnvironmentVariable("RIMWORLD_MOD_MCP_PLAYER_LOG", null);
        Environment.SetEnvironmentVariable("USERPROFILE", _home);
        Environment.SetEnvironmentVariable("HOME", _home);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// 1.7 在 macOS、Linux 寫日誌的根目錄（取自反編譯的 RimWorldWaylith.Bootstrap）。
    /// Windows 的 LocalLow 取自系統 known folder，無法以環境變數隔離，用到它的測試在 Windows 上略過（Linux CI 涵蓋）。
    /// </summary>
    private string LogRoot()
    {
        return OperatingSystem.IsMacOS()
            ? Path.Combine(_home, "Library", "Application Support", "Ludeon Studios", "RimWorld by Ludeon Studios")
            : Path.Combine(_home, ".config", "unity3d", "Ludeon Studios", "RimWorld by Ludeon Studios");
    }

    /// <summary>受控組件所在目錄：安裝根目錄，macOS 則是 app bundle 的 Contents/MacOS（未驗證的推定）。</summary>
    private string ManagedDirectory()
        => OperatingSystem.IsMacOS() ? Path.Combine(_install, "RimWorld.app", "Contents", "MacOS") : _install;

    private void CreateDotNetInstall()
    {
        var managed = ManagedDirectory();
        Directory.CreateDirectory(managed);
        File.WriteAllBytes(Path.Combine(managed, "Assembly-CSharp.dll"), []);
        File.WriteAllBytes(Path.Combine(managed, "UnityStubs.dll"), []);
        File.WriteAllBytes(Path.Combine(managed, OperatingSystem.IsWindows() ? "RimWorld.exe" : "RimWorld"), []);
    }

    [Fact]
    public void DotNetLayoutUsesTheInstallRootAsManagedDirectory()
    {
        CreateDotNetInstall();

        var paths = new RimWorldLocator().Detect();

        Assert.Equal(Path.GetFullPath(ManagedDirectory()), paths.ManagedDir);
        Assert.Equal(Path.Combine(Path.GetFullPath(ManagedDirectory()), OperatingSystem.IsWindows() ? "RimWorld.exe" : "RimWorld"), paths.Executable);
        Assert.True(paths.HasManagedAndData);
        Assert.Null(paths.UnsupportedVersion);
    }

    /// <summary>1.6 的 Unity 結構不再支援：不回報受控目錄，並標出不支援的版本。</summary>
    [Fact]
    public void UnityLayoutOfAnOlderGameIsReportedAsUnsupported()
    {
        File.WriteAllText(Path.Combine(_install, "Version.txt"), "1.6.4871 rev590");
        var managed = Path.Combine(_install, "RimWorldWin64_Data", "Managed");
        Directory.CreateDirectory(managed);
        File.WriteAllBytes(Path.Combine(managed, "Assembly-CSharp.dll"), []);

        var paths = new RimWorldLocator().Detect();

        Assert.Null(paths.ManagedDir);
        Assert.Null(paths.Executable);
        Assert.False(paths.HasManagedAndData);
        Assert.Equal("1.6", paths.UnsupportedVersion);
        Assert.Equal(Path.GetFullPath(_install), paths.InstallRoot);
    }

    /// <summary>Version.txt 讀不到時，Unity 的 *_Data/Managed 結構本身就代表 1.7 以前的版本。</summary>
    [Fact]
    public void UnityLayoutWithoutAVersionIsReportedAsUnsupported()
    {
        Directory.CreateDirectory(Path.Combine(_install, "RimWorldWin64_Data", "Managed"));

        var paths = new RimWorldLocator().Detect();

        Assert.Equal("pre-1.7", paths.UnsupportedVersion);
        Assert.Equal("RimWorld pre-1.7 detected; this tool supports RimWorld 1.7 only.", paths.UnsupportedMessage);
    }

    /// <summary>舊版遊戲不能套 net10.0 + UnityStubs 的骨架：create_mod 直接說明不支援，而不是產生建置不了的專案。</summary>
    [Fact]
    public void ScaffolderRefusesAnUnsupportedGame()
    {
        File.WriteAllText(Path.Combine(_install, "Version.txt"), "1.6.4871 rev590");
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);

        var error = Assert.Throws<NotSupportedException>(() => new ModScaffolder(new RimWorldLocator()).Create(workspace, "OldMod", "author.oldmod", withCode: true));

        Assert.Contains("supports RimWorld 1.7 only", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(workspace, "OldMod")));
    }

    /// <summary>版本無法讀取時不能硬說是舊版：只回報缺組件。</summary>
    [Fact]
    public void MissingAssembliesWithoutAVersionAreNotReportedAsUnsupported()
    {
        var paths = new RimWorldLocator().Detect();

        Assert.Null(paths.ManagedDir);
        Assert.Null(paths.UnsupportedVersion);
    }

    /// <summary>
    /// 1.7 還沒啟動過時 Logs/Player.log 不存在，同目錄卻留著 1.6 的 Player.log。
    /// 必須指向之後會被寫入的 Logs/Player.log，不能退回那份不再更新的舊檔。
    /// </summary>
    [Fact]
    public void DotNetLayoutPointsAtLogsPlayerLogBeforeTheFirstLaunch()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        CreateDotNetInstall();
        var logRoot = LogRoot();
        Directory.CreateDirectory(logRoot);
        File.WriteAllText(Path.Combine(logRoot, "Player.log"), "1.6 log");

        var paths = new RimWorldLocator().Detect();

        Assert.Equal(Path.Combine(logRoot, "Logs", "Player.log"), paths.PlayerLog);
    }

    /// <summary>1.7 在 Linux 上遵循 XDG_CONFIG_HOME（1.6 的 Unity 不會），日誌與設定檔都在那裡。</summary>
    [Fact]
    public void DotNetLayoutOnLinuxHonoursXdgConfigHome()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        CreateDotNetInstall();
        var xdg = Path.Combine(_root, "xdg");
        var logRoot = Path.Combine(xdg, "unity3d", "Ludeon Studios", "RimWorld by Ludeon Studios");
        Directory.CreateDirectory(Path.Combine(logRoot, "Config"));
        File.WriteAllText(Path.Combine(logRoot, "Config", "ModsConfig.xml"), "<ModsConfigData />");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdg);

        var paths = new RimWorldLocator().Detect();

        Assert.Equal(Path.Combine(logRoot, "Logs", "Player.log"), paths.PlayerLog);
        Assert.Equal(Path.Combine(logRoot, "Config", "ModsConfig.xml"), paths.ModsConfig);
    }

    [Fact]
    public void DotNetLayoutWithoutAnyDataFolderHasNoPlayerLog()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        CreateDotNetInstall();

        Assert.Null(new RimWorldLocator().Detect().PlayerLog);
    }

    /// <summary>1.7 的遊戲跑在 .NET 10 上，create_mod 的 C# 骨架要以 net10.0 為目標並參考 UnityStubs。</summary>
    [Fact]
    public void ScaffolderGeneratesANet10ProjectForTheDotNetGame()
    {
        CreateDotNetInstall();
        File.WriteAllText(Path.Combine(_install, "Version.txt"), "1.7.4976 rev1043");
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);

        var mod = new ModScaffolder(new RimWorldLocator()).Create(workspace, "CodeMod", "author.codemod", withCode: true);

        var project = File.ReadAllText(Path.Combine(mod, "Source", "author.codemod.csproj"));
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", project, StringComparison.Ordinal);
        Assert.Contains(@"$(RimWorldManagedDir)\UnityStubs.dll", project, StringComparison.Ordinal);
        Assert.DoesNotContain("UnityEngine.CoreModule", project, StringComparison.Ordinal);
        Assert.Contains("<li>1.7</li>", File.ReadAllText(Path.Combine(mod, "About", "About.xml")), StringComparison.Ordinal);
    }
}

/// <summary>改寫行程共用環境變數（遊戲路徑、家目錄）的測試不能與其他測試平行跑。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "ProcessEnvironment";
}
