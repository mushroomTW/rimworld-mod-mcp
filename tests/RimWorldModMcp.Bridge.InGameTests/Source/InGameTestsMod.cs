using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimWorldModMcp.Bridge.InGameTests
{
    public sealed class InGameTestsMod : Mod
    {
        public InGameTestsMod(ModContentPack content) : base(content)
        {
            // Bridge 的診斷走背景執行緒、狀態取樣分散在各幀，內建的 Run at startup 也會在
            // quicktest 地圖就緒前就開跑。改由 TestDriver 決定時機：把內建 patch 標成「已跑過」，
            // 不動 RimTest Redux 的設定——改設定值的話，使用者一開關設定視窗就會被寫回檔案。
            var builtInHasRun = AccessTools.Field(AccessTools.TypeByName("RimTestRedux.Patches.Root_Update_Patch"), "_hasRunOnReady");
            if (builtInHasRun == null)
                Log.Warning(TestHelpers.Prefix + " RimTest Redux's run-at-startup hook not found; tests may run twice.");
            else
                builtInHasRun.SetValue(null, true);

            // 本 Mod 排在 Bridge 之前載入：此時 Bridge 組件已載入、建構式尚未執行，
            // probe 因此錄得到它的啟動訊息。
            new Harmony("rimworldmodmcp.bridge.ingametests").PatchAll(Assembly.GetExecutingAssembly());
            Log.Message(TestHelpers.Prefix + " probes installed; RimTest Redux run-at-startup suppressed in favour of TestDriver.");
        }
    }
}
