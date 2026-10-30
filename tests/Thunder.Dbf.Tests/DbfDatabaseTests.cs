//      _______________
//     |  |  CONNECT  |     ProjectThunder :: Thunder.Dbf tests
//     |  |___________|     One line to connect, and every way it can go
//     |  [__]   [__] |     wrong explained: which file, why, and the fix.
//     |______________|

using System.Text;

namespace Thunder.Dbf.Tests;

public sealed class DbfDatabaseTests : IDisposable
{
    private static readonly string FixtureRoot = FindFixtureRoot();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("thunder-db-");

    public DbfDatabaseTests()
    {
        foreach (var file in new[] { "client.dbf", "client.dbt" })
            File.Copy(Path.Combine(FixtureRoot, "dbfdatareader", file), Path.Combine(_folder.FullName, file));
        foreach (var file in new[] { "CHANGELOG.DBF", "CHANGELOG.DBT", "CHANGELOG.MDX" })
            File.Copy(Path.Combine(FixtureRoot, "siemens-step7/siprom", file), Path.Combine(_folder.FullName, file));
        File.Copy(Path.Combine(FixtureRoot, "infused-dbf/dbase_03.dbf"), Path.Combine(_folder.FullName, "plain.dbf"));
    }

    public void Dispose()
    {
        foreach (var file in _folder.EnumerateFiles())
            file.Attributes = FileAttributes.Normal;
        _folder.Delete(recursive: true);
    }

    private string PathOf(string name) => Path.Combine(_folder.FullName, name);

    [Fact]
    public void One_line_opens_a_folder_read_only()
    {
        using var db = DbfDatabase.Open(_folder.FullName);

        Assert.True(db.ReadOnly);
        Assert.Equal(["CHANGELOG", "client", "plain"], db.TableNames);
        Assert.Same(db["CLIENT"], db["client.dbf"]);
        Assert.StartsWith("85-200", (string)db["client"].Read(0)["CLIEN_HIST"]!);
        Assert.NotNull(db["changelog"].OpenProductionIndex());
    }

    [Fact]
    public void Connection_strings_set_the_options()
    {
        using var db = DbfDatabase.Open($"Data Source={_folder.FullName};Read Only=false;Code Page=850;Allow Unmaintained Indexes=yes");

        Assert.False(db.ReadOnly);
        Assert.Equal(850, db.Options.Encoding!.CodePage);
        Assert.True(db.Options.AllowUnmaintainedIndexes);
        Assert.Equal(_folder.FullName.TrimEnd(Path.DirectorySeparatorChar), db.Folder.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("Data Source=/x;Colour=blue")]
    [InlineData("Read Only=true;Mode=ReadWrite")]
    [InlineData("Data Source=/x;Read Only=maybe")]
    [InlineData("Data Source=/x;Code Page=klingon")]
    [InlineData("   ")]
    public void Bad_connection_strings_say_what_is_wrong(string connection)
    {
        var error = Assert.Throws<DbfException>(() => DbfDatabase.Open(connection));
        Assert.Equal(DbfErrorCode.InvalidConnectionString, error.Code);
        Assert.Equal(400, error.HttpStatus);
        Assert.False(string.IsNullOrWhiteSpace(error.Hint));
    }

    [Fact]
    public void Missing_folders_are_404s()
    {
        var error = Assert.Throws<DbfException>(() => DbfDatabase.Open(Path.Combine(_folder.FullName, "nope")));
        Assert.Equal(DbfErrorCode.FolderNotFound, error.Code);
        Assert.Equal(404, error.HttpStatus);
    }

    [Theory]
    [InlineData("../client")]
    [InlineData("..")]
    [InlineData("sub/client")]
    [InlineData(@"sub\client")]
    [InlineData("C:client")]
    [InlineData("/etc/passwd")]
    public void Table_names_cannot_reach_outside_the_folder(string name)
    {
        using var db = DbfDatabase.Open(_folder.FullName);

        var error = Assert.Throws<DbfException>(() => db[name]);
        Assert.Equal(DbfErrorCode.PathEscapesFolder, error.Code);
        Assert.True(error.IsSecurityRelated);
        Assert.Equal(403, error.HttpStatus);
    }

    [Fact]
    public void Links_to_files_outside_the_folder_are_refused()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"thunder-outside-{Guid.NewGuid():N}.dbf");
        File.Copy(Path.Combine(FixtureRoot, "infused-dbf/dbase_03.dbf"), outside);
        try
        {
            File.CreateSymbolicLink(PathOf("sneaky.dbf"), outside);
            using var db = DbfDatabase.Open(_folder.FullName);

            Assert.Equal(DbfErrorCode.PathEscapesFolder, Assert.Throws<DbfException>(() => db["sneaky"]).Code);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Unknown_tables_list_what_is_there()
    {
        using var db = DbfDatabase.Open(_folder.FullName);

        var error = Assert.Throws<DbfException>(() => db["CONTACT1"]);
        Assert.Equal(DbfErrorCode.TableNotFound, error.Code);
        Assert.Contains("CHANGELOG, client, plain", error.Hint);
        Assert.Equal(DbfErrorCode.TableNotFound, Assert.Throws<DbfException>(() => db["client.dbt"]).Code);
    }

    [Fact]
    public void Read_only_connections_refuse_writes()
    {
        using var db = DbfDatabase.Open(_folder.FullName);

        var error = Assert.Throws<DbfException>(() => db["plain"].Delete(0));
        Assert.Equal(DbfErrorCode.ReadOnlyConnection, error.Code);
        Assert.Equal(409, error.HttpStatus);
    }

    [Fact]
    public void Encrypted_tables_are_refused_with_an_explanation()
    {
        PatchByte("plain.dbf", 15, 0x01);
        using var db = DbfDatabase.Open(_folder.FullName);

        var error = Assert.Throws<DbfException>(() => db["plain"]);
        Assert.Equal(DbfErrorCode.EncryptedTable, error.Code);
        Assert.Contains("PROTECT", error.Message);
        Assert.Equal(403, error.HttpStatus);
    }

    [Fact]
    public void Unfinished_transactions_allow_reads_but_block_writes()
    {
        PatchByte("plain.dbf", 14, 0x01);
        using var db = DbfDatabase.Open(_folder.FullName, new DbfDatabaseOptions { ReadOnly = false });

        Assert.NotNull(db["plain"].Read(0));
        Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => db["plain"].Delete(0)).Code);
    }

    [Fact]
    public void Read_only_files_are_explained()
    {
        File.SetAttributes(PathOf("plain.dbf"), FileAttributes.ReadOnly);
        using var db = DbfDatabase.Open(_folder.FullName, new DbfDatabaseOptions { ReadOnly = false });

        var error = Assert.Throws<DbfException>(() => db["plain"]);
        Assert.Equal(DbfErrorCode.ReadOnlyLocation, error.Code);
        Assert.Equal(PathOf("plain.dbf"), error.FilePath);
    }

    [Fact]
    public void Files_the_account_cannot_read_are_explained()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return; // chmod doesn't apply / root ignores it

        File.SetUnixFileMode(PathOf("plain.dbf"), UnixFileMode.None);
        try
        {
            using var db = DbfDatabase.Open(_folder.FullName);
            var error = Assert.Throws<DbfException>(() => db["plain"]);
            Assert.Equal(DbfErrorCode.AccessDenied, error.Code);
            Assert.Contains(Environment.UserName, error.Hint);
        }
        finally
        {
            File.SetUnixFileMode(PathOf("plain.dbf"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Files_another_program_holds_exclusively_are_explained()
    {
        using var legacyApp = new FileStream(PathOf("plain.dbf"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var db = DbfDatabase.Open(_folder.FullName, new DbfDatabaseOptions { ReadOnly = false });

        var error = Assert.Throws<DbfException>(() => db["plain"]);
        Assert.Equal(DbfErrorCode.FileInUse, error.Code);
        Assert.Equal(409, error.HttpStatus);
    }

    [Fact]
    public void Files_that_are_not_tables_are_explained()
    {
        File.WriteAllText(PathOf("notes.dbf"), "this was never a table");
        using var db = DbfDatabase.Open(_folder.FullName);

        Assert.Equal(DbfErrorCode.NotADbfFile, Assert.Throws<DbfException>(() => db["notes"]).Code);
    }

    [Fact]
    public void Missing_memo_files_are_explained()
    {
        File.Delete(PathOf("client.dbt"));
        using var db = DbfDatabase.Open(_folder.FullName);

        var error = Assert.Throws<DbfException>(() => db["client"]);
        Assert.Equal(DbfErrorCode.MemoFileMissing, error.Code);
        Assert.Contains("ignoreMissingMemo", error.Hint);
    }

    [Fact]
    public void The_handshake_reports_every_table_without_reading_records()
    {
        PatchByte("plain.dbf", 15, 0x01);
        File.WriteAllText(PathOf("notes.dbf"), "this was never a table");
        using var db = DbfDatabase.Open(_folder.FullName, new DbfDatabaseOptions { ReadOnly = false });

        var report = db.Check();

        Assert.False(report.Ok);
        Assert.Equal(["CHANGELOG", "client", "notes", "plain"], report.Tables.Select(t => t.Name));

        var changelog = report.Tables[0];
        Assert.True(changelog.CanRead);
        Assert.True(changelog.CanWrite);
        Assert.Equal(6, changelog.IndexTags.Count);
        Assert.Equal("dBASE IV with memo", changelog.Format);

        Assert.Equal(DbfErrorCode.NotADbfFile, report.Tables[2].Problems.Single().Code);
        Assert.Equal(DbfErrorCode.EncryptedTable, report.Tables[3].Problems.Single().Code);

        var text = report.ToString();
        Assert.Contains("[PROBLEM] plain", text);
        Assert.Contains("EncryptedTable", text);
        Assert.True(report.Elapsed < TimeSpan.FromSeconds(5));
    }

    private void PatchByte(string file, int offset, byte value)
    {
        var bytes = File.ReadAllBytes(PathOf(file));
        bytes[offset] = value;
        File.WriteAllBytes(PathOf(file), bytes);
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
