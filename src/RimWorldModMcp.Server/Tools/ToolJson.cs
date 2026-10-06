using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol;

namespace RimWorldModMcp.Server.Tools;

/// <summary>工具參數與回傳值的序列化設定。</summary>
internal static class ToolJson
{
    /// <summary>
    /// 預設 encoder 會把 &lt; &gt; " 與所有非 ASCII 字元跳脫成 \uXXXX，XML 與原始碼的回傳因此膨脹三到四成；
    /// 這裡的輸出只走 stdio 給 MCP client，不會嵌進 HTML，不需要那層防護。
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(McpJsonUtilities.DefaultOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
