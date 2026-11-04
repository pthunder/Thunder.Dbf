//      {^2024-03-11 03:42:47}        ProjectThunder :: Thunder.Dbf tests
//        = 2460381.15471...          A .cdx tag on a Visual FoxPro datetime field keys whole seconds,
//        not  2460381  (a date)      so seeking and reading it keep the time of day.

namespace Thunder.Dbf.Tests;

public sealed class CdxDateTimeTagTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-cdx-datetime-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static readonly DateTime[] Stamps =
    [
        new(2024, 3, 11, 3, 42, 47),
        new(2024, 3, 11, 9, 9, 28),
        new(1978, 2, 10, 16, 40, 55),
        new(2024, 3, 11, 0, 0, 0),
    ];

    private string TableWithStampTag()
    {
        var path = Path.Combine(_dir.FullName, "TT.DBF");
        using (var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Integer("ID"), DbfFieldDefinition.DateTime("STAMP")]))
        {
            for (var i = 0; i < Stamps.Length; i++)
                table.Append(i + 1, Stamps[i]);
        }
        using (var table = DbfTable.Open(path, writable: true, exclusive: true, maintainFoxProIndexes: true))
            table.CreateIndexTag("STAMP", "stamp");
        return path;
    }

    [Fact]
    public void A_datetime_tag_seeks_to_the_second_and_reads_its_keys_back_with_their_time()
    {
        var path = TableWithStampTag();
        using var table = DbfTable.Open(path);
        using var cdx = table.OpenStructuralIndex()!;
        var tag = cdx["STAMP"];

        Assert.Equal('T', tag.KeyType);
        Assert.Equal(Stamps.Order(), tag.Entries().Select(e => (DateTime)e.Key!));
        foreach (var (stamp, id) in Stamps.Select((s, i) => (s, i + 1)))
            Assert.Equal(id, table.Read(Assert.Single(tag.SeekExact(stamp)).RecordNumber - 1)["ID"]);
        Assert.Empty(tag.SeekExact(new DateTime(2024, 3, 11, 3, 42, 48)));
        Assert.Equal(new DateTime(2024, 3, 11, 3, 42, 47), (DateTime)tag.Seek(new DateTime(2024, 3, 11, 1, 0, 0)).First().Key!); // the next one at or after
        Assert.Equal(new DateTime(2024, 3, 11), (DateTime)Assert.Single(tag.SeekExact(new DateOnly(2024, 3, 11))).Key!); // a date is its midnight
    }

    [Fact]
    public void A_datetime_VFP_stored_a_millisecond_short_is_found_by_its_second()
    {
        // VFP stores 03:42:47 as 13,366,999 ms; the key is the second either way.
        var path = TableWithStampTag();
        using (var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true))
            table.UpdateFields(0, new Dictionary<string, object?> { ["STAMP"] = new DateTime(2024, 3, 11, 3, 42, 46, 999) });

        using var reopened = DbfTable.Open(path);
        using var cdx = reopened.OpenStructuralIndex()!;
        Assert.Equal(new DateTime(2024, 3, 11, 3, 42, 47), reopened.Read(0)["STAMP"]);
        Assert.Equal(1, reopened.Read(Assert.Single(cdx["STAMP"].SeekExact(new DateTime(2024, 3, 11, 3, 42, 47))).RecordNumber - 1)["ID"]);
    }
}
