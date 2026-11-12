using FakeIbmI.Rpg;

namespace FakeIbmI.Tests;

/// <summary>Free-form RPG run by the interpreter, in a job with no display: DSPLY's messages are what a test reads.</summary>
public class RpgTests
{
    /// <summary>Saves the source as program TEST, creates it and calls it. Returns what DSPLY showed, and the error it ended in, if any.</summary>
    internal static (IReadOnlyList<string> Shown, string? Error) Run(Machine machine, string source, params string[] parameters)
    {
        var member = new Member("QGPL", "QRPGLESRC", "TEST", "SQLRPGLE", "**FREE\n" + source);
        machine.Save(member);
        Assert.True(machine.Compile(member).Ok);
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        var result = machine.Run($"CALL TEST ({string.Join(' ', parameters)})", job);
        return (job.Messages, result.Ok ? null : result.Message);
    }

    private static IReadOnlyList<string> Shown(string source, params string[] parameters)
    {
        using var machine = Machine.Open();
        var (shown, error) = Run(machine, source, parameters);
        Assert.Null(error);
        return shown;
    }

    [Fact]
    public void VariablesExpressionsAndControlFlow()
    {
        var shown = Shown("""
            dcl-s total packed(7:2) inz(0);
            dcl-s i int(10);
            dcl-s name char(10) inz('World');
            dcl-c LIMIT 5;

            for i = 1 to LIMIT;
              if %rem(i:2) = 0;
                iter;
              endif;
              total += i * 1.5;
            endfor;
            dsply ('Hello ' + %trim(name) + '!');
            dsply %char(total);
            dow i > 0;
              i -= 2;
              if i < 3;
                leave;
              endif;
            enddo;
            dsply %char(i);
            select;
              when total > 100;
                dsply 'big';
              when total > 10;
                dsply 'medium';
              other;
                dsply 'small';
            endsl;
            *inlr = *on;
            return;
            """);

        Assert.Equal(["Hello World!", "13.50", "2", "medium"], shown);
    }

    [Fact]
    public void ProceduresSubroutinesAndParametersByReference()
    {
        var shown = Shown("""
            dcl-s n int(10) inz(4);
            dcl-s text varchar(20);

            exsr greet;
            double(n);
            dsply %char(n);
            dsply %char(factorial(5));
            dsply text;
            return;

            begsr greet;
              text = 'from the subroutine';
            endsr;

            dcl-proc double;
              dcl-pi *n;
                value int(10);
              end-pi;
              value = value * 2;
            end-proc;

            dcl-proc factorial;
              dcl-pi *n packed(15:0);
                n packed(15:0) const;
              end-pi;
              if n <= 1;
                return 1;
              endif;
              return n * factorial(n - 1);
            end-proc;
            """);

        Assert.Equal(["8", "120", "from the subroutine"], shown);
    }

    [Fact]
    public void DataStructuresArraysAndBuiltIns()
    {
        var shown = Shown("""
            dcl-ds customer qualified;
              id packed(5:0) inz(42);
              name char(20) inz('Ada');
              balance packed(9:2);
            end-ds;
            dcl-ds copy likeds(customer);
            dcl-s numbers int(10) dim(5);
            dcl-s i int(10);
            dcl-s d date inz(d'2024-02-28');

            customer.balance = -1234.5;
            copy = customer;
            dsply (%trim(copy.name) + ' ' + %char(copy.id));
            dsply %editc(copy.balance:'J');
            for i = 1 to %elem(numbers);
              numbers(i) = i * i;
            endfor;
            dsply %char(%xfoot(numbers));
            dsply %char(%lookup(16:numbers));
            dsply %subst('abcdef':2:3);
            dsply %char(%scan('cd':'abcdef'));
            dsply %upper(%replace('XY':'abcdef':3:2));
            dsply %char(%date('2024-02-28') + %days(2));
            dsply %char(%diff(%date('2024-03-01'):%date('2024-02-28'):*days));
            dsply %char(%dec('12.345':5:2));
            dsply %editc(1234567:'1');
            """.Replace("inz(d'2024-02-28')", "inz(%date('2024-02-28'))"));

        Assert.Equal(["Ada 42", "    1,234.50-", "55", "4", "bcd", "3", "ABXYEF", "2024-03-01", "2", "12.34", "1,234,567"], shown);
    }

    [Fact]
    public void ParametersComeInAndMonitorCatchesErrors()
    {
        var shown = Shown("""
            dcl-pi *n;
              name char(10);
              count packed(15:5);
            end-pi;
            dcl-s zero int(10);
            dcl-s small packed(3:0);

            dsply (%trim(name) + ' x ' + %char(%int(count)));
            monitor;
              small = 10 / zero;
            on-error 102;
              dsply 'divide by zero';
            endmon;
            monitor;
              small = 12345;
            on-error;
              dsply ('status ' + %char(%status));
            endmon;
            """, "'Bob'", "3");

        Assert.Equal(["Bob x 3", "divide by zero", "status 103"], shown);
    }

    [Fact]
    public void AnErrorNobodyMonitorsEndsTheProgramWithItsLine()
    {
        using var machine = Machine.Open();
        var (_, error) = Run(machine, """
            dcl-s a int(10) dim(3);
            dcl-s i int(10) inz(4);
            a(i) = 1;
            """);

        Assert.Equal("TEST ended in error at line 4: An array index is out of range. (status 00121)", error);
    }

    [Fact]
    public void OneProgramCallsAnother()
    {
        using var machine = Machine.Open();
        var callee = new Member("QGPL", "QRPGLESRC", "ADDER", "RPGLE", """
            **FREE
            dcl-pi *n;
              a packed(5:0) const;
              b packed(5:0) const;
              sum packed(7:0);
            end-pi;
            sum = a + b;
            return;
            """);
        machine.Save(callee);
        machine.Compile(callee);

        var (shown, error) = Run(machine, """
            dcl-pr adder extpgm('ADDER');
              a packed(5:0) const;
              b packed(5:0) const;
              sum packed(7:0);
            end-pr;
            dcl-pr missing extpgm;
            end-pr;
            dcl-s result packed(7:0);
            adder(2:40:result);
            dsply %char(result);
            monitor;
              missing();
            on-error 202;
              dsply 'no such program';
            endmon;
            """);

        Assert.Null(error);
        Assert.Equal(["42", "no such program"], shown);
    }

    [Fact]
    public void WritingToAPrinterFileMakesASpooledFileWhenTheProgramEnds()
    {
        using var machine = Machine.Open();
        var (shown, error) = Run(machine, """
            dcl-f report printer(20);
            dcl-s line char(40);
            dcl-s i int(10);
            for i = 1 to 70;
              line = 'Line ' + %char(i) + '   ';
              write report line;
            endfor;
            write report;
            line = 'a very long line that is cut at the width';
            write report line;
            dsply 'printed';
            """);

        Assert.Null(error);
        Assert.Equal(["printed"], shown);
        var spool = Assert.Single(machine.Store.Spools());
        Assert.Equal(("REPORT", "PRTUSER", 2, 20), (spool.Name, spool.User, spool.Pages, spool.Width));
        var lines = spool.Text.Split('\n');
        Assert.Equal(72, lines.Length);
        Assert.Equal("Line 1", lines[0]);
        Assert.Equal("", lines[70]);
        Assert.Equal("a very long line tha", lines[71]);
    }

    [Fact]
    public void SavingSaysWhatIsNotSupportedByLine()
    {
        using var machine = Machine.Open();
        var found = machine.Save(new Member("QGPL", "QRPGLESRC", "OLD", "RPGLE", """
            **FREE
            dcl-f custfile disk keyed;
            dcl-s p pointer;
            dcl-s n int(10);
            /copy qcpysrc,protos
            n = %bitand(n:1);
            chain 1 custfile;
            n = 1;
            """));

        Assert.Equal(
        [
            (2, "native file I/O isn't supported (file CUSTFILE): use embedded SQL"),
            (3, "the type POINTER isn't supported"),
            (5, "the directive /COPY isn't supported: put the copied code in the member"),
            (6, "the built-in function %BITAND isn't supported"),
            (7, "native file I/O isn't supported (CHAIN CUSTFILE): use embedded SQL"),
        ], found.Select(d => (d.Line, d.Message)));
        Assert.All(found, d => Assert.Equal(Severity.Unsupported, d.Severity));
        Assert.Equal(found, machine.Store.Diagnostics(machine.Store.FindMember(null, null, "OLD")!));
    }

    [Fact]
    public void FixedFormIsReportedAndTheRestStillRuns()
    {
        using var machine = Machine.Open();
        var member = new Member("QGPL", "QRPGLESRC", "MIXED", "RPGLE",
            "     D total           S              7P 2\n" +
            "       dcl-s n int(10) inz(2);\n" +
            "       dsply %char(n);\n" +
            "     C                   EVAL      n = 3\n" +
            "       dsply 'after';\n");
        var found = machine.Save(member);
        machine.Compile(member);
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        var result = machine.Run("CALL MIXED", job);

        Assert.Equal([1, 4], found.Select(d => d.Line));
        Assert.Equal(["2"], job.Messages);
        Assert.False(result.Ok);
        Assert.Contains("reached line 4, which isn't supported", result.Message);
    }

    [Fact]
    public void ALoopThatNeverEndsStopsWhenItsJobIsCancelled()
    {
        using var machine = Machine.Open();
        var member = new Member("QGPL", "QRPGLESRC", "SPIN", "RPGLE", "**FREE\ndow *on;\nenddo;\n");
        machine.Save(member);
        machine.Compile(member);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Assert.Throws<OperationCanceledException>(() => machine.Run("CALL SPIN", new BatchJob("PRTUSER", cancel.Token)));
    }
}
