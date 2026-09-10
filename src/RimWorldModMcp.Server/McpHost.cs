using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RimWorldModMcp.Core;
using RimWorldModMcp.Diagnostics;
using RimWorldModMcp.Indexing;

namespace RimWorldModMcp.Server;

public static class McpHost
{
    /// <summary>
    /// 以 stdio 傳輸執行 MCP server。
    /// <c>--workspace &lt;路徑&gt;</c> 可重複，指定本次啟動信任的 Mod 開發工作區；
    /// 這是唯一的寫入授權來源，沒有給就只剩唯讀工具能用。
    /// </summary>
    public static async Task<int> RunStdioAsync(string[] args, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> workspaces;
        string[] hostArgs;

        try
        {
            (workspaces, hostArgs) = SplitWorkspaceArgs(args);
        }
        catch (ArgumentException e)
        {
            await Console.Error.WriteLineAsync(e.Message);
            return 2;
        }

        // 用 CreateEmptyApplicationBuilder 而不是 CreateApplicationBuilder：
        // 後者會去讀工作目錄的 appsettings.json，而這個工具以 dotnet tool 安裝時，
        // 工作目錄是使用者的 Mod 資料夾——會撿到完全不相干的設定檔。
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = hostArgs,
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

        try
        {
            builder.Services.AddRimWorldCore(workspaces);
        }
        catch (DirectoryNotFoundException e)
        {
            // 設定檔裡的工作區路徑打錯了。在這裡就停下來並把原因寫到 stderr，
            // 比起讓 server 帶著空白信任清單啟動、等第一次 build_mod 才報錯清楚得多。
            await Console.Error.WriteLineAsync($"--workspace 無效：{e.Message}");
            return 2;
        }

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

    /// <summary>把 <c>--workspace &lt;路徑&gt;</c> 從參數裡挑出來，其餘原樣交給 host。</summary>
    private static (IReadOnlyList<string> Workspaces, string[] HostArgs) SplitWorkspaceArgs(string[] args)
    {
        var workspaces = new List<string>();
        var rest = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--workspace")
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    throw new ArgumentException("--workspace 後面必須接工作區路徑。");
                }

                workspaces.Add(args[++i]);
            }
            else if (args[i].StartsWith("--workspace=", StringComparison.Ordinal))
            {
                var value = args[i]["--workspace=".Length..];

                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException("--workspace= 後面必須接工作區路徑。");
                }

                workspaces.Add(value);
            }
            else if (args[i].StartsWith("--workspace", StringComparison.OrdinalIgnoreCase))
            {
                // 拼錯或大小寫不對的寫法不能靜默吞掉：那會讓 server 帶著空白信任清單啟動，
                // 使用者要到第一次 build_mod 才發現，而且訊息會叫他去加一個他以為已經加了的參數。
                throw new ArgumentException($"無法辨識的參數：{args[i]}。寫法是 --workspace <路徑> 或 --workspace=<路徑>。");
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        return (workspaces, [.. rest]);
    }
}
