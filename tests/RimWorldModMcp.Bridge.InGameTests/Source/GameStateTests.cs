using System.Collections.Generic;
using System.Linq;
using RimTestRedux;
using RimWorld;
using Verse;

namespace RimWorldModMcp.Bridge.InGameTests
{
    [TestSuite]
    internal static class GameStateTests
    {
        /// <summary>Root.Update postfix 確實在取樣，而且全部在主執行緒——Current.Game 與 WindowStack 都不是執行緒安全的。</summary>
        [Test]
        public static void SamplesOnMainThreadOnly()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(SamplesOnMainThreadOnly))) return;

            var failures = new List<string>();
            if (SendGameStateProbe.Calls == 0) failures.Add("no game_state was ever sent");
            TestHelpers.CompareValue(failures, "game_state sends off the main thread", 0, SendGameStateProbe.OffMainThread);
            TestHelpers.AssertNone(failures, "game state sampling");
        }

        /// <summary>Capture() 的通用欄位與遊戲當下的值逐項一致。</summary>
        [Test]
        public static void CaptureMatchesLiveState()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(CaptureMatchesLiveState))) return;

            var snapshot = BridgeAccess.Capture();
            var failures = new List<string>();
            TestHelpers.CompareValue(failures, "ProgramState", Current.ProgramState.ToString(), BridgeAccess.Field<string>(snapshot, "ProgramState"));
            TestHelpers.CompareValue(failures, "GameVersion", VersionControl.CurrentVersionString, BridgeAccess.Field<string>(snapshot, "GameVersion"));
            TestHelpers.CompareValue(failures, "Loading", LongEventHandler.AnyEventNowOrWaiting, BridgeAccess.Field<bool>(snapshot, "Loading"));
            if (BridgeAccess.Field<long>(snapshot, "UptimeMs") <= 0) failures.Add("UptimeMs is not positive");

            var windows = Find.WindowStack.Windows.Where(window => window != null).Select(window => window.GetType().Name).Take(8).ToList();
            TestHelpers.CompareSequence(failures, "OpenWindows", windows, BridgeAccess.Field<string[]>(snapshot, "OpenWindows"));

            TestHelpers.AssertNone(failures, "Capture() differs from live state");
        }

        /// <summary>有地圖時，tick、暫停、流速、殖民者數與遊戲當下的值逐項一致。</summary>
        [Test]
        public static void CaptureMatchesMapState()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(CaptureMatchesMapState))) return;
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null)
            {
                TestHelpers.Skip(nameof(CaptureMatchesMapState), "needs a current map (launch with quicktest)");
                return;
            }

            var snapshot = BridgeAccess.Capture();
            var ticks = Find.TickManager;
            var failures = new List<string>();
            TestHelpers.CompareValue(failures, "MapLoaded", true, BridgeAccess.Field<bool>(snapshot, "MapLoaded"));
            TestHelpers.CompareValue(failures, "Tick", ticks.TicksGame, BridgeAccess.Field<int>(snapshot, "Tick"));
            TestHelpers.CompareValue(failures, "Paused", ticks.Paused, BridgeAccess.Field<bool>(snapshot, "Paused"));
            TestHelpers.CompareValue(failures, "TimeSpeed", ticks.CurTimeSpeed.ToString(), BridgeAccess.Field<string>(snapshot, "TimeSpeed"));
            TestHelpers.CompareValue(failures, "Colonists", Find.CurrentMap.mapPawns.FreeColonistsCount, BridgeAccess.Field<int>(snapshot, "Colonists"));
            TestHelpers.AssertNone(failures, "Capture() differs from map state");
        }
    }
}
