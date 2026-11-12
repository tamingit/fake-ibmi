// Reads free-form RPG source into statements, and a statement into tokens.

using System.Globalization;
using System.Text;

namespace FakeIbmI.Rpg;

internal enum StatementKind
{
    Code,

    /// <summary>EXEC SQL: <see cref="SourceStatement.Text"/> is the SQL after those words.</summary>
    Sql,

    /// <summary>A compiler directive, such as /COPY.</summary>
    Directive,

    /// <summary>A fixed-form specification, which isn't supported.</summary>
    Fixed,
}

internal sealed record SourceStatement(StatementKind Kind, string Text, int Line);

/// <summary>Something the fake's RPG doesn't support. The message says what.</summary>
internal sealed class RpgUnsupported(string message) : Exception(message);

internal static class RpgSource
{
    /// <summary>
    /// The statements of a member. With **FREE on its first line, a line is all code. Without it, code
    /// is in columns 8 to 80, and a line with a specification letter in column 6 is fixed-form.
    /// </summary>
    public static List<SourceStatement> Statements(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var fullyFree = lines.Length > 0 && lines[0].TrimEnd().Equals("**FREE", StringComparison.OrdinalIgnoreCase);
        var statements = new List<SourceStatement>();
        var text = new StringBuilder();
        var startLine = 0;
        var inString = false;
        for (var n = fullyFree ? 1 : 0; n < lines.Length; n++)
        {
            var line = lines[n];
            if (!fullyFree)
            {
                if (line.Length >= 7 && line[6] == '*' && !inString) continue;
                if (line.Length >= 6 && "HFDCIOPhfdciop".Contains(line[5]) && line[..5].All(c => c == ' ' || char.IsDigit(c)) && !inString)
                {
                    statements.Add(new SourceStatement(StatementKind.Fixed, line.TrimEnd(), n + 1));
                    continue;
                }
                line = line.Length > 7 ? line[7..Math.Min(line.Length, 80)] : "";
            }
            var trimmed = line.Trim();
            if (!inString && text.ToString().Trim().Length == 0)
            {
                // Compile-time data ends the code.
                if (line.StartsWith("**"))
                {
                    statements.Add(new SourceStatement(StatementKind.Directive, trimmed, n + 1));
                    break;
                }
                if (trimmed.Length > 1 && trimmed[0] == '/' && char.IsLetter(trimmed[1]))
                {
                    statements.Add(new SourceStatement(StatementKind.Directive, trimmed, n + 1));
                    continue;
                }
            }
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inString)
                {
                    text.Append(c);
                    if (c != '\'') continue;
                    if (i + 1 < line.Length && line[i + 1] == '\'') text.Append(line[++i]);
                    else inString = false;
                    continue;
                }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                if (c == ';')
                {
                    Add(statements, text, startLine);
                    startLine = 0;
                    continue;
                }
                if (startLine == 0 && !char.IsWhiteSpace(c)) startLine = n + 1;
                if (c == '\'') inString = true;
                text.Append(c);
            }
            if (inString)
            {
                // A literal continues on the next line after + (from its first non-blank) or - (from its start).
                var soFar = text.ToString().TrimEnd();
                if (soFar.EndsWith('+') || soFar.EndsWith('-'))
                {
                    var plus = soFar.EndsWith('+');
                    text.Clear().Append(soFar[..^1]);
                    if (plus && n + 1 < lines.Length)
                    {
                        var next = lines[n + 1];
                        var body = fullyFree ? next : next.Length > 7 ? next[7..] : "";
                        lines[n + 1] = (fullyFree ? "" : new string(' ', 7)) + body.TrimStart();
                    }
                }
                else
                {
                    inString = false;
                }
            }
            else
            {
                text.Append('\n');
            }
        }
        if (text.ToString().Trim().Length > 0) Add(statements, text, startLine);
        return statements;
    }

    private static void Add(List<SourceStatement> statements, StringBuilder text, int line)
    {
        var statement = text.ToString().Trim();
        text.Clear();
        if (statement.Length == 0) return;
        var words = statement.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && words[0].Equals("EXEC", StringComparison.OrdinalIgnoreCase) && words[1].Equals("SQL", StringComparison.OrdinalIgnoreCase))
            statements.Add(new SourceStatement(StatementKind.Sql, words.Length > 2 ? words[2] : "", line));
        else statements.Add(new SourceStatement(StatementKind.Code, statement, line));
    }
}

internal enum TokenKind
{
    /// <summary>A name or operation code, upper case. Hyphenated ones (DCL-S, END-PROC) are one token.</summary>
    Name,
    Number,
    String,

    /// <summary>A special word, such as *ON or *INLR, upper case with its asterisk.</summary>
    Special,

    /// <summary>A built-in function, such as %TRIM, upper case with its percent sign.</summary>
    Bif,
    Op,
}

internal sealed record Token(TokenKind Kind, string Text, object? Value = null)
{
    public bool IsOp(string op) => Kind == TokenKind.Op && Text == op;
    public bool IsName(string name) => Kind == TokenKind.Name && Text == name;
}

internal static class RpgLexer
{
    private static readonly HashSet<string> Hyphenated = ["DCL", "END", "CTL", "ON", "EVAL", "XML", "DATA", "SND", "FOR", "WHEN"];
    private static readonly HashSet<string> BeforeSpecial = ["AND", "OR", "NOT", "TO", "DOWNTO", "BY"];

    public static List<Token> Tokens(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            var previous = tokens.Count > 0 ? tokens[^1] : null;
            if (c == '\'')
            {
                tokens.Add(new Token(TokenKind.String, "", ReadString(text, ref i)));
            }
            else if ((c is 'x' or 'X') && i + 1 < text.Length && text[i + 1] == '\'')
            {
                i++;
                var hex = ReadString(text, ref i);
                try
                {
                    tokens.Add(new Token(TokenKind.String, "", Ebcdic.Decode(Convert.FromHexString(hex))));
                }
                catch (FormatException)
                {
                    throw new RpgUnsupported($"x'{hex}' isn't a hexadecimal literal");
                }
            }
            else if (char.IsDigit(c) || c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                var start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))) i++;
                if (i < text.Length && text[i] == '.') i++;
                var number = text[start..i];
                if (i < text.Length && (char.IsLetter(text[i]) || text[i] == '_')) throw new RpgUnsupported($"{number}{text[i]}... isn't a number or a name");
                tokens.Add(new Token(TokenKind.Number, number, decimal.Parse(number.TrimEnd('.'), CultureInfo.InvariantCulture)));
            }
            else if (IsNameStart(c))
            {
                var start = i;
                while (i < text.Length && IsNameChar(text[i])) i++;
                var name = text[start..i].ToUpperInvariant();
                // DCL-S, END-DS, ON-ERROR and the like, at the start of a statement (or END-DS and END-PI at the end of one).
                while (i + 1 < text.Length && text[i] == '-' && char.IsLetter(text[i + 1]) && Hyphenated.Contains(name.Split('-')[0])
                       && (previous is null || name is "END"))
                {
                    var from = ++i;
                    while (i < text.Length && IsNameChar(text[i])) i++;
                    name += "-" + text[from..i].ToUpperInvariant();
                }
                tokens.Add(new Token(TokenKind.Name, name));
            }
            else if (c == '%' && i + 1 < text.Length && char.IsLetter(text[i + 1]))
            {
                var start = i++;
                while (i < text.Length && IsNameChar(text[i])) i++;
                tokens.Add(new Token(TokenKind.Bif, text[start..i].ToUpperInvariant()));
            }
            else if (c == '*' && i + 1 < text.Length && char.IsLetter(text[i + 1]) && SpecialFollows(tokens))
            {
                var start = i++;
                while (i < text.Length && IsNameChar(text[i])) i++;
                tokens.Add(new Token(TokenKind.Special, text[start..i].ToUpperInvariant()));
            }
            else
            {
                var two = i + 1 < text.Length ? text.Substring(i, 2) : "";
                if (two is "<>" or "<=" or ">=" or "+=" or "-=" or "*=" or "/=" or "**")
                {
                    // **= is rare enough to leave out.
                    tokens.Add(new Token(TokenKind.Op, two));
                    i += 2;
                }
                else if ("+-*/=<>():,.".Contains(c))
                {
                    tokens.Add(new Token(TokenKind.Op, c.ToString()));
                    i++;
                }
                else
                {
                    throw new RpgUnsupported($"the character {c} isn't expected here");
                }
            }
        }
        return tokens;
    }

    /// <summary>Whether an asterisk here starts a special word (*ON) rather than multiplying.</summary>
    private static bool SpecialFollows(List<Token> tokens)
    {
        if (tokens.Count == 0) return true;
        var previous = tokens[^1];
        return previous.Kind switch
        {
            TokenKind.Op => previous.Text != ")",
            TokenKind.Name => tokens.Count == 1 || BeforeSpecial.Contains(previous.Text),
            _ => false,
        };
    }

    private static bool IsNameStart(char c) => char.IsLetter(c) || c is '_' or '#' or '@' or '$';

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '#' or '@' or '$';

    private static string ReadString(string text, ref int i)
    {
        var value = new StringBuilder();
        i++;
        while (true)
        {
            if (i >= text.Length) throw new RpgUnsupported("a literal isn't closed");
            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    value.Append('\'');
                    i += 2;
                    continue;
                }
                i++;
                return value.ToString();
            }
            value.Append(text[i++]);
        }
    }
}
