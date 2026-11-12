// Parses free-form RPG. Whatever it can't parse, or the fake can't run, becomes a diagnostic and
// (where it's a statement) an UnsupportedStmt, so the rest of the program still runs.

namespace FakeIbmI.Rpg;

internal sealed class RpgParser
{
    private readonly List<SourceStatement> _statements;
    private readonly RpgProgram _program = new();
    private readonly Func<string, string?>? _prepareProblem;
    private readonly HashSet<string> _otherFiles = [];
    private readonly HashSet<string> _printers = [];
    private int _next;
    private List<Token> _t = [];
    private int _p;
    private int _line;
    private Procedure _scope;

    private RpgParser(string source, Func<string, string?>? prepareProblem)
    {
        _statements = RpgSource.Statements(source);
        _prepareProblem = prepareProblem;
        _scope = _program.Main;
    }

    /// <summary>
    /// Parses a member. <paramref name="prepareProblem"/> says why SQLite won't take a translated
    /// SQL statement (null when it will); without it, SQL is translated but not tried.
    /// </summary>
    public static RpgProgram Parse(string source, Func<string, string?>? prepareProblem = null)
    {
        var parser = new RpgParser(source, prepareProblem);
        parser._program.Main.Body = parser.Block().Body;
        return parser._program;
    }

    // ---- built-in functions and operation codes ----

    internal static readonly HashSet<string> Bifs =
    [
        "%TRIM", "%TRIMR", "%TRIML", "%LEN", "%SUBST", "%SCAN", "%SCANRPL", "%REPLACE", "%CHAR", "%DEC", "%DECH", "%INT", "%INTH",
        "%UNS", "%UNSH", "%FLOAT", "%EDITC", "%XLATE", "%UPPER", "%LOWER", "%ABS", "%REM", "%DIV", "%SQRT", "%EOF", "%FOUND", "%ERROR", "%STATUS",
        "%ELEM", "%DATE", "%TIME", "%TIMESTAMP", "%DAYS", "%MONTHS", "%YEARS", "%HOURS", "%MINUTES", "%SECONDS", "%DIFF", "%SUBDT",
        "%PARMS", "%SIZE", "%LOOKUP", "%XFOOT", "%MIN", "%MAX", "%CHECK", "%CHECKR", "%CONCAT", "%LEFT", "%RIGHT", "%PASSED", "%OMITTED",
        "%EQUAL", "%DECPOS", "%MAXARR", "%MINARR",
    ];

    private static readonly Dictionary<string, string> UnsupportedOps = new()
    {
        ["SETLL"] = "native file I/O", ["SETGT"] = "native file I/O", ["READE"] = "native file I/O", ["READP"] = "native file I/O",
        ["READPE"] = "native file I/O", ["DELETE"] = "native file I/O", ["OPEN"] = "native file I/O", ["CLOSE"] = "native file I/O",
        ["UNLOCK"] = "native file I/O", ["FEOD"] = "native file I/O", ["COMMIT"] = "commitment control", ["ROLBK"] = "commitment control",
        ["IN"] = "data areas", ["OUT"] = "data areas", ["ALLOC"] = "storage management", ["DEALLOC"] = "storage management",
        ["REALLOC"] = "storage management", ["CALLB"] = "bound calls by name", ["CALL"] = "the fixed-form CALL", ["GOTO"] = "GOTO",
        ["TAG"] = "TAG", ["EVAL-CORR"] = "EVAL-CORR", ["XML-INTO"] = "XML-INTO", ["XML-SAX"] = "XML-SAX", ["DATA-INTO"] = "DATA-INTO",
        ["DATA-GEN"] = "DATA-GEN", ["ON-EXIT"] = "ON-EXIT", ["TEST"] = "TEST", ["SORTA"] = "SORTA", ["SND-MSG"] = "SND-MSG",
        ["FORCE"] = "FORCE", ["EXCEPT"] = "program-described output", ["POST"] = "POST", ["ACQ"] = "ACQ", ["REL"] = "REL", ["NEXT"] = "NEXT",
        ["DCL-ENUM"] = "DCL-ENUM", ["WHEN-IN"] = "WHEN-IN", ["WHEN-IS"] = "WHEN-IS", ["ON-EXCP"] = "ON-EXCP",
    };

    private static readonly HashSet<string> DisplayOps = ["EXFMT", "WRITE", "READ", "READC", "CHAIN", "UPDATE"];

    // ---- statements ----

    private void Report(Severity severity, string message, int? line = null) =>
        _program.Diagnostics.Add(new Diagnostic(line ?? _line, severity, message));

    /// <summary>Moves to the next statement and tokenizes it. False at the end of the member.</summary>
    private bool Advance(out SourceStatement statement)
    {
        statement = null!;
        if (_next >= _statements.Count) return false;
        statement = _statements[_next++];
        _line = statement.Line;
        _p = 0;
        _t = [];
        if (statement.Kind != StatementKind.Code) return true;
        try
        {
            _t = RpgLexer.Tokens(statement.Text);
        }
        catch (RpgUnsupported e)
        {
            Report(Severity.Unsupported, e.Message);
            _t = [new Token(TokenKind.Name, "*BAD", e.Message)];
        }
        return true;
    }

    /// <summary>
    /// Statements up to one that starts with one of <paramref name="ends"/>, which is left as the
    /// current statement, after its first word.
    /// </summary>
    private (List<Stmt> Body, string End) Block(params string[] ends)
    {
        var body = new List<Stmt>();
        var startLine = _line;
        while (Advance(out var statement))
        {
            if (statement.Kind == StatementKind.Code && _t.Count > 0 && _t[0].Kind == TokenKind.Name && ends.Contains(_t[0].Text))
            {
                _p = 1;
                return (body, _t[0].Text);
            }
            Stmt? parsed;
            try
            {
                parsed = statement.Kind switch
                {
                    StatementKind.Sql => Sql(statement),
                    StatementKind.Fixed => Fixed(statement),
                    StatementKind.Directive => Directive(statement),
                    _ => Statement(),
                };
            }
            catch (RpgUnsupported e)
            {
                Report(Severity.Unsupported, e.Message, statement.Line);
                parsed = new UnsupportedStmt(statement.Line, e.Message);
            }
            if (parsed is not null) body.Add(parsed);
        }
        if (ends.Length > 0) Report(Severity.Unsupported, $"{ends[^1]} is missing for the block that starts here", startLine);
        return (body, "");
    }

    private Stmt? Fixed(SourceStatement statement)
    {
        const string message = "fixed-form specifications aren't supported: only free-form RPG";
        Report(Severity.Unsupported, message);
        return char.ToUpperInvariant(statement.Text[5]) == 'C' ? new UnsupportedStmt(statement.Line, message) : null;
    }

    private Stmt? Directive(SourceStatement statement)
    {
        var name = statement.Text.Split(' ', 2)[0].ToUpperInvariant();
        if (name is "/FREE" or "/END-FREE" or "/TITLE" or "/EJECT" or "/SPACE") return null;
        Report(Severity.Unsupported, name.StartsWith("**")
            ? "compile-time data isn't supported"
            : $"the directive {name} isn't supported{(name is "/COPY" or "/INCLUDE" ? ": put the copied code in the member" : "")}");
        return null;
    }

    private Stmt? Sql(SourceStatement statement)
    {
        var translation = SqlTranslator.Translate(statement.Text);
        foreach (var note in translation.Notes) Report(Severity.Info, $"SQL: {note}");
        if (translation.Kind == SqlKind.Unsupported)
        {
            Report(Severity.Unsupported, $"SQL: {translation.Problem}");
        }
        else if (translation.Sql is { } sql && translation.Kind is not (SqlKind.Prepare or SqlKind.ExecuteImmediate) && _prepareProblem?.Invoke(sql) is { } problem)
        {
            if (problem.Contains("no such table") || problem.Contains("no such column"))
            {
                Report(Severity.Warning, $"SQL: {problem} (yet)");
            }
            else if (!problem.Contains("already exists") && !problem.Contains("values were supplied"))
            {
                Report(Severity.Unsupported, $"SQL: SQLite doesn't take this statement, even translated: {problem}");
                translation = new SqlTranslation(SqlKind.Unsupported, Problem: problem);
            }
        }
        if (translation.Kind == SqlKind.Select) Report(Severity.Warning, "SQL: a SELECT with no INTO returns nothing to the program");
        if (translation.Kind != SqlKind.DeclareCursor) return new SqlStmt(statement.Line, translation);
        _program.Cursors[translation.Cursor!] = translation;
        return null;
    }

    // ---- token helpers ----

    private Token? Peek => _p < _t.Count ? _t[_p] : null;
    private bool AtEnd => _p >= _t.Count;
    private bool PeekOp(string op) => Peek is { } t && t.IsOp(op);
    private bool PeekName(string name) => Peek is { } t && t.IsName(name);

    private bool TakeOp(string op)
    {
        if (!PeekOp(op)) return false;
        _p++;
        return true;
    }

    private bool TakeName(string name)
    {
        if (!PeekName(name)) return false;
        _p++;
        return true;
    }

    private void ExpectOp(string op)
    {
        if (!TakeOp(op)) throw new RpgUnsupported($"{op} is expected{(Peek is { } t ? $" before {Show(t)}" : " at the end of the statement")}");
    }

    private string ExpectName(string what)
    {
        if (Peek is { Kind: TokenKind.Name } t)
        {
            _p++;
            return t.Text;
        }
        throw new RpgUnsupported($"{what} is expected{(Peek is { } other ? $", not {Show(other)}" : "")}");
    }

    private void ExpectEnd()
    {
        if (!AtEnd) throw new RpgUnsupported($"{Show(Peek!)} isn't expected here");
    }

    private static string Show(Token t) => t.Kind == TokenKind.String ? $"'{t.Value}'" : t.Text;

    /// <summary>An operation extender, such as (E) or (H): its letters, or "" when there's none.</summary>
    private string Extender()
    {
        if (_p + 2 < _t.Count && _t[_p].IsOp("(") && _t[_p + 1].Kind == TokenKind.Name && _t[_p + 2].IsOp(")")
            && _t[_p + 1].Text.All(c => "HENRTMADPZ".Contains(c)) && _t[_p + 1].Text.Length <= 3)
        {
            var letters = _t[_p + 1].Text;
            _p += 3;
            return letters;
        }
        return "";
    }

    /// <summary>A condition or other expression of a block's first statement: when it can't be parsed, the block still is.</summary>
    private Expr Header(Func<Expr> parse)
    {
        try
        {
            var e = parse();
            ExpectEnd();
            return e;
        }
        catch (RpgUnsupported e)
        {
            Report(Severity.Unsupported, e.Message);
            return new UnsupportedExpr(e.Message);
        }
    }

    // ---- one statement ----

    private Stmt? Statement()
    {
        var line = _line;
        var first = _t[0];
        if (first.Kind != TokenKind.Name) return AssignOrCall(line, false, false);
        if (first.Text == "*BAD") return new UnsupportedStmt(line, (string)first.Value!);
        _p = 1;
        switch (first.Text)
        {
            case "CTL-OPT":
                ControlOptions();
                return null;
            case "DCL-S":
            {
                var name = ExpectName("a name");
                var k = Keywords();
                if (k.Type.Keyword is null && k.Type.Like is null) throw new RpgUnsupported($"{name} needs a type");
                _scope.Locals.Add(new VarDecl(line, name, k.Type, k.Inz));
                return null;
            }
            case "DCL-C":
            {
                var name = ExpectName("a name");
                var value = TakeName("CONST") ? Parenthesized() : Expression();
                ExpectEnd();
                _scope.Locals.Add(new ConstDecl(line, name, value));
                return null;
            }
            case "DCL-DS":
                DataStructure(line);
                return null;
            case "DCL-F":
                File(line);
                return null;
            case "DCL-PR":
                Prototype();
                return null;
            case "DCL-PI":
                Interface();
                return null;
            case "DCL-PROC":
            {
                var name = ExpectName("a procedure name");
                var outer = _scope;
                _scope = new Procedure(name);
                _program.Procedures[name] = _scope;
                _scope.Body = Block("END-PROC").Body;
                _scope = outer;
                return null;
            }
            case "BEGSR":
            {
                var name = ExpectName("a subroutine name");
                _scope.Subroutines[name] = Block("ENDSR").Body;
                return null;
            }
            case "IF":
            {
                var branches = new List<(Expr, IReadOnlyList<Stmt>)>();
                List<Stmt>? otherwise = null;
                var condition = Header(Expression);
                while (true)
                {
                    var (body, end) = Block("ELSEIF", "ELSE", "ENDIF");
                    branches.Add((condition, body));
                    if (end == "ELSEIF")
                    {
                        condition = Header(Expression);
                        continue;
                    }
                    if (end == "ELSE") otherwise = Block("ENDIF").Body;
                    break;
                }
                return new IfStmt(line, branches, otherwise);
            }
            case "DOW" or "DOU":
            {
                var condition = Header(Expression);
                return new LoopStmt(line, condition, first.Text == "DOU", Block("ENDDO").Body);
            }
            case "FOR":
            {
                Expr variable = new UnsupportedExpr("");
                Expr? start = null, limit = null, by = null;
                var down = false;
                Header(() =>
                {
                    variable = Postfix();
                    if (TakeOp("=")) start = Expression();
                    while (!AtEnd)
                    {
                        if (TakeName("BY")) by = Expression();
                        else if (TakeName("TO")) limit = Expression();
                        else if (TakeName("DOWNTO"))
                        {
                            limit = Expression();
                            down = true;
                        }
                        else break;
                    }
                    return variable;
                });
                return new ForStmt(line, variable, start, limit, by, down, Block("ENDFOR").Body);
            }
            case "FOR-EACH":
            {
                Expr variable = new UnsupportedExpr(""), array = new UnsupportedExpr("");
                Header(() =>
                {
                    variable = Postfix();
                    if (!TakeName("IN")) throw new RpgUnsupported("FOR-EACH needs IN and an array");
                    return array = Expression();
                });
                return new ForEachStmt(line, variable, array, Block("ENDFOR").Body);
            }
            case "SELECT":
            {
                if (!AtEnd) Report(Severity.Unsupported, "SELECT with an operand isn't supported: use SELECT and WHEN conditions");
                var whens = new List<(Expr, IReadOnlyList<Stmt>)>();
                List<Stmt>? other = null;
                var end = Block("WHEN", "OTHER", "ENDSL").End;
                while (end == "WHEN")
                {
                    var condition = Header(Expression);
                    (var body, end) = Block("WHEN", "OTHER", "ENDSL");
                    whens.Add((condition, body));
                }
                if (end == "OTHER") other = Block("ENDSL").Body;
                return new SelectStmt(line, whens, other);
            }
            case "MONITOR":
            {
                var handlers = new List<(IReadOnlyList<int>, IReadOnlyList<Stmt>)>();
                var (body, end) = Block("ON-ERROR", "ENDMON");
                while (end == "ON-ERROR")
                {
                    var statuses = new List<int>();
                    while (!AtEnd)
                    {
                        var t = _t[_p++];
                        if (t.Kind == TokenKind.Number) statuses.Add((int)(decimal)t.Value!);
                        // *PROGRAM, *FILE and *ALL: any status.
                        else if (t.Kind == TokenKind.Special) statuses.Clear();
                    }
                    (var handler, end) = Block("ON-ERROR", "ENDMON");
                    handlers.Add((statuses, handler));
                }
                return new MonitorStmt(line, body, handlers);
            }
            case "LEAVE":
                ExpectEnd();
                return new JumpStmt(line, Signal.Leave);
            case "ITER":
                ExpectEnd();
                return new JumpStmt(line, Signal.Iter);
            case "LEAVESR":
                ExpectEnd();
                return new JumpStmt(line, Signal.LeaveSr);
            case "RETURN":
            {
                Extender();
                var value = AtEnd ? null : Expression();
                ExpectEnd();
                return new JumpStmt(line, Signal.Return, value);
            }
            case "EXSR":
            {
                var name = ExpectName("a subroutine name");
                ExpectEnd();
                return new ExsrStmt(line, name);
            }
            case "CALLP":
            {
                Extender();
                var call = Postfix();
                ExpectEnd();
                return new CallStmt(line, call is NameExpr ? new CallExpr(call, []) : call);
            }
            case "EVAL" or "EVALR":
            {
                var extender = Extender();
                return AssignOrCall(line, extender.Contains('H'), first.Text == "EVALR" || extender.Contains('R'));
            }
            case "DSPLY":
            {
                Extender();
                var message = AtEnd ? null : Expression();
                Expr? response = null;
                if (!AtEnd)
                {
                    // The second operand is a message queue, which there isn't; the third takes the reply.
                    var second = Expression();
                    response = AtEnd ? null : Postfix();
                    if (response is null && second is not Literal) response = second;
                }
                ExpectEnd();
                return new DsplyStmt(line, message, response);
            }
            case "CLEAR" or "RESET":
            {
                while (Peek is { Kind: TokenKind.Special }) _p++;
                var target = Postfix();
                ExpectEnd();
                return new ClearStmt(line, target);
            }
            case "DUMP":
                return null;
            case "ENDIF" or "ENDDO" or "ENDFOR" or "ENDSL" or "ENDMON" or "ENDSR" or "END-PROC" or "ELSE" or "ELSEIF" or "WHEN" or "OTHER"
                or "ON-ERROR" or "END-DS" or "END-PI" or "END-PR":
                throw new RpgUnsupported($"{first.Text} has no block to end");
        }
        if (DisplayOps.Contains(first.Text) && !PeekOp("=") && !PeekOp("."))
        {
            var extender = Extender();
            // WRITE to a printer file: the line to print follows the file's name.
            if (first.Text == "WRITE" && Peek is { Kind: TokenKind.Name } printer && _printers.Contains(printer.Text))
            {
                _p++;
                var data = AtEnd ? new Literal("") : Expression();
                ExpectEnd();
                return new PrintStmt(line, printer.Text, data);
            }
            Expr? key = null;
            if (first.Text == "CHAIN")
            {
                key = Expression();
            }
            var record = ExpectName("a record format name");
            if (!AtEnd) throw new RpgUnsupported($"{first.Text} with a data structure isn't supported");
            if (_program.Files.Count == 0 || _otherFiles.Contains(record))
                throw new RpgUnsupported($"native file I/O isn't supported ({first.Text} {record}): use embedded SQL");
            return new DisplayStmt(line, first.Text, record, key, extender.Contains('E'));
        }
        // A name that is an operation code, not a variable being assigned or a procedure called.
        if (UnsupportedOps.TryGetValue(first.Text, out var what) && !PeekOp("=") && !PeekOp("(") && !PeekOp("."))
            throw new RpgUnsupported($"{what} isn't supported ({first.Text})");
        _p = 0;
        return AssignOrCall(line, false, false);
    }

    private Stmt AssignOrCall(int line, bool halfAdjust, bool right)
    {
        var target = Postfix();
        if (AtEnd)
        {
            return target switch
            {
                NameExpr => new CallStmt(line, new CallExpr(target, [])),
                CallExpr => new CallStmt(line, target),
                _ => throw new RpgUnsupported("this isn't a statement"),
            };
        }
        var op = _t[_p];
        if (op.Kind == TokenKind.Op && op.Text is "=" or "+=" or "-=" or "*=" or "/=")
        {
            _p++;
            var value = Expression();
            ExpectEnd();
            return new AssignStmt(line, target, op.Text, value, halfAdjust, right);
        }
        throw new RpgUnsupported(target is NameExpr n && _p == 1
            ? $"the operation {n.Name} isn't supported"
            : $"{Show(op)} isn't expected here");
    }

    // ---- declarations ----

    private void ControlOptions()
    {
        while (!AtEnd)
        {
            var keyword = ExpectName("a keyword");
            var args = PeekOp("(") ? Arguments() : [];
            if (keyword == "MAIN" && args is [NameExpr main]) _program.MainProcedure = main.Name;
        }
    }

    private sealed class KeywordSet
    {
        public TypeSpec Type { get; } = new();
        public Expr? Inz { get; set; }
        public int Position { get; set; }
        public bool Const { get; set; }
        public bool ByValue { get; set; }
        public bool HasProgram { get; set; }
        public string? Program { get; set; }
        public bool Closed { get; set; }
    }

    private List<Expr> Arguments()
    {
        ExpectOp("(");
        var args = new List<Expr>();
        if (TakeOp(")")) return args;
        do
        {
            args.Add(Expression());
        }
        while (TakeOp(":"));
        ExpectOp(")");
        return args;
    }

    private Expr Parenthesized()
    {
        ExpectOp("(");
        var e = Expression();
        ExpectOp(")");
        return e;
    }

    private static string NameOf(Expr e, string keyword) => e switch
    {
        NameExpr n => n.Name,
        Literal { Value: string s } => s.Trim().ToUpperInvariant(),
        _ => throw new RpgUnsupported($"{keyword} needs a name"),
    };

    /// <summary>The type and other keywords of a declaration, to the end of the statement (or END-DS, END-PI, END-PR).</summary>
    private KeywordSet Keywords()
    {
        var k = new KeywordSet();
        while (!AtEnd)
        {
            var keyword = ExpectName("a keyword");
            if (keyword is "END-DS" or "END-PI" or "END-PR")
            {
                k.Closed = true;
                if (Peek is { Kind: TokenKind.Name }) _p++;
                ExpectEnd();
                break;
            }
            var args = PeekOp("(") ? Arguments() : null;
            switch (keyword)
            {
                case "CHAR" or "VARCHAR" or "PACKED" or "ZONED" or "BINDEC" or "INT" or "UNS" or "FLOAT":
                    if (args is null or []) throw new RpgUnsupported($"{keyword} needs a length");
                    k.Type.Keyword = keyword;
                    k.Type.Length = args[0];
                    k.Type.Decimals = args.Count > 1 && keyword is "PACKED" or "ZONED" or "BINDEC" ? args[1] : null;
                    break;
                case "IND" or "DATE" or "TIME" or "TIMESTAMP":
                    k.Type.Keyword = keyword;
                    break;
                case "POINTER" or "OBJECT" or "UCS2" or "VARUCS2" or "GRAPH" or "VARGRAPH":
                    throw new RpgUnsupported($"the type {keyword} isn't supported");
                case "LIKE":
                    if (args is not [NameExpr like]) throw new RpgUnsupported("LIKE with a length adjustment isn't supported");
                    k.Type.Like = like.Name;
                    break;
                case "LIKEDS":
                    k.Type.LikeDs = NameOf(args is [var ds] ? ds : throw new RpgUnsupported("LIKEDS needs a data structure name"), keyword);
                    k.Type.Keyword = "DS";
                    break;
                case "EXTNAME":
                    if (args is not [var table]) throw new RpgUnsupported("EXTNAME with a record format or field type isn't supported");
                    k.Type.ExtName = NameOf(table, keyword);
                    k.Type.ExtName = k.Type.ExtName[(k.Type.ExtName.LastIndexOf('/') + 1)..];
                    break;
                case "DIM":
                    k.Type.Dim = args is [var dim] ? dim : throw new RpgUnsupported("DIM needs one number");
                    break;
                case "QUALIFIED":
                    k.Type.Qualified = true;
                    break;
                case "VARYING":
                    k.Type.Keyword = "VARCHAR";
                    break;
                case "INZ":
                    k.Inz = args is [var inz and not SpecialExpr { Name: "*LIKEDS" }] ? inz : null;
                    break;
                case "POS":
                    k.Position = args is [Literal { Value: decimal position }] ? (int)position : throw new RpgUnsupported("POS needs a number");
                    break;
                case "CONST":
                    k.Const = true;
                    break;
                case "VALUE":
                    k.ByValue = true;
                    break;
                case "EXTPGM":
                    k.HasProgram = true;
                    k.Program = args is [var program] ? NameOf(program, keyword) : null;
                    break;
                case "STATIC" or "TEMPLATE" or "EXPORT" or "OPTIONS" or "CCSID" or "ASCEND" or "DESCEND" or "OPDESC" or "RTNPARM" or "NOOPT"
                    or "ALIGN" or "DATFMT" or "TIMFMT" or "REQPROTO" or "DCL-PARM" or "DCL-SUBF":
                    break;
                default:
                    throw new RpgUnsupported($"the keyword {keyword} isn't supported");
            }
        }
        return k;
    }

    /// <summary>The subfields or parameters that follow, up to <paramref name="end"/>.</summary>
    private List<VarDecl> Entries(string end)
    {
        var entries = new List<VarDecl>();
        while (true)
        {
            if (!Advance(out var statement))
            {
                Report(Severity.Unsupported, $"{end} is missing");
                break;
            }
            if (statement.Kind != StatementKind.Code)
            {
                Report(Severity.Unsupported, $"only subfields or parameters are expected before {end}");
                continue;
            }
            if (_t[0].IsName(end)) break;
            try
            {
                if (_t[0].IsName("DCL-PARM") || _t[0].IsName("DCL-SUBF")) _p = 1;
                var name = Peek is { Kind: TokenKind.Special, Text: "*N" } ? "" : ExpectName("a name");
                if (name.Length == 0) _p++;
                var line = _line;
                var k = Keywords();
                if (k.Type.Keyword is null && k.Type.Like is null) throw new RpgUnsupported($"{name} needs a type");
                entries.Add(new VarDecl(line, name, k.Type, k.Inz, k.Position, k.Const, k.ByValue));
            }
            catch (RpgUnsupported e)
            {
                Report(Severity.Unsupported, e.Message);
            }
        }
        return entries;
    }

    /// <summary>The name after DCL-DS, DCL-PI or DCL-PR: a name, or *N for none.</summary>
    private string DeclaredName()
    {
        if (Peek is { Kind: TokenKind.Special, Text: "*N" })
        {
            _p++;
            return "";
        }
        return ExpectName("a name");
    }

    private void DataStructure(int line)
    {
        var name = DeclaredName();
        var k = Keywords();
        k.Type.Keyword = "DS";
        if (k.Type.LikeDs is null && !k.Closed) k.Type.Fields = Entries("END-DS");
        if (k.Type.LikeDs is null && k.Type.ExtName is null && k.Type.Fields is null or []) Report(Severity.Warning, $"data structure {name} has no subfields", line);
        _scope.Locals.Add(new VarDecl(line, name, k.Type, k.Inz));
    }

    private void Interface()
    {
        DeclaredName();
        var k = Keywords();
        if (k.Type.Keyword is not null || k.Type.Like is not null) _scope.Returns = k.Type;
        _scope.Params = k.Closed ? [] : Entries("END-PI");
    }

    private void Prototype()
    {
        var name = DeclaredName();
        var k = Keywords();
        var parameters = k.Closed ? [] : Entries("END-PR");
        _program.Prototypes[name] = new ProtoDecl(name, k.HasProgram ? k.Program ?? name : null, parameters);
    }

    private void File(int line)
    {
        var name = ExpectName("a file name");
        var workstation = false;
        var printerWidth = 0;
        string? indicators = null;
        var subfiles = new Dictionary<string, string>();
        while (!AtEnd)
        {
            var keyword = ExpectName("a keyword");
            var args = PeekOp("(") ? Arguments() : [];
            switch (keyword)
            {
                case "WORKSTN":
                    workstation = true;
                    break;
                case "INDDS":
                    indicators = NameOf(args is [var ds] ? ds : throw new RpgUnsupported("INDDS needs a data structure name"), keyword);
                    break;
                case "SFILE":
                    if (args is not [NameExpr record, NameExpr number]) throw new RpgUnsupported("SFILE needs a record format and a field");
                    subfiles[record.Name] = number.Name;
                    break;
                case "PRINTER":
                    printerWidth = args is [Literal { Value: decimal width }] && width is >= 1 and <= 378 ? (int)width : 132;
                    _printers.Add(name);
                    break;
                case "DISK" or "SEQ" or "SPECIAL":
                    _otherFiles.Add(name);
                    throw new RpgUnsupported(keyword == "DISK"
                        ? $"native file I/O isn't supported (file {name}): use embedded SQL"
                        : $"{keyword} files aren't supported (file {name})");
                case "USAGE" or "USROPN" or "EXTFILE" or "EXTDESC" or "ALIAS" or "QUALIFIED" or "INFDS" or "INFSR" or "MAXDEV" or "IGNORE"
                    or "INCLUDE" or "PREFIX" or "RENAME" or "KEYED" or "COMMIT" or "BLOCK" or "STATIC" or "EXTMBR" or "HANDLER":
                    if (keyword is "PREFIX" or "RENAME" or "QUALIFIED" or "HANDLER" or "USROPN")
                        Report(Severity.Unsupported, $"the file keyword {keyword} isn't supported", line);
                    break;
                default:
                    Report(Severity.Unsupported, $"the file keyword {keyword} isn't supported", line);
                    break;
            }
        }
        if (!workstation && printerWidth == 0)
        {
            // With no device, a file is a database file.
            _otherFiles.Add(name);
            throw new RpgUnsupported($"native file I/O isn't supported (file {name}): use embedded SQL");
        }
        _program.Files.Add(new FileDecl(line, name, indicators, subfiles, printerWidth));
    }

    // ---- expressions ----

    private Expr Expression() => Or();

    private Expr Or()
    {
        var left = And();
        while (TakeName("OR")) left = new BinaryExpr("OR", left, And());
        return left;
    }

    private Expr And()
    {
        var left = Not();
        while (TakeName("AND")) left = new BinaryExpr("AND", left, Not());
        return left;
    }

    private Expr Not() => TakeName("NOT") ? new UnaryExpr("NOT", Not()) : Comparison();

    private Expr Comparison()
    {
        var left = Additive();
        if (Peek is { Kind: TokenKind.Op } op && op.Text is "=" or "<>" or "<" or ">" or "<=" or ">=")
        {
            _p++;
            return new BinaryExpr(op.Text, left, Additive());
        }
        if (PeekName("IN")) throw new RpgUnsupported("the IN operator isn't supported");
        return left;
    }

    private Expr Additive()
    {
        var left = Multiplicative();
        while (Peek is { Kind: TokenKind.Op } op && op.Text is "+" or "-")
        {
            _p++;
            left = new BinaryExpr(op.Text, left, Multiplicative());
        }
        return left;
    }

    private Expr Multiplicative()
    {
        var left = Unary();
        while (Peek is { Kind: TokenKind.Op } op && op.Text is "*" or "/")
        {
            _p++;
            left = new BinaryExpr(op.Text, left, Unary());
        }
        return left;
    }

    private Expr Unary()
    {
        if (TakeOp("-")) return new UnaryExpr("-", Unary());
        if (TakeOp("+")) return Unary();
        var operand = Postfix();
        return TakeOp("**") ? new BinaryExpr("**", operand, Unary()) : operand;
    }

    private Expr Postfix()
    {
        var e = Primary();
        while (true)
        {
            if (PeekOp("(") && e is NameExpr or MemberExpr or SpecialExpr { Name: "*IN" })
            {
                e = new CallExpr(e, Arguments());
            }
            else if (PeekOp(".") && e is NameExpr or MemberExpr or CallExpr)
            {
                _p++;
                e = new MemberExpr(e, ExpectName("a subfield name"));
            }
            else
            {
                return e;
            }
        }
    }

    private Expr Primary()
    {
        if (Peek is not { } t) throw new RpgUnsupported("the statement ends where a value is expected");
        _p++;
        switch (t.Kind)
        {
            case TokenKind.Number or TokenKind.String:
                return new Literal(t.Value!);
            case TokenKind.Name:
                return new NameExpr(t.Text);
            case TokenKind.Special:
                if (t.Text == "*ALL" && Peek is { Kind: TokenKind.String } all)
                {
                    _p++;
                    return new SpecialExpr("*ALL", (string)all.Value!);
                }
                if (t.Text is "*NULL" or "*OMIT") throw new RpgUnsupported($"{t.Text} isn't supported");
                return new SpecialExpr(t.Text);
            case TokenKind.Bif:
                if (!Bifs.Contains(t.Text)) throw new RpgUnsupported($"the built-in function {t.Text} isn't supported");
                return new BifExpr(t.Text, PeekOp("(") ? Arguments() : []);
            default:
                if (t.IsOp("("))
                {
                    var e = Expression();
                    ExpectOp(")");
                    return e;
                }
                throw new RpgUnsupported($"{Show(t)} isn't expected here");
        }
    }
}
