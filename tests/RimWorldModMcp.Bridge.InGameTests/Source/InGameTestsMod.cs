using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimWorldModMcp.Bridge.InGameTests
{
    public sealed class InGameTestsMod : Mod
    {
        public InGameTestsMod(ModContentPack content) : base(content)
        {
            // 本 Mod 排在 Bridge 之前載入：此時 Bridge 組件已載入、建構式尚未執行，
            // probe 因此錄得到它的啟動訊息。
            new Harmony("rimworldmodmcp.bridge.ingametests").PatchAll(Assembly.GetExecutingAssembly());
            Log.Message(TestHelpers.Prefix + " probes installed.");
        }
    }
}
