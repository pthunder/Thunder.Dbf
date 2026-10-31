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

// ---- keeping indexes up to date ----------------------------------------------------------------

/// <summary>
/// An open index file a table keeps in step with its writes. A format gets write support by
/// implementing this; until then a table refuses writes while such a file sits next to it.
/// </summary>
internal interface IMaintainedIndex : IDisposable
{
    IEnumerable<IMaintainedTag> MaintainedTags { get; }

    /// <summary>
    /// False when this particular file can't be kept exactly as its own program would (an unknown sort
    /// order, a maintenance mode that isn't supported): the table then refuses writes instead.
    /// </summary>
    bool CanMaintain => true;

    /// <summary>
    /// True when the only thing stopping maintenance is the order the keys are in now (a collation
    /// this code doesn't reproduce), which a rebuild in byte order fixes.
    /// </summary>
    bool CanMaintainAfterRebuild => CanMaintain;

    /// <summary>Told before a rebuild that will put the keys in byte order, so reloading meanwhile doesn't refuse.</summary>
    void ExpectRebuild()
    {
    }

    /// <summary>Told when that rebuild failed and the index is being let go: order refusals apply again.</summary>
    void RebuildAbandoned()
    {
    }

    /// <summary>Why <see cref="CanMaintain"/> is false, for logs (it may name the tag); null when it isn't.</summary>
    string? CannotMaintainReason => null;

    /// <summary>Re-reads the tag list, in case another program changed the file.</summary>
    void ReloadTags();

    /// <summary>
    /// Holds the index file's lock across one record's changes to its tags (one lock, one check, one
    /// counter bump), or null when the format takes its lock per change. Disposing publishes and releases.
    /// </summary>
    IDisposable? Hold() => null;

    /// <summary>Pushes every change out of this process, and forces it to the disk unless <paramref name="toDisk"/> is false.</summary>
    void Commit(bool toDisk);
}

/// <summary>One tag of an <see cref="IMaintainedIndex"/>, with keys in the format's own stored bytes.</summary>
internal interface IMaintainedTag
{
    string Name { get; }
    bool IsUnique { get; }

    /// <summary>True when the tag's FOR filter (if any) lets <paramref name="record"/> in.</summary>
    bool Includes(DbfRecord record);

    /// <summary>The stored key bytes for <paramref name="record"/>.</summary>
    byte[] KeyFor(DbfRecord record);

    void Insert(byte[] key, int recordNumber);

    /// <summary>Removes the entry for exactly this key and record; false when there wasn't one.</summary>
    bool Remove(byte[] key, int recordNumber);

    /// <summary>True when the tag holds exactly this key for this record.</summary>
    bool Contains(byte[] key, int recordNumber);

    /// <summary>The record number stored under <paramref name="key"/>, or null. Used for unique tags.</summary>
    int? FirstRecordFor(byte[] key);

    /// <summary>Removes every key (for a rebuild).</summary>
    void Clear();

    /// <summary>
    /// True when no two records may share a key and a write that would give one a taken key must be
    /// refused (Visual FoxPro's CANDIDATE and PRIMARY KEY tags), rather than left out of the tag as a
    /// UNIQUE tag leaves it. The table checks it against the keys a batch has planned but not yet
    /// applied, comparing key bytes, so this is only for formats whose keys compare as bytes.
    /// </summary>
    bool RefusesDuplicates => false;

    /// <summary>
    /// For a unique tag: true when, as in Visual FoxPro, the record indexed under a key keeps it until
    /// it leaves, nothing takes its place when it does, and an earlier record never evicts it. False
    /// (dBASE, Clipper) when the lowest record number with the key always holds it, as a rebuild picks.
    /// </summary>
    bool UniqueKeepsFirstIndexed => false;

    /// <summary>
    /// Replaces every key with <paramref name="keys"/> (in record order), for a rebuild. A unique tag
    /// keeps the lowest record number per key. The default empties the tag and refills it; a format
    /// can instead build the new tree beside the old one and switch over at the end.
    /// </summary>
    void ReplaceAll(IReadOnlyList<(byte[] Key, int Record)> keys)
    {
        Clear();
        foreach (var (key, record) in keys)
        {
            // Judged by the tag's own comparison, so 1E2 and 100 count as one key.
            if (!IsUnique || FirstRecordFor(key) is null)
                Insert(key, record);
        }
    }
}

public sealed partial class MdxIndex : IMaintainedIndex
{
    IEnumerable<IMaintainedTag> IMaintainedIndex.MaintainedTags => Tags;
    void IMaintainedIndex.ReloadTags() => ReloadTags();
    void IMaintainedIndex.Commit(bool toDisk) => Commit(toDisk);
}

public sealed partial class MdxTag : IMaintainedTag
{
    bool IMaintainedTag.Includes(DbfRecord record) => Includes(record);
    byte[] IMaintainedTag.KeyFor(DbfRecord record) => KeyFor(record);
    void IMaintainedTag.Insert(byte[] key, int recordNumber) => Insert(key, recordNumber);
    bool IMaintainedTag.Remove(byte[] key, int recordNumber) => Remove(key, recordNumber);
    bool IMaintainedTag.Contains(byte[] key, int recordNumber) => Contains(key, recordNumber);
    int? IMaintainedTag.FirstRecordFor(byte[] key) => FirstRecordFor(key);
    void IMaintainedTag.Clear() => Clear();
}
