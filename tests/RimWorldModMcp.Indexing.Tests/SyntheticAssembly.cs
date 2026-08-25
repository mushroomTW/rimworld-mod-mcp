using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// 用 Roslyn 把 C# 原始碼即時編成真的組件。
///
/// <para>
/// 這讓符號讀取器可以對著<b>真實的 IL metadata</b> 測試，而不是對 mock 測。
/// 也因此不需要把二進位 fixture 進版控，更不需要安裝 RimWorld。
/// </para>
/// </summary>
internal static class SyntheticAssembly
{
    internal static string Emit(string source, string assemblyName, string outputDirectory)
    {
        var references = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, assemblyName + ".dll");

        var result = compilation.Emit(path);

        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());

            throw new InvalidOperationException("測試組件編譯失敗：" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        return path;
    }
}
