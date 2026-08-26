using System.Text;

namespace RimWorldModMcp.Bridge
{
    /// <summary>
    /// 產生送往 Python daemon 的 NDJSON 行。
    ///
    /// 這個類別刻意不相依 RimWorld 與 Harmony，因此能在沒有安裝遊戲的環境下獨立編譯，
    /// 讓 Escape 的逐字元邏輯可以被真正執行與測試（見 tests/test_bridge_payload.py）。
    /// </summary>
    internal static class DiagnosticPayload
    {
        /// <summary>DEL (U+007F)。以數值書寫，避免原始碼裡出現不可見字元。</summary>
        private const char Delete = (char)0x7F;

        internal static string BuildLine(string type, string token, string firstLine, string text)
        {
            var builder = new StringBuilder(256);
            builder.Append("{\"type\":\"").Append(Escape(type));
            builder.Append("\",\"token\":\"").Append(Escape(token));
            builder.Append("\",\"first_line\":\"").Append(Escape(firstLine));
            builder.Append("\",\"text\":\"").Append(Escape(text));
            builder.Append("\"}");
            return builder.ToString();
        }

        /// <summary>
        /// 轉義成合法的 JSON 字串內容。RimWorld 的 log 常含 tab 與其他控制字元，
        /// 漏轉義會讓 daemon 的 json.loads 失敗，整筆診斷就被丟掉。
        /// </summary>
        internal static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var builder = new StringBuilder(value.Length + 16);
            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < ' ' || character == Delete)
                        {
                            builder.Append("\\u").Append(((int)character).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }
            return builder.ToString();
        }
    }
}
