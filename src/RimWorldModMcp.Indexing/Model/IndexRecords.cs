namespace RimWorldModMcp.Indexing.Model;

/// <summary>索引中的一筆 Def 定義。</summary>
public sealed record DefRecord(
    string Pack,
    string DefType,
    string? DefName,
    string? InheritName,
    string? ParentName,
    bool Abstract,
    string Label,
    string Description,
    string FilePath,
    string Xml);

/// <summary>Def 查詢結果（不含完整 XML，除非明確要求）。</summary>
public sealed record DefHit
{
    public required long Id { get; init; }

    public required string Pack { get; init; }

    public required string DefType { get; init; }

    public string? DefName { get; init; }

    public string? InheritName { get; init; }

    public string? ParentName { get; init; }

    public required bool Abstract { get; init; }

    public required string Label { get; init; }

    public required string Description { get; init; }

    public required string FilePath { get; init; }

    /// <summary>只有在明確要求時才會有值。</summary>
    public string? Xml { get; init; }

    /// <summary>XML 是否因為位元組上限而被截斷。</summary>
    public bool? XmlTruncated { get; init; }
}

/// <summary>符號的種類。</summary>
public enum SymbolKind
{
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
    Method,
    Constructor,
    Property,
    Field,
    Event,
}

/// <summary>
/// 從 IL metadata 讀出的一個符號。
///
/// <para>
/// 與 Python 版最大的差異：<see cref="Signature"/> 是真正組出來的型別簽章
/// （而非「宣告那一行的原始文字」），而且有完整的
/// <see cref="BaseChain"/> 與 <see cref="Interfaces"/>。
/// </para>
/// </summary>
public sealed record SymbolRecord
{
    public required string Assembly { get; init; }

    /// <summary>
    /// 組件檔案的實際路徑，反編譯時用來定位。Mod 組件的 <see cref="Assembly"/>
    /// 是 <c>mod:pkg:Name:hash</c> 形式的索引鍵，拼不回檔案路徑，必須另外存。
    /// </summary>
    public string? AssemblyPath { get; init; }

    public required string Fqn { get; init; }

    public required string ShortName { get; init; }

    public required SymbolKind Kind { get; init; }

    public string? ParentFqn { get; init; }

    /// <summary>反編譯單一成員時用來定位的 metadata token。</summary>
    public required int MetadataToken { get; init; }

    public required string Signature { get; init; }

    /// <summary>由近到遠的基底型別鏈，以 <c>|</c> 分隔。僅型別符號有值。</summary>
    public string? BaseChain { get; init; }

    /// <summary>實作的介面，以 <c>|</c> 分隔。僅型別符號有值。</summary>
    public string? Interfaces { get; init; }

    public required string Accessibility { get; init; }

    public required bool IsStatic { get; init; }
}

/// <summary>Def 被引用的一個位置。</summary>
public sealed record DefReference(
    string DefName,
    string FilePath,
    int Line,
    DefReferenceSource SourceKind,
    string? Context,
    DefReferenceConfidence Confidence);

/// <summary>引用出現在哪一類來源。</summary>
public enum DefReferenceSource
{
    DefXml,
    GameSource,
    ModSource,
}

/// <summary>引用判定的可信度。</summary>
public enum DefReferenceConfidence
{
    /// <summary>由語意分析確認，例如 DefOf 欄位存取。</summary>
    Exact,

    /// <summary>由字串比對推測，可能誤中註解或同名字串。</summary>
    Heuristic,
}
