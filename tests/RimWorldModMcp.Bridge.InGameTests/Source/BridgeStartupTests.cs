using System.Linq;
using System.Text.RegularExpressions;
using DevTools.Testing;
using Verse;

namespace RimWorldModMcp.Bridge.InGameTests
{
    [TestFixture(TestType.MainMenu)]
    internal sealed class BridgeStartupTests
    {
        /// <summary>Bridge 建構式送出的第一則訊息就是連線公告。錄不到代表 probe 沒有排在 Bridge 之前。</summary>
        [Test]
        public void AnnouncesConnectionFirst()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(AnnouncesConnectionFirst))) return;

            var entries = SendProbe.Snapshot();
            Expect.IsNotEmpty(entries, "first Send (is this mod loaded before rimworldmodmcp.bridge?)");
            if (entries.Count == 0) return;

            var expected = new SentEntry("diagnostic", "RimWorld MCP Bridge connected", "Bridge initialised.").ToString();
            Expect.AreEqual(expected, entries[0].ToString(), "startup announcement mismatch");
        }

        /// <summary>loaded_mods 必須是載入流程結束後完整的 RunningModsListForReading，逐項且依序。</summary>
        [Test]
        public void LoadedModsMatchRunningMods()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(LoadedModsMatchRunningMods))) return;

            var sent = SendProbe.Snapshot().Where(entry => entry.Type == "loaded_mods").ToList();
            Expect.AreEqual(1, sent.Count, "loaded_mods messages count");
            if (sent.Count == 0) return;

            // Bridge 送出前會把文字截到 MaxTextChars；Mod 很多時清單被截斷是設計行為，比對同樣截過的期望值。
            var expected = string.Join(",", LoadedModManager.RunningModsListForReading.Select(pack => pack.PackageId));
            var limit = BridgeAccess.MaxTextChars();
            if (expected.Length > limit) expected = expected.Substring(0, limit);
            Expect.That(() => expected.Split(',').SequenceEqual(sent[0].Text.Split(',')), "loaded_mods report mismatch");
        }

        [Test]
        public void ReportsStartupUptime()
        {
            if (TestHelpers.SkipUnlessBridgeActive(nameof(ReportsStartupUptime))) return;

            var sent = SendProbe.Snapshot().Where(entry => entry.Type == "performance").ToList();
            Expect.AreEqual(1, sent.Count, "performance messages count");
            if (sent.Count == 0) return;

            foreach (var entry in sent)
            {
                Expect.AreEqual("Bridge startup", entry.FirstLine, "performance first line");
                Expect.IsTrue(Regex.IsMatch(entry.Text, @"^uptime_ms=\d+$"), "performance text format: " + entry.Text);
            }
        }
    }
}
