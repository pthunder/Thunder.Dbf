//     .mdx   .ndx   .ntx   .cdx           ProjectThunder :: Thunder.Dbf tests
//       \      \     /      /             Every index format through one door:
//        '------[ tag ]-----'             DbfTable.OpenIndex, ReadInOrder and Seek.

namespace Thunder.Dbf.Tests;

public class IndexHookTests
{
    private static string Fixture(string relative) => Path.Combine(FindFixtureRoot(), relative);

    [Theory]
    [InlineData("siemens-step7/siprom/CHANGELOG.DBF", "CHANGELOG.MDX")]
    [InlineData("xsharp/rdd-tests/TESTNTX2.DBF", "TESTNTX2.NTX")]
    [InlineData("infused-dbf/foxprodb/contacts.dbf", "contacts.CDX")]
    public void OpenIndex_reads_every_format_and_seeks_through_the_interface(string table, string index)
    {
        using var dbf = DbfTable.Open(Fixture(table), ignoreMissingMemo: true);
        using var opened = dbf.OpenIndex(index);
        var tag = opened.Tags[0];
        var first = tag.Entries().First();

        Assert.Contains(first.RecordNumber, tag.SeekExact(first.Key).Select(e => e.RecordNumber));
        Assert.NotNull(dbf.Seek(tag, first.Key));
        Assert.Equal(tag.Entries().Count(), dbf.ReadInOrder(tag).Count());
    }

    [Fact]
    public void The_structural_cdx_opens_like_the_production_mdx()
    {
        using var table = DbfTable.Open(Fixture("infused-dbf/foxprodb/calls.dbf"));
        using var cdx = table.OpenStructuralIndex();

        Assert.NotNull(cdx);
        Assert.NotEmpty(cdx.Tags);
        Assert.Null(table.OpenProductionIndex()); // FoxPro tables have no .mdx
    }

    [Fact]
    public void Unknown_index_types_are_explained()
    {
        using var table = DbfTable.Open(Fixture("siemens-step7/siprom/CHANGELOG.DBF"));
        Assert.Equal(DbfErrorCode.UnsupportedFormat, Assert.Throws<DbfException>(() => table.OpenIndex("CHANGELOG.DBT")).Code);
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
