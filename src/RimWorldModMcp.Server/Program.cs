using System.Diagnostics;
using System.Text.Json;
using RimWorldModMcp.Core.Paths;
using System.Net.Sockets;
using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Diagnostics;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Indexing.Metadata;
using RimWorldModMcp.Indexing.Decompilation;
using RimWorldModMcp.Indexing.Pipeline;
using RimWorldModMcp.Indexing.Storage;
using RimWorldModMcp.Indexing.Model;

// stdout 屬於 MCP 協定，除了 JSON-RPC 之外不得寫入任何東西。
// 開發用的診斷輸出一律走 stderr；正式的 stdio 模式在 Phase 4 接上 MCP host。

var command = args.Length > 0 ? args[0] : "stdio";

switch (command)
{
    case "detect":
        return Detect();

    case "symbols":
        return Symbols();

    case "index":
        return BuildIndex();

    case "decompile":
        return Decompile(args.Length > 1 ? args[1] : "ThingDef");

    case "stdio":
        return await RimWorldModMcp.Server.McpHost.RunStdioAsync(args[1..]);

    case "daemon":
        return await RunDaemon();

    default:
        await Console.Error.WriteLineAsync(
            $"未知指令：{command}。可用：stdio（預設）| daemon | detect | index | symbols | decompile");
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

// 開發用：對真實安裝的組件跑一次符號讀取，檢查產出量與耗時。
static int Symbols()
{
    var paths = new RimWorldLocator().Detect();

    if (paths.ManagedDir is null)
    {
        Console.Error.WriteLine("找不到 RimWorld 的 Managed 目錄。");
        return 1;
    }

    var reader = new AssemblySymbolReader();
    var total = 0;

    foreach (var assembly in Directory.GetFiles(paths.ManagedDir, "*.dll").Order())
    {
        var name = Path.GetFileNameWithoutExtension(assembly);

        // 只看遊戲本體與 DLC，Unity 與 BCL 組件不是索引目標。
        if (!name.StartsWith("Assembly-CSharp", StringComparison.Ordinal))
        {
            continue;
        }

        var stopwatch = Stopwatch.StartNew();
        var symbols = reader.Read(assembly);
        stopwatch.Stop();

        total += symbols.Count;

        var byKind = symbols
            .GroupBy(s => s.Kind)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}={g.Count()}");

        Console.Error.WriteLine($"{name}: {symbols.Count} 個符號，{stopwatch.ElapsedMilliseconds} ms");
        Console.Error.WriteLine("  " + string.Join(", ", byKind));

        var deepest = symbols
            .Where(s => s.BaseChain is not null)
            .MaxBy(s => s.BaseChain!.Count(c => c == '|'));

        if (deepest is not null)
        {
            Console.Error.WriteLine($"  最深繼承鏈：{deepest.Fqn} : {deepest.BaseChain}");
        }
    }

    Console.Error.WriteLine($"合計 {total} 個符號。");
    return total > 0 ? 0 : 1;
}

// 開發用：對真實安裝建一次完整索引，驗證管線與耗時。
static int BuildIndex()
{
    var store = new StoreDirectories();
    var locator = new RimWorldLocator();
    var processes = new ProcessHost();
    var database = new IndexDatabase(store);
    var builder = new IndexBuilder(database, locator, new IndexFingerprint(), new CriticalSectionLock(store, processes));

    Console.Error.WriteLine($"索引資料庫：{database.DatabasePath}");

    var result = builder.Rebuild();

    Console.Error.WriteLine($"組件：{string.Join(", ", result.Assemblies)}");
    Console.Error.WriteLine($"Def：{result.DefCount}，符號：{result.SymbolCount}，引用：{result.ReferenceCount}");
    Console.Error.WriteLine($"耗時：{result.ElapsedMilliseconds} ms");
    Console.Error.WriteLine($"指紋：{result.Fingerprint?[..16]}…");

    var status = builder.Status();
    Console.Error.WriteLine($"新鮮度：{(status.Fresh ? "fresh" : "stale")}，原始碼已索引：{status.SourceIndexed}");

    return result.DefCount > 0 && result.SymbolCount > 0 ? 0 : 1;
}

// 開發用：從索引查出符號，再按需反編譯它的原始碼。
static int Decompile(string name)
{
    var store = new StoreDirectories();
    var paths = new RimWorldLocator().Detect();

    if (paths.ManagedDir is null)
    {
        Console.Error.WriteLine("找不到 RimWorld 的 Managed 目錄。");
        return 1;
    }

    using var connection = new IndexDatabase(store).Open();
    var hits = SymbolRepository.Read(connection, name, 5);

    if (hits.Count == 0)
    {
        Console.Error.WriteLine($"索引中找不到符號：{name}。請先執行 index。");
        return 1;
    }

    using var decompiler = new MemberDecompiler();

    foreach (var hit in hits.Take(2))
    {
        var assembly = Path.Combine(paths.ManagedDir, hit.Assembly + ".dll");

        var stopwatch = Stopwatch.StartNew();
        var source = decompiler.DecompileMember(assembly, hit.MetadataToken);
        stopwatch.Stop();

        Console.Error.WriteLine($"--- {hit.Fqn} [{hit.Kind}] {stopwatch.ElapsedMilliseconds} ms ---");
        Console.Error.WriteLine($"簽章：{hit.Signature}");

        if (hit.BaseChain.Count > 0)
        {
            Console.Error.WriteLine($"繼承鏈：{string.Join(" > ", hit.BaseChain)}");
        }

        var preview = source is null
            ? "（反編譯失敗）"
            : string.Join(Environment.NewLine, source.Split('\n').Take(12));

        Console.Error.WriteLine(preview);
    }

    return 0;
}

// 診斷 daemon：接收遊戲內 Bridge 的 NDJSON 並監看 Player.log。
// 由 run_test_cycle 自動啟動，不需要使用者手動執行。
static async Task<int> RunDaemon()
{
    var store = new StoreDirectories();
    var locator = new RimWorldLocator();
    var sessions = new TestSessionStore(store);
    var records = new DaemonRecordStore(store);
    var listener = new DaemonListener(store, locator, new DiagnosticStore(store), sessions, records);

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
