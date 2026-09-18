using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RimWorldModMcp.Core;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Diagnostics;
using RimWorldModMcp.Indexing;
using RimWorldModMcp.Server.Tools;

namespace RimWorldModMcp.Server.Tests;

/// <summary>
/// 在行程內把 MCP server 跑起來，用真正的 MCP client 透過 stream 傳輸呼叫工具。
///
/// <para>
/// 測的是 LLM 看到的那一層：工具名稱、參數名稱、回傳的 JSON 形狀。
/// 這些是跨版本的契約——agent 的提示與工作流程都依賴它們，改壞了不會有編譯錯誤。
/// 狀態與快取全部導向暫時目錄，不碰使用者真正的 store。
/// </para>
/// </summary>
public sealed class McpServerFixture : IAsyncDisposable
{
    private readonly string _root;
    private readonly IHost _host;
    private readonly Task _running;

    public StoreDirectories Store { get; }

    public McpClient Client { get; private set; } = null!;

    private McpServerFixture(string root, IHost host, StoreDirectories store)
    {
        _root = root;
        _host = host;
        Store = store;
        _running = host.RunAsync();
    }

    public static async Task<McpServerFixture> StartAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "rwmm-server-" + Guid.NewGuid().ToString("n")[..12]);
        var store = new StoreDirectories(Path.Combine(root, "data"), Path.Combine(root, "cache"));

        // client → server 與 server → client 各一條管線。
        var toServer = new Pipe();
        var toClient = new Pipe();

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

        builder.Services.AddRimWorldCore();
        builder.Services.AddRimWorldIndexing();
        builder.Services.AddRimWorldDiagnostics();

        // 後登記的贏：把預設（指向使用者家目錄）的 StoreDirectories 換成暫時目錄。
        builder.Services.AddSingleton(store);

        builder.Services
            .AddMcpServer()
            .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
            .WithToolsFromAssembly(typeof(TestCycleTools).Assembly)
            .WithRequestFilters(filters => filters.AddCallToolFilter(UnknownArgumentFilter.Reject));

        var fixture = new McpServerFixture(root, builder.Build(), store);

        fixture.Client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));

        return fixture;
    }

    /// <summary>呼叫工具並回傳 structuredContent；工具回錯時把錯誤文字當例外拋出。</summary>
    public async Task<System.Text.Json.JsonElement> CallAsync(string tool, IReadOnlyDictionary<string, object?>? arguments = null)
    {
        var result = await Client.CallToolAsync(tool, arguments);

        if (result.IsError == true)
        {
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            throw new InvalidOperationException(text);
        }

        return result.StructuredContent ?? throw new InvalidOperationException($"{tool} returned no structuredContent.");
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        _host.Dispose();

        try
        {
            await _running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException)
        {
            // 管線關閉後 server 迴圈以 IO 例外或取消收尾，都是正常結束。
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
