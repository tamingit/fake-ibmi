// RPG's types and values. A value is a string (character), decimal (every numeric type but
// float), double (float), bool (indicator), DateOnly, TimeOnly, DateTime (timestamp), a DsValue
// or, for an array, the Cell that holds its elements.

using System.Globalization;

namespace FakeIbmI.Rpg;

internal enum RpgKind
{
    Char,
    Varchar,

    /// <summary>Packed, zoned and binary decimal: Length digits, Decimals of them after the point.</summary>
    Decimal,

    /// <summary>Integer, signed or not: Length is 3, 5, 10 or 20 digits.</summary>
    Int,
    Float,
    Ind,
    Date,
    Time,
    Timestamp,
    Ds,
}

internal sealed record RpgType(RpgKind Kind, int Length = 0, int Decimals = 0, bool Unsigned = false, DsLayout? Ds = null, int Dim = 0)
{
    public bool IsNumeric => Kind is RpgKind.Decimal or RpgKind.Int or RpgKind.Float;
    public RpgType Element => this with { Dim = 0 };

    public static RpgType Char(int length) => new(RpgKind.Char, length);
    public static RpgType Number(int digits, int decimals) => new(RpgKind.Decimal, digits, decimals);
    public static readonly RpgType Indicator = new(RpgKind.Ind, 1);
}

internal sealed record DsField(string Name, RpgType Type, Expr? Inz, int Position);

internal sealed record DsLayout(IReadOnlyList<DsField> Fields, bool Qualified);

/// <summary>A data structure's subfields, in order.</summary>
internal sealed class DsValue(DsLayout layout, IReadOnlyList<Cell> cells)
{
    public DsLayout Layout { get; } = layout;
    public IReadOnlyList<Cell> Cells { get; } = cells;

    public Cell? Field(string name)
    {
        for (var i = 0; i < Layout.Fields.Count; i++)
        {
            if (Layout.Fields[i].Name == name) return Cells[i];
        }
        return null;
    }
}

/// <summary>*BLANKS, *ZEROS, *HIVAL, *LOVAL, *ON, *OFF, *ALL'x' or *SYS: a value that takes its target's type.</summary>
internal sealed record SpecialValue(string Name, string? All = null);

/// <summary>%DAYS(n) and the like.</summary>
internal sealed record Duration(string Unit, decimal Count);

/// <summary>A run-time error, with RPG's status code (%STATUS). MONITOR catches it; otherwise it ends the program.</summary>
internal sealed class RpgError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>A variable, subfield, array or array element: its type and current value.</summary>
internal sealed class Cell
{
    public Cell(RpgType type)
    {
        Type = type;
        Value = null!;
        Clear();
    }

    public RpgType Type { get; }

    /// <summary>The value; for an array, null (see <see cref="Elements"/>).</summary>
    public object Value { get; private set; }

    public Cell[]? Elements { get; private set; }

    public DsValue Ds => (DsValue)Value;

    /// <summary>The value as an expression sees it: an array is the cell itself.</summary>
    public object Get() => Elements is null ? Value : this;

    public void Clear()
    {
        if (Type.Dim > 0)
        {
            Elements ??= [.. Enumerable.Range(0, Type.Dim).Select(_ => new Cell(Type.Element))];
            foreach (var e in Elements) e.Clear();
            return;
        }
        if (Type.Kind == RpgKind.Ds)
        {
            if (Value is DsValue ds)
            {
                foreach (var c in ds.Cells) c.Clear();
            }
            else
            {
                Value = new DsValue(Type.Ds!, [.. Type.Ds!.Fields.Select(f => new Cell(f.Type))]);
            }
            return;
        }
        Value = Values.Default(Type);
    }

    /// <summary>Assigns as EVAL does: converted to this type, padded or truncated; an array takes a value in every element.</summary>
    public void Set(object value, bool halfAdjust = false, bool right = false)
    {
        if (Elements is not null)
        {
            if (value is Cell { Elements: { } source })
            {
                for (var i = 0; i < Math.Min(source.Length, Elements.Length); i++) Elements[i].Set(source[i].Get());
            }
            else
            {
                foreach (var e in Elements) e.Set(value, halfAdjust, right);
            }
            return;
        }
        if (Type.Kind == RpgKind.Ds)
        {
            switch (value)
            {
                case DsValue other:
                    // Subfield by subfield, in order, as two structures of the same layout are.
                    for (var i = 0; i < Math.Min(other.Cells.Count, Ds.Cells.Count); i++) Ds.Cells[i].Set(other.Cells[i].Get());
                    break;
                case SpecialValue:
                    foreach (var c in Ds.Cells) c.Set(value);
                    break;
                case string s:
                    // A character value fills the character subfields from the left, as it would the structure's bytes.
                    var at = 0;
                    foreach (var c in Ds.Cells)
                    {
                        var length = Values.ByteLength(c.Type);
                        var piece = at < s.Length ? s.Substring(at, Math.Min(length, s.Length - at)) : "";
                        at += length;
                        if (c.Type.Kind is RpgKind.Char or RpgKind.Varchar) c.Set(piece);
                        else if (c.Type.IsNumeric) c.Set(decimal.TryParse(piece, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? Values.Scaled(n, c.Type) : 0m);
                    }
                    break;
                default:
                    throw new RpgError(100, "a data structure can't take this value");
            }
            return;
        }
        Value = Values.Convert(value, Type, halfAdjust, right);
    }
}

internal static class Values
{
    public static object Default(RpgType type) => type.Kind switch
    {
        RpgKind.Char => new string(' ', type.Length),
        RpgKind.Varchar => "",
        RpgKind.Decimal => WithScale(0m, type.Decimals),
        RpgKind.Int => 0m,
        RpgKind.Float => 0d,
        RpgKind.Ind => false,
        RpgKind.Date => new DateOnly(1, 1, 1),
        RpgKind.Time => new TimeOnly(0, 0, 0),
        RpgKind.Timestamp => new DateTime(1, 1, 1),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>How many characters the type takes in a data structure used as a string.</summary>
    public static int ByteLength(RpgType type) => type.Kind switch
    {
        RpgKind.Ds => type.Ds!.Fields.Sum(f => ByteLength(f.Type) * Math.Max(1, f.Type.Dim)),
        RpgKind.Date => 10,
        RpgKind.Time => 8,
        RpgKind.Timestamp => 26,
        RpgKind.Ind => 1,
        _ => type.Length,
    };

    /// <summary>The decimal with exactly this many decimal places, so %CHAR and the like show them.</summary>
    public static decimal WithScale(decimal value, int scale) =>
        decimal.Round(value, scale, MidpointRounding.ToZero) + new decimal(0, 0, 0, false, (byte)scale);

    /// <summary>Digits read from a data structure's characters, with the type's implied decimal point.</summary>
    public static decimal Scaled(decimal digits, RpgType type) => type.Kind == RpgKind.Decimal && digits == Math.Truncate(digits)
        ? digits / Pow10(type.Decimals)
        : digits;

    public static decimal Pow10(int n)
    {
        var result = 1m;
        for (var i = 0; i < n; i++) result *= 10;
        return result;
    }

    public static bool IsNumber(object v) => v is decimal or double;

    public static decimal ToDecimal(object v) => v switch
    {
        decimal d => d,
        double f => double.IsFinite(f) && Math.Abs(f) < 7.9e28 ? (decimal)f : throw new RpgError(103, "The target for a numeric operation is too small to hold the result."),
        bool b => b ? 1 : 0,
        string s => ParseNumber(s),
        SpecialValue { Name: "*ZERO" or "*ZEROS" } => 0,
        _ => throw new RpgError(105, "A character representation of a numeric value is in error."),
    };

    /// <summary>A number from characters, as %DEC and %INT read them: blanks ignored, a sign either end, a point or comma.</summary>
    public static decimal ParseNumber(string text)
    {
        var s = text.Replace(" ", "");
        if (s.Length == 0) return 0;
        var negative = s.StartsWith('-') || s.EndsWith('-');
        s = s.Trim('-', '+').Replace(',', '.');
        if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
            throw new RpgError(105, "A character representation of a numeric value is in error.");
        return negative ? -n : n;
    }

    public static string ToText(object v) => v switch
    {
        string s => s,
        bool b => b ? "1" : "0",
        decimal d => NumberText(d),
        double f => f.ToString("E", CultureInfo.InvariantCulture),
        DateOnly d => FormatDate(d, "*ISO"),
        TimeOnly t => FormatTime(t, "*ISO"),
        DateTime t => t.ToString("yyyy-MM-dd-HH.mm.ss.ffffff", CultureInfo.InvariantCulture),
        DsValue ds => string.Concat(ds.Cells.Select(DsText)),
        Cell { Elements: { } e } => string.Concat(e.Select(DsText)),
        SpecialValue { Name: "*BLANK" or "*BLANKS" } => "",
        SpecialValue { Name: "*ON" } => "1",
        SpecialValue { Name: "*OFF" } => "0",
        SpecialValue { Name: "*ALL", All: { } all } => all,
        _ => throw new RpgError(100, "this value has no character form"),
    };

    /// <summary>A subfield as it lies in its data structure: numbers as digits, with no point or sign.</summary>
    private static string DsText(Cell c)
    {
        if (c.Elements is not null) return string.Concat(c.Elements.Select(DsText));
        return c.Value switch
        {
            decimal d when c.Type.Kind == RpgKind.Decimal =>
                decimal.Truncate(Math.Abs(d) * Pow10(c.Type.Decimals)).ToString(CultureInfo.InvariantCulture).PadLeft(c.Type.Length, '0'),
            decimal d => d.ToString(CultureInfo.InvariantCulture).PadLeft(c.Type.Length),
            string s when c.Type.Kind == RpgKind.Varchar => s.PadRight(c.Type.Length),
            var other => ToText(other),
        };
    }

    /// <summary>A number as %CHAR shows it: every decimal place, no leading zero before the point.</summary>
    public static string NumberText(decimal d)
    {
        var text = d.ToString(CultureInfo.InvariantCulture);
        if (text.StartsWith("0.")) return text[1..];
        return text.StartsWith("-0.") ? "-" + text[2..] : text;
    }

    public static bool ToBool(object v) => v switch
    {
        bool b => b,
        string s => s.TrimEnd() == "1",
        SpecialValue { Name: "*ON" } => true,
        SpecialValue { Name: "*OFF" or "*BLANK" or "*BLANKS" or "*ZERO" or "*ZEROS" } => false,
        decimal d => d != 0,
        _ => throw new RpgError(100, "this value isn't an indicator"),
    };

    public static object Convert(object value, RpgType type, bool halfAdjust = false, bool right = false)
    {
        if (value is SpecialValue special) return FromSpecial(special, type);
        if (value is Cell or Duration) throw new RpgError(100, "the value and the variable it's assigned to don't match");
        switch (type.Kind)
        {
            case RpgKind.Char:
            {
                var s = ToText(value);
                if (right) return s.Length >= type.Length ? s[^type.Length..] : s.PadLeft(type.Length);
                return s.Length >= type.Length ? s[..type.Length] : s.PadRight(type.Length);
            }
            case RpgKind.Varchar:
            {
                var s = ToText(value);
                return s.Length > type.Length ? s[..type.Length] : s;
            }
            case RpgKind.Decimal:
            {
                var d = decimal.Round(ToDecimal(value), type.Decimals, halfAdjust ? MidpointRounding.AwayFromZero : MidpointRounding.ToZero);
                if (Math.Abs(decimal.Truncate(d)) >= Pow10(type.Length - type.Decimals))
                    throw new RpgError(103, "The target for a numeric operation is too small to hold the result.");
                return WithScale(d, type.Decimals);
            }
            case RpgKind.Int:
            {
                var d = decimal.Round(ToDecimal(value), 0, halfAdjust ? MidpointRounding.AwayFromZero : MidpointRounding.ToZero);
                var (low, high) = IntRange(type);
                if (d < low || d > high) throw new RpgError(103, "The target for a numeric operation is too small to hold the result.");
                return d;
            }
            case RpgKind.Float:
                return value is double f ? f : (double)ToDecimal(value);
            case RpgKind.Ind:
                return ToBool(value);
            case RpgKind.Date:
                return value switch
                {
                    DateOnly d => d,
                    DateTime t => DateOnly.FromDateTime(t),
                    string s => ParseDate(s, "*ISO"),
                    _ => throw new RpgError(112, "Date, Time or Timestamp value is not valid."),
                };
            case RpgKind.Time:
                return value switch
                {
                    TimeOnly t => t,
                    DateTime t => TimeOnly.FromDateTime(t),
                    string s => ParseTime(s),
                    _ => throw new RpgError(112, "Date, Time or Timestamp value is not valid."),
                };
            case RpgKind.Timestamp:
                return value switch
                {
                    DateTime t => t,
                    DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                    string s => ParseTimestamp(s),
                    _ => throw new RpgError(112, "Date, Time or Timestamp value is not valid."),
                };
            default:
                throw new InvalidOperationException();
        }
    }

    private static (decimal Low, decimal High) IntRange(RpgType type) => (type.Length, type.Unsigned) switch
    {
        (3, false) => (sbyte.MinValue, sbyte.MaxValue),
        (3, true) => (0, byte.MaxValue),
        (5, false) => (short.MinValue, short.MaxValue),
        (5, true) => (0, ushort.MaxValue),
        (10, false) => (int.MinValue, int.MaxValue),
        (10, true) => (0, uint.MaxValue),
        (_, false) => (long.MinValue, long.MaxValue),
        _ => (0, ulong.MaxValue),
    };

    private static object FromSpecial(SpecialValue special, RpgType type)
    {
        var high = special.Name == "*HIVAL";
        switch (special.Name)
        {
            case "*BLANK" or "*BLANKS":
                return type.Kind switch
                {
                    RpgKind.Char or RpgKind.Varchar or RpgKind.Ind => Default(type),
                    _ => throw new RpgError(100, "*BLANKS is for character variables"),
                };
            case "*ZERO" or "*ZEROS":
                return type.Kind == RpgKind.Char ? new string('0', type.Length) : type.Kind == RpgKind.Varchar ? "" : Default(type);
            case "*ON" or "*OFF":
                var on = special.Name == "*ON";
                return type.Kind switch
                {
                    RpgKind.Ind => on,
                    RpgKind.Char => new string(on ? '1' : '0', type.Length),
                    _ => throw new RpgError(100, $"{special.Name} is for indicators"),
                };
            case "*ALL":
                var all = special.All is { Length: > 0 } a ? a : " ";
                var repeated = string.Concat(Enumerable.Repeat(all, type.Length / all.Length + 1))[..Math.Max(type.Length, 0)];
                return type.IsNumeric ? Convert(ParseNumber(repeated), type) : Convert(repeated, type);
            case "*HIVAL" or "*LOVAL":
                return type.Kind switch
                {
                    RpgKind.Char or RpgKind.Varchar => new string(high ? Ebcdic.ToChar(0xff) : '\0', type.Length),
                    RpgKind.Decimal => WithScale((high ? 1 : -1) * (Pow10(type.Length) - 1) / Pow10(type.Decimals), type.Decimals),
                    RpgKind.Int => high ? IntRange(type).High : IntRange(type).Low,
                    RpgKind.Float => high ? double.MaxValue : double.MinValue,
                    RpgKind.Date => high ? new DateOnly(9999, 12, 31) : new DateOnly(1, 1, 1),
                    RpgKind.Time => high ? new TimeOnly(23, 59, 59) : new TimeOnly(0, 0, 0),
                    RpgKind.Timestamp => high ? new DateTime(9999, 12, 31, 23, 59, 59) : new DateTime(1, 1, 1),
                    _ => high,
                };
            case "*SYS":
                return type.Kind switch
                {
                    RpgKind.Date => DateOnly.FromDateTime(DateTime.Now),
                    RpgKind.Time => TimeOnly.FromDateTime(DateTime.Now),
                    RpgKind.Timestamp => DateTime.Now,
                    _ => throw new RpgError(100, "*SYS is for dates, times and timestamps"),
                };
            default:
                throw new RpgError(100, $"{special.Name} isn't supported as a value");
        }
    }

    /// <summary>Compares as RPG does: numbers by value, characters padded with blanks and in EBCDIC order.</summary>
    public static int Compare(object a, object b)
    {
        if (a is SpecialValue sa && b is not SpecialValue) return -Compare(b, sa);
        if (b is SpecialValue special)
        {
            b = a switch
            {
                string s => FromSpecial(special, RpgType.Char(Math.Max(s.Length, 1))),
                bool => FromSpecial(special, RpgType.Indicator),
                decimal => special.Name is "*HIVAL" ? decimal.MaxValue : special.Name is "*LOVAL" ? decimal.MinValue : FromSpecial(special, RpgType.Number(28, 0)),
                double => FromSpecial(special, new RpgType(RpgKind.Float, 8)),
                DateOnly => FromSpecial(special, new RpgType(RpgKind.Date)),
                TimeOnly => FromSpecial(special, new RpgType(RpgKind.Time)),
                DateTime => FromSpecial(special, new RpgType(RpgKind.Timestamp)),
                _ => throw new RpgError(100, "these values can't be compared"),
            };
        }
        if (a is bool || b is bool) (a, b) = (a is bool x ? (x ? "1" : "0") : a, b is bool y ? (y ? "1" : "0") : b);
        if (IsNumber(a) && IsNumber(b))
            return a is double || b is double ? System.Convert.ToDouble(a).CompareTo(System.Convert.ToDouble(b)) : ((decimal)a).CompareTo((decimal)b);
        if (a is DsValue or Cell) a = ToText(a);
        if (b is DsValue or Cell) b = ToText(b);
        if (a is string s1 && b is string s2)
        {
            var length = Math.Max(s1.Length, s2.Length);
            return Ebcdic.Encode(s1.PadRight(length)).AsSpan().SequenceCompareTo(Ebcdic.Encode(s2.PadRight(length)));
        }
        if (a.GetType() == b.GetType() && a is IComparable c) return c.CompareTo(b);
        if (a is DateTime t1 && b is DateOnly d2) return t1.CompareTo(d2.ToDateTime(TimeOnly.MinValue));
        if (a is DateOnly d1 && b is DateTime t2) return d1.ToDateTime(TimeOnly.MinValue).CompareTo(t2);
        throw new RpgError(100, "these values can't be compared");
    }

    // ---- dates and times ----

    private static readonly Dictionary<string, string> DateFormats = new()
    {
        ["*ISO"] = "yyyy-MM-dd", ["*USA"] = "MM/dd/yyyy", ["*EUR"] = "dd.MM.yyyy", ["*JIS"] = "yyyy-MM-dd",
        ["*MDY"] = "MM/dd/yy", ["*DMY"] = "dd/MM/yy", ["*YMD"] = "yy/MM/dd", ["*JUL"] = "yy/ddd", ["*LONGJUL"] = "yyyy/ddd",
        ["*CYMD"] = "cyy/MM/dd", ["*CMDY"] = "cMM/dd/yy", ["*CDMY"] = "cdd/MM/yy",
    };

    /// <summary>The .NET pattern for an RPG date format, such as *USA or *ISO0 (a 0 on the end: no separators).</summary>
    private static string DatePattern(string format)
    {
        var bare = format.EndsWith('0') && format.Length > 1;
        var name = bare ? format[..^1] : format;
        if (!DateFormats.TryGetValue(name, out var pattern)) throw new RpgError(100, $"the date format {format} isn't supported");
        return bare ? pattern.Replace("-", "").Replace("/", "").Replace(".", "") : pattern;
    }

    public static string FormatDate(DateOnly date, string format)
    {
        var pattern = DatePattern(format);
        if (pattern.Contains("ddd"))
        {
            var year = pattern.StartsWith("yyyy") ? date.Year.ToString("0000") : (date.Year % 100).ToString("00");
            return year + (pattern.Contains('/') ? "/" : "") + date.DayOfYear.ToString("000");
        }
        var century = pattern.StartsWith('c') ? ((date.Year - 1900) / 100).ToString() : "";
        return century + date.ToString(pattern.TrimStart('c'), CultureInfo.InvariantCulture);
    }

    public static DateOnly ParseDate(string text, string format)
    {
        var s = text.Trim();
        var pattern = DatePattern(format);
        if (pattern.StartsWith('c'))
        {
            // c is the century digit: 0 is 19xx, 1 is 20xx.
            var digits = new string(s.Where(char.IsDigit).ToArray());
            if (digits.Length == 7)
            {
                var order = format.TrimEnd('0');
                var (yy, mm, dd) = order switch
                {
                    "*CYMD" => (digits[1..3], digits[3..5], digits[5..7]),
                    "*CMDY" => (digits[5..7], digits[1..3], digits[3..5]),
                    _ => (digits[5..7], digits[3..5], digits[1..3]),
                };
                var iso = $"{1900 + (digits[0] - '0') * 100 + int.Parse(yy, CultureInfo.InvariantCulture):0000}-{mm}-{dd}";
                if (DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var c)) return c;
            }
            throw new RpgError(112, "Date, Time or Timestamp value is not valid.");
        }
        if (pattern.Contains("ddd"))
        {
            var digits = new string(s.Where(char.IsDigit).ToArray());
            var yearLength = pattern.StartsWith("yyyy") ? 4 : 2;
            if (digits.Length == yearLength + 3)
            {
                var year = int.Parse(digits[..yearLength], CultureInfo.InvariantCulture);
                if (yearLength == 2) year += year >= 40 ? 1900 : 2000;
                var day = int.Parse(digits[yearLength..], CultureInfo.InvariantCulture);
                if (day >= 1 && day <= (DateTime.IsLeapYear(year) ? 366 : 365)) return new DateOnly(year, 1, 1).AddDays(day - 1);
            }
            throw new RpgError(112, "Date, Time or Timestamp value is not valid.");
        }
        // Two-digit years: 40 to 99 are 19xx, as RPG has it.
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.Calendar.TwoDigitYearMax = 2039;
        if (DateOnly.TryParseExact(s, pattern, culture, DateTimeStyles.None, out var date)) return date;
        // Any separator will do, as will ISO whatever the format.
        var loose = pattern.Replace("/", "-").Replace(".", "-");
        if (DateOnly.TryParseExact(s.Replace("/", "-").Replace(".", "-"), loose, culture, DateTimeStyles.None, out date)) return date;
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return date;
        throw new RpgError(112, "Date, Time or Timestamp value is not valid.");
    }

    public static string FormatTime(TimeOnly time, string format) => format switch
    {
        "*ISO" or "*EUR" => time.ToString("HH.mm.ss", CultureInfo.InvariantCulture),
        "*HMS" or "*JIS" => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        "*USA" => time.ToString("hh:mm tt", CultureInfo.InvariantCulture),
        "*ISO0" or "*HMS0" or "*EUR0" or "*JIS0" => time.ToString("HHmmss", CultureInfo.InvariantCulture),
        _ => throw new RpgError(100, $"the time format {format} isn't supported"),
    };

    public static TimeOnly ParseTime(string text)
    {
        var s = text.Trim().Replace('.', ':');
        if (s.Length == 6 && s.All(char.IsDigit)) s = $"{s[..2]}:{s[2..4]}:{s[4..]}";
        return TimeOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new RpgError(112, "Date, Time or Timestamp value is not valid.");
    }

    public static DateTime ParseTimestamp(string text)
    {
        var s = text.Trim();
        string[] patterns =
        [
            "yyyy-MM-dd-HH.mm.ss.ffffff", "yyyy-MM-dd-HH.mm.ss", "yyyy-MM-dd HH:mm:ss.ffffff", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd", "yyyyMMddHHmmss", "yyyyMMddHHmmssffffff",
        ];
        return DateTime.TryParseExact(s, patterns, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : throw new RpgError(112, "Date, Time or Timestamp value is not valid.");
    }

    // ---- edit codes ----

    /// <summary>
    /// A number edited with an edit code, as %EDITC and a display file's EDTCDE show it:
    /// 1 to 4, A to D (CR), J to M (trailing minus), N to Q (leading minus), X, Y and Z.
    /// </summary>
    public static string Edit(decimal value, int digits, int decimals, char code)
    {
        code = char.ToUpperInvariant(code);
        var negative = value < 0;
        var unscaled = decimal.Truncate(Math.Abs(value) * Pow10(decimals)).ToString(CultureInfo.InvariantCulture);
        var all = unscaled.Length > digits ? unscaled[^digits..] : unscaled.PadLeft(digits, '0');
        switch (code)
        {
            case 'X':
                return all;
            case 'Z':
                return all.TrimStart('0').PadLeft(digits);
            case 'Y':
            {
                var d = all.PadLeft(digits <= 6 ? 6 : 8, '0');
                var text = d.Length == 6 ? $"{d[..2]}/{d[2..4]}/{d[4..]}" : $"{d[..2]}/{d[2..4]}/{d[4..]}";
                return text[0] == '0' ? " " + text[1..] : text;
            }
        }
        var group = "1AJN2BKO".Contains(code);
        var showZero = "1AJN3CLP".Contains(code);
        var sign = "ABCD".Contains(code) ? "CR" : "JKLM".Contains(code) ? "-" : "";
        var leading = "NOPQ".Contains(code);
        if (!group && !showZero && !"4DMQ".Contains(code)) throw new RpgError(100, $"the edit code {code} isn't supported");
        var integerDigits = digits - decimals;
        var width = integerDigits + (group ? Math.Max(0, (integerDigits - 1) / 3) : 0) + (decimals > 0 ? decimals + 1 : 0) + sign.Length + (leading ? 1 : 0);
        if (value == 0 && !showZero) return new string(' ', width);
        var whole = all[..integerDigits].TrimStart('0');
        if (group)
        {
            for (var i = whole.Length - 3; i > 0; i -= 3) whole = whole.Insert(i, ",");
        }
        if (whole.Length == 0 && decimals == 0) whole = "0";
        var body = whole + (decimals > 0 ? "." + all[integerDigits..] : "");
        if (leading && negative) body = "-" + body;
        body += sign.Length == 0 ? "" : negative ? sign : new string(' ', sign.Length);
        return body.PadLeft(width);
    }
}
