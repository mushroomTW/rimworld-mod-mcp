using System.Text;

namespace RimWorldModMcp.Bridge
{
    /// <summary>
    /// 一份遊戲狀態快照。純資料，不相依 RimWorld，讓 JSON 組裝可以在沒有遊戲的環境下編譯與測試。
    /// </summary>
    internal sealed class GameStateSnapshot
    {
        public string ProgramState = string.Empty;
        public bool MapLoaded;
        public int Tick;
        public bool Paused;
        public string TimeSpeed = string.Empty;
        public bool Loading;
        public string[] OpenWindows = new string[0];
        public int Colonists;
        public string GameVersion = string.Empty;
        public long UptimeMs;
    }

    /// <summary>
    /// 產生 <c>game_state</c> 型別的 NDJSON 行。
    ///
    /// <para>
    /// 欄位直接放在頂層而不是塞進 <c>text</c>，daemon 端才能用型別化的方式讀，
    /// 不必再解析一次字串。
    /// </para>
    /// </summary>
    internal static class GameStatePayload
    {
        internal static string BuildLine(string token, GameStateSnapshot state)
        {
            var builder = new StringBuilder(384);
            builder.Append("{\"type\":\"game_state\",\"token\":\"").Append(DiagnosticPayload.Escape(token)).Append('"');
            builder.Append(",\"program_state\":\"").Append(DiagnosticPayload.Escape(state.ProgramState)).Append('"');
            builder.Append(",\"map_loaded\":").Append(state.MapLoaded ? "true" : "false");
            builder.Append(",\"tick\":").Append(state.Tick);
            builder.Append(",\"paused\":").Append(state.Paused ? "true" : "false");
            builder.Append(",\"time_speed\":\"").Append(DiagnosticPayload.Escape(state.TimeSpeed)).Append('"');
            builder.Append(",\"loading\":").Append(state.Loading ? "true" : "false");
            builder.Append(",\"open_windows\":[");
            for (var i = 0; i < state.OpenWindows.Length; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append('"').Append(DiagnosticPayload.Escape(state.OpenWindows[i])).Append('"');
            }
            builder.Append(']');
            builder.Append(",\"colonists\":").Append(state.Colonists);
            builder.Append(",\"game_version\":\"").Append(DiagnosticPayload.Escape(state.GameVersion)).Append('"');
            builder.Append(",\"uptime_ms\":").Append(state.UptimeMs);
            builder.Append('}');
            return builder.ToString();
        }
    }
}
