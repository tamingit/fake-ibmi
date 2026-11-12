// The fake's system beyond its servers: source members, the programs and display files made
// from them, the CL commands that work on them, and calling a program. Everything is in the Store.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FakeIbmI.Dds;
using FakeIbmI.Rpg;
using Microsoft.Data.Sqlite;

namespace FakeIbmI;

/// <summary>How a CL command went. <see cref="Known"/> is false for a command the machine doesn't run at all.</summary>
public sealed record CommandResult(bool Ok, string Message, bool Known = true);

public sealed partial class Machine : IDisposable
{
    private readonly ConcurrentDictionary<(string Library, string Name), (long Version, RpgProgram Program)> _programs = new();
    private readonly ConcurrentDictionary<(string Library, string Name), (long Version, DisplayFile File)> _displayFiles = new();

    public Machine(Store store)
    {
        Store = store;
    }

    /// <summary>A machine on a database file, or (null) one in memory that's gone when the fake stops.</summary>
    public static Machine Open(string? database = null) => new(Store.Open(database));

    public Store Store { get; }

    /// <summary>The library members and objects go to when none is named.</summary>
    public string CurrentLibrary => "QGPL";

    public void Dispose() => Store.Dispose();

    [GeneratedRegex(@"CREATE\s+(?:TABLE|VIEW)\s+(?:IF\s+NOT\s+EXISTS\s+)?(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex CreatedName();

    [GeneratedRegex("^[A-Z#$@][A-Z0-9#$@_.]{0,9}$")]
    private static partial Regex ObjectName();

    /// <summary>Whether this is a name an object, member or library can have.</summary>
    public static bool IsName(string name) => ObjectName().IsMatch(name);

    // ---- source types ----

    /// <summary>The source type for a file extension (.rpgle, .sqlrpgle, .dspf, .sql), or null for one the fake doesn't read.</summary>
    public static string? TypeForExtension(string extension) => extension.TrimStart('.').ToUpperInvariant() switch
    {
        "RPGLE" => "RPGLE",
        "SQLRPGLE" => "SQLRPGLE",
        "DSPF" => "DSPF",
        "SQL" => "SQL",
        _ => null,
    };

    /// <summary>The source file a member of this type goes in when none is named.</summary>
    public static string DefaultFile(string type) => type switch
    {
        "RPGLE" or "SQLRPGLE" => "QRPGLESRC",
        "DSPF" => "QDDSSRC",
        "SQL" => "QSQLSRC",
        _ => "QTXTSRC",
    };

    // ---- checking and saving ----

    /// <summary>What the fake can't run in this source, and what it translated or ignored, in line order.</summary>
    public IReadOnlyList<Diagnostic> Check(string type, string name, string source)
    {
        IEnumerable<Diagnostic> found = type switch
        {
            "RPGLE" or "SQLRPGLE" => RpgParser.Parse(source, Store.PrepareProblem).Diagnostics,
            "DSPF" => DisplayFile.Parse(name, source).Diagnostics,
            "SQL" => CheckScript(source),
            _ => [new Diagnostic(0, Severity.Unsupported, $"source type {type} isn't supported: only RPGLE, SQLRPGLE, DSPF and SQL")],
        };
        return [.. found.OrderBy(d => d.Line)];
    }

    private List<Diagnostic> CheckScript(string source)
    {
        var found = new List<Diagnostic>();
        var created = new List<string>();
        foreach (var (text, line) in SqlTranslator.SplitScript(source))
        {
            var translation = SqlTranslator.Translate(text);
            if (translation.Sql is { } made && CreatedName().Match(made) is { Success: true } match) created.Add(match.Groups[1].Value);
            found.AddRange(translation.Notes.Select(note => new Diagnostic(line, Severity.Info, note)));
            if (translation.Kind == SqlKind.Unsupported)
            {
                found.Add(new Diagnostic(line, Severity.Unsupported, translation.Problem!));
            }
            else if (translation.Kind is not (SqlKind.Execute or SqlKind.Select or SqlKind.NoOp) || translation.Inputs.Any(i => i != "*USER"))
            {
                found.Add(new Diagnostic(line, Severity.Unsupported, "a script can't use host variables, cursors or prepared statements"));
            }
            else if (translation.Sql is { } sql && Store.PrepareProblem(sql) is { } problem && !problem.Contains("already exists"))
            {
                // A script often creates the table its next statement uses, so a missing one isn't an error yet.
                var missing = problem.Contains("no such table") || problem.Contains("no such column");
                if (created.Any(name => problem.EndsWith($"no such table: {name}", StringComparison.OrdinalIgnoreCase))) continue;
                found.Add(new Diagnostic(line, missing ? Severity.Warning : Severity.Unsupported,
                    missing ? $"{problem} (yet)" : $"SQLite doesn't take this statement, even translated: {problem}"));
            }
        }
        return found;
    }

    /// <summary>Saves the member and what checking it found, which it returns.</summary>
    public IReadOnlyList<Diagnostic> Save(Member member)
    {
        var diagnostics = Check(member.Type, member.Name, member.Source);
        Store.SaveMember(member, diagnostics);
        return diagnostics;
    }

    // ---- compiling ----

    /// <summary>
    /// Makes the member's object: a program from RPGLE or SQLRPGLE, a display file from DSPF; an
    /// SQL member is run. A program is made even with lines the fake can't run, which end it if reached.
    /// </summary>
    public CommandResult Compile(Member member, string? library = null, string? name = null, string user = "QUSER")
    {
        library ??= member.Library;
        name ??= member.Name;
        var unsupported = Check(member.Type, member.Name, member.Source).Count(d => d.Severity == Severity.Unsupported);
        var caveat = unsupported == 0 ? "" : $" {unsupported} line{(unsupported == 1 ? "" : "s")} can't be run: see the member's diagnostics.";
        switch (member.Type)
        {
            case "RPGLE" or "SQLRPGLE":
                Store.SaveObject(library, name, "*PGM", member.Type, member.Source);
                return new CommandResult(true, $"Program {name} created in library {library}.{caveat}");
            case "DSPF":
                Store.SaveObject(library, name, "*FILE", "DSPF", member.Source);
                return new CommandResult(true, $"File {name} created in library {library}.{caveat}");
            case "SQL":
            {
                var (ran, failures) = RunScript(member.Source, user);
                return new CommandResult(failures.Count == 0,
                    failures.Count == 0 ? $"{ran} SQL statement{(ran == 1 ? "" : "s")} run from {member}." : $"{failures.Count} of {ran + failures.Count} SQL statements failed. First: {failures[0]}");
            }
            default:
                return new CommandResult(false, $"Member {member.Name} has source type {member.Type}, which isn't supported.");
        }
    }

    /// <summary>Runs each statement of a script, carrying on after one fails. Returns how many ran and what failed.</summary>
    public (int Ran, IReadOnlyList<string> Failures) RunScript(string source, string user)
    {
        var ran = 0;
        var failures = new List<string>();
        foreach (var (text, line) in SqlTranslator.SplitScript(source))
        {
            var translation = SqlTranslator.Translate(text);
            if (translation.Kind == SqlKind.NoOp)
            {
                ran++;
                continue;
            }
            if (translation.Kind is not (SqlKind.Execute or SqlKind.Select) || translation.Inputs.Any(i => i != "*USER"))
            {
                failures.Add($"line {line}: {translation.Problem ?? "a script can't use host variables, cursors or prepared statements"}");
                continue;
            }
            try
            {
                lock (Store.Gate)
                {
                    using var command = Store.Connection.CreateCommand();
                    command.CommandText = translation.Sql;
                    for (var i = 0; i < translation.Inputs.Count; i++) command.Parameters.AddWithValue($"@h{i}", user);
                    command.ExecuteNonQuery();
                }
                ran++;
            }
            catch (SqliteException e)
            {
                failures.Add($"line {line}: {Store.Clean(e.Message)}");
            }
        }
        return (ran, failures);
    }

    // ---- objects ----

    /// <summary>The library and name in LIB/NAME. No library, *LIBL or *CURLIB is null: any library.</summary>
    internal static (string? Library, string Name) Qualified(string text)
    {
        var parts = text.Trim().ToUpperInvariant().Split('/');
        if (parts.Length == 1) return (null, parts[0]);
        return (parts[0] is "*LIBL" or "*CURLIB" or "" ? null : parts[0], parts[1]);
    }

    /// <summary>Whether a program of this name has been created.</summary>
    public bool HasProgram(string? library, string name) => Store.FindObject(library, name, "*PGM") is not null;

    internal RpgProgram? LoadProgram(string? library, string name)
    {
        if (Store.FindObject(library, name, "*PGM") is not { } o) return null;
        var cached = _programs.GetValueOrDefault((o.Library, o.Name));
        if (cached.Version == o.Version) return cached.Program;
        var program = RpgParser.Parse(o.Source);
        _programs[(o.Library, o.Name)] = (o.Version, program);
        return program;
    }

    internal DisplayFile? LoadDisplayFile(string name)
    {
        if (Store.FindObject(null, name, "*FILE") is not { } o) return null;
        var cached = _displayFiles.GetValueOrDefault((o.Library, o.Name));
        if (cached.Version == o.Version) return cached.File;
        var file = DisplayFile.Parse(o.Name, o.Source);
        _displayFiles[(o.Library, o.Name)] = (o.Version, file);
        return file;
    }

    /// <summary>Calls a program with these arguments, which take back what it leaves in its parameters. Throws RpgHalt when it fails.</summary>
    internal void Call(string program, IReadOnlyList<Cell> arguments, IJobHost host, int depth = 0)
    {
        var (library, name) = Qualified(program);
        var parsed = LoadProgram(library, name) ?? throw new RpgHalt($"Program {name} in library {library ?? "*LIBL"} not found.");
        new Interpreter(parsed, name, this, host, depth).Run(arguments);
    }

    /// <summary>The types of a program's parameters, or null when there's no such program.</summary>
    internal IReadOnlyList<RpgType>? ParameterTypes(string? library, string name, IJobHost host) =>
        LoadProgram(library, name) is { } program ? new Interpreter(program, name, this, host).ParameterTypes() : null;

    // ---- CL commands ----

    /// <summary>
    /// Runs a CL command: CALL, CRTBNDRPG, CRTSQLRPGI, CRTDSPF, RUNSQLSTM, RUNSQL, DLTPGM or DLTF.
    /// Any other comes back as not <see cref="CommandResult.Known"/>.
    /// </summary>
    internal CommandResult Run(string text, IJobHost host)
    {
        ClCommand command;
        try
        {
            command = ClCommand.Parse(text);
        }
        catch (FormatException e)
        {
            return new CommandResult(false, e.Message);
        }
        switch (command.Name)
        {
            case "CALL":
            {
                if (command.Value("PGM", 0) is not { } program) return new CommandResult(false, "Parameter PGM required.");
                var arguments = new List<Cell>();
                foreach (var (value, quoted) in ClCommand.Values(command.Value("PARM", 1) ?? ""))
                {
                    // As CL passes literals: characters as they are, numbers as packed (15 5).
                    var number = !quoted && decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n);
                    var cell = new Cell(number ? RpgType.Number(15, 5) : RpgType.Char(Math.Max(value.Length, 1)));
                    cell.Set(number ? decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : quoted ? value : value.ToUpperInvariant());
                    arguments.Add(cell);
                }
                try
                {
                    Call(program, arguments, host);
                    return new CommandResult(true, "");
                }
                catch (RpgHalt e)
                {
                    return new CommandResult(false, e.Message);
                }
            }
            case "CRTBNDRPG" or "CRTSQLRPGI" or "CRTDSPF":
            {
                var display = command.Name == "CRTDSPF";
                var keyword = command.Name switch { "CRTBNDRPG" => "PGM", "CRTSQLRPGI" => "OBJ", _ => "FILE" };
                if (command.Value(keyword, 0) is not { } target) return new CommandResult(false, $"Parameter {keyword} required.");
                var (library, name) = Qualified(target);
                var (sourceLibrary, sourceFile) = command.Value("SRCFILE", 1) is { } file ? Qualified(file) : (null, null);
                var memberName = command.Value("SRCMBR", 2)?.ToUpperInvariant() is { } m && !m.StartsWith('*') ? m : name;
                var member = Store.FindMember(sourceLibrary, sourceFile, memberName);
                if (member is null && sourceFile is null)
                {
                    // No SRCFILE: the command's default, then any file with a member of the name.
                    member = Store.FindMember(null, display ? "QDDSSRC" : "QRPGLESRC", memberName);
                }
                if (member is null) return new CommandResult(false, $"Member {memberName} not found in file {sourceFile ?? (display ? "QDDSSRC" : "QRPGLESRC")}.");
                if (display != (member.Type == "DSPF") || member.Type == "SQL")
                    return new CommandResult(false, $"Member {member} has source type {member.Type}, which {command.Name} doesn't compile.");
                return Compile(member, library ?? member.Library, name, host.User);
            }
            case "RUNSQLSTM":
            {
                if (command.Value("SRCMBR", 1) is not { } memberName) return new CommandResult(false, "Parameter SRCMBR required.");
                var (library, file) = command.Value("SRCFILE", 0) is { } f ? Qualified(f) : (null, null);
                var member = Store.FindMember(library, file, memberName);
                if (member is null) return new CommandResult(false, $"Member {memberName.ToUpperInvariant()} not found in file {file ?? "*LIBL"}.");
                var (ran, failures) = RunScript(member.Source, host.User);
                return new CommandResult(failures.Count == 0,
                    failures.Count == 0 ? $"{ran} SQL statement{(ran == 1 ? "" : "s")} run." : $"{failures.Count} of {ran + failures.Count} SQL statements failed. First: {failures[0]}");
            }
            case "RUNSQL":
            {
                if (command.Value("SQL", 0) is not { } raw) return new CommandResult(false, "Parameter SQL required.");
                var sql = ClCommand.Values(raw) is [(var quoted, true)] ? quoted : raw;
                var (ran, failures) = RunScript(sql, host.User);
                return new CommandResult(failures.Count == 0, failures.Count == 0 ? (ran == 0 ? "No SQL statement to run." : "SQL statement run.") : failures[0][(failures[0].IndexOf(':') + 2)..]);
            }
            case "DLTPGM" or "DLTF":
            {
                var program = command.Name == "DLTPGM";
                if (command.Value(program ? "PGM" : "FILE", 0) is not { } target) return new CommandResult(false, $"Parameter {(program ? "PGM" : "FILE")} required.");
                var (library, name) = Qualified(target);
                if (Store.FindObject(library, name, program ? "*PGM" : "*FILE") is { } o)
                {
                    Store.DeleteObject(o);
                    return new CommandResult(true, $"Object {name} in {o.Library} type {o.Type} deleted.");
                }
                if (!program && IsName(name) && Store.Columns(name).Count > 0)
                {
                    RunScript($"DROP TABLE {name}", host.User);
                    return new CommandResult(true, $"File {name} deleted.");
                }
                return new CommandResult(false, $"Object {name} in library {library ?? "*LIBL"} not found.");
            }
            default:
                return new CommandResult(false, $"Command {command.Name} in library *LIBL not found.", Known: false);
        }
    }

    // ---- a folder of source ----

    /// <summary>
    /// Reads every source file under a folder (.rpgle, .sqlrpgle, .dspf, .sql) as a member named for
    /// the file, in the source file named for its folder (or the type's default, directly under
    /// <paramref name="directory"/>). A member that's new or changed is saved and checked, then
    /// SQL members are run, display files created and programs created, in that order. Without
    /// <paramref name="create"/>, nothing is created (SQL members still run: use a machine to throw away).
    /// Returns how many lines the fake can't run.
    /// </summary>
    public int Import(string directory, Action<string> log, bool create = true)
    {
        var root = Path.GetFullPath(directory);
        var changed = new List<Member>();
        var unsupported = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (TypeForExtension(Path.GetExtension(path)) is not { } type) continue;
            var name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
            var folder = Path.GetDirectoryName(path)!;
            var file = folder == root ? DefaultFile(type) : Path.GetFileName(folder).ToUpperInvariant();
            if (!IsName(name) || !IsName(file))
            {
                log($"source: {Path.GetRelativePath(root, path)} skipped: {(IsName(name) ? file : name)} isn't a name of up to 10 letters and digits");
                continue;
            }
            var member = new Member(CurrentLibrary, file, name, type, System.IO.File.ReadAllText(path).Replace("\r\n", "\n"));
            var existing = Store.FindMember(member.Library, member.File, member.Name);
            var made = type == "SQL" || Store.FindObject(member.Library, name, type == "DSPF" ? "*FILE" : "*PGM") is not null;
            if (existing == member && made) continue;
            changed.Add(member);
        }
        // Tables first, so the check of a program's SQL finds them; then display files, then programs.
        foreach (var member in changed.OrderBy(m => m.Type switch { "SQL" => 0, "DSPF" => 1, _ => 2 }))
        {
            var diagnostics = Save(member);
            // Checking only still runs the SQL members, so the SQL of the programs after them finds its tables.
            var result = create || member.Type == "SQL" ? Compile(member) : new CommandResult(true, "checked.");
            log($"source: {member} {member.Type}: {result.Message}");
            foreach (var d in diagnostics.Where(d => d.Severity != Severity.Info))
            {
                log($"source:   {(d.Line > 0 ? $"line {d.Line}: " : "")}{(d.Severity == Severity.Unsupported ? "not supported" : "warning")}: {d.Message}");
            }
            unsupported += diagnostics.Count(d => d.Severity == Severity.Unsupported);
        }
        return unsupported;
    }
}

/// <summary>A CL command, parsed: its name, its positional values and its KEYWORD(value) parameters, as written.</summary>
internal sealed record ClCommand(string Name, IReadOnlyList<string> Positional, IReadOnlyDictionary<string, string> Keyed)
{
    /// <summary>The parameter by keyword, or failing that by position, or null.</summary>
    public string? Value(string keyword, int position) =>
        Keyed.TryGetValue(keyword, out var value) ? value : position < Positional.Count ? Positional[position] : null;

    public static ClCommand Parse(string text)
    {
        var i = 0;
        string Word()
        {
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '(') i++;
            return text[start..i];
        }
        void Blanks()
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        }
        Blanks();
        var name = Word().ToUpperInvariant();
        name = name[(name.LastIndexOf('/') + 1)..];
        if (!Machine.IsName(name)) throw new FormatException($"{(name.Length == 0 ? "That" : name)} isn't a command.");
        var positional = new List<string>();
        var keyed = new Dictionary<string, string>();
        while (true)
        {
            Blanks();
            if (i >= text.Length) break;
            if (text[i] == '(')
            {
                positional.Add(Group());
                continue;
            }
            if (text[i] == '\'')
            {
                var start = i;
                Skip();
                positional.Add(text[start..i]);
                continue;
            }
            var word = Word();
            if (i < text.Length && text[i] == '(') keyed[word.ToUpperInvariant()] = Group();
            else positional.Add(word);
        }
        return new ClCommand(name, positional, keyed);

        // What's inside a parenthesized group, which may nest and hold quoted strings.
        string Group()
        {
            var start = ++i;
            var depth = 1;
            while (i < text.Length && depth > 0)
            {
                if (text[i] == '\'')
                {
                    Skip();
                    continue;
                }
                if (text[i] == '(') depth++;
                else if (text[i] == ')') depth--;
                i++;
            }
            if (depth > 0) throw new FormatException("A parenthesis isn't closed.");
            return text[start..(i - 1)].Trim();
        }
        void Skip()
        {
            i++;
            while (i < text.Length)
            {
                if (text[i] == '\'')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\'') i++;
                    else break;
                }
                i++;
            }
            if (i >= text.Length) throw new FormatException("A quoted value isn't closed.");
            i++;
        }
    }

    /// <summary>The values in a list such as PARM('A B' 12 X): each one's text, and whether it was quoted.</summary>
    public static List<(string Text, bool Quoted)> Values(string list)
    {
        var values = new List<(string, bool)>();
        var i = 0;
        while (i < list.Length)
        {
            if (char.IsWhiteSpace(list[i]))
            {
                i++;
            }
            else if (list[i] == '\'')
            {
                var value = new System.Text.StringBuilder();
                i++;
                while (i < list.Length)
                {
                    if (list[i] == '\'')
                    {
                        if (i + 1 < list.Length && list[i + 1] == '\'') i++;
                        else break;
                    }
                    value.Append(list[i++]);
                }
                i++;
                values.Add((value.ToString(), true));
            }
            else
            {
                var start = i;
                while (i < list.Length && !char.IsWhiteSpace(list[i])) i++;
                values.Add((list[start..i], false));
            }
        }
        return values;
    }
}
