using System.Text;
using System.Text.Unicode;

namespace RimWorldModMcp.Core.Platform;

/// <summary>
/// 以 UTF-8 位元組為單位的文字截斷。
///
/// <para>
/// 所有 <c>max_bytes</c> 類參數都必須經過這裡。Python 版的截斷一律以 UTF-8 byte 計算，
/// 若在 C# 改用 <c>string.Substring</c>（UTF-16 char 語意）會產生不同的截斷點，
/// 連帶讓 <c>*_truncated</c> 旗標的判定跟著漂移。
/// </para>
/// </summary>
public static class Utf8Text
{
    /// <summary>
    /// 把 <paramref name="value"/> 截到最多 <paramref name="maxBytes"/> 個 UTF-8 位元組。
    /// 截斷點一定落在合法的字元邊界，不會切碎多位元組字元或代理對。
    /// </summary>
    /// <param name="truncated">是否真的發生截斷。</param>
    public static string Truncate(string value, int maxBytes, out bool truncated)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        if (string.IsNullOrEmpty(value))
        {
            truncated = false;
            return value ?? string.Empty;
        }

        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            truncated = false;
            return value;
        }

        // isFinalBlock: false 會讓轉換停在合法邊界並回報吃掉了幾個 char，
        // 因此不會產生半個 code point。直接對 GetBytes 的結果做切片則會。
        var destination = new byte[maxBytes];
        Utf8.FromUtf16(value, destination, out var charsRead, out _, isFinalBlock: false);

        truncated = true;
        return value[..charsRead];
    }
}
