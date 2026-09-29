using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RimTestRedux;
using Verse;

namespace RimWorldModMcp.Bridge.InGameTests
{
    [TestSuite]
    internal static class BridgeStartupTests
    {
        /// <summary>Bridge 建構式送出的第一則訊息就是連線公告。錄不到代表 probe 沒有排在 Bridge 之前。</summary>
        [Test]
        public static void AnnouncesConnectionFirst()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(AnnouncesConnectionFirst))) return;

            var entries = SendProbe.Snapshot();
            var failures = new List<string>();
            var expected = new SentEntry("diagnostic", "RimWorld MCP Bridge connected", "Bridge initialised.").ToString();
            TestHelpers.CompareValue(failures, "first Send (is this mod loaded before rimworldmodmcp.bridge?)",
                expected, entries.Count > 0 ? entries[0].ToString() : "<none>");
            TestHelpers.AssertNone(failures, "startup announcement");
        }

        /// <summary>loaded_mods 必須是載入流程結束後完整的 RunningModsListForReading，逐項且依序。</summary>
        [Test]
        public static void LoadedModsMatchRunningMods()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(LoadedModsMatchRunningMods))) return;

            var failures = new List<string>();
            var sent = SendProbe.Snapshot().Where(entry => entry.Type == "loaded_mods").ToList();
            TestHelpers.CompareValue(failures, "loaded_mods messages", 1, sent.Count);

            if (sent.Count > 0)
            {
                // Bridge 送出前會把文字截到 MaxTextChars；Mod 很多時清單被截斷是設計行為，比對同樣截過的期望值。
                var expected = string.Join(",", LoadedModManager.RunningModsListForReading.Select(pack => pack.PackageId));
                var limit = BridgeAccess.MaxTextChars();
                if (expected.Length > limit) expected = expected.Substring(0, limit);
                TestHelpers.CompareSequence(failures, "loaded_mods", expected.Split(','), sent[0].Text.Split(','));
            }

            TestHelpers.AssertNone(failures, "loaded_mods report");
        }

        [Test]
        public static void ReportsStartupUptime()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(ReportsStartupUptime))) return;

            var failures = new List<string>();
            var sent = SendProbe.Snapshot().Where(entry => entry.Type == "performance").ToList();
            TestHelpers.CompareValue(failures, "performance messages", 1, sent.Count);

            foreach (var entry in sent)
            {
                TestHelpers.CompareValue(failures, "performance first line", "Bridge startup", entry.FirstLine);
                if (!Regex.IsMatch(entry.Text, @"^uptime_ms=\d+$")) failures.Add("performance text: " + entry.Text);
            }

            TestHelpers.AssertNone(failures, "startup performance report");
        }
    }
}
