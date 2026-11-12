// The fake's SQLite database: source members, the objects made from them (programs and display
// files), what the last save of a member found unsupported, and the tables embedded SQL works on.
// One connection, used under a lock: programs run on their own threads.

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FakeIbmI;

public enum Severity
{
    /// <summary>Something translated or ignored; the code runs.</summary>
    Info,

    /// <summary>It may fail when it runs, such as SQL on a table that doesn't exist yet.</summary>
    Warning,

    /// <summary>The fake can't run this line. Reaching it at run time ends the program with an error.</summary>
    Unsupported,
}

/// <summary>One finding from checking a member: its line (1-based; 0 for the whole member), and what was found.</summary>
public sealed record Diagnostic(int Line, Severity Severity, string Message);

/// <summary>A source member, such as QGPL/QRPGLESRC(HELLO), type RPGLE.</summary>
public sealed record Member(string Library, string File, string Name, string Type, string Source)
{
    public override string ToString() => $"{Library}/{File}({Name})";
}

/// <summary>A spooled file a program printed: its lines, as text.</summary>
public sealed record SpoolRecord(long Id, string Name, string User, int Pages, int Width, DateTime Created, string Text);

/// <summary>One user profile as the table QFAKE_USER keeps it. Status is *ENABLED or *DISABLED, PasswordExpired *YES or *NO.</summary>
public sealed record UserRow(string User, string Password, string Status, string PasswordExpired, string Text);

/// <summary>An object made from a member: type *PGM or *FILE, with the source it was made from.</summary>
public sealed record StoredObject(string Library, string Name, string Type, string Attribute, string Source, long Version);

public sealed class Store : IDisposable
{
    private readonly SqliteConnection _connection;

    private Store(SqliteConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Guards every use of <see cref="Connection"/>.</summary>
    internal object Gate { get; } = new();

    internal SqliteConnection Connection => _connection;

    /// <summary>Opens the database file, making it when it's missing. Null keeps it in memory, gone when the fake stops.</summary>
    public static Store Open(string? path = null)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? ":memory:" }.ToString());
        connection.Open();
        var store = new Store(connection);
        Db2Functions.Register(connection);
        store.Run("""
            CREATE TABLE IF NOT EXISTS QFAKE_MEMBER (
                LIBRARY TEXT NOT NULL, FILE TEXT NOT NULL, NAME TEXT NOT NULL, TYPE TEXT NOT NULL, SOURCE TEXT NOT NULL,
                PRIMARY KEY (LIBRARY, FILE, NAME));
            CREATE TABLE IF NOT EXISTS QFAKE_DIAGNOSTIC (
                LIBRARY TEXT NOT NULL, FILE TEXT NOT NULL, NAME TEXT NOT NULL, SEQ INTEGER NOT NULL,
                LINE INTEGER NOT NULL, SEVERITY INTEGER NOT NULL, MESSAGE TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS QFAKE_OBJECT (
                LIBRARY TEXT NOT NULL, NAME TEXT NOT NULL, TYPE TEXT NOT NULL, ATTRIBUTE TEXT NOT NULL, SOURCE TEXT NOT NULL,
                VERSION INTEGER NOT NULL, PRIMARY KEY (LIBRARY, NAME, TYPE));
            CREATE TABLE IF NOT EXISTS QFAKE_SPOOL (
                ID INTEGER PRIMARY KEY AUTOINCREMENT, NAME TEXT NOT NULL, USER TEXT NOT NULL, PAGES INTEGER NOT NULL,
                WIDTH INTEGER NOT NULL, CREATED TEXT NOT NULL, TEXT TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS QFAKE_USER (
                USERNAME TEXT NOT NULL PRIMARY KEY, PASSWORD TEXT NOT NULL, STATUS TEXT NOT NULL DEFAULT '*ENABLED',
                PWDEXP TEXT NOT NULL DEFAULT '*NO', TEXT TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS SYSDUMMY1 (IBMREQD TEXT NOT NULL);
            INSERT INTO SYSDUMMY1 SELECT 'Y' WHERE NOT EXISTS (SELECT 1 FROM SYSDUMMY1);
            """);
        return store;
    }

    public void Dispose() => _connection.Dispose();

    private void Run(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (Gate)
        {
            using var command = Command(sql, parameters);
            command.ExecuteNonQuery();
        }
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        lock (Gate)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader();
            var rows = new List<T>();
            while (reader.Read()) rows.Add(read(reader));
            return rows;
        }
    }

    // ---- members ----

    /// <summary>Saves the member, replacing one of the same name, with what checking it found.</summary>
    public void SaveMember(Member member, IReadOnlyList<Diagnostic> diagnostics)
    {
        lock (Gate)
        {
            using var transaction = _connection.BeginTransaction();
            Run("INSERT OR REPLACE INTO QFAKE_MEMBER VALUES (@l, @f, @n, @t, @s)",
                ("@l", member.Library), ("@f", member.File), ("@n", member.Name), ("@t", member.Type), ("@s", member.Source));
            Run("DELETE FROM QFAKE_DIAGNOSTIC WHERE LIBRARY = @l AND FILE = @f AND NAME = @n",
                ("@l", member.Library), ("@f", member.File), ("@n", member.Name));
            for (var i = 0; i < diagnostics.Count; i++)
            {
                Run("INSERT INTO QFAKE_DIAGNOSTIC VALUES (@l, @f, @n, @q, @line, @sev, @m)",
                    ("@l", member.Library), ("@f", member.File), ("@n", member.Name), ("@q", i),
                    ("@line", diagnostics[i].Line), ("@sev", (int)diagnostics[i].Severity), ("@m", diagnostics[i].Message));
            }
            transaction.Commit();
        }
    }

    private static Member ReadMember(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));

    /// <summary>Every member, by library, file and name.</summary>
    public IReadOnlyList<Member> Members() =>
        Query("SELECT LIBRARY, FILE, NAME, TYPE, SOURCE FROM QFAKE_MEMBER ORDER BY LIBRARY, FILE, NAME", ReadMember);

    /// <summary>The member of this name. A null library or file matches any, as *LIBL would.</summary>
    public Member? FindMember(string? library, string? file, string name) =>
        Query("""
            SELECT LIBRARY, FILE, NAME, TYPE, SOURCE FROM QFAKE_MEMBER
            WHERE NAME = @n AND (@l IS NULL OR LIBRARY = @l) AND (@f IS NULL OR FILE = @f) ORDER BY LIBRARY, FILE
            """, ReadMember, ("@n", name.ToUpperInvariant()), ("@l", library?.ToUpperInvariant()), ("@f", file?.ToUpperInvariant()))
            .FirstOrDefault();

    public void DeleteMember(Member member)
    {
        foreach (var table in new[] { "QFAKE_MEMBER", "QFAKE_DIAGNOSTIC" })
        {
            Run($"DELETE FROM {table} WHERE LIBRARY = @l AND FILE = @f AND NAME = @n",
                ("@l", member.Library), ("@f", member.File), ("@n", member.Name));
        }
    }

    /// <summary>What the last save of the member found.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics(Member member) =>
        Query("SELECT LINE, SEVERITY, MESSAGE FROM QFAKE_DIAGNOSTIC WHERE LIBRARY = @l AND FILE = @f AND NAME = @n ORDER BY SEQ",
            r => new Diagnostic(r.GetInt32(0), (Severity)r.GetInt32(1), r.GetString(2)),
            ("@l", member.Library), ("@f", member.File), ("@n", member.Name));

    // ---- objects ----

    public StoredObject SaveObject(string library, string name, string type, string attribute, string source)
    {
        var version = DateTime.UtcNow.Ticks;
        Run("INSERT OR REPLACE INTO QFAKE_OBJECT VALUES (@l, @n, @t, @a, @s, @v)",
            ("@l", library), ("@n", name), ("@t", type), ("@a", attribute), ("@s", source), ("@v", version));
        return new StoredObject(library, name, type, attribute, source, version);
    }

    /// <summary>The object of this name and type (*PGM or *FILE). A null library matches any.</summary>
    public StoredObject? FindObject(string? library, string name, string type) =>
        Query("""
            SELECT LIBRARY, NAME, TYPE, ATTRIBUTE, SOURCE, VERSION FROM QFAKE_OBJECT
            WHERE NAME = @n AND TYPE = @t AND (@l IS NULL OR LIBRARY = @l) ORDER BY LIBRARY
            """,
            r => new StoredObject(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5)),
            ("@n", name.ToUpperInvariant()), ("@t", type), ("@l", library?.ToUpperInvariant())).FirstOrDefault();

    public bool DeleteObject(StoredObject o)
    {
        lock (Gate)
        {
            using var command = Command("DELETE FROM QFAKE_OBJECT WHERE LIBRARY = @l AND NAME = @n AND TYPE = @t",
                [("@l", o.Library), ("@n", o.Name), ("@t", o.Type)]);
            return command.ExecuteNonQuery() > 0;
        }
    }

    // ---- spooled files ----

    // ---- users ----

    private static UserRow ReadUser(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));

    /// <summary>Every user profile, by name.</summary>
    public IReadOnlyList<UserRow> UserRows() =>
        Query("SELECT USERNAME, PASSWORD, STATUS, PWDEXP, TEXT FROM QFAKE_USER ORDER BY USERNAME", ReadUser);

    public UserRow? FindUser(string name) =>
        Query("SELECT USERNAME, PASSWORD, STATUS, PWDEXP, TEXT FROM QFAKE_USER WHERE USERNAME = @n", ReadUser, ("@n", name)).FirstOrDefault();

    /// <summary>Adds the user, or replaces the one of that name.</summary>
    public void SaveUser(UserRow user) =>
        Run("INSERT OR REPLACE INTO QFAKE_USER VALUES (@u, @p, @s, @e, @t)",
            ("@u", user.User), ("@p", user.Password), ("@s", user.Status), ("@e", user.PasswordExpired), ("@t", user.Text));

    /// <summary>Keeps a user's password and flags, as signing on changes them, leaving its text.</summary>
    public void UpdateUser(string name, string password, bool disabled, bool passwordExpired) =>
        Run("UPDATE QFAKE_USER SET PASSWORD = @p, STATUS = @s, PWDEXP = @e WHERE USERNAME = @u",
            ("@u", name), ("@p", password), ("@s", disabled ? "*DISABLED" : "*ENABLED"), ("@e", passwordExpired ? "*YES" : "*NO"));

    /// <summary>Keeps the printed lines as a new spooled file, 60 lines to a page. Returns its number.</summary>
    public long AddSpool(string name, string user, int width, IReadOnlyList<string> lines)
    {
        lock (Gate)
        {
            using var command = Command("""
                INSERT INTO QFAKE_SPOOL (NAME, USER, PAGES, WIDTH, CREATED, TEXT) VALUES (@n, @u, @p, @w, @c, @t);
                SELECT last_insert_rowid();
                """,
                [("@n", name), ("@u", user), ("@p", Math.Max(1, (lines.Count + 59) / 60)), ("@w", width),
                 ("@c", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), ("@t", string.Join('\n', lines))]);
            return (long)command.ExecuteScalar()!;
        }
    }

    /// <summary>Every spooled file, oldest first.</summary>
    public IReadOnlyList<SpoolRecord> Spools() =>
        Query("SELECT ID, NAME, USER, PAGES, WIDTH, CREATED, TEXT FROM QFAKE_SPOOL ORDER BY ID",
            r => new SpoolRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4),
                DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture), r.GetString(6)));

    // ---- tables ----

    /// <summary>A table's columns and declared types, in order; empty when there's no such table.</summary>
    public IReadOnlyList<(string Name, string Type)> Columns(string table) =>
        Query("SELECT name, type FROM pragma_table_info(@t)", r => (r.GetString(0), r.GetString(1)), ("@t", table.ToUpperInvariant()))
            is { Count: > 0 } exact
            ? exact
            : Query("SELECT p.name, p.type FROM sqlite_master m, pragma_table_info(m.name) p WHERE upper(m.name) = @t",
                r => (r.GetString(0), r.GetString(1)), ("@t", table.ToUpperInvariant()));

    /// <summary>
    /// Why SQLite won't take this statement, or null when it will. Each statement of the text is
    /// compiled, not run. Host variables are its @ parameters.
    /// </summary>
    public string? PrepareProblem(string sql)
    {
        lock (Gate)
        {
            try
            {
                using var command = _connection.CreateCommand();
                // EXPLAIN compiles the statement without running it.
                foreach (var statement in sql.Split(";\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    command.CommandText = "EXPLAIN " + statement;
                    command.Parameters.Clear();
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(statement, "@h[0-9_]+"))
                    {
                        if (!command.Parameters.Contains(m.Value)) command.Parameters.AddWithValue(m.Value, DBNull.Value);
                    }
                    using var reader = command.ExecuteReader();
                }
                return null;
            }
            catch (SqliteException e)
            {
                return Clean(e.Message);
            }
        }
    }

    /// <summary>SQLite's message without the wrapper Microsoft.Data.Sqlite puts round it.</summary>
    internal static string Clean(string message)
    {
        var start = message.IndexOf('\'');
        var end = message.LastIndexOf('\'');
        return start >= 0 && end > start ? message[(start + 1)..end] : message;
    }
}

/// <summary>Db2 scalar functions SQLite doesn't have, so SQL that uses them runs unchanged.</summary>
internal static class Db2Functions
{
    public static void Register(SqliteConnection c)
    {
        static string? Str(object? v) => v is null or DBNull ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        static DateTime? Date(object? v) =>
            Str(v) is { } s && DateTime.TryParse(s.Replace('.', ':'), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

        c.CreateFunction("UCASE", (string? s) => s?.ToUpperInvariant());
        c.CreateFunction("LCASE", (string? s) => s?.ToLowerInvariant());
        c.CreateFunction("STRIP", (string? s) => s?.Trim(' '));
        c.CreateFunction("VALUE", (object?[] a) => a.FirstOrDefault(v => v is not null and not DBNull));
        c.CreateFunction("POSSTR", (string? s, string? find) => s is null || find is null ? (long?)null : s.IndexOf(find, StringComparison.Ordinal) + 1);
        c.CreateFunction("LOCATE", (string? find, string? s) => s is null || find is null ? (long?)null : s.IndexOf(find, StringComparison.Ordinal) + 1);
        c.CreateFunction("LEFT", (string? s, long n) => s is null ? null : s[..(int)Math.Clamp(n, 0, s.Length)]);
        c.CreateFunction("RIGHT", (string? s, long n) => s is null ? null : s[(s.Length - (int)Math.Clamp(n, 0, s.Length))..]);
        c.CreateFunction("SPACE", (long n) => new string(' ', (int)Math.Clamp(n, 0, 32000)));
        c.CreateFunction("REPEAT", (string? s, long n) => s is null ? null : string.Concat(Enumerable.Repeat(s, (int)Math.Clamp(n, 0, 32000))));
        c.CreateFunction("YEAR", (object? v) => (long?)Date(v)?.Year);
        c.CreateFunction("MONTH", (object? v) => (long?)Date(v)?.Month);
        c.CreateFunction("DAY", (object? v) => (long?)Date(v)?.Day);
        c.CreateFunction("DAYOFWEEK", (object? v) => Date(v) is { } d ? (long)d.DayOfWeek + 1 : (long?)null);
        c.CreateFunction("DAYS", (object? v) => Date(v) is { } d ? (long)(d.Date - DateTime.MinValue).TotalDays + 1 : (long?)null);
        c.CreateFunction("NOW", () => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture));
        c.CreateFunction("CURDATE", () => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        c.CreateFunction("CURTIME", () => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        c.CreateFunction("DIGITS", (object? v) => v is null or DBNull ? null : Str(v)!.Replace("-", "").Replace(".", ""));
        // CHAR and VARCHAR with one argument (a second, the length, is ignored); INT, DEC and the
        // like convert as a cast does.
        c.CreateFunction("CHAR", (object?[] a) => Str(a.FirstOrDefault()));
        c.CreateFunction("VARCHAR", (object?[] a) => Str(a.FirstOrDefault()));
        foreach (var name in new[] { "INT", "INTEGER", "BIGINT", "SMALLINT" })
        {
            c.CreateFunction(name, (object? v) => Number(v) is { } n ? (long)Math.Truncate(n) : (long?)null);
        }
        foreach (var name in new[] { "DEC", "DECIMAL", "NUMERIC", "ZONED" })
        {
            c.CreateFunction(name, (object?[] a) =>
            {
                if (Number(a.FirstOrDefault()) is not { } n) return (object?)null;
                var scale = a.Length > 2 && Number(a[2]) is { } s ? (int)s : 0;
                var result = Math.Round(n, Math.Clamp(scale, 0, 15), MidpointRounding.ToZero);
                return scale == 0 ? (long)result : (double)result;
            });
        }
        c.CreateFunction("DOUBLE", (object? v) => (double?)Number(v));
    }

    private static decimal? Number(object? v) => v switch
    {
        null or DBNull => null,
        long l => l,
        double d => (decimal)d,
        _ => decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null,
    };
}
