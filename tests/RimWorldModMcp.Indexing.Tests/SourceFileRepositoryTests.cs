using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// <c>source_file</c> 與 <c>source_fts</c> 的同步不變式。
///
/// <para>
/// FTS5 虛擬表不支援 UPSERT，且 external content 表對同 rowid 重複插入
/// 會留下舊內容的幽靈索引項——這兩件事都只有「對同一個鍵寫兩次」才會現形，
/// 這個檔案就是那個測試。
/// </para>
/// </summary>
public sealed class SourceFileRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly IndexDatabase _database;
    private readonly SqliteConnection _connection;

    public SourceFileRepositoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-src-" + Guid.NewGuid().ToString("n")[..12]);
        var store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _database = new IndexDatabase(store);
        _connection = _database.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private long FtsMatches(string term)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM source_fts WHERE source_fts MATCH $term;";
        command.Parameters.AddWithValue("$term", term);
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void InsertingTheSameKeyTwiceDoesNotThrow()
    {
        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "Verse/Thing.cs", "hello world");

        // 這一行在 UPSERT 版本會直接拋「UPSERT not implemented for virtual table」。
        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "Verse/Thing.cs", "goodbye moon");

        Assert.Equal(1, SourceFileRepository.Count(_connection));
    }

    /// <summary>更新後舊內容的關鍵字必須查不到——否則就是幽靈索引項。</summary>
    [Fact]
    public void UpdateReplacesTheFtsEntry()
    {
        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "Verse/Thing.cs", "hello world");
        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "Verse/Thing.cs", "goodbye moon");

        Assert.Equal(0, FtsMatches("hello"));
        Assert.Equal(1, FtsMatches("moon"));
    }

    [Fact]
    public void DeleteWhereAssemblyLikeRemovesRowsAndFtsEntries()
    {
        SourceFileRepository.Insert(_connection, "mod:pkg:Alpha:aaaa", "A.cs", "alphaonly content");
        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "B.cs", "betaonly content");

        SourceFileRepository.DeleteWhereAssemblyLike(_connection, "mod:pkg:%");

        Assert.Equal(1, SourceFileRepository.Count(_connection));
        Assert.Equal(0, FtsMatches("alphaonly"));
        Assert.Equal(1, FtsMatches("betaonly"));
    }

    [Fact]
    public void ReadReturnsTheFullTextForAnExactKeyAndNullOtherwise()
    {
        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "Verse/Thing.cs", "hello world");

        Assert.Equal("hello world", SourceFileRepository.Read(_connection, "Assembly-CSharp", "Verse/Thing.cs"));
        Assert.Null(SourceFileRepository.Read(_connection, "Assembly-CSharp", "Verse/Missing.cs"));
    }

    [Fact]
    public void ClearRemovesEverything()
    {
        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "A.cs", "somecontent");

        SourceFileRepository.Clear(_connection);

        Assert.Equal(0, SourceFileRepository.Count(_connection));
        Assert.Equal(0, FtsMatches("somecontent"));
    }
}
