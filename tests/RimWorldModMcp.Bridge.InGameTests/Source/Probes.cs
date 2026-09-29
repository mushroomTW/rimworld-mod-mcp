using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldModMcp.Bridge.InGameTests
{
    internal sealed class SentEntry
    {
        public SentEntry(string type, string firstLine, string text)
        {
            Type = type;
            FirstLine = firstLine;
            Text = text;
        }

        public string Type { get; }
        public string FirstLine { get; }
        public string Text { get; }

        public override string ToString() => $"({Type} | {FirstLine} | {Text.Replace("\r", "\\r").Replace("\n", "\\n")})";
    }

    /// <summary>錄下 Bridge 每一次 Diagnostics.Send。Log.Warning/Error 可能來自任何執行緒，所以要上鎖。</summary>
    [HarmonyPatch]
    internal static class SendProbe
    {
        private static readonly object gate = new object();
        private static readonly List<SentEntry> entries = new List<SentEntry>();

        internal static List<SentEntry> Snapshot()
        {
            lock (gate) return entries.ToList();
        }

        private static bool Prepare() => TargetMethods().Any();

        private static IEnumerable<MethodBase> TargetMethods() => BridgeAccess.DeclaredMethods(BridgeAccess.Diagnostics, "Send");

        private static void Postfix(string type, string firstLine, string text)
        {
            lock (gate) entries.Add(new SentEntry(type, firstLine, text ?? string.Empty));
        }
    }

    /// <summary>錄下 Bridge 每一次 Diagnostics.SendGameState，以及它是否在主執行緒上。</summary>
    [HarmonyPatch]
    internal static class SendGameStateProbe
    {
        private static int calls;
        private static int offMainThread;

        internal static int Calls => Volatile.Read(ref calls);
        internal static int OffMainThread => Volatile.Read(ref offMainThread);

        private static bool Prepare() => TargetMethods().Any();

        private static IEnumerable<MethodBase> TargetMethods() => BridgeAccess.DeclaredMethods(BridgeAccess.Diagnostics, "SendGameState");

        private static void Postfix()
        {
            Interlocked.Increment(ref calls);
            if (!UnityData.IsInMainThread) Interlocked.Increment(ref offMainThread);
        }
    }
}
