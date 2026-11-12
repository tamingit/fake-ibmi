// Display file DDS, parsed from its fixed columns into record formats and fields. What isn't
// supported becomes a diagnostic; the rest of the file still works.

using System.Globalization;

namespace FakeIbmI.Dds;

/// <summary>Option indicators, all of which have to hold: 03 is on, N03 is off. Empty always holds.</summary>
internal sealed record Condition(IReadOnlyList<(int Indicator, bool Not)> Terms)
{
    public static readonly Condition Always = new([]);

    public bool Holds(Func<int, bool> indicator) => Terms.All(t => indicator(t.Indicator) != t.Not);
}

/// <summary>A key the user may press: its AID byte, the response indicator it sets (0: none), and whether it's a CA key, which returns no data.</summary>
internal sealed record FunctionKey(int Aid, int Indicator, bool Attention, Condition When);

internal sealed class DisplayField
{
    /// <summary>The field's name, or null for a constant.</summary>
    public string? Name { get; init; }

    /// <summary>A constant's text.</summary>
    public string? Text { get; set; }

    /// <summary>DATE, TIME, USER or SYSNAME, for a constant the system fills in.</summary>
    public string? System { get; set; }

    public int Length { get; init; }
    public int Decimals { get; init; }
    public bool Numeric { get; init; }

    /// <summary>O (output), I (input), B (both) or H (hidden).</summary>
    public char Usage { get; init; } = 'O';

    public int Row { get; init; }
    public int Column { get; init; }
    public Condition When { get; init; } = Condition.Always;
    public List<(Condition When, string Attribute)> Attributes { get; } = [];
    public List<(Condition When, string Color)> Colors { get; } = [];
    public List<(Condition When, string Text)> Errors { get; } = [];
    public char? EditCode { get; set; }
    public bool LowerCase { get; set; }

    /// <summary>SFLRCDNBR: the field holds the subfile record whose page is shown.</summary>
    public bool RecordNumber { get; set; }

    public bool Input => Usage is 'I' or 'B';

    /// <summary>How many columns the field takes on the screen.</summary>
    public int Width => Text is not null ? Text.Length
        : System is not null ? SystemWidth
        : Numeric && EditCode is { } code ? Rpg.Values.Edit(0, Length, Decimals, code).Length
        : Length;

    private int SystemWidth => System switch
    {
        "DATE" => EditCode is null ? 6 : 8,
        "TIME" => 8,
        "USER" => 10,
        _ => 8,
    };
}

internal sealed class RecordFormat(string name)
{
    public string Name { get; } = name;
    public List<DisplayField> Fields { get; } = [];
    public List<FunctionKey> Keys { get; } = [];

    /// <summary>OVERLAY: what's on the screen stays when this record is written.</summary>
    public bool Overlay { get; set; }

    /// <summary>SFL: a subfile record.</summary>
    public bool Subfile { get; set; }

    /// <summary>SFLCTL(name): the subfile this record controls.</summary>
    public string? Controls { get; set; }

    public int PageSize { get; set; }
    public Condition? Display { get; set; }
    public Condition? DisplayControl { get; set; }
    public Condition? Clear { get; set; }
    public Condition? End { get; set; }
}

internal sealed class DisplayFile(string name)
{
    public string Name { get; } = name;
    public Dictionary<string, RecordFormat> Records { get; } = [];

    /// <summary>Keys allowed on every record.</summary>
    public List<FunctionKey> Keys { get; } = [];

    public List<Diagnostic> Diagnostics { get; } = [];

    // AID bytes.
    public const int Enter = 0xf1;
    public const int RollDown = 0xf4;
    public const int RollUp = 0xf5;

    /// <summary>The AID byte of function key 1 to 24.</summary>
    public static int Aid(int key) => key <= 12 ? 0x30 + key : 0xb0 + key - 12;

    private static readonly HashSet<string> Ignored =
    [
        "INDARA", "PRINT", "HELP", "ALTHELP", "MSGLOC", "CHGINPDFT", "ERRSFL", "OPENPRT", "PUTOVR", "OVRDTA", "OVRATR", "KEEP", "ASSUME",
        "BLINK", "ALARM", "TEXT", "RTNDTA", "ALIAS", "DFTVAL", "BLKFOLD", "AUTO", "INDTXT", "FLDCSRPRG", "LOCK", "FRCDTA", "ERASEINP",
        "INZRCD", "UNLOCK", "MDTOFF", "LOGINP", "LOGOUT", "HLPRTN", "HLPTITLE", "HLPCLR", "SFLCSRRRN", "SFLMODE", "SFLLIN", "CHRID", "NOCCSID",
        "ERASE", "HOME", "CLEAR", "SETOF", "SETOFF", "ALWROL", "USRDSPMGT", "INVITE", "ALTPAGEUP", "ALTPAGEDWN", "RETKEY", "RETCMDKEY",
    ];

    public static DisplayFile Parse(string name, string source)
    {
        var file = new DisplayFile(name);
        RecordFormat? record = null;
        DisplayField? field = null;
        var pending = "";
        var pendingLine = 0;
        var pendingWhen = Condition.Always;
        var lastEnd = (Row: 1, Column: 1);
        var lines = source.Replace("\r\n", "\n").Split('\n');
        void Report(Severity severity, string message, int line) => file.Diagnostics.Add(new Diagnostic(line, severity, message));

        for (var n = 0; n < lines.Length; n++)
        {
            var number = n + 1;
            var line = lines[n].TrimEnd().PadRight(80);
            if (line.Trim().Length == 0 || line[6] == '*') continue;
            if (line[5] is not ('A' or 'a' or ' '))
            {
                Report(Severity.Unsupported, "this isn't a DDS line: column 6 has to be A or blank", number);
                continue;
            }
            string Columns(int from, int to) => line[(from - 1)..to].Trim();

            // Option indicators, columns 8 to 16: up to three of N and a number.
            var terms = new List<(int, bool)>();
            var conditionProblem = line[6] is 'O' or 'o' or 'A' or 'a';
            for (var c = 7; c < 16; c += 3)
            {
                var digits = line.Substring(c + 1, 2).Trim();
                if (digits.Length == 0) continue;
                if (int.TryParse(digits, out var indicator) && indicator is >= 1 and <= 99) terms.Add((indicator, line[c] is 'N' or 'n'));
                else conditionProblem = true;
            }
            if (conditionProblem) Report(Severity.Unsupported, "conditions over more than one line (A and O) aren't supported", number);
            var when = terms.Count == 0 ? Condition.Always : new Condition(terms);

            var type = char.ToUpperInvariant(line[16]);
            var fieldName = Columns(19, 28).ToUpperInvariant();
            var position = Columns(39, 41);
            var keywords = line[44..Math.Min(line.Length, 80)].TrimEnd();

            if (type == 'R')
            {
                record = new RecordFormat(fieldName);
                file.Records[fieldName] = record;
                field = null;
            }
            else if (type is 'H' or 'K' or 'S' or 'J')
            {
                Report(Severity.Unsupported, type == 'H' ? "help specifications aren't supported" : $"type {type} specifications aren't for display files", number);
                continue;
            }
            else if (fieldName.Length > 0 || position.Length > 0)
            {
                if (record is null)
                {
                    Report(Severity.Unsupported, "a field has to follow a record format (R in column 17)", number);
                    continue;
                }
                if (line[28] is 'R' or 'r') Report(Severity.Unsupported, $"{fieldName} refers to a field in another file, which isn't supported: give its length here", number);
                var usage = char.ToUpperInvariant(line[37]);
                if (usage == ' ') usage = 'O';
                if (usage is 'M' or 'P')
                {
                    Report(Severity.Unsupported, $"usage {usage} fields aren't supported ({fieldName})", number);
                    usage = 'H';
                }
                var dataType = char.ToUpperInvariant(line[34]);
                if (dataType is 'L' or 'T' or 'Z' or 'F' or 'J' or 'E' or 'O' or 'G')
                    Report(Severity.Unsupported, $"data type {dataType} isn't supported ({fieldName}): use A, S or Y", number);
                var decimalsText = Columns(36, 37);
                int.TryParse(Columns(30, 34), out var length);
                int.TryParse(decimalsText, out var decimals);
                if (fieldName.Length > 0 && length == 0 && line[28] is not ('R' or 'r')) Report(Severity.Unsupported, $"{fieldName} needs a length", number);
                var (row, column) = (0, 0);
                if (usage != 'H')
                {
                    var columnText = Columns(42, 44);
                    if (position.StartsWith('+') || columnText.StartsWith('+'))
                    {
                        // +n: n columns after the previous field.
                        int.TryParse(columnText.TrimStart('+'), out var gap);
                        (row, column) = (lastEnd.Row, lastEnd.Column + gap);
                    }
                    else if (!int.TryParse(position, out row) | !int.TryParse(columnText, out column) || row is < 1 or > 24 || column is < 1 or > 80)
                    {
                        Report(Severity.Unsupported, $"line {position} position {columnText} isn't on a 24 by 80 screen", number);
                        (row, column) = (1, 1);
                    }
                }
                field = new DisplayField
                {
                    Name = fieldName.Length > 0 ? fieldName : null,
                    Length = length,
                    Decimals = decimals,
                    Numeric = dataType is 'S' or 'Y' or 'D' or 'P' or 'B' || dataType == ' ' && decimalsText.Length > 0,
                    Usage = usage,
                    Row = row,
                    Column = column,
                    When = when,
                };
                if (field.Numeric && (length > 28 || decimals > length)) Report(Severity.Unsupported, $"{fieldName}: numbers of more than 28 digits aren't supported", number);
                record.Fields.Add(field);
            }

            // Keywords, which may continue on the next line after + or -.
            var continues = keywords.EndsWith('+') || keywords.EndsWith('-');
            if (pending.Length == 0)
            {
                pendingLine = number;
                pendingWhen = when;
            }
            pending += continues ? keywords[..^1] + (keywords.EndsWith('-') ? "" : "") : keywords;
            if (continues)
            {
                if (keywords.EndsWith('+') && n + 1 < lines.Length && lines[n + 1].Length > 44)
                    lines[n + 1] = lines[n + 1][..44] + lines[n + 1][44..].TrimStart();
                continue;
            }
            foreach (var (keyword, argument) in Keywords(pending))
            {
                try
                {
                    file.Apply(keyword, argument, record, field, pendingWhen);
                }
                catch (FormatException e)
                {
                    Report(Severity.Unsupported, e.Message, pendingLine);
                }
            }
            pending = "";
            if (field is { Usage: not 'H' }) lastEnd = (field.Row, field.Column + field.Width);
        }

        foreach (var format in file.Records.Values)
        {
            if (format.Controls is { } controlled)
            {
                if (!file.Records.TryGetValue(controlled, out var subfile) || !subfile.Subfile)
                    Report(Severity.Unsupported, $"SFLCTL({controlled}): there's no subfile record of that name before {format.Name}", 0);
                else if (subfile.Fields.Where(f => f.Usage != 'H').Select(f => f.Row).Distinct().Count() > 1)
                    Report(Severity.Unsupported, $"subfile record {controlled} takes more than one line, which isn't supported", 0);
                if (format.PageSize < 1) Report(Severity.Unsupported, $"{format.Name} needs SFLPAG", 0);
            }
        }
        if (file.Records.Count == 0) Report(Severity.Unsupported, "the display file has no record format", 0);
        return file;
    }

    /// <summary>Splits a line's functions: NAME, NAME(arguments) or a quoted constant, which comes back as keyword "'".</summary>
    private static IEnumerable<(string Keyword, string Argument)> Keywords(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            if (text[i] == '\'')
            {
                yield return ("'", Quoted(text, ref i));
                continue;
            }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '(') i++;
            var keyword = text[start..i].ToUpperInvariant();
            var argument = "";
            if (i < text.Length && text[i] == '(')
            {
                var from = ++i;
                var depth = 1;
                while (i < text.Length && depth > 0)
                {
                    if (text[i] == '\'')
                    {
                        Quoted(text, ref i);
                        continue;
                    }
                    if (text[i] == '(') depth++;
                    else if (text[i] == ')') depth--;
                    i++;
                }
                argument = text[from..Math.Max(from, i - 1)];
            }
            yield return (keyword, argument);
        }
    }

    private static string Quoted(string text, ref int i)
    {
        var value = new System.Text.StringBuilder();
        i++;
        while (i < text.Length)
        {
            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    value.Append('\'');
                    i += 2;
                    continue;
                }
                i++;
                break;
            }
            value.Append(text[i++]);
        }
        return value.ToString();
    }

    /// <summary>Applies one keyword to the file, record or field it's written on. Throws FormatException for what isn't supported.</summary>
    private void Apply(string keyword, string argument, RecordFormat? record, DisplayField? field, Condition when)
    {
        string[] Words() => argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int Indicator() => Words().FirstOrDefault() is { } word && int.TryParse(word, out var n) && n is >= 1 and <= 99 ? n : 0;
        FormatException No(string what) => new($"{what} isn't supported");
        var keys = record?.Keys ?? Keys;

        if (keyword == "'")
        {
            if (field is null || field.Name is not null) throw No("a constant with no line and position");
            field.Text = argument;
            return;
        }
        if (keyword.Length == 4 && keyword[..2] is "CA" or "CF" && int.TryParse(keyword[2..], out var key) && key is >= 1 and <= 24)
        {
            keys.Add(new FunctionKey(Aid(key), Indicator(), keyword[1] == 'A', when));
            return;
        }
        if (Ignored.Contains(keyword)) return;
        switch (keyword)
        {
            case "DSPSIZ":
                if (!(argument.Contains("24 80") || argument.Contains("*DS3"))) throw No($"DSPSIZ({argument}): only 24 by 80 (*DS3)");
                return;
            case "ROLLUP" or "PAGEDOWN":
                keys.Add(new FunctionKey(RollUp, Indicator(), false, when));
                return;
            case "ROLLDOWN" or "PAGEUP":
                keys.Add(new FunctionKey(RollDown, Indicator(), false, when));
                return;
        }
        if (record is null) throw No($"the file keyword {keyword}");
        if (field is null)
        {
            switch (keyword)
            {
                case "OVERLAY" or "CLRL":
                    record.Overlay = true;
                    return;
                case "SFL":
                    record.Subfile = true;
                    return;
                case "SFLCTL":
                    record.Controls = argument.Trim().ToUpperInvariant();
                    return;
                case "SFLSIZ":
                    return;
                case "SFLPAG":
                    record.PageSize = int.TryParse(argument, out var page) && page is >= 1 and <= 23 ? page : throw No($"SFLPAG({argument})");
                    return;
                case "SFLDSP":
                    record.Display = when;
                    return;
                case "SFLDSPCTL":
                    record.DisplayControl = when;
                    return;
                case "SFLCLR" or "SFLDLT":
                    record.Clear = when;
                    return;
                case "SFLEND":
                    record.End = when;
                    return;
                default:
                    throw No($"the record keyword {keyword}");
            }
        }
        switch (keyword)
        {
            case "DSPATR":
                foreach (var attribute in Words().Select(w => w.ToUpperInvariant()))
                {
                    if (attribute is not ("HI" or "RI" or "UL" or "BL" or "ND" or "PC" or "PR" or "CS" or "MDT" or "OID" or "SP"))
                        throw No($"DSPATR({attribute})");
                    field.Attributes.Add((when, attribute));
                }
                return;
            case "COLOR":
                if (argument.Trim().ToUpperInvariant() is not ("GRN" or "WHT" or "RED" or "TRQ" or "YLW" or "PNK" or "BLU")) throw No($"COLOR({argument})");
                field.Colors.Add((when, argument.Trim().ToUpperInvariant()));
                return;
            case "EDTCDE":
            {
                var code = Words().FirstOrDefault() ?? "";
                if (code.Length != 1 || !"1234ABCDJKLMNOPQXYZ".Contains(char.ToUpperInvariant(code[0]))) throw No($"EDTCDE({argument})");
                if (Words().Length > 1) throw No("EDTCDE with a fill character or currency symbol");
                field.EditCode = char.ToUpperInvariant(code[0]);
                return;
            }
            case "CHECK" or "CHK":
                if (Words().Any(w => w.Equals("LC", StringComparison.OrdinalIgnoreCase))) field.LowerCase = true;
                return;
            case "LOWER":
                field.LowerCase = true;
                return;
            case "ERRMSG":
            {
                var i = argument.IndexOf('\'');
                if (i < 0) throw No("ERRMSG without a message");
                field.Errors.Add((when, Quoted(argument, ref i)));
                return;
            }
            case "DFT":
            {
                var i = argument.IndexOf('\'');
                if (i < 0 || field.Name is not null) throw No("DFT on a named field");
                field.Text = Quoted(argument, ref i);
                return;
            }
            case "DATE" or "TIME" or "USER" or "SYSNAME":
                if (field.Name is not null) throw No($"{keyword} on a named field");
                field.System = keyword;
                return;
            case "SFLRCDNBR":
                field.RecordNumber = true;
                return;
            case "VALUES" or "RANGE" or "COMP" or "CMP":
                throw new FormatException($"{keyword} isn't checked: the program gets whatever is typed");
            default:
                throw No($"the field keyword {keyword}");
        }
    }

    /// <summary>The number typed into a numeric field: without a decimal point, the last digits are the decimals, as on a real display.</summary>
    public static decimal ParseInput(string text, DisplayField field)
    {
        var negative = text.Contains('-') || text.TrimEnd().EndsWith("CR", StringComparison.OrdinalIgnoreCase);
        var digits = new string(text.Where(c => char.IsDigit(c) || c == '.').ToArray());
        if (digits.Length == 0 || !decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return 0;
        if (!digits.Contains('.')) value /= Rpg.Values.Pow10(field.Decimals);
        value = decimal.Round(value, field.Decimals, MidpointRounding.ToZero) % Rpg.Values.Pow10(field.Length - field.Decimals);
        return negative ? -value : value;
    }
}
