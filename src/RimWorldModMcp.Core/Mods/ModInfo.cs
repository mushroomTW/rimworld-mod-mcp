using System.Xml;
using System.Xml.Linq;

namespace RimWorldModMcp.Core.Mods;

/// <summary>一個已安裝或工作區中的 Mod。</summary>
public sealed record ModInfo
{
    public required string PackageId { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    /// <summary><c>local</c>、<c>workshop</c>、<c>core</c>、<c>expansion</c> 或 <c>workspace</c>。</summary>
    public required string Source { get; init; }

    public IReadOnlyList<string> Dependencies { get; init; } = [];

    public IReadOnlyList<string> LoadAfter { get; init; } = [];

    public IReadOnlyList<string> LoadBefore { get; init; } = [];

    public IReadOnlyList<string> IncompatibleWith { get; init; } = [];

    public IReadOnlyList<string> SupportedVersions { get; init; } = [];
}

/// <summary>解析 Mod 的 About.xml。</summary>
public static class AboutXml
{
    /// <summary>
    /// 讀取一個 Mod 目錄的 About.xml。
    /// 缺少 packageId 或檔案無法解析時回傳 <c>null</c>——那代表這個目錄不是 Mod。
    /// </summary>
    public static ModInfo? Parse(string modDirectory, string source)
    {
        var aboutPath = System.IO.Path.Combine(modDirectory, "About", "About.xml");

        XDocument document;

        try
        {
            using var stream = File.OpenRead(aboutPath);
            document = XDocument.Load(stream);
        }
        catch (Exception e) when (e is IOException or XmlException or UnauthorizedAccessException)
        {
            return null;
        }

        var root = document.Root;

        if (root is null)
        {
            return null;
        }

        var packageId = root.Element("packageId")?.Value.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(packageId))
        {
            return null;
        }

        return new ModInfo
        {
            PackageId = packageId,
            // 沒寫 name 就退回目錄名，總比顯示空白好。
            Name = root.Element("name")?.Value.Trim() is { Length: > 0 } name
                ? name
                : System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(modDirectory)),
            Path = modDirectory,
            Source = source,
            Dependencies = PackageIds(root.Element("modDependencies")),
            LoadAfter = PackageIds(root.Element("loadAfter")),
            LoadBefore = PackageIds(root.Element("loadBefore")),
            IncompatibleWith = PackageIds(root.Element("incompatibleWith")),
            SupportedVersions = [.. (root.Element("supportedVersions")?.Elements("li") ?? []).Select(e => e.Value.Trim())],
        };
    }

    /// <summary>
    /// 讀出一個清單元素裡的 packageId。
    /// 同時接受 <c>&lt;li&gt;&lt;packageId&gt;x&lt;/packageId&gt;&lt;/li&gt;</c> 與
    /// <c>&lt;li&gt;x&lt;/li&gt;</c> 兩種寫法——兩種在實際的 Mod 裡都很常見。
    /// </summary>
    private static IReadOnlyList<string> PackageIds(XElement? container)
    {
        if (container is null)
        {
            return [];
        }

        var ids = new List<string>();

        foreach (var item in container.Elements("li"))
        {
            var value = item.Element("packageId")?.Value ?? item.Value;
            value = value.Trim().ToLowerInvariant();

            if (value.Length > 0)
            {
                ids.Add(value);
            }
        }

        return ids;
    }
}
