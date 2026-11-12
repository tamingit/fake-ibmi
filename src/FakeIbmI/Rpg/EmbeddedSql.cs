// Embedded SQL: runs each translated statement on the store's SQLite, moving values between it
// and the program's host variables, and setting SQLCODE and SQLSTATE as Db2 would.

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FakeIbmI.Rpg;

internal sealed partial class Interpreter
{
    private sealed class OpenCursor(SqliteCommand command, SqliteDataReader reader) : IDisposable
    {
        public SqliteDataReader Reader { get; } = reader;

        public void Dispose()
        {
            Reader.Dispose();
            command.Dispose();
        }
    }

    private readonly Dictionary<string, OpenCursor> _cursors = [];
    private readonly Dictionary<string, SqlTranslation> _prepared = [];
    private Cell _sqlCode = null!;
    private Cell _sqlState = null!;
    private Cell _sqlErrd = null!;
    private Cell _sqlEr3 = null!;
    private long _rowCount;

    private void DeclareSql()
    {
        _sqlCode = new Cell(new RpgType(RpgKind.Int, 10));
        _sqlState = new Cell(RpgType.Char(5));
        _sqlErrd = new Cell(new RpgType(RpgKind.Int, 10, Dim: 6));
        _sqlEr3 = new Cell(new RpgType(RpgKind.Int, 10));
        _sqlState.Set("00000");
        _globals["SQLCODE"] = _globals["SQLCOD"] = _sqlCode;
        _globals["SQLSTATE"] = _globals["SQLSTT"] = _sqlState;
        _globals["SQLERRD"] = _sqlErrd;
        _globals["SQLER3"] = _sqlEr3;
    }

    private void CloseSql()
    {
        lock (_machine.Store.Gate)
        {
            foreach (var cursor in _cursors.Values) cursor.Dispose();
        }
        _cursors.Clear();
    }

    private void SetSql(int code, string state, long rows = 0)
    {
        _sqlCode.Set((decimal)code);
        _sqlState.Set(state);
        _rowCount = rows;
        _sqlErrd.Elements![2].Set((decimal)rows);
        _sqlEr3.Set((decimal)rows);
    }

    private void ExecuteSql(SqlTranslation sql, Frame frame)
    {
        try
        {
            lock (_machine.Store.Gate) RunSql(sql, frame, []);
        }
        catch (SqliteException e)
        {
            var message = Store.Clean(e.Message);
            var (code, state) = message switch
            {
                _ when message.Contains("no such table") => (-204, "42704"),
                _ when message.Contains("no such column") => (-206, "42703"),
                _ when message.Contains("already exists") => (-601, "42710"),
                _ when message.Contains("UNIQUE constraint") || message.Contains("PRIMARY KEY") => (-803, "23505"),
                _ when message.Contains("NOT NULL constraint") => (-407, "23502"),
                _ when message.Contains("FOREIGN KEY constraint") => (-530, "23503"),
                _ when message.Contains("CHECK constraint") => (-545, "23513"),
                _ when message.Contains("syntax error") || message.Contains("no such function") => (-104, "42601"),
                _ => (-901, "58004"),
            };
            SetSql(code, state);
        }
    }

    private void RunSql(SqlTranslation sql, Frame frame, IReadOnlyList<string> usingNames)
    {
        switch (sql.Kind)
        {
            case SqlKind.NoOp or SqlKind.DeclareCursor:
                SetSql(0, "00000");
                break;
            case SqlKind.Unsupported:
                throw new RpgHalt($"{_name} reached line {_line}, an SQL statement that isn't supported: {sql.Problem}");
            case SqlKind.Query:
            {
                using var command = Command(sql, frame, usingNames);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    SetSql(100, "02000");
                    break;
                }
                var row = new object[reader.FieldCount];
                reader.GetValues(row);
                if (reader.Read())
                {
                    SetSql(-811, "21000");
                    break;
                }
                SetSql(0, "00000", 1);
                Receive(row, sql.Outputs, frame);
                break;
            }
            case SqlKind.Execute or SqlKind.Select:
            {
                using var command = Command(sql, frame, usingNames);
                var rows = command.ExecuteNonQuery();
                var changes = sql.Sql!.TrimStart().Split(' ', 2)[0].ToUpperInvariant() is "INSERT" or "UPDATE" or "DELETE";
                if (changes && rows == 0) SetSql(100, "02000");
                else SetSql(0, "00000", changes ? Math.Max(rows, 0) : 0);
                break;
            }
            case SqlKind.Open:
            {
                var name = sql.Cursor!;
                if (!_program.Cursors.TryGetValue(name, out var declared))
                {
                    SetSql(-504, "34000");
                    break;
                }
                if (_cursors.ContainsKey(name))
                {
                    SetSql(-502, "24502");
                    break;
                }
                var select = declared;
                if (declared.Statement is { } statement)
                {
                    if (!_prepared.TryGetValue(statement, out select!))
                    {
                        SetSql(-514, "26501");
                        break;
                    }
                    if (select.Kind is not (SqlKind.Select or SqlKind.Query)) throw new RpgError(100, $"cursor {name}'s statement {statement} isn't a SELECT");
                }
                var command = Command(select, frame, sql.Inputs);
                try
                {
                    _cursors[name] = new OpenCursor(command, command.ExecuteReader());
                }
                catch
                {
                    command.Dispose();
                    throw;
                }
                SetSql(0, "00000");
                break;
            }
            case SqlKind.Fetch:
            {
                if (!_cursors.TryGetValue(sql.Cursor!, out var cursor))
                {
                    SetSql(-501, "24501");
                    break;
                }
                if (!cursor.Reader.Read())
                {
                    SetSql(100, "02000");
                    break;
                }
                var row = new object[cursor.Reader.FieldCount];
                cursor.Reader.GetValues(row);
                SetSql(0, "00000", 1);
                Receive(row, sql.Outputs, frame);
                break;
            }
            case SqlKind.Close:
                if (_cursors.Remove(sql.Cursor!, out var open))
                {
                    open.Dispose();
                    SetSql(0, "00000");
                }
                else SetSql(-501, "24501");
                break;
            case SqlKind.Prepare or SqlKind.ExecuteImmediate:
            {
                var text = sql.Sql ?? Values.ToText(HostCell(sql.Inputs[0], frame).Get());
                var translated = SqlTranslator.Translate(text);
                if (translated.Kind == SqlKind.Unsupported || translated.Inputs.Any(i => i != "?" && i != "*USER"))
                {
                    // As Db2 answers SQL it can't prepare.
                    SetSql(-104, "42601");
                    break;
                }
                if (sql.Kind == SqlKind.Prepare)
                {
                    _prepared[sql.Statement!] = translated;
                    SetSql(0, "00000");
                }
                else RunSql(translated, frame, []);
                break;
            }
            case SqlKind.ExecutePrepared:
                if (_prepared.TryGetValue(sql.Statement!, out var prepared)) RunSql(prepared, frame, sql.Inputs);
                else SetSql(-518, "07003");
                break;
            case SqlKind.RowCount:
                HostCell(sql.Outputs[0].Name, frame).Set((decimal)_rowCount);
                break;
        }
    }

    /// <summary>A host variable: NAME, or STRUCTURE.SUBFIELD.</summary>
    private Cell HostCell(string name, Frame frame)
    {
        var parts = name.ToUpperInvariant().Split('.');
        var cell = Find(parts[0], frame) ?? throw new RpgError(100, $"the host variable {name} isn't defined");
        foreach (var part in parts.Skip(1))
        {
            if (cell.Type.Kind != RpgKind.Ds || cell.Elements is not null) throw new RpgError(100, $"the host variable {name} isn't in a data structure");
            cell = cell.Ds.Field(part) ?? throw new RpgError(100, $"the host variable {name} isn't defined");
        }
        return cell;
    }

    /// <summary>
    /// The SQLite command for a statement, with its host variables bound. A data structure stands
    /// for its subfields, as in VALUES(:structure). A ? marker takes the next of <paramref name="usingNames"/>.
    /// </summary>
    private SqliteCommand Command(SqlTranslation sql, Frame frame, IReadOnlyList<string> usingNames)
    {
        var command = _machine.Store.Connection.CreateCommand();
        var text = sql.Sql!;
        var markers = 0;
        for (var i = 0; i < sql.Inputs.Count; i++)
        {
            var parameter = $"@h{i}";
            if (sql.Inputs[i] == "*USER")
            {
                command.Parameters.AddWithValue(parameter, _host.User);
                continue;
            }
            var name = sql.Inputs[i];
            if (name == "?")
            {
                if (markers >= usingNames.Count) throw new RpgError(100, "the statement has more ? markers than USING has variables");
                name = usingNames[markers++];
            }
            var cell = HostCell(name, frame);
            if (cell.Type.Kind == RpgKind.Ds && cell.Elements is null)
            {
                var names = cell.Ds.Cells.Select((_, k) => $"{parameter}_{k}").ToList();
                text = System.Text.RegularExpressions.Regex.Replace(text, parameter + @"\b", string.Join(", ", names));
                for (var k = 0; k < names.Count; k++) command.Parameters.AddWithValue(names[k], Bind(cell.Ds.Cells[k].Get()));
            }
            else
            {
                command.Parameters.AddWithValue(parameter, Bind(cell.Get()));
            }
        }
        command.CommandText = text;
        return command;
    }

    /// <summary>
    /// A value as SQLite takes it. Character values lose their trailing blanks, since SQLite, unlike
    /// Db2, counts them when it compares: so a CHAR(10) host variable still finds its row.
    /// </summary>
    private static object Bind(object value) => value switch
    {
        string s => s.TrimEnd(' '),
        decimal d when d == decimal.Truncate(d) && d is >= long.MinValue and <= long.MaxValue => (long)d,
        decimal d => (double)d,
        bool b => b ? "1" : "0",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        double f => f,
        _ => throw new RpgError(100, "this host variable's type can't be used in SQL"),
    };

    /// <summary>Moves a row into the INTO variables: a data structure takes a column for each subfield.</summary>
    private void Receive(object[] row, IReadOnlyList<(string Name, string? Indicator)> outputs, Frame frame)
    {
        var column = 0;
        foreach (var (name, indicator) in outputs)
        {
            var cell = HostCell(name, frame);
            var targets = cell.Type.Kind == RpgKind.Ds && cell.Elements is null ? cell.Ds.Cells : [cell];
            var indicators = indicator is null ? null : HostCell(indicator, frame);
            for (var i = 0; i < targets.Count; i++, column++)
            {
                if (column >= row.Length) return;
                var isNull = row[column] is DBNull;
                // The indicator: -1 for null, 0 otherwise. An array of them goes with a data structure.
                var flag = indicators?.Elements is { } array ? (i < array.Length ? array[i] : null) : i == 0 ? indicators : null;
                flag?.Set(isNull ? -1m : 0m);
                if (isNull)
                {
                    // Db2 would answer -305 with no indicator; the variable is cleared instead.
                    targets[i].Clear();
                    continue;
                }
                targets[i].Set(row[column] switch
                {
                    long l => (decimal)l,
                    double d => targets[i].Type.Kind == RpgKind.Float ? d : (decimal)d,
                    string s when targets[i].Type.IsNumeric => Values.ParseNumber(s),
                    string s when targets[i].Type.Kind == RpgKind.Time => s.Replace('.', ':'),
                    byte[] bytes => Convert.ToHexString(bytes),
                    var other => other,
                }, halfAdjust: true);
            }
        }
    }
}
