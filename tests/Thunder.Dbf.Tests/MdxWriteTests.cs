//          insert 42                       ProjectThunder :: Thunder.Dbf tests
//              |                           Change the table every way there is, then
//      [ 7 | 40 ] [ 42 | 88 ]              prove every tag still matches it, key for
//          \     /                         key, through splits and new roots.
//          [ 40 ]

namespace Thunder.Dbf.Tests;

public sealed class MdxWriteTests : IDisposable
{
    private static readonly string FixtureRoot = FindFixtureRoot();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-mdx-");
    private readonly string _table;

    public MdxWriteTests()
    {
        foreach (var file in new[] { "CHANGELOG.DBF", "CHANGELOG.DBT", "CHANGELOG.MDX" })
            File.Copy(Path.Combine(FixtureRoot, "siemens-step7/siprom", file), Path.Combine(_dir.FullName, file));
        _table = Path.Combine(_dir.FullName, "CHANGELOG.DBF");
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private DbfTable OpenWritable()
    {
        var table = DbfTable.Open(_table, writable: true, exclusive: true);
        Assert.True(table.MaintainsIndex);
        return table;
    }

    [Fact]
    public void Appends_land_in_every_tag()
    {
        using (var table = OpenWritable())
        {
            var template = table.Read(0).Values.ToArray();
            for (var i = 0; i < 5; i++)
                table.Append(Row(table, template, id: 100 + i, action: i % 3, user: $"user{i}"));
        }

        AssertIndexMatchesTable();
    }

    [Fact]
    public void Thousands_of_appends_split_nodes_and_grow_new_roots()
    {
        using (var table = OpenWritable())
        {
            var template = table.Read(0).Values.ToArray();
            var random = new Random(1997);
            table.AppendRange(Enumerable.Range(0, 3000).Select(i =>
                Row(table, template, id: 1000 + i, action: random.Next(50), user: $"u{random.Next(400):D3}")));
        }

        AssertIndexMatchesTable();
        using var reopened = DbfTable.Open(_table);
        using var index = reopened.OpenProductionIndex()!;
        Assert.Equal(1000m + 1234, reopened.Seek(index["ID_TAG"], 1000m + 1234)!["ID"]);
        Assert.Equal(reopened.RecordCount, index["ID_TAG"].Entries().Count() + DeletedCount(reopened));
    }

    [Fact]
    public void Updates_move_keys_and_unchanged_keys_stay_put()
    {
        using (var table = OpenWritable())
        {
            var values = table.Read(1).Values.ToArray();
            values[Field(table, "USER")] = "zz-renamed";
            values[Field(table, "ACTION")] = 999m;
            table.Update(1, values);
        }

        AssertIndexMatchesTable();
        using var reopened = DbfTable.Open(_table);
        using var index = reopened.OpenProductionIndex()!;
        Assert.Equal(2, reopened.Seek(index["USER_TAG"], "zz-renamed")!.RecordNumber);
    }

    [Fact]
    public void Delete_and_recall_follow_the_FOR_NOT_DELETED_filter()
    {
        using (var table = OpenWritable())
            table.Delete(0);
        AssertIndexMatchesTable();
        using (var check = DbfTable.Open(_table))
        using (var index = check.OpenProductionIndex()!)
            Assert.DoesNotContain(1, index["ID_TAG"].Entries().Select(e => e.RecordNumber));

        using (var table = OpenWritable())
            table.Recall(0);
        AssertIndexMatchesTable();
    }

    [Fact]
    public void Random_changes_keep_every_tag_exact()
    {
        using (var table = OpenWritable())
            Mutate(table, new Random(42), steps: 600);

        AssertIndexMatchesTable();
    }

    // DeferIndexWrites: a batch under LockTable() keeps the .mdx's pages in memory and writes each
    // changed one once, when the lock goes. The file must end exactly as record-by-record writes leave it.
    [Theory]
    [InlineData(16_384)]
    [InlineData(4)] // pages kept only because they were read are let go every few reads
    public void A_deferred_batch_leaves_the_mdx_exactly_as_record_by_record_writes_would(int maxUnchangedPages)
    {
        var plain = Directory.CreateDirectory(Path.Combine(_dir.FullName, "plain")).FullName;
        foreach (var file in Directory.EnumerateFiles(_dir.FullName))
            File.Copy(file, Path.Combine(plain, Path.GetFileName(file)));
        using (var table = DbfTable.Open(Path.Combine(plain, "CHANGELOG.DBF"), writable: true, exclusive: true))
        {
            table.FlushWritesToDisk = false; // forcing writes changes nothing in the bytes compared
            Mutate(table, new Random(7), steps: 2500);
        }

        var before = SharedFile.ReadAllBytes(Path.ChangeExtension(_table, ".MDX"));
        var saved = MdxIndex.MaxUnchangedPages;
        MdxIndex.MaxUnchangedPages = maxUnchangedPages;
        try
        {
            using var table = OpenWritable();
            table.FlushWritesToDisk = false;
            table.DeferIndexWrites = true;
            using (table.LockTable())
            {
                Mutate(table, new Random(7), steps: 2500);
                Assert.Equal(before, SharedFile.ReadAllBytes(Path.ChangeExtension(_table, ".MDX"))); // nothing written yet
            }
            Assert.False(table.HasPendingJournal);
        }
        finally
        {
            MdxIndex.MaxUnchangedPages = saved;
        }

        Assert.Equal(SharedFile.ReadAllBytes(Path.Combine(plain, "CHANGELOG.MDX")), SharedFile.ReadAllBytes(Path.ChangeExtension(_table, ".MDX")));
        AssertIndexMatchesTable();
    }

    // A batch cut off while its pages are going out can leave a tag reaching a page twice (a new root
    // over an old one not yet rewritten). RebuildIndex is the way back, so it must rebuild such a tag
    // rather than refuse to walk it (found by an automated review, gpt-6-astra).
    [Fact]
    public void RebuildIndex_repairs_a_tag_that_reaches_a_page_twice()
    {
        using (var table = OpenWritable())
        {
            var template = table.Read(0).Values.ToArray();
            table.AppendRange(Enumerable.Range(0, 600).Select(i => Row(table, template, id: 6000 + i, action: i % 9, user: $"g{i % 40:D2}")));
        }
        using (var stream = new FileStream(Path.ChangeExtension(_table, ".MDX"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var index = MdxIndex.Open(stream, leaveOpen: true))
        {
            var tag = index["ID_TAG"];
            var root = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(index.ReadBlock(tag.HeaderPage));
            var block = index.ReadBlock(root);
            Assert.True(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(block) >= 2); // an interior root
            block.AsSpan(8, 4).CopyTo(block.AsSpan(8 + tag.ItemLength)); // its second child is its first again
            index.WriteBlock(root, block);
        }

        using (var table = DbfTable.Open(_table, writable: true, exclusive: true, allowUnmaintainedIndexes: true))
            table.RebuildIndex();

        AssertIndexMatchesTable();
    }

    // Blocks allocated and freed inside one batch (tags grown, then emptied by deleting every record,
    // which their FOR .NOT. DELETED() filters drop) must come out as record-by-record writes leave them.
    [Fact]
    public void A_batch_that_grows_then_empties_the_tags_leaves_the_same_bytes()
    {
        static void GrowThenEmpty(DbfTable table)
        {
            table.FlushWritesToDisk = false;
            var template = table.Read(0).Values.ToArray();
            for (var i = 0; i < 400; i++)
                table.Append(Row(table, template, id: 20000 + i, action: i % 11, user: $"e{i % 70:D2}"));
            for (var i = 0; i < table.RecordCount; i++)
                table.Delete(i);
        }
        var plain = Directory.CreateDirectory(Path.Combine(_dir.FullName, "plain")).FullName;
        foreach (var file in Directory.EnumerateFiles(_dir.FullName))
            File.Copy(file, Path.Combine(plain, Path.GetFileName(file)));
        using (var table = DbfTable.Open(Path.Combine(plain, "CHANGELOG.DBF"), writable: true, exclusive: true))
            GrowThenEmpty(table);

        using (var table = OpenWritable())
        {
            table.DeferIndexWrites = true;
            using (table.LockTable())
                GrowThenEmpty(table);
        }

        Assert.Equal(SharedFile.ReadAllBytes(Path.Combine(plain, "CHANGELOG.MDX")), SharedFile.ReadAllBytes(Path.ChangeExtension(_table, ".MDX")));
        AssertIndexMatchesTable();
    }

    // The upgrade note's promise: during a batch, an index opened separately (here in the same process)
    // sees the batch's records with the index as it was before the batch, and the new keys once it ends.
    [Fact]
    public void An_index_opened_during_a_batch_sees_its_keys_only_once_it_ends()
    {
        using var table = OpenWritable();
        table.DeferIndexWrites = true;
        var template = table.Read(0).Values.ToArray();
        using (table.LockTable())
        {
            table.Append(Row(table, template, id: 9999, action: 1, user: "batch"));
            using var during = table.OpenProductionIndex()!;
            Assert.Null(table.Seek(during["ID_TAG"], 9999m));
        }
        using var after = table.OpenProductionIndex()!;
        Assert.Equal(9999m, table.Seek(after["ID_TAG"], 9999m)!["ID"]);
    }

    // What a process killed during a batch leaves: the records, an .mdx without their keys, and the
    // batch's note. The next write refuses until RebuildIndex() puts the index right.
    [Fact]
    public void An_mdx_batch_cut_off_is_refused_until_rebuilt()
    {
        var killed = Directory.CreateDirectory(Path.Combine(_dir.FullName, "killed")).FullName;
        using (var table = DbfTable.Open(_table, writable: true)) // shared, so the files can be copied mid-batch
        {
            table.DeferIndexWrites = true;
            var template = table.Read(0).Values.ToArray();
            using (table.LockTable())
            {
                for (var i = 0; i < 300; i++)
                    table.Append(Row(table, template, id: 7000 + i, action: i % 7, user: "cut"));
                foreach (var file in Directory.EnumerateFiles(_dir.FullName))
                    File.WriteAllBytes(Path.Combine(killed, Path.GetFileName(file)), SharedFile.ReadAllBytes(file));
            }
        }
        AssertIndexMatchesTable(); // the batch that finished is whole

        var cutOff = Path.Combine(killed, "CHANGELOG.DBF");
        var error = Assert.Throws<DbfException>(() =>
        {
            using var reopened = DbfTable.Open(cutOff, writable: true);
            reopened.UpdateFields(0, new Dictionary<string, object?> { ["USER"] = "refused" });
        });
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);

        using (var exclusive = DbfTable.Open(cutOff, writable: true, exclusive: true))
        {
            exclusive.RebuildIndex();
            Assert.False(exclusive.HasPendingJournal);
        }
        using var rebuilt = DbfTable.Open(cutOff);
        using var index = rebuilt.OpenProductionIndex()!;
        Assert.Equal(7299m, rebuilt.Seek(index["ID_TAG"], 7299m)!["ID"]);
    }

    // Creating a tag ends the batch first: its pages go out, then the tag is built from the table.
    [Fact]
    public void Creating_a_tag_during_a_deferred_batch_writes_the_batch_first()
    {
        using (var table = OpenWritable())
        {
            table.DeferIndexWrites = true;
            var template = table.Read(0).Values.ToArray();
            using (table.LockTable())
            {
                for (var i = 0; i < 200; i++)
                    table.Append(Row(table, template, id: 8000 + i, action: i % 5, user: $"b{i % 9}"));
                table.CreateIndexTag("USER2", "user");
                table.Append(Row(table, template, id: 8500, action: 1, user: "after"));
            }
            Assert.False(table.HasPendingJournal);
        }

        AssertIndexMatchesTable();
        using var reopened = DbfTable.Open(_table);
        using var index = reopened.OpenProductionIndex()!;
        Assert.Equal(reopened.RecordCount - DeletedCount(reopened), index["USER2"].Entries().Count());
    }

    private static void Mutate(DbfTable table, Random random, int steps)
    {
        var template = table.Read(0).Values.ToArray();
        for (var step = 0; step < steps; step++)
        {
            var pick = random.Next(table.RecordCount);
            switch (random.Next(5))
            {
                case 0 or 1:
                    table.Append(Row(table, template, id: 5000 + step, action: random.Next(20), user: $"r{random.Next(60):D2}"));
                    break;
                case 2:
                    var values = table.Read(pick).Values.ToArray();
                    values[Field(table, "LEVEL")] = (decimal)random.Next(-5, 5);
                    values[Field(table, "USER")] = $"r{random.Next(60):D2}";
                    table.Update(pick, values);
                    break;
                case 3:
                    table.Delete(pick);
                    break;
                default:
                    table.Recall(pick);
                    break;
            }
        }
    }

    [Fact]
    public void Unique_tags_keep_one_record_per_key_and_promote_the_next()
    {
        using (var table = OpenWritable())
        {
            var template = table.Read(0).Values.ToArray();
            table.Append(Row(table, template, id: 3, action: 1, user: "dup")); // ID 3 already exists (record 3)
            table.Delete(2);                                                   // record 3 leaves the unique ID tag
        }

        using var reopened = DbfTable.Open(_table);
        using var index = reopened.OpenProductionIndex()!;
        var threes = index["ID_TAG"].SeekExact(3m).Select(e => e.RecordNumber).ToList();
        Assert.Equal([reopened.RecordCount], threes); // the appended duplicate took over
    }

    [Fact]
    public void Rebuild_produces_the_same_index_as_maintenance()
    {
        using (var table = OpenWritable())
        {
            var template = table.Read(0).Values.ToArray();
            table.AppendRange(Enumerable.Range(0, 400).Select(i => Row(table, template, id: 700 + i, action: i % 7, user: $"b{i % 13}")));
        }
        var maintained = Snapshot();

        using (var table = OpenWritable())
            table.RebuildIndex();

        Assert.Equal(maintained, Snapshot());
        AssertIndexMatchesTable();
    }

    [Fact]
    public void Writes_without_the_index_are_still_refused_unless_allowed()
    {
        File.Move(Path.Combine(_dir.FullName, "CHANGELOG.MDX"), Path.Combine(_dir.FullName, "moved.mdx"));
        using var table = DbfTable.Open(_table, writable: true, exclusive: true);

        Assert.False(table.MaintainsIndex);
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, Assert.Throws<DbfException>(() => table.Delete(0)).Code);
    }

    [Fact]
    public void A_bad_value_leaves_table_and_index_untouched()
    {
        var before = Directory.EnumerateFiles(_dir.FullName).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
        using (var table = OpenWritable())
        {
            var values = table.Read(0).Values.ToArray();
            values[Field(table, "USER")] = "changed";       // a valid change to an indexed field...
            values[Field(table, "ID")] = "not a number";    // ...alongside one that can't be stored
            Assert.ThrowsAny<Exception>(() => table.Update(0, values));
        }

        foreach (var (name, bytes) in before)
        {
            var now = SharedFile.ReadAllBytes(Path.Combine(_dir.FullName, name));
            Assert.True(bytes.AsSpan(4).SequenceEqual(now.AsSpan(4)), $"{name} changed"); // header dates may differ
        }
    }

    // ---- the oracle -------------------------------------------------------------------------

    // Every tag must hold exactly the keys its expression gives for the records its FOR filter
    // admits (one per key for unique tags), in order.
    private void AssertIndexMatchesTable()
    {
        using var table = DbfTable.Open(_table);
        using var index = table.OpenProductionIndex()!;
        var records = table.ReadAll().ToList();

        foreach (var tag in index.Tags)
        {
            var key = DbfExpression.Parse(tag.Expression);
            var filter = tag.ForExpression is { } f ? DbfExpression.Parse(f) : null;
            var expected = records.Where(r => filter is null || filter.Evaluate(r) is true)
                .Select(r => (Record: r.RecordNumber, Key: Normalize(tag, key.Evaluate(r))))
                .ToList();
            var actual = tag.Entries().ToList();

            for (var i = 1; i < actual.Count; i++)
                Assert.True(Compare(tag, actual[i - 1].Key, actual[i].Key) <= 0, $"{tag.Name} out of order at {i}");

            if (tag.IsUnique)
            {
                Assert.Equal(expected.Select(e => e.Key).Distinct().OrderBy(k => k).ToList(), actual.Select(e => e.Key).OrderBy(k => k).ToList());
                var byRecord = expected.ToDictionary(e => e.Record, e => e.Key);
                Assert.All(actual, e => Assert.Equal(byRecord[e.RecordNumber], e.Key));
            }
            else
            {
                Assert.Equal(expected.OrderBy(e => e.Record).ToList(), actual.Select(e => (Record: e.RecordNumber, Key: e.Key)).OrderBy(e => e.Record).ToList());
            }
        }
    }

    private static object? Normalize(MdxTag tag, object? value) => tag.KeyType switch
    {
        'C' => ((string)value!).Length > tag.KeyLength ? ((string)value!)[..tag.KeyLength].TrimEnd(' ') : ((string)value!).TrimEnd(' '),
        'N' => value is decimal d ? d : 0m,
        _ => value,
    };

    private static int Compare(MdxTag tag, object? a, object? b) => tag.KeyType switch
    {
        'C' => string.CompareOrdinal((string?)a, (string?)b),
        'N' => ((decimal)a!).CompareTo((decimal)b!),
        _ => Nullable.Compare((DateOnly?)a, (DateOnly?)b),
    };

    // Equal keys may sit in any order (dBASE doesn't define one), so compare each tag's entries sorted.
    private List<(string, int, object?)> Snapshot()
    {
        using var table = DbfTable.Open(_table);
        using var index = table.OpenProductionIndex()!;
        return index.Tags
            .SelectMany(t => t.Entries().Select(e => (t.Name, e.RecordNumber, e.Key)))
            .OrderBy(e => e.Name).ThenBy(e => e.RecordNumber)
            .ToList();
    }

    private static object?[] Row(DbfTable table, object?[] template, int id, int action, string user)
    {
        var values = (object?[])template.Clone();
        values[Field(table, "ID")] = (decimal)id;
        values[Field(table, "ACTION")] = (decimal)action;
        values[Field(table, "USER")] = user;
        return values;
    }

    private static int Field(DbfTable table, string name) =>
        table.Fields.Select((f, i) => (f, i)).Single(x => x.f.Name == name).i;

    private static int DeletedCount(DbfTable table) => table.ReadAll().Count(r => r.IsDeleted);

    private static string FindFixtureRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("Couldn't find tests/fixtures above the test output folder.");
    }
}
