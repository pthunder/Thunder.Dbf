//      INDEX ON ... TAG ...  in Visual FoxPro 9      vs      CreateIndexTag here
//              |                                                  |
//              +---------------> the same keys, in the same order <-+
//
//   ProjectThunder :: Thunder.Dbf tests
//   vfp9/kinds.CDX holds one tag of every kind, built by Visual FoxPro 9 itself. Each is made again
//   here on a copy of the table without its .cdx, and has to come out holding exactly what VFP's did.

using System.Buffers.Binary;

namespace Thunder.Dbf.Tests;

public sealed class CdxCreateTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-cdxnew-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static string Fixture(string relative) => IndexTestSupport.Fixture(relative);

    // kinds.DBF and .FPT without the .cdx, and with the header's "has a structural .cdx" bit cleared.
    private string BareKinds()
    {
        foreach (var file in new[] { "kinds.DBF", "kinds.FPT" })
            File.Copy(Fixture("vfp9/" + file), Path.Combine(_dir.FullName, file));
        var path = Path.Combine(_dir.FullName, "kinds.DBF");
        var bytes = File.ReadAllBytes(path);
        bytes[28] &= unchecked((byte)~0x01);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static DbfTable OpenToIndex(string path) => DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true);

    public static TheoryData<string, string, string?, bool, bool, bool> VfpTags() => new()
    {
        // name, expression, FOR, unique, descending, candidate: as kinds.prg made them
        { "CODE", "code", null, false, false, false },
        { "UNAME", "UPPER(name)", null, false, false, false },
        { "NAMED", "name", null, false, true, false },
        { "AMOUNT", "amount", null, false, false, false },
        { "PRICE", "price", null, false, false, false },
        { "SCORE", "score", null, false, false, false },
        { "WHEN", "when", null, false, false, false },
        { "STAMP", "stamp", null, false, false, false },
        { "FLAG", "flag", null, false, false, false },
        { "QTY", "qty", null, false, false, false },
        { "CODEWHEN", "code+DTOS(when)", null, false, false, false },
        { "QTYCODE", "STR(qty,6)+code", null, false, false, false },
        { "UCODE", "code", null, true, false, false },
        { "LIVE", "id", "status=\"A\"", false, false, false },
        { "OPT", "opt", null, false, false, false },
        { "ID", "id", null, false, false, true },
    };

    [Theory]
    [MemberData(nameof(VfpTags))]
    public void A_tag_made_here_holds_what_Visual_FoxPros_INDEX_ON_made(string name, string expression, string? filter, bool unique, bool descending, bool candidate)
    {
        var path = BareKinds();
        using (var table = OpenToIndex(path))
        {
            table.CreateIndexTag(name, expression, filter, unique, descending, candidate);
            Assert.True(table.MaintainsIndex);
        }

        using var check = DbfTable.Open(path);
        using var ours = CdxIndex.Open(CdxIndex.FindFor(path)!, table: check);
        using var theirs = CdxIndex.Open(Fixture("vfp9/kinds.CDX"), table: check);
        var made = ours[name];
        var vfp = theirs[name];
        Assert.Equal(vfp.KeyLength, made.KeyLength);
        Assert.Equal(vfp.IsDescending, made.IsDescending);
        Assert.Equal(vfp.IsUnique, made.IsUnique);
        Assert.Equal(vfp.IsCandidate, made.IsCandidate);
        Assert.Equal(vfp.ForExpression, made.ForExpression);
        Assert.Equal(vfp.RawEntries().Select(e => (Convert.ToHexString(e.Key), e.Record)), made.RawEntries().Select(e => (Convert.ToHexString(e.Key), e.Record)));

        // The header's options byte and the root right after the header, as VFP lays a tag out.
        var vfpHeader = File.ReadAllBytes(Fixture("vfp9/kinds.CDX")).AsSpan((int)vfp.HeaderOffset, 1024);
        var ourHeader = SharedFile.ReadAllBytes(CdxIndex.FindFor(path)!).AsSpan((int)made.HeaderOffset, 1024);
        Assert.Equal(vfpHeader[14], ourHeader[14]);
        Assert.Equal(made.HeaderOffset + 1024, BinaryPrimitives.ReadUInt32LittleEndian(ourHeader));
    }

    [Fact]
    public void Every_kind_in_one_cdx_is_kept_up_to_date_afterwards()
    {
        var path = BareKinds();
        using (var table = OpenToIndex(path))
        {
            foreach (var row in VfpTags())
                table.CreateIndexTag((string)row[0], (string)row[1], (string?)row[2], (bool)row[3], (bool)row[4], (bool)row[5]);
            var template = table.Read(0).Values.ToArray();
            template[0] = 5000;   // ID is a candidate key: a value no record has
            table.Append(template);
            table.UpdateFields(3, new Dictionary<string, object?> { ["CODE"] = "C99999", ["PRICE"] = 12.5m, ["STAMP"] = new DateTime(2026, 10, 31, 12, 30, 15) });
            table.Delete(5);
        }

        using var check = DbfTable.Open(path);
        using var ours = CdxIndex.Open(CdxIndex.FindFor(path)!, table: check);
        Assert.Equal(VfpTags().Select(r => (string)r[0]), ours.Tags.Select(t => t.Name));
        foreach (var tag in ours.Tags)
        {
            var entries = tag.RawEntries().Select(e => (Key: Convert.ToHexString(e.Key), e.Record)).ToList();
            if (tag.IsUnique)
            {
                // Visual FoxPro's UNIQUE: each key once, held by a record that has it (the first that
                // got there, not necessarily the lowest; nobody is promoted when the holder leaves).
                Assert.Equal(entries.Count, entries.Select(e => e.Key).Distinct().Count());
                Assert.All(entries, e => Assert.Equal(e.Key, Convert.ToHexString(tag.KeyBytes(check.Read(e.Record - 1)))));
                continue;
            }
            var expected = check.ReadAll().Where(tag.Includes).Select(r => (Key: Convert.ToHexString(tag.KeyBytes(r)), Record: r.RecordNumber))
                .OrderBy(e => e.Key, StringComparer.Ordinal).ThenBy(e => e.Record).ToList();
            Assert.Equal(expected, entries);
        }
    }

    [Fact]
    public void Expressions_this_library_cant_key_exactly_are_refused_and_change_nothing()
    {
        var path = BareKinds();
        using (var table = OpenToIndex(path))
            table.CreateIndexTag("CODE", "code");
        var cdx = CdxIndex.FindFor(path)!;
        var before = SharedFile.ReadAllBytes(cdx);

        using var again = OpenToIndex(path);
        Assert.Equal(DbfErrorCode.InvalidValue, Assert.Throws<DbfException>(() => again.CreateIndexTag("TWICE", "amount*2")).Code);
        Assert.Equal(DbfErrorCode.InvalidValue, Assert.Throws<DbfException>(() => again.CreateIndexTag("SUBS", "SUBSTR(name,1,3)")).Code);
        var duplicate = Assert.Throws<DbfException>(() => again.CreateIndexTag("CCODE", "code", null, false, false, candidate: true));
        Assert.Contains("same key", duplicate.Message);
        Assert.Equal(DbfErrorCode.InvalidValue, Assert.Throws<DbfException>(() => again.CreateIndexTag("CODE", "name")).Code);
        again.Dispose();
        Assert.Equal(before, SharedFile.ReadAllBytes(cdx));
    }

    [Fact]
    public void Creating_a_cdx_tag_needs_exclusive_use_and_the_FoxPro_index_preview()
    {
        var path = BareKinds();
        using (var shared = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true))
            Assert.Equal(DbfErrorCode.ExclusiveUseRequired, Assert.Throws<DbfException>(() => shared.CreateIndexTag("CODE", "code")).Code);
        using (var plain = DbfTable.Open(path, writable: true, exclusive: true))
            Assert.Equal(DbfErrorCode.UnsupportedFormat, Assert.Throws<DbfException>(() => plain.CreateIndexTag("CODE", "code")).Code);
        Assert.Null(CdxIndex.FindFor(path));
    }

    // A refusal writes nothing: not even a new .cdx, or the header flag that would name it.
    [Fact]
    public void A_refused_tag_on_a_table_without_a_cdx_leaves_no_cdx_behind()
    {
        var path = BareKinds();
        var before = File.ReadAllBytes(path);
        using (var table = OpenToIndex(path))
        {
            Assert.Equal(DbfErrorCode.InvalidValue, Assert.Throws<DbfException>(() => table.CreateIndexTag("TWICE", "amount*2")).Code);
            Assert.Equal(DbfErrorCode.InvalidValue, Assert.Throws<DbfException>(() => table.CreateIndexTag("CCODE", "code", null, false, false, candidate: true)).Code);
            Assert.False(table.MaintainsIndex);
        }
        Assert.Null(CdxIndex.FindFor(path));
        Assert.Equal(before[28], SharedFile.ReadAllBytes(path)[28]);
    }

    // Found by the Visual FoxPro stress tests: a tag on a C(254) field failed inside the tree code.
    [Fact]
    public void Keys_longer_than_FoxPro_allows_are_refused_before_anything_is_written()
    {
        var path = BareKinds();
        using var table = OpenToIndex(path);
        var error = Assert.Throws<DbfException>(() => table.CreateIndexTag("LONG", string.Join("+", Enumerable.Repeat("name", 9))));
        Assert.Equal(DbfErrorCode.InvalidValue, error.Code);
        Assert.Contains("240", error.Message);
        Assert.Null(CdxIndex.FindFor(path));

        table.CreateIndexTag("LONGEST", string.Join("+", Enumerable.Repeat("name", 8))); // 240 bytes: the most FoxPro allows
        using var made = CdxIndex.Open(new MemoryStream(SharedFile.ReadAllBytes(CdxIndex.FindFor(path)!)));
        Assert.Equal(240, made["LONGEST"].KeyLength);
    }

    // Visual FoxPro's UPPER() beyond ASCII, measured byte for byte in VFP 9 running in code page 1250:
    // .NET's capital when the code page has it, the byte kept when it doesn't (µ, whose capital is
    // Greek; ß, which has no one-letter capital).
    [Fact]
    public void UPPER_keys_in_a_checked_code_page_take_Visual_FoxPros_capitals()
    {
        var path = BareKinds();
        var bytes = File.ReadAllBytes(path);
        bytes[29] = 0xC8; // Windows-1250, Central European
        File.WriteAllBytes(path, bytes);
        using (var table = OpenToIndex(path))
        {
            Assert.Equal(1250, table.Encoding.CodePage);
            var row = table.Read(0).Values.ToArray();
            row[0] = 5000;
            row[2] = "čaj šum µm ß";
            table.Append(row);
            table.CreateIndexTag("UNAME", "UPPER(name)");
        }

        using var check = DbfTable.Open(path);
        using var index = CdxIndex.Open(CdxIndex.FindFor(path)!, table: check);
        var key = index["UNAME"].RawEntries().Single(e => e.Record == check.RecordCount).Key;
        var cp1250 = System.Text.Encoding.GetEncoding(1250);
        Assert.Equal(cp1250.GetBytes("ČAJ ŠUM µM ß".PadRight(30)), key);
        Assert.Equal([0xC8, 0x41, 0x4A, 0x20, 0x8A], key[..5]); // Č A J, space, Š
    }

    // VFP 9 SP2's UPPER() per code page, as measured byte by byte with VFP running in each: one ordinary
    // capital, and every byte where VFP differs from the rule (.NET's capital if the code page has it).
    [Theory]
    [InlineData(852, 0xA5, 0xA4)]   // ą → Ą
    [InlineData(1250, 0x9A, 0x8A)]  // š → Š
    [InlineData(1250, 0xB5, 0xB5)]  // µ kept: its capital is Greek
    [InlineData(1251, 0xB8, 0xA8)]  // ё → Ё
    [InlineData(1251, 0xB5, 0xB5)]  // µ kept
    [InlineData(866, 0xE0, 0x90)]   // р → Р
    [InlineData(1257, 0xE0, 0xC0)]  // ą → Ą
    [InlineData(1252, 0xE9, 0xC9)]  // é → É
    [InlineData(1252, 0x9E, 0x9E)]  // ž: VFP leaves it lower case
    [InlineData(1252, 0xDF, 0xDF)]  // ß kept: no one-letter capital
    [InlineData(437, 0x82, 0x90)]   // é → É
    [InlineData(437, 0xE5, 0xE5)]   // σ: VFP leaves it
    [InlineData(437, 0xED, 0xED)]   // φ: VFP leaves it
    [InlineData(1253, 0xE1, 0xC1)]  // α → Α
    [InlineData(1253, 0xB5, 0xB5)]  // µ kept
    [InlineData(1253, 0xF2, 0xF2)]  // final ς: VFP leaves it
    [InlineData(1254, 0xE7, 0xC7)]  // ç → Ç
    [InlineData(1254, 0xA2, 0xC2)]  // ¢ → Â, as VFP does
    [InlineData(1254, 0xE2, 0xE2)]  // â: VFP leaves it
    [InlineData(1254, 0xF8, 0xF8)]  // ø: VFP leaves it
    [InlineData(850, 0x82, 0x90)]   // é → É
    [InlineData(850, 0x8B, 0x8B)]   // ï: VFP leaves it
    [InlineData(850, 0xB8, 0xD8)]   // VFP's own mapping
    [InlineData(850, 0xE2, 0xED)]   // VFP's own mapping
    [InlineData(850, 0xEC, 0xEC)]   // ý: VFP leaves it
    public void UPPER_takes_Visual_FoxPros_capital_byte_for_byte(int codePage, int lower, int upper)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var table = FoxProUpper.For(System.Text.Encoding.GetEncoding(codePage));
        Assert.NotNull(table);
        Assert.Equal((byte)upper, table[lower]);
        Assert.Equal((byte)'A', table['a']);
    }

    // A code page whose upper case hasn't been checked against VFP keeps refusing non-ASCII UPPER() keys.
    [Fact]
    public void UPPER_keys_over_non_ASCII_text_in_an_unchecked_code_page_are_refused()
    {
        var path = BareKinds();
        var bytes = File.ReadAllBytes(path);
        bytes[29] = 0x7E; // Windows-1256, Arabic: VFP's UPPER() there hasn't been measured
        File.WriteAllBytes(path, bytes);
        using var table = OpenToIndex(path);
        Assert.Equal(1256, table.Encoding.CodePage);
        var row = table.Read(0).Values.ToArray();
        row[0] = 5000;
        row[2] = "café";
        table.Append(row);
        var error = Assert.Throws<DbfException>(() => table.CreateIndexTag("UNAME", "UPPER(name)"));
        Assert.Contains("checked against Visual FoxPro", error.Message);
    }

    [Fact]
    public void A_FOR_filter_must_be_logical_and_expressions_must_fit_the_code_page()
    {
        var path = BareKinds();
        using var table = OpenToIndex(path);
        Assert.Contains("logical", Assert.Throws<DbfException>(() => table.CreateIndexTag("LIVE", "id", forExpression: "id")).Message);
        Assert.Contains("code page", Assert.Throws<DbfException>(() => table.CreateIndexTag("SNOW", "code", forExpression: "status=\"\u2603\"")).Message);
        Assert.Null(CdxIndex.FindFor(path));
    }
}
