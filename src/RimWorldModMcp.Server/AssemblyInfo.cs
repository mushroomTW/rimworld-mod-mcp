using System.Runtime.CompilerServices;

// 讓測試專案能掛上 UnknownArgumentFilter，驗證的是與正式 server 相同的請求管線。
[assembly: InternalsVisibleTo("RimWorldModMcp.Server.Tests")]
