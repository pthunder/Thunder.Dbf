//        lock 0x7FFFFFFE            ProjectThunder :: Thunder.Dbf tests
//      +-----------------+          What keeps a maintained .cdx safe: Visual FoxPro's index
//      | new | parent |  |  power   lock, crash-safe write order, pages with one owner each,
//      | page| last   |  |  cut! -> keys FoxPro would build byte for byte, and refusing what
//      +-----------------+          can't be reproduced. Each test failed before its fix.

using System.Buffers.Binary;
using System.Text;
using static Thunder.Dbf.Tests.IndexTestSupport;

namespace Thunder.Dbf.Tests;

public sealed partial class CdxWriteTests
{
    // ---- Visual FoxPro's index lock -----------------------------------------------------------

    // Visual FoxPro locks one byte at 0x7FFFFFFE of the index file itself while it reads or changes
    // it (Harbour's IDX_LOCKPOS_VFP); while another program holds it, no key may change.
    [Fact]
    public void A_held_FoxPro_index_lock_holds_the_write_back()
    {
        Assert.Equal((0x7FFF_FFFEL, 1L), CdxIndex.FoxProLock);
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        var table = OpenWritable(dbf);
        table.LockTimeout = TimeSpan.FromMilliseconds(200);
        var template = table.Read(0).Values.ToArray();
        var before = SharedFile.ReadAllBytes(cdx);

        using (var other = new FileStream(cdx, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            var locks = new DbfLocks(other, cdx);
            using (locks.Range((0x7FFF_FFFE, 1), DbfErrorCode.TableLocked, "test"))
            {
                var error = Assert.Throws<DbfException>(() => table.Append(Call(template, id: 5000, contact: 1, subject: "held")));
                Assert.Equal(DbfErrorCode.TableLocked, error.Code);
                Assert.Equal(16, table.RecordCount); // refused before the record was written
            }
            DbfLocks.CloseWhenSafe(other);
        }
        Assert.Equal(before, SharedFile.ReadAllBytes(cdx));

        table.Append(Call(template, id: 5001, contact: 1, subject: "free"));
        table.Dispose();
        AssertIndexMatchesTable(dbf, cdx);
    }

    // ---- leaves past 4-byte record numbers -----------------------------------------------------

    // Record 67,108,864 doesn't fit the 26 record bits a 4-byte entry leaves beside two 3-bit counts
    // (4-byte keys), so the entry grows to 5 bytes: 34 record bits, a mask capped at 32 bits, and the
    // counts still in the entry's top 6 bits, as Harbour's dbfcdx1.c packs and reads them.
    [Fact]
    public void Record_numbers_past_four_bytes_widen_the_entry_and_keep_the_counts_on_top()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        using var table = DbfTable.Open(dbf);
        var bytes = SharedFile.ReadAllBytes(cdx);
        long leaf;
        using (var index = CdxIndex.Open(new MemoryStream(bytes), table: table))
            leaf = index["CALL_ID"].LeafOffsets().Single(); // a root leaf

        var page = new byte[512];
        bytes.AsSpan((int)leaf, 2).CopyTo(page);                    // the attributes FoxPro gave it
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(2), 2); // two keys
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(12), 512 - 24 - 2 * 5 - 4 - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), 0xFFFF_FFFF);
        (page[18], page[19], page[20], page[21], page[22], page[23]) = (7, 7, 34, 3, 3, 5);
        // Entry 0: record 0x4000000, no counts. Entry 1: record 0x4000001, 3 bytes shared with key 0.
        new byte[] { 0x00, 0x00, 0x00, 0x04, 0x00 }.CopyTo(page, 24);
        new byte[] { 0x01, 0x00, 0x00, 0x04, 0x0C }.CopyTo(page, 29);
        new byte[] { 0x80, 0x00, 0x00, 0x01 }.CopyTo(page, 508); // CALL_ID 1, an integer key
        page[507] = 0x02;                                          // CALL_ID 2: only its last byte
        page.CopyTo(bytes, (int)leaf);

        using var patched = CdxIndex.Open(new MemoryStream(bytes), table: table);
        var tag = patched["CALL_ID"];
        var entries = tag.RawEntries();
        Assert.Equal([67_108_864, 67_108_865], entries.Select(e => e.Record));
        Assert.Equal("80000001", Convert.ToHexString(entries[0].Key));
        Assert.Equal("80000002", Convert.ToHexString(entries[1].Key));
        Assert.Equal(page, tag.ReencodeLeaf(leaf));
    }

    // ---- candidate keys --------------------------------------------------------------------------

    // AppendRange applies its rows' keys at the commit, so the second of two equal candidate keys in
    // one batch must be checked against the first, not only against the tree.
    [Fact]
    public void Two_equal_candidate_keys_in_one_batch_are_refused()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        using (var table = OpenWritable(dbf))
        {
            var template = table.Read(0).Values.ToArray();
            var error = Assert.Throws<DbfException>(() => table.AppendRange(
                [Call(template, id: 900, contact: 1, subject: "a"), Call(template, id: 901, contact: 1, subject: "b"), Call(template, id: 900, contact: 2, subject: "dup")]));
            Assert.Equal(DbfErrorCode.InvalidValue, error.Code);
            Assert.Contains("CALL_ID", error.Message);
            Assert.Equal(18, table.RecordCount); // the rows before the duplicate stay
        }
        AssertIndexMatchesTable(dbf, cdx);
    }

    // A rebuild's candidate keys are checked among themselves: the old tree says nothing about them.
    // Another program changed keys behind the index's back, so the index no longer matches the table
    // and isn't attached when the table opens. Opened exclusively, a takeover with a rebuild repairs
    // it, and that rebuild checks candidate keys among themselves.
    [Fact]
    public void A_rebuild_checks_candidate_keys_on_their_own()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        int first, second;
        using (var other = DbfTable.Open(dbf, writable: true, allowUnmaintainedIndexes: true))
        {
            (first, second) = ((int)other.Read(0)["CALL_ID"]!, (int)other.Read(1)["CALL_ID"]!);
            other.UpdateFields(0, new Dictionary<string, object?> { ["CALL_ID"] = second });
            other.UpdateFields(1, new Dictionary<string, object?> { ["CALL_ID"] = first });
        }
        using (var shared = DbfTable.Open(dbf, writable: true, allowUnmaintainedIndexes: true, maintainFoxProIndexes: true))
        {
            Assert.False(shared.MaintainsIndex);
            Assert.Equal(DbfErrorCode.ExclusiveUseRequired, Assert.Throws<DbfException>(() => shared.MaintainIndex(cdx, rebuild: true)).Code);
        }
        TakeOverAndRebuild(dbf, cdx); // each key is still unique: the rebuild goes through
        AssertIndexMatchesTable(dbf, cdx, rebuilt: true);

        // Now two records really share one, and the rebuild stops before the index changes.
        using (var other = DbfTable.Open(dbf, writable: true, allowUnmaintainedIndexes: true))
            other.UpdateFields(2, new Dictionary<string, object?> { ["CALL_ID"] = first });
        var before = SharedFile.ReadAllBytes(cdx);
        Assert.Equal(DbfErrorCode.InvalidValue, Assert.Throws<DbfException>(() => TakeOverAndRebuild(dbf, cdx)).Code);
        Assert.Equal(before, SharedFile.ReadAllBytes(cdx));
    }

    private static void TakeOverAndRebuild(string dbf, string cdx)
    {
        using var table = DbfTable.Open(dbf, writable: true, allowUnmaintainedIndexes: true, maintainFoxProIndexes: true, exclusive: true);
        table.MaintainIndex(cdx, rebuild: true);
    }

    // ---- crash ordering ---------------------------------------------------------------------------

    // The power is cut after every possible number of page writes during a split, a removal that
    // empties a leaf, and a rebuild. Whatever made it to disk, the tree must still be walkable and
    // hold every key it held before (a key may show twice mid-split, but never go missing), and no
    // page may be on the free list while the tree still uses it.
    [Fact]
    public void A_crash_at_any_write_loses_no_key()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        using var table = DbfTable.Open(dbf);
        var bytes = SharedFile.ReadAllBytes(cdx);

        // Fill the root leaf from the front until the next key splits it in the middle and grows a
        // new root above it.
        var next = 5000;
        while (true)
        {
            var (grown, _) = Crash(bytes, table, int.MaxValue, tag => tag.Insert(IntegerKey(next), next));
            if (grown.Length > bytes.Length)
                break;
            bytes = grown;
            next -= 2;
        }
        AssertEveryCrashKeeps(bytes, table, tag => tag.Insert(IntegerKey(next), next), adds: (IntegerKey(next), next));
        bytes = Crash(bytes, table, int.MaxValue, tag => tag.Insert(IntegerKey(next), next)).Bytes;
        Assert.Equal(2, Leaves(bytes, table).Count);

        // Keep filling the left leaf from the front until it splits in the middle too: keys move to
        // a new page, and the root gains an entry for it.
        var gap = next - 1;
        while (true)
        {
            var (grown, _) = Crash(bytes, table, int.MaxValue, tag => tag.Insert(IntegerKey(gap), gap));
            if (grown.Length > bytes.Length)
                break;
            bytes = grown;
            gap--;
        }
        AssertEveryCrashKeeps(bytes, table, tag => tag.Insert(IntegerKey(gap), gap), adds: (IntegerKey(gap), gap));
        bytes = Crash(bytes, table, int.MaxValue, tag => tag.Insert(IntegerKey(gap), gap)).Bytes;
        Assert.Equal(3, Leaves(bytes, table).Count);

        // Empty the last leaf, key by key; its last removal drops it from the tree.
        while (LastLeafCount(bytes, table) > 1)
        {
            var last = Entries(bytes, table)[^1];
            bytes = Crash(bytes, table, int.MaxValue, tag => tag.Remove(Convert.FromHexString(last.Key), last.Record)).Bytes;
        }
        var only = Entries(bytes, table)[^1];
        AssertEveryCrashKeeps(bytes, table, tag => tag.Remove(Convert.FromHexString(only.Key), only.Record), removes: only);

        // A rebuild: either the old keys or the new ones, never a mix or a broken tree.
        var old = Entries(bytes, table);
        var rebuilt = Enumerable.Range(1, 300).Select(i => (Key: IntegerKey(i), Record: i)).ToList();
        var rebuiltEntries = rebuilt.Select(e => (Convert.ToHexString(e.Key), e.Record)).ToList();
        for (var allowed = 0; ; allowed++)
        {
            var (after, crashed) = Crash(bytes, table, allowed, tag => tag.ReplaceAll(rebuilt));
            var found = Entries(after, table);
            Assert.True(found.SequenceEqual(old) || found.SequenceEqual(rebuiltEntries),
                $"after {allowed} writes the tree holds neither the old keys nor the new ones");
            AssertNoFreePageInUse(after, table);
            if (!crashed)
                break;
        }
    }

    private static void AssertEveryCrashKeeps(byte[] bytes, DbfTable table, Action<CdxTag> change,
        (byte[] Key, int Record)? adds = null, (string Key, int Record)? removes = null)
    {
        var before = Entries(bytes, table);
        var allowedExtra = adds is { } a ? new[] { (Convert.ToHexString(a.Key), a.Record) } : [];
        for (var allowed = 0; ; allowed++)
        {
            var (after, crashed) = Crash(bytes, table, allowed, change);
            var found = Entries(after, table);
            foreach (var entry in before.Where(e => removes is null || e != removes.Value))
                Assert.True(found.Contains(entry), $"after {allowed} writes, record {entry.Record} is missing from the tree");
            Assert.All(found, e => Assert.True(before.Contains(e) || allowedExtra.Contains(e), $"after {allowed} writes, record {e.Record} appeared from nowhere"));
            AssertNoFreePageInUse(after, table);
            if (!crashed)
                break;
        }
    }

    private static int LastLeafCount(byte[] bytes, DbfTable table)
    {
        using var index = CdxIndex.Open(new MemoryStream(bytes), table: table);
        var leaf = index["CALL_ID"].LeafOffsets()[^1];
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)leaf + 2));
    }

    private static List<long> Leaves(byte[] bytes, DbfTable table)
    {
        using var index = CdxIndex.Open(new MemoryStream(bytes), table: table);
        return index["CALL_ID"].LeafOffsets();
    }

    private static List<(string Key, int Record)> Entries(byte[] bytes, DbfTable table)
    {
        using var index = CdxIndex.Open(new MemoryStream(bytes), table: table);
        return index["CALL_ID"].RawEntries().Select(e => (Convert.ToHexString(e.Key), e.Record)).ToList();
    }

    private static void AssertNoFreePageInUse(byte[] bytes, DbfTable table)
    {
        using var index = CdxIndex.Open(new MemoryStream(bytes), table: table);
        var used = index.Tags.SelectMany(t => t.Pages()).ToHashSet();
        var listed = 0;
        for (var page = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)); page is not 0 and not uint.MaxValue && listed++ < bytes.Length / 512;
             page = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)page)))
            Assert.DoesNotContain((long)page, used);
    }

    private static byte[] IntegerKey(int value)
    {
        var key = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(key, unchecked((uint)value + 0x80000000));
        return key;
    }

    // Runs a change on a copy of the index whose disk dies after the given number of writes.
    private static (byte[] Bytes, bool Crashed) Crash(byte[] bytes, DbfTable table, int writesAllowed, Action<CdxTag> change)
    {
        var stream = new PowerCutStream(bytes);
        using var index = CdxIndex.Open(stream, table: table, leaveOpen: true);
        stream.WritesLeft = writesAllowed;
        try
        {
            change(index["CALL_ID"]);
            return (stream.ToArray(), false);
        }
        catch (IOException)
        {
            return (stream.ToArray(), true);
        }
    }

    private sealed class PowerCutStream : MemoryStream
    {
        public PowerCutStream(byte[] bytes)
        {
            base.Write(bytes);
            Position = 0;
        }

        public int WritesLeft { get; set; } = int.MaxValue;

        // A MemoryStream subclass's span write goes through the array one, so only that one counts.
        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (WritesLeft-- <= 0)
                throw new IOException("The power went out.");
            base.Write(buffer, offset, count);
        }
    }

    // ---- every page has one owner ------------------------------------------------------------------

    // A page two tags share, a sibling link that doesn't point back, or a free page a tree still uses
    // would make the next change overwrite another tree's keys: such an index is damaged, not kept.
    [Fact]
    public void Pages_with_two_owners_or_one_way_links_make_the_index_damaged()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        using (var table = OpenWritable(dbf))
        {
            var template = table.Read(0).Values.ToArray();
            table.AppendRange(Enumerable.Range(0, 600).Select(i => Call(template, id: 17 + i, contact: i % 5, subject: "")));
        }
        var grown = SharedFile.ReadAllBytes(cdx);
        long tagA, tagB, middleLeaf, rootA;
        using (var index = CdxIndex.Open(new MemoryStream(grown)))
        {
            middleLeaf = index["CALL_ID"].LeafOffsets()[1];
            (tagA, tagB) = (index["CALL_ID"].HeaderOffset, index["CONTACT_ID"].HeaderOffset);
            rootA = BinaryPrimitives.ReadUInt32LittleEndian(grown.AsSpan((int)tagA));
        }

        var freeListOnALeaf = (byte[])grown.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(freeListOnALeaf.AsSpan(4), (uint)middleLeaf);
        var oneWayLink = (byte[])grown.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(oneWayLink.AsSpan((int)middleLeaf + 4), uint.MaxValue);
        var sharedTree = (byte[])grown.Clone(); // CONTACT_ID would grow CALL_ID's tree
        BinaryPrimitives.WriteUInt32LittleEndian(sharedTree.AsSpan((int)tagB), (uint)rootA);

        foreach (var damaged in new[] { freeListOnALeaf, oneWayLink, sharedTree })
        {
            SharedFile.WriteAllBytes(cdx, damaged);
            using (var table = DbfTable.Open(dbf, writable: true, maintainFoxProIndexes: true))
            {
                Assert.False(table.MaintainsIndex);
                Assert.Equal(DbfErrorCode.IndexWouldGoStale, Assert.Throws<DbfException>(() => table.Delete(0)).Code);
            }
            Assert.Equal(damaged, SharedFile.ReadAllBytes(cdx));
        }
    }

    // ---- numeric expressions ----------------------------------------------------------------------

    // FoxPro works AGE+0 out in binary floating point and this library in decimal; a bare field is
    // read once, correctly rounded, by both, so only that is kept.
    [Fact]
    public void A_numeric_key_over_more_than_a_bare_field_is_refused()
    {
        var (dbf, cdx) = People();
        SetExpression(cdx, "AGE", "AGE+0");
        using var reader = DbfTable.Open(dbf);
        using (var stream = new FileStream(cdx, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        using (var index = CdxIndex.Open(stream, table: reader))
            Assert.Contains("binary floating point", index.MaintainRefusal);

        SetExpression(cdx, "AGE", "AGE");
        using (var stream = new FileStream(cdx, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        using (var index = CdxIndex.Open(stream, table: reader))
            Assert.DoesNotContain("AGE can't", index.MaintainRefusal ?? "");
    }

    // ---- character keys ---------------------------------------------------------------------------

    // Decoding drops NULs, but FoxPro keys the field's bytes: "A\0" is 41 00 20 20 ..., not 41 20 20 20.
    [Fact]
    public void Character_keys_are_the_fields_stored_bytes()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "setup");
        using (var table = OpenWritable(dbf))
            table.Append("A\0", 1);

        using var reader = DbfTable.Open(dbf);
        using var index = CdxIndex.Open(cdx, table: reader);
        var stored = index["KEY_NAME"].RawEntries().Single(e => e.Record == reader.RecordCount).Key;
        Assert.Equal("4100" + string.Concat(Enumerable.Repeat("20", 48)), Convert.ToHexString(stored));
        AssertIndexMatchesTable(dbf, cdx);
    }

    // In a double-byte code page a trailing byte can look like an ASCII letter, so UPPER() isn't
    // reproduced there; any function past the few built from stored bytes is refused everywhere.
    [Fact]
    public void Character_functions_FoxPro_works_out_by_byte_are_refused()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "setup");
        SetExpression(cdx, "KEY_NAME", "UPPER(key_name)");
        Assert.Null(Refusal(dbf, cdx)); // single-byte code page, upper-case ASCII values: kept

        var table = SharedFile.ReadAllBytes(dbf);
        var codePage = table[29];
        table[29] = 0x13; // code page 932, Shift-JIS
        SharedFile.WriteAllBytes(dbf, table);
        Assert.Contains("double-byte", Refusal(dbf, cdx));

        table[29] = codePage;
        SharedFile.WriteAllBytes(dbf, table);
        SetExpression(cdx, "KEY_NAME", "SUBSTR(key_name,1,50)");
        Assert.Contains("SUBSTR", Refusal(dbf, cdx));
    }

    private static string? Refusal(string dbf, string cdx)
    {
        using var reader = DbfTable.Open(dbf);
        using var stream = new FileStream(cdx, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using var index = CdxIndex.Open(stream, table: reader);
        return index.MaintainRefusal;
    }

    // Puts a new key expression in the tag's pool, past the ones already there.
    private static void SetExpression(string cdx, string tagName, string expression) => PatchTag(cdx, tagName, header =>
    {
        var text = Encoding.ASCII.GetBytes(expression + "\0");
        header.AsSpan(512 + 300, 200).Clear();
        text.CopyTo(header, 512 + 300);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(508), 300);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(510), (ushort)text.Length);
    });

    // ---- Visual FoxPro's UNIQUE tags ----------------------------------------------------------------

    // Visual FoxPro files a key under the first record that brings it, never hands it to another
    // record when that one leaves, and never lets an earlier record take it over: only REINDEX
    // picks the lowest record number.
    [Fact]
    public void Unique_tags_change_the_way_Visual_FoxPro_changes_them()
    {
        var (dbf, cdx) = People();
        PatchTag(cdx, "STATE", header => header[14] |= CdxIndex.OptionUnique);
        int first, second;
        using (var table = TakeOver(dbf, cdx))
        {
            var row = table.Read(0).Values.ToArray();
            row[4] = "Q1";
            first = table.Append(row) + 1;
            second = table.Append(row) + 1;                                                    // not indexed: Q1 is taken
            table.UpdateFields(first - 1, new Dictionary<string, object?> { ["STATE"] = "Q2" }); // Q1 leaves with it
            table.UpdateFields(0, new Dictionary<string, object?> { ["STATE"] = "Q2" });         // record 1 doesn't take Q2 over
        }

        Assert.Equal([], Holders(dbf, cdx, "Q1"));
        Assert.Equal([first], Holders(dbf, cdx, "Q2"));

        using (var table = IndexedTables.OpenWithIndexes(dbf))
            table.RebuildIndex();
        Assert.Equal([second], Holders(dbf, cdx, "Q1"));
        Assert.Equal([1], Holders(dbf, cdx, "Q2"));
    }

    private static List<int> Holders(string dbf, string cdx, string state)
    {
        using var reader = DbfTable.Open(dbf);
        using var index = CdxIndex.Open(cdx, table: reader);
        return index["STATE"].SeekExact(state).Select(e => e.RecordNumber).ToList();
    }

    // ---- taking an index over ----------------------------------------------------------------------

    // Keys out of order in the root are a sort order a rebuild fixes, not damage.
    [Fact]
    public void Disorder_in_the_root_can_be_taken_over_and_rebuilt()
    {
        var (dbf, cdx) = People();
        long root;
        using (var index = CdxIndex.Open(cdx))
            root = BinaryPrimitives.ReadUInt32LittleEndian(SharedFile.ReadAllBytes(cdx).AsSpan((int)index["STATE"].HeaderOffset));
        var bytes = SharedFile.ReadAllBytes(cdx);
        Assert.Equal(0, bytes[root] & 0x02); // an interior root
        // Swap the first two keys (and their record numbers), but not the children they lead to.
        var first = bytes.AsSpan((int)root + 12, 6).ToArray();
        bytes.AsSpan((int)root + 22, 6).CopyTo(bytes.AsSpan((int)root + 12));
        first.CopyTo(bytes, (int)root + 22);
        SharedFile.WriteAllBytes(cdx, bytes);

        using (var table = TakeOver(dbf, cdx))
            table.Delete(3);
        AssertIndexMatchesTable(dbf, cdx);
    }

    [Fact]
    public void Asking_to_rebuild_an_index_already_kept_rebuilds_it()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        var before = SharedFile.ReadAllBytes(cdx);
        using (var table = OpenWritable(dbf))
            table.MaintainIndex(cdx, rebuild: true);
        Assert.NotEqual(before, SharedFile.ReadAllBytes(cdx)); // rebuilt, not just accepted
        AssertIndexMatchesTable(dbf, cdx, rebuilt: true);
    }

    // A FOR tag holds fewer keys than records anyway, so its count says nothing; the last records'
    // keys show an append that reached the table but not the index.
    [Fact]
    public void A_FOR_tag_missing_the_last_records_key_is_caught()
    {
        var (dbf, cdx) = People();
        PatchTag(cdx, "SALARY", header =>
        {
            var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(510));
            var filter = "!DELETED()\0"u8;
            filter.CopyTo(header.AsSpan(512 + keyLength));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(504), keyLength);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(506), (ushort)filter.Length);
            header[14] |= CdxIndex.OptionForFilter;
        });
        using (TakeOver(dbf, cdx)) { }
        using (var killed = DbfTable.Open(dbf, writable: true, allowUnmaintainedIndexes: true))
            killed.Append(killed.Read(0).Values.ToArray());

        using var table = DbfTable.Open(dbf);
        using var index = CdxIndex.Open(cdx, table: table);
        Assert.Contains($"record {table.RecordCount}", index["SALARY"].MissingLastKeys(table));
    }

    // Found against Visual FoxPro 9: a writer killed between writing a record and its keys left a tag
    // one key short, and the next open took the index as it was, so SEEK couldn't find that record.
    // A short tag is now refused on open, and a takeover with a rebuild repairs it.
    [Fact]
    public void A_record_whose_keys_never_reached_the_index_is_caught_on_open()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        using (var killed = DbfTable.Open(dbf, writable: true, allowUnmaintainedIndexes: true))
            killed.Append(Call(killed.Read(0).Values.ToArray(), id: 777, contact: 1, subject: "unindexed"));

        using (var table = DbfTable.Open(dbf, writable: true, maintainFoxProIndexes: true))
        {
            Assert.False(table.MaintainsIndex);
            var error = Assert.Throws<DbfException>(() => table.Append(table.Read(0).Values.ToArray()));
            Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
            Assert.Contains("keys for", error.Diagnostic);
        }

        using (var table = DbfTable.Open(dbf, writable: true, allowUnmaintainedIndexes: true, maintainFoxProIndexes: true, exclusive: true))
            table.MaintainIndex(cdx, rebuild: true);
        AssertIndexMatchesTable(dbf, cdx, rebuilt: true);
        using var reopened = OpenWritable(dbf);
        Assert.True(reopened.MaintainsIndex);
    }

    // A takeover whose rebuild fails lets the index go again: it keeps blocking writes, and isn't
    // written to in an order these writes can't keep.
    [Fact]
    public void A_takeover_whose_rebuild_fails_lets_the_index_go()
    {
        var (dbf, cdx) = People();
        PatchTag(cdx, "STATE", header => header[14] |= CdxIndex.OptionCandidate); // and many records share a state
        var before = SharedFile.ReadAllBytes(cdx);

        using var table = DbfTable.Open(dbf, writable: true, maintainFoxProIndexes: true, exclusive: true);
        Assert.Equal(DbfErrorCode.InvalidValue, Assert.Throws<DbfException>(() => table.MaintainIndex(cdx, rebuild: true)).Code);
        Assert.False(table.MaintainsIndex);

        var row = table.Read(0).Values.ToArray();
        row[4] = "Q9";
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, Assert.Throws<DbfException>(() => table.Append(row)).Code);
        Assert.Equal(before, SharedFile.ReadAllBytes(cdx));
    }

    // ---- FoxPro 2 or dBASE IV? --------------------------------------------------------------------

    // A dBASE IV table whose .mdx is missing, with an unrelated .cdx that happens to share its name:
    // still dBASE, so the missing .mdx blocks writes and dBASE's lock positions stay.
    [Fact]
    public void A_stray_cdx_doesnt_make_a_dBASE_table_FoxPro()
    {
        foreach (var file in new[] { "CHANGELOG.DBF", "CHANGELOG.DBT" })
            File.Copy(Fixture(Path.Combine("siemens-step7/siprom", file)), Path.Combine(_dir.FullName, file));
        File.Copy(Fixture("infused-dbf/foxprodb/calls.CDX"), Path.Combine(_dir.FullName, "CHANGELOG.CDX"));
        var dbf = Path.Combine(_dir.FullName, "CHANGELOG.DBF");

        using var table = DbfTable.Open(dbf, writable: true, maintainFoxProIndexes: true);
        Assert.True(table.Header.HasProductionMdx);
        Assert.False(table.Header.HasStructuralCdx);
        Assert.Equal(DbfLockScheme.Dbase, table.LockScheme);
        var error = Assert.Throws<DbfException>(() => table.Delete(0));
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
        Assert.Contains(".mdx", error.Message);
    }
}
