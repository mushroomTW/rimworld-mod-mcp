using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RimWorldModMcp.Core;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Diagnostics;
using RimWorldModMcp.Indexing;
using RimWorldModMcp.Server.Tools;

namespace RimWorldModMcp.Server.Tests;

/// <summary>
/// 測試專用的記憶體日誌提供者。MCP SDK 把工具的非預期例外只寫進 server 端日誌、
/// 回傳「An error occurred invoking ...」這種通用訊息；沒有這份日誌，
/// CI 偶發失敗時永遠看不到真正的例外與堆疊。
/// </summary>
public sealed class BufferedLogProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();
    private int _count;
    private const int Cap = 1000;

    public ILogger CreateLogger(string categoryName) => new BufferedLogger(categoryName, this);

    public void Dispose() { }

    internal void Write(string line)
    {
        _lines.Enqueue(line);
        if (Interlocked.Increment(ref _count) > Cap && _lines.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
        }
    }

    public string Snapshot() => string.Join('\n', _lines);

    private sealed class BufferedLogger(string category, BufferedLogProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string message;
            try
            {
                message = formatter(state, exception);
            }
            catch (Exception e)
            {
                message = $"{state} (formatter failed: {e.Message})";
            }

            provider.Write(exception is null
                ? $"{logLevel}: {category}: {message}"
                : $"{logLevel}: {category}: {message}\n{exception}");
        }
    }
}

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
    private readonly BufferedLogProvider _logs = new();

    public StoreDirectories Store { get; }

    public McpClient Client { get; private set; } = null!;

    /// <summary>server 端日誌（含 MCP SDK 記錄的工具例外）。失敗診斷用。</summary>
    public string ServerLog => _logs.Snapshot();

    private McpServerFixture(string root, IHost host, StoreDirectories store, BufferedLogProvider logs)
    {
        _root = root;
        _host = host;
        Store = store;
        _logs = logs;
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
        builder.Services.AddHostedService<SourceIndexResumer>();

        // 把 server 端日誌（含工具非預期例外的堆疊）收進記憶體，
        // 工具回錯時一起拋出，CI 偶發失敗才有線索可查。
        var logs = new BufferedLogProvider();
        builder.Logging.AddProvider(logs);

        // 後登記的贏：把預設（指向使用者家目錄）的 StoreDirectories 換成暫時目錄。
        builder.Services.AddSingleton(store);

        builder.Services
            .AddMcpServer()
            .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
            .WithToolsFromAssembly(typeof(TestCycleTools).Assembly, ToolJson.Options)
            .WithRequestFilters(filters => filters.AddCallToolFilter(UnknownArgumentFilter.Reject));

        var fixture = new McpServerFixture(root, builder.Build(), store, logs);

        fixture.Client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));

        return fixture;
    }

    /// <summary>
    /// 呼叫工具並回傳 structuredContent；工具回錯時把錯誤文字當例外拋出，
    /// 並附上 server 端日誌（MCP SDK 會把非預期例外的堆疊寫進日誌，只回傳通用訊息）。
    /// </summary>
    public async Task<System.Text.Json.JsonElement> CallAsync(string tool, IReadOnlyDictionary<string, object?>? arguments = null)
    {
        var result = await Client.CallToolAsync(tool, arguments);

        if (result.IsError == true)
        {
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            throw new InvalidOperationException($"{text}\n----- server log -----\n{ServerLog}");
        }

        return result.StructuredContent ?? throw new InvalidOperationException($"{tool} returned no structuredContent.\n----- server log -----\n{ServerLog}");
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

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // 忽略測試目錄清理被短暫鎖定時的例外
            }
        }
    }
}
