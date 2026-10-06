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
    Console.Error.WriteLine($"Unknown command: {command}. Available: stdio (default) | daemon | detect");
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
    var listener = new DaemonListener(store, locator.BridgePort(), new DiagnosticStore(store), sessions, records, new GameStateStore(store));

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

    // 埠被佔用由 DaemonListener 自己降級成僅日誌模式處理，不會拋到這裡。
    await listener.RunAsync(cancellation.Token);
    return 0;
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
    builder.Services.AddHostedService<RimWorldModMcp.Server.SourceIndexResumer>();

    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "rimworld-mod-mcp",
                Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            };

            // 工作流程放這裡而不是散在各工具描述裡：每一輪都要送全部工具的 schema，
            // 描述越長每輪成本越高；instructions 只在 initialize 送一次。
            options.ServerInstructions = """
                RimWorld mod development server: Core/DLC Defs, the game's C# API, and the decompiled C# of any installed mod (Workshop or local), all indexed locally. Workflow:
                1. rimworld_status first; if index.busy=true another writer holds the index database, so retry shortly; if index.healthy=false and busy=false call rebuild_index; if index.fresh=false call rebuild_index. index.source_index tells whether the source full-text layer is running, failed (error), or done.
                2. Research the game: search_defs -> read_def for Core/DLC XML; read_symbol / list_symbols / find_descendants for the C# API (zero hits return suggestions); search_source for regex over decompiled code (indexing=true means retry shortly); read_source_file to page a whole file.
                3. Analyze an installed mod (Workshop or local, including DLL-only mods): list_installed_mods(package_id=<name fragment>, include_details=true) resolves packageId, path, dependencies and versions; search_source(package_id, e.g. pattern=HarmonyPatch for its patches) starts decompiling and indexing every DLL in the background; poll it until indexing=false (inspect_installed_mod does the same synchronously and can exceed your tool-call timeout on large mods). Then scope the same tools with assembly=mod:<packageId>: list_symbols(parent="") for its namespaces, read_symbol(include_body=true) for one member, read_source_file to page through a whole file. find_descendants lists mod types whose direct base is the given game type. A mod's own XML Defs are not indexed: read them from its path.
                4. Build: create_mod (with_code=true for C#), then build_mod after every source change.
                5. Test: run_test_cycle launches the game in an isolated session (companion_mods enables installed mods alongside, e.g. the mod you are patching); test_status(wait_for_state=Playing) blocks until the map is up and reports in-game state (paused, loading, open_windows); poll list_test_diagnostics with since_at=latest_at and wait_seconds>0; get_test_diagnostic for a full stack trace; stop_test(confirm=true) when done.
                Unknown parameters are rejected, so use only the documented names.
                """;
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly(serializerOptions: RimWorldModMcp.Server.Tools.ToolJson.Options)
        .WithRequestFilters(filters => filters.AddCallToolFilter(RimWorldModMcp.Server.Tools.UnknownArgumentFilter.Reject));

    await builder.Build().RunAsync(cancellationToken);
    return 0;
}
