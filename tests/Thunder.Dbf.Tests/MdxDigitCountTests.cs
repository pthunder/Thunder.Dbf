//      36 29 95 00 ..   Jet           ProjectThunder :: Thunder.Dbf tests
//      36 09 95 00 ..   dBASE         The same 95, two ways. A number key carries a digit count, and
//          [ 95 ]                     Jet goes down by interior keys and steps by bytes: write its
//       /        \                    form, and keep every interior key on its child's last key.

using System.Buffers.Binary;

namespace Thunder.Dbf.Tests;

public sealed class MdxDigitCountTests : IDisposable
{
    private static readonly string FixtureRoot = FindFixtureRoot();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-mdx-digits-");

    public void Dispose() => _dir.Delete(recursive: true);

    // ---- the two rules ----------------------------------------------------------------------

    [Fact]
    public void Keys_count_digits_as_dBASE_and_Jet_write_them()
    {
        // dBASE: every numeric key SIMATIC Manager's engine wrote is the key Thunder.Dbf writes.
        var checkedKeys = 0;
        foreach (var mdx in Directory.EnumerateFiles(Path.Combine(FixtureRoot, "siemens-step7"), "*.MDX", SearchOption.AllDirectories))
        {
            foreach (var key in NumericKeys(mdx).Where(k => k.Leaf).Select(k => k.Key))
            {
                Assert.Equal(Convert.ToHexString(key.Bytes), Convert.ToHexString(MdxKeys.EncodeNumeric((decimal)MdxKeys.DecodeNumeric(key.Bytes), NumericKeyStyle.Dbase)));
                checkedKeys++;
            }
        }
        Assert.True(checkedKeys > 40, $"only {checkedKeys} numeric keys found");
        Assert.Equal(4, MdxKeys.DigitCount(MdxKeys.EncodeNumeric(15940m, NumericKeyStyle.Dbase)));

        // Jet, as measured on Windows: whole numbers 10, anything else its significant digits, zero 0.
        foreach (var (value, digits) in new[] { (1m, 10), (95m, 10), (12345m, 10), (-3m, 10), (100m, 10), (0.5m, 1), (1.25m, 3), (-2.5m, 2), (1234567.89m, 9), (0.00001m, 1), (0m, 0) })
            Assert.Equal(digits, MdxKeys.DigitCount(MdxKeys.EncodeNumeric(value, NumericKeyStyle.Jet)));
        Assert.Equal(MdxKeys.EncodeNumeric(95m, NumericKeyStyle.Jet), MdxKeys.EncodeNumeric(95d, NumericKeyStyle.Jet));
        Assert.Equal("362995", Convert.ToHexString(MdxKeys.EncodeNumeric(95m, NumericKeyStyle.Jet))[..6]);
    }

    [Fact]
    public void A_tag_dBASE_wrote_keeps_dBASEs_counts()
    {
        foreach (var file in new[] { "SYMLISTS.DBF", "SYMLISTS.MDX" })
            File.Copy(Path.Combine(FixtureRoot, "siemens-step7/YDBs", file), Path.Combine(_dir.FullName, file));
        var path = Path.Combine(_dir.FullName, "SYMLISTS.DBF");

        using (var table = Writable(path))
            table.UpdateFields(0, new Dictionary<string, object?> { ["_ID"] = 300m }); // 15940 says dBASE: 300 counts 1

        var key = Assert.Single(NumericKeys(Path.ChangeExtension(path, ".MDX")), k => k.Leaf && MdxKeys.DecodeNumeric(k.Key.Bytes) is 300m).Key;
        Assert.Equal(1, MdxKeys.DigitCount(key.Bytes));
    }

    [Fact]
    public void A_new_tag_counts_as_Jet_does()
    {
        var path = Table(DbfFieldDefinition.Numeric("AMT", 12, 2), Enumerable.Range(1, 300).Select(i => i / 4m));
        Assert.All(NumericKeys(Path.ChangeExtension(path, ".MDX")), k => Assert.Equal(JetCount(k.Key.Bytes), MdxKeys.DigitCount(k.Key.Bytes)));
    }

    // ---- what Jet finds -------------------------------------------------------------------

    [Fact]
    public void Jet_finds_the_record_after_a_deleted_holder_of_its_key()
    {
        // The repro from the Windows PC, on a table Jet indexed: delete #95, give #351 the number 95.
        var path = JetTable(rows: 400);
        using (var table = Writable(path))
        {
            table.Delete(94);
            table.UpdateFields(350, new Dictionary<string, object?> { ["ID"] = 95m });
        }
        Assert.True(JetFinds(path, 95m));
        AssertJetFindsEveryLiveRecord(path);
    }

    [Fact]
    public void Jet_finds_fractions_after_a_deleted_holder()
    {
        // Jet counts 3.25 as 3 digits and 7.5 as 2: padding them to 10, as Jet does whole numbers, hides them.
        var path = JetTable(rows: 400, field: DbfFieldDefinition.Numeric("ID", 10, 2), value: i => i / 4m);
        using (var table = Writable(path))
        {
            table.Delete(12); // 3.25
            table.Delete(29); // 7.5
            table.UpdateFields(300, new Dictionary<string, object?> { ["ID"] = 3.25m });
            table.UpdateFields(301, new Dictionary<string, object?> { ["ID"] = 7.5m });
        }
        Assert.True(JetFinds(path, 3.25m));
        Assert.True(JetFinds(path, 7.5m));
        AssertJetFindsEveryLiveRecord(path);
    }

    [Fact]
    public void Interior_keys_follow_a_leaf_whose_last_key_moves_out()
    {
        // The PC's second repro: the last key of several leaves moves far away, then one comes back.
        var path = JetTable(rows: 2_000);
        var lasts = InteriorValues(path).Take(4).ToList();
        using (var table = Writable(path))
        {
            foreach (var value in lasts)
                table.UpdateFields((int)value - 1, new Dictionary<string, object?> { ["ID"] = value + 900_000m });
            table.UpdateFields(1_499, new Dictionary<string, object?> { ["ID"] = lasts[0] });
        }
        AssertInteriorKeysMatchTheirChildren(path);
        Assert.True(JetFinds(path, lasts[0]));
        AssertJetFindsEveryLiveRecord(path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jet_finds_every_live_record_after_mixed_writes(bool exclusive)
    {
        // As the PC's power-cut writer does: updates, deletes, recalls and appends, many keys repeated.
        var path = JetTable(rows: 3_000);
        var random = new Random(7);
        using (var table = Writable(path, exclusive))
        {
            for (var i = 0; i < 2_000; i++)
            {
                var record = random.Next(table.RecordCount);
                switch (random.Next(4))
                {
                    case 0: table.Delete(record); break;
                    case 1: table.Recall(record); break;
                    case 2: table.Append((decimal)random.Next(1, 3_500), "appended"); break;
                    default: table.UpdateFields(record, new Dictionary<string, object?> { ["ID"] = (decimal)random.Next(1, 3_500) }); break;
                }
            }
        }
        AssertInteriorKeysMatchTheirChildren(path);
        AssertJetFindsEveryLiveRecord(path);
    }

    [Fact]
    public void Jet_finds_every_record_in_a_tag_Thunder_Dbf_made_after_deletes_and_appends()
    {
        // The PC's case 4: a deleted record holds each of these keys first.
        var path = Table(DbfFieldDefinition.Numeric("ID", 10, 0), Enumerable.Range(1, 2_000).Select(i => (decimal)i));
        using (var table = Writable(path))
        {
            for (var i = 0; i < 200; i++)
            {
                table.Delete(i);
                table.Append((decimal)(i + 1), "again");
            }
        }
        AssertJetFindsEveryLiveRecord(path);
    }

    [Fact]
    public void A_rebuild_puts_keys_earlier_versions_wrote_into_Jets_form()
    {
        var path = JetTable(rows: 400);
        // As if 0.4.0 had written some of them: its printed-digit count for 10, 20 ... 400.
        Patch(path, key => MdxKeys.DecodeNumeric(key) is decimal d && d % 10 == 0 ? MdxKeys.PrintedDigits(key) : null);

        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.RebuildIndex();

        Assert.All(NumericKeys(Path.ChangeExtension(path, ".MDX")), k => Assert.Equal(JetCount(k.Key.Bytes), MdxKeys.DigitCount(k.Key.Bytes)));
        AssertJetFindsEveryLiveRecord(path);
    }

    [Fact]
    public void Thunder_Dbfs_own_lookups_find_keys_in_any_form()
    {
        var path = JetTable(rows: 400);
        Patch(path, key => MdxKeys.DecodeNumeric(key) is decimal d && d % 3 == 0 ? MdxKeys.SignificantDigits(key) : null);
        using var table = DbfTable.Open(path);
        using var mdx = table.OpenProductionIndex()!;
        foreach (var id in new[] { 1m, 3m, 95m, 99m, 100m, 400m })
            Assert.Equal(id, Assert.Single(mdx["ID"].SeekExact(id)).Key);
    }

    [Fact]
    public void A_journal_note_0_4_0_left_keeps_the_records_own_key()
    {
        // 0.4.0 with UseJournal, killed after an update's new keys went in: #152's ID 152 -> 4635.
        var path = Table(DbfFieldDefinition.Numeric("ID", 10, 0), Enumerable.Range(1, 300).Select(i => (decimal)i));
        using (var table = Writable(path))
        {
            table.UseJournal = true;
            table.CrashPoint = point => { if (point == "new keys") throw new Killed(); };
            Assert.Throws<Killed>(() => table.UpdateFields(151, new Dictionary<string, object?> { ["ID"] = 4_635m }));
        }
        // Every key, and the note's, in 0.4.0's form (6152 with 4 digits, not Jet's 10).
        Patch(path, key => MdxKeys.PrintedDigits(key));
        var note = DbfJournal.Read(path)!;
        DbfJournal.Write(path, note with { Changes = [.. note.Changes.Select(c => c with { OldKey = Printed(c.OldKey), NewKey = Printed(c.NewKey) })] });

        using (var table = Writable(path))
            table.UpdateFields(0, new Dictionary<string, object?> { ["NAME"] = "next write" });

        using var reopened = DbfTable.Open(path);
        using var mdx = reopened.OpenProductionIndex()!;
        Assert.Equal(152, Assert.Single(mdx["ID"].SeekExact(152m)).RecordNumber); // the record's own key stays
        Assert.Empty(mdx["ID"].SeekExact(4_635m)); // the cut-off change's key goes
        Assert.Equal(300, mdx["ID"].Entries().Count());

        static byte[]? Printed(byte[]? key) =>
            key is null ? null : [key[0], (byte)((key[1] & 0x83) | (MdxKeys.PrintedDigits(key) << 2)), .. key[2..]];
    }

    private sealed class Killed : Exception;

    // ---- tables ---------------------------------------------------------------------------

    // A table with a tag on its number field, made by Thunder.Dbf.
    private string Table(DbfFieldDefinition numberField, IEnumerable<decimal> values)
    {
        var path = Path.Combine(_dir.FullName, $"T{Guid.NewGuid():N}"[..8] + ".DBF");
        using (var table = DbfTable.Create(path, [numberField, DbfFieldDefinition.Character("NAME", 20)]))
        {
            table.FlushWritesToDisk = false;
            foreach (var value in values)
                table.Append(value, "row");
        }
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.CreateIndexTag("ID", numberField.Name.ToLowerInvariant());
        return path;
    }

    // Writes aren't forced to disk here: it changes no byte, only the time the tests take.
    private static DbfTable Writable(string path, bool exclusive = false)
    {
        var table = DbfTable.Open(path, writable: true, exclusive: exclusive);
        table.FlushWritesToDisk = false;
        return table;
    }

    // A table whose tag Jet might have written: every key in Jet's form.
    private string JetTable(int rows, DbfFieldDefinition? field = null, Func<int, decimal>? value = null)
    {
        var path = Table(field ?? DbfFieldDefinition.Numeric("ID", 20, 5), Enumerable.Range(1, rows).Select(value ?? (i => i)));
        Patch(path, key => JetCount(key));
        return path;
    }

    private static int JetCount(byte[] key) => MdxKeys.DigitCount(MdxKeys.EncodeNumeric((decimal)MdxKeys.DecodeNumeric(key), NumericKeyStyle.Jet));

    // Rewrites the digit count of every numeric key, leaf and interior, as another writer would have.
    private static void Patch(string path, Func<byte[], int?> count)
    {
        var mdx = Path.ChangeExtension(path, ".MDX");
        var bytes = File.ReadAllBytes(mdx);
        foreach (var (_, key, _) in NumericKeys(mdx))
        {
            if (count(key.Bytes) is { } digits)
                bytes[key.Offset + 1] = (byte)((bytes[key.Offset + 1] & 0x83) | (digits << 2));
        }
        File.WriteAllBytes(mdx, bytes);
    }

    // ---- Jet, as the Windows PC measured it --------------------------------------------------

    // Down the tree to the first child whose key is at or above the value (the last child past them
    // all), never on to the next leaf; there, from the first key at or above it, every key whose bytes
    // equal Jet's own key for the value. True if one of those records is live.
    private static bool JetFinds(string path, decimal value) => new JetView(path).Finds(value);

    private static void AssertJetFindsEveryLiveRecord(string path)
    {
        var jet = new JetView(path);
        var live = jet.LiveValues();
        var missed = live.Where(v => !jet.Finds(v)).ToList();
        Assert.True(missed.Count == 0, $"Jet would miss {missed.Count} of {live.Count}: {string.Join(", ", missed.Take(10))}");
    }

    // The .mdx and which records are deleted, read once.
    private sealed class JetView
    {
        private readonly byte[] _mdx;
        private readonly int _header, _itemLength;
        private readonly bool[] _deleted;
        private readonly decimal[] _values;

        public JetView(string path)
        {
            _mdx = File.ReadAllBytes(Path.ChangeExtension(path, ".MDX"));
            (_header, _itemLength) = Tag(_mdx);
            using var table = DbfTable.Open(path);
            var records = table.ReadAll().ToList();
            _deleted = [.. records.Select(r => r.IsDeleted)];
            _values = [.. records.Select(r => Convert.ToDecimal(r[0]))];
        }

        public List<decimal> LiveValues() => [.. _values.Where((_, i) => !_deleted[i]).Distinct()];

        public bool Finds(decimal value)
        {
            var probe = MdxKeys.EncodeNumeric(value, NumericKeyStyle.Jet);
            var page = BinaryPrimitives.ReadInt32LittleEndian(_mdx.AsSpan(_header));
            while (true)
            {
                var node = page * 512;
                var count = BinaryPrimitives.ReadInt32LittleEndian(_mdx.AsSpan(node));
                var lastPointer = BinaryPrimitives.ReadInt32LittleEndian(_mdx.AsSpan(node + 8 + count * _itemLength));
                var at = 0;
                while (at < count && MdxKeys.CompareNumeric(KeyAt(_mdx, node, at, _itemLength), probe) < 0)
                    at++;
                if (lastPointer != 0)
                {
                    page = at < count ? BinaryPrimitives.ReadInt32LittleEndian(_mdx.AsSpan(node + 8 + at * _itemLength)) : lastPointer;
                    continue;
                }
                for (var i = at; i < count && KeyAt(_mdx, node, i, _itemLength).SequenceEqual(probe); i++)
                {
                    if (!_deleted[BinaryPrimitives.ReadInt32LittleEndian(_mdx.AsSpan(node + 8 + i * _itemLength)) - 1])
                        return true;
                }
                return false;
            }
        }
    }

    private static void AssertInteriorKeysMatchTheirChildren(string path)
    {
        var mdx = File.ReadAllBytes(Path.ChangeExtension(path, ".MDX"));
        var (header, itemLength) = Tag(mdx);
        var wrong = new List<string>();
        LastKey(BinaryPrimitives.ReadInt32LittleEndian(mdx.AsSpan(header)));
        Assert.True(wrong.Count == 0, $"{wrong.Count} interior keys aren't their child's last key: {string.Join("; ", wrong.Take(5))}");

        byte[] LastKey(int page)
        {
            var node = page * 512;
            var count = BinaryPrimitives.ReadInt32LittleEndian(mdx.AsSpan(node));
            var lastPointer = BinaryPrimitives.ReadInt32LittleEndian(mdx.AsSpan(node + 8 + count * itemLength));
            if (lastPointer == 0)
                return KeyAt(mdx, node, count - 1, itemLength).ToArray();
            for (var i = 0; i < count; i++)
            {
                var childLast = LastKey(BinaryPrimitives.ReadInt32LittleEndian(mdx.AsSpan(node + 8 + i * itemLength)));
                if (!KeyAt(mdx, node, i, itemLength).SequenceEqual(childLast))
                    wrong.Add($"page {page} key {i} = {MdxKeys.DecodeNumeric(KeyAt(mdx, node, i, itemLength))}, child ends at {MdxKeys.DecodeNumeric(childLast)}");
            }
            return LastKey(lastPointer);
        }
    }

    // The values of the interior keys, smallest first: each is the last key of a leaf (or subtree).
    private static IEnumerable<decimal> InteriorValues(string path) =>
        NumericKeys(Path.ChangeExtension(path, ".MDX")).Where(k => !k.Leaf).Select(k => Convert.ToDecimal(MdxKeys.DecodeNumeric(k.Key.Bytes))).Order();

    // ---- raw pages ------------------------------------------------------------------------

    private readonly record struct RawKey(int Offset, byte[] Bytes);

    private static (int Header, int ItemLength) Tag(byte[] mdx)
    {
        var header = BinaryPrimitives.ReadInt32LittleEndian(mdx.AsSpan(544)) * 512;
        return (header, BinaryPrimitives.ReadUInt16LittleEndian(mdx.AsSpan(header + 18)));
    }

    private static ReadOnlySpan<byte> KeyAt(byte[] mdx, int node, int i, int itemLength) => mdx.AsSpan(node + 8 + i * itemLength + 4, MdxKeys.NumericKeyLength);

    // Every numeric key in the file, read straight from the pages: (tag, key, whether it's in a leaf).
    private static IEnumerable<(string Tag, RawKey Key, bool Leaf)> NumericKeys(string mdx)
    {
        var file = File.ReadAllBytes(mdx);
        var tags = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(28));
        var entryLength = file[26] == 0 ? 32 : file[26];
        for (var t = 0; t < tags; t++)
        {
            var entry = 544 + t * entryLength;
            var header = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(entry)) * 512;
            var name = System.Text.Encoding.ASCII.GetString(file, entry + 4, 11).TrimEnd('\0', ' ');
            if (file[header + 9] != 'N')
                continue;
            var itemLength = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(header + 18));
            var pages = new Stack<int>([BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(header))]);
            while (pages.TryPop(out var page))
            {
                var node = page * 512;
                var count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(node));
                var leaf = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(node + 8 + count * itemLength)) == 0;
                for (var i = 0; i < count; i++)
                {
                    var at = node + 8 + i * itemLength + 4;
                    yield return (name, new RawKey(at, file[at..(at + MdxKeys.NumericKeyLength)]), leaf);
                }
                if (!leaf)
                {
                    for (var i = 0; i <= count; i++)
                        pages.Push(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(node + 8 + i * itemLength)));
                }
            }
        }
    }

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
