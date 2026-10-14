using System;
using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RimWorldModMcp.Bridge
{
    /// <summary>
    /// 產生送往 daemon 的 NDJSON 行。
    ///
    /// 這個類別刻意不相依 RimWorld 與 Harmony，因此能在沒有安裝遊戲的環境下獨立編譯，
    /// 由 tests/RimWorldModMcp.PlatformContracts.Tests 以原始碼連結直接驗證。
    /// </summary>
    internal static class DiagnosticPayload
    {
        /// <summary>
        /// 只走 localhost 的 NDJSON，不會嵌進 HTML，因此不跳脫非 ASCII，中文日誌不會膨脹成 \uXXXX。
        /// 控制字元與無效的 UTF-16 仍由 Utf8JsonWriter 處理。
        /// </summary>
        private static readonly JsonWriterOptions Options = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        // Log 的 postfix 在錯誤風暴時每 tick 都會呼叫，而且不限於主執行緒；
        // 每條執行緒重用自己的緩衝區與 writer，不必每則訊息都重新配置。
        [ThreadStatic] private static ArrayBufferWriter<byte>? buffer;
        [ThreadStatic] private static Utf8JsonWriter? writer;

        internal static string BuildLine(string type, string token, string firstLine, string text)
            => Write((type, token, firstLine, text), static (json, line) =>
            {
                WriteText(json, "type", line.type);
                WriteText(json, "token", line.token);
                WriteText(json, "first_line", line.firstLine);
                WriteText(json, "text", line.text);
            });

        /// <summary>寫出字串欄位；null 一律送空字串，daemon 對非字串的 type 會丟掉整行。</summary>
        internal static void WriteText(Utf8JsonWriter json, string name, string? value)
            => json.WriteString(name, value ?? string.Empty);

        /// <summary>
        /// 把 <paramref name="writeProperties"/> 寫出的欄位包成一個 JSON 物件，回傳單行字串。
        /// 欄位值以 <paramref name="state"/> 傳入，讓呼叫端用 static lambda，不必每次配置 closure。
        /// </summary>
        internal static string Write<TState>(TState state, Action<Utf8JsonWriter, TState> writeProperties)
        {
            var target = buffer ??= new ArrayBufferWriter<byte>(256);
            target.ResetWrittenCount();

            // Reset 也清掉上一次中途拋例外時殘留的狀態。
            var json = writer ??= new Utf8JsonWriter(target, Options);
            json.Reset(target);

            json.WriteStartObject();
            writeProperties(json, state);
            json.WriteEndObject();
            json.Flush();

            return Encoding.UTF8.GetString(target.WrittenSpan);
        }
    }
}
