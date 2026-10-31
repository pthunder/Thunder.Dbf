//     _____________
//    |  dBASE III  |     ProjectThunder :: Thunder.Dbf tests
//    |  PLUS  1.1  |     Write to tables whose .ndx files dBASE III Plus made, thousands of
//    |_____________|     times, then walk every page by hand: each key byte for byte what a
//      |_|     |_|       rebuild gives, in order, under separators that route a seek right.
//
//      [ n | -- | 0,rec,key | ... ]   leaf        [ n | -- | pg,0,key | ... | pg ]   inner

using System.Buffers.Binary;
using static Thunder.Dbf.Tests.IndexTestSupport;

namespace Thunder.Dbf.Tests;

public sealed class NdxWriteTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-ndx-");

    public void Dispose() => _dir.Delete(recursive: true);

    /// <summary>Copies fixtures from retroinv into a fresh folder (one per call) and returns its path.</summary>
    private string Copy(params string[] files)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir.FullName, Guid.NewGuid().ToString("N"))).FullName;
        foreach (var file in files)
            File.Copy(Fixture(Path.Combine("retroinv", file)), Path.Combine(folder, file));
        return folder;
    }

    private static DbfTable OpenWritable(string path)
    {
        var table = IndexedTables.OpenWithIndexes(path);
        Assert.True(table.MaintainsIndex);
        return table;
    }

    // ---- what dBASE III wrote -------------------------------------------------------------

    public static TheoryData<string, string> RealIndexes() => new()
    {
        { "BIG1.NDX", "BIG.DBF" },     // STR(ID,8)
        { "BIG2.NDX", "BIG.DBF" },     // NAME
        { "BIG3.NDX", "BIG.DBF" },     // D: dates as Julian-day doubles
        { "ITEMS1.NDX", "ITEMS.DBF" }, // STR(ITEM_ID,8)
        { "NUM2000.NDX", "NUM2000.DBF" },
        { "NUM993.NDX", "NUM993.DBF" },
        { "WIDE1.NDX", "WIDE.DBF" },   // 100-byte keys, four to a page
        { "WIDED1.NDX", "WIDED.DBF" },
    };

    [Theory]
    [MemberData(nameof(RealIndexes))]
    public void Keys_are_built_byte_for_byte_as_dBASE_III_stored_them(string indexFile, string tableFile)
    {
        using var table = DbfTable.Open(Fixture("retroinv/" + tableFile));
        using var index = NdxIndex.Open(Fixture("retroinv/" + indexFile), table: table);
        var stored = ReadRaw(Fixture("retroinv/" + indexFile), index.Tag).Entries;

        var records = table.ReadAll().ToDictionary(r => r.RecordNumber);
        Assert.Equal(records.Count, stored.Count);
        foreach (var (key, record) in stored)
            Assert.Equal(Convert.ToHexString(key), Convert.ToHexString(((IMaintainedTag)index.Tag).KeyFor(records[record])));
    }

    // ---- writes ---------------------------------------------------------------------------

    [Fact]
    public void Thousands_of_appends_split_pages_and_grow_new_roots_in_every_index()
    {
        var folder = Copy("BIG.DBF", "BIG1.NDX", "BIG2.NDX", "BIG3.NDX");
        var path = Path.Combine(folder, "BIG.DBF");
        var before = new[] { "BIG1", "BIG2", "BIG3" }.ToDictionary(n => n, n => Shape(path, Path.Combine(folder, n + ".NDX")));

        var random = new Random(1986);
        using (var table = OpenWritable(path))
            table.AppendRange(Enumerable.Range(0, 2500).Select(i => BigRow(random, id: 10_000 + random.Next(90_000))));

        foreach (var name in before.Keys)
        {
            var ndx = Path.Combine(folder, name + ".NDX");
            AssertIndexMatchesTable(path, ndx);
            var after = Shape(path, ndx);
            Assert.NotEqual(before[name].Root, after.Root);
            Assert.True(after.Depth > before[name].Depth, $"{name} should have grown a level ({before[name].Depth} -> {after.Depth}).");
        }

        // New keys can be sought, through the index reader and through the table.
        using var check = DbfTable.Open(path);
        using var big1 = NdxIndex.Open(Path.Combine(folder, "BIG1.NDX"), table: check);
        var last = check.Read(check.RecordCount - 1);
        var sought = ((decimal)last["ID"]!).ToString().PadLeft(8);
        Assert.Contains(check.RecordCount, big1.Tag.SeekExact(sought).Select(e => e.RecordNumber));
        Assert.Equal(last["ID"], check.Seek(big1.Tag, sought)!["ID"]);

        using var big3 = NdxIndex.Open(Path.Combine(folder, "BIG3.NDX"), table: check);
        Assert.Contains(check.RecordCount, big3.Tag.SeekExact(last["D"]).Select(e => e.RecordNumber));
    }

    [Fact]
    public void Random_updates_deletes_and_recalls_keep_every_index_exact()
    {
        var folder = Copy("BIG.DBF", "BIG1.NDX", "BIG2.NDX", "BIG3.NDX");
        var path = Path.Combine(folder, "BIG.DBF");

        var random = new Random(42);
        using (var table = OpenWritable(path))
        {
            // Each single write is flushed to disk, so volume comes from one batch, then a few hundred steps.
            table.AppendRange(Enumerable.Range(0, 600).Select(i => BigRow(random, id: random.Next(-500, 2000))));
            for (var step = 0; step < 250; step++)
            {
                var pick = random.Next(table.RecordCount);
                switch (random.Next(6))
                {
                    case 0 or 1:
                        table.Append(BigRow(random, id: random.Next(-500, 2000)));
                        break;
                    case 2 or 3:
                        table.Update(pick, BigRow(random, id: random.Next(-500, 2000)));
                        break;
                    case 4:
                        table.Delete(pick);   // a dBASE III index keeps deleted records
                        break;
                    default:
                        table.Recall(pick);
                        break;
                }
            }
        }

        foreach (var name in new[] { "BIG1", "BIG2", "BIG3" })
            AssertIndexMatchesTable(path, Path.Combine(folder, name + ".NDX"));
    }

    [Fact]
    public void Updates_move_keys_and_the_old_key_is_gone()
    {
        var folder = Copy("BIG.DBF", "BIG1.NDX", "BIG2.NDX", "BIG3.NDX");
        var path = Path.Combine(folder, "BIG.DBF");
        using (var table = OpenWritable(path))
            table.Update(4, 99_999_999m, "Moved", new DateOnly(2031, 7, 4));

        AssertIndexMatchesTable(path, Path.Combine(folder, "BIG2.NDX"));
        using var check = DbfTable.Open(path);
        using var big1 = NdxIndex.Open(Path.Combine(folder, "BIG1.NDX"), table: check);
        using var big3 = NdxIndex.Open(Path.Combine(folder, "BIG3.NDX"), table: check);
        Assert.Equal([5], big1.Tag.SeekExact("99999999").Select(e => e.RecordNumber));
        Assert.Empty(big1.Tag.SeekExact("       5"));
        Assert.Equal(5, big1.Tag.Entries().Last().RecordNumber);
        Assert.Equal([5], big3.Tag.SeekExact(new DateOnly(2031, 7, 4)).Select(e => e.RecordNumber));
    }

    [Fact]
    public void A_tree_with_uneven_depths_takes_appends_and_updates()
    {
        // dBASE's INDEX ON hung NUM2000's last leaf straight under the root.
        var folder = Copy("NUM2000.DBF", "NUM2000.NDX");
        var path = Path.Combine(folder, "NUM2000.DBF");
        using (var table = OpenWritable(path))
        {
            table.AppendRange(Enumerable.Range(0, 3000).Select(i => new object?[] { (decimal)(3000 + i) }));
            for (var i = 0; i < 150; i++)
                table.Update(i * 13, (decimal)(-i));
        }

        AssertIndexMatchesTable(path, Path.Combine(folder, "NUM2000.NDX"));
    }

    [Fact]
    public void Wide_keys_and_a_table_with_memos_are_kept_too()
    {
        var wide = Copy("WIDE.DBF", "WIDE1.NDX");
        using (var table = OpenWritable(Path.Combine(wide, "WIDE.DBF")))
        {
            var random = new Random(7);
            table.AppendRange(Enumerable.Range(0, 300).Select(i =>
                new object?[] { new string((char)('A' + random.Next(26)), 1 + random.Next(90)) + $" Ærø {i}" }));
            for (var i = 0; i < 60; i += 3)
                table.Update(i, $"renamed {i}");
        }
        AssertIndexMatchesTable(Path.Combine(wide, "WIDE.DBF"), Path.Combine(wide, "WIDE1.NDX"));

        var items = Copy("ITEMS.DBF", "ITEMS.DBT", "ITEMS1.NDX");
        using (var table = OpenWritable(Path.Combine(items, "ITEMS.DBF")))
        {
            var template = table.Read(0).Values.ToArray();
            table.AppendRange(Enumerable.Range(0, 200).Select(i =>
            {
                var row = (object?[])template.Clone();
                row[0] = (decimal)(500 - i);
                return row;
            }));
        }
        AssertIndexMatchesTable(Path.Combine(items, "ITEMS.DBF"), Path.Combine(items, "ITEMS1.NDX"));
    }

    [Fact]
    public void Short_character_keys_are_padded_with_spaces()
    {
        // The fixtures' keys always fill their width, so make one that doesn't: TRIM(NAME) keeps
        // BIG2's 10-byte key length, and dBASE pads what's left with spaces, not NULs.
        var folder = Copy("BIG.DBF", "BIG2.NDX");
        var path = Path.Combine(folder, "BIG.DBF");
        var ndx = Path.Combine(folder, "BIG2.NDX");
        PatchExpression(ndx, "TRIM(NAME)");

        using (var table = OpenWritable(path))
        {
            table.RebuildIndex();
            table.Append(5000m, "Ab", null);
        }

        AssertIndexMatchesTable(path, ndx);
        using var check = DbfTable.Open(path);
        using var index = NdxIndex.Open(ndx, table: check);
        var stored = ReadRaw(ndx, index.Tag).Entries.Single(e => e.Record == check.RecordCount).Key;
        Assert.Equal("Ab        "u8.ToArray(), stored);
        Assert.Contains(check.RecordCount, index.Tag.SeekExact("Ab").Select(e => e.RecordNumber));
    }

    [Fact]
    public void Number_keys_are_kept_as_doubles()
    {
        // No fixture indexes a plain number, so point BIG3's numeric header at ID and rebuild it.
        var folder = Copy("BIG.DBF", "BIG3.NDX");
        var path = Path.Combine(folder, "BIG.DBF");
        var ndx = Path.Combine(folder, "BIG3.NDX");
        PatchExpression(ndx, "ID");

        using (var table = OpenWritable(path))
        {
            table.RebuildIndex();
            foreach (var id in new[] { -1_234_567m, 0m, 4242m, 99_999_999m, -1m, 4242m })
                table.Append(id, "num", null);
        }

        AssertIndexMatchesTable(path, ndx);
        using var check = DbfTable.Open(path);
        using var index = NdxIndex.Open(ndx, table: check);
        Assert.Equal('N', index.Tag.KeyType);
        Assert.Equal(-1234567m, index.Tag.Entries().First().Key);
        Assert.Equal([check.RecordCount - 3, check.RecordCount], index.Tag.SeekExact(4242m).Select(e => e.RecordNumber));
    }

    [Fact]
    public void Unique_indexes_keep_the_lowest_record_per_key_like_dBASE()
    {
        // Built WITH SET UNIQUE ON: one entry per key, the first record that has it.
        var folder = Copy("BIG.DBF", "BIG2.NDX");
        var path = Path.Combine(folder, "BIG.DBF");
        var ndx = Path.Combine(folder, "BIG2.NDX");
        using (var file = SharedFile.OpenWrite(ndx))
        {
            file.Position = 23;
            file.WriteByte(1);
        }

        string firstName;
        using (var table = OpenWritable(path))
        {
            table.RebuildIndex();
            firstName = (string)table.Read(0)["NAME"]!;
            table.Append(10_001m, firstName, null);      // a duplicate: record 1 keeps the key
            table.Append(10_002m, "Only once", null);
            table.Append(10_003m, "Only once", null);
            table.Update(0, 1m, "Renamed", null);        // record 1 leaves; the next with the name takes over
        }

        AssertIndexMatchesTable(path, ndx);
        using var check = DbfTable.Open(path);
        using var index = NdxIndex.Open(ndx, table: check);
        Assert.True(index.Tag.IsUnique);
        var holders = check.ReadAll().Where(r => (string)r["NAME"]! == firstName).Select(r => r.RecordNumber).ToList();
        Assert.Equal([holders.Min()], index.Tag.SeekExact(firstName).Select(e => e.RecordNumber));
        Assert.Equal([check.RecordCount - 1], index.Tag.SeekExact("Only once").Select(e => e.RecordNumber));
    }

    // ---- rebuilds -------------------------------------------------------------------------

    [Fact]
    public void Rebuild_gives_the_same_keys_as_maintenance()
    {
        var folder = Copy("BIG.DBF", "BIG1.NDX", "BIG2.NDX", "BIG3.NDX");
        var path = Path.Combine(folder, "BIG.DBF");
        var random = new Random(5);
        using (var table = OpenWritable(path))
        {
            table.AppendRange(Enumerable.Range(0, 800).Select(i => BigRow(random, id: random.Next(5000))));
            for (var i = 0; i < 100; i++)
                table.Update(random.Next(table.RecordCount), BigRow(random, id: random.Next(5000)));
        }
        var maintained = new[] { "BIG1", "BIG2", "BIG3" }.ToDictionary(n => n, n => Snapshot(path, Path.Combine(folder, n + ".NDX")));

        using (var table = OpenWritable(path))
            table.RebuildIndex();

        foreach (var (name, keys) in maintained)
        {
            var rebuilt = Snapshot(path, Path.Combine(folder, name + ".NDX"));
            Assert.Equal(keys.InOrder, rebuilt.InOrder);
            Assert.Equal(keys.Pairs, rebuilt.Pairs);
            AssertIndexMatchesTable(path, Path.Combine(folder, name + ".NDX"));
        }
    }

    [Theory]
    [InlineData("BIG1.NDX")]
    [InlineData("BIG2.NDX")]
    [InlineData("BIG3.NDX")]
    public void Rebuilding_a_dBASE_index_keeps_its_header_and_its_keys(string indexFile)
    {
        var folder = Copy("BIG.DBF", indexFile);
        var path = Path.Combine(folder, "BIG.DBF");
        var ndx = Path.Combine(folder, indexFile);
        var original = SharedFile.ReadAllBytes(ndx);
        var dBase = Snapshot(path, ndx);

        using (var table = OpenWritable(path))
            table.RebuildIndex();

        // Key length, keys per page, key type, item length, unique flag and the expression (with
        // dBASE's stray bytes after it) are untouched; only the root and the page count change.
        var rebuilt = SharedFile.ReadAllBytes(ndx);
        Assert.Equal(original.AsSpan(8, 504).ToArray(), rebuilt.AsSpan(8, 504).ToArray());
        Assert.Equal((uint)(rebuilt.Length / 512), BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(4)));

        var ours = Snapshot(path, ndx);
        Assert.Equal(dBase.InOrder, ours.InOrder);
        Assert.Equal(dBase.Pairs, ours.Pairs);
        AssertIndexMatchesTable(path, ndx);
    }

    // ---- damage ---------------------------------------------------------------------------

    public enum Damage { RootPointsPastTheEnd, ChildPointsAtItsParent, ChildPointsAtTheHeader, PageClaimsTooManyKeys }

    [Theory]
    [InlineData(Damage.RootPointsPastTheEnd, true)]
    [InlineData(Damage.ChildPointsAtItsParent, true)]
    [InlineData(Damage.ChildPointsAtItsParent, false)]
    [InlineData(Damage.ChildPointsAtTheHeader, true)]
    [InlineData(Damage.PageClaimsTooManyKeys, false)]
    public void A_damaged_index_is_reported_and_never_loops(Damage damage, bool append)
    {
        var folder = Copy("BIG.DBF", "BIG1.NDX");
        var path = Path.Combine(folder, "BIG.DBF");
        var ndx = Path.Combine(folder, "BIG1.NDX");
        var bytes = SharedFile.ReadAllBytes(ndx);
        var root = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var rootPage = bytes.AsSpan(root * 512, 512);
        switch (damage)
        {
            case Damage.RootPointsPastTheEnd:
                BinaryPrimitives.WriteInt32LittleEndian(bytes, 4000);
                break;
            case Damage.ChildPointsAtItsParent:
                BinaryPrimitives.WriteInt32LittleEndian(rootPage[4..], root); // the first child, where low keys go
                break;
            case Damage.ChildPointsAtTheHeader:
                // The last child, where high keys go: its slot follows the last full item.
                BinaryPrimitives.WriteInt32LittleEndian(rootPage[(4 + BinaryPrimitives.ReadUInt16LittleEndian(rootPage) * 16)..], 0);
                break;
            case Damage.PageClaimsTooManyKeys:
                BinaryPrimitives.WriteUInt16LittleEndian(rootPage, 999);
                break;
        }
        SharedFile.WriteAllBytes(ndx, bytes);

        var id = damage == Damage.ChildPointsAtTheHeader ? 99_999_999m : 0m;
        using var table = OpenWritable(path);
        var error = Assert.Throws<DbfException>(() =>
        {
            if (append)
                table.Append(id, "damage", null);
            else
                table.Update(0, id, "damage", null);
        });
        Assert.Equal(DbfErrorCode.IndexDamaged, error.Code);
    }

    // ---- what can't be maintained -----------------------------------------------------------

    // A localized dBASE that sorted text by a collation leaves keys out of byte order (numbers out of
    // number order would be damage). Either way inserting by byte order would misfile keys: refuse.
    [Theory]
    [InlineData("BIG2.NDX", "byte order")]   // NAME
    [InlineData("BIG3.NDX", "number order")] // dates as Julian-day doubles
    public void Keys_out_of_order_refuse_maintenance(string indexFile, string reason)
    {
        var folder = Copy("BIG.DBF", indexFile);
        var path = Path.Combine(folder, "BIG.DBF");
        var ndx = Path.Combine(folder, indexFile);
        var bytes = SharedFile.ReadAllBytes(ndx);
        var itemLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18));
        var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12));

        // Swap the keys of the first and last items in the leftmost leaf.
        var (_, leaf) = LeftmostParentOfLeaves(bytes);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(leaf * 512));
        var a = bytes.AsSpan(leaf * 512 + 4 + 8, keyLength).ToArray();
        var b = bytes.AsSpan(leaf * 512 + 4 + (count - 1) * itemLength + 8, keyLength).ToArray();
        Assert.NotEqual(a, b);
        b.CopyTo(bytes.AsSpan(leaf * 512 + 4 + 8));
        a.CopyTo(bytes.AsSpan(leaf * 512 + 4 + (count - 1) * itemLength + 8));
        SharedFile.WriteAllBytes(ndx, bytes);

        using (var table = DbfTable.Open(path))
        using (var index = NdxIndex.Open(ndx, table: table))
        {
            Assert.False(((IMaintainedIndex)index).CanMaintain);
            Assert.Contains(reason, ((IMaintainedIndex)index).CannotMaintainReason);
        }

        using var writable = DbfTable.Open(path, writable: true);
        var error = Assert.Throws<DbfException>(() => writable.MaintainIndex(ndx));
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
    }

    // Two children of one page pointing at the same leaf is damage: a change there would change both
    // branches. The write stops with the index untouched.
    [Fact]
    public void Two_children_sharing_a_leaf_fail_the_write_as_damage()
    {
        var folder = Copy("BIG.DBF", "BIG2.NDX");
        var path = Path.Combine(folder, "BIG.DBF");
        var ndx = Path.Combine(folder, "BIG2.NDX");
        var bytes = SharedFile.ReadAllBytes(ndx);
        var itemLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18));
        var (parent, leaf) = LeftmostParentOfLeaves(bytes);
        var record = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(leaf * 512 + 4 + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(parent * 512 + 4 + itemLength), (uint)leaf); // child 1 = child 0
        SharedFile.WriteAllBytes(ndx, bytes);

        using var table = OpenWritable(path);
        var values = table.Read(record - 1).Values.ToArray();
        values[1] = "zzz";
        var error = Assert.Throws<DbfException>(() => table.Update(record - 1, values));
        Assert.Equal(DbfErrorCode.IndexDamaged, error.Code);
        Assert.Contains("twice", Assert.IsType<InvalidDataException>(error.InnerException).Message);
        Assert.Equal(bytes, SharedFile.ReadAllBytes(ndx));
    }

    // Follows first children from the root to the page whose children are leaves.
    private static (int Parent, int Leaf) LeftmostParentOfLeaves(byte[] bytes)
    {
        int FirstChild(int page) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(page * 512 + 4));
        var parent = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        Assert.NotEqual(0, FirstChild(parent)); // the root is an inner page
        while (FirstChild(FirstChild(parent)) != 0)
            parent = FirstChild(parent);
        return (parent, FirstChild(parent));
    }

    // ---- the oracle -------------------------------------------------------------------------

    private static object?[] BigRow(Random random, int id) =>
    [
        (decimal)id,
        $"{(char)('A' + random.Next(26))}{random.Next(300)}",
        random.Next(10) == 0 ? null : new DateOnly(1950, 1, 1).AddDays(random.Next(40_000)),
    ];

    /// <summary>
    /// The index holds exactly the keys a rebuild would (one per record, or the lowest record per key
    /// when unique), byte for byte, in order, with every page sound; and the reader agrees.
    /// </summary>
    private static void AssertIndexMatchesTable(string tablePath, string indexPath)
    {
        using var table = DbfTable.Open(tablePath);
        using var index = NdxIndex.Open(indexPath, table: table);
        var tag = index.Tag;
        var raw = ReadRaw(indexPath, tag).Entries;

        for (var i = 1; i < raw.Count; i++)
            Assert.True(tag.CompareKeys(raw[i - 1].Key, raw[i].Key) <= 0, $"{tag.Name} is out of order at entry {i}.");
        Assert.Equal(raw.Select(e => e.Record), tag.Entries().Select(e => e.RecordNumber));

        var expected = table.ReadAll()
            .Select(r => (Key: Convert.ToHexString(((IMaintainedTag)tag).KeyFor(r)), Record: r.RecordNumber))
            .ToList();
        if (tag.IsUnique)
            expected = expected.GroupBy(e => e.Key).Select(g => (g.Key, g.Min(e => e.Record))).ToList();
        Assert.Equal(
            expected.OrderBy(e => e.Key, StringComparer.Ordinal).ThenBy(e => e.Record),
            raw.Select(e => (Key: Convert.ToHexString(e.Key), e.Record)).OrderBy(e => e.Key, StringComparer.Ordinal).ThenBy(e => e.Record));

        AssertTagMatchesTable(TagView.Of(tag), table);
    }

    private sealed record KeySnapshot(List<string> InOrder, List<(string, int)> Pairs);

    // Equal keys may sit in any order (dBASE doesn't define one), so pairs are compared sorted.
    private static KeySnapshot Snapshot(string tablePath, string indexPath)
    {
        using var table = DbfTable.Open(tablePath);
        using var index = NdxIndex.Open(indexPath, table: table);
        var entries = ReadRaw(indexPath, index.Tag).Entries;
        return new KeySnapshot(
            entries.Select(e => Convert.ToHexString(e.Key)).ToList(),
            entries.Select(e => (Convert.ToHexString(e.Key), e.Record)).Order().ToList());
    }

    private static (uint Root, int Depth) Shape(string tablePath, string indexPath)
    {
        using var table = DbfTable.Open(tablePath);
        using var index = NdxIndex.Open(indexPath, table: table);
        var raw = ReadRaw(indexPath, index.Tag);
        return (raw.Root, raw.Depth);
    }

    private sealed record RawIndex(uint Root, int Depth, List<(byte[] Key, int Record)> Entries);

    /// <summary>
    /// Walks the file's pages without the library's reader, checking what dBASE III relies on: the
    /// page count covers the file, every page is reached once, leaves have no lower page, inner items
    /// no record number, and each separator is at least every key under its child.
    /// </summary>
    private static RawIndex ReadRaw(string path, NdxTag tag)
    {
        var file = SharedFile.ReadAllBytes(path);
        Assert.Equal(0, file.Length % 512);
        var pages = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        Assert.Equal((uint)(file.Length / 512), pages);
        var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(12));
        var maxKeys = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(14));
        var itemLength = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(18));

        var entries = new List<(byte[], int)>();
        var visited = new HashSet<uint>();
        var depth = 0;

        byte[]? Walk(uint page, int level)
        {
            Assert.InRange(page, 1u, pages - 1);
            Assert.True(visited.Add(page), $"Page {page} is reached twice.");
            Assert.InRange(level, 0, 32);
            depth = Math.Max(depth, level + 1);

            var node = file.AsSpan((int)page * 512, 512);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(node);
            Assert.InRange(count, 0, maxKeys);
            var isLeaf = BinaryPrimitives.ReadUInt32LittleEndian(node[4..]) == 0;
            byte[]? highest = null;

            for (var i = 0; i < count; i++)
            {
                var item = node.Slice(4 + i * itemLength, itemLength);
                var key = item.Slice(8, keyLength).ToArray();
                if (isLeaf)
                {
                    Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(item));
                    entries.Add((key, (int)BinaryPrimitives.ReadUInt32LittleEndian(item[4..])));
                    highest = key;
                    continue;
                }

                Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(item[4..]));
                if (Walk(BinaryPrimitives.ReadUInt32LittleEndian(item), level + 1) is { } under)
                    Assert.True(tag.CompareKeys(under, key) <= 0, $"Page {page}'s separator {i} is below a key under it.");
                highest = key;
            }

            if (!isLeaf)
                highest = Walk(BinaryPrimitives.ReadUInt32LittleEndian(node[(4 + count * itemLength)..]), level + 1) ?? highest;
            return highest;
        }

        var root = BinaryPrimitives.ReadUInt32LittleEndian(file);
        Walk(root, 0);
        return new RawIndex(root, depth, entries);
    }

    private static void PatchExpression(string path, string expression)
    {
        using var file = SharedFile.OpenWrite(path);
        file.Position = 24;
        var bytes = new byte[488];
        System.Text.Encoding.ASCII.GetBytes(expression).CopyTo(bytes, 0);
        file.Write(bytes);
    }
}
