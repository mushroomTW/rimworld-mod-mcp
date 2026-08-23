using System;
using System.IO;
using System.Text;
using RimWorldMcp.Bridge;

namespace RimWorldMcp.Bridge.Tools
{
    /// <summary>
    /// 從 stdin 讀入 base64(UTF-8) 字串（每行一筆），對每筆輸出 DiagnosticPayload.BuildLine
    /// 產生的 NDJSON 行。
    ///
    /// 之所以用 base64：測試輸入本身就包含換行與控制字元，直接走 stdin 會被切斷。
    /// 輸出直接寫 stdout 的 byte stream，避開主控台字碼頁對 UTF-8 的干擾。
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n" };
            string line;
            while ((line = input.ReadLine()) != null)
            {
                var text = Encoding.UTF8.GetString(Convert.FromBase64String(line.Trim()));
                output.WriteLine(DiagnosticPayload.BuildLine("error", "test-token", "probe", text));
            }
            output.Flush();
            return 0;
        }
    }
}
