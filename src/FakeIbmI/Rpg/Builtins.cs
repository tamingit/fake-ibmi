// RPG's built-in functions. RpgParser.Bifs lists the ones here; any other is reported when the member is saved.

using System.Globalization;

namespace FakeIbmI.Rpg;

internal sealed partial class Interpreter
{
    private object Builtin(BifExpr bif, Frame frame)
    {
        var args = bif.Args;
        object Value(int i) => i < args.Count ? Evaluate(args[i], frame) : throw new RpgError(100, $"{bif.Name} needs more parameters");
        string Text(int i) => Values.ToText(Value(i));
        decimal Number(int i) => Values.ToDecimal(Value(i));
        int Int(int i) => (int)Number(i);
        bool Has(int i) => i < args.Count;
        string Word(int i) => i < args.Count && args[i] is SpecialExpr s ? s.Name : throw new RpgError(100, $"{bif.Name} needs a word such as *ISO or *DAYS");
        RpgError Range() => new(100, "Value out of range for string operation.");

        switch (bif.Name)
        {
            case "%TRIM":
                return Has(1) ? Text(0).Trim(Text(1).ToCharArray()) : Text(0).Trim(' ');
            case "%TRIMR":
                return Has(1) ? Text(0).TrimEnd(Text(1).ToCharArray()) : Text(0).TrimEnd(' ');
            case "%TRIML":
                return Has(1) ? Text(0).TrimStart(Text(1).ToCharArray()) : Text(0).TrimStart(' ');
            case "%LEN":
                if (Has(0) && IsVariable(args[0]) && Root(args[0]).Length > 0 && Find(Root(args[0]), frame) is not null)
                {
                    var cell = Locate(args[0], frame);
                    if (cell.Type.Kind != RpgKind.Varchar && cell.Elements is null) return (decimal)Values.ByteLength(cell.Type);
                }
                return (decimal)Text(0).Length;
            case "%SIZE":
                return (decimal)Values.ByteLength(Locate(args.Count > 0 ? args[0] : throw new RpgError(100, "%SIZE needs a variable"), frame).Type);
            case "%DECPOS":
                return (decimal)Number(0).Scale;
            case "%SUBST":
            {
                var (s, start) = (Text(0), Int(1));
                var length = Has(2) ? Int(2) : s.Length - start + 1;
                if (start < 1 || length < 0 || start + length - 1 > s.Length) throw Range();
                return s.Substring(start - 1, length);
            }
            case "%LEFT":
                return Text(0)[..Math.Clamp(Int(1), 0, Text(0).Length)];
            case "%RIGHT":
            {
                var s = Text(0);
                return s[(s.Length - Math.Clamp(Int(1), 0, s.Length))..];
            }
            case "%SCAN":
            {
                var (find, s) = (Text(0), Text(1));
                var start = Has(2) ? Int(2) : 1;
                if (start < 1 || start > Math.Max(s.Length, 1)) throw Range();
                return (decimal)(find.Length == 0 ? 0 : s.IndexOf(find, start - 1, StringComparison.Ordinal) + 1);
            }
            case "%SCANRPL":
                return Text(0).Length == 0 ? Text(2) : Text(2).Replace(Text(0), Text(1), StringComparison.Ordinal);
            case "%REPLACE":
            {
                var (replacement, s) = (Text(0), Text(1));
                var start = Has(2) ? Int(2) : 1;
                var length = Has(3) ? Int(3) : replacement.Length;
                if (start < 1 || start > s.Length + 1 || length < 0) throw Range();
                var end = Math.Min(start - 1 + length, s.Length);
                return s[..(start - 1)] + replacement + s[end..];
            }
            case "%XLATE":
            {
                var (from, to, s) = (Text(0), Text(1), Text(2));
                return string.Concat(s.Select(c => from.IndexOf(c) is >= 0 and var i && i < to.Length ? to[i] : c));
            }
            case "%UPPER":
                return Text(0).ToUpperInvariant();
            case "%LOWER":
                return Text(0).ToLowerInvariant();
            case "%CONCAT":
                return string.Join(Value(0) is SpecialValue ? "" : Text(0), Enumerable.Range(1, args.Count - 1).Select(Text));
            case "%CHECK" or "%CHECKR":
            {
                var (allowed, s) = (Text(0), Text(1));
                var forward = bif.Name == "%CHECK";
                var start = Has(2) ? Int(2) : forward ? 1 : s.Length;
                if (s.Length > 0 && (start < 1 || start > s.Length)) throw Range();
                for (var i = start - 1; i >= 0 && i < s.Length; i += forward ? 1 : -1)
                {
                    if (!allowed.Contains(s[i])) return (decimal)(i + 1);
                }
                return 0m;
            }
            case "%CHAR":
            {
                var value = Value(0);
                return value switch
                {
                    DateOnly d => Values.FormatDate(d, Has(1) ? Word(1) : "*ISO"),
                    TimeOnly t => Values.FormatTime(t, Has(1) ? Word(1) : "*ISO"),
                    string s => s,
                    _ => Values.ToText(value),
                };
            }
            case "%DEC" or "%DECH":
            {
                var value = Value(0);
                if (value is DateOnly date) return Values.ParseNumber(Values.FormatDate(date, (Has(1) ? Word(1) : "*ISO").TrimEnd('0') + "0"));
                if (value is TimeOnly time) return Values.ParseNumber(Values.FormatTime(time, "*ISO0"));
                if (value is DateTime stamp) return Values.ParseNumber(stamp.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
                if (!Has(2)) return value is string ? Values.ToDecimal(value) : decimal.Truncate(Values.ToDecimal(value));
                return Values.Convert(value, RpgType.Number(Int(1), Int(2)), halfAdjust: bif.Name == "%DECH");
            }
            case "%INT" or "%UNS":
                return decimal.Truncate(Number(0));
            case "%INTH" or "%UNSH":
                return decimal.Round(Number(0), 0, MidpointRounding.AwayFromZero);
            case "%FLOAT":
                return (double)Number(0);
            case "%EDITC":
            {
                var value = Number(0);
                var code = Text(1);
                if (code.Length != 1) throw new RpgError(100, "%EDITC needs a one-character edit code");
                var (digits, decimals) = (Math.Max(decimal.Truncate(Math.Abs(value)).ToString(CultureInfo.InvariantCulture).Length, 1) + value.Scale, (int)value.Scale);
                if (IsVariable(args[0]) && Find(Root(args[0]), frame) is not null && Locate(args[0], frame) is { Type: { IsNumeric: true } type, Elements: null })
                    (digits, decimals) = (type.Length, type.Decimals);
                return Values.Edit(value, digits, decimals, code[0]);
            }
            case "%ABS":
                return Value(0) is double f ? Math.Abs(f) : Math.Abs(Number(0));
            case "%SQRT":
                return Number(0) < 0 ? throw new RpgError(101, "Negative square root.") : (double)Math.Sqrt((double)Number(0));
            case "%REM" or "%DIV":
            {
                var (a, b) = (decimal.Truncate(Number(0)), decimal.Truncate(Number(1)));
                if (b == 0) throw new RpgError(102, "Attempt to divide by zero.");
                return bif.Name == "%REM" ? a % b : decimal.Truncate(a / b);
            }
            case "%MIN" or "%MAX":
            {
                var best = Value(0);
                for (var i = 1; i < args.Count; i++)
                {
                    var other = Value(i);
                    if (bif.Name == "%MIN" ? Values.Compare(other, best) < 0 : Values.Compare(other, best) > 0) best = other;
                }
                return best;
            }
            case "%EOF":
                return _eof;
            case "%FOUND":
                return _found;
            case "%EQUAL":
                return false;
            case "%ERROR":
                return _error;
            case "%STATUS":
                return (decimal)_status;
            case "%PARMS":
                return (decimal)frame.Parameters;
            case "%PASSED":
                return args is [NameExpr name] && frame.Variables.ContainsKey(name.Name);
            case "%OMITTED":
                return false;
            case "%ELEM":
                return (decimal)(Elements(0).Length);
            case "%XFOOT":
                return Elements(0).Sum(c => Values.ToDecimal(c.Get()));
            case "%LOOKUP":
            {
                var (wanted, elements) = (Value(0), Elements(1));
                var start = Has(2) ? Int(2) : 1;
                var count = Has(3) ? Int(3) : elements.Length;
                for (var i = Math.Max(start, 1); i <= Math.Min(elements.Length, start + count - 1); i++)
                {
                    if (Values.Compare(elements[i - 1].Get(), wanted) == 0) return (decimal)i;
                }
                return 0m;
            }
            case "%MAXARR" or "%MINARR":
            {
                var elements = Elements(0);
                var best = 0;
                for (var i = 1; i < elements.Length; i++)
                {
                    var compared = Values.Compare(elements[i].Get(), elements[best].Get());
                    if (bif.Name == "%MAXARR" ? compared > 0 : compared < 0) best = i;
                }
                return (decimal)(best + 1);
            }
            case "%DATE":
            {
                if (!Has(0)) return DateOnly.FromDateTime(DateTime.Now);
                var value = Value(0);
                var format = Has(1) ? Word(1) : "*ISO";
                return value switch
                {
                    DateOnly d => d,
                    DateTime t => DateOnly.FromDateTime(t),
                    decimal n => Values.ParseDate(decimal.Truncate(n).ToString(CultureInfo.InvariantCulture).PadLeft(DigitsOf(format), '0'), format.TrimEnd('0') + "0"),
                    _ => Values.ParseDate(Values.ToText(value), format),
                };
            }
            case "%TIME":
            {
                if (!Has(0)) return TimeOnly.FromDateTime(DateTime.Now);
                var value = Value(0);
                return value switch
                {
                    TimeOnly t => t,
                    DateTime t => TimeOnly.FromDateTime(t),
                    decimal n => Values.ParseTime(decimal.Truncate(n).ToString(CultureInfo.InvariantCulture).PadLeft(6, '0')),
                    _ => Values.ParseTime(Values.ToText(value)),
                };
            }
            case "%TIMESTAMP":
            {
                if (!Has(0)) return DateTime.Now;
                var value = Value(0);
                return value switch
                {
                    DateTime t => t,
                    DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                    decimal n => Values.ParseTimestamp(decimal.Truncate(n).ToString(CultureInfo.InvariantCulture)),
                    _ => Values.ParseTimestamp(Values.ToText(value)),
                };
            }
            case "%DAYS" or "%MONTHS" or "%YEARS" or "%HOURS" or "%MINUTES" or "%SECONDS":
                return new Duration("*" + bif.Name[1..], Number(0));
            case "%DIFF":
            {
                var (a, b) = (Moment(Value(0)), Moment(Value(1)));
                var span = a - b;
                return Unit(Word(2)) switch
                {
                    "*DAYS" => decimal.Truncate((decimal)span.TotalDays),
                    "*HOURS" => decimal.Truncate((decimal)span.TotalHours),
                    "*MINUTES" => decimal.Truncate((decimal)span.TotalMinutes),
                    "*SECONDS" => decimal.Truncate((decimal)span.TotalSeconds),
                    "*MSECONDS" => span.Ticks / 10,
                    "*MONTHS" => Months(a, b),
                    "*YEARS" => decimal.Truncate(Months(a, b) / 12),
                    var unit => throw new RpgError(100, $"%DIFF doesn't support {unit}"),
                };
            }
            case "%SUBDT":
            {
                var moment = Moment(Value(0));
                return Unit(Word(1)) switch
                {
                    "*YEARS" => moment.Year,
                    "*MONTHS" => moment.Month,
                    "*DAYS" => moment.Day,
                    "*HOURS" => moment.Hour,
                    "*MINUTES" => moment.Minute,
                    "*SECONDS" => moment.Second,
                    "*MSECONDS" => (decimal)(moment.Ticks % TimeSpan.TicksPerSecond / 10),
                    var unit => throw new RpgError(100, $"%SUBDT doesn't support {unit}"),
                };
            }
            default:
                throw new RpgError(100, $"{bif.Name} isn't supported");
        }

        Cell[] Elements(int i) => Value(i) is Cell { Elements: { } elements } ? elements : throw new RpgError(100, $"{bif.Name} needs an array");
    }

    /// <summary>How many digits a numeric date has in this format.</summary>
    private static int DigitsOf(string format) => format.TrimEnd('0') switch
    {
        "*ISO" or "*USA" or "*EUR" or "*JIS" => 8,
        "*CYMD" or "*CMDY" or "*CDMY" or "*LONGJUL" => 7,
        "*JUL" => 5,
        _ => 6,
    };

    private static DateTime Moment(object value) => value switch
    {
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        TimeOnly t => new DateTime(2000, 1, 1).Add(t.ToTimeSpan()),
        DateTime t => t,
        _ => throw new RpgError(112, "Date, Time or Timestamp value is not valid."),
    };

    private static string Unit(string word) => word switch
    {
        "*D" or "*DAYS" => "*DAYS",
        "*M" or "*MONTHS" => "*MONTHS",
        "*Y" or "*YEARS" => "*YEARS",
        "*H" or "*HOURS" => "*HOURS",
        "*MN" or "*MINUTES" => "*MINUTES",
        "*S" or "*SECONDS" => "*SECONDS",
        "*MS" or "*MSECONDS" => "*MSECONDS",
        _ => word,
    };

    /// <summary>Whole months from <paramref name="b"/> to <paramref name="a"/>.</summary>
    private static decimal Months(DateTime a, DateTime b)
    {
        var months = (a.Year - b.Year) * 12 + a.Month - b.Month;
        if (months > 0 && a.Day < b.Day) months--;
        if (months < 0 && a.Day > b.Day) months++;
        return months;
    }
}
