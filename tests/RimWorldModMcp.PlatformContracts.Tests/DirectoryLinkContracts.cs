using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.PlatformContracts.Tests;

/// <summary>
/// 目錄連結的平台契約。
///
/// <para>
/// 這些契約若在某個 OS 上悄悄改變，清理邏輯會靜默失效——最壞的情況是把使用者的
/// Mod 原始碼刪掉。因此每一條都必須在三個 OS 的 CI 上真的執行，
/// 不使用 Skip：Windows 專屬的事實改用雙分支斷言，讓每個平台都斷言到真實行為。
/// </para>
/// </summary>
public sealed class DirectoryLinkContracts : IDisposable
{
    private readonly string _root;
    private readonly string _target;

    public DirectoryLinkContracts()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-link-" + Guid.NewGuid().ToString("n")[..12]);
        _target = Path.Combine(_root, "MyMod");
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "About.xml"), "<ModMetaData />");
    }

    public void Dispose()
    {
        // 測試自身的清理必須跟正式程式碼一樣小心：先拆掉所有 reparse point，
        // 否則遞迴刪除會走進連結，把 _target 的內容一起帶走。
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var entry in Directory.GetDirectories(_root))
        {
            var info = new DirectoryInfo(entry);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(entry);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private string LinkPath(string suffix = "Test") => Path.Combine(_root, DirectoryLink.LinkPrefix + suffix);

    /// <summary>
    /// 契約：建立連結不需要管理員權限。
    /// 這是選用 junction 而非 symlink 的全部理由，在 Windows 上若這條紅了，
    /// 代表實作退化成 symlink，沒有開發者模式的使用者會完全無法使用測試功能。
    /// </summary>
    [Fact]
    public void CreatingALinkDoesNotRequireElevation()
    {
        var link = LinkPath();

        DirectoryLink.EnsureLink(link, _target);

        Assert.True(Directory.Exists(link));
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
        // 連結必須可穿透讀到目標內容。
        Assert.True(File.Exists(Path.Combine(link, "About.xml")));
    }

    /// <summary>契約：對自己建立、指向同一目標的連結重複呼叫不得拋出。</summary>
    [Fact]
    public void EnsureLinkIsIdempotentForItsOwnLink()
    {
        var link = LinkPath();

        DirectoryLink.EnsureLink(link, _target);
        DirectoryLink.EnsureLink(link, _target);

        Assert.True(DirectoryLink.IsOwnedLink(link, _target));
    }

    /// <summary>
    /// 契約：連結的三重防線（前綴、reparse point、解析後目標相符）在每個平台各自成立。
    ///
    /// <para>
    /// Python 版這條是 Windows-only 的 is_symlink() is False 斷言。
    /// 在 .NET 改成斷言每一重防線的實際依據——ReparsePoint 屬性與 ResolveLinkTarget——
    /// 讓三個平台都能驗到同一組不變式。
    /// </para>
    /// </summary>
    [Fact]
    public void OwnedLinkDetectionMatchesPlatformSemantics()
    {
        var link = LinkPath();
        DirectoryLink.EnsureLink(link, _target);

        var info = new DirectoryInfo(link);

        Assert.True(info.Attributes.HasFlag(FileAttributes.ReparsePoint));

        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
        Assert.NotNull(resolved);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_target)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved!.FullName)));

        Assert.True(DirectoryLink.IsOwnedLink(link, _target));
    }

    /// <summary>契約：已存在但指向別處的連結不得被覆寫。</summary>
    [Fact]
    public void EnsureLinkRefusesToOverwriteALinkPointingElsewhere()
    {
        var other = Path.Combine(_root, "OtherMod");
        Directory.CreateDirectory(other);

        var link = LinkPath();
        DirectoryLink.EnsureLink(link, other);

        Assert.Throws<IOException>(() => DirectoryLink.EnsureLink(link, _target));

        // 失敗之後原本的連結必須完好，不能被拆掉一半。
        Assert.True(DirectoryLink.IsOwnedLink(link, other));
    }

    /// <summary>
    /// 契約：移除連結只拆掉連結本身，目標目錄的內容必須完好。
    ///
    /// <para>
    /// 這是整組契約裡最重要的一條。Directory.Delete(path, recursive: true)
    /// 會遞迴走進 junction 把目標內容刪光，等於毀掉使用者的 Mod 原始碼。
    /// 這條測試就是那個行為的防線。
    /// </para>
    /// </summary>
    [Fact]
    public void RemoveLinkDeletesTheLinkAndKeepsTheMod()
    {
        var link = LinkPath();
        DirectoryLink.EnsureLink(link, _target);

        DirectoryLink.RemoveLink(link, _target);

        Assert.False(Directory.Exists(link));
        Assert.True(Directory.Exists(_target));
        Assert.True(File.Exists(Path.Combine(_target, "About.xml")));
    }

    /// <summary>契約：沒有本工具前綴的目錄，即使位置吻合也絕不能被刪。</summary>
    [Fact]
    public void RemoveLinkIgnoresDirectoriesItDidNotCreate()
    {
        var intruder = Path.Combine(_root, "SomeUsersOwnMod");
        Directory.CreateDirectory(intruder);
        File.WriteAllText(Path.Combine(intruder, "About.xml"), "<ModMetaData />");

        var result = DirectoryLink.RemoveLink(intruder, _target);

        Assert.Equal(LinkRemoval.NotOurs, result);
        Assert.True(Directory.Exists(intruder));
        Assert.True(File.Exists(Path.Combine(intruder, "About.xml")));
    }

    /// <summary>
    /// 契約：連結目標位於「會被重新導向的路徑」底下時，仍必須被認出是自家連結。
    ///
    /// <para>
    /// 這條是端對端測試抓到的真實迴歸。ResolveLinkTarget(returnFinalTarget: true)
    /// 會穿透所有層級的重新導向（企業資料夾重新導向、OneDrive 已知資料夾移動、
    /// 打包應用程式的容器虛擬化），使得完全解析後的路徑與當初寫入 reparse point
    /// 的字串完全不同。當時只解析比對的其中一邊，導致自家連結被誤判成別人的，
    /// 清理時被跳過，在使用者的 Mods 目錄留下孤兒連結。
    /// </para>
    /// <para>
    /// 這裡用「連結指向另一個連結」來模擬同樣的不對稱：中間層一旦被完全解析，
    /// 只解析單邊的比對就會失敗。
    /// </para>
    /// </summary>
    [Fact]
    public void OwnedLinkIsRecognisedWhenTheTargetIsItselfRedirected()
    {
        var real = Path.Combine(_root, "RealLocation");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "About.xml"), "<ModMetaData />");

        // 中間層：一個指向真實位置的連結，扮演被重新導向的路徑。
        var redirected = Path.Combine(_root, DirectoryLink.LinkPrefix + "Redirected");
        DirectoryLink.EnsureLink(redirected, real);

        // 我們建立的連結指向那個「被重新導向的路徑」，而不是最終位置。
        var link = LinkPath("ViaRedirect");
        DirectoryLink.EnsureLink(link, redirected);

        Assert.True(DirectoryLink.IsOwnedLink(link, redirected), "應認出指向重新導向路徑的自家連結");

        DirectoryLink.RemoveLink(link, redirected);

        Assert.False(Directory.Exists(link));
        // 中間層與真實內容都必須完好。
        Assert.True(Directory.Exists(redirected));
        Assert.True(File.Exists(Path.Combine(real, "About.xml")));
    }

    /// <summary>契約：移除結果要能區分「已移除」「不是我們的」「失敗」，不能靜默當成成功。</summary>
    [Fact]
    public void RemoveLinkReportsWhetherTheLinkIsGone()
    {
        var link = LinkPath("Reported");
        DirectoryLink.EnsureLink(link, _target);

        Assert.Equal(LinkRemoval.Removed, DirectoryLink.RemoveLink(link, _target));
        Assert.False(Directory.Exists(link));

        // 已經不存在的連結再移除一次也算成功。
        Assert.Equal(LinkRemoval.Removed, DirectoryLink.RemoveLink(link, _target));
    }
}
