using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace RimWorldModMcp.Server.Tests;

/// <summary>工具清單與 schema 的契約：名稱與參數名一變，agent 的提示與工作流程就會失效。</summary>
public sealed partial class ToolContractTests : IAsyncLifetime
{
    private McpServerFixture _server = null!;

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SnakeCasePattern();

    public async Task InitializeAsync() => _server = await McpServerFixture.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    /// <summary>README 與 server instructions 列出的工具，一個都不能少、也不能多出未文件化的。</summary>
    [Fact]
    public async Task ExposesTheDocumentedToolSet()
    {
        var tools = await _server.Client.ListToolsAsync();

        string[] expected =
        [
            "rimworld_status", "rebuild_index",
            "search_defs", "read_def", "read_symbol", "list_symbols", "find_descendants",
            "search_source", "read_source_file", "find_def_usages",
            "list_installed_mods", "inspect_installed_mod",
            "create_mod", "build_mod",
            "run_test_cycle", "test_status", "stop_test", "list_test_diagnostics", "get_test_diagnostic",
        ];

        Assert.Equal(expected.Order(), tools.Select(t => t.Name).Order());
    }

    /// <summary>參數一律 snake_case：LLM 呼叫端對 camelCase 與 snake_case 混用的 schema 最容易打錯。</summary>
    [Fact]
    public async Task AllParametersAreSnakeCase()
    {
        var tools = await _server.Client.ListToolsAsync();
        var pattern = SnakeCasePattern();
        var offenders = new List<string>();

        foreach (var tool in tools)
        {
            if (!tool.JsonSchema.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            offenders.AddRange(properties.EnumerateObject()
                .Where(p => !pattern.IsMatch(p.Name))
                .Select(p => $"{tool.Name}.{p.Name}"));
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task EveryToolAndParameterHasADescription()
    {
        var tools = await _server.Client.ListToolsAsync();
        var missing = new List<string>();

        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Description))
            {
                missing.Add(tool.Name);
            }

            if (!tool.JsonSchema.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            missing.AddRange(properties.EnumerateObject()
                .Where(p => !p.Value.TryGetProperty("description", out var d) || string.IsNullOrWhiteSpace(d.GetString()))
                .Select(p => $"{tool.Name}.{p.Name}"));
        }

        Assert.Empty(missing);
    }

    /// <summary>
    /// 未知參數必須被拒絕而不是靜默忽略——打錯參數名拿到一大包無關結果，
    /// 比一行錯誤訊息貴得多。
    /// </summary>
    [Fact]
    public async Task UnknownArgumentsAreRejectedWithTheValidNames()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _server.CallAsync("test_status", new Dictionary<string, object?> { ["wait_for"] = "Playing" }));

        Assert.Contains("Unknown parameter(s) for test_status: wait_for", error.Message);
        Assert.Contains("wait_for_state", error.Message);
        Assert.Contains("wait_seconds", error.Message);
    }

    /// <summary>驗證經 MCP 傳輸後的四個旗標及 JSON 型別，避免 SDK 預設值掩蓋缺漏。</summary>
    [Fact]
    public async Task EveryToolDeclaresAllFourBooleanHintsMatchingItsBehavior()
    {
        var tools = await _server.Client.ListToolsAsync();
        var expected = new Dictionary<string, (bool ReadOnly, bool Destructive, bool Idempotent, bool OpenWorld)>
        {
            ["rimworld_status"] = (true, false, true, false),
            ["rebuild_index"] = (false, true, false, false),
            ["search_defs"] = (true, false, true, false),
            ["read_def"] = (true, false, true, false),
            ["read_symbol"] = (true, false, true, false),
            ["list_symbols"] = (true, false, true, false),
            ["find_descendants"] = (true, false, true, false),
            ["search_source"] = (false, true, true, false),
            ["read_source_file"] = (true, false, true, false),
            ["find_def_usages"] = (true, false, true, false),
            ["list_installed_mods"] = (true, false, true, false),
            ["inspect_installed_mod"] = (false, true, true, false),
            ["create_mod"] = (false, false, false, false),
            ["build_mod"] = (false, true, false, true),
            ["run_test_cycle"] = (false, true, false, true),
            ["test_status"] = (true, false, true, false),
            ["stop_test"] = (false, true, true, false),
            ["list_test_diagnostics"] = (true, false, true, false),
            ["get_test_diagnostic"] = (true, false, true, false),
        };

        Assert.Equal(expected.Keys.Order(), tools.Select(t => t.Name).Order());

        foreach (var tool in tools)
        {
            var json = JsonSerializer.SerializeToElement(tool.ProtocolTool, McpJsonUtilities.DefaultOptions);
            Assert.True(json.TryGetProperty("annotations", out var annotations), tool.Name);
            var hints = expected[tool.Name];
            AssertHint("readOnlyHint", hints.ReadOnly);
            AssertHint("destructiveHint", hints.Destructive);
            AssertHint("idempotentHint", hints.Idempotent);
            AssertHint("openWorldHint", hints.OpenWorld);

            void AssertHint(string name, bool value)
            {
                Assert.True(annotations.TryGetProperty(name, out var hint), $"{tool.Name}.{name}");
                Assert.Equal(value ? JsonValueKind.True : JsonValueKind.False, hint.ValueKind);
            }
        }
    }
}
