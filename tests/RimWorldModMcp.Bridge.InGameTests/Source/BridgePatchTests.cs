using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DevTools.Testing;
using HarmonyLib;
using LudeonTK;
using Verse;

namespace RimWorldModMcp.Bridge.InGameTests
{
    [TestFixture(TestType.MainMenu)]
    internal sealed class BridgePatchTests
    {
        /// <summary>1.7 的每個 Log.Warning / Log.Error overload 與 Root.Update 都真的掛上了 Bridge 的 postfix。</summary>
        [Test]
        public void LogAndRootUpdateCarryBridgePostfix()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(LogAndRootUpdateCarryBridgePostfix))) return;

            var targets = typeof(Log).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name is "Warning" or "Error")
                .Cast<MethodBase>()
                .ToList();
            Expect.IsNotEmpty(targets, "Verse.Log exposes no Warning/Error overloads");
            targets.Add(AccessTools.Method(typeof(Root), nameof(Root.Update)));

            var failures = new List<string>();
            foreach (var target in targets)
            {
                var info = Harmony.GetPatchInfo(target);
                if (info == null || !info.Postfixes.Any(patch => patch.owner == BridgeAccess.HarmonyId))
                    failures.Add(target.FullDescription());
            }

            Expect.IsEmpty(failures, "methods without a Bridge postfix: " + string.Join(", ", failures));
        }

        /// <summary>
        /// 從每個公開入口記錄一則訊息，Bridge 送出的內容與順序必須完全對應；
        /// WarningOnce / ErrorOnce 靠轉呼 Warning / Error 才被涵蓋，Message 不應送出。
        /// </summary>
        [Test]
        [WarningsAllowed(".*")]
        [ErrorsAllowed(".*intentional.*")]
        public void ForwardsEveryLogEntryPoint()
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

            Expect.AreEqual(expected.Count, actual.Count, "forwarded entries count mismatch");
            Expect.That(() => expected.SequenceEqual(actual), "forwarded entries sequence mismatch:\nExpected:\n" + string.Join("\n", expected) + "\nActual:\n" + string.Join("\n", actual));
        }
    }
}
