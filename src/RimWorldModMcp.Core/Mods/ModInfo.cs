using System.Xml;
using System.Xml.Linq;

namespace RimWorldModMcp.Core.Mods;

/// <summary>一個已安裝或工作區中的 Mod。</summary>
public sealed record ModInfo
{
    public required string PackageId { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    /// <summary>local、workshop、core、expansion 或 workspace。</summary>
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
    /// 缺少 packageId 或檔案無法解析時回傳 null——那代表這個目錄不是 Mod。
    /// </summary>
    /// <param name="gameVersion">遊戲的 major.minor（例如 1.6）；給了才會套用 *ByVersion 清單。</param>
    public static ModInfo? Parse(string modDirectory, string source, string? gameVersion = null)
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
            Dependencies = PackageIds(Versioned(root, "modDependencies", gameVersion)),
            LoadAfter = PackageIds(Versioned(root, "loadAfter", gameVersion)),
            LoadBefore = PackageIds(Versioned(root, "loadBefore", gameVersion)),
            IncompatibleWith = PackageIds(Versioned(root, "incompatibleWith", gameVersion)),
            SupportedVersions = [.. (root.Element("supportedVersions")?.Elements("li") ?? []).Select(e => e.Value.Trim())],
        };
    }

    /// <summary>
    /// 仿 RimWorld 的 ModMetaData.InitVersionedData：＜name＞ByVersion 底下若有目前版本的
    /// 條目（元素名轉小寫、去掉開頭的 v，例如 ＜v1.6＞），它**取代**基本清單；
    /// 同版本重複時取第一個。沒有對應條目就用基本清單。
    /// </summary>
    private static XElement? Versioned(XElement root, string name, string? gameVersion)
    {
        if (gameVersion is not null)
        {
            foreach (var entry in root.Element(name + "ByVersion")?.Elements() ?? [])
            {
                var version = entry.Name.LocalName.ToLowerInvariant();

                if (version.StartsWith('v'))
                {
                    version = version[1..];
                }

                if (version == gameVersion)
                {
                    return entry;
                }
            }
        }

        return root.Element(name);
    }

    /// <summary>
    /// 讀出一個清單元素裡的 packageId。
    /// 同時接受 ＜li＞＜packageId＞x＜/packageId＞＜/li＞ 與
    /// ＜li＞x＜/li＞ 兩種寫法——兩種在實際的 Mod 裡都很常見。
    /// </summary>
    private static List<string> PackageIds(XElement? container)
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
