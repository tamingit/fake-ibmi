// A display file open in a running program: writes its record formats as 5250 screens, reads
// what the user does back into the program's fields and indicators, and keeps its subfiles.

using FakeIbmI.Rpg;
using static FakeIbmI.Stream5250;

namespace FakeIbmI.Dds;

internal sealed class DisplaySession(
    DisplayFile file, FileDecl declaration, Func<string, Cell?> variable, Func<FileDecl, int, Cell> indicator, string user)
{
    private sealed class Row(Dictionary<string, object> values)
    {
        public Dictionary<string, object> Values { get; } = values;
        public bool Changed { get; set; }
    }

    private sealed class Subfile
    {
        public SortedDictionary<int, Row> Rows { get; } = [];

        /// <summary>The index, among the records in order, of the first one on the page shown.</summary>
        public int Top { get; set; }
    }

    /// <summary>An input field on the screen: where it is, and the subfile record it belongs to, if any.</summary>
    private sealed record Target(int Row, int Column, DisplayField Field, string? Subfile, int Number);

    private readonly Dictionary<string, Subfile> _subfiles = [];
    private readonly List<string> _shown = [];
    private readonly List<Target> _targets = [];

    public DisplayFile File { get; } = file;

    private bool On(int number) => Values.ToBool(indicator(declaration, number).Get());

    private Subfile SubfileOf(string record) => _subfiles.TryGetValue(record, out var s) ? s : _subfiles[record] = new Subfile();

    private Cell NumberField(string record) =>
        declaration.Subfiles.TryGetValue(record, out var name) && variable(name) is { } cell
            ? cell
            : throw new RpgError(1221, $"subfile {record} needs SFILE({record}:field) on the file, and the field declared");

    private Cell Variable(DisplayField field) => variable(field.Name!) ?? throw new RpgError(100, $"{field.Name} isn't defined");

    // ---- operations ----

    /// <summary>WRITE, or EXFMT with <paramref name="read"/>: shows the record, or adds a record to a subfile.</summary>
    public void Write(string name, IWorkstation workstation, bool read = false)
    {
        var format = File.Records[name];
        if (format.Subfile)
        {
            if (read) throw new RpgError(1221, $"EXFMT is for the subfile's control record, not {name}");
            var number = (int)Values.ToDecimal(NumberField(name).Get());
            var subfile = SubfileOf(name);
            if (number < 1 || subfile.Rows.ContainsKey(number)) throw new RpgError(1021, $"subfile {name} can't take record number {number}: it's below 1 or already there");
            subfile.Rows[number] = Snapshot(format);
            return;
        }
        if (format.Controls is { } controlled)
        {
            var subfile = SubfileOf(controlled);
            if (format.Clear?.Holds(On) == true) subfile.Rows.Clear();
            subfile.Top = 0;
            // SFLRCDNBR: the page with that record on it.
            if (format.Fields.FirstOrDefault(f => f.RecordNumber) is { Name: not null } positioner && format.PageSize > 0)
            {
                var wanted = (int)Values.ToDecimal(Variable(positioner).Get());
                var index = subfile.Rows.Keys.ToList().FindIndex(k => k >= wanted);
                if (wanted > 0 && index > 0) subfile.Top = index - index % format.PageSize;
            }
        }
        if (!format.Overlay) _shown.Clear();
        _shown.Remove(name);
        _shown.Add(name);
        var screen = Paint(read, null);
        if (read) Await(screen, format, workstation);
        else workstation.Send(screen);
    }

    /// <summary>READ: waits for the user on what WRITE put there.</summary>
    public void Read(string name, IWorkstation workstation)
    {
        var format = File.Records[name];
        if (format.Subfile) throw new RpgError(1221, $"READ is for the subfile's control record, not {name}: use READC");
        if (!_shown.Contains(name)) throw new RpgError(1221, $"{name} has to be written before it's read");
        Await(HostRecord(0x03, Join(WriteToDisplay(true), ReadMdtFields)), format, workstation);
    }

    /// <summary>READC: the next subfile record the user changed, into the program's fields. False when there's none.</summary>
    public bool ReadChanged(string name)
    {
        foreach (var (number, row) in SubfileOf(name).Rows)
        {
            if (!row.Changed) continue;
            row.Changed = false;
            Load(name, number, row);
            return true;
        }
        return false;
    }

    /// <summary>CHAIN: the subfile record of that number, into the program's fields. False when there's none.</summary>
    public bool Chain(string name, int number)
    {
        if (!File.Records[name].Subfile) throw new RpgError(1221, $"CHAIN is supported on subfile records, and {name} isn't one");
        if (!SubfileOf(name).Rows.TryGetValue(number, out var row)) return false;
        Load(name, number, row);
        return true;
    }

    /// <summary>UPDATE: the program's fields, back into the subfile record last read.</summary>
    public void Update(string name)
    {
        var format = File.Records[name];
        if (!format.Subfile) throw new RpgError(1221, $"UPDATE is supported on subfile records, and {name} isn't one");
        var number = (int)Values.ToDecimal(NumberField(name).Get());
        var rows = SubfileOf(name).Rows;
        if (!rows.ContainsKey(number)) throw new RpgError(1221, $"subfile {name} has no record {number} to update");
        rows[number] = Snapshot(format);
    }

    /// <summary>CLEAR of a record format: clears its fields. False when the file has no such record.</summary>
    public bool ClearRecord(string name)
    {
        if (!File.Records.TryGetValue(name, out var format)) return false;
        foreach (var field in format.Fields.Where(f => f.Name is not null)) variable(field.Name!)?.Clear();
        return true;
    }

    private Row Snapshot(RecordFormat format) =>
        new(format.Fields.Where(f => f.Name is not null).ToDictionary(f => f.Name!, f => Variable(f).Get()));

    private void Load(string name, int number, Row row)
    {
        foreach (var (field, value) in row.Values) variable(field)?.Set(value);
        NumberField(name).Set((decimal)number);
    }

    // ---- reading ----

    private void Await(byte[] screen, RecordFormat format, IWorkstation workstation)
    {
        var keys = format.Keys.Concat(File.Keys).ToList();
        while (true)
        {
            var input = workstation.Exchange(screen);
            var key = keys.FirstOrDefault(k => k.Aid == input.Aid && k.When.Holds(On));
            var roll = input.Aid is DisplayFile.RollUp or DisplayFile.RollDown;
            if (input.Aid != DisplayFile.Enter && key is null && !roll)
            {
                screen = HostRecord(0x03, Join(WriteToDisplay(true, Message("Function key not allowed.")), ReadMdtFields));
                continue;
            }
            if (key is not { Attention: true }) Apply(input);
            if (roll)
            {
                // The subfile pages by itself while it has records that way; past them, it's the program's key, if it has one.
                var control = format.Controls is not null ? format : _shown.Select(n => File.Records[n]).FirstOrDefault(r => r.Controls is not null);
                if (control is not null && Page(control, input.Aid == DisplayFile.RollUp))
                {
                    screen = Paint(true, null);
                    continue;
                }
                if (key is null)
                {
                    screen = Paint(true, "Roll up or down past the first or last record in file.");
                    continue;
                }
            }
            foreach (var k in keys.Where(k => k.Indicator > 0)) indicator(declaration, k.Indicator).Set(false);
            if (key is { Indicator: > 0 }) indicator(declaration, key.Indicator).Set(true);
            return;
        }
    }

    private bool Page(RecordFormat control, bool down)
    {
        var subfile = SubfileOf(control.Controls!);
        var size = Math.Max(control.PageSize, 1);
        if (control.Display?.Holds(On) != true) return false;
        if (down)
        {
            if (subfile.Top + size >= subfile.Rows.Count) return false;
            subfile.Top += size;
            return true;
        }
        if (subfile.Top == 0) return false;
        subfile.Top = Math.Max(0, subfile.Top - size);
        return true;
    }

    /// <summary>Moves what the user typed into the program's fields, or into the subfile records, marking those changed.</summary>
    private void Apply(ReceivedInput input)
    {
        foreach (var target in _targets)
        {
            var text = input.FieldAt(target.Row, target.Column);
            var field = target.Field;
            if (text is null)
            {
                // An input-only field nobody typed in comes back empty.
                if (field.Usage == 'I' && target.Subfile is null) Variable(field).Clear();
                continue;
            }
            object value = field.Numeric ? DisplayFile.ParseInput(text, field) : field.LowerCase ? text : text.ToUpperInvariant();
            if (target.Subfile is null)
            {
                Variable(field).Set(value);
            }
            else if (SubfileOf(target.Subfile).Rows.TryGetValue(target.Number, out var row))
            {
                row.Values[field.Name!] = value;
                row.Changed = true;
            }
        }
    }

    // ---- painting ----

    /// <summary>The whole screen: every record shown, in the order written.</summary>
    private byte[] Paint(bool read, string? message)
    {
        _targets.Clear();
        var orders = new List<byte[]> { Header(24) };
        (int Row, int Column)? cursor = null;
        foreach (var format in _shown.Select(n => File.Records[n]))
        {
            if (format.Controls is not { } controlled)
            {
                Fields(format, 0, f => Variable(f).Get(), null, 0, orders, ref cursor, ref message);
                continue;
            }
            if (format.DisplayControl?.Holds(On) == true) Fields(format, 0, f => Variable(f).Get(), null, 0, orders, ref cursor, ref message);
            var subfile = SubfileOf(controlled);
            if (format.Display?.Holds(On) != true || subfile.Rows.Count == 0 || !File.Records.TryGetValue(controlled, out var record)) continue;
            var numbers = subfile.Rows.Keys.ToList();
            var size = Math.Max(format.PageSize, 1);
            for (var i = 0; i < size && subfile.Top + i < numbers.Count; i++)
            {
                var number = numbers[subfile.Top + i];
                var row = subfile.Rows[number];
                Fields(record, i, f => row.Values.GetValueOrDefault(f.Name!, f.Numeric ? 0m : ""), controlled, number, orders, ref cursor, ref message);
            }
            var visible = record.Fields.Where(f => f.Usage != 'H').ToList();
            if (format.End is { } end && visible.Count > 0 && visible[0].Row + size <= 24)
            {
                var last = subfile.Top + size >= numbers.Count && end.Holds(On);
                orders.Add(Join(Sba(visible[0].Row + size, 72), [Highlight], Text(last ? " Bottom" : "More..."), [Normal]));
            }
        }
        orders.Add(Message(message));
        if (cursor is { } at) orders.Add(Ic(at.Row, at.Column));
        return HostRecord(read ? 0x03 : 0x02, Join(ClearUnit, WriteToDisplay(read, [.. orders]), read ? ReadMdtFields : []));
    }

    private void Fields(RecordFormat format, int offset, Func<DisplayField, object> value, string? subfile, int number,
        List<byte[]> orders, ref (int Row, int Column)? cursor, ref string? message)
    {
        foreach (var field in format.Fields)
        {
            if (field.Usage == 'H' || !field.When.Holds(On)) continue;
            var (row, column) = (field.Row + offset, field.Column);
            if (row > 24) continue;
            var attributes = field.Attributes.Where(a => a.When.Holds(On)).Select(a => a.Attribute).ToHashSet();
            if (field.Errors.FirstOrDefault(e => e.When.Holds(On)) is { Text: { } error })
            {
                message ??= error;
                attributes.Add("RI");
                attributes.Add("PC");
            }
            var input = field.Input && !attributes.Contains("PR");
            var text = field.Text ?? SystemText(field) ?? Format(field, value(field));
            if (column + text.Length > 81) text = text[..(81 - column)];
            var attribute = Attribute(attributes, field.Colors.LastOrDefault(c => c.When.Holds(On)).Color, input);
            // The attribute goes in the position before the field.
            var before = column > 1 ? Sba(row, column - 1) : row > 1 ? Sba(row - 1, 80) : null;
            if (input)
            {
                var ffw = field.Numeric ? 0x4300 : field.LowerCase ? 0x4000 : 0x4020;
                if (before is null) continue;
                orders.Add(Join(before, Sf(ffw, attribute, text.Length), field.Usage == 'B' ? Text(text) : []));
                _targets.Add(new Target(row, column, field, subfile, number));
                if (attributes.Contains("PC")) cursor ??= (row, column);
            }
            else
            {
                orders.Add(Join(before ?? Sba(row, column), before is null ? [] : [attribute], Text(text),
                    attribute != Normal && column + text.Length <= 80 ? [Normal] : []));
            }
        }
    }

    private static byte Attribute(HashSet<string> attributes, string? color, bool input)
    {
        if (attributes.Contains("ND")) return 0x27;
        var value = color switch
        {
            "WHT" => 0x22,
            "RED" => 0x28,
            "TRQ" => 0x30,
            "YLW" => 0x32,
            "PNK" => 0x38,
            "BLU" => 0x3a,
            _ => 0x20,
        };
        if (color is null or "GRN" && (attributes.Contains("HI") || attributes.Contains("BL"))) value |= 0x02;
        if (attributes.Contains("RI")) value |= 0x01;
        // Input fields are underlined unless the DDS says how they look.
        if (attributes.Contains("UL") || input && !attributes.Overlaps(["HI", "RI", "BL"]) && color is null) value |= 0x04;
        // Highlighted, reversed and underlined together would be nondisplay.
        return (byte)(value == 0x27 ? 0x26 : value);
    }

    private string? SystemText(DisplayField field) => field.System switch
    {
        null => null,
        "DATE" => field.EditCode is null ? DateTime.Now.ToString("MMddyy") : DateTime.Now.ToString("MM'/'dd'/'yy"),
        "TIME" => DateTime.Now.ToString("HH':'mm':'ss"),
        "USER" => user.PadRight(10),
        _ => "FAKEIBMI",
    };

    private static string Format(DisplayField field, object value)
    {
        if (!field.Numeric)
        {
            var text = Values.ToText(value);
            return text.Length >= field.Length ? text[..field.Length] : text.PadRight(field.Length);
        }
        return Values.Edit(Values.ToDecimal(value), field.Length, field.Decimals, field.EditCode ?? 'X');
    }
}
