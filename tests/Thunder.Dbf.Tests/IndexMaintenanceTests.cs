//      INDEX ON ID TAG ID_TAG FOR .NOT. DELETED() UNIQUE        ProjectThunder :: Thunder.Dbf tests
//         ours:    0400 0000 49445f544147 ... 10 0203 00 02 4e    INDEX ON and PACK. New tags are checked
//         dBASE:   0400 0000 49445f544147 ... 10 0203 00 02 4e    byte for byte against the .mdx SIMATIC
//                                                                 Manager wrote for the same table.

using System.Buffers.Binary;

namespace Thunder.Dbf.Tests;

public sealed class IndexMaintenanceTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-indexon-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string CopyChangelog(bool withIndex)
    {
        foreach (var file in new[] { "CHANGELOG.DBF", "CHANGELOG.DBT" }.Concat(withIndex ? ["CHANGELOG.MDX"] : []))
            File.Copy(Path.Combine(Fixtures.Root, "siemens-step7/siprom", file), Path.Combine(_dir.FullName, file));
        var path = Path.Combine(_dir.FullName, "CHANGELOG.DBF");
        if (!withIndex)
        {
            // Clear the header's "has a production .mdx" flag, as a table that never had an index.
            using var stream = SharedFile.OpenWrite(path);
            stream.Position = 28;
            stream.WriteByte(0);
        }
        return path;
    }

    private static readonly (string Name, string Expression, bool Unique)[] SiemensTags =
    [
        ("ID_TAG", "ID", true), ("DATETIME_T", "DATETIME", false), ("USER_TAG", "USER", false),
        ("ACTION_TAG", "ACTION", false), ("OBJECTID_T", "OBJECTID", false), ("LEVEL_TAG", "LEVEL", false),
    ];

    [Fact]
    public void Tags_made_from_scratch_match_the_ones_dBASE_software_wrote()
    {
        var path = CopyChangelog(withIndex: false);
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            Assert.Null(table.OpenProductionIndex());
            foreach (var (name, expression, unique) in SiemensTags)
                table.CreateIndexTag(name, expression, ".NOT. DELETED()", unique);
            Assert.True(table.Header.HasProductionMdx);
        }

        var ours = SharedFile.ReadAllBytes(Path.Combine(_dir.FullName, "CHANGELOG.MDX"));
        var theirs = SharedFile.ReadAllBytes(Path.Combine(Fixtures.Root, "siemens-step7/siprom/CHANGELOG.MDX"));

        // File header: version, name, block factor and size, production flag, slots, entry length, tag count.
        Assert.Equal(theirs[0], ours[0]);
        Assert.Equal(theirs.AsSpan(4, 16).ToArray(), ours.AsSpan(4, 16).ToArray());
        Assert.Equal(theirs.AsSpan(20, 10).ToArray(), ours.AsSpan(20, 10).ToArray());
        Assert.Equal(theirs.AsSpan(512, 32).ToArray(), ours.AsSpan(512, 32).ToArray()); // the name tree's anchor

        for (var i = 0; i < SiemensTags.Length; i++)
        {
            // Tag table entry: everything after the header page number, including dBASE's name tree.
            var o = ours.AsSpan(544 + 32 * i, 32).ToArray();
            var t = theirs.AsSpan(544 + 32 * i, 32).ToArray();
            Assert.Equal(t[4..], o[4..]);

            // Tag header: key format, type, length, items per node, item length, unique flag,
            // expression, FOR flag and FOR expression. Not the root page or the change counter.
            var (oh, th) = (TagHeader(ours, o), TagHeader(theirs, t));
            Assert.Equal(th.AsSpan(8, 12).ToArray(), oh.AsSpan(8, 12).ToArray());
            Assert.Equal(th[23], oh[23]);
            Assert.Equal(th.AsSpan(24, 222).ToArray(), oh.AsSpan(24, 222).ToArray());
            Assert.Equal(th.AsSpan(762, 220).ToArray(), oh.AsSpan(762, 220).ToArray());
        }

        // And the keys agree with the original index, tag by tag.
        using var check = DbfTable.Open(path);
        using var ourIndex = check.OpenProductionIndex()!;
        using var original = MdxIndex.Open(Path.Combine(Fixtures.Root, "siemens-step7/siprom/CHANGELOG.MDX"));
        foreach (var (name, _, _) in SiemensTags)
            Assert.Equal(Keys(original[name]), Keys(ourIndex[name]));
    }

    private static byte[] TagHeader(byte[] file, byte[] entry)
    {
        var page = BinaryPrimitives.ReadInt32LittleEndian(entry);
        return file.AsSpan(page * 512, 1024).ToArray();
    }

    private static List<string> Keys(MdxTag tag) =>
        tag.Entries().Select(e => $"{e.Key}@{e.RecordNumber}").Order().ToList();

    [Fact]
    public void A_new_tag_joins_an_existing_index_and_is_kept_up_to_date()
    {
        var path = CopyChangelog(withIndex: true);
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            table.CreateIndexTag("UPUSER", "UPPER(USER)+STR(ID,10)", descending: true);
            var values = table.Read(0).Values.ToArray();
            values[3] = "zed";
            table.Append(values);
        }

        using var check = DbfTable.Open(path);
        using var index = check.OpenProductionIndex()!;
        Assert.Equal(7, index.Tags.Count);
        var tag = index["UPUSER"];
        Assert.Equal(('C', 42, true), (tag.KeyType, tag.KeyLength, tag.IsDescending));
        Assert.Equal(check.RecordCount, tag.Entries().Count());
        Assert.Equal(4, check.Seek(tag, "ZED")!.RecordNumber);
        foreach (var other in index.Tags.Where(t => t.Name != "UPUSER"))
            Assert.NotEmpty(other.Entries()); // the old tags survived
    }

    [Theory]
    [InlineData("1BAD", "ID", null, "must be 1-10")]
    [InlineData("OK", "NOPE", null, "no field named NOPE")]
    [InlineData("OK", "EVENT", null, "memo field")]
    [InlineData("OK", "ID > 1", null, "logical value")]
    [InlineData("OK", "ID", "USER", "logical expression")]
    [InlineData("ID_TAG", "ID", null, "already has a tag")]
    public void Bad_tags_are_explained_and_change_nothing(string name, string expression, string? filter, string expected)
    {
        var path = CopyChangelog(withIndex: true);
        var before = SharedFile.ReadAllBytes(Path.ChangeExtension(path, ".MDX"));
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            var error = Assert.ThrowsAny<Exception>(() => table.CreateIndexTag(name, expression, filter));
            Assert.Contains(expected, error.Message);
        }
        Assert.Equal(before, SharedFile.ReadAllBytes(Path.ChangeExtension(path, ".MDX")));
    }

    [Fact]
    public void Pack_removes_deleted_records_renumbers_and_rebuilds_every_tag()
    {
        var path = CopyChangelog(withIndex: true);
        int[] keptIds;
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            for (var i = 0; i < 5; i++)
            {
                var values = table.Read(0).Values.ToArray();
                values[0] = 100m + i;
                table.Append(values);
            }
            table.Delete(1);
            table.Delete(4);
            table.Delete(7);
            keptIds = table.ReadAll().Where(r => !r.IsDeleted).Select(r => Convert.ToInt32(r["ID"])).ToArray();

            Assert.Equal(3, table.Pack());
            Assert.Equal(5, table.RecordCount);
        }

        Assert.False(File.Exists(path + ".packing")); // the safety copy is gone once it's done
        using var check = DbfTable.Open(path);
        Assert.Equal(keptIds, check.ReadAll().Select(r => Convert.ToInt32(r["ID"])).ToArray());
        Assert.All(check.ReadAll(), r => Assert.False(r.IsDeleted));
        Assert.Equal(check.Header.HeaderLength + 5L * check.Header.RecordLength + 1, new FileInfo(path).Length);

        using var index = check.OpenProductionIndex()!;
        foreach (var tag in index.Tags)
        {
            Assert.Equal(5, tag.Entries().Count());
            Assert.All(tag.Entries(), e => Assert.InRange(e.RecordNumber, 1, 5));
        }
        Assert.Equal(5, check.Seek(index["ID_TAG"], (decimal)keptIds[^1])!.RecordNumber);
    }

    [Fact]
    public void An_unfinished_pack_blocks_every_write_until_the_table_is_restored()
    {
        var path = CopyChangelog(withIndex: true);
        File.WriteAllText(path + ".packing", "left over from a crash");
        using var table = DbfTable.Open(path, writable: true, exclusive: true);

        Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => table.Pack()).Code);
        Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => table.Append(table.Read(0).Values.ToArray())).Code);
        Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => table.Delete(0)).Code);
        Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => table.CreateIndexTag("X", "ID")).Code);

        File.Delete(path + ".packing");
        table.Delete(0); // restored: writes work again
    }

    [Fact]
    public void A_pack_whose_keys_cant_be_worked_out_changes_nothing()
    {
        var path = Path.Combine(_dir.FullName, "bad.dbf");
        using (var table = DbfTable.Create(path, [DbfFieldDefinition.Numeric("N", 4)]))
        {
            table.Append(5m);
            table.Append(4m);
            table.Append(2m);
            table.CreateIndexTag("BYRATIO", "STR(100/N, 10)");
            table.Delete(0);
        }

        // Another program writes 0 into record 2 without updating the index: 100/N now fails for it.
        long offset;
        using (var probe = DbfTable.Open(path))
            offset = probe.Header.HeaderLength + probe.Header.RecordLength + 1;
        using (var file = SharedFile.OpenWrite(path))
        {
            file.Position = offset;
            file.Write("   0"u8);
        }
        var before = SharedFile.ReadAllBytes(path);

        using (var reopened = DbfTable.Open(path, writable: true, exclusive: true))
            Assert.ThrowsAny<Exception>(() => reopened.Pack());
        Assert.Equal(before, SharedFile.ReadAllBytes(path));   // not one record moved
        Assert.False(File.Exists(path + ".packing"));    // and no backup, since nothing started
    }

    [Fact]
    public void Character_keys_are_as_wide_as_the_widest_real_value()
    {
        var path = CopyChangelog(withIndex: true);
        using var table = DbfTable.Open(path, writable: true, exclusive: true);
        // On a synthetic all-blank record LEVEL is 0, which would pick the 1-character branch.
        table.CreateIndexTag("LONGLVL", "IIF(LEVEL > 0, \"LONGVALUE\", \"X\")");

        using var index = table.OpenProductionIndex()!;
        var real = table.ReadAll().Max(r => (decimal)r["LEVEL"]! > 0 ? 9 : 1);
        Assert.Equal(real, index["LONGLVL"].KeyLength);
    }

    [Fact]
    public void An_expression_whose_type_changes_between_records_is_refused_before_anything_is_written()
    {
        var path = Path.Combine(_dir.FullName, "mixed.dbf");
        using (var table = DbfTable.Create(path, [DbfFieldDefinition.Numeric("N", 4)]))
        {
            table.Append(1m);
            table.Append(2m);
            table.CreateIndexTag("FIRST", "N"); // so the .mdx exists to compare
        }
        var mdx = Path.ChangeExtension(path, ".mdx");
        var before = SharedFile.ReadAllBytes(mdx);

        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            var error = Assert.Throws<DbfException>(() => table.CreateIndexTag("MIXED", "IIF(N = 1, \"text\", N)"));
            Assert.Equal(DbfErrorCode.InvalidValue, error.Code);
        }
        Assert.Equal(before, SharedFile.ReadAllBytes(mdx));
    }

    [Fact]
    public void Date_tags_carry_dBASEs_secondary_type_and_filters_keep_their_code_page()
    {
        var path = Path.Combine(_dir.FullName, "people.dbf");
        using (var table = DbfTable.Create(path, [DbfFieldDefinition.Character("CITY", 10), DbfFieldDefinition.Date("BORN")], languageDriver: 0x02))
        {
            table.Append("Québec", new DateOnly(1990, 5, 1));
            table.Append("Paris", new DateOnly(1980, 1, 2));
            table.CreateIndexTag("BYBORN", "BORN", forExpression: "CITY = \"Québec\"");
        }

        var bytes = SharedFile.ReadAllBytes(Path.ChangeExtension(path, ".mdx"));
        var headerPage = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(544));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(headerPage * 512 + 16)));

        using var check = DbfTable.Open(path);
        using var index = check.OpenProductionIndex()!;
        Assert.Equal("CITY = \"Québec\"", index["BYBORN"].ForExpression);
        Assert.Equal([1], index["BYBORN"].Entries().Select(e => e.RecordNumber));
    }

    [Fact]
    public void A_table_just_created_by_path_can_be_indexed_and_packed()
    {
        var path = Path.Combine(_dir.FullName, "fresh.dbf");
        using var table = DbfTable.Create(path, [DbfFieldDefinition.Character("NAME", 8)]);
        table.Append("b");
        table.Append("a");
        table.CreateIndexTag("NAME", "NAME");
        table.Delete(0);
        Assert.Equal(1, table.Pack());
        Assert.False(File.Exists(path + ".packing"));
    }

    [Fact]
    public void Tables_from_streams_cant_be_packed()
    {
        using var table = DbfTable.Create(new MemoryStream(), [DbfFieldDefinition.Character("NAME", 5)]);
        Assert.Equal(DbfErrorCode.UnsupportedFormat, Assert.Throws<DbfException>(() => table.Pack()).Code);
    }

    [Fact]
    public void A_table_without_an_index_packs_too()
    {
        var path = Path.Combine(_dir.FullName, "plain.dbf");
        using var table = DbfTable.Create(path, [DbfFieldDefinition.Character("NAME", 5)]);
        foreach (var name in new[] { "a", "b", "c" })
            table.Append(name);
        table.Delete(0);

        Assert.Equal(1, table.Pack());
        Assert.Equal(["b", "c"], table.ReadAll().Select(r => (string)r["NAME"]!));
    }
}
