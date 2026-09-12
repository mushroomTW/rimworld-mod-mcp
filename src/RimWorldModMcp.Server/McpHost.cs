using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RimWorldModMcp.Core;
using RimWorldModMcp.Diagnostics;
using RimWorldModMcp.Indexing;

namespace RimWorldModMcp.Server;

public static class McpHost
{
    /// <summary>以 stdio 傳輸執行 MCP server。</summary>
    public static async Task<int> RunStdioAsync(string[] args, CancellationToken cancellationToken = default)
    {
        // 用 CreateEmptyApplicationBuilder 而不是 CreateApplicationBuilder：
        // 後者會去讀工作目錄的 appsettings.json，而這個工具以 dotnet tool 安裝時，
        // 工作目錄是使用者的 Mod 資料夾——會撿到完全不相干的設定檔。
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
        });

        // 環境變數由 EnvironmentVariables 直接讀取，不經過 IConfiguration——
        // 少一層設定管線，也不會意外撿到工作目錄裡的設定來源。

        // stdout 屬於 MCP 協定。任何寫到 stdout 的日誌都會讓 JSON-RPC 解析失敗，
        // 所以全部導向 stderr。
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        // 預設只報警告，避免干擾。排查問題時可用 RIMWORLD_MOD_MCP_LOG_LEVEL=Debug
        // 讓工具內部拋出的例外細節出現在 stderr——MCP 協定本身只會回傳
        // 「An error occurred」這種通用訊息。
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
                    Version = typeof(McpHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                };
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync(cancellationToken);
        return 0;
    }
}
