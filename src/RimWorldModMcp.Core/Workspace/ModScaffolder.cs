using System.Text;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Core.Workspace;

/// <summary>建立新 Mod 的目錄骨架。</summary>
public sealed class ModScaffolder(RimWorldLocator? locator = null)
{
    private static readonly string[] Folders = ["About", "Defs", "Patches", "Textures", "Sounds", "Source"];

    /// <summary>偵測不到遊戲版本時 supportedVersions 的退路。</summary>
    private const string FallbackGameVersion = GameVersion.Fallback;

    /// <summary>
    /// 在 <paramref name="parentPath"/> 底下建立一個新 Mod。
    /// </summary>
    /// <param name="withCode">是否一併產生 C# 專案骨架。</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "DI instance service method")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeSmell", "S2325:Methods that don't access instance data should be 'static'", Justification = "DI instance service method")]
    public string Create(string parentPath, string name, string packageId, bool withCode)
    {
        var normalisedId = packageId.Trim().ToLowerInvariant();

        // RimWorld 的 packageId 只接受這些字元；先擋掉才不會產生載入不了的 Mod。
        if (normalisedId.Length == 0 || !normalisedId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw new ArgumentException(
                "package_id allows only lowercase alphanumerics and . _ -, e.g. yourname.yourmod.",
                nameof(packageId));
        }

        var modPath = Path.Combine(PathText.ResolveDirectory(parentPath), SanitiseFolderName(name));

        if (Directory.Exists(modPath) || File.Exists(modPath))
        {
            throw new IOException($"Target already exists: {modPath}");
        }

        Directory.CreateDirectory(modPath);

        try
        {
            foreach (var folder in Folders)
            {
                Directory.CreateDirectory(Path.Combine(modPath, folder));
            }

            // supportedVersions 跟著實際安裝的遊戲走：寫死 1.6 的骨架在 1.5 上一載入就是版本不符警告。
            var gameVersion = GameVersion.ReadMajorMinor(locator?.Detect().InstallRoot) ?? FallbackGameVersion;
            File.WriteAllText(Path.Combine(modPath, "About", "About.xml"), AboutXml(name, normalisedId, gameVersion), new UTF8Encoding(false));

            if (withCode)
            {
                File.WriteAllText(
                    Path.Combine(modPath, "Source", normalisedId + ".csproj"),
                    CsProjTemplate,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // 建到一半失敗時要把殘骸清掉。留著半成品目錄的話，
            // 使用者修正問題後重試會撞上「目標已存在」而卡死。
            try
            {
                Directory.Delete(modPath, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // 清理失敗不應遮蔽原本的錯誤。
            }

            throw;
        }

        return modPath;
    }

    /// <summary>Windows 的保留裝置名，任何副檔名組合都不能當目錄名。</summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private static string SanitiseFolderName(string name)
    {
        var cleaned = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
            .Trim()
            // Windows 不接受結尾是句點或空白的目錄名（建得出來但很多 API 打不開）。
            .TrimEnd('.', ' ');

        if (cleaned.Length == 0)
        {
            throw new ArgumentException("name must not be empty or all illegal characters.", nameof(name));
        }

        // "." 與 ".." 是路徑導航元件；目前雖然會被「目標已存在」擋下，
        // 但那是巧合而不是設計——這裡要靠自己擋。
        if (cleaned is "." or ".." ||
            ReservedNames.Contains(Path.GetFileNameWithoutExtension(cleaned), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"name must not be a reserved name: {cleaned}", nameof(name));
        }

        return cleaned;
    }

    private static string AboutXml(string name, string packageId, string gameVersion) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <ModMetaData>
          <name>{System.Security.SecurityElement.Escape(name)}</name>
          <author>Unknown</author>
          <packageId>{packageId}</packageId>
          <supportedVersions>
            <li>{gameVersion}</li>
          </supportedVersions>
          <description>A RimWorld mod.</description>
        </ModMetaData>

        """;

    /// <summary>
    /// C# Mod 專案骨架。
    /// 目標框架必須是 net472——RimWorld 跑在 Unity Mono 上，其他 TFM 載入不了。
    /// 遊戲組件的位置由 RIMWORLD_MANAGED_DIR 環境變數提供，建置時由本工具注入。
    /// </summary>
    private const string CsProjTemplate = """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <TargetFramework>net472</TargetFramework>
            <LangVersion>latest</LangVersion>
            <Nullable>enable</Nullable>
            <ImplicitUsings>disable</ImplicitUsings>
            <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
          </PropertyGroup>

          <PropertyGroup Condition="'$(RimWorldManagedDir)' == ''">
            <RimWorldManagedDir>$(RIMWORLD_MANAGED_DIR)</RimWorldManagedDir>
          </PropertyGroup>

          <ItemGroup>
            <Reference Include="Assembly-CSharp">
              <HintPath>$(RimWorldManagedDir)\Assembly-CSharp.dll</HintPath>
              <Private>false</Private>
            </Reference>
            <Reference Include="UnityEngine.CoreModule">
              <HintPath>$(RimWorldManagedDir)\UnityEngine.CoreModule.dll</HintPath>
              <Private>false</Private>
            </Reference>
          </ItemGroup>

          <ItemGroup>
            <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies.net472" Version="1.0.3" PrivateAssets="all" />
          </ItemGroup>

        </Project>

        """;
}
