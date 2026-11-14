//      SEEK null     .NULL. + blank      ProjectThunder :: Thunder.Dbf tests
//      N(10,2)       12345678.9          What Visual FoxPro stores and finds that Thunder.Dbf didn't:
//                                        blank dates on nullable tags, decimals dropped to fit.

using Thunder.Dbf;

namespace Thunder.Dbf.Tests;

// Two things Visual FoxPro 9 does that Thunder.Dbf now matches: it finds blank (not .NULL.) dates on nullable
// date and datetime tags, it fits a number into its field by dropping decimals, and it rounds
// currency half away from zero.
public sealed class FoxProFitTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-foxfit-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void Seeking_null_on_a_nullable_date_tag_finds_blank_dates_too()
    {
        var path = Path.Combine(_dir.FullName, "dates.dbf");
        var date = new DateOnly(2026, 11, 13);
        var moment = new DateTime(2026, 11, 13, 8, 30, 0);
        using (var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro,
            [DbfFieldDefinition.Date("NDAT") with { Nullable = true }, DbfFieldDefinition.DateTime("NDTM") with { Nullable = true }]))
        {
            for (var i = 0; i < 12; i++)
                table.Append(i % 3 == 0 ? null : date, i % 3 == 0 ? null : moment);
        }

        // Rows 1, 4, 7 and 10 become blank, not .NULL.: VFP's empty date is spaces, its empty datetime zeros.
        var blankRows = new[] { 1, 4, 7, 10 };
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        using (var table = DbfTable.Open(stream, leaveOpen: true))
        {
            var dat = table.Fields.Single(f => f.Name == "NDAT");
            var dtm = table.Fields.Single(f => f.Name == "NDTM");
            foreach (var row in blankRows)
            {
                var at = table.Header.HeaderLength + (long)row * table.Header.RecordLength;
                stream.Position = at + dat.Offset;
                stream.Write(new byte[dat.Length].Select(_ => (byte)' ').ToArray());
                stream.Position = at + dtm.Offset;
                stream.Write(new byte[dtm.Length]);
            }
        }

        using (var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true))
        {
            Assert.False(table.Read(1).IsNull(0));
            Assert.Null(table.Read(1)[0]);
            table.CreateIndexTag("NDAT", "NDAT");
            table.CreateIndexTag("NDTM", "NDTM");
            table.CreateIndexTag("NDATD", "NDAT", descending: true);
        }

        using (var table = DbfTable.Open(path))
        using (var cdx = table.OpenStructuralIndex()!)
        {
            var expected = new[] { 1, 2, 4, 5, 7, 8, 10, 11 }; // record numbers count from 1
            foreach (var tag in new[] { "NDAT", "NDTM", "NDATD" })
            {
                var found = cdx[tag].SeekExact(null).Select(e => e.RecordNumber).ToList();
                Assert.Equal(expected, found.Order());
                Assert.Equal(found.Count, found.Distinct().Count());
                Assert.All(cdx[tag].SeekExact(null), e => Assert.Null(e.Key));
            }
            Assert.Equal(4, cdx["NDAT"].SeekExact(date).Count());
            Assert.Equal(4, cdx["NDTM"].SeekExact(moment).Count());
        }
    }

    [Fact]
    public void A_number_whose_decimals_do_not_fit_keeps_the_decimals_that_do()
    {
        var path = Path.Combine(_dir.FullName, "nums.dbf");
        using var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Numeric("NNUM", 10, 2)]);

        table.Append(12345678.9m);
        table.Append(12345678.95m);
        table.Append(-1234567.891m);
        table.Append(123456789.4);
        table.Append(-12.5m);

        Assert.Equal(12345678.9m, Convert.ToDecimal(table.Read(0)[0]));
        Assert.Equal(12345679.0m, Convert.ToDecimal(table.Read(1)[0]));
        Assert.Equal(-1234567.9m, Convert.ToDecimal(table.Read(2)[0]));
        Assert.Equal(123456789m, Convert.ToDecimal(table.Read(3)[0]));
        Assert.Equal(-12.5m, Convert.ToDecimal(table.Read(4)[0]));

        // Rounding to fewer decimals can carry into the whole part; the rounded value is what's stored.
        table.Append(99999999.96m);
        table.Append(1234567890.5m);
        Assert.Equal(100000000m, Convert.ToDecimal(table.Read(5)[0]));
        Assert.Equal(1234567891m, Convert.ToDecimal(table.Read(6)[0]));

        // A whole part that doesn't fit still overflows.
        Assert.Throws<OverflowException>(() => table.Append(12345678901m));
    }

    [Fact]
    public void Currency_rounds_half_away_from_zero_when_written_and_sought()
    {
        var path = Path.Combine(_dir.FullName, "money.dbf");
        using (var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Currency("CUR")]))
        {
            table.Append(1.00005m);
            table.Append(1.00025m);
            table.Append(-1.00005m);
            table.Append(1.00015m);

            Assert.Equal(1.0001m, table.Read(0)[0]);
            Assert.Equal(1.0003m, table.Read(1)[0]);
            Assert.Equal(-1.0001m, table.Read(2)[0]);
            Assert.Equal(1.0002m, table.Read(3)[0]);
        }

        using (var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true))
            table.CreateIndexTag("CUR", "CUR");

        using (var table = DbfTable.Open(path))
        using (var cdx = table.OpenStructuralIndex()!)
        {
            Assert.Equal([1], cdx["CUR"].SeekExact(1.00005m).Select(e => e.RecordNumber));
            Assert.Equal([2], cdx["CUR"].SeekExact(1.00025m).Select(e => e.RecordNumber));
            Assert.Equal([3], cdx["CUR"].SeekExact(-1.00005m).Select(e => e.RecordNumber));
        }
    }
}
