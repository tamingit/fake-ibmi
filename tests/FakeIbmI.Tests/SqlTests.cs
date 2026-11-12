namespace FakeIbmI.Tests;

/// <summary>Db2 for i SQL translated to SQLite, and embedded SQL run from RPG.</summary>
public class SqlTests
{
    /// <summary>A machine with the library the tests' tables are in.</summary>
    private static Machine OpenMachine()
    {
        var machine = Machine.Open();
        machine.Store.CreateLibrary("MYLIB");
        return machine;
    }

    [Theory]
    [InlineData("select * from mylib/customer where id = 1", "select * from mylib.customer where id = 1")]
    [InlineData("SELECT A.NAME FROM MYLIB.CUSTOMER A JOIN MYLIB.ORDERS O ON O.CUST = A.ID", "SELECT A.NAME FROM MYLIB.CUSTOMER A JOIN MYLIB.ORDERS O ON O.CUST = A.ID")]
    [InlineData("select name from customer order by name fetch first 5 rows only", "select name from customer order by name LIMIT 5")]
    [InlineData("select name from customer fetch first row only with ur", "select name from customer LIMIT 1")]
    [InlineData("select name from customer offset 10 rows fetch next 5 rows only", "select name from customer LIMIT 5 offset 10")]
    [InlineData("select first concat ' ' concat last from people for read only", "select first || ' ' || last from people")]
    [InlineData("update mylib.customer c set c.name = 'x' where c.id = 1", "update mylib.customer AS c set c.name = 'x' where c.id = 1")]
    [InlineData("delete from customer c where c.id = 1", "delete from customer AS c where c.id = 1")]
    [InlineData("insert into qtemp/work (a, b) values (1, current date)", "insert into temp.work (a, b) values (1, date('now', 'localtime'))")]
    [InlineData("select trim(both from name), a / b from t", "select trim(both from name), a / b from t")]
    [InlineData("select rrn(c), name from customer c", "select c.rowid, name from customer c")]
    public void SimpleStatementsAreTranslated(string db2, string sqlite)
    {
        var translation = SqlTranslator.Translate(db2);

        Assert.Null(translation.Problem);
        Assert.Equal(sqlite, translation.Sql);
    }

    [Fact]
    public void HostVariablesBecomeParametersAndIntoComesOut()
    {
        var translation = SqlTranslator.Translate(
            "select name, balance into :custName, :ds.balance :nullInd from customer where id = :id and region = :ds.region");

        Assert.Equal(SqlKind.Query, translation.Kind);
        Assert.Equal("select name, balance from customer where id = @h0 and region = @h1", translation.Sql);
        Assert.Equal(["id", "ds.region"], translation.Inputs);
        Assert.Equal([("custName", null), ("ds.balance", "nullInd")], translation.Outputs);
    }

    [Fact]
    public void CreateTableLosesWhatSqliteHasNoUseFor()
    {
        var translation = SqlTranslator.Translate("""
            CREATE OR REPLACE TABLE MYLIB.CUSTOMER (
                ID INTEGER GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1),
                CUSTOMER_NAME FOR COLUMN CUSTNAME VARCHAR(50) CCSID 37 NOT NULL WITH DEFAULT,
                BALANCE DECIMAL(9, 2) NOT NULL WITH DEFAULT 0,
                CREATED TIMESTAMP NOT NULL WITH DEFAULT,
                CONSTRAINT CUSTOMER_PK PRIMARY KEY (ID)
            ) RCDFMT CUSTR
            """);

        Assert.Null(translation.Problem);
        Assert.Equal(
            "DROP TABLE IF EXISTS MYLIB.CUSTOMER;\nCREATE TABLE MYLIB.CUSTOMER (ID INTEGER PRIMARY KEY AUTOINCREMENT, "
            + "CUSTOMER_NAME VARCHAR(50) NOT NULL DEFAULT '', BALANCE DECIMAL(9, 2) NOT NULL DEFAULT 0, "
            + "CREATED TIMESTAMP NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f', 'now', 'localtime')))",
            translation.Sql);
        using var store = Store.Open();
        store.CreateLibrary("MYLIB");
        Assert.Null(store.PrepareProblem(translation.Sql!));
    }

    [Theory]
    [InlineData("call mylib.proc(:a)", "the SQL statement CALL isn't supported")]
    [InlineData("merge into t using s on t.id = s.id when matched then update set a = 1", "the SQL statement MERGE isn't supported")]
    [InlineData("select * from qsys2.syscolumns", "QSYS2.SYSCOLUMNS isn't supported: the fake has no system catalog or services")]
    [InlineData("select * from table(qsys2.active_job_info()) x", "TABLE in FROM isn't supported")]
    [InlineData("update t set a = 1 where current of c1", "WHERE CURRENT OF a cursor isn't supported: use the row's key")]
    [InlineData("fetch prior from c1 into :a", "FETCH PRIOR isn't supported: only FETCH NEXT")]
    [InlineData("fetch c1 for 10 rows into :ds", "a multiple-row FETCH (FOR n ROWS) isn't supported")]
    [InlineData("select d + 1 month from t", "date arithmetic with MONTH isn't supported")]
    [InlineData("create procedure p() begin end", "CREATE PROCEDURE isn't supported: only tables, indexes and views")]
    [InlineData("whenever sqlerror goto failed", "WHENEVER isn't supported: test SQLCODE or SQLSTATE after each statement")]
    public void WhatCantBeTranslatedIsNamed(string db2, string problem)
    {
        var translation = SqlTranslator.Translate(db2);

        Assert.Equal(SqlKind.Unsupported, translation.Kind);
        Assert.Equal(problem, translation.Problem);
    }

    [Theory]
    [InlineData("set option commit = *none, closqlcsr = *endmod")]
    [InlineData("commit")]
    [InlineData("include sqlca")]
    public void StatementsWithNothingToDoAreIgnoredWithANote(string db2)
    {
        var translation = SqlTranslator.Translate(db2);

        Assert.Equal(SqlKind.NoOp, translation.Kind);
        Assert.Single(translation.Notes);
    }

    private const string Setup = """
        exec sql create table mylib/customer (
            id integer not null primary key,
            name char(20) not null with default,
            balance decimal(9, 2) not null with default,
            joined date);
        exec sql insert into customer values (1, 'Ada', 100.50, '2024-01-15'), (2, 'Grace', 20, null), (3, 'Linus', -5.25, '2023-06-01');
        """;

    [Fact]
    public void EmbeddedSqlReadsAndChangesRows()
    {
        using var machine = OpenMachine();
        var (shown, error) = RpgTests.Run(machine, Setup + """
            dcl-s id int(10) inz(1);
            dcl-s name char(20);
            dcl-s balance packed(9:2);
            dcl-s wanted char(20) inz('Grace');
            dcl-s count int(10);

            exec sql select name, balance into :name, :balance from mylib/customer where id = :id;
            dsply (%trim(name) + ' ' + %char(balance) + ' ' + %char(sqlcode));
            exec sql select id into :id from customer where name = :wanted;
            dsply ('Grace is ' + %char(id));
            exec sql select name into :name from customer where id = 99;
            dsply ('missing: ' + %char(sqlcode) + ' ' + sqlstate);
            exec sql select name into :name from customer;
            dsply ('too many: ' + %char(sqlcode));
            exec sql update customer set balance = balance + 1 where balance < 50;
            exec sql get diagnostics :count = row_count;
            dsply ('updated ' + %char(count) + ' ' + %char(sqlerrd(3)));
            exec sql delete from customer where id = 99;
            dsply ('deleted: ' + %char(sqlcode));
            exec sql insert into customer (id, name) values (1, 'Again');
            dsply ('duplicate: ' + %char(sqlcode) + ' ' + sqlstate);
            exec sql select count(*), sum(balance) into :count, :balance from customer;
            dsply (%char(count) + ' rows, ' + %char(balance));
            exec sql select ucase(name) concat '!' into :name from customer where id = 3 with ur;
            dsply name;
            """);

        Assert.Null(error);
        Assert.Equal(
        [
            "Ada 100.50 0", "Grace is 2", "missing: 100 02000", "too many: -811", "updated 2 2", "deleted: 100",
            "duplicate: -803 23505", "3 rows, 117.25", "LINUS!",
        ], shown);
    }

    [Fact]
    public void ACursorFetchesIntoADataStructureWithNullIndicators()
    {
        using var machine = OpenMachine();
        var (shown, error) = RpgTests.Run(machine, Setup + """
            dcl-ds row qualified;
              id int(10);
              name char(20);
              joined date;
            end-ds;
            dcl-s nulls int(5) dim(3);
            dcl-s low packed(9:2) inz(0);

            exec sql declare c1 cursor for
              select id, name, joined from customer where balance >= :low order by id for read only;
            exec sql open c1;
            exec sql fetch next from c1 into :row :nulls;
            dow sqlcode = 0;
              dsply (%char(row.id) + ' ' + %trim(row.name) + ' ' + %char(row.joined) + ' ' + %char(nulls(3)));
              exec sql fetch next from c1 into :row :nulls;
            enddo;
            exec sql close c1;
            exec sql fetch c1 into :row;
            dsply ('closed: ' + %char(sqlcode));
            """);

        Assert.Null(error);
        Assert.Equal(["1 Ada 2024-01-15 0", "2 Grace 0001-01-01 -1", "closed: -501"], shown);
    }

    [Fact]
    public void DynamicSqlIsTranslatedWhenItRuns()
    {
        using var machine = OpenMachine();
        // EXTNAME needs its table when the program starts, as a compile would.
        Assert.Empty(machine.RunScript(Setup.Replace("exec sql ", ""), "PRTUSER").Failures);
        var (shown, error) = RpgTests.Run(machine, """
            dcl-s statement varchar(200);
            dcl-s name char(20);
            dcl-s low packed(9:2) inz(50);
            dcl-ds extra extname('CUSTOMER') qualified end-ds;

            statement = 'select name from mylib/customer where balance > ? order by name fetch first 1 rows only';
            exec sql prepare s1 from :statement;
            exec sql declare c2 cursor for s1;
            exec sql open c2 using :low;
            exec sql fetch c2 into :name;
            dsply name;
            exec sql close c2;
            exec sql execute immediate 'delete from mylib/customer where id = 2';
            exec sql select * into :extra from customer where id = 3;
            dsply (%trim(extra.name) + ' ' + %char(extra.balance) + ' ' + %char(extra.joined));
            exec sql insert into customer values (:extra);
            dsply ('insert: ' + %char(sqlcode));
            """);

        Assert.Null(error);
        Assert.Equal(["Ada", "Linus -5.25 2023-06-01", "insert: -803"], shown);
    }

    [Fact]
    public void SavingAMemberReportsItsSqlByLine()
    {
        using var machine = OpenMachine();
        var found = machine.Save(new Member("QGPL", "QRPGLESRC", "SQLPGM", "SQLRPGLE", """
            **FREE
            dcl-s n int(10);
            exec sql set option commit = *none;
            exec sql select count(*) into :n from mylib/orders with ur;
            exec sql call mylib/nightly();
            exec sql select listagg(name, ',') within group (order by name) into :n from sysdummy1;
            """));

        Assert.Equal(
        [
            (3, Severity.Info, "SQL: SET OPTION is ignored"),
            (4, Severity.Info, "SQL: WITH UR is dropped: SQL runs without commitment control"),
            (4, Severity.Warning, "SQL: no such table: mylib.orders (yet)"),
            (5, Severity.Unsupported, "SQL: the SQL statement CALL isn't supported"),
        ], found.Take(4).Select(d => (d.Line, d.Severity, d.Message)));
        Assert.Equal(6, found[4].Line);
        Assert.Equal(Severity.Unsupported, found[4].Severity);
        Assert.StartsWith("SQL: SQLite doesn't take this statement, even translated:", found[4].Message);
    }

    [Fact]
    public void AScriptRunsStatementByStatement()
    {
        using var machine = OpenMachine();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        var member = new Member("QGPL", "QSQLSRC", "TABLES", "SQL", """
            -- the tables
            create table mylib.item (code char(5) not null primary key, price decimal(7, 2));
            insert into mylib.item values ('A', 1.50), ('B;C', 2);
            label on table mylib.item is 'Items';
            """);
        machine.Save(member);

        var first = machine.Run("RUNSQLSTM SRCFILE(QSQLSRC) SRCMBR(TABLES)", job);
        var again = machine.Run("RUNSQLSTM SRCFILE(QSQLSRC) SRCMBR(TABLES)", job);
        var one = machine.Run("RUNSQL SQL('update item set price = price * 2 where code = ''A''')", job);

        Assert.Equal(new CommandResult(true, "3 SQL statements run."), first);
        Assert.False(again.Ok);
        Assert.StartsWith("2 of 3 SQL statements failed. First: line 2: table item already exists", again.Message);
        Assert.True(one.Ok);
        Assert.Equal([("CODE", "char(5)"), ("PRICE", "decimal(7, 2)")], machine.Store.Columns("ITEM").Select(c => (c.Name.ToUpperInvariant(), c.Type)));
    }
}
