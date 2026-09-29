using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimTestRedux;
using Verse;

namespace RimWorldModMcp.Bridge.InGameTests
{
    /// <summary>以反射存取 Bridge 的 internal 型別。</summary>
    internal static class BridgeAccess
    {
        internal const string HarmonyId = "rimworldmodmcp.bridge";

        internal static readonly Type? Diagnostics = AccessTools.TypeByName("RimWorldModMcp.Bridge.Diagnostics");
        internal static readonly Type? GameStateReporter = AccessTools.TypeByName("RimWorldModMcp.Bridge.GameStateReporter");

        /// <summary>Bridge 只在 run_test_cycle 設了 token 時才掛 patch 與送訊息。</summary>
        internal static bool TokenPresent =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RIMWORLD_MOD_MCP_BRIDGE_TOKEN"));

        internal static IEnumerable<MethodBase> DeclaredMethods(Type? type, string name) =>
            type == null
                ? Enumerable.Empty<MethodBase>()
                : AccessTools.GetDeclaredMethods(type).Where(method => method.Name == name).Cast<MethodBase>();

        /// <summary>呼叫 Bridge 的 GameStateReporter.Capture()，回傳 GameStateSnapshot。</summary>
        internal static object Capture()
        {
            var capture = AccessTools.Method(GameStateReporter, "Capture")
                ?? throw new AssertionException("GameStateReporter.Capture not found in the Bridge assembly");
            return capture.Invoke(null, null);
        }

        internal static T Field<T>(object snapshot, string name) => Traverse.Create(snapshot).Field(name).GetValue<T>();

        /// <summary>Bridge 的 Diagnostics.MaxTextChars：Send 送出前的文字長度上限。</summary>
        internal static int MaxTextChars() =>
            (int)(AccessTools.Field(Diagnostics, "MaxTextChars")
                ?? throw new AssertionException("Diagnostics.MaxTextChars not found in the Bridge assembly")).GetValue(null);
    }

    internal static class TestHelpers
    {
        internal const string Prefix = "[RimWorldModMcp.Bridge.InGameTests]";

        /// <summary>Bridge 休眠時記一行 skipped 並回傳 true；RimTest Redux 會把這種測試算成通過。</summary>
        internal static bool SkipUnlessBridgeActive(string test)
        {
            if (BridgeAccess.TokenPresent) return false;
            Skip(test, "Bridge is dormant (RIMWORLD_MOD_MCP_BRIDGE_TOKEN not set; launch with run_test_cycle)");
            return true;
        }

        internal static void Skip(string test, string reason) => Log.Message($"{Prefix} skipped: {test}: {reason}");

        /// <summary>收集完所有失敗再一次丟出，一次執行就看得到全部問題。</summary>
        internal static void AssertNone(List<string> failures, string message)
        {
            if (failures.Count > 0)
                throw new AssertionException($"{message} ({failures.Count}):\n{string.Join("\n", failures)}");
        }

        /// <summary>逐項比對內容與順序，不只比數量。</summary>
        internal static void CompareSequence(List<string> failures, string what, IList<string> expected, IList<string> actual)
        {
            if (expected.Count != actual.Count)
                failures.Add($"{what}: expected {expected.Count} items, got {actual.Count}");

            for (var i = 0; i < Math.Max(expected.Count, actual.Count); i++)
            {
                var want = i < expected.Count ? expected[i] : "<none>";
                var got = i < actual.Count ? actual[i] : "<none>";
                if (want != got) failures.Add($"{what}[{i}]: expected {want}, got {got}");
            }
        }

        internal static void CompareValue<T>(List<string> failures, string what, T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                failures.Add($"{what}: expected {expected}, got {actual}");
        }
    }
}
