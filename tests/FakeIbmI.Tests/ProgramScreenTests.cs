using FakeIbmI.Tests.Client;

namespace FakeIbmI.Tests;

/// <summary>Programs, display files and the source screens, through a real 5250 session.</summary>
public class ProgramScreenTests
{
    private static Key Click(int row, int column) => new CursorKey(row, column);

    private static async Task<TestClient> SignOnAsync(TelnetServer host)
    {
        var client = await TestClient.ConnectAsync(host, null);
        await client.WaitForTextAsync("Sign On");
        await client.PressAsync(Click(6, 53));
        await client.TypeAsync("PRTUSER");
        await client.PressAsync(Click(7, 53));
        await client.TypeAsync("Secret12");
        await client.EnterAsync();
        await client.WaitForTextAsync("IBM i Main Menu");
        return client;
    }

    /// <summary>The screen's text with each display attribute as the blank it shows as.</summary>
    private static string Plain(TestClient client) => System.Text.RegularExpressions.Regex.Replace(client.Text(), "[\u0080-\u009f]", " ");

    private static async Task CommandAsync(TestClient client, string command)
    {
        await client.PressAsync(Click(20, 7));
        await client.TypeAsync(command);
        await client.EnterAsync();
    }

    private static async Task TypeAtAsync(TestClient client, int row, int column, string text)
    {
        await client.PressAsync(Click(row, column));
        await client.TypeAsync(text);
    }

    /// <summary>A DDS line, with each part in its columns.</summary>
    private static string Dds(string keywords = "", string spec = " ", string name = "", string length = "", string type = " ",
        string decimals = "", string usage = " ", string row = "", string column = "", string when = "") =>
        $"     A {when,-9}{spec} {name,-10} {length,5}{type}{decimals,2}{usage}{row,3}{column,3}{keywords}";

    private static void Create(Machine machine, string name, string type, string source)
    {
        var member = new Member("QGPL", Machine.DefaultFile(type), name, type, source);
        Assert.DoesNotContain(machine.Save(member), d => d.Severity == Severity.Unsupported);
        Assert.True(machine.Compile(member).Ok);
    }

    [Fact]
    public async Task DsplyShowsItsMessagesAndTakesAReply()
    {
        using var machine = Machine.Open();
        Create(machine, "GREET", "RPGLE", """
            **FREE
            dcl-s name char(10);
            dsply 'Who are you?' '' name;
            dsply ('Hello ' + %trim(name) + '.');
            return;
            """);
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "call greet");
        await client.WaitForTextAsync("Type reply, press Enter.");
        Assert.Contains("Display Program Messages", client.Text());
        Assert.Contains("Who are you?", client.Text());
        await client.TypeAsync("Ada");
        await client.EnterAsync();
        await client.WaitForTextAsync("Hello Ada.");
        Assert.Contains("Press Enter to continue.", client.Text());
        await client.EnterAsync();
        await client.WaitForTextAsync("IBM i Main Menu");
    }

    [Fact]
    public async Task CommandsThatFailSayWhyOnTheMenu()
    {
        await using var host = await TelnetServer.StartAsync();
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "call nothere");
        await client.WaitForTextAsync("Program NOTHERE in library *LIBL not found.");
        await CommandAsync(client, "wrkactjob");
        await client.WaitForTextAsync("Command WRKACTJOB in library *LIBL not found.");
        await CommandAsync(client, "crtbndrpg pgm(qgpl/nosrc)");
        await client.WaitForTextAsync("Member NOSRC not found in file QRPGLESRC.");
    }

    [Fact]
    public async Task AProgramShowsItsDisplayFileAndReadsWhatIsTyped()
    {
        using var machine = Machine.Open();
        Create(machine, "CUSTD", "DSPF", string.Join('\n',
            Dds("DSPSIZ(24 80 *DS3)"),
            Dds("CA03(03 'Exit')"),
            Dds(spec: "R", name: "ENTRY"),
            Dds("'Customer Entry'", row: "1", column: "30"),
            Dds("DSPATR(HI)"),
            Dds("'Number . . .'", row: "3", column: "2"),
            Dds("EDTCDE(Z)", name: "CUSTNO", length: "5", type: "Y", decimals: "0", usage: "B", row: "3", column: "16"),
            Dds("'Name . . . .'", row: "4", column: "2"),
            Dds("CHECK(LC)", name: "NAME", length: "20", type: "A", usage: "B", row: "4", column: "16"),
            Dds("ERRMSG('Name required' 50)", when: " 50"),
            Dds("'Balance  . .'", row: "5", column: "2"),
            Dds("EDTCDE(J)", name: "BALANCE", length: "9", type: "Y", decimals: "2", usage: "O", row: "5", column: "16"),
            Dds("'F3=Exit'", row: "23", column: "2")));
        Create(machine, "CUSTPGM", "RPGLE", """
            **FREE
            dcl-f custd workstn;
            dcl-s saved int(10);

            balance = 1234.5;
            dow not *in03;
              exfmt entry;
              if *in03;
                leave;
              endif;
              *in50 = (name = *blanks);
              if not *in50;
                saved += 1;
                balance += custno;
              endif;
            enddo;
            dsply ('saved ' + %char(saved) + ' for ' + %trim(name));
            *inlr = *on;
            """);
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "CALL PGM(CUSTPGM)");
        await client.WaitForTextAsync("Customer Entry");
        Assert.Contains("Balance  . .      1,234.50", Plain(client));

        await TypeAtAsync(client, 3, 16, "7");
        await client.EnterAsync();
        await client.WaitForTextAsync("Name required");

        await TypeAtAsync(client, 4, 16, "Ada");
        await client.EnterAsync();
        await client.WaitForTextAsync("1,241.50");
        Assert.DoesNotContain("Name required", client.Text());

        await client.PressAsync(new FunctionKey("F5"));
        await client.WaitForTextAsync("Function key not allowed.");
        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("saved 1 for Ada");
        await client.EnterAsync();
        await client.WaitForTextAsync("IBM i Main Menu");
    }

    [Fact]
    public async Task ASubfilePagesByItselfAndReturnsTheChangedRecords()
    {
        using var machine = Machine.Open();
        Create(machine, "ITEMD", "DSPF", string.Join('\n',
            Dds("CA03(03)"),
            Dds("SFL", spec: "R", name: "ROW"),
            Dds(name: "OPT", length: "1", type: "A", usage: "B", row: "6", column: "3"),
            Dds(name: "ITEM", length: "10", type: "A", usage: "O", row: "6", column: "7"),
            Dds("EDTCDE(Z)", name: "QTY", length: "5", type: "Y", decimals: "0", usage: "O", row: "6", column: "20"),
            Dds("SFLCTL(ROW)", spec: "R", name: "CTL"),
            Dds("SFLSIZ(50) SFLPAG(5)"),
            Dds("OVERLAY"),
            Dds("SFLDSP", when: " 40"),
            Dds("SFLDSPCTL", when: " 41"),
            Dds("SFLCLR", when: " 42"),
            Dds("SFLEND(*MORE)", when: " 43"),
            Dds("'Items'", row: "1", column: "30"),
            Dds("'Opt Item          Qty'", row: "5", column: "2"),
            Dds(spec: "R", name: "FOOT"),
            Dds("'F3=Exit'", row: "23", column: "2")));
        Create(machine, "ITEMPGM", "RPGLE", """
            **FREE
            dcl-f itemd workstn sfile(row:rrn);
            dcl-s rrn int(5);
            dcl-s picked varchar(50);

            *in42 = *on;
            write ctl;
            *in42 = *off;
            for rrn = 1 to 12;
              opt = ' ';
              item = 'ITEM' + %char(rrn);
              qty = rrn * 10;
              write row;
            endfor;
            *in40 = *on;
            *in41 = *on;
            *in43 = *on;
            dow not *in03;
              write foot;
              exfmt ctl;
              if *in03;
                leave;
              endif;
              picked = '';
              readc row;
              dow not %eof;
                picked += %trim(item) + '=' + opt + ' ';
                opt = ' ';
                update row;
                readc row;
              enddo;
              dsply picked;
            enddo;
            *inlr = *on;
            """);
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "call itempgm");
        await client.WaitForTextAsync("ITEM5");
        Assert.Contains("ITEM1           10", Plain(client));
        Assert.DoesNotContain("ITEM6", client.Text());
        Assert.Contains("More...", client.Text());
        Assert.Contains("F3=Exit", client.Text());

        await client.PressAsync(new FunctionKey("RollUp"));
        await client.WaitForTextAsync("ITEM10");
        await TypeAtAsync(client, 7, 3, "4"); // ITEM7
        await client.PressAsync(new FunctionKey("RollUp"));
        await client.WaitForTextAsync("Bottom");
        Assert.Contains("ITEM12", client.Text());
        await client.PressAsync(new FunctionKey("RollUp"));
        await client.WaitForTextAsync("Roll up or down past the first or last record in file.");

        await client.PressAsync(new FunctionKey("RollDown"));
        await client.WaitForTextAsync("ITEM7");
        await client.PressAsync(new FunctionKey("RollDown"));
        await client.WaitForTextAsync("ITEM1 ");
        await TypeAtAsync(client, 7, 3, "5"); // ITEM2
        await client.EnterAsync();
        // Back on the first page, the options cleared by the program.
        await client.WaitForAsync(c => c.Text().Contains("ITEM1 ") && !c.Screen.KeyboardLocked && host.Inputs.Count(i => i.Aid == 0xf1) >= 3, "the list again");

        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("ITEM2=5 ITEM7=4");
        await client.EnterAsync();
        await client.WaitForTextAsync("IBM i Main Menu");
    }

    [Fact]
    public async Task AMemberIsWrittenInTheEditorCheckedOnSavingCompiledAndCalled()
    {
        var lines = new List<string>();
        await using var host = await TelnetServer.StartAsync(log: line => { lock (lines) lines.Add(line); });
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "5");
        await client.WaitForTextAsync("Work with Members");
        Assert.Contains("No members", client.Text());
        await client.PressAsync(new FunctionKey("F6"));
        await client.WaitForTextAsync("Create Member");
        await TypeAtAsync(client, 6, 30, "hello");
        await client.EnterAsync();

        await client.WaitForTextAsync("QGPL/QRPGLESRC(HELLO)  RPGLE");
        Assert.Contains("0001   **FREE", Plain(client));
        await TypeAtAsync(client, 5, 8, "dsply 'Hi from the editor';");
        await TypeAtAsync(client, 6, 8, "  n = %bitand(1:2);");
        await client.PressAsync(new FunctionKey("F3"));

        await client.WaitForTextAsync("Member HELLO saved: 1 line not supported.");
        Assert.Contains("Member Diagnostics", client.Text());
        Assert.Contains("    3  Not supported  the built-in function %BITAND isn't supported", client.Text());
        Assert.Equal("**FREE\ndsply 'Hi from the editor';\n  n = %bitand(1:2);\n", host.Machine.Store.FindMember(null, null, "HELLO")!.Source);
        await client.EnterAsync();

        // The list counts what isn't supported; 2 edits the member again, and D deletes the line.
        await client.WaitForTextAsync("Work with Members");
        Assert.Matches(@"HELLO +RPGLE +QRPGLESRC +QGPL +1", client.Text());
        await TypeAtAsync(client, 7, 3, "2");
        await client.EnterAsync();
        await client.WaitForAsync(c => Plain(c).Contains("0003     n = %bitand(1:2);") && !c.Screen.KeyboardLocked, "the editor");
        await TypeAtAsync(client, 6, 6, "d");
        await client.EnterAsync();
        await client.WaitForTextAsync("2 lines");
        Assert.DoesNotContain("bitand", client.Text());
        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("Member HELLO saved.");

        await TypeAtAsync(client, 7, 3, "14");
        await client.EnterAsync();
        await client.WaitForTextAsync("Program HELLO created in library QGPL.");
        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("IBM i Main Menu");

        await CommandAsync(client, "call hello");
        await client.WaitForTextAsync("Hi from the editor");
        await client.EnterAsync();
        await client.WaitForTextAsync("IBM i Main Menu");
        lock (lines)
        {
            Assert.Contains(lines, l => l.Contains("command CALL by PRTUSER"));
            Assert.Contains(lines, l => l.Contains("member QGPL/QRPGLESRC(HELLO) saved: 1 not supported"));
        }
    }

    [Fact]
    public async Task StrseuOpensAMemberAndF12LeavesItUnchanged()
    {
        using var machine = Machine.Open();
        machine.Save(new Member("QGPL", "QSQLSRC", "TABLES", "SQL", "create table t (a int);\n"));
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "strseu srcfile(qsqlsrc) srcmbr(tables)");
        await client.WaitForTextAsync("QGPL/QSQLSRC(TABLES)  SQL");
        await TypeAtAsync(client, 5, 8, "drop table t;");
        await client.PressAsync(new FunctionKey("F12"));

        await client.WaitForTextAsync("Edit cancelled: member TABLES not changed.");
        Assert.Equal("create table t (a int);\n", machine.Store.FindMember(null, null, "TABLES")!.Source);
    }

    [Fact]
    public async Task ADisplayFileIsCheckedOnSaving()
    {
        using var machine = Machine.Open();

        var found = machine.Save(new Member("QGPL", "QDDSSRC", "BADD", "DSPF", string.Join('\n',
            Dds("DSPSIZ(27 132 *DS4)"),
            Dds("WINDOW(5 10 10 40)", spec: "R", name: "POPUP"),
            Dds("EDTWRD('  /  /  ')", name: "WHEN", length: "6", type: "Y", decimals: "0", usage: "O", row: "2", column: "2"),
            Dds(name: "STAMP", length: "26", type: "Z", usage: "O", row: "3", column: "2"),
            Dds("'ok'", row: "30", column: "2"))));

        Assert.Equal(
        [
            (1, "DSPSIZ(27 132 *DS4): only 24 by 80 (*DS3) isn't supported"),
            (2, "the record keyword WINDOW isn't supported"),
            (3, "the field keyword EDTWRD isn't supported"),
            (4, "data type Z isn't supported (STAMP): use A, S or Y"),
            (5, "line 30 position 2 isn't on a 24 by 80 screen"),
        ], found.Select(d => (d.Line, d.Message)));
    }

    /// <summary>The repository's examples/source folder.</summary>
    private static string Examples()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "examples", "source"))) folder = folder.Parent;
        return Path.Combine(folder!.FullName, "examples", "source");
    }

    [Fact]
    public async Task TheExamplesAreSupportedAndTheCustomerListWorks()
    {
        using var machine = Machine.Open();
        var lines = new List<string>();
        Assert.Equal(0, machine.Import(Examples(), lines.Add));
        Assert.DoesNotContain(lines, l => l.Contains("warning") || l.Contains("not supported"));
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "call hello ('Ada')");
        await client.WaitForTextAsync("Hello, Ada!");
        await client.EnterAsync();
        await client.WaitForTextAsync("IBM i Main Menu");

        await CommandAsync(client, "call custlist");
        await client.WaitForTextAsync("Work with Customers");
        Assert.Contains("Ada Lovelace", client.Text());
        Assert.Contains("Frances Allen", client.Text());
        Assert.DoesNotContain("John Backus", client.Text());
        Assert.Contains("More...", client.Text());
        await client.PressAsync(new FunctionKey("RollUp"));
        await client.WaitForTextAsync("Niklaus Wirth");

        // 5 beside Niklaus Wirth, the second on this page, then a new name.
        await TypeAtAsync(client, 8, 3, "5");
        await client.EnterAsync();
        await client.WaitForTextAsync("Enter=Save   F12=Cancel");
        Assert.Contains("Niklaus Wirth", client.Text());
        await TypeAtAsync(client, 5, 16, "N. Wirth     ");
        await client.EnterAsync();
        await client.WaitForTextAsync("Work with Customers");
        await client.PressAsync(new FunctionKey("RollUp"));
        await client.WaitForTextAsync("N. Wirth");
        Assert.DoesNotContain("Niklaus", client.Text());

        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("IBM i Main Menu");
    }

    [Fact]
    public async Task TheUserAppCreatesChangesAndDeletesUsersTheServersSignOn()
    {
        using var machine = Machine.Open();
        var lines = new List<string>();
        Assert.Equal(0, machine.Import(Path.Combine(Examples(), "..", "users"), lines.Add));
        Assert.DoesNotContain(lines, l => l.Contains("warning") || l.Contains("not supported"));
        var users = Users.Open(machine.Store, new Dictionary<string, string>());
        await using var host = await TelnetServer.StartAsync(users: users, machine: machine);
        await using var client = await SignOnAsync(host);
        await CommandAsync(client, "call usrmnt");
        await client.WaitForTextAsync("Work with Users");
        Assert.Contains("PRTUSER", client.Text());
        Assert.Contains("*DISABLED", client.Text());

        // F6: a new user needs a valid name and the same password twice.
        await client.PressAsync(new FunctionKey("F6"));
        await client.WaitForTextAsync("Create User");
        await client.EnterAsync();
        await client.WaitForTextAsync("A user name is 1 to 10");
        await TypeAtAsync(client, 4, 25, "ada");
        await TypeAtAsync(client, 5, 25, "Pass1");
        await TypeAtAsync(client, 6, 25, "Pass2");
        await client.EnterAsync();
        await client.WaitForTextAsync("The passwords are not the same");
        await TypeAtAsync(client, 6, 25, "Pass1");
        await client.EnterAsync();
        await client.WaitForTextAsync("User ADA created.");
        Assert.Equal(SignOnResult.SignedOn, users.Check("ada", "Pass1").Result);

        // 2 beside ADA (the first, by name): disable it. A blank password keeps the old one.
        await TypeAtAsync(client, 7, 3, "2");
        await client.EnterAsync();
        await client.WaitForTextAsync("Change User");
        await TypeAtAsync(client, 7, 25, "*DISABLED");
        await client.EnterAsync();
        await client.WaitForTextAsync("User ADA changed.");
        Assert.Equal(SignOnResult.Disabled, users.Check("ADA", "Pass1").Result);
        Assert.Equal("Pass1", users.Find("ADA")!.Password);

        // 4 beside ADA: delete it.
        await TypeAtAsync(client, 7, 3, "4");
        await client.EnterAsync();
        await client.WaitForTextAsync("Delete User");
        await client.EnterAsync();
        await client.WaitForTextAsync("User ADA deleted.");
        Assert.Equal(SignOnResult.UnknownUser, users.Check("ADA", "Pass1").Result);

        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("IBM i Main Menu");
    }

    [Fact]
    public async Task TheCustomerAppListsSearchesLooksUpAndCreates()
    {
        using var machine = Machine.Open();
        var lines = new List<string>();
        Assert.Equal(0, machine.Import(Path.Combine(Examples(), "..", "customers"), lines.Add));
        Assert.DoesNotContain(lines, l => l.Contains("warning") || l.Contains("not supported"));
        lock (machine.Store.Gate)
        {
            using var count = machine.Store.Connection.CreateCommand();
            count.CommandText = "select count(*) from customers";
            Assert.Equal(50L, count.ExecuteScalar());
        }
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);
        await CommandAsync(client, "call custmnt");
        await client.WaitForTextAsync("Customer Maintenance");

        // 1: the list, 12 to a page, then a search.
        await TypeAtAsync(client, 12, 20, "1");
        await client.EnterAsync();
        await client.WaitForTextAsync("Search name");
        Assert.Contains("More...", Plain(client));
        await TypeAtAsync(client, 4, 18, "lovelace");
        await client.EnterAsync();
        await client.WaitForAsync(c => !Plain(c).Contains("More...") && Plain(c).Contains("Lovelace"), "the search result");
        Assert.DoesNotContain("Hopper", client.Text());

        // 5 beside the first match shows its detail.
        await TypeAtAsync(client, 7, 3, "5");
        await client.EnterAsync();
        await client.WaitForTextAsync("Email");
        Assert.Contains("Rochester", client.Text());
        await client.EnterAsync();
        await client.WaitForTextAsync("Search name");
        await client.PressAsync(new FunctionKey("F12"));
        await client.WaitForTextAsync("Customer Maintenance");

        // 2: look up by number, first one that isn't there.
        await TypeAtAsync(client, 12, 20, "2");
        await client.EnterAsync();
        await client.WaitForTextAsync("Look Up Customer");
        await TypeAtAsync(client, 5, 20, "99");
        await client.EnterAsync();
        await client.WaitForTextAsync("Customer not found");
        await TypeAtAsync(client, 5, 20, "2");
        await client.EnterAsync();
        await client.WaitForTextAsync("Email");
        Assert.Contains("Austin", client.Text());
        await client.EnterAsync();
        await client.WaitForTextAsync("Customer Maintenance");

        // 3: a new customer needs a name.
        await TypeAtAsync(client, 12, 20, "3");
        await client.EnterAsync();
        await client.WaitForTextAsync("New Customer");
        await client.EnterAsync();
        await client.WaitForTextAsync("Name required");
        await TypeAtAsync(client, 5, 20, "Test Person");
        await TypeAtAsync(client, 6, 20, "Oslo");
        await client.EnterAsync();
        await client.WaitForTextAsync("Customer 51 created.");

        await TypeAtAsync(client, 12, 20, "2");
        await client.EnterAsync();
        await client.WaitForTextAsync("Look Up Customer");
        await TypeAtAsync(client, 5, 20, "51");
        await client.EnterAsync();
        await client.WaitForTextAsync("Email");
        Assert.Contains("Test Person", client.Text());
        Assert.Contains("Oslo", client.Text());
        await client.EnterAsync();
        await client.WaitForTextAsync("Customer Maintenance");

        // 4: print customer 3 from the menu.
        await TypeAtAsync(client, 12, 20, "4");
        await client.EnterAsync();
        await client.WaitForTextAsync("Print Customer Record");
        await TypeAtAsync(client, 5, 20, "3");
        await client.EnterAsync();
        await client.WaitForTextAsync("Customer 3 printed to spooled file CUSTPRT.");

        // And 6 beside the first customer in the list (by name: Alan ...).
        await TypeAtAsync(client, 12, 20, "1");
        await client.EnterAsync();
        await client.WaitForTextAsync("Search name");
        await TypeAtAsync(client, 7, 3, "6");
        await client.EnterAsync();
        await client.WaitForTextAsync("printed to spooled file CUSTPRT.");
        await client.PressAsync(new FunctionKey("F12"));
        await client.WaitForTextAsync("Customer Maintenance");
        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("IBM i Main Menu");

        // The run made one spooled file, as the print server lists it: SCS, EBCDIC lines.
        var spools = machine.Store.Spools();
        Assert.Single(spools);
        var scs = SpooledFile.From(spools[0]);
        Assert.Equal(("CUSTPRT", "*SCS", 1), (scs.Name, scs.DeviceType, scs.Pages));
        var text = Ebcdic.Decode(scs.Data).Replace('\u0085', '\n').Replace('\u000c', '\n');
        Assert.Contains("CUSTOMER RECORD\n---------------\nNumber  : 3\n", text);
        Assert.Contains("Email   : ", text);
        Assert.Contains("Balance : ", text);
        // Two records of ten lines each, from the one run of the program.
        Assert.Equal(20, spools[0].Text.Split('\n').Length);
        Assert.Equal(2, text.Split("CUSTOMER RECORD").Length - 1);
    }

    [Fact]
    public void AFolderOfSourceIsReadCheckedAndCreated()
    {
        var folder = Directory.CreateTempSubdirectory("fake-ibmi-source");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "tables.sql"), "create table mylib.part (code char(5) not null, qty int);\ninsert into part values ('A1', 3);\n");
            File.WriteAllText(Path.Combine(folder.FullName, "count.sqlrpgle"),
                "**FREE\ndcl-s n int(10);\nexec sql select sum(qty) into :n from mylib/part;\ndsply %char(n);\nin dataarea;\nreturn;\n");
            Directory.CreateDirectory(Path.Combine(folder.FullName, "MYSRC"));
            File.WriteAllText(Path.Combine(folder.FullName, "MYSRC", "menu.dspf"), Dds(spec: "R", name: "MENU") + "\n" + Dds("'Menu'", row: "1", column: "2") + "\n");
            File.WriteAllText(Path.Combine(folder.FullName, "notes.txt"), "not source");
            using var machine = Machine.Open();
            var lines = new List<string>();

            var unsupported = machine.Import(folder.FullName, lines.Add);
            var again = machine.Import(folder.FullName, lines.Add);

            Assert.Equal(1, unsupported);
            Assert.Equal(0, again);
            Assert.Equal(
            [
                "source: QGPL/QSQLSRC(TABLES) SQL: 2 SQL statements run from QGPL/QSQLSRC(TABLES).",
                "source: QGPL/MYSRC(MENU) DSPF: File MENU created in library QGPL.",
                "source: QGPL/QRPGLESRC(COUNT) SQLRPGLE: Program COUNT created in library QGPL. 1 line can't be run: see the member's diagnostics.",
                "source:   line 5: not supported: data areas isn't supported (IN)",
            ], lines);
            var job = new BatchJob("PRTUSER", CancellationToken.None);
            var result = machine.Run("CALL COUNT", job);
            Assert.Equal(["3"], job.Messages);
            Assert.Equal("COUNT reached line 5, which isn't supported: data areas isn't supported (IN)", result.Message);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
