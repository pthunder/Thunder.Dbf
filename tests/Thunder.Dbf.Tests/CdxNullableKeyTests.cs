//      0x80 + key    a value              ProjectThunder :: Thunder.Dbf tests
//      00 00 .. 00   .NULL.               Tags on nullable Visual FoxPro fields of every type: the key
//                                         is the field's usual key behind VFP's null marker byte.

using System.Globalization;
using System.Reflection;

namespace Thunder.Dbf.Tests;

public sealed class CdxNullableKeyTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-cdx-null-");

    public void Dispose() => _dir.Delete(recursive: true);

    // Each nullable field has a twin that isn't, holding the same values, so the twin's tag shows the
    // field's usual key: Visual FoxPro's, which the VFP 9 fixtures pin down for every one of these types.
    private static readonly (string Name, DbfFieldDefinition Plain, DbfFieldDefinition Nullable, object?[] Values)[] Kinds =
    [
        ("CUR", DbfFieldDefinition.Currency("CUR"), DbfFieldDefinition.Currency("NCUR") with { Nullable = true }, [-2.25m, 0m, 1.5m, 99999.9999m]),
        ("NUM", DbfFieldDefinition.Numeric("NUM", 10, 2), DbfFieldDefinition.Numeric("NNUM", 10, 2) with { Nullable = true }, [-0.01m, 0m, 12.5m, 1234567.89m]),
        ("INT", DbfFieldDefinition.Integer("INT"), DbfFieldDefinition.Integer("NINT") with { Nullable = true }, [-7, 0, 42, 2_000_000_000]),
        ("DAT", DbfFieldDefinition.Date("DAT"), DbfFieldDefinition.Date("NDAT") with { Nullable = true }, [new DateOnly(1994, 3, 14), new DateOnly(2026, 11, 13), new DateOnly(1900, 1, 1)]),
        ("DTM", DbfFieldDefinition.DateTime("DTM"), DbfFieldDefinition.DateTime("NDTM") with { Nullable = true }, [new DateTime(2026, 11, 13, 8, 30, 0), new DateTime(1999, 12, 31, 23, 59, 59)]),
        ("DBL", DbfFieldDefinition.Double("DBL", 2), DbfFieldDefinition.Double("NDBL", 2) with { Nullable = true }, [-1.25, 0.5, 1e10]),
        ("NAM", DbfFieldDefinition.Character("NAM", 6), DbfFieldDefinition.Character("NNAM", 6) with { Nullable = true }, ["A", "Bob", "zz"]),
    ];

    private string MakeTable(int rows)
    {
        var path = Path.Combine(_dir.FullName, "kinds2.dbf");
        var fields = Kinds.SelectMany(k => new[] { k.Plain, k.Nullable }).ToList();
        using (var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, fields))
        {
            for (var i = 0; i < rows; i++)
            {
                var values = new List<object?>();
                foreach (var kind in Kinds)
                {
                    var value = kind.Values[i % kind.Values.Length];
                    values.Add(value);
                    values.Add(i % 5 == 3 ? null : value); // .NULL. in every fifth row
                }
                table.Append([.. values]);
            }
        }
        using (var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true))
        {
            foreach (var kind in Kinds)
            {
                table.CreateIndexTag(kind.Plain.Name, kind.Plain.Name);
                table.CreateIndexTag(kind.Nullable.Name, kind.Nullable.Name);
            }
        }
        return path;
    }

    private static int FieldIndex(DbfTable table, string name) => table.Fields.ToList().FindIndex(f => f.Name == name);

    private static void AssertTagsMatchRecords(DbfTable table)
    {
        using var cdx = table.OpenStructuralIndex()!;
        var records = table.ReadAll().ToList();
        foreach (var kind in Kinds)
        {
            var name = kind.Nullable.Name;
            var tag = cdx[name];
            var field = FieldIndex(table, name);
            Assert.Equal(cdx[kind.Plain.Name].KeyLength + 1, tag.KeyLength);

            // Every key decodes to its record's value, .NULL. to null.
            var entries = tag.Entries().ToList();
            Assert.Equal(records.Count, entries.Count);
            Assert.All(entries, e =>
            {
                var record = records[e.RecordNumber - 1];
                var expected = record.IsNull(field) ? null : record[name];
                Assert.Equal(Normalize(expected), Normalize(e.Key));
            });

            // And every value, null included, finds exactly the records that hold it.
            foreach (var group in records.GroupBy(r => r.IsNull(field) ? null : Normalize(r[name])))
            {
                var probe = group.Key is null ? null : group.First()[name];
                var found = tag.SeekExact(probe).Select(e => e.RecordNumber).Order().ToList();
                if (kind.Plain.Type == 'C' && probe is string text)
                    found = found.Where(n => Equals(Normalize(records[n - 1][name]), text)).ToList(); // a character seek matches by prefix
                Assert.Equal(group.Select(r => r.RecordNumber).Order(), found);
            }
        }
    }

    // Keys come back as decimal for numbers; values as the field's own type.
    private static object? Normalize(object? value) => value switch
    {
        null => null,
        int or long or double or float => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        _ => value,
    };

    [Fact]
    public void A_tag_on_a_nullable_field_of_any_type_is_the_usual_key_behind_the_null_marker()
    {
        var path = MakeTable(40);
        using var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true);
        Assert.True(table.MaintainsIndex);

        // The bytes, as Visual FoxPro stores them: 0x80, then exactly the twin field's key; all zeros for .NULL.
        var tags = MaintainedTags(table);
        foreach (var record in table.ReadAll())
        {
            foreach (var kind in Kinds)
            {
                var nullable = tags.Single(t => t.Name == kind.Nullable.Name).KeyFor(record);
                if (record.IsNull(FieldIndex(table, kind.Nullable.Name)))
                {
                    Assert.All(nullable, b => Assert.Equal(0, b));
                    continue;
                }
                var plain = tags.Single(t => t.Name == kind.Plain.Name).KeyFor(record);
                Assert.Equal(0x80, nullable[0]);
                Assert.Equal(plain, nullable[1..]);
            }
        }
        AssertTagsMatchRecords(table);
    }

    [Fact]
    public void A_table_with_tags_on_nullable_fields_takes_writes_and_keeps_them()
    {
        var path = MakeTable(30);
        using (var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true))
        {
            Assert.True(table.MaintainsIndex);
            // A value to null, a null to a value, and a new record of each.
            var row = new List<object?>();
            var nulls = new List<object?>();
            foreach (var kind in Kinds)
            {
                row.Add(kind.Values[1]); row.Add(kind.Values[^1]);
                nulls.Add(kind.Values[0]); nulls.Add(null);
            }
            table.Append([.. row]);
            table.Append([.. nulls]);
            table.Update(0, [.. nulls]);
            table.Update(3, [.. row]); // row 3 held nulls
            AssertTagsMatchRecords(table);
        }

        // A fresh writable open finds every tag in step.
        using var again = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true);
        Assert.True(again.MaintainsIndex);
        AssertTagsMatchRecords(again);
    }

    [Fact]
    public void A_tag_on_a_nullable_field_without_the_marker_is_refused_with_its_reason()
    {
        var path = Path.Combine(_dir.FullName, "expr.dbf");
        using (DbfTable.Create(path, DbfTableFormat.VisualFoxPro,
                   [DbfFieldDefinition.Numeric("QTY", 6) with { Nullable = true }, DbfFieldDefinition.Character("NAME", 5)]))
        {
        }
        // A tag over the nullable field whose keys lack the null marker (a 5-byte key over QTY, made by
        // pointing NAME's tag at QTY): not a layout Thunder.Dbf builds, and not an index out of step.
        var cdx = Path.ChangeExtension(path, ".cdx");
        using (var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true))
            table.CreateIndexTag("NAME", "NAME");
        var bytes = File.ReadAllBytes(cdx);
        var name = bytes.AsSpan().IndexOf("NAME"u8);                                   // the tag's name, in the tag directory
        var at = name + 4 + bytes.AsSpan(name + 4).IndexOf("NAME\0"u8);                 // then its expression, in its header's pool
        "QTY\0\0"u8.CopyTo(bytes.AsSpan(at)); // same length, so the expression pool stays valid
        File.WriteAllBytes(cdx, bytes);

        using var opened = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true);
        using (var probe = opened.OpenStructuralIndex()!)
            Assert.Equal("QTY", probe["NAME"].Expression);
        var error = Assert.Throws<DbfException>(() => opened.Append(1, "x"));
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
        Assert.Contains("nullable field QTY", error.Diagnostic);
        Assert.Contains("doesn't build", error.Message); // the message names the kind of problem, not text from the index
        Assert.DoesNotContain("QTY", error.Message);
    }

    private static List<IMaintainedTag> MaintainedTags(DbfTable table)
    {
        var indexes = (IEnumerable<IMaintainedIndex>)typeof(DbfTable)
            .GetProperty("MaintainedIndexes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(table)!;
        return indexes.SelectMany(i => i.MaintainedTags).ToList();
    }
}
