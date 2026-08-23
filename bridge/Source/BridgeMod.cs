using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using Verse;

namespace RimWorldMcp.Bridge
{
    public sealed class BridgeMod : Mod
    {
        public BridgeMod(ModContentPack content) : base(content)
        {
            var token = Environment.GetEnvironmentVariable("RIMWORLD_MCP_BRIDGE_TOKEN");
            if (string.IsNullOrWhiteSpace(token)) return;
            Diagnostics.Configure(token, Environment.GetEnvironmentVariable("RIMWORLD_MCP_BRIDGE_PORT"));
            new Harmony("rimworldmcp.bridge").PatchAll(Assembly.GetExecutingAssembly());
            Diagnostics.Send("diagnostic", "RimWorld MCP Bridge connected", "Bridge initialised.");
            var loaded = string.Join(",", LoadedModManager.RunningModsListForReading.Select(pack => pack.PackageId));
            Diagnostics.Send("loaded_mods", "Loaded mods", loaded);
            Diagnostics.Send("performance", "Bridge startup", "uptime_ms=" + Diagnostics.UptimeMilliseconds);
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
        private static int port = 49460;
        private static readonly Stopwatch startedAt = Stopwatch.StartNew();

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
            // JSON 組裝與轉義在 DiagnosticPayload，該類別不相依 RimWorld，因此可獨立測試。
            var line = DiagnosticPayload.BuildLine(type, token, firstLine, text);
            Task.Run(() =>
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        client.Connect("127.0.0.1", port);
                        using (var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)))
                        {
                            writer.WriteLine(line);
                        }
                    }
                }
                catch { /* 診斷絕不能影響遊戲。 */ }
            });
        }
    }
}
