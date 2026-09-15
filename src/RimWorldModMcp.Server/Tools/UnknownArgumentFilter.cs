using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RimWorldModMcp.Server.Tools;

/// <summary>
/// 拒絕 inputSchema 之外的參數。
///
/// <para>
/// MCP SDK 預設對未知參數靜默忽略：LLM 呼叫端把 <c>parent</c> 打成 <c>pattern</c>，
/// 拿到的不是錯誤而是整個根命名空間的清單——一大包無關結果比一行錯誤訊息貴得多，
/// 而且呼叫端還以為自己問對了。
/// </para>
/// </summary>
internal static class UnknownArgumentFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Reject(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => (request, cancellationToken) =>
        {
            if (request.MatchedPrimitive is McpServerTool tool
                && request.Params?.Arguments is { Count: > 0 } arguments
                && tool.ProtocolTool.InputSchema.ValueKind == JsonValueKind.Object
                && tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties))
            {
                var known = properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                var unknown = arguments.Keys.Where(k => !known.Contains(k)).ToList();

                if (unknown.Count > 0)
                {
                    throw new McpException(
                        $"Unknown parameter(s) for {tool.ProtocolTool.Name}: {string.Join(", ", unknown)}. "
                        + $"Valid parameters: {string.Join(", ", known.Order())}.");
                }
            }

            return next(request, cancellationToken);
        };
}
