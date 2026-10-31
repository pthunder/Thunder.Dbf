//     .mdx   .ndx   .ntx   .cdx           ProjectThunder :: Thunder.Dbf
//       \      \     /      /             Four index formats from four families of xBase,
//        '------[ tag ]-----'             one way to use them: walk keys in order, or seek.

namespace Thunder.Dbf;

/// <summary>One tag (B-tree) of any supported index format.</summary>
public interface IDbfIndexTag
{
    /// <summary>The tag's name (for a single-tag file, the file's name).</summary>
    string Name { get; }

    /// <summary>'C' (character), 'N' (numeric), 'D' (date) or 'L' (logical).</summary>
    char KeyType { get; }

    /// <summary>Bytes per key.</summary>
    int KeyLength { get; }
    /// <summary>True when keys are stored in descending order.</summary>
    bool IsDescending { get; }
    /// <summary>True when the tag keeps only the first record for each key (dBASE UNIQUE).</summary>
    bool IsUnique { get; }

    /// <summary>The dBASE key expression, such as <c>UPPER(LASTNAME)+DTOS(HIRED)</c>.</summary>
    string Expression { get; }

    /// <summary>The FOR filter that decides which records are indexed, or null.</summary>
    string? ForExpression { get; }

    /// <summary>Every key in index order.</summary>
    IEnumerable<IndexEntry> Entries();

    /// <summary>Keys from the first at or after <paramref name="key"/> (prefix match for character keys).</summary>
    IEnumerable<IndexEntry> Seek(object? key);

    /// <summary>Only the keys equal to <paramref name="key"/>.</summary>
    IEnumerable<IndexEntry> SeekExact(object? key);
}

/// <summary>An open index file of any supported format, with one or more tags.</summary>
public interface IDbfIndex : IDisposable
{
    /// <summary>The tags in the file.</summary>
    IReadOnlyList<IDbfIndexTag> Tags { get; }
}

/// <summary>Helpers that work on any <see cref="IDbfIndex"/>.</summary>
public static class DbfIndexExtensions
{
    /// <summary>The tag called <paramref name="name"/>, in any case.</summary>
    public static IDbfIndexTag Tag(this IDbfIndex index, string name) =>
        index.Tags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"The index has no tag named '{name}'.");
}

public sealed partial class MdxIndex : IDbfIndex
{
    IReadOnlyList<IDbfIndexTag> IDbfIndex.Tags => Tags;
}

public sealed partial class MdxTag : IDbfIndexTag
{
    IEnumerable<IndexEntry> IDbfIndexTag.Entries() => Entries().Select(e => new IndexEntry(e.Key, e.RecordNumber));
    IEnumerable<IndexEntry> IDbfIndexTag.Seek(object? key) => Seek(key).Select(e => new IndexEntry(e.Key, e.RecordNumber));
    IEnumerable<IndexEntry> IDbfIndexTag.SeekExact(object? key) => SeekExact(key).Select(e => new IndexEntry(e.Key, e.RecordNumber));
}

public sealed partial class NdxIndex : IDbfIndex
{
    IReadOnlyList<IDbfIndexTag> IDbfIndex.Tags => Tags;
}

public sealed partial class NdxTag : IDbfIndexTag;

public sealed partial class NtxIndex : IDbfIndex
{
    IReadOnlyList<IDbfIndexTag> IDbfIndex.Tags => Tags;
}

public sealed partial class NtxTag : IDbfIndexTag;

public sealed partial class CdxIndex : IDbfIndex
{
    IReadOnlyList<IDbfIndexTag> IDbfIndex.Tags => Tags;
}

public sealed partial class CdxTag : IDbfIndexTag;
