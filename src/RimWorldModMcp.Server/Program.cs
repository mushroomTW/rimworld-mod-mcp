using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RimWorldModMcp.Core;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Diagnostics;
using RimWorldModMcp.Indexing;

// stdout 屬於 MCP 協定，除了 JSON-RPC 之外不得寫入任何東西。
// 開發用的診斷輸出一律走 stderr。

var command = args.Length > 0 ? args[0] : "stdio";

return command switch
{
    "detect" => Detect(),
    "stdio" => await RunStdioAsync(args.Length > 0 ? args[1..] : []),
    "daemon" => await RunDaemon(),
    _ => UnknownCommand(command),
};

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"未知指令：{command}。可用：stdio（預設）| daemon | detect");
    return 2;
}

static int Detect()
{
    var locator = new RimWorldLocator();
    var paths = locator.Detect();

    var payload = new Dictionary<string, object?>
    {
        ["install_root"] = paths.InstallRoot,
        ["executable"] = paths.Executable,
        ["managed_dir"] = paths.ManagedDir,
        ["data_dir"] = paths.DataDir,
        ["mods_dir"] = paths.ModsDir,
        ["workshop_dir"] = paths.WorkshopDir,
        ["player_log"] = paths.PlayerLog,
        ["mods_config"] = paths.ModsConfig,
        ["prefs_xml"] = paths.PrefsXml,
        ["bridge_port"] = locator.BridgePort(),
    };

    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    Console.Error.WriteLine(json);

    return paths.HasManagedAndData ? 0 : 1;
}

// 診斷 daemon：接收遊戲內 Bridge 的 NDJSON 並監看 Player.log。
// 由 run_test_cycle 自動啟動，不需要使用者手動執行。
static async Task<int> RunDaemon()
{
    var store = new StoreDirectories();
    var locator = new RimWorldLocator();
    var sessions = new TestSessionStore(store);
    var records = new DaemonRecordStore(store);
    var listener = new DaemonListener(store, locator.BridgePort(), new DiagnosticStore(store), sessions, records);

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

    try
    {
        await listener.RunAsync(cancellation.Token);
        return 0;
    }
    catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
    {
        // 埠已被佔用。以約定的離開碼退出，讓啟動者知道要去讀自述檔判斷
        // 佔用者是不是自己人——由 OS 的 bind 當唯一仲裁者，沒有 TOCTOU 空窗。
        await Console.Error.WriteLineAsync($"連接埠 {locator.BridgePort()} 已被佔用。");
        return DaemonBootstrapper.PortInUseExitCode;
    }
}

/// <summary>以 stdio 傳輸執行 MCP server。</summary>
static async Task<int> RunStdioAsync(string[] args, CancellationToken cancellationToken = default)
{
    // 用 CreateEmptyApplicationBuilder 而不是 CreateApplicationBuilder：
    // 後者會去讀工作目錄的 appsettings.json，而這個工具以 dotnet tool 安裝時，
    // 工作目錄是使用者的 Mod 資料夾——會撿到完全不相干的設定檔。
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
    });

    // stdout 屬於 MCP 協定。任何寫到 stdout 的日誌都會讓 JSON-RPC 解析失敗，全部導向 stderr。
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

    // 預設只報警告，避免干擾。排查問題時可用 RIMWORLD_MOD_MCP_LOG_LEVEL=Debug
    builder.Logging.SetMinimumLevel(
        Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("RIMWORLD_MOD_MCP_LOG_LEVEL"), ignoreCase: true, out var level)
            ? level
            : LogLevel.Warning);

    builder.Services.AddRimWorldCore();
    builder.Services.AddRimWorldIndexing();
    builder.Services.AddRimWorldDiagnostics();

    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "rimworld-mod-mcp",
                Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            };
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync(cancellationToken);
    return 0;
}
