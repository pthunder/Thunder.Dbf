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
        var table = DbfTable.Open(_table, writable: true);
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
        var random = new Random(42);
        using (var table = OpenWritable())
        {
            var template = table.Read(0).Values.ToArray();
            for (var step = 0; step < 600; step++)
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

        AssertIndexMatchesTable();
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
        using var table = DbfTable.Open(_table, writable: true);

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
            var now = File.ReadAllBytes(Path.Combine(_dir.FullName, name));
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
