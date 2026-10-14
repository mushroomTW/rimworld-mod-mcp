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
        public string[] OpenWindows = System.Array.Empty<string>();
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
            => DiagnosticPayload.Write((token, state), static (writer, line) =>
            {
                var snapshot = line.state;
                writer.WriteString("type", "game_state");
                DiagnosticPayload.WriteText(writer, "token", line.token);
                DiagnosticPayload.WriteText(writer, "program_state", snapshot.ProgramState);
                writer.WriteBoolean("map_loaded", snapshot.MapLoaded);
                writer.WriteNumber("tick", snapshot.Tick);
                writer.WriteBoolean("paused", snapshot.Paused);
                DiagnosticPayload.WriteText(writer, "time_speed", snapshot.TimeSpeed);
                writer.WriteBoolean("loading", snapshot.Loading);
                writer.WriteStartArray("open_windows");
                foreach (var window in snapshot.OpenWindows)
                {
                    writer.WriteStringValue(window ?? string.Empty);
                }
                writer.WriteEndArray();
                writer.WriteNumber("colonists", snapshot.Colonists);
                DiagnosticPayload.WriteText(writer, "game_version", snapshot.GameVersion);
                writer.WriteNumber("uptime_ms", snapshot.UptimeMs);
            });
    }
}
