// Turns a Db2 for i SQL statement into one SQLite runs, where the difference is simple: library
// qualifiers, FETCH FIRST, host variables, INTO, isolation clauses, special registers and the
// common column options of CREATE TABLE. What it can't translate it says so, by name. Scalar
// functions SQLite lacks (UCASE, DIGITS, YEAR and so on) are registered with SQLite instead (see
// Db2Functions), so they need no rewriting.

using System.Text;

namespace FakeIbmI;

public enum SqlKind
{
    /// <summary>A statement that returns one row into host variables: SELECT INTO, VALUES INTO, SET :v =.</summary>
    Query,

    /// <summary>INSERT, UPDATE, DELETE and data definition.</summary>
    Execute,

    /// <summary>A SELECT with no INTO, as a script or RUNSQL runs it.</summary>
    Select,
    DeclareCursor,
    Open,
    Fetch,
    Close,
    Prepare,
    ExecuteImmediate,
    ExecutePrepared,

    /// <summary>GET DIAGNOSTICS :v = ROW_COUNT.</summary>
    RowCount,

    /// <summary>A statement with nothing to do here, such as SET OPTION or COMMIT.</summary>
    NoOp,
    Unsupported,
}

/// <summary>
/// A translated statement. <see cref="Sql"/> is SQLite's text, with a parameter @h0, @h1 ... for
/// each of <see cref="Inputs"/> (host variable names; *USER is the job's user). <see cref="Outputs"/>
/// are the INTO variables, each with its indicator variable or null. <see cref="Notes"/> say what
/// was changed or dropped; <see cref="Problem"/> why the statement isn't supported.
/// </summary>
public sealed record SqlTranslation(
    SqlKind Kind,
    string? Sql = null,
    IReadOnlyList<string>? InputNames = null,
    IReadOnlyList<(string Name, string? Indicator)>? OutputNames = null,
    string? Cursor = null,
    string? Statement = null,
    IReadOnlyList<string>? NoteList = null,
    string? Problem = null)
{
    public IReadOnlyList<string> Inputs => InputNames ?? [];
    public IReadOnlyList<(string Name, string? Indicator)> Outputs => OutputNames ?? [];
    public IReadOnlyList<string> Notes => NoteList ?? [];
}

public static class SqlTranslator
{
    private enum T
    {
        Word,
        Number,
        String,
        Quoted,
        Host,
        Marker,
        Symbol,
    }

    /// <param name="Spaced">Whether white space came before it, which the translation keeps.</param>
    private sealed record Token(T Type, string Text, bool Spaced = true)
    {
        public bool Is(string word) => Type == T.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);
        public bool IsSymbol(string symbol) => Type == T.Symbol && Text == symbol;
        public bool IsAny(params string[] words) => Type == T.Word && words.Contains(Text, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Unsupported(string message) : Exception(message);

    /// <summary>Splits a script into statements at each semicolon outside a string or comment, with each one's first line.</summary>
    public static List<(string Text, int Line)> SplitScript(string script)
    {
        var statements = new List<(string, int)>();
        var text = new StringBuilder();
        var line = 1;
        var startLine = 0;
        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            if (c == '-' && i + 1 < script.Length && script[i + 1] == '-')
            {
                while (i < script.Length && script[i] != '\n') i++;
                i--;
                continue;
            }
            if (c == '/' && i + 1 < script.Length && script[i + 1] == '*')
            {
                var end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? script.Length : end + 2;
                line += script[i..end].Count(ch => ch == '\n');
                i = end - 1;
                continue;
            }
            if (c is '\'' or '"')
            {
                var end = i + 1;
                while (end < script.Length && script[end] != c) end++;
                var quoted = script[i..Math.Min(end + 1, script.Length)];
                if (startLine == 0) startLine = line;
                text.Append(quoted);
                line += quoted.Count(ch => ch == '\n');
                i = end;
                continue;
            }
            if (c == ';')
            {
                if (text.ToString().Trim().Length > 0) statements.Add((text.ToString().Trim(), startLine));
                text.Clear();
                startLine = 0;
                continue;
            }
            if (c == '\n') line++;
            if (startLine == 0 && !char.IsWhiteSpace(c)) startLine = line;
            text.Append(c);
        }
        if (text.ToString().Trim().Length > 0) statements.Add((text.ToString().Trim(), startLine));
        return statements;
    }

    public static SqlTranslation Translate(string db2)
    {
        var notes = new List<string>();
        try
        {
            var tokens = Tokenize(db2);
            if (tokens.Count == 0) return new SqlTranslation(SqlKind.NoOp);
            return Statement(tokens, notes) with { NoteList = notes };
        }
        catch (Unsupported e)
        {
            return new SqlTranslation(SqlKind.Unsupported, Problem: e.Message, NoteList: notes);
        }
    }

    // ---- tokens ----

    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var i = 0;
        var spaced = false;
        var counted = 0;
        while (i < sql.Length)
        {
            // Each token remembers whether white space or a comment came before it.
            if (tokens.Count > counted)
            {
                tokens[^1] = tokens[^1] with { Spaced = spaced };
                counted = tokens.Count;
                spaced = false;
            }
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                spaced = true;
                i++;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                spaced = true;
                while (i < sql.Length && sql[i] != '\n') i++;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                spaced = true;
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
            }
            else if (c is '\'' or '"')
            {
                var start = i++;
                while (i < sql.Length)
                {
                    if (sql[i] == c)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == c) i++;
                        else break;
                    }
                    i++;
                }
                if (i >= sql.Length) throw new Unsupported("a string or quoted name isn't closed");
                i++;
                tokens.Add(new Token(c == '\'' ? T.String : T.Quoted, sql[start..i]));
            }
            else if (c == ':' && i + 1 < sql.Length && (char.IsLetter(sql[i + 1]) || sql[i + 1] is '_' or '#' or '@' or '$'))
            {
                var start = ++i;
                while (i < sql.Length && (IsWordChar(sql[i]) || sql[i] == '.' && i + 1 < sql.Length && IsWordChar(sql[i + 1]))) i++;
                tokens.Add(new Token(T.Host, sql[start..i]));
            }
            else if (char.IsDigit(c) || c == '.' && i + 1 < sql.Length && char.IsDigit(sql[i + 1]))
            {
                var start = i;
                while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '.')) i++;
                if (i < sql.Length && sql[i] is 'e' or 'E' && i + 1 < sql.Length && (char.IsDigit(sql[i + 1]) || sql[i + 1] is '+' or '-'))
                {
                    i += 2;
                    while (i < sql.Length && char.IsDigit(sql[i])) i++;
                }
                tokens.Add(new Token(T.Number, sql[start..i]));
            }
            else if (IsWordChar(c))
            {
                var start = i;
                while (i < sql.Length && IsWordChar(sql[i])) i++;
                tokens.Add(new Token(T.Word, sql[start..i]));
            }
            else if (c == '?')
            {
                tokens.Add(new Token(T.Marker, "?"));
                i++;
            }
            else
            {
                var two = i + 1 < sql.Length ? sql.Substring(i, 2) : "";
                if (two is "<>" or "<=" or ">=" or "!=" or "||" or "¬=")
                {
                    tokens.Add(new Token(T.Symbol, two == "¬=" ? "<>" : two));
                    i += 2;
                }
                else if (two == "!!")
                {
                    tokens.Add(new Token(T.Symbol, "||"));
                    i += 2;
                }
                else
                {
                    tokens.Add(new Token(T.Symbol, c.ToString()));
                    i++;
                }
            }
        }
        if (tokens.Count > counted) tokens[^1] = tokens[^1] with { Spaced = spaced };
        // An indicator variable follows its host variable, with or without INDICATOR: one token, "name\tindicator".
        for (var k = 0; k < tokens.Count - 1; k++)
        {
            if (tokens[k].Type != T.Host) continue;
            var next = tokens[k + 1].Is("INDICATOR") && k + 2 < tokens.Count ? k + 2 : k + 1;
            if (tokens[next].Type != T.Host) continue;
            tokens[k] = tokens[k] with { Text = $"{tokens[k].Text}\t{tokens[next].Text}" };
            tokens.RemoveRange(k + 1, next - k);
        }
        return tokens;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '#' or '@' or '$';

    /// <summary>The tokens as text, spaced as the statement was, with any second statement on its own line.</summary>
    private static string Join(IEnumerable<Token> tokens)
    {
        var text = new StringBuilder();
        Token? previous = null;
        foreach (var t in tokens)
        {
            if (previous is not null && previous.IsSymbol(";")) text.Append('\n');
            else if (previous is not null && t.Spaced && !(t.Type == T.Symbol && t.Text is "," or ")" or ";") && !previous.IsSymbol("(")) text.Append(' ');
            text.Append(t.Text);
            previous = t;
        }
        return text.ToString();
    }

    // ---- statements ----

    private static SqlTranslation Statement(List<Token> t, List<string> notes)
    {
        bool Starts(params string[] words) => t.Count >= words.Length && words.Select((w, i) => t[i].Is(w)).All(b => b);
        string Rest(int from) => string.Join(' ', t.Skip(from).Select(x => x.Text));

        if (Starts("SET", "OPTION")) return NoOp(notes, "SET OPTION is ignored");
        if (Starts("SET", "CURRENT") || Starts("SET", "SCHEMA") || Starts("SET", "PATH"))
            return NoOp(notes, $"SET {t[1].Text.ToUpperInvariant()} is ignored: name the library in each table, as LIB.TABLE");
        if (Starts("SET", "TRANSACTION")) return NoOp(notes, "SET TRANSACTION is ignored: SQL runs without commitment control");
        if (Starts("COMMIT") || Starts("ROLLBACK"))
            return NoOp(notes, $"{t[0].Text.ToUpperInvariant()} is ignored: SQL runs without commitment control");
        if (Starts("INCLUDE")) return NoOp(notes, "INCLUDE is ignored: SQLCODE and SQLSTATE are always there");
        if (Starts("LABEL", "ON") || Starts("COMMENT", "ON") || Starts("GRANT") || Starts("REVOKE"))
            return NoOp(notes, $"{t[0].Text.ToUpperInvariant()} is ignored");
        if (Starts("CREATE", "SCHEMA") || Starts("CREATE", "COLLECTION"))
            return NoOp(notes, "CREATE SCHEMA makes the library in a script (or use CRTLIB); it does nothing in a program");
        if (Starts("WHENEVER")) throw new Unsupported("WHENEVER isn't supported: test SQLCODE or SQLSTATE after each statement");

        if (Starts("SET") && t.Count > 3 && t[1].Type == T.Host && t[2].IsSymbol("="))
        {
            if (t.Skip(3).Any(x => x.Type == T.Host && TopLevelAssignment(t, x))) throw new Unsupported("SET with more than one host variable isn't supported");
            var body = Rewrite([new Token(T.Word, "SELECT"), .. t.Skip(3)], notes, out var inputs, out _);
            return new SqlTranslation(SqlKind.Query, body, inputs, [Output(t[1])]);
        }

        if (Starts("DECLARE"))
        {
            var at = t.FindIndex(x => x.Is("CURSOR"));
            if (at < 0) throw new Unsupported($"DECLARE {Rest(1).Split(' ').FirstOrDefault()?.ToUpperInvariant()} isn't supported: only cursors can be declared");
            var forAt = t.FindIndex(at, x => x.Is("FOR"));
            if (forAt < 0 || forAt + 1 >= t.Count) throw new Unsupported("DECLARE CURSOR needs FOR and a SELECT or statement name");
            if (t.Skip(at).Take(forAt - at).Any(x => x.Is("RETURN") && !t[t.IndexOf(x) - 1].Is("WITHOUT")))
                throw new Unsupported("a cursor WITH RETURN isn't supported");
            if (t.Take(at).Any(x => x.Is("SCROLL") && !t[Math.Max(0, t.IndexOf(x) - 1)].Is("NO")))
                notes.Add("SCROLL is ignored: only FETCH NEXT works");
            var name = t[1].Text.ToUpperInvariant();
            if (forAt + 2 == t.Count && t[forAt + 1].Type == T.Word)
                return new SqlTranslation(SqlKind.DeclareCursor, Cursor: name, Statement: t[forAt + 1].Text.ToUpperInvariant());
            var select = Rewrite(t.Skip(forAt + 1).ToList(), notes, out var inputs, out var outputs);
            if (outputs.Count > 0) throw new Unsupported("a cursor's SELECT can't have INTO");
            return new SqlTranslation(SqlKind.DeclareCursor, select, inputs, Cursor: name);
        }

        if (Starts("OPEN") && t.Count >= 2)
        {
            var inputs = new List<string>();
            if (t.Count > 2)
            {
                if (!t[2].Is("USING") || t.Skip(3).Any(x => x.Type != T.Host && !x.IsSymbol(",")))
                    throw new Unsupported("OPEN takes only USING and host variables");
                inputs.AddRange(t.Skip(3).Where(x => x.Type == T.Host).Select(x => Output(x).Name));
            }
            return new SqlTranslation(SqlKind.Open, InputNames: inputs, Cursor: t[1].Text.ToUpperInvariant());
        }

        if (Starts("FETCH"))
        {
            var i = 1;
            if (i < t.Count && t[i].IsAny("PRIOR", "FIRST", "LAST", "BEFORE", "AFTER", "CURRENT", "RELATIVE", "ABSOLUTE"))
                throw new Unsupported($"FETCH {t[i].Text.ToUpperInvariant()} isn't supported: only FETCH NEXT");
            if (i < t.Count && t[i].Is("NEXT")) i++;
            if (i < t.Count && t[i].Is("FROM")) i++;
            if (i >= t.Count) throw new Unsupported("FETCH needs a cursor name");
            var cursor = t[i++].Text.ToUpperInvariant();
            if (i < t.Count && t[i].Is("FOR")) throw new Unsupported("a multiple-row FETCH (FOR n ROWS) isn't supported");
            if (i >= t.Count || !t[i].Is("INTO")) throw new Unsupported("FETCH needs INTO and host variables");
            var outputs = t.Skip(i + 1).Where(x => x.Type == T.Host).Select(Output).ToList();
            if (outputs.Count == 0 || t.Skip(i + 1).Any(x => x.Type != T.Host && !x.IsSymbol(",")))
                throw new Unsupported("FETCH INTO takes host variables only");
            return new SqlTranslation(SqlKind.Fetch, OutputNames: outputs, Cursor: cursor);
        }

        if (Starts("CLOSE") && t.Count == 2) return new SqlTranslation(SqlKind.Close, Cursor: t[1].Text.ToUpperInvariant());

        if (Starts("PREPARE") && t.Count == 4 && t[2].Is("FROM") && t[3].Type is T.Host or T.String)
        {
            return t[3].Type == T.Host
                ? new SqlTranslation(SqlKind.Prepare, InputNames: [Output(t[3]).Name], Statement: t[1].Text.ToUpperInvariant())
                : new SqlTranslation(SqlKind.Prepare, Sql: Unquote(t[3].Text), Statement: t[1].Text.ToUpperInvariant());
        }
        if (Starts("EXECUTE", "IMMEDIATE") && t.Count == 3 && t[2].Type is T.Host or T.String)
        {
            return t[2].Type == T.Host
                ? new SqlTranslation(SqlKind.ExecuteImmediate, InputNames: [Output(t[2]).Name])
                : new SqlTranslation(SqlKind.ExecuteImmediate, Sql: Unquote(t[2].Text));
        }
        if (Starts("EXECUTE") && t.Count >= 2 && t[1].Type == T.Word && (t.Count == 2 || t[2].Is("USING")))
        {
            if (t.Skip(3).Any(x => x.Type != T.Host && !x.IsSymbol(","))) throw new Unsupported("EXECUTE USING takes host variables only");
            return new SqlTranslation(SqlKind.ExecutePrepared,
                InputNames: t.Skip(3).Where(x => x.Type == T.Host).Select(x => Output(x).Name).ToList(), Statement: t[1].Text.ToUpperInvariant());
        }

        if (Starts("GET", "DIAGNOSTICS"))
        {
            if (t.Count == 5 && t[2].Type == T.Host && t[3].IsSymbol("=") && t[4].Is("ROW_COUNT"))
                return new SqlTranslation(SqlKind.RowCount, OutputNames: [Output(t[2])]);
            throw new Unsupported("GET DIAGNOSTICS is supported only as GET DIAGNOSTICS :variable = ROW_COUNT");
        }

        if (!t[0].IsAny("SELECT", "WITH", "VALUES", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER"))
            throw new Unsupported($"the SQL statement {t[0].Text.ToUpperInvariant()} isn't supported");
        if (t[0].Is("CREATE"))
        {
            var what = t.Skip(1).FirstOrDefault(x => !x.IsAny("OR", "REPLACE", "UNIQUE"));
            if (what is null || !what.IsAny("TABLE", "INDEX", "VIEW"))
                throw new Unsupported($"CREATE {what?.Text.ToUpperInvariant()} isn't supported: only tables, indexes and views");
        }

        var sql = Rewrite(t, notes, out var hostInputs, out var hostOutputs);
        var query = t[0].IsAny("SELECT", "WITH", "VALUES");
        if (hostOutputs.Count > 0 && !query) throw new Unsupported("INTO with host variables is supported only on SELECT and VALUES");
        var kind = !query ? SqlKind.Execute : hostOutputs.Count > 0 ? SqlKind.Query : SqlKind.Select;
        return new SqlTranslation(kind, sql, hostInputs, hostOutputs);
    }

    private static bool TopLevelAssignment(List<Token> t, Token host)
    {
        var i = t.IndexOf(host);
        return i + 1 < t.Count && t[i + 1].IsSymbol("=") && i > 0 && t[i - 1].IsSymbol(",");
    }

    private static SqlTranslation NoOp(List<string> notes, string note)
    {
        notes.Add(note);
        return new SqlTranslation(SqlKind.NoOp);
    }

    private static (string Name, string? Indicator) Output(Token host)
    {
        var parts = host.Text.Split('\t');
        return (parts[0], parts.Length > 1 ? parts[1] : null);
    }

    private static string Unquote(string literal) => literal[1..^1].Replace("''", "'");

    // ---- the rewrite ----

    private static readonly string[] Isolation = ["UR", "CS", "RS", "RR", "NC", "CHG", "ALL", "NONE"];
    private static readonly string[] DurationUnits =
        ["DAY", "DAYS", "MONTH", "MONTHS", "YEAR", "YEARS", "HOUR", "HOURS", "MINUTE", "MINUTES", "SECOND", "SECONDS", "MICROSECOND", "MICROSECONDS"];
    private static readonly string[] SystemSchemas = ["QSYS2", "SYSIBM", "SYSCAT", "SYSTOOLS", "SYSPROC", "QSYS", "SYSIBMADM"];

    /// <summary>
    /// Rewrites a data or definition statement, token by token. Host variables become @h parameters
    /// (<paramref name="inputs"/>), and a SELECT's or VALUES' INTO comes out as <paramref name="outputs"/>.
    /// </summary>
    private static string Rewrite(List<Token> source, List<string> notes, out List<string> inputs, out List<(string, string?)> outputs)
    {
        var t = source.ToList();
        outputs = [];
        inputs = [];
        void Note(string note)
        {
            if (!notes.Contains(note)) notes.Add(note);
        }
        Token Word(string text) => new(T.Word, text);
        // A library is a schema of the connection; QGPL's database is the connection's own, SQLite's "main".
        // QTEMP, the job's temporary library, is SQLite's temp.
        Token Schema(Token library) =>
            library.Text.Equals("QGPL", StringComparison.OrdinalIgnoreCase) ? Word("main")
            : library.Text.Equals("QTEMP", StringComparison.OrdinalIgnoreCase) ? Word("temp") : library;
        Token Symbol(string text, bool spaced = true) => new(T.Symbol, text, spaced);
        bool At(int i, params string[] words) => i >= 0 && i + words.Length <= t.Count && words.Select((w, k) => t[i + k].Is(w)).All(b => b);
        var query = t[0].IsAny("SELECT", "WITH", "VALUES");
        var definition = t[0].IsAny("CREATE", "ALTER", "DROP");

        // INTO :a, :b of a SELECT or VALUES.
        if (query)
        {
            var depth = 0;
            for (var i = 0; i < t.Count; i++)
            {
                if (t[i].IsSymbol("(")) depth++;
                else if (t[i].IsSymbol(")")) depth--;
                else if (depth == 0 && t[i].Is("INTO") && i + 1 < t.Count && t[i + 1].Type == T.Host)
                {
                    var end = i + 1;
                    while (end < t.Count && (t[end].Type == T.Host || t[end].IsSymbol(",") && end + 1 < t.Count && t[end + 1].Type == T.Host)) end++;
                    outputs.AddRange(t.Skip(i + 1).Take(end - i - 1).Where(x => x.Type == T.Host).Select(Output));
                    t.RemoveRange(i, end - i);
                    break;
                }
            }
            // VALUES (a, b) INTO :x, :y is SELECT a, b.
            if (t[0].Is("VALUES") && outputs.Count > 0)
            {
                t[0] = Word("SELECT");
                if (t.Count > 2 && t[1].IsSymbol("(") && MatchingParen(t, 1) == t.Count - 1)
                {
                    t.RemoveAt(t.Count - 1);
                    t.RemoveAt(1);
                }
            }
        }

        // CREATE OR REPLACE: drop it first.
        var prefix = new List<Token>();
        if (At(0, "CREATE", "OR", "REPLACE") && t.Count > 4 && t[3].IsAny("TABLE", "VIEW"))
        {
            var qualified = t.Count > 6 && t[5].Type == T.Symbol && t[5].Text is "." or "/";
            Token[] name = qualified ? [Schema(t[4]) with { Spaced = true }, Symbol(".", false), t[6] with { Spaced = false }]
                : [Word(Machine.DefaultSchemaMarker) with { Spaced = true }, Symbol(".", false), t[4] with { Spaced = false }];
            prefix = [new Token(T.Word, "DROP", false), Word(t[3].Text.ToUpperInvariant()), Word("IF"), Word("EXISTS"), .. name, Symbol(";", false)];
            if (t[3].Is("TABLE")) Note("CREATE OR REPLACE TABLE drops the table and its rows first");
            t.RemoveRange(1, 2);
        }

        var result = new List<Token>();
        var expectTable = false;
        var fromDepths = new HashSet<int>();
        var parenDepth = 0;
        for (var i = 0; i < t.Count; i++)
        {
            var token = t[i];
            var previous = result.Count > 0 ? result[^1] : null;
            var next = i + 1 < t.Count ? t[i + 1] : null;

            if (token.IsSymbol("(")) parenDepth++;
            if (token.IsSymbol(")")) fromDepths.Remove(parenDepth--);

            // ---- a table name, with its library dropped ----
            if (expectTable && token.Type is T.Word or T.Quoted && !token.IsAny("IF", "NOT", "EXISTS", "ONLY"))
            {
                expectTable = false;
                if (token.IsAny("TABLE", "LATERAL", "FINAL", "NEW", "OLD", "UNNEST", "XMLTABLE", "JSON_TABLE") && next is not null && (next.IsSymbol("(") || next.Is("TABLE")))
                    throw new Unsupported($"{token.Text.ToUpperInvariant()} in FROM isn't supported");
                if (next is not null && next.Type == T.Symbol && next.Text is "." or "/" && i + 2 < t.Count && t[i + 2].Type is T.Word or T.Quoted)
                {
                    var isSystem = SystemSchemas.Contains(token.Text, StringComparer.OrdinalIgnoreCase);
                    if (isSystem && !t[i + 2].Is("SYSDUMMY1"))
                        throw new Unsupported($"{token.Text.ToUpperInvariant()}.{t[i + 2].Text.ToUpperInvariant()} isn't supported: the fake has no system catalog or services");
                    // SYSIBM.SYSDUMMY1 is the one system table there is: in QGPL's database, found without a library.
                    if (isSystem)
                        result.Add(t[i + 2] with { Spaced = token.Spaced });
                    else
                        result.AddRange([Schema(token) with { Spaced = token.Spaced }, Symbol(".", false), t[i + 2] with { Spaced = false }]);
                    i += 2;
                    continue;
                }
                // A table made with no library goes in the job's default library, which the machine fills in when it runs.
                if (At(0, "CREATE") && previous is not null && previous.IsAny("TABLE", "VIEW", "EXISTS") && !t.Any(x => x.Is("INDEX")))
                    result.AddRange([Word(Machine.DefaultSchemaMarker) with { Spaced = token.Spaced }, Symbol(".", false), token with { Spaced = false }]);
                else
                    result.Add(token);
                continue;
            }
            if (token.Type != T.Word || !token.IsAny("IF", "NOT", "EXISTS", "ONLY")) expectTable = false;

            if (token.Type == T.Host)
            {
                inputs.Add(Output(token).Name);
                result.Add(Word($"@h{inputs.Count - 1}"));
                continue;
            }
            if (token.Type == T.Marker)
            {
                // A parameter marker takes its value from USING, in order.
                inputs.Add("?");
                result.Add(Word($"@h{inputs.Count - 1}"));
                continue;
            }
            if (token.Type != T.Word)
            {
                if (token.IsSymbol(",") && fromDepths.Contains(parenDepth)) expectTable = true;
                result.Add(token);
                continue;
            }

            // ---- where a table name comes next ----
            if (token.Is("FROM"))
            {
                // Not the FROM of TRIM(x FROM y), SUBSTRING(x FROM n) or EXTRACT.
                if (!(previous is not null && previous.IsAny("BOTH", "LEADING", "TRAILING")) && !InsideFunctionFrom(t, i))
                {
                    fromDepths.Add(parenDepth);
                    expectTable = true;
                }
                result.Add(token);
                // DELETE FROM t x: SQLite wants AS before the correlation name.
                if (At(0, "DELETE") && i == 1 && AliasAfterTable(t, i + 1) is { } aliasAt) t.Insert(aliasAt, Word("AS"));
                continue;
            }
            if (token.IsAny("WHERE", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "SET", "ON", "USING")) fromDepths.Remove(parenDepth);
            if (token.IsAny("JOIN", "TABLE", "INDEX", "VIEW", "REFERENCES") || token.Is("INTO") && next?.Type != T.Host
                || token.Is("UPDATE") && i == 0 || token.Is("ON") && At(0, "CREATE") && t.Any(x => x.Is("INDEX")) && parenDepth == 0)
            {
                expectTable = true;
                result.Add(token);
                if (token.Is("UPDATE") && AliasAfterTable(t, i + 1) is { } aliasAt) t.Insert(aliasAt, Word("AS"));
                continue;
            }

            // ---- row limits ----
            if (token.Is("FETCH") && next is not null && next.IsAny("FIRST", "NEXT"))
            {
                var j = i + 2;
                var count = j < t.Count && !t[j].IsAny("ROW", "ROWS") ? t[j++] : new Token(T.Number, "1");
                if (!(j + 1 < t.Count && t[j].IsAny("ROW", "ROWS") && t[j + 1].Is("ONLY"))) throw new Unsupported("FETCH FIRST needs ROWS ONLY");
                if (count.Type == T.Host)
                {
                    inputs.Add(Output(count).Name);
                    count = Word($"@h{inputs.Count - 1}");
                }
                // OFFSET n ROWS came first: SQLite wants LIMIT before OFFSET.
                var offsetAt = result.FindLastIndex(x => x.Is("OFFSET"));
                if (offsetAt >= 0 && offsetAt == result.Count - 2)
                {
                    result.InsertRange(offsetAt, [Word("LIMIT"), count]);
                }
                else
                {
                    result.Add(Word("LIMIT"));
                    result.Add(count);
                }
                i = j + 1;
                continue;
            }
            if (token.IsAny("ROW", "ROWS") && result.Count >= 2 && result[^2].Is("OFFSET"))
            {
                // OFFSET n ROWS with no FETCH FIRST after it.
                if (!(next is not null && next.Is("FETCH"))) result.InsertRange(result.Count - 2, [Word("LIMIT"), new Token(T.Number, "-1")]);
                continue;
            }

            // ---- clauses with nothing to do here ----
            if (token.Is("WITH") && next is not null && next.IsAny(Isolation) && (i + 2 >= t.Count || !t[i + 2].IsSymbol("(") && !t[i + 2].Is("AS")))
            {
                Note($"WITH {next.Text.ToUpperInvariant()} is dropped: SQL runs without commitment control");
                i++;
                continue;
            }
            if (token.Is("FOR") && (At(i + 1, "READ", "ONLY") || At(i + 1, "FETCH", "ONLY")))
            {
                i += 2;
                continue;
            }
            if (token.Is("FOR") && At(i + 1, "UPDATE"))
            {
                Note("FOR UPDATE is dropped");
                i++;
                if (At(i + 1, "OF"))
                {
                    i += 2;
                    while (i + 1 < t.Count && t[i + 1].IsSymbol(",")) i += 2;
                }
                continue;
            }
            if (token.Is("OPTIMIZE") && At(i + 1, "FOR"))
            {
                while (i < t.Count && !t[i].IsAny("ROW", "ROWS")) i++;
                continue;
            }
            if (token.Is("WHERE") && At(i + 1, "CURRENT", "OF")) throw new Unsupported("WHERE CURRENT OF a cursor isn't supported: use the row's key");

            // ---- operators and special registers ----
            if (token.Is("CONCAT") && next is not null && !next.IsSymbol("(") && previous is not null)
            {
                result.Add(Symbol("||"));
                continue;
            }
            if (token.Is("CURRENT") && next is not null && next.IsAny("DATE", "TIME", "TIMESTAMP", "USER", "SCHEMA", "SERVER", "TIMEZONE", "PATH"))
            {
                token = Word($"CURRENT_{next.Text.ToUpperInvariant()}");
                i++;
                next = i + 1 < t.Count ? t[i + 1] : null;
            }
            if (token.IsAny("CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP"))
            {
                // SQLite's own are in UTC, and it has no microseconds.
                var text = token.Text.ToUpperInvariant() switch
                {
                    "CURRENT_DATE" => "date('now', 'localtime')",
                    "CURRENT_TIME" => "time('now', 'localtime')",
                    _ => "strftime('%Y-%m-%d %H:%M:%f', 'now', 'localtime')",
                };
                result.Add(Word(previous is not null && previous.Is("DEFAULT") ? $"({text})" : text));
                continue;
            }
            if (token.IsAny("CURRENT_USER", "SESSION_USER"))
            {
                inputs.Add("*USER");
                result.Add(Word($"@h{inputs.Count - 1}"));
                continue;
            }
            if (token.IsAny("CURRENT_SCHEMA", "CURRENT_SERVER", "CURRENT_TIMEZONE", "CURRENT_PATH"))
                throw new Unsupported($"{token.Text.ToUpperInvariant().Replace('_', ' ')} isn't supported");
            if (token.IsAny(DurationUnits) && previous is not null && (previous.Type is T.Number || previous.IsSymbol(")") || previous.Text.StartsWith("@h"))
                && !(next?.IsSymbol("(") ?? false) && result.Count >= 2 && result[^2].Type == T.Symbol && result[^2].Text is "+" or "-")
                throw new Unsupported($"date arithmetic with {token.Text.ToUpperInvariant()} isn't supported");
            if (token.Is("RRN") && next is not null && next.IsSymbol("(") && i + 3 < t.Count && t[i + 3].IsSymbol(")"))
            {
                result.AddRange([t[i + 2] with { Spaced = token.Spaced }, Symbol(".", false), new Token(T.Word, "rowid", false)]);
                i += 3;
                continue;
            }

            // ---- column options of CREATE TABLE and ALTER TABLE ----
            if (definition)
            {
                if (token.Is("FOR") && At(i + 1, "COLUMN"))
                {
                    i += 2;
                    continue;
                }
                if (token.Is("FOR") && At(i + 1, "SYSTEM", "NAME"))
                {
                    i += 3;
                    continue;
                }
                if (token.IsAny("CCSID", "RCDFMT"))
                {
                    i++;
                    continue;
                }
                if (token.Is("ALLOCATE") && next is not null && next.IsSymbol("("))
                {
                    i = MatchingParen(t, i + 1);
                    continue;
                }
                if (At(i, "IMPLICITLY", "HIDDEN"))
                {
                    i++;
                    continue;
                }
                if (token.Is("WITH") && At(i + 1, "DEFAULT"))
                {
                    // WITH DEFAULT and no value: the type's default.
                    var hasValue = i + 2 < t.Count && !t[i + 2].IsSymbol(",") && !t[i + 2].IsSymbol(")") && t[i + 2].Type is T.Number or T.String or T.Word;
                    result.Add(Word("DEFAULT"));
                    if (!hasValue) result.Add(Word(DefaultFor(ColumnType(result))));
                    i++;
                    continue;
                }
                if (token.Is("GENERATED") && next is not null && next.IsAny("ALWAYS", "BY"))
                {
                    var j = i + (next.Is("BY") ? 3 : 2);
                    if (!At(j, "AS", "IDENTITY")) throw new Unsupported("GENERATED is supported only as an IDENTITY column");
                    j += 2;
                    if (j < t.Count && t[j].IsSymbol("(")) j = MatchingParen(t, j) + 1;
                    i = j - 1;
                    MakeIdentity(result, t, i + 1, Note);
                    continue;
                }
            }

            result.Add(token);
        }

        return Join([.. prefix, .. result]);
    }

    /// <summary>Whether the FROM at <paramref name="i"/> is inside TRIM(... FROM ...), SUBSTRING or EXTRACT, not a table list.</summary>
    private static bool InsideFunctionFrom(List<Token> t, int i)
    {
        var depth = 0;
        for (var k = i - 1; k > 0; k--)
        {
            if (t[k].IsSymbol(")")) depth++;
            else if (t[k].IsSymbol("("))
            {
                if (depth == 0) return t[k - 1].IsAny("TRIM", "SUBSTRING", "EXTRACT", "LTRIM", "RTRIM", "POSITION");
                depth--;
            }
            else if (depth == 0 && t[k].Is("SELECT")) return false;
        }
        return false;
    }

    /// <summary>Where a correlation name with no AS follows the table name at <paramref name="i"/>, if one does.</summary>
    private static int? AliasAfterTable(List<Token> t, int i)
    {
        if (i + 2 < t.Count && t[i + 1].Type == T.Symbol && t[i + 1].Text is "." or "/") i += 2;
        return i + 1 < t.Count && t[i + 1].Type == T.Word && !t[i + 1].IsAny("SET", "WHERE", "AS") ? i + 1 : null;
    }

    private static int MatchingParen(List<Token> t, int open)
    {
        var depth = 0;
        for (var i = open; i < t.Count; i++)
        {
            if (t[i].IsSymbol("(")) depth++;
            else if (t[i].IsSymbol(")") && --depth == 0) return i;
        }
        throw new Unsupported("a parenthesis isn't closed");
    }

    /// <summary>Where the column definition being written starts: after the last comma or opening parenthesis at the table's level.</summary>
    private static int ColumnStart(List<Token> result)
    {
        var depth = 0;
        for (var i = result.Count - 1; i >= 0; i--)
        {
            if (result[i].IsSymbol(")")) depth++;
            else if (result[i].IsSymbol("("))
            {
                if (depth == 0) return i + 1;
                depth--;
            }
            else if (result[i].IsSymbol(",") && depth == 0) return i + 1;
        }
        return 0;
    }

    /// <summary>The type of the column definition being written: the word after its name (after ADD COLUMN, in ALTER TABLE).</summary>
    private static string ColumnType(List<Token> result)
    {
        var start = ColumnStart(result);
        var column = result.FindIndex(start, x => x.Is("COLUMN"));
        if (column >= 0) start = column + 1;
        return start + 1 < result.Count ? result[start + 1].Text.ToUpperInvariant() : "";
    }

    private static string DefaultFor(string type) => type switch
    {
        "CHAR" or "CHARACTER" or "VARCHAR" or "CLOB" or "GRAPHIC" or "VARGRAPHIC" or "NCHAR" or "NVARCHAR" => "''",
        "DATE" => "(date('now', 'localtime'))",
        "TIME" => "(time('now', 'localtime'))",
        "TIMESTAMP" => "(strftime('%Y-%m-%d %H:%M:%f', 'now', 'localtime'))",
        _ => "0",
    };

    /// <summary>
    /// An identity column is SQLite's INTEGER PRIMARY KEY AUTOINCREMENT, which has to be the
    /// table's whole primary key: a PRIMARY KEY (column) elsewhere in the table is taken out.
    /// </summary>
    private static void MakeIdentity(List<Token> result, List<Token> t, int rest, Action<string> note)
    {
        var start = ColumnStart(result);
        if (start + 1 >= result.Count) throw new Unsupported("an IDENTITY column needs a name and type");
        var name = result[start].Text;
        // The type, with any length, becomes INTEGER; NOT NULL goes, as the key implies it.
        result.RemoveRange(start + 1, result.Count - start - 1);
        result.AddRange([new Token(T.Word, "INTEGER"), new Token(T.Word, "PRIMARY"), new Token(T.Word, "KEY"), new Token(T.Word, "AUTOINCREMENT")]);
        note($"identity column {name.ToUpperInvariant()} becomes INTEGER PRIMARY KEY AUTOINCREMENT");
        // PRIMARY KEY later in this column's definition.
        var depth = 0;
        for (var i = rest; i < t.Count; i++)
        {
            if (t[i].IsSymbol("(")) depth++;
            else if (t[i].IsSymbol(")")) depth--;
            if (depth < 0 || depth == 0 && t[i].IsSymbol(",")) break;
            if (depth == 0 && t[i].Is("PRIMARY") && i + 1 < t.Count && t[i + 1].Is("KEY")) t.RemoveRange(i--, 2);
            else if (depth == 0 && t[i].Is("NOT") && i + 1 < t.Count && t[i + 1].Is("NULL")) t.RemoveRange(i--, 2);
        }
        // [CONSTRAINT name] PRIMARY KEY (column) as a table constraint.
        for (var i = rest; i + 4 < t.Count; i++)
        {
            if (!(t[i].Is("PRIMARY") && t[i + 1].Is("KEY") && t[i + 2].IsSymbol("("))) continue;
            if (!(t[i + 3].Text.Equals(name, StringComparison.OrdinalIgnoreCase) && t[i + 4].IsSymbol(")")))
                throw new Unsupported($"identity column {name.ToUpperInvariant()} has to be the whole primary key");
            var from = i >= 2 && t[i - 2].Is("CONSTRAINT") ? i - 2 : i;
            if (from > 0 && t[from - 1].IsSymbol(",")) from--;
            t.RemoveRange(from, i + 5 - from);
            break;
        }
        // The same in what's already written.
        for (var i = 0; i + 4 < start; i++)
        {
            if (!(result[i].Is("PRIMARY") && result[i + 1].Is("KEY") && result[i + 2].IsSymbol("("))) continue;
            if (!(result[i + 3].Text.Equals(name, StringComparison.OrdinalIgnoreCase) && result[i + 4].IsSymbol(")")))
                throw new Unsupported($"identity column {name.ToUpperInvariant()} has to be the whole primary key");
            var from = i >= 2 && result[i - 2].Is("CONSTRAINT") ? i - 2 : i;
            result.RemoveRange(from, i + 6 - from > result.Count ? result.Count - from : i + 6 - from);
            break;
        }
    }
}
