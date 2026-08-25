using System.Text;

namespace RimWorldModMcp.Core.Workspace;

/// <summary>建立新 Mod 的目錄骨架。</summary>
public sealed class ModScaffolder(WorkspaceRegistry workspaces)
{
    private static readonly string[] Folders = ["About", "Defs", "Patches", "Textures", "Sounds", "Source"];

    /// <summary>
    /// 在已登記的工作區根目錄下建立一個新 Mod。
    /// </summary>
    /// <param name="withCode">是否一併產生 C# 專案骨架。</param>
    public string Create(string workspacePath, string name, string packageId, bool withCode)
    {
        var normalisedId = packageId.Trim().ToLowerInvariant();

        // RimWorld 的 packageId 只接受這些字元；先擋掉才不會產生載入不了的 Mod。
        if (normalisedId.Length == 0 || !normalisedId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw new ArgumentException(
                "package_id 只能包含小寫英數字與 . _ - ，例如 yourname.yourmod。",
                nameof(packageId));
        }

        var workspace = PathGuard.Canonicalize(workspacePath);

        // 必須恰好是已登記的根目錄本身，不接受子目錄——避免在別人的 Mod 裡面又建一個 Mod。
        if (!workspaces.Roots().Any(root => string.Equals(
                PathGuard.Canonicalize(root),
                workspace,
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException("workspace 必須是已登記的工作區根目錄；請先呼叫 configure_workspace。");
        }

        var modPath = Path.Combine(workspace, SanitiseFolderName(name));

        if (Directory.Exists(modPath) || File.Exists(modPath))
        {
            throw new IOException($"目標已存在：{modPath}");
        }

        Directory.CreateDirectory(modPath);

        foreach (var folder in Folders)
        {
            Directory.CreateDirectory(Path.Combine(modPath, folder));
        }

        File.WriteAllText(Path.Combine(modPath, "About", "About.xml"), AboutXml(name, normalisedId), new UTF8Encoding(false));

        if (withCode)
        {
            File.WriteAllText(
                Path.Combine(modPath, "Source", normalisedId + ".csproj"),
                CsProj(),
                new UTF8Encoding(false));
        }

        return modPath;
    }

    private static string SanitiseFolderName(string name)
    {
        var cleaned = new string([.. name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))]).Trim();

        return cleaned.Length > 0
            ? cleaned
            : throw new ArgumentException("name 不能是空的或只有非法字元。", nameof(name));
    }

    private static string AboutXml(string name, string packageId) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <ModMetaData>
          <name>{System.Security.SecurityElement.Escape(name)}</name>
          <author>Unknown</author>
          <packageId>{packageId}</packageId>
          <supportedVersions>
            <li>1.6</li>
          </supportedVersions>
          <description>A RimWorld mod.</description>
        </ModMetaData>

        """;

    /// <summary>
    /// C# Mod 專案骨架。
    /// 目標框架必須是 net472——RimWorld 跑在 Unity Mono 上，其他 TFM 載入不了。
    /// 遊戲組件的位置由 RIMWORLD_MANAGED_DIR 環境變數提供，建置時由本工具注入。
    /// </summary>
    private static string CsProj() => """
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
