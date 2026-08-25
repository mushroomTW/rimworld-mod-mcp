using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Indexing.Model;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Tests;

public sealed class DefRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly IndexDatabase _database;
    private readonly SqliteConnection _connection;

    public DefRepositoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-idx-" + Guid.NewGuid().ToString("n")[..12]);
        var store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _database = new IndexDatabase(store);
        _connection = _database.Open();

        Seed();
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

    private void Seed()
    {
        using var transaction = _connection.BeginTransaction();

        DefRepository.Insert(_connection, [
            new DefRecord("Core", "ThingDef", "Steel", null, null, false, "steel", "An alloy of iron.", "Core/Defs/Things.xml", "<ThingDef><defName>Steel</defName></ThingDef>"),
            new DefRecord("Core", "ThingDef", "Gun_Revolver", null, "BaseGun", false, "revolver", "A simple sidearm.", "Core/Defs/Weapons.xml", "<ThingDef><defName>Gun_Revolver</defName></ThingDef>"),
            new DefRecord("Core", "ThingDef", null, "BaseGun", null, true, string.Empty, string.Empty, "Core/Defs/Weapons.xml", "<ThingDef Name=\"BaseGun\" Abstract=\"True\" />"),
            new DefRecord("Royalty", "PawnKindDef", "Empire_Fighter", null, null, false, "fighter", "An imperial soldier.", "Royalty/Defs/Pawns.xml", new string('x', 10_000)),
        ]);

        // 批次寫入之後才建全文索引，與正式的建置流程一致。
        DefRepository.RebuildFts(_connection);

        transaction.Commit();
    }

    [Fact]
    public void SearchByDefNameFindsTheDef()
    {
        var hits = DefRepository.Search(_connection, "Steel", null, 25, includeXml: false);

        Assert.Single(hits);
        Assert.Equal("Steel", hits[0].DefName);
    }

    /// <summary>
    /// 契約：預設不回傳 xml 欄位。單筆 Def 的 XML 可能上百 KB，
    /// 一次搜尋就會灌爆呼叫端的 context。
    /// </summary>
    [Fact]
    public void SearchOmitsXmlByDefault()
    {
        var hits = DefRepository.Search(_connection, "Steel", null, 25, includeXml: false);

        Assert.Null(hits[0].Xml);
        Assert.Null(hits[0].XmlTruncated);
    }

    [Fact]
    public void SearchTruncatesXmlWhenRequested()
    {
        var hits = DefRepository.Search(_connection, "fighter", null, 25, includeXml: true);

        Assert.Single(hits);
        Assert.NotNull(hits[0].Xml);
        Assert.True(hits[0].XmlTruncated);
        Assert.Equal(DefRepository.SearchXmlBytes, hits[0].Xml!.Length);
    }

    [Fact]
    public void SearchFiltersByDefType()
    {
        Assert.Empty(DefRepository.Search(_connection, "Steel", "PawnKindDef", 25, includeXml: false));
        Assert.Single(DefRepository.Search(_connection, "Steel", "ThingDef", 25, includeXml: false));
    }

    /// <summary>
    /// 契約：FTS5 的運算子字元不得讓查詢炸掉。
    /// 這些輸入直接轉送給 FTS5 都會拋語法錯誤。
    /// </summary>
    [Theory]
    [InlineData("steel\"")]
    [InlineData("NEAR")]
    [InlineData("^steel")]
    [InlineData("a:b")]
    [InlineData("*")]
    [InlineData("-steel")]
    [InlineData("steel OR gun")]
    public void OperatorCharactersDoNotBreakTheQuery(string query)
    {
        // 不拋出即通過；結果內容不是這條測試的重點。
        DefRepository.Search(_connection, query, null, 25, includeXml: false);
    }

    /// <summary>
    /// 契約：只有 <c>*</c> 的查詢沒有可用搜尋詞，應退化成「無條件回傳到上限」
    /// 而不是變成錯誤或回傳空集合。
    /// </summary>
    [Fact]
    public void BareWildcardReturnsEverythingUpToTheLimit()
    {
        Assert.Equal(string.Empty, FtsQuery.Match("*"));

        var hits = DefRepository.Search(_connection, "*", null, 25, includeXml: false);
        Assert.Equal(4, hits.Count);
    }

    [Fact]
    public void PrefixSearchIsSupported()
    {
        Assert.Equal("\"Steel\"*", FtsQuery.Match("Steel*"));
        Assert.Single(DefRepository.Search(_connection, "Ste*", null, 25, includeXml: false));
    }

    [Fact]
    public void LimitIsClampedToTheAllowedRange()
    {
        Assert.Equal(4, DefRepository.Search(_connection, "*", null, 9999, includeXml: false).Count);
        Assert.Single(DefRepository.Search(_connection, "*", null, 1, includeXml: false));

        // 低於下限時鉗制成 1，而不是回傳空集合。
        Assert.Single(DefRepository.Search(_connection, "*", null, 0, includeXml: false));
    }

    /// <summary>抽象 Def 沒有 defName，只能靠 Name 屬性讀出來。</summary>
    [Fact]
    public void ReadResolvesAbstractDefsByInheritName()
    {
        var hits = DefRepository.Read(_connection, "BaseGun", null, DefRepository.ReadDefXmlBytes);

        Assert.Single(hits);
        Assert.True(hits[0].Abstract);
        Assert.Equal("BaseGun", hits[0].InheritName);
        Assert.Contains("Abstract", hits[0].Xml!, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadReturnsFullXml()
    {
        var hits = DefRepository.Read(_connection, "Steel", null, DefRepository.ReadDefXmlBytes);

        Assert.Single(hits);
        Assert.Equal("<ThingDef><defName>Steel</defName></ThingDef>", hits[0].Xml);
        Assert.False(hits[0].XmlTruncated);
    }

    /// <summary>
    /// 契約：清空之後，用先前搜得到的關鍵字查 FTS 必須零筆。
    ///
    /// <para>
    /// external content 表若清空順序寫錯，索引項會變成孤兒——查得到 rowid
    /// 卻回讀不到內容，症狀是搜尋結果數量對但欄位全空。這條測試守住那個不變式。
    /// </para>
    /// </summary>
    [Fact]
    public void ClearLeavesNoOrphanFtsEntries()
    {
        Assert.NotEmpty(DefRepository.Search(_connection, "Steel", null, 25, includeXml: false));

        using (var transaction = _connection.BeginTransaction())
        {
            DefRepository.Clear(_connection);
            transaction.Commit();
        }

        Assert.Equal(0, DefRepository.Count(_connection));
        Assert.Empty(DefRepository.Search(_connection, "Steel", null, 25, includeXml: false));

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM def_fts WHERE def_fts MATCH '\"Steel\"';";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void DefNamesExcludesAbstractDefs()
    {
        var names = DefRepository.DefNames(_connection);

        Assert.Contains("Steel", names);
        Assert.Contains("Gun_Revolver", names);
        Assert.DoesNotContain("BaseGun", names);
    }

    /// <summary>
    /// FTS5 預設把底線當分隔字元，所以 <c>Gun_Revolver</c> 會被切成兩個 token。
    /// RimWorld 的 defName 大量使用底線，這個行為直接影響搜尋手感，
    /// 因此明確測起來——日後若有人加上 <c>tokenchars '_'</c>，這條會紅。
    /// </summary>
    [Fact]
    public void UnderscoreIsATokenSeparator()
    {
        Assert.Single(DefRepository.Search(_connection, "revolver", null, 25, includeXml: false));
        Assert.Single(DefRepository.Search(_connection, "Gun_Revolver", null, 25, includeXml: false));
    }

    [Fact]
    public void LikeLiteralEscapesWildcards()
    {
        Assert.Equal(@"Pawn\_X", FtsQuery.LikeLiteral("Pawn_X"));
        Assert.Equal(@"100\%", FtsQuery.LikeLiteral("100%"));
        Assert.Equal(@"a\\b", FtsQuery.LikeLiteral(@"a\b"));
    }

    [Fact]
    public void SchemaMismatchRebuildsTheDatabase()
    {
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version=999;";
            command.ExecuteNonQuery();
        }

        _connection.Close();
        SqliteConnection.ClearAllPools();

        // 版本不符時整個丟掉重建；索引是衍生資料，不需要 migration。
        var store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        using var fresh = new IndexDatabase(store).Open();

        Assert.Equal(0, DefRepository.Count(fresh));
    }
}
