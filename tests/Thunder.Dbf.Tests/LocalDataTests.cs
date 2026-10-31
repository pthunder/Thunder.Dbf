//      _____________                   ProjectThunder :: Thunder.Dbf tests
//     | your data   |                  Bring your own legacy folder. Point THUNDER_TEST_DATA
//     |  goes here  |                  at a copy of it and these tests check every table and
//     |_____________|                  every index tag in it, then write to a scratch copy and
//                                      check again. Without the variable they do nothing, so
//   export THUNDER_TEST_DATA=~/data    no private data ever needs to live in the repo.

namespace Thunder.Dbf.Tests;

public sealed class LocalDataTests
{
    private static readonly string? Folder = Environment.GetEnvironmentVariable("THUNDER_TEST_DATA");

    [Fact]
    public void Every_table_reads_and_every_index_tag_matches_its_table()
    {
        if (Folder is null)
            return;

        using var db = DbfDatabase.Open(Folder);
        var report = db.Check();
        Assert.True(report.Ok, report.ToString());

        foreach (var name in db.TableNames)
        {
            using var table = db.OpenTable(name);
            Assert.Equal(table.RecordCount, table.ReadAll().Count());
            IndexOracle.AssertMatches(table);
        }
    }

    [Fact]
    public void Writes_to_a_copy_keep_every_index_exact()
    {
        if (Folder is null)
            return;

        var copy = Directory.CreateTempSubdirectory("thunder-local-");
        try
        {
            foreach (var file in Directory.EnumerateFiles(Folder))
                File.Copy(file, Path.Combine(copy.FullName, Path.GetFileName(file)));

            using var db = DbfDatabase.Open(copy.FullName, new DbfDatabaseOptions { ReadOnly = false, Exclusive = true });
            var random = new Random(2002);
            foreach (var name in db.TableNames)
            {
                using var table = db.OpenTable(name);
                if (table.RecordCount == 0)
                    continue;

                for (var step = 0; step < 40; step++)
                {
                    var pick = random.Next(table.RecordCount);
                    switch (random.Next(4))
                    {
                        case 0:
                            table.Append([.. table.Read(pick).Values]);
                            break;
                        case 1:
                            table.Delete(pick);
                            break;
                        case 2:
                            table.Recall(pick);
                            break;
                        default:
                            table.Update(pick, Shuffled(table, table.Read(pick), random));
                            break;
                    }
                }
                IndexOracle.AssertMatches(table);

                if (table.MaintainsIndex)
                {
                    table.RebuildIndex();
                    IndexOracle.AssertMatches(table);
                }
            }
        }
        finally
        {
            copy.Delete(recursive: true);
        }
    }

    // Changes one character field to a value another record has, so keys move between records.
    private static object?[] Shuffled(DbfTable table, DbfRecord record, Random random)
    {
        var values = record.Values.ToArray();
        var text = table.Fields.Select((f, i) => (f, i)).Where(x => x.f.Type == 'C').ToList();
        if (text.Count == 0)
            return values;
        var (_, column) = text[random.Next(text.Count)];
        values[column] = table.Read(random.Next(table.RecordCount)).Values[column];
        return values;
    }
}

/// <summary>Checks a table's production index against keys rebuilt from the table itself.</summary>
internal static class IndexOracle
{
    public static void AssertMatches(DbfTable table)
    {
        using var index = table.OpenProductionIndex();
        if (index is null)
            return;

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
                Assert.True(Compare(tag, actual[i - 1].Key, actual[i].Key) * (tag.IsDescending ? -1 : 1) <= 0,
                    $"{table.Header.Version:X2} tag {tag.Name} out of order at entry {i}");

            if (tag.IsUnique)
            {
                var byRecord = expected.ToDictionary(e => e.Record, e => e.Key);
                Assert.Equal(expected.Select(e => e.Key).Distinct().Count(), actual.Count);
                Assert.All(actual, e => Assert.Equal(byRecord[e.RecordNumber], e.Key));
            }
            else
            {
                Assert.Equal(expected.OrderBy(e => e.Record).ToList(),
                    actual.Select(e => (Record: e.RecordNumber, Key: e.Key)).OrderBy(e => e.Record).ToList());
            }
        }
    }

    private static object? Normalize(MdxTag tag, object? value) => tag.KeyType switch
    {
        'C' => Fit((string)value!, tag.KeyLength),
        'N' => value is decimal or double ? value : 0m,
        _ => value,
    };

    private static string Fit(string s, int length) => (s.Length > length ? s[..length] : s).TrimEnd(' ', '\0');

    private static int Compare(MdxTag tag, object? a, object? b) => tag.KeyType switch
    {
        'C' => string.CompareOrdinal((string?)a, (string?)b),
        'N' => Convert.ToDouble(a).CompareTo(Convert.ToDouble(b)),
        _ => Nullable.Compare((DateOnly?)a, (DateOnly?)b),
    };
}
