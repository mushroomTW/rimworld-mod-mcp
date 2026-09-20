using System.Runtime.CompilerServices;

// 讓測試能驗證快取的內部界線（例如反編譯映像的上限），
// 而不必把那些只為測試存在的成員放到公開 API 上。
[assembly: InternalsVisibleTo("RimWorldModMcp.Indexing.Tests")]
