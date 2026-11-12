// The 5250 screens for source: the list of members, the editor, and what the last save of a
// member found. Each is a Panel the Telnet session shows until it hands over to another.

using static FakeIbmI.Stream5250;

namespace FakeIbmI;

/// <summary>What a panel wants after the user's input: another panel (or itself), or back to the main menu.</summary>
/// <param name="Next">The panel to show; null goes back to the main menu.</param>
/// <param name="Message">A message for the error row of whatever is shown next.</param>
/// <param name="Command">A command to run as if typed on the main menu's command line, in place of showing a panel.</param>
internal sealed record PanelResult(Panel? Next, string? Message = null, string? Command = null);

internal abstract class Panel
{
    public abstract byte[] Render(string? message);

    public abstract PanelResult Handle(ReceivedInput input);

    protected static byte[] Title(string text) => Join(Sba(1, Math.Max(2, (80 - text.Length) / 2)), [Highlight], Text(text), [Normal]);
}

/// <summary>System Options: the user, library and source tools, each one choice.</summary>
internal sealed class SystemMenuPanel(Machine machine, string defaultLibrary, Action<string> log) : Panel
{
    private const int SelectionRow = 12;
    private const int Column = 20;

    public override byte[] Render(string? message) => Screen5250(
        Header(24),
        Title("System Options"),
        Sba(3, 2), Text("Select one of the following:"),
        Sba(5, 5), Text("1. Work with users"),
        Sba(6, 5), Text("2. Work with libraries"),
        Sba(7, 5), Text("3. Work with source members"),
        Sba(SelectionRow, 2), Text("Selection  . . ."), Sba(SelectionRow, Column - 1), Sf(0x4020, Underline, 2),
        Sba(22, 2), [Normal], Text("F3=Exit   F12=Cancel"),
        Message(message));

    public override PanelResult Handle(ReceivedInput input)
    {
        if (input.Aid is F3Aid or F12Aid) return new PanelResult(null);
        if (input.Aid != EnterAid) return new PanelResult(this, "Function key not allowed.");
        switch (input.FieldAt(SelectionRow, Column)?.Trim() ?? "")
        {
            case "":
                return new PanelResult(this);
            case "1":
                // The user maintenance program, a program of the fake's own like any other.
                return new PanelResult(null, Command: "CALL USRMNT");
            case "2":
                return new PanelResult(new LibraryListPanel(machine, defaultLibrary, log, this));
            case "3":
                return new PanelResult(new MemberListPanel(machine, defaultLibrary, log, this));
            case var other:
                return new PanelResult(this, $"Option {other} isn't 1, 2 or 3.");
        }
    }
}

/// <summary>Work with Members: every member, with options to edit, delete, show diagnostics and compile.</summary>
internal sealed class MemberListPanel(Machine machine, string defaultLibrary, Action<string> log, Panel? back = null) : Panel
{
    private const int FirstRow = 7;
    private const int PageSize = 12;
    private int _top;

    public override byte[] Render(string? message)
    {
        var members = machine.Store.Members();
        _top = Math.Clamp(_top, 0, Math.Max(0, (members.Count - 1) / PageSize * PageSize));
        var rows = new List<byte[]>();
        for (var i = 0; i < PageSize && _top + i < members.Count; i++)
        {
            var member = members[_top + i];
            var unsupported = machine.Store.Diagnostics(member).Count(d => d.Severity == Severity.Unsupported);
            var row = FirstRow + i;
            rows.Add(Join(
                Sba(row, 2), Sf(0x4000, Underline, 2),
                Sba(row, 5), [Normal],
                Text($" {member.Name,-10}  {member.Type,-10}  {member.File,-10}  {member.Library,-10}  {(unsupported == 0 ? "" : unsupported.ToString())}")));
        }
        if (members.Count == 0) rows.Add(Join(Sba(FirstRow, 7), Text("(No members. F6 creates one, or start the fake with --source-dir.)")));
        return Screen5250(
            Header(24),
            Title("Work with Members"),
            Sba(3, 2), Text("Type options, press Enter."),
            Sba(4, 4), Text("2=Edit   4=Delete   5=Diagnostics   14=Compile"),
            Sba(6, 1), [Highlight], Text("Opt  Member      Type        File        Library     Not supported"), [Normal],
            Join([.. rows]),
            Sba(FirstRow + PageSize, 70), Text(_top + PageSize >= members.Count ? "Bottom" : "More..."),
            Sba(22, 2), Text("F3=Exit   F6=Create   F12=Cancel"),
            Message(message));
    }

    public override PanelResult Handle(ReceivedInput input)
    {
        var members = machine.Store.Members();
        switch (input.Aid)
        {
            case F3Aid:
                return new PanelResult(null);
            case F12Aid:
                return new PanelResult(back);
            case F6Aid:
                return new PanelResult(new NewMemberPanel(machine, defaultLibrary, this, log));
            case RollUpAid:
                if (_top + PageSize >= members.Count) return new PanelResult(this, "Already at the bottom of the list.");
                _top += PageSize;
                return new PanelResult(this);
            case RollDownAid:
                if (_top == 0) return new PanelResult(this, "Already at the top of the list.");
                _top -= PageSize;
                return new PanelResult(this);
            case not EnterAid:
                return new PanelResult(this, "Function key not allowed.");
        }
        string? message = null;
        for (var i = 0; i < PageSize && _top + i < members.Count; i++)
        {
            var option = input.FieldAt(FirstRow + i, 3)?.Trim() ?? "";
            if (option.Length == 0) continue;
            var member = members[_top + i];
            switch (option)
            {
                case "2":
                    return new PanelResult(new EditorPanel(machine, member, this, log), message);
                case "5":
                    return new PanelResult(new DiagnosticsPanel(machine, member, this), message);
                case "4":
                    machine.Store.DeleteMember(member);
                    log($"member {member} deleted");
                    message = $"Member {member.Name} removed from file {member.File}.";
                    break;
                case "14":
                    message = machine.Compile(member).Message;
                    log($"member {member} compiled");
                    break;
                default:
                    return new PanelResult(this, $"Option {option} isn't 2, 4, 5 or 14.");
            }
        }
        return new PanelResult(this, message);
    }
}

/// <summary>Work with Libraries: every library with what it holds, and options to delete one; F6 makes one.</summary>
internal sealed class LibraryListPanel(Machine machine, string defaultLibrary, Action<string> log, Panel? back = null) : Panel
{
    private const int FirstRow = 7;
    private const int PageSize = 12;
    private int _top;

    public override byte[] Render(string? message)
    {
        var libraries = machine.Store.Libraries();
        _top = Math.Clamp(_top, 0, Math.Max(0, (libraries.Count - 1) / PageSize * PageSize));
        var rows = new List<byte[]>();
        for (var i = 0; i < PageSize && _top + i < libraries.Count; i++)
        {
            var library = libraries[_top + i];
            var (members, objects, tables) = machine.Store.Counts(library);
            var row = FirstRow + i;
            rows.Add(Join(
                Sba(row, 2), Sf(0x4000, Underline, 2),
                Sba(row, 5), [Normal],
                Text($" {library,-10}  {members,7}  {objects,7}  {tables,6}  {(library == defaultLibrary ? "Your default library" : library == machine.CurrentLibrary ? "System library" : "")}")));
        }
        return Screen5250(
            Header(24),
            Title("Work with Libraries"),
            Sba(3, 2), Text("Type options, press Enter."),
            Sba(4, 4), Text("4=Delete"),
            Sba(6, 1), [Highlight], Text("Opt  Library     Members  Objects  Tables"), [Normal],
            Join([.. rows]),
            Sba(FirstRow + PageSize, 70), Text(_top + PageSize >= libraries.Count ? "Bottom" : "More..."),
            Sba(22, 2), Text("F3=Exit   F6=Create   F12=Cancel"),
            Message(message));
    }

    public override PanelResult Handle(ReceivedInput input)
    {
        var libraries = machine.Store.Libraries();
        switch (input.Aid)
        {
            case F3Aid:
                return new PanelResult(null);
            case F12Aid:
                return new PanelResult(back);
            case F6Aid:
                return new PanelResult(new NewLibraryPanel(machine, this, log));
            case RollUpAid:
                if (_top + PageSize >= libraries.Count) return new PanelResult(this, "Already at the bottom of the list.");
                _top += PageSize;
                return new PanelResult(this);
            case RollDownAid:
                if (_top == 0) return new PanelResult(this, "Already at the top of the list.");
                _top -= PageSize;
                return new PanelResult(this);
            case not EnterAid:
                return new PanelResult(this, "Function key not allowed.");
        }
        for (var i = 0; i < PageSize && _top + i < libraries.Count; i++)
        {
            var option = input.FieldAt(FirstRow + i, 3)?.Trim() ?? "";
            if (option.Length == 0) continue;
            var library = libraries[_top + i];
            if (option != "4") return new PanelResult(this, $"Option {option} isn't 4.");
            if (library == machine.CurrentLibrary) return new PanelResult(this, $"Library {library} can't be deleted: it holds the system's tables.");
            return new PanelResult(new DeleteLibraryPanel(machine, library, this, log));
        }
        return new PanelResult(this);
    }
}

/// <summary>Option 4 on the library list: asks to confirm, as what's in the library goes with it.</summary>
internal sealed class DeleteLibraryPanel(Machine machine, string library, Panel back, Action<string> log) : Panel
{
    public override byte[] Render(string? message)
    {
        var (members, objects, tables) = machine.Store.Counts(library);
        return Screen5250(
            Header(24),
            Title("Confirm Delete of Library"),
            Sba(4, 2), Text($"Library {library} has {members} source member{(members == 1 ? "" : "s")}, {objects} object{(objects == 1 ? "" : "s")} and {tables} table{(tables == 1 ? "" : "s")}."),
            Sba(5, 2), Text("They are all deleted with it, and its database file too."),
            Sba(7, 2), Text("Press Enter to confirm the delete."),
            Sba(22, 2), Text("Enter=Delete   F12=Cancel"),
            Message(message));
    }

    public override PanelResult Handle(ReceivedInput input)
    {
        switch (input.Aid)
        {
            case F12Aid or F3Aid:
                return new PanelResult(back);
            case EnterAid:
                var result = machine.Run($"DLTLIB LIB({library})", new BatchJob("QUSER", CancellationToken.None));
                log($"library {library} deleted");
                return new PanelResult(back, result.Message);
            default:
                return new PanelResult(this, "Function key not allowed.");
        }
    }
}

/// <summary>F6 on the library list: names a new library.</summary>
internal sealed class NewLibraryPanel(Machine machine, Panel back, Action<string> log) : Panel
{
    private const int Column = 30;

    public override byte[] Render(string? message) => Screen5250(
        Header(24),
        Title("Create Library"),
        Sba(3, 2), Text("Type choices, press Enter."),
        Sba(6, 4), Text("Library . . . . . . ."), Sba(6, Column - 1), Sf(0x4020, Underline, 10),
        Sba(6, 43), [Normal], Text("1 to 10 letters, digits, # $ @ _"),
        Sba(22, 2), Text("F3=Exit   F12=Cancel"),
        Message(message));

    public override PanelResult Handle(ReceivedInput input)
    {
        if (input.Aid is F12Aid or F3Aid) return new PanelResult(back);
        if (input.Aid != EnterAid) return new PanelResult(this, "Function key not allowed.");
        var name = input.FieldAt(6, Column)?.Trim().ToUpperInvariant() ?? "";
        if (name.Length == 0) return new PanelResult(this, "Library name required.");
        var result = machine.Run($"CRTLIB LIB({name})", new BatchJob("QUSER", CancellationToken.None));
        if (!result.Ok) return new PanelResult(this, result.Message);
        log($"library {name} created");
        return new PanelResult(back, result.Message);
    }
}

/// <summary>F6 on the list: names a new member, then edits it.</summary>
internal sealed class NewMemberPanel(Machine machine, string defaultLibrary, Panel back, Action<string> log) : Panel
{
    private const int Column = 30;

    public override byte[] Render(string? message) => Screen5250(
        Header(24),
        Title("Create Member"),
        Sba(3, 2), Text("Type choices, press Enter."),
        Sba(6, 4), Text("Member . . . . . . . ."), Sba(6, Column - 1), Sf(0x4020, Underline, 10),
        Sba(8, 4), Text("Source type  . . . . ."), Sba(8, Column - 1), Sf(0x4020, Underline, 10), Text("RPGLE"),
        Sba(8, 44), [Normal], Text("RPGLE, SQLRPGLE, DSPF, SQL"),
        Sba(10, 4), Text("Source file  . . . . ."), Sba(10, Column - 1), Sf(0x4020, Underline, 10),
        Sba(10, 44), [Normal], Text("Blank: the type's usual file"),
        Sba(12, 4), Text("Library  . . . . . . ."), Sba(12, Column - 1), Sf(0x4020, Underline, 10), Text(defaultLibrary),
        Sba(22, 2), Text("F3=Exit   F12=Cancel"),
        Message(message));

    public override PanelResult Handle(ReceivedInput input)
    {
        if (input.Aid is F3Aid or F12Aid) return new PanelResult(back);
        if (input.Aid != EnterAid) return new PanelResult(this, "Function key not allowed.");
        string Field(int row, string fallback) => input.FieldAt(row, Column)?.Trim().ToUpperInvariant() is { Length: > 0 } text ? text : fallback;
        var name = Field(6, "");
        var type = Field(8, "RPGLE");
        var file = Field(10, Machine.DefaultFile(type));
        var library = Field(12, defaultLibrary);
        if (name.Length == 0) return new PanelResult(this, "Member name required.");
        foreach (var value in new[] { name, file, library })
        {
            if (!Machine.IsName(value)) return new PanelResult(this, $"{value} isn't a name: up to 10 letters and digits, starting with a letter.");
        }
        if (Machine.TypeForExtension(type) is null) return new PanelResult(this, $"Source type {type} isn't supported: RPGLE, SQLRPGLE, DSPF or SQL.");
        if (!machine.Store.HasLibrary(library)) return new PanelResult(this, $"Library {library} not found.");
        if (machine.Store.FindMember(library, file, name) is not null) return new PanelResult(this, $"Member {name} already exists in {library}/{file}.");
        return new PanelResult(new EditorPanel(machine, new Member(library, file, name, type, type is "RPGLE" or "SQLRPGLE" ? "**FREE" : ""), back, log));
    }
}

/// <summary>
/// The source editor. Each line has a one-character command field (I inserts a line after it, D
/// deletes it) and its source, 72 columns of it. F3 saves the member, which checks it.
/// </summary>
internal sealed class EditorPanel : Panel
{
    private const int FirstRow = 4;
    private const int PageSize = 17;
    private const int CommandColumn = 6;
    private const int SourceColumn = 8;
    private const int Width = 72;

    private readonly Machine _machine;
    private readonly Member _member;
    private readonly Panel _back;
    private readonly Action<string> _log;
    private readonly List<string> _lines;
    private int _top;
    private int? _cursorLine;

    public EditorPanel(Machine machine, Member member, Panel back, Action<string> log)
    {
        _machine = machine;
        _member = member;
        _back = back;
        _log = log;
        _lines = [.. member.Source.Replace("\r\n", "\n").Split('\n')];
        while (_lines.Count > 0 && _lines[^1].Length == 0) _lines.RemoveAt(_lines.Count - 1);
    }

    public override byte[] Render(string? message)
    {
        _top = Math.Clamp(_top, 0, Math.Max(0, _lines.Count - 1));
        var rows = new List<byte[]>();
        var tooLong = false;
        for (var i = 0; i < PageSize; i++)
        {
            var index = _top + i;
            var row = FirstRow + i;
            var line = index < _lines.Count ? _lines[index].Replace('\t', ' ') : "";
            var editable = line.Length <= Width;
            tooLong |= !editable;
            rows.Add(Join(
                Sba(row, 1), Text(index < 9999 ? $"{index + 1:0000}" : "    "),
                Sba(row, CommandColumn - 1), Sf(0x4020, Underline, 1),
                // A line too long for the field is shown in part, and can't be changed here.
                editable
                    ? Join(Sba(row, SourceColumn - 1), Sf(0x4000, index < _lines.Count ? Normal : Underline, Width), Text(line))
                    : Join(Sba(row, SourceColumn - 1), [Highlight], Text(line[..(Width - 1)] + ">"), [Normal])));
        }
        var cursor = _cursorLine is { } at && at >= _top && at < _top + PageSize ? at - _top : 0;
        _cursorLine = null;
        return Screen5250(
            Header(24),
            Sba(1, 2), Text("Edit"), Sba(1, 9), [Highlight], Text($"{_member}  {_member.Type}"), [Normal],
            Sba(1, 62), Text($"{_lines.Count} line{(_lines.Count == 1 ? "" : "s")}"),
            Sba(3, 1), Text("Line C ....+....1....+....2....+....3....+....4....+....5....+....6....+....7.."),
            Join([.. rows]),
            Sba(22, 2), Text("F3=Save and exit   F12=Cancel   Page Up/Down   C: I=Insert after  D=Delete"),
            tooLong ? Join(Sba(23, 2), Text("Lines ending > are longer than 72 columns: shown in part, not changed here.")) : [],
            Message(message),
            Ic(FirstRow + cursor, SourceColumn));
    }

    public override PanelResult Handle(ReceivedInput input)
    {
        if (input.Aid == F12Aid) return new PanelResult(_back, $"Edit cancelled: member {_member.Name} not changed.");
        if (input.Aid is not (EnterAid or F3Aid or RollUpAid or RollDownAid)) return new PanelResult(this, "Function key not allowed.");

        // What was typed over the lines, then the line commands from the bottom up, so the lines above keep their numbers.
        for (var i = 0; i < PageSize; i++)
        {
            if (input.FieldAt(FirstRow + i, SourceColumn) is not { } text) continue;
            while (_lines.Count <= _top + i) _lines.Add("");
            _lines[_top + i] = text;
        }
        string? message = null;
        for (var i = PageSize - 1; i >= 0; i--)
        {
            var command = input.FieldAt(FirstRow + i, CommandColumn)?.Trim().ToUpperInvariant() ?? "";
            var index = _top + i;
            if (command.Length == 0) continue;
            if (command == "I")
            {
                while (_lines.Count <= index) _lines.Add("");
                _lines.Insert(index + 1, "");
                _cursorLine = index + 1;
            }
            else if (command == "D")
            {
                if (index < _lines.Count) _lines.RemoveAt(index);
            }
            else
            {
                message = $"Line command {command} isn't I or D.";
            }
        }
        while (_lines.Count > 0 && _lines[^1].Length == 0 && _cursorLine != _lines.Count - 1) _lines.RemoveAt(_lines.Count - 1);

        switch (input.Aid)
        {
            case RollUpAid:
                if (_top + PageSize - 1 > _lines.Count) return new PanelResult(this, message ?? "Already at the bottom of the member.");
                _top += PageSize - 1;
                return new PanelResult(this, message);
            case RollDownAid:
                if (_top == 0) return new PanelResult(this, message ?? "Already at the top of the member.");
                _top = Math.Max(0, _top - (PageSize - 1));
                return new PanelResult(this, message);
            case F3Aid:
            {
                var saved = _member with { Source = string.Join('\n', _lines) + (_lines.Count > 0 ? "\n" : "") };
                var diagnostics = _machine.Save(saved);
                _log($"member {saved} saved: {diagnostics.Count(d => d.Severity == Severity.Unsupported)} not supported");
                var unsupported = diagnostics.Count(d => d.Severity == Severity.Unsupported);
                var summary = $"Member {saved.Name} saved{(unsupported == 0 ? "." : $": {unsupported} line{(unsupported == 1 ? "" : "s")} not supported.")}";
                return diagnostics.Count == 0 ? new PanelResult(_back, summary) : new PanelResult(new DiagnosticsPanel(_machine, saved, _back), summary);
            }
            default:
                return new PanelResult(this, message);
        }
    }
}

/// <summary>What the last save of a member found: each line the fake can't run, and what it translated or ignored.</summary>
internal sealed class DiagnosticsPanel : Panel
{
    private const int FirstRow = 5;
    private const int PageSize = 16;
    private const int MessageWidth = 60;

    private readonly Member _member;
    private readonly Panel _back;
    private readonly List<string> _rows = [];
    private readonly string _summary;
    private int _top;

    public DiagnosticsPanel(Machine machine, Member member, Panel back)
    {
        _member = member;
        _back = back;
        var diagnostics = machine.Store.Diagnostics(member);
        int Count(Severity severity) => diagnostics.Count(d => d.Severity == severity);
        _summary = diagnostics.Count == 0
            ? "Nothing found: every line is supported."
            : $"{Count(Severity.Unsupported)} not supported, {Count(Severity.Warning)} warning{(Count(Severity.Warning) == 1 ? "" : "s")}, {Count(Severity.Info)} translated or ignored";
        foreach (var d in diagnostics)
        {
            var kind = d.Severity switch { Severity.Unsupported => "Not supported", Severity.Warning => "Warning", _ => "Note" };
            // A long message carries on, indented, on the rows below.
            var words = d.Message.Split(' ');
            var text = "";
            var first = true;
            void Flush()
            {
                _rows.Add(first ? $"{(d.Line > 0 ? d.Line.ToString() : ""),5}  {kind,-13}  {text}" : $"{"",22}{text}");
                first = false;
                text = "";
            }
            foreach (var word in words)
            {
                if (text.Length > 0 && text.Length + 1 + word.Length > (first ? MessageWidth - 4 : MessageWidth - 4)) Flush();
                text += (text.Length > 0 ? " " : "") + (word.Length > MessageWidth - 4 ? word[..(MessageWidth - 4)] : word);
            }
            if (text.Length > 0 || first) Flush();
        }
    }

    public override byte[] Render(string? message)
    {
        var rows = _rows.Skip(_top).Take(PageSize).Select((text, i) => Join(Sba(FirstRow + i, 1), Text(text.Length > 80 ? text[..80] : text))).ToArray();
        return Screen5250(
            Header(24),
            Title("Member Diagnostics"),
            Sba(2, 2), Text($"{_member}  {_member.Type}: {_summary}"),
            Sba(4, 1), [Highlight], Text(" Line  Finding        Message"), [Normal],
            Join(rows),
            Sba(FirstRow + PageSize, 70), Text(_top + PageSize >= _rows.Count ? "Bottom" : "More..."),
            Sba(22, 2), Text("F3=Exit   F12=Cancel   Enter=Continue"),
            Message(message));
    }

    public override PanelResult Handle(ReceivedInput input)
    {
        switch (input.Aid)
        {
            case RollUpAid:
                if (_top + PageSize >= _rows.Count) return new PanelResult(this, "Already at the bottom of the list.");
                _top += PageSize;
                return new PanelResult(this);
            case RollDownAid:
                if (_top == 0) return new PanelResult(this, "Already at the top of the list.");
                _top -= PageSize;
                return new PanelResult(this);
            default:
                return new PanelResult(_back);
        }
    }
}
