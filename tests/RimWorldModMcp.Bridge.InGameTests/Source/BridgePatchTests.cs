using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimTestRedux;
using Verse;

namespace RimWorldModMcp.Bridge.InGameTests
{
    [TestSuite]
    internal static class BridgePatchTests
    {
        /// <summary>1.6 的每個 Log.Warning / Log.Error overload 與 Root.Update 都真的掛上了 Bridge 的 postfix。</summary>
        [Test]
        public static void LogAndRootUpdateCarryBridgePostfix()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(LogAndRootUpdateCarryBridgePostfix))) return;

            var failures = new List<string>();
            var targets = typeof(Log).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "Warning" || method.Name == "Error")
                .Cast<MethodBase>()
                .ToList();
            if (targets.Count == 0) failures.Add("Verse.Log exposes no Warning/Error overloads");
            targets.Add(AccessTools.Method(typeof(Root), nameof(Root.Update)));

            foreach (var target in targets)
            {
                var info = Harmony.GetPatchInfo(target);
                if (info == null || !info.Postfixes.Any(patch => patch.owner == BridgeAccess.HarmonyId))
                    failures.Add(target.FullDescription());
            }

            TestHelpers.AssertNone(failures, "methods without a Bridge postfix");
        }

        /// <summary>
        /// 從每個公開入口記錄一則訊息，Bridge 送出的內容與順序必須完全對應；
        /// WarningOnce / ErrorOnce 靠轉呼 Warning / Error 才被涵蓋，Message 不應送出。
        /// </summary>
        [Test]
        public static void ForwardsEveryLogEntryPoint()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(ForwardsEveryLogEntryPoint))) return;

            var tag = TestHelpers.Prefix + " probe " + Guid.NewGuid().ToString("N");
            var key = tag.GetHashCode();
            var logWindowWasOpen = Find.WindowStack.IsOpen<EditWindow_Log>();
            var pauseOnError = DebugSettings.pauseOnError;

            try
            {
                // pauseOnError 開著時 Log.Error 會暫停遊戲；測試不該留下被自己暫停的地圖。
                DebugSettings.pauseOnError = false;
                Log.Warning(tag + " Warning\nsecond line");
                Log.WarningOnce(tag + " WarningOnce", key);
                Log.Error(tag + " Error (intentional)\r\nsecond line");
                Log.ErrorOnce(tag + " ErrorOnce (intentional)", key + 1);
                Log.Message(tag + " Message");
            }
            finally
            {
                DebugSettings.pauseOnError = pauseOnError;
                // 開發模式下 Log.Error 會開 log 視窗；不留下測試造成的視窗。
                if (!logWindowWasOpen) Find.WindowStack.TryRemove(typeof(EditWindow_Log), doCloseSound: false);
            }

            var expected = new List<string>
            {
                new SentEntry("warning", tag + " Warning", tag + " Warning\nsecond line").ToString(),
                new SentEntry("warning", tag + " WarningOnce", tag + " WarningOnce").ToString(),
                new SentEntry("error", tag + " Error (intentional)", tag + " Error (intentional)\r\nsecond line").ToString(),
                new SentEntry("error", tag + " ErrorOnce (intentional)", tag + " ErrorOnce (intentional)").ToString(),
            };
            var actual = SendProbe.Snapshot()
                .Where(entry => entry.Text.Contains(tag))
                .Select(entry => entry.ToString())
                .ToList();

            var failures = new List<string>();
            TestHelpers.CompareSequence(failures, "forwarded entries", expected, actual);
            TestHelpers.AssertNone(failures, "Log calls not forwarded as expected");
        }
    }
}
