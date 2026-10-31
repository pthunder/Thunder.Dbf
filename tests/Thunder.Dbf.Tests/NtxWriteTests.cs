//          [ 7 19 | 42 | 57 88 ]  full!        ProjectThunder :: Thunder.Dbf tests
//                   |                          Clipper .ntx files written by X#, changed
//          [ 7 19 ] [ 57 88 ]                  through the table every way there is, then
//               \    /                         walked byte by byte: every key where a full
//              [ 42 ]   new root               rebuild would put it, every page accounted for.

using System.Buffers.Binary;
using System.Text;
using static Thunder.Dbf.Tests.IndexTestSupport;

namespace Thunder.Dbf.Tests;

public sealed class NtxWriteTests : IDisposable
{
    private const int PageSize = 1024;

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-ntx-");

    public void Dispose() => _dir.Delete(recursive: true);

    // ---- fixtures -------------------------------------------------------------------------

    // Customer.dbf (20 records) and its two one-page indexes: a few hundred appends split them and
    // grow new roots.
    private string CopyCustomers()
    {
        foreach (var file in new[] { "Customer.dbf", "Custname.ntx", "Custnum.ntx" })
            File.Copy(Fixture("xsharp/explorer/" + file), Path.Combine(_dir.FullName, file));
        return Path.Combine(_dir.FullName, "Customer.dbf");
    }

    // TEST10K.DBF and three 10,000-key indexes: character (40-byte keys, 18 to a page), negative
    // numeric (-Salary) and date. X# set the FoxPro structural-.cdx flag on the table for its .cdx
    // tests; Clipper doesn't use it, so the copy clears it rather than bringing the .cdx along.
    private string CopyTenThousand()
    {
        var table = Path.Combine(_dir.FullName, "TEST10K.DBF");
        var bytes = File.ReadAllBytes(Fixture("xsharp/rdd-tests/TEST10K.DBF"));
        bytes[28] = 0;
        File.WriteAllBytes(table, bytes);
        File.Copy(Fixture("xsharp/rdd-tests/10kDate.ntx"), Path.Combine(_dir.FullName, "10kDate.ntx"));
        File.Copy(Fixture("xsharp/rdd-tests/10kSalary.ntx"), Path.Combine(_dir.FullName, "10kSalary.ntx"));
        File.Copy(Fixture("xsharp/rdd-tests/10kName.xxx"), Path.Combine(_dir.FullName, "10kName.ntx"));
        return table;
    }

    private string[] IndexFiles() => Directory.GetFiles(_dir.FullName, "*.ntx").Order().ToArray();

    private static DbfTable OpenWritable(string path)
    {
        var table = DbfTable.Open(path, writable: true);
        Assert.True(table.MaintainsIndex);
        return table;
    }

    // ---- the tests ------------------------------------------------------------------------

    // The oracle the other tests lean on: before any write, KeyFor rebuilds every key X# stored,
    // byte for byte and in the same order.
    [Theory]
    [InlineData("xsharp/explorer/Custname.ntx", "xsharp/explorer/Customer.dbf")]
    [InlineData("xsharp/explorer/Custnum.ntx", "xsharp/explorer/Customer.dbf")]
    [InlineData("xsharp/rdd-tests/10kDate.ntx", "xsharp/rdd-tests/TEST10K.DBF")]
    [InlineData("xsharp/rdd-tests/10kName.xxx", "xsharp/rdd-tests/TEST10K.DBF")]
    [InlineData("xsharp/rdd-tests/10kSalary.ntx", "xsharp/rdd-tests/TEST10K.DBF")]
    [InlineData("xsharp/rdd-tests/TESTNTX2.NTX", "xsharp/rdd-tests/TESTNTX2.DBF")]
    public void KeyFor_rebuilds_every_key_Clipper_stored(string indexFixture, string tableFixture)
    {
        using var table = DbfTable.Open(Fixture(tableFixture));
        using var index = NtxIndex.Open(Fixture(indexFixture), table: table);

        Assert.Equal(Oracle(index.Tag, table), RawTree.Read(File.ReadAllBytes(Fixture(indexFixture))).Entries);
    }

    [Fact]
    public void Hundreds_of_appends_split_pages_and_grow_new_roots()
    {
        var path = CopyCustomers();
        var before = IndexFiles().ToDictionary(f => f, f => RawTree.Read(File.ReadAllBytes(f)));
        Assert.All(before.Values, tree => Assert.Equal(1, tree.Height));

        using (var table = OpenWritable(path))
        {
            var random = new Random(1990);
            table.AppendRange(Enumerable.Range(0, 400).Select(i => Customer(table, 1000 + random.Next(9000), $"F{random.Next(500):D3}", $"L{random.Next(80):D2}")));
        }

        foreach (var file in IndexFiles())
        {
            var after = RawTree.Read(File.ReadAllBytes(file));
            Assert.True(after.Height >= 2, $"{Path.GetFileName(file)} never grew a new root.");
            Assert.True(after.Pages.Count > 3, $"{Path.GetFileName(file)} split only {after.Pages.Count - 1} times.");
            Assert.True(after.Version > before[file].Version);
        }
        AssertIndexesMatchTable(path);

        // New keys can be sought, through the reader and through the table.
        using var reopened = DbfTable.Open(path);
        using var custnum = reopened.OpenIndex(Path.Combine(_dir.FullName, "Custnum.ntx"));
        var last = reopened.Read(reopened.RecordCount - 1);
        Assert.Contains(last.RecordNumber, custnum.Tags[0].SeekExact(last["CUSTNUM"]).Select(e => e.RecordNumber));
        Assert.Equal(last["CUSTNUM"], reopened.Seek(custnum.Tags[0], last["CUSTNUM"])!["CUSTNUM"]);
        using var custname = reopened.OpenIndex(Path.Combine(_dir.FullName, "Custname.ntx"));
        var name = ((string)last["LASTNAME"]!).PadRight(10) + (string)last["FIRSTNAME"]!;
        Assert.Contains(last.RecordNumber, custname.Tags[0].SeekExact(name).Select(e => e.RecordNumber));
    }

    [Fact]
    public void Random_changes_keep_three_ten_thousand_key_indexes_exact()
    {
        var path = CopyTenThousand();
        var random = new Random(53);
        using (var table = OpenWritable(path))
        {
            var template = table.Read(0).Values.ToArray();
            for (var step = 0; step < 350; step++)
            {
                var pick = random.Next(table.RecordCount);
                switch (random.Next(6))
                {
                    case 0 or 1:
                        table.Append(Employee(table, template, random));
                        break;
                    case 2 or 3:
                    {
                        var values = Employee(table, table.Read(pick).Values.ToArray(), random);
                        if (random.Next(3) == 0)
                            values[Field(table, "LAST")] = table.Read(pick)["LAST"]; // only some keys move
                        table.Update(pick, values);
                        break;
                    }
                    case 4:
                        table.Delete(pick);
                        break;
                    default:
                        table.Recall(pick);
                        break;
                }
            }
        }

        AssertIndexesMatchTable(path);

        using var reopened = DbfTable.Open(path);
        using var salary = NtxIndex.Open(Path.Combine(_dir.FullName, "10kSalary.ntx"), table: reopened);
        var lastRecord = reopened.Read(reopened.RecordCount - 1);
        Assert.Contains(lastRecord.RecordNumber, salary.Tag.SeekExact(-(decimal)lastRecord["SALARY"]!).Select(e => e.RecordNumber));
        using var hired = NtxIndex.Open(Path.Combine(_dir.FullName, "10kDate.ntx"), table: reopened);
        Assert.Contains(lastRecord.RecordNumber, hired.Tag.SeekExact(lastRecord["HIREDATE"]).Select(e => e.RecordNumber));
    }

    // Moving most keys to one end of the tree empties pages all over it: they must borrow from a
    // sibling or merge with it, and the pages that go must end up on the free list, not lost.
    [Fact]
    public void Mass_key_changes_merge_emptied_pages_and_free_them()
    {
        var path = CopyCustomers();
        using (var table = OpenWritable(path))
        {
            table.AppendRange(Enumerable.Range(0, 300).Select(i => Customer(table, 2000 + i, $"F{i:D4}", $"M{i % 97:D2}")));
            var heightBefore = RawTree.Read(File.ReadAllBytes(Path.Combine(_dir.FullName, "Custnum.ntx"))).Height;
            Assert.True(heightBefore >= 2);

            for (var i = 0; i < table.RecordCount; i++)
            {
                var values = table.Read(i).Values.ToArray();
                values[Field(table, "CUSTNUM")] = 99_000m - i;
                values[Field(table, "LASTNAME")] = "ZZ";
                table.Update(i, values);
            }
        }

        AssertIndexesMatchTable(path);
        var custnum = RawTree.Read(File.ReadAllBytes(Path.Combine(_dir.FullName, "Custnum.ntx")));
        Assert.NotEmpty(custnum.FreePages);
    }

    [Fact]
    public void RebuildIndex_gives_the_same_keys_the_writes_left()
    {
        var path = CopyTenThousand();
        var random = new Random(7);
        using (var table = OpenWritable(path))
        {
            var template = table.Read(0).Values.ToArray();
            table.AppendRange(Enumerable.Range(0, 400).Select(_ => Employee(table, template, random)));
            for (var i = 0; i < 100; i++)
            {
                var pick = random.Next(table.RecordCount);
                table.Update(pick, Employee(table, table.Read(pick).Values.ToArray(), random));
            }
        }
        var maintained = IndexFiles().ToDictionary(f => f, f => RawTree.Read(File.ReadAllBytes(f)).Entries);

        using (var table = OpenWritable(path))
            table.RebuildIndex();

        foreach (var file in IndexFiles())
        {
            var rebuilt = RawTree.Read(File.ReadAllBytes(file));
            Assert.Equal(maintained[file], rebuilt.Entries);
            Assert.Empty(rebuilt.FreePages); // a rebuild starts the file over
        }
        AssertIndexesMatchTable(path);
    }

    // Unique: the lowest record number holds each key, and when it leaves, the next lowest steps in.
    [Fact]
    public void Unique_indexes_keep_the_lowest_record_per_key()
    {
        var path = CopyCustomers();
        SetHeaderByte("Custname.ntx", 278, 1);
        using (var table = DbfTable.Open(path, writable: true))
            table.RebuildIndex();

        int firstDuplicate;
        using (var table = OpenWritable(path))
        {
            var original = table.Read(4);
            firstDuplicate = table.Append(Customer(table, 5000, (string)original["FIRSTNAME"]!, (string)original["LASTNAME"]!));
            table.Append(Customer(table, 5001, (string)original["FIRSTNAME"]!, (string)original["LASTNAME"]!));
        }
        AssertIndexesMatchTable(path);
        Assert.Equal([5], SeekCustname(path, 4));

        using (var table = OpenWritable(path))
        {
            var values = table.Read(4).Values.ToArray();
            values[Field(table, "LASTNAME")] = "MOVED";
            table.Update(4, values);
        }
        AssertIndexesMatchTable(path);
        Assert.Equal([firstDuplicate + 1], SeekCustname(path, firstDuplicate));
    }

    private int[] SeekCustname(string path, int recordIndex)
    {
        using var table = DbfTable.Open(path);
        using var index = NtxIndex.Open(Path.Combine(_dir.FullName, "Custname.ntx"), table: table);
        var record = table.Read(recordIndex);
        return index.Tag.SeekExact(((string)record["LASTNAME"]!).PadRight(10) + (string)record["FIRSTNAME"]!).Select(e => e.RecordNumber).ToArray();
    }

    [Fact]
    public void Descending_indexes_stay_descending()
    {
        var path = CopyCustomers();
        SetHeaderByte("Custnum.ntx", 280, 1);
        using (var table = DbfTable.Open(path, writable: true))
        {
            table.RebuildIndex();
            var random = new Random(11);
            table.AppendRange(Enumerable.Range(0, 300).Select(_ => Customer(table, random.Next(1, 99_999), "D", "D")));
        }

        AssertIndexesMatchTable(path);
        using var reopened = DbfTable.Open(path);
        using var index = NtxIndex.Open(Path.Combine(_dir.FullName, "Custnum.ntx"), table: reopened);
        Assert.True(index.Tag.IsDescending);
        var keys = index.Tag.Entries().Select(e => (decimal)e.Key!).ToList();
        Assert.Equal(keys.OrderDescending(), keys);
    }

    // Clipper 5.3 and Harbour store a FOR condition in the header; records it leaves out stay out,
    // and records that cross it on update come and go.
    [Fact]
    public void A_FOR_condition_decides_which_records_are_indexed()
    {
        var path = CopyCustomers();
        var file = Path.Combine(_dir.FullName, "Custnum.ntx");
        EditHeader("Custnum.ntx", header =>
        {
            header[0] |= 0x01; // signature bit: a FOR expression is present
            Encoding.ASCII.GetBytes("CUSTNUM >= 5000").CopyTo(header, 282);
        });

        using (var table = DbfTable.Open(path, writable: true))
        {
            table.RebuildIndex();
            var random = new Random(3);
            table.AppendRange(Enumerable.Range(0, 200).Select(_ => Customer(table, random.Next(1, 10_000), "F", "F")));
            for (var i = 0; i < 100; i++)
            {
                var pick = random.Next(table.RecordCount);
                var values = table.Read(pick).Values.ToArray();
                values[Field(table, "CUSTNUM")] = (decimal)random.Next(1, 10_000);
                table.Update(pick, values);
            }
        }

        AssertIndexesMatchTable(path);
        using var reopened = DbfTable.Open(path);
        using var index = NtxIndex.Open(file, table: reopened);
        Assert.Equal("CUSTNUM >= 5000", index.Tag.ForExpression);
        Assert.All(index.Tag.Entries(), e => Assert.True((decimal)e.Key! >= 5000));
    }

    // A damaged index stops the write with an explained error: a page that points back at itself,
    // or one claiming more keys than a page holds, must not hang or crash the writer.
    [Theory]
    [InlineData("cycle")]
    [InlineData("count")]
    [InlineData("offset")]
    public void A_damaged_index_fails_the_write_with_an_explanation(string damage)
    {
        var path = CopyTenThousand();
        var file = Path.Combine(_dir.FullName, "10kName.ntx");
        var bytes = File.ReadAllBytes(file);
        var root = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        switch (damage)
        {
            case "cycle":
                for (var slot = 0; slot <= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(root)); slot++)
                {
                    var at = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(root + 2 + slot * 2));
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(root + at), (uint)root); // every child is the root itself
                }
                break;
            case "count":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(root), 999);
                break;
            default:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(root + 2), 1020); // slot 0's item runs off the page
                break;
        }
        File.WriteAllBytes(file, bytes);

        using var table = DbfTable.Open(path, writable: true);
        var template = table.Read(0).Values.ToArray();
        var error = Assert.Throws<DbfException>(() => table.Append(template));
        Assert.Equal(DbfErrorCode.IndexDamaged, error.Code);
        Assert.IsType<InvalidDataException>(error.InnerException);
    }

    // ---- checks ---------------------------------------------------------------------------

    /// <summary>
    /// Every .ntx in the folder against its table: (a) the reader returns keys in exactly the order a
    /// full rebuild would store them, (b) the stored bytes equal KeyFor over every record, and the
    /// reader's decoded keys equal the expression's values, (e) the header's layout fields didn't
    /// change, and the tree is a sound, balanced B-tree with every page in use or on the free list.
    /// </summary>
    private void AssertIndexesMatchTable(string tablePath)
    {
        using var table = DbfTable.Open(tablePath);
        foreach (var file in IndexFiles())
        {
            var bytes = File.ReadAllBytes(file);
            using var index = NtxIndex.Open(file, table: table);
            var tree = RawTree.Read(bytes);
            var oracle = Oracle(index.Tag, table);

            Assert.Equal(oracle, tree.Entries);
            Assert.Equal(oracle.Select(e => e.Record), index.Tag.Entries().Select(e => e.RecordNumber));
            AssertTagMatchesTable(TagView.Of(index.Tag), table);

            var original = OriginalHeader(file);
            Assert.Equal(original.AsSpan(0, 2).ToArray(), bytes.AsSpan(0, 2).ToArray());     // signature
            Assert.Equal(original.AsSpan(12, 10).ToArray(), bytes.AsSpan(12, 10).ToArray());  // item/key length, decimals, max and half keys
            Assert.Equal(original.AsSpan(22, 529).ToArray(), bytes.AsSpan(22, 529).ToArray()); // expression, unique, descending, FOR, name
        }
    }

    // The header as the test last set it (fixture copy plus any flags a test flipped).
    private readonly Dictionary<string, byte[]> _headers = [];

    private byte[] OriginalHeader(string file) =>
        _headers.TryGetValue(Path.GetFileName(file), out var header) ? header : _headers[Path.GetFileName(file)] = InitialHeader(file);

    private byte[] InitialHeader(string file)
    {
        var name = Path.GetFileName(file);
        var source = name switch
        {
            "10kName.ntx" => "xsharp/rdd-tests/10kName.xxx",
            "10kDate.ntx" or "10kSalary.ntx" => "xsharp/rdd-tests/" + name,
            _ => "xsharp/explorer/" + name,
        };
        return File.ReadAllBytes(Fixture(source)).AsSpan(0, PageSize).ToArray();
    }

    private void SetHeaderByte(string name, int offset, byte value) => EditHeader(name, header => header[offset] = value);

    private void EditHeader(string name, Action<byte[]> edit)
    {
        var file = Path.Combine(_dir.FullName, name);
        var bytes = File.ReadAllBytes(file);
        edit(bytes);
        File.WriteAllBytes(file, bytes);
        _headers[name] = bytes.AsSpan(0, PageSize).ToArray();
    }

    /// <summary>What a full rebuild stores: KeyFor over every record the FOR condition admits, in byte order (reversed when descending), ties in record order; one per key when unique.</summary>
    private static List<(string Key, int Record)> Oracle(NtxTag tag, DbfTable table)
    {
        var maintained = (IMaintainedTag)tag;
        var entries = table.ReadAll()
            .Where(maintained.Includes)
            .Select(r => (Key: maintained.KeyFor(r), Record: r.RecordNumber))
            .ToList();
        if (tag.IsUnique)
            entries = entries.GroupBy(e => Convert.ToHexString(e.Key)).Select(g => g.First()).ToList();
        entries.Sort((a, b) =>
        {
            var order = a.Key.AsSpan().SequenceCompareTo(b.Key);
            return order != 0 ? (tag.IsDescending ? -order : order) : a.Record.CompareTo(b.Record);
        });
        return entries.Select(e => (Convert.ToHexString(e.Key), e.Record)).ToList();
    }

    // ---- rows -----------------------------------------------------------------------------

    private static object?[] Customer(DbfTable table, int number, string first, string last)
    {
        var values = new object?[table.Fields.Count];
        values[Field(table, "CUSTNUM")] = (decimal)number;
        values[Field(table, "FIRSTNAME")] = first;
        values[Field(table, "LASTNAME")] = last;
        values[Field(table, "CITY")] = "Springfield";
        return values;
    }

    private static object?[] Employee(DbfTable table, object?[] template, Random random)
    {
        var values = template.ToArray();
        values[Field(table, "FIRST")] = $"First{random.Next(200)}";
        values[Field(table, "LAST")] = $"Last{random.Next(150)}";
        values[Field(table, "HIREDATE")] = random.Next(10) == 0 ? null : new DateOnly(1970, 1, 1).AddDays(random.Next(15_000));
        values[Field(table, "SALARY")] = Math.Round((decimal)(random.NextDouble() * 1_099.98 - 99.99), 2); // N(6,2); negatives test the inverted digits
        return values;
    }

    private static int Field(DbfTable table, string name) =>
        table.Fields.Select((f, i) => (f, i)).Single(x => x.f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).i;

    /// <summary>
    /// An .ntx read straight from its bytes, without the library: the in-order keys (as hex) and
    /// record numbers, the tree's height, and its pages. Checks what Clipper relies on as it goes:
    /// all leaves at one depth, no empty page but the root, an offset table with a distinct in-page
    /// slot for every item a page can hold, and every page either in the tree or on the free list.
    /// </summary>
    private sealed record RawTree(List<(string Key, int Record)> Entries, int Height, HashSet<int> Pages, List<int> FreePages, int Version)
    {
        public static RawTree Read(byte[] file)
        {
            var version = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2));
            var root = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
            var itemLength = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(12));
            var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(14));
            var maxKeys = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(18));
            var entries = new List<(string, int)>();
            var pages = new HashSet<int>();
            var leafDepths = new HashSet<int>();

            void Walk(int offset, int depth)
            {
                Assert.True(depth < 16 && pages.Add(offset), $"page {offset} reached twice");
                var page = file.AsSpan(offset, PageSize);
                var count = BinaryPrimitives.ReadUInt16LittleEndian(page);
                Assert.True(count <= maxKeys);
                Assert.True(count > 0 || offset == root, $"page {offset} is empty");

                var slots = new HashSet<int>();
                for (var slot = 0; slot <= maxKeys; slot++)
                {
                    var at = BinaryPrimitives.ReadUInt16LittleEndian(page[(2 + slot * 2)..]);
                    Assert.True(at >= 2 + (maxKeys + 1) * 2 && at + itemLength <= PageSize && slots.Add(at), $"page {offset} slot {slot}");
                }

                var leaf = BinaryPrimitives.ReadUInt32LittleEndian(page[BinaryPrimitives.ReadUInt16LittleEndian(page[(2 + count * 2)..])..]) == 0;
                if (leaf)
                    leafDepths.Add(depth);
                for (var slot = 0; slot <= count; slot++)
                {
                    var at = BinaryPrimitives.ReadUInt16LittleEndian(page[(2 + slot * 2)..]);
                    var child = (int)BinaryPrimitives.ReadUInt32LittleEndian(page[at..]);
                    Assert.Equal(leaf, child == 0);
                    if (child != 0)
                        Walk(child, depth + 1);
                    if (slot < count)
                        entries.Add((Convert.ToHexString(page.Slice(at + 8, keyLength)), (int)BinaryPrimitives.ReadUInt32LittleEndian(page[(at + 4)..])));
                }
            }

            Walk(root, 0);
            Assert.Single(leafDepths);

            var free = new List<int>();
            for (var next = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8)); next != 0;)
            {
                Assert.True(free.Count < file.Length / PageSize && !pages.Contains(next) && !free.Contains(next), $"free page {next}");
                free.Add(next);
                var page = file.AsSpan(next, PageSize);
                Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(page));
                next = (int)BinaryPrimitives.ReadUInt32LittleEndian(page[BinaryPrimitives.ReadUInt16LittleEndian(page[2..])..]);
            }

            Assert.Equal(0, file.Length % PageSize);
            Assert.Equal(Enumerable.Range(1, file.Length / PageSize - 1).Select(p => p * PageSize), pages.Concat(free).Order());
            return new RawTree(entries, leafDepths.Single() + 1, pages, free, version);
        }
    }
}
