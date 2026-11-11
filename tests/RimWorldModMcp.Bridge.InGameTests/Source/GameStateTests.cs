using System.Linq;
using DevTools.Testing;
using RimWorld;
using Verse;

namespace RimWorldModMcp.Bridge.InGameTests
{
    [TestFixture(TestType.Playing)]
    internal sealed class GameStateTests
    {
        /// <summary>Root.Update postfix 確實在取樣，而且全部在主執行緒——Current.Game 與 WindowStack 都不是執行緒安全的。</summary>
        [Test]
        public void SamplesOnMainThreadOnly()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(SamplesOnMainThreadOnly))) return;

            Expect.GreaterThan(SendGameStateProbe.Calls, 0, "no game_state was ever sent");
            Expect.AreEqual(0, SendGameStateProbe.OffMainThread, "game_state sends off the main thread");
        }

        /// <summary>Capture() 的通用欄位與遊戲當下的值逐項一致。</summary>
        [Test]
        public void CaptureMatchesLiveState()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(CaptureMatchesLiveState))) return;

            var snapshot = BridgeAccess.Capture();
            Expect.AreEqual(Current.ProgramState.ToString(), BridgeAccess.Field<string>(snapshot, "ProgramState"), "ProgramState mismatch");
            Expect.AreEqual(VersionControl.CurrentVersionString, BridgeAccess.Field<string>(snapshot, "GameVersion"), "GameVersion mismatch");
            Expect.AreEqual(LongEventHandler.AnyEventNowOrWaiting, BridgeAccess.Field<bool>(snapshot, "Loading"), "Loading mismatch");
            Expect.GreaterThan(BridgeAccess.Field<long>(snapshot, "UptimeMs"), 0L, "UptimeMs is not positive");

            var windows = Find.WindowStack.Windows.Where(window => window != null).Select(window => window.GetType().Name).Take(8).ToList();
            Expect.That(() => windows.SequenceEqual(BridgeAccess.Field<string[]>(snapshot, "OpenWindows")), "OpenWindows differs from live state");
        }

        /// <summary>有地圖時，tick、暫停、流速、殖民者數與遊戲當下的值逐項一致。</summary>
        [Test]
        public void CaptureMatchesMapState()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(CaptureMatchesMapState))) return;
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null)
            {
                TestHelpers.Skip(nameof(CaptureMatchesMapState), "needs a current map");
                return;
            }

            var snapshot = BridgeAccess.Capture();
            var ticks = Find.TickManager;
            Expect.AreEqual(true, BridgeAccess.Field<bool>(snapshot, "MapLoaded"), "MapLoaded mismatch");
            Expect.AreEqual(ticks.TicksGame, BridgeAccess.Field<int>(snapshot, "Tick"), "Tick mismatch");
            Expect.AreEqual(ticks.Paused, BridgeAccess.Field<bool>(snapshot, "Paused"), "Paused mismatch");
            Expect.AreEqual(ticks.CurTimeSpeed.ToString(), BridgeAccess.Field<string>(snapshot, "TimeSpeed"), "TimeSpeed mismatch");
            Expect.AreEqual(Find.CurrentMap.mapPawns.FreeColonistsCount, BridgeAccess.Field<int>(snapshot, "Colonists"), "Colonists mismatch");
        }
    }
}
