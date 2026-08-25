using System.Runtime.CompilerServices;

// NDJSON 的逐行驗證邏輯是 internal，但它是協定契約的核心，值得直接測試——
// 透過真的 TCP 連線來測會把 socket 的不確定性帶進單元測試。
[assembly: InternalsVisibleTo("RimWorldModMcp.Diagnostics.Tests")]
