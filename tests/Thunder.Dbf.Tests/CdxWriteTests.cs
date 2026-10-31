//      [ tag directory ]                   ProjectThunder :: Thunder.Dbf tests
//       /      |      \                    Change FoxPro and X# tables every way there
//   [CALL_ID] [AGE] [STATE]  <- insert     is, then hold every .cdx tag to a rebuild from
//      |   \                               the table, byte for byte: leaves FoxPro wrote
//   [..|..] [..|..]  split, new root       must re-encode to exactly what FoxPro wrote.

using System.Buffers.Binary;
using static Thunder.Dbf.Tests.IndexTestSupport;

namespace Thunder.Dbf.Tests;

public sealed class CdxWriteTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-cdx-");

    public void Dispose() => _dir.Delete(recursive: true);

    public static TheoryData<string, string> RealIndexes() => new()
    {
        { "infused-dbf/foxprodb/calls.CDX", "infused-dbf/foxprodb/calls.dbf" },
        { "infused-dbf/foxprodb/contacts.CDX", "infused-dbf/foxprodb/contacts.dbf" },
        { "infused-dbf/foxprodb/setup.CDX", "infused-dbf/foxprodb/setup.dbf" },
        { "infused-dbf/foxprodb/types.CDX", "infused-dbf/foxprodb/types.dbf" },
        { "xsharp/rdd-tests/TEST10K.CDX", "xsharp/rdd-tests/TEST10K.DBF" },
    };

    // ---- fidelity with what FoxPro and X# wrote -----------------------------------------------

    // The strongest check there is: decode every leaf FoxPro (or X#) wrote and pack it again with
    // freshly chosen bit widths. Everything but the free gap between entries and key bytes must come
    // out identical; FoxPro leaves stale bytes in that gap, and this writer zeroes it.
    [Theory]
    [MemberData(nameof(RealIndexes))]
    public void Untouched_leaves_reencode_to_the_bytes_FoxPro_wrote(string indexFixture, string tableFixture)
    {
        var file = File.ReadAllBytes(Fixture(indexFixture));
        using var table = DbfTable.Open(Fixture(tableFixture));
        using var index = CdxIndex.Open(Fixture(indexFixture), table: table);

        var leaves = 0;
        foreach (var tag in index.Tags.Where(t => Evaluates(t, table)).Prepend(CdxTag.LoadDirectory(index)))
        {
            foreach (var leaf in tag.LeafOffsets())
            {
                AssertSameBesidesTheGap(file.AsSpan((int)leaf, CdxIndex.DefaultPageLength), tag.ReencodeLeaf(leaf), $"{tag.Name} leaf at {leaf}");
                leaves++;
            }
        }
        Assert.NotEqual(0, leaves);
    }

    // The fixtures with tags over the table's own field names (the others use a database container's long names).
    public static TheoryData<string, string> EvaluableIndexes() => new()
    {
        { "infused-dbf/foxprodb/calls.CDX", "infused-dbf/foxprodb/calls.dbf" },
        { "infused-dbf/foxprodb/setup.CDX", "infused-dbf/foxprodb/setup.dbf" },
        { "xsharp/rdd-tests/TEST10K.CDX", "xsharp/rdd-tests/TEST10K.DBF" },
    };

    // Every key FoxPro and X# stored is the key built here for the same record, so maintained keys
    // sort among them exactly as FoxPro's own would.
    [Theory]
    [MemberData(nameof(EvaluableIndexes))]
    public void Keys_built_here_are_the_bytes_FoxPro_stored(string indexFixture, string tableFixture)
    {
        using var table = DbfTable.Open(Fixture(tableFixture));
        using var index = CdxIndex.Open(Fixture(indexFixture), table: table);
        var checkedTags = 0;
        foreach (var tag in index.Tags.Where(t => Evaluates(t, table)))
        {
            // X# sorted these character tags without regard to case; their keys are still byte-exact.
            var byteOrdered = !(indexFixture.Contains("TEST10K") && tag.Name is "CITY" or "FIRST" or "LAST");
            AssertExactKeys(tag, table, byteOrdered);
            checkedTags++;
        }
        Assert.True(checkedTags > 0);
    }

    // ---- Visual FoxPro: integer keys, a candidate key, splits and new roots ------------------

    [Fact]
    public void Thousands_of_appends_split_leaves_and_interior_pages_and_grow_new_roots()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        var before = TagDirectory(cdx);
        using (var table = OpenWritable(dbf))
        {
            Assert.Equal(1, Tag(cdx, "CALL_ID").Height());
            var random = new Random(1997);
            var template = table.Read(0).Values.ToArray();
            table.AppendRange(Enumerable.Range(0, 7000).Select(i => Call(template, id: 17 + i, contact: random.Next(1, 40), subject: $"s{i}")));
        }

        AssertIndexMatchesTable(dbf, cdx);
        Assert.Equal(3, Tag(cdx, "CALL_ID").Height()); // the root leaf split, then the root above it
        Assert.True(Tag(cdx, "CONTACT_ID").Height() >= 2);
        Assert.Equal(before, TagDirectory(cdx));

        using var reopened = DbfTable.Open(dbf);
        using var index = CdxIndex.Open(cdx, table: reopened);
        var hit = Assert.Single(index["CALL_ID"].SeekExact(17 + 4321));
        Assert.Equal(17 + 4321, reopened.Read(hit.RecordNumber - 1)["CALL_ID"]);
        Assert.Equal(reopened.ReadAll().Count(r => (int)r["CONTACT_ID"]! == 7), index["CONTACT_ID"].SeekExact(7).Count());
    }

    [Fact]
    public void Updates_deletes_and_recalls_keep_every_tag_exact()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        var random = new Random(42);
        using (var table = OpenWritable(dbf))
        {
            var template = table.Read(0).Values.ToArray();
            table.AppendRange(Enumerable.Range(0, 900).Select(i => Call(template, id: 100 + i, contact: random.Next(1, 9), subject: "x")));
            for (var step = 0; step < 500; step++)
            {
                var pick = random.Next(table.RecordCount);
                switch (random.Next(4))
                {
                    case 0:
                        table.UpdateFields(pick, new Dictionary<string, object?> { ["CONTACT_ID"] = random.Next(1, 30) });
                        break;
                    case 1:
                        // A new candidate key no other record holds.
                        table.UpdateFields(pick, new Dictionary<string, object?> { ["CALL_ID"] = 50_000 + step });
                        break;
                    case 2:
                        table.Delete(pick);
                        break;
                    default:
                        table.Recall(pick);
                        break;
                }
            }
        }

        AssertIndexMatchesTable(dbf, cdx);
    }

    [Fact]
    public void A_candidate_key_refuses_a_duplicate_before_the_table_changes()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        var files = Snapshot();
        using (var table = OpenWritable(dbf))
        {
            Assert.True(Tag(cdx, "CALL_ID").IsCandidate);
            var template = table.Read(0).Values.ToArray();
            var error = Assert.Throws<DbfException>(() => table.Append(Call(template, id: 3, contact: 1, subject: "dup")));
            Assert.Equal(DbfErrorCode.InvalidValue, error.Code);
            Assert.Contains("CALL_ID", error.Message);

            error = Assert.Throws<DbfException>(() => table.UpdateFields(0, new Dictionary<string, object?> { ["CALL_ID"] = 5 }));
            Assert.Equal(DbfErrorCode.InvalidValue, error.Code);
            Assert.Equal(16, table.RecordCount);

            // Keeping its own key, or changing other fields, is fine.
            table.UpdateFields(4, new Dictionary<string, object?> { ["SUBJECT"] = "still unique" });
        }
        Assert.Equal(files.Where(f => !f.Key.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase)), Snapshot().Where(f => !f.Key.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase)));
        AssertIndexMatchesTable(dbf, cdx);
    }

    [Fact]
    public void Character_candidate_keys_split_and_seek()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "setup");
        var random = new Random(7);
        var names = Enumerable.Range(0, 600).Select(i => $"KEY_{random.Next(1_000_000):D7}_{i}").ToList();
        using (var table = OpenWritable(dbf))
        {
            table.AppendRange(names.Select((n, i) => new object?[] { n, i }));
            for (var i = 0; i < 200; i += 3)
                table.UpdateFields(3 + i, new Dictionary<string, object?> { ["KEY_NAME"] = $"RENAMED {i:D4}" });
        }

        AssertIndexMatchesTable(dbf, cdx);
        using var reopened = DbfTable.Open(dbf);
        using var index = CdxIndex.Open(cdx, table: reopened);
        Assert.Equal(["RENAMED 0003"], index["KEY_NAME"].SeekExact("RENAMED 0003").Select(e => (string)e.Key!));
        Assert.Equal(names[500], reopened.Read(index["KEY_NAME"].SeekExact(names[500]).Single().RecordNumber - 1)["KEY_NAME"]);
    }

    // ---- X#'s 10,000-record table: rebuild, then descending, unique and FOR tags -------------

    [Fact]
    public void Rebuilding_gives_the_same_keys_as_maintaining()
    {
        var (dbf, cdx) = People();
        using (var table = OpenWritable(dbf))
        {
            table.RebuildIndex(); // X# sorted three tags by a case-blind collation; this puts them in byte order
            Mutate(table, new Random(11), steps: 500);
        }
        AssertIndexMatchesTable(dbf, cdx);
        var maintained = AllEntries(cdx, dbf);

        using (var table = OpenWritable(dbf))
            table.RebuildIndex();

        Assert.Equal(maintained, AllEntries(cdx, dbf));
        AssertIndexMatchesTable(dbf, cdx);
    }

    [Fact]
    public void Descending_unique_and_FOR_NOT_DELETED_tags_behave_like_FoxPro()
    {
        var (dbf, cdx) = People();
        PatchTag(cdx, "AGE", header => BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(502), 1));
        PatchTag(cdx, "STATE", header => header[14] |= CdxIndex.OptionUnique);
        PatchTag(cdx, "SALARY", header =>
        {
            // The FOR expression goes in the pool right after the key expression's NUL, as FoxPro puts it.
            var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(510));
            var filter = "!DELETED()\0"u8;
            filter.CopyTo(header.AsSpan(512 + keyLength));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(504), keyLength);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(506), (ushort)filter.Length);
            header[14] |= CdxIndex.OptionForFilter;
        });

        using (var table = OpenWritable(dbf))
        {
            table.RebuildIndex();
            for (var i = 0; i < 150; i++)
                table.Delete(i * 7);
            table.Recall(14);
            Mutate(table, new Random(5), steps: 300);
        }

        AssertIndexMatchesTable(dbf, cdx);
        using var reopened = DbfTable.Open(dbf);
        using var index = CdxIndex.Open(cdx, table: reopened);
        Assert.True(index["AGE"].IsDescending);
        Assert.Equal(reopened.ReadAll().Max(r => (decimal)r["AGE"]!), index["AGE"].Entries().First().Key);
        Assert.Equal(reopened.ReadAll().Select(r => (string)r["STATE"]!).Distinct().Count(), index["STATE"].Entries().Count());
        Assert.Equal(reopened.ReadAll().Count(r => !r.IsDeleted), index["SALARY"].Entries().Count());
        Assert.DoesNotContain(1, index["SALARY"].Entries().Select(e => e.RecordNumber));   // deleted
        Assert.Contains(15, index["SALARY"].Entries().Select(e => e.RecordNumber));        // recalled
    }

    [Fact]
    public void Emptied_pages_go_on_the_free_list_and_out_of_every_tree()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        int leavesBefore;
        using (var table = OpenWritable(dbf))
        {
            // Sequential IDs pack full leaves; moving the lowest 400 IDs past the highest empties
            // the leftmost leaves, which must leave the tree and its sibling chain.
            var template = table.Read(0).Values.ToArray();
            table.AppendRange(Enumerable.Range(0, 1000).Select(i => Call(template, id: 17 + i, contact: 1, subject: "")));
            leavesBefore = Tag(cdx, "CALL_ID").LeafOffsets().Count;
            for (var i = 0; i < 400; i++)
                table.UpdateFields(i, new Dictionary<string, object?> { ["CALL_ID"] = 100_000 + i });
        }

        AssertIndexMatchesTable(dbf, cdx);
        var bytes = File.ReadAllBytes(cdx);
        var free = new List<long>();
        for (var page = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)); page is not 0 and not uint.MaxValue; page = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)page)))
        {
            Assert.True(page % 512 == 0 && page < bytes.Length && free.Count < bytes.Length / 512);
            free.Add(page);
        }
        Assert.NotEmpty(free);

        using var table2 = DbfTable.Open(dbf);
        using var index = CdxIndex.Open(cdx, table: table2);
        Assert.True(leavesBefore >= 6, $"only {leavesBefore} leaves");
        Assert.Empty(index.Tags.SelectMany(t => t.Pages()).Intersect(free));
    }

    // ---- refusals and damage -----------------------------------------------------------------

    [Fact]
    public void A_tag_sorted_by_another_collation_keeps_writes_blocked()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "setup");
        PatchTag(cdx, "KEY_NAME", header => "GENERAL"u8.CopyTo(header.AsSpan(92)));
        var files = Snapshot();

        using (var table = DbfTable.Open(dbf, writable: true))
        {
            Assert.False(table.MaintainsIndex);
            var error = Assert.Throws<DbfException>(() => table.Append("NEW", 1));
            Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
        }
        Assert.Equal(files, Snapshot());

        using var stream = new FileStream(cdx, FileMode.Open, FileAccess.ReadWrite);
        using var reader = DbfTable.Open(dbf);
        using var index = CdxIndex.Open(stream, table: reader);
        Assert.Contains("GENERAL", index.MaintainRefusal);
    }

    // The handshake promises exactly what writes do: a maintainable .cdx allows writes, a refused one doesn't.
    [Fact]
    public void The_check_report_agrees_with_what_writes_do()
    {
        Copy("infused-dbf/foxprodb", "calls");
        Copy("infused-dbf/foxprodb", "contacts");
        using var db = DbfDatabase.Open(_dir.FullName, new DbfDatabaseOptions { ReadOnly = false });
        var report = db.Check().Tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        Assert.True(report["calls"].CanWrite);
        Assert.False(report["contacts"].CanWrite);
        Assert.Contains(report["contacts"].Warnings, w => w.Contains("TYPE_ID"));
    }

    // Visual FoxPro tables in a database container index by long field names the .dbf doesn't hold.
    [Fact]
    public void Keys_over_long_field_names_keep_writes_blocked()
    {
        var (dbf, _) = Copy("infused-dbf/foxprodb", "contacts");
        using var table = DbfTable.Open(dbf, writable: true);
        Assert.False(table.MaintainsIndex);
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, Assert.Throws<DbfException>(() => table.Delete(0)).Code);
    }

    [Fact]
    public void Damaged_trees_are_reported_not_followed()
    {
        var (dbf, cdx) = People();
        using var table = DbfTable.Open(dbf);
        var bytes = File.ReadAllBytes(cdx);
        var (rootOffset, tagOffset) = (0, 0);
        using (var index = CdxIndex.Open(cdx, table: table))
        {
            tagOffset = (int)index["STATE"].HeaderOffset;
            rootOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(tagOffset));
        }

        // The root's first and last children point back at the root.
        var cyclic = (byte[])bytes.Clone();
        var rootKeys = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(rootOffset + 2));
        BinaryPrimitives.WriteUInt32BigEndian(cyclic.AsSpan(rootOffset + 12 + 2 + 4), (uint)rootOffset);
        BinaryPrimitives.WriteUInt32BigEndian(cyclic.AsSpan(rootOffset + 12 + (rootKeys - 1) * 10 + 2 + 4), (uint)rootOffset);
        AssertDamaged(cyclic, table, tag => tag.Insert("ZZ"u8.ToArray(), 1));
        AssertDamaged(cyclic, table, tag => tag.Remove("AA"u8.ToArray(), 1));
        AssertDamaged(cyclic, table, tag => tag.Clear());

        // A leaf whose entries claim more bits than they hold.
        var leaf = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(rootOffset + 12 + 2 + 4));
        while (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(leaf)) is var attributes && (attributes & 2) == 0)
            leaf = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(leaf + 12 + 2 + 4));
        var badBits = (byte[])bytes.Clone();
        badBits[leaf + 20] = 60;
        AssertDamaged(badBits, table, tag => tag.Insert("AA"u8.ToArray(), 1));

        // A child pointer past the end of the file.
        var outside = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(outside.AsSpan(rootOffset + 12 + 2 + 4), 0x7FFF_FE00);
        AssertDamaged(outside, table, tag => tag.Insert("AA"u8.ToArray(), 1));
    }

    private static void AssertDamaged(byte[] file, DbfTable table, Action<CdxTag> change)
    {
        using var index = CdxIndex.Open(new MemoryStream(file, writable: true), table: table);
        Assert.Throws<InvalidDataException>(() => change(index["STATE"]));
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static bool Evaluates(CdxTag tag, DbfTable table)
    {
        try
        {
            DbfExpression.Parse(tag.Expression).Evaluate(table.Read(0));
            return true;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private static void AssertSameBesidesTheGap(ReadOnlySpan<byte> original, byte[] reencoded, string where)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(original[2..]);
        var gapStart = 24 + count * original[23];
        var gapEnd = gapStart + BinaryPrimitives.ReadUInt16LittleEndian(original[12..]);
        Assert.True(original[..gapStart].SequenceEqual(reencoded.AsSpan(0, gapStart)), $"{where}: header or entries differ");
        Assert.True(original[gapEnd..].SequenceEqual(reencoded.AsSpan(gapEnd)), $"{where}: key bytes differ");
        Assert.True(reencoded.AsSpan(gapStart, gapEnd - gapStart).IndexOfAnyExcept((byte)0) < 0, $"{where}: the gap isn't zeroed");
    }

    /// <summary>
    /// The tag holds exactly the keys a rebuild from the table would: every record its FOR filter
    /// lets in (one per key for unique tags), with the key built from the record, in byte order
    /// then record order.
    /// </summary>
    private static void AssertExactKeys(CdxTag tag, DbfTable table, bool byteOrdered = true)
    {
        var expected = new List<(byte[] Key, int Record)>();
        var seen = new HashSet<string>();
        foreach (var record in table.ReadAll())
        {
            if (!tag.Includes(record))
                continue;
            var key = tag.KeyBytes(record);
            if (!tag.IsUnique || seen.Add(Convert.ToHexString(key)))
                expected.Add((key, record.RecordNumber));
        }
        expected.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key) is var order and not 0 ? order : a.Record.CompareTo(b.Record));

        var actual = tag.RawEntries();
        if (!byteOrdered)
        {
            actual = [.. actual.OrderBy(e => e.Record)];
            expected = [.. expected.OrderBy(e => e.Record)];
        }
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i].Record == actual[i].Record && expected[i].Key.AsSpan().SequenceEqual(actual[i].Key),
                $"Tag {tag.Name}, entry {i}: expected record {expected[i].Record} key {Convert.ToHexString(expected[i].Key)}, found record {actual[i].Record} key {Convert.ToHexString(actual[i].Key)}");
        }
    }

    // Everything a maintained .cdx must satisfy: the reader reads every tag back in order and in step
    // with the table; the raw keys equal a rebuild; leaves link to their neighbours both ways; and
    // every leaf is packed exactly as FoxPro would pack it.
    private static void AssertIndexMatchesTable(string dbf, string cdx)
    {
        using var table = DbfTable.Open(dbf);
        using var index = CdxIndex.Open(cdx, table: table);
        var file = File.ReadAllBytes(cdx);
        Assert.Equal(0, file.Length % 512);
        foreach (var tag in index.Tags)
        {
            AssertTagMatchesTable(TagView.Of(tag), table);
            AssertExactKeys(tag, table);

            var leaves = tag.LeafOffsets();
            for (var i = 0; i < leaves.Count; i++)
            {
                var page = file.AsSpan((int)leaves[i], 512);
                Assert.Equal(i == 0 ? uint.MaxValue : (uint)leaves[i - 1], BinaryPrimitives.ReadUInt32LittleEndian(page[4..]));
                Assert.Equal(i == leaves.Count - 1 ? uint.MaxValue : (uint)leaves[i + 1], BinaryPrimitives.ReadUInt32LittleEndian(page[8..]));
                AssertSameBesidesTheGap(page, tag.ReencodeLeaf(leaves[i]), $"{tag.Name} leaf at {leaves[i]}");
            }

            foreach (var entry in tag.Entries().Where((_, n) => n % 97 == 0).Take(60))
                Assert.Contains(entry.RecordNumber, tag.SeekExact(entry.Key).Select(e => e.RecordNumber));
        }
    }

    private static List<(string Tag, string Key, int Record)> AllEntries(string cdx, string dbf)
    {
        using var table = DbfTable.Open(dbf);
        using var index = CdxIndex.Open(cdx, table: table);
        return index.Tags.SelectMany(t => t.RawEntries().Select(e => (t.Name, Convert.ToHexString(e.Key), e.Record))).ToList();
    }

    // The tag directory: names, header offsets and every header's expression fields.
    private static List<string> TagDirectory(string cdx)
    {
        using var index = CdxIndex.Open(cdx);
        var bytes = File.ReadAllBytes(cdx);
        var names = CdxTag.LoadDirectory(index).Entries().Select(e => $"{e.Key}@{e.RecordNumber}").ToList();
        return [.. names, .. index.Tags.Select(t => $"{t.Name}:{t.Expression}:{t.ForExpression}:{Convert.ToHexString(bytes, (int)t.HeaderOffset + 12, 4)}")];
    }

    private static CdxTag Tag(string cdx, string name)
    {
        var index = CdxIndex.Open(new MemoryStream(File.ReadAllBytes(cdx)));
        return index[name];
    }

    private DbfTable OpenWritable(string dbf)
    {
        // The renamed X# index isn't named after its table, so it's named here, as USE ... INDEX would.
        var table = IndexedTables.OpenWithIndexes(dbf);
        Assert.True(table.MaintainsIndex);
        return table;
    }

    private (string Dbf, string Cdx) Copy(string folder, string name)
    {
        foreach (var file in Directory.EnumerateFiles(Fixture(folder), name + ".*"))
            File.Copy(file, Path.Combine(_dir.FullName, Path.GetFileName(file)));
        return (Path.Combine(_dir.FullName, name + ".dbf"), Path.Combine(_dir.FullName, name + ".CDX"));
    }

    // X#'s table, with the header's index flag cleared (in a dBASE III header it would mean a
    // production .mdx) and its .cdx under another name, so the table picks it up by its expressions.
    private (string Dbf, string Cdx) People()
    {
        var dbf = Path.Combine(_dir.FullName, "people.dbf");
        var cdx = Path.Combine(_dir.FullName, "tags.cdx");
        var table = File.ReadAllBytes(Fixture("xsharp/rdd-tests/TEST10K.DBF"));
        table[28] = 0;
        File.WriteAllBytes(dbf, table);
        File.Copy(Fixture("xsharp/rdd-tests/TEST10K.CDX"), cdx);
        return (dbf, cdx);
    }

    private static void PatchTag(string cdx, string tagName, Action<byte[]> patch)
    {
        long offset;
        using (var index = CdxIndex.Open(cdx))
            offset = index[tagName].HeaderOffset;
        var bytes = File.ReadAllBytes(cdx);
        var header = bytes.AsSpan((int)offset, CdxIndex.HeaderLength).ToArray();
        patch(header);
        header.CopyTo(bytes, (int)offset);
        File.WriteAllBytes(cdx, bytes);
    }

    private static void Mutate(DbfTable table, Random random, int steps)
    {
        string[] states = ["AK", "CA", "IL", "NY", "TX", "WA", "ZZ", "aa"];
        var template = table.Read(0).Values.ToArray();
        for (var step = 0; step < steps; step++)
        {
            var pick = random.Next(table.RecordCount);
            switch (random.Next(6))
            {
                case 0 or 1:
                    var row = (object?[])template.Clone();
                    row[0] = $"First{random.Next(5000)}";
                    row[1] = $"Last{random.Next(5000)}";
                    row[3] = $"City {random.Next(300)}";
                    row[4] = states[random.Next(states.Length)];
                    row[6] = new DateOnly(1950 + random.Next(70), 1 + random.Next(12), 1 + random.Next(28));
                    row[8] = (decimal)random.Next(18, 99);
                    row[9] = random.Next(-9999, 100_000) / 100m;
                    table.Append(row);
                    break;
                case 2:
                    table.UpdateFields(pick, new Dictionary<string, object?>
                    {
                        ["STATE"] = states[random.Next(states.Length)],
                        ["SALARY"] = random.Next(-9999, 100_000) / 100m,
                        ["HIREDATE"] = random.Next(10) == 0 ? null : new DateOnly(1950 + random.Next(70), 6, 15),
                    });
                    break;
                case 3:
                    table.UpdateFields(pick, new Dictionary<string, object?> { ["LAST"] = $"Zed{random.Next(100)}", ["AGE"] = (decimal)random.Next(18, 99) });
                    break;
                case 4:
                    table.Delete(pick);
                    break;
                default:
                    table.Recall(pick);
                    break;
            }
        }
    }

    private static object?[] Call(object?[] template, int id, int contact, string subject)
    {
        var row = (object?[])template.Clone();
        row[0] = id;
        row[1] = contact;
        row[4] = subject;
        row[5] = null;
        return row;
    }

    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(_dir.FullName).ToDictionary(p => Path.GetFileName(p), p => Convert.ToHexString(File.ReadAllBytes(p)));
}
