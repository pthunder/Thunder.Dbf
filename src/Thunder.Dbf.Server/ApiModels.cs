//      .---.---.---.
//      | { | : | } |     ProjectThunder :: Thunder.Dbf
//      '---'---'---'     The shapes the API answers with. Records themselves are plain
//                        JSON objects keyed by field name, so they're dictionaries here.

namespace Thunder.Dbf.Server;

/// <summary>The answer to GET /health.</summary>
public sealed record HealthResponse(string Status);

/// <summary>The answer to GET /tables.</summary>
public sealed record TableListResponse(IReadOnlyList<TableSummary> Tables);

/// <summary>A table the key can read, and whether it can write it too.</summary>
public sealed record TableSummary(string Name, bool CanWrite);

/// <summary>The answer to GET /tables/{name}: the table's fields and index tags, as this key sees them.</summary>
public sealed record TableSchema(
    string Name,
    int RecordCount,
    DateOnly? LastUpdate,
    int CodePage,
    string? MemoFormat,
    bool CanWrite,
    IReadOnlyList<FieldSchema> Fields,
    IReadOnlyList<IndexTagSchema> IndexTags);

/// <summary>One field. <c>Type</c> is the DBF letter; <c>JsonType</c> is what to send when writing.</summary>
public sealed record FieldSchema(
    string Name,
    string Type,
    string TypeName,
    int Length,
    int Decimals,
    bool Nullable,
    bool AutoIncrement,
    string? JsonType,
    bool CanWrite);

/// <summary>One tag of the table's production .mdx index, usable with GET /tables/{name}/seek.</summary>
public sealed record IndexTagSchema(string Name, string Expression, string? Filter, string KeyType, bool Unique, bool Descending);

/// <summary>A page of records. <c>Skip</c> and <c>Take</c> count record numbers, deleted records included.</summary>
public sealed record RecordPage(
    string Table,
    int Skip,
    int Take,
    int Total,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Records,
    string? Next);

/// <summary>The records an index seek found, in key order.</summary>
public sealed record SeekResponse(string Table, string Tag, string Key, IReadOnlyList<IReadOnlyDictionary<string, object?>> Records);
