namespace FakeIbmI.Tests;

/// <summary>Libraries: each a SQLite database of its own, in a folder, with a folder of source to match.</summary>
public class LibraryTests
{
    private static string TempFolder() => Directory.CreateTempSubdirectory("fake-ibmi-libs").FullName;

    [Fact]
    public void EachLibraryIsADatabaseFileThatComesBackWithItsMembersAndTables()
    {
        var folder = TempFolder();
        try
        {
            using (var machine = Machine.Open(folder))
            {
                Assert.True(machine.Run("CRTLIB LIB(SALES)", new BatchJob("PRTUSER", CancellationToken.None)).Ok);
                Assert.True(machine.Run("RUNSQL SQL('create table sales/orders (id int, total dec(7,2))')", new BatchJob("PRTUSER", CancellationToken.None)).Ok);
                Assert.True(machine.Run("RUNSQL SQL('insert into sales.orders values (1, 9.50)')", new BatchJob("PRTUSER", CancellationToken.None)).Ok);
                machine.Save(new Member("SALES", "QRPGLESRC", "HELLO", "RPGLE", "**FREE\ndsply 'hi';\nreturn;\n"));
                Assert.Equal(["QGPL", "SALES"], machine.Store.Libraries());
            }

            Assert.Equal(["QGPL.db", "SALES.db"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
            using var again = Machine.Open(folder);
            Assert.Equal(["QGPL", "SALES"], again.Store.Libraries());
            Assert.Equal("SALES/QRPGLESRC(HELLO)", again.Store.FindMember(null, null, "HELLO")!.ToString());
            Assert.Null(again.Store.FindMember("QGPL", null, "HELLO"));
            Assert.Equal(2, again.Store.Columns("ORDERS", "SALES").Count);
            Assert.Empty(again.Store.Columns("ORDERS", "QGPL"));
        }
        finally
        {
            SqliteClear();
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void SqliteClear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    [Fact]
    public void LibrariesKeepTheirTablesApartAndSqlSaysWhichByLibrary()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("CRTLIB LIB(AAA)", job).Ok);
        Assert.True(machine.Run("CRTLIB LIB(BBB)", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('create table aaa/item (n int)')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('create table bbb/item (n int, m int)')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('create table item (n int, m int, o int)')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('create table qtemp/scratch (n int)')", job).Ok);

        Assert.Single(machine.Store.Columns("ITEM", "AAA"));
        Assert.Equal(2, machine.Store.Columns("ITEM", "BBB").Count);
        // No library: it goes in the current one (QGPL), and a name with none is found there first.
        Assert.Equal(3, machine.Store.Columns("ITEM", "QGPL").Count);
        Assert.Equal(3, machine.Store.Columns("ITEM").Count);
        Assert.Single(machine.Store.Columns("SCRATCH"));
        Assert.False(machine.Run("RUNSQL SQL('create table nolib/item (n int)')", job).Ok);
        Assert.Equal("Library NOLIB not found.", machine.Run("CALL NOLIB/PGM", job).Message.Replace("Program PGM in library NOLIB not found.", "Library NOLIB not found."));
    }

    [Fact]
    public void ProgramsAndSourceAreFoundInEveryLibraryUnlessOneIsNamed()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        machine.Run("CRTLIB LIB(ONE)", job);
        machine.Run("CRTLIB LIB(TWO)", job);
        foreach (var (library, text) in new[] { ("ONE", "one"), ("TWO", "two") })
        {
            var member = new Member(library, "QRPGLESRC", "WHO", "RPGLE", $"**FREE\ndsply '{text}';\nreturn;\n");
            machine.Save(member);
            Assert.True(machine.Compile(member).Ok);
        }

        machine.Run("CALL WHO", job);
        machine.Run("CALL TWO/WHO", job);
        Assert.Equal(["one", "two"], job.Messages);
        Assert.Equal(["ONE", "TWO"], machine.Store.Members().Select(m => m.Library));
        Assert.False(machine.Compile(new Member("NOLIB", "QRPGLESRC", "X", "RPGLE", "**FREE\n")).Ok);
    }

    [Fact]
    public void ALibraryCanBeMadeInAScriptAndDeletedExceptQgpl()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);

        Assert.True(machine.Run("RUNSQL SQL('create schema extra')", job).Ok);
        Assert.Contains("EXTRA", machine.Store.Libraries());
        Assert.False(machine.Run("CRTLIB LIB(EXTRA)", job).Ok);
        Assert.False(machine.Run("CRTLIB LIB(1BAD)", job).Ok);
        Assert.True(machine.Run("DLTLIB LIB(EXTRA)", job).Ok);
        Assert.DoesNotContain("EXTRA", machine.Store.Libraries());
        Assert.Contains("can't be deleted", machine.Run("DLTLIB LIB(QGPL)", job).Message);
        Assert.Equal("Library EXTRA not found.", machine.Run("DLTLIB LIB(EXTRA)", job).Message);
    }

    [Fact]
    public void AFolderForEachLibraryIsReadAndMakesTheLibrary()
    {
        var folder = TempFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "SALES"));
            File.WriteAllText(Path.Combine(folder, "SALES", "hello.rpgle"), "**FREE\ndsply 'sales';\nreturn;\n");
            File.WriteAllText(Path.Combine(folder, "top.rpgle"), "**FREE\ndsply 'top';\nreturn;\n");
            using var machine = Machine.Open();

            Assert.Equal(0, machine.Import(folder, _ => { }));

            Assert.Equal(["QGPL/QRPGLESRC(TOP)", "SALES/QRPGLESRC(HELLO)"], machine.Store.Members().Select(m => m.ToString()));
            Assert.NotNull(machine.Store.FindObject("SALES", "HELLO", "*PGM"));
            Assert.Null(machine.Store.FindObject("QGPL", "HELLO", "*PGM"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ManyLibrariesCanBeMadeUpToWhatSqliteAttaches()
    {
        using var machine = Machine.Open();
        var made = 0;
        for (; made < 40; made++)
        {
            try
            {
                machine.Store.CreateLibrary($"LIB{made}");
            }
            catch (InvalidOperationException e)
            {
                Assert.Contains("at most", e.Message);
                break;
            }
        }

        Assert.InRange(made, 8, 39);
        Assert.Equal(made + 1, machine.Store.Libraries().Count);
    }

    [Fact]
    public void AUsersDefaultLibraryIsLookedInFirstAndHoldsWhatItCreates()
    {
        using var machine = Machine.Open();
        var admin = new BatchJob("PRTUSER", CancellationToken.None);
        machine.Run("CRTLIB LIB(ONE)", admin);
        machine.Run("CRTLIB LIB(TWO)", admin);
        Users.Open(machine.Store, new Dictionary<string, string>());
        machine.Store.SaveUser(new UserRow("BOB", "Bobpass1", "*ENABLED", "*NO", "", "TWO"));
        machine.Store.SaveUser(new UserRow("GONE", "Gonepass", "*ENABLED", "*NO", "", "NOLIB"));
        foreach (var library in new[] { "ONE", "TWO" })
        {
            var member = new Member(library, "QRPGLESRC", "WHO", "SQLRPGLE", $"**FREE\ndsply '{library}';\nexec sql create table made (a int);\nreturn;\n");
            machine.Save(member);
            Assert.True(machine.Compile(member).Ok);
        }
        var bob = new BatchJob("BOB", CancellationToken.None);

        Assert.Equal("TWO", machine.DefaultLibrary("bob"));
        Assert.Equal("QGPL", machine.DefaultLibrary("PRTUSER"));
        Assert.Equal("QGPL", machine.DefaultLibrary("GONE"));
        Assert.Equal("QGPL", machine.DefaultLibrary("NOBODY"));
        Assert.Equal("TWO/QRPGLESRC(WHO)", machine.Store.FindMember(null, null, "WHO", "TWO")!.ToString());
        Assert.Equal("ONE/QRPGLESRC(WHO)", machine.Store.FindMember(null, null, "WHO")!.ToString());

        // The same CALL runs the program in each user's own library first.
        Assert.True(machine.Run("CALL WHO", bob).Ok);
        Assert.True(machine.Run("CALL WHO", admin).Ok);
        Assert.Equal(["TWO"], bob.Messages);
        Assert.Equal(["ONE"], admin.Messages);

        // A table made with no library, in a program or a script, goes in the library of whoever runs it.
        Assert.Single(machine.Store.Columns("MADE", "TWO"));
        Assert.Single(machine.Store.Columns("MADE", "QGPL"));
        Assert.Empty(machine.Store.Columns("MADE", "ONE"));
        Assert.True(machine.Run("RUNSQL SQL('create table scripted (a int, b int)')", bob).Ok);
        Assert.True(machine.Run("RUNSQL SQL('create table scripted (a int)')", admin).Ok);
        Assert.Equal(2, machine.Store.Columns("SCRIPTED", "TWO").Count);
        Assert.Single(machine.Store.Columns("SCRIPTED", "QGPL"));
    }
}
