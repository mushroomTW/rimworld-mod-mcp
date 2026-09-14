using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldModMcp.Bridge
{
    public sealed class BridgeMod : Mod
    {
        public BridgeMod(ModContentPack content) : base(content)
        {
            var token = Environment.GetEnvironmentVariable("RIMWORLD_MOD_MCP_BRIDGE_TOKEN");
            if (string.IsNullOrWhiteSpace(token)) return;
            Diagnostics.Configure(token, Environment.GetEnvironmentVariable("RIMWORLD_MOD_MCP_BRIDGE_PORT"));
            new Harmony("rimworldmodmcp.bridge").PatchAll(Assembly.GetExecutingAssembly());
            Diagnostics.Send("diagnostic", "RimWorld MCP Bridge connected", "Bridge initialised.");

            // loaded_mods 延後到載入流程結束才送。建構式執行當下 Mod 仍在
            // 逐一建構中，RunningModsListForReading 可能還不完整。
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    var loaded = string.Join(",", LoadedModManager.RunningModsListForReading.Select(pack => pack.PackageId));
                    Diagnostics.Send("loaded_mods", "Loaded mods", loaded);
                    Diagnostics.Send("performance", "Bridge startup", "uptime_ms=" + Diagnostics.UptimeMilliseconds);
                }
                catch { /* 診斷絕不能影響遊戲。 */ }
            });
        }
    }

    [HarmonyPatch]
    internal static class VerseLogPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(Log).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "Warning" || method.Name == "Error");
        }

        private static void Postfix(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                var text = __args == null
                    ? string.Empty
                    : string.Join(" ", __args.Select(value => value == null ? string.Empty : value.ToString()));
                var firstLine = text.Split('\n')[0].TrimEnd('\r');
                Diagnostics.Send(__originalMethod.Name == "Error" ? "error" : "warning", firstLine, text);
            }
            catch { /* 診斷絕不能影響遊戲。 */ }
        }
    }

    internal static class Diagnostics
    {
        private static string token = string.Empty;

        // 預設埠必須與 server 端 RimWorldLocator.DefaultBridgePort 一致。
        // Bridge 無法參考 Core，這裡只能複製常數——改任一邊都要同步另一邊。
        private static int port = 49460;

        private static readonly Stopwatch startedAt = Stopwatch.StartNew();

        // 送出全部走單一背景執行緒與有界佇列。每一則 Log.Error 都
        // Task.Run + 開新 TcpClient 的話，每 tick 拋例外的 Mod 會造成
        // 執行緒池飢餓與暫時埠（TIME_WAIT）耗盡，訊息順序也沒有保證。
        private static readonly object gate = new object();
        private static readonly Queue<string> queue = new Queue<string>();
        private const int MaxQueuedLines = 500;
        private const int MaxTextChars = 16000;
        private static Thread? worker;
        private static volatile bool running = true;

        private static TcpClient? client;
        private static StreamWriter? writer;

        internal static long UptimeMilliseconds => startedAt.ElapsedMilliseconds;

        internal static void Configure(string value, string requestedPort)
        {
            token = value;
            int parsed;
            if (int.TryParse(requestedPort, out parsed) && parsed > 0 && parsed < 65536) port = parsed;
        }

        internal static void Send(string type, string firstLine, string text)
        {
            if (string.IsNullOrWhiteSpace(token)) return;

            // 上限保護：daemon 端也會截，但先在來源截掉能省掉整包網路傳輸。
            if (text == null) text = string.Empty;
            if (text.Length > MaxTextChars) text = text.Substring(0, MaxTextChars);

            // JSON 組裝與轉義在 DiagnosticPayload，該類別不相依 RimWorld，因此可獨立測試。
            var line = DiagnosticPayload.BuildLine(type, token, firstLine, text);

            lock (gate)
            {
                // 錯誤風暴時丟棄多出來的訊息：遊戲的流暢優先於診斷的完整。
                // daemon 端本來就會對相同錯誤去重，丟棄的多半是重複訊息。
                if (queue.Count >= MaxQueuedLines) return;

                queue.Enqueue(line);

                if (worker == null)
                {
                    worker = new Thread(Pump) { IsBackground = true, Name = "RimWorldModMcp.Bridge" };
                    worker.Start();
                }

                System.Threading.Monitor.Pulse(gate);
            }
        }

        private static void Pump()
        {
            while (running)
            {
                string line;

                lock (gate)
                {
                    while (running && queue.Count == 0) System.Threading.Monitor.Wait(gate);
                    if (!running) break;
                    line = queue.Dequeue();
                }

                // 第一次失敗可能只是連線被 daemon 的閒置逾時關掉了，重連一次。
                if (!TryWrite(line)) TryWrite(line);
            }
        }

        private static bool TryWrite(string line)
        {
            try
            {
                if (writer == null)
                {
                    client = new TcpClient();
                    client.Connect("127.0.0.1", port);
                    writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                }

                writer.WriteLine(line);
                return true;
            }
            catch
            {
                try { if (writer != null) writer.Dispose(); } catch { }
                try { if (client != null) client.Close(); } catch { }
                writer = null;
                client = null;
                return false;
            }
        }
    }
}
