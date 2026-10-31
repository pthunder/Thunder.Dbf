//      record  ->  new Change { Id = 2, User = "admin", Level = 1 }      ProjectThunder :: Thunder.Dbf tests
//      LINQ    ->  .Where(c => c.Id == 2)  -->  seek ID_TAG              Typed records and LINQ: the same answer
//                  .Where(c => c.Level > 0) -->  scan                    whether an index helps or not.

namespace Thunder.Dbf.Tests;

public sealed class TypedRecordTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-typed-");
    private readonly string _path;

    public TypedRecordTests()
    {
        foreach (var file in new[] { "CHANGELOG.DBF", "CHANGELOG.DBT", "CHANGELOG.MDX" })
            File.Copy(Path.Combine(Fixtures.Root, "siemens-step7/siprom", file), Path.Combine(_dir.FullName, file));
        _path = Path.Combine(_dir.FullName, "CHANGELOG.DBF");
    }

    public void Dispose() => _dir.Delete(recursive: true);

    public enum Severity { Info = 0, Warning = 1, Error = 2 }

    public sealed class Change
    {
        public int Id { get; set; }
        public string? User { get; set; }
        [DbfField("LEVEL")] public Severity Severity { get; set; }
        public long? ObjectId { get; set; }
        public string? Event { get; set; }                // a memo
        [DbfRecordNumber] public int Recno { get; set; }
        [DbfDeleted] public bool Deleted { get; set; }
        [DbfIgnore] public string Note { get; set; } = "not a field";
        public string? NotAField { get; set; }            // plain extra property: ignored
    }

    [Fact]
    public void Records_read_as_objects_with_converted_values()
    {
        using var table = DbfTable.Open(_path);
        var all = table.ReadAll<Change>().ToList();

        Assert.Equal(table.ReadAll().Count(r => !r.IsDeleted), all.Count);
        var first = table.Read<Change>(0);
        Assert.Equal(Convert.ToInt32(table.Read(0)["ID"]), first.Id);
        Assert.Equal(table.Read(0)["USER"], first.User);
        Assert.Equal(1, first.Recno);
        Assert.Equal("not a field", first.Note);
        Assert.Null(first.NotAField);
    }

    [Fact]
    public void A_typo_in_DbfField_is_reported_not_ignored()
    {
        using var table = DbfTable.Open(_path);
        var error = Assert.Throws<ArgumentException>(() => table.ReadAll<Typo>().ToList());
        Assert.Contains("NOPE", error.Message);
    }

    public sealed class Typo
    {
        [DbfField("NOPE")] public int Id { get; set; }
    }

    [Fact]
    public void Objects_append_and_update_through_the_index()
    {
        using (var table = DbfTable.Open(_path, writable: true))
        {
            var index = table.AppendRecord(new Change { Id = 900, User = "typed", Severity = Severity.Error, ObjectId = 7 });
            var added = table.Read<Change>(index);
            Assert.Equal((900, "typed", Severity.Error), (added.Id, added.User, added.Severity));

            added.User = "renamed";
            table.UpdateRecord(index, added);
        }

        using var reopened = DbfTable.Open(_path);
        using var mdx = reopened.OpenProductionIndex()!;
        Assert.Equal("renamed", reopened.Seek(mdx["ID_TAG"], 900m)!["USER"]);
        Assert.Equal(4, reopened.Seek(mdx["USER_TAG"], "renamed")!.RecordNumber);
    }

    [Fact]
    public void An_equality_on_an_indexed_field_seeks_and_gives_the_scans_answer()
    {
        using var table = DbfTable.Open(_path);
        var objectId = table.Read<Change>(1).ObjectId;

        var query = table.AsQueryable<Change>().Where(c => c.ObjectId == objectId);
        Assert.StartsWith("seek CHANGELOG index tag OBJECTID_T for OBJECTID = ", query.Explain());
        Assert.Equal(table.ReadAll<Change>().Where(c => c.ObjectId == objectId).Select(c => c.Recno), query.Select(c => c.Recno).ToList());

        // ID_TAG is UNIQUE: it keeps one record per key, so seeking through it could miss rows.
        var id = table.Read<Change>(1).Id;
        Assert.StartsWith("scan", table.AsQueryable<Change>().Where(c => c.Id == id).Explain());

        var user = table.Read<Change>(0).User;
        var byUser = table.AsQueryable<Change>().Where(c => c.User == user && c.Severity >= Severity.Info);
        Assert.Contains("USER_TAG", byUser.Explain());
        Assert.Equal(table.ReadAll<Change>().Count(c => c.User == user), byUser.Count());
    }

    [Fact]
    public void Everything_else_scans_and_still_composes()
    {
        using var table = DbfTable.Open(_path);

        var query = table.AsQueryable<Change>().Where(c => c.Id > 0).OrderByDescending(c => c.Id).Select(c => c.Id);
        Assert.StartsWith("scan CHANGELOG", query.Explain());
        Assert.Equal(table.ReadAll<Change>().Where(c => c.Id > 0).OrderByDescending(c => c.Id).Select(c => c.Id), query.ToList());
        Assert.Equal(table.ReadAll<Change>().Max(c => c.Id), table.AsQueryable<Change>().Max(c => c.Id));
        Assert.False(table.AsQueryable<Change>().Any(c => c.Id == -1));
    }

    [Fact]
    public void A_condition_after_Skip_or_Select_isnt_used_to_seek()
    {
        using var table = DbfTable.Open(_path);
        var second = table.Read<Change>(1);

        // Where after Skip filters what's left after skipping. Seeking first would hand Skip only the
        // matching records, and Skip(1) would then throw away the very record we're looking for.
        var skipped = table.AsQueryable<Change>().Skip(1).Where(c => c.ObjectId == second.ObjectId);
        Assert.StartsWith("scan", skipped.Explain());
        Assert.Equal([2], skipped.Select(c => c.Recno).ToList());
    }

    [Fact]
    public void Deleted_records_are_left_out_of_seeks_and_scans()
    {
        using (var table = DbfTable.Open(_path, writable: true))
            table.Delete(0);

        using var reopened = DbfTable.Open(_path);
        var id = reopened.Read<Change>(0).Id;
        Assert.True(reopened.Read<Change>(0).Deleted);
        Assert.Empty(reopened.AsQueryable<Change>().Where(c => c.Id == id).ToList());
        Assert.DoesNotContain(reopened.AsQueryable<Change>().ToList(), c => c.Recno == 1);
    }
}
