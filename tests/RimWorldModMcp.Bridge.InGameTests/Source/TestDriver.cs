using System;
using System.Diagnostics;
using HarmonyLib;
using Verse;

namespace RimWorldModMcp.Bridge.InGameTests
{
    /// <summary>
    /// 取代 RimTest Redux 的 Run at startup。內建版本在「資料載入完、沒有 long event」的第一幀就跑，
    /// quicktest 下那時地圖還沒生成，地圖測試只能略過；這裡改成 quicktest 時等到 Playing 且有地圖。
    /// </summary>
    [HarmonyPatch(typeof(Root), nameof(Root.Update))]
    internal static class TestDriver
    {
        /// <summary>quicktest 等地圖的上限。地圖生成失敗（例如 DLC 排序錯）時仍要跑，才看得到結果。</summary>
        private const int MapWaitLimitMs = 120000;

        private static readonly Action? RunAll = Internal("Runner:RunAllRegisteredTests");
        private static readonly Action? UpdateCounts = Internal("StatusExplorer:UpdateAllStatusCounts");
        private static readonly Action? LogResults = Internal("Viewer:LogTestsResults");

        private static bool hasRun;
        private static Stopwatch? waitingForMap;

        private static void Postfix()
        {
            if (hasRun || !Ready()) return;
            hasRun = true;

            if (RunAll == null || UpdateCounts == null || LogResults == null)
            {
                Log.Error(TestHelpers.Prefix + " RimTest Redux internals (Runner/StatusExplorer/Viewer) not found; tests were not run.");
                return;
            }

            RunAll();
            UpdateCounts();
            LogResults();
        }

        private static bool Ready()
        {
            if (!PlayDataLoader.Loaded || LongEventHandler.AnyEventNowOrWaiting || Find.UIRoot == null) return false;
            if (!GenCommandLine.CommandLineArgPassed("quicktest")) return true;
            if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null) return true;

            waitingForMap ??= Stopwatch.StartNew();
            if (waitingForMap.ElapsedMilliseconds < MapWaitLimitMs) return false;

            Log.Warning($"{TestHelpers.Prefix} quicktest map not ready after {MapWaitLimitMs / 1000}s (ProgramState={Current.ProgramState}); running tests without a map.");
            return true;
        }

        private static Action? Internal(string typeColonMethod)
        {
            var method = AccessTools.Method("RimTestRedux.Testing." + typeColonMethod);
            return method == null ? null : AccessTools.MethodDelegate<Action>(method);
        }
    }
}
