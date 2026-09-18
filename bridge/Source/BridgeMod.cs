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
using RimWorld;
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
        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeSmell", "S1144:Unused private types or members should be removed", Justification = "Invoked by Harmony via reflection")]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(Log).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "Warning" || method.Name == "Error");
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeSmell", "S1144:Unused private types or members should be removed", Justification = "Invoked by Harmony via reflection")]
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

    /// <summary>
    /// 每一幀在主執行緒上執行；狀態取樣必須在主執行緒，Current.Game 與 WindowStack
    /// 都不是執行緒安全的。Root_Entry 與 Root_Play 的 Update 都會呼叫 base.Update，
    /// 所以主選單與遊戲中都會回報。
    /// </summary>
    [HarmonyPatch(typeof(Root), nameof(Root.Update))]
    internal static class RootUpdatePatch
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeSmell", "S1144:Unused private types or members should be removed", Justification = "Invoked by Harmony via reflection")]
        private static void Postfix() => GameStateReporter.Sample();
    }

    /// <summary>
    /// 定期把遊戲狀態推給 daemon。Bridge 到 daemon 只有單向通道，
    /// 所以是由這裡主動推送，daemon 只保留最後一份。
    /// </summary>
    internal static class GameStateReporter
    {
        /// <summary>取樣間隔。狀態變了才送，變化最快的欄位是 tick，未暫停時大約每秒一筆。</summary>
        private const int SampleIntervalMs = 1000;

        /// <summary>沒有變化時的心跳間隔，讓 server 端能用 age 判斷遊戲是否還活著。</summary>
        private const int HeartbeatIntervalMs = 5000;

        /// <summary>回報的視窗數上限；只需要知道「有沒有對話框擋著」，不需要整個堆疊。</summary>
        private const int MaxWindows = 8;

        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static long nextSampleAt;
        private static long lastSentAt;
        private static string lastBody = string.Empty;

        internal static void Sample()
        {
            try
            {
                var now = clock.ElapsedMilliseconds;
                if (now < nextSampleAt) return;
                nextSampleAt = now + SampleIntervalMs;

                var snapshot = Capture();

                // uptime 每次都不同，變化比對要排除它，否則每次取樣都會送。
                var body = GameStatePayload.BuildLine(string.Empty, new GameStateSnapshot
                {
                    ProgramState = snapshot.ProgramState,
                    MapLoaded = snapshot.MapLoaded,
                    Tick = snapshot.Tick,
                    Paused = snapshot.Paused,
                    TimeSpeed = snapshot.TimeSpeed,
                    Loading = snapshot.Loading,
                    OpenWindows = snapshot.OpenWindows,
                    Colonists = snapshot.Colonists,
                    GameVersion = snapshot.GameVersion,
                });

                if (body == lastBody && now - lastSentAt < HeartbeatIntervalMs) return;

                lastBody = body;
                lastSentAt = now;
                Diagnostics.SendGameState(snapshot);
            }
            catch { /* 診斷絕不能影響遊戲。 */ }
        }

        private static GameStateSnapshot Capture()
        {
            var snapshot = new GameStateSnapshot
            {
                ProgramState = Current.ProgramState.ToString(),
                GameVersion = VersionControl.CurrentVersionString,
                Loading = LongEventHandler.AnyEventNowOrWaiting,
                UptimeMs = Diagnostics.UptimeMilliseconds,
            };

            // Find.TickManager / Find.CurrentMap 在沒有 Game 時會 NRE，先確認再讀。
            if (Current.Game != null && Current.ProgramState == ProgramState.Playing)
            {
                var tickManager = Find.TickManager;
                if (tickManager != null)
                {
                    snapshot.Tick = tickManager.TicksGame;
                    snapshot.Paused = tickManager.Paused;
                    snapshot.TimeSpeed = tickManager.CurTimeSpeed.ToString();
                }

                var map = Find.CurrentMap;
                snapshot.MapLoaded = map != null;
                if (map != null && map.mapPawns != null)
                {
                    snapshot.Colonists = map.mapPawns.FreeColonistsCount;
                }
            }

            var windows = Find.WindowStack;
            if (windows != null)
            {
                snapshot.OpenWindows = windows.Windows
                    .Where(window => window != null)
                    .Select(window => window.GetType().Name)
                    .Take(MaxWindows)
                    .ToArray();
            }

            return snapshot;
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
            Enqueue(DiagnosticPayload.BuildLine(type, token, firstLine, text));
        }

        internal static void SendGameState(GameStateSnapshot state)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            Enqueue(GameStatePayload.BuildLine(token, state));
        }

        private static void Enqueue(string line)
        {
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
                try { if (writer != null) writer.Dispose(); } catch { /* 忽略關閉 writer 過程中的例外。 */ }
                try { if (client != null) client.Close(); } catch { /* 忽略關閉 client 過程中的例外。 */ }
                writer = null;
                client = null;
                return false;
            }
        }
    }
}
