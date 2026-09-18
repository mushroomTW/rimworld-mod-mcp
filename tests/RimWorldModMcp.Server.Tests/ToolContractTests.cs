using System.Text.RegularExpressions;

namespace RimWorldModMcp.Server.Tests;

/// <summary>工具清單與 schema 的契約：名稱與參數名一變，agent 的提示與工作流程就會失效。</summary>
public sealed class ToolContractTests : IAsyncLifetime
{
    private McpServerFixture _server = null!;

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
        var pattern = new Regex("^[a-z][a-z0-9_]*$");
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

    /// <summary>破壞性工具的旗標要正確，client 才能據此決定要不要先問使用者。</summary>
    [Fact]
    public async Task AnnotationsMarkDestructiveAndReadOnlyTools()
    {
        var tools = (await _server.Client.ListToolsAsync()).ToDictionary(t => t.Name);

        Assert.True(tools["stop_test"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools["test_status"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools["read_def"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.NotEqual(true, tools["run_test_cycle"].ProtocolTool.Annotations?.ReadOnlyHint);
    }
}
