using RimWorldModMcp.Core.Building;

namespace RimWorldModMcp.Core.Tests;

/// <summary>
/// Windows 上 dotnet build 的輸出以 CRLF 換行；舊的 regex 在 Multiline 下以 <c>$</c> 收尾，
/// 只認 <c>\n</c> 前的位置，殘留的 <c>\r</c> 讓每一行都比對失敗，build_mod 因此拿不到任何結構化診斷。
/// </summary>
public sealed class BuildServiceDiagnosticParsingTests
{
    private const string Output =
        @"C:\mods\Probe\Source\Broken.cs(2,35): error CS0029: Cannot implicitly convert type 'string' to 'int' [C:\mods\Probe\Source\probe.csproj]" + "\r\n" +
        @"C:\mods\Probe\Source\Broken.cs(2,40): error CS0103: The name 'Foo' does not exist in the current context [C:\mods\Probe\Source\probe.csproj]" + "\r\n";

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void ParsesMsBuildDiagnosticsRegardlessOfLineEnding(string newline)
    {
        var diagnostics = BuildService.ParseDiagnostics(Output.Replace("\r\n", newline, StringComparison.Ordinal));

        Assert.Collection(
            diagnostics,
            first =>
            {
                Assert.Equal("error", first.Severity);
                Assert.Equal("CS0029", first.Code);
                Assert.Equal("Cannot implicitly convert type 'string' to 'int'", first.Message);
                Assert.Equal(@"C:\mods\Probe\Source\Broken.cs", first.File);
                Assert.Equal(2, first.Line);
                Assert.Equal(35, first.Column);
            },
            second =>
            {
                Assert.Equal("CS0103", second.Code);
                Assert.Equal("The name 'Foo' does not exist in the current context", second.Message);
                Assert.Equal(40, second.Column);
            });
    }

    /// <summary>
    /// Steam 預設裝在 C:\Program Files (x86)\Steam，直接在遊戲 Mods 資料夾開發的 Mod 路徑都帶括號；
    /// 舊 regex 的 file 不允許「(」，這些診斷全部比對失敗。
    /// </summary>
    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\Probe\Source\Broken.cs(2,35): error CS0029: Cannot implicitly convert type 'string' to 'int' [C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\Probe\Source\probe.csproj]", @"C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\Probe\Source\Broken.cs", 2, "CS0029")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\Probe\Source\probe.csproj : error NETSDK1045: The current .NET SDK does not support targeting .NET 99. [C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\Probe\Source\probe.csproj]", @"C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\Probe\Source\probe.csproj", null, "NETSDK1045")]
    public void ParsesDiagnosticsWhosePathContainsParentheses(string line, string file, int? lineNumber, string code)
    {
        var diagnostic = Assert.Single(BuildService.ParseDiagnostics(line + "\r\n"));

        Assert.Equal(file, diagnostic.File);
        Assert.Equal(lineNumber, diagnostic.Line);
        Assert.Equal(code, diagnostic.Code);
    }

    /// <summary>專案層級與工具層級的錯誤沒有行號，舊 regex 完全比對不到，建置失敗的原因因此消失。</summary>
    [Theory]
    [InlineData(@"C:\mods\Probe\Source\probe.csproj : error NETSDK1045: The current .NET SDK does not support targeting .NET 99. [C:\mods\Probe\Source\probe.csproj]", @"C:\mods\Probe\Source\probe.csproj", "NETSDK1045", "The current .NET SDK does not support targeting .NET 99.")]
    [InlineData(@"CSC : error CS2012: Cannot open 'obj\Release\probe.dll' for writing [C:\mods\Probe\Source\probe.csproj]", "CSC", "CS2012", @"Cannot open 'obj\Release\probe.dll' for writing")]
    public void ParsesDiagnosticsWithoutLineNumbers(string line, string origin, string code, string message)
    {
        var diagnostic = Assert.Single(BuildService.ParseDiagnostics(line + "\r\n"));

        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal(origin, diagnostic.File);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(message, diagnostic.Message);
        Assert.Null(diagnostic.Line);
        Assert.Null(diagnostic.Column);
    }
}
