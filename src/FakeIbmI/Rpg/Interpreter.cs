// Runs a parsed RPG program by walking its statements. One Interpreter is one call of one
// program: nothing is kept between calls, as if every program ended with *INLR on.

namespace FakeIbmI.Rpg;

/// <summary>What a running program needs from the job it runs in.</summary>
internal interface IJobHost
{
    /// <summary>The job's user profile.</summary>
    string User { get; }

    /// <summary>Cancelled when the job ends, such as when its 5250 session closes.</summary>
    CancellationToken Cancelled { get; }

    /// <summary>The job's display, or null in a job with none.</summary>
    IWorkstation? Workstation { get; }

    /// <summary>
    /// Shows DSPLY's message. When <paramref name="replyLength"/> is above 0 it waits for a reply of up to that
    /// many characters, and returns it; a job with nobody to answer returns null.
    /// </summary>
    string? Dsply(string message, int replyLength);
}

/// <summary>A 5250 display: send a record, or send one and wait for what the user does.</summary>
internal interface IWorkstation
{
    void Send(byte[] record);

    ReceivedInput Exchange(byte[] record);
}

/// <summary>The program ended in error, or reached code the fake doesn't support. The message is for whoever called it.</summary>
internal sealed class RpgHalt(string message) : Exception(message);

internal sealed partial class Interpreter
{
    private const long StatementLimit = 200_000_000;
    private const int DepthLimit = 400;

    private sealed class Frame(Procedure procedure, Dictionary<string, Cell> variables)
    {
        public Procedure Procedure { get; } = procedure;
        public Dictionary<string, Cell> Variables { get; } = variables;
        public int Parameters { get; set; }
        public object? Result { get; set; }
    }

    private readonly RpgProgram _program;
    private readonly string _name;
    private readonly Machine _machine;
    private readonly IJobHost _host;
    private readonly int _depth;
    private readonly Dictionary<string, Cell> _globals = [];
    private readonly Cell[] _indicators = [.. Enumerable.Range(0, 100).Select(_ => new Cell(RpgType.Indicator))];
    private readonly Cell _lastRecord = new(RpgType.Indicator);
    private readonly Frame _main;
    private readonly List<Dds.DisplaySession> _displays = [];
    private long _statements;
    private int _line;
    private int _calls;
    private bool _eof;
    private bool _found;
    private bool _error;
    private int _status;

    public Interpreter(RpgProgram program, string name, Machine machine, IJobHost host, int depth = 0)
    {
        _program = program;
        _name = name;
        _machine = machine;
        _host = host;
        _depth = depth;
        _main = new Frame(program.Main, _globals);
    }

    /// <summary>
    /// Calls the program. Each argument is copied into its parameter and, unless the parameter is
    /// CONST or by value, copied back when the program returns. Throws <see cref="RpgHalt"/> when it ends in error.
    /// </summary>
    public void Run(IReadOnlyList<Cell> arguments)
    {
        if (_depth > 50) throw new RpgHalt($"{_name}: programs are calling each other more than 50 deep");
        try
        {
            DeclareSql();
            foreach (var file in _program.Files.Where(f => f.Printer == 0)) OpenDisplay(file);
            foreach (var declaration in _program.Main.Locals) Declare(declaration, _main);
            var entry = _main;
            if (_program.MainProcedure is { } mainName)
            {
                if (!_program.Procedures.TryGetValue(mainName, out var procedure)) throw new RpgHalt($"{_name}: the main procedure {mainName} isn't in the program");
                entry = new Frame(procedure, []);
            }
            var parameters = entry.Procedure.Params;
            if (arguments.Count > parameters.Count)
                throw new RpgHalt($"{_name} takes {parameters.Count} parameter{(parameters.Count == 1 ? "" : "s")}, not {arguments.Count}");
            var cells = new List<Cell>();
            for (var i = 0; i < arguments.Count; i++)
            {
                var cell = new Cell(Resolve(parameters[i].Type, entry));
                cell.Set(arguments[i].Get());
                if (parameters[i].Name.Length > 0) entry.Variables[parameters[i].Name] = cell;
                cells.Add(cell);
            }
            entry.Parameters = arguments.Count;
            if (entry != _main) foreach (var declaration in entry.Procedure.Locals) Declare(declaration, entry);
            Execute(entry.Procedure.Body, entry);
            for (var i = 0; i < arguments.Count; i++)
            {
                if (!parameters[i].Const && !parameters[i].ByValue) arguments[i].Set(cells[i].Get());
            }
        }
        catch (RpgError e)
        {
            throw new RpgHalt($"{_name} ended in error at line {_line}: {e.Message}{(e.Status > 0 ? $" (status {e.Status:00000})" : "")}");
        }
        catch (OverflowException)
        {
            throw new RpgHalt($"{_name} ended in error at line {_line}: a number is too large");
        }
        catch (InvalidCastException)
        {
            throw new RpgHalt($"{_name} ended in error at line {_line}: a value isn't of the type the operation needs");
        }
        finally
        {
            CloseSql();
            FinishSpool();
        }
    }

    private readonly Dictionary<string, List<string>> _spool = [];

    /// <summary>Whatever the program printed becomes one spooled file for each printer file, named for the file.</summary>
    private void FinishSpool()
    {
        foreach (var (file, lines) in _spool)
        {
            _machine.Store.AddSpool(file, _host.User, _program.Files.First(f => f.Name == file).Printer, lines);
        }
        _spool.Clear();
    }

    private void Print(PrintStmt print, Frame frame)
    {
        var file = _program.Files.FirstOrDefault(f => f.Name == print.File && f.Printer > 0)
                   ?? throw new RpgError(1211, $"{print.File} isn't a printer file in this program");
        var text = Values.ToText(Evaluate(print.Data, frame)).TrimEnd();
        if (!_spool.TryGetValue(file.Name, out var lines)) _spool[file.Name] = lines = [];
        lines.Add(text.Length > file.Printer ? text[..file.Printer] : text);
    }

    /// <summary>The types of the program's parameters, for a caller that has to build them from bytes.</summary>
    public IReadOnlyList<RpgType> ParameterTypes()
    {
        try
        {
            var frame = new Frame(_program.Main, new Dictionary<string, Cell>(_globals));
            foreach (var declaration in _program.Main.Locals.OfType<ConstDecl>()) Declare(declaration, frame);
            var procedure = _program.MainProcedure is { } main && _program.Procedures.TryGetValue(main, out var p) ? p : _program.Main;
            return [.. procedure.Params.Select(parameter => Resolve(parameter.Type, frame))];
        }
        catch (RpgError e)
        {
            throw new RpgHalt($"{_name}: {e.Message}");
        }
    }

    // ---- declarations ----

    private Cell? Find(string name, Frame frame) =>
        frame.Variables.TryGetValue(name, out var cell) || _globals.TryGetValue(name, out cell) ? cell : null;

    private void Declare(Decl declaration, Frame frame)
    {
        _line = declaration.Line;
        switch (declaration)
        {
            case ConstDecl constant:
            {
                var value = Evaluate(constant.Value, frame);
                var type = value switch
                {
                    string s => new RpgType(RpgKind.Varchar, Math.Max(s.Length, 1)),
                    decimal d => RpgType.Number(28, Math.Min((int)d.Scale, 27)),
                    _ => throw new RpgError(100, $"the constant {constant.Name} has to be a number or a literal"),
                };
                var cell = new Cell(type);
                cell.Set(value);
                frame.Variables[constant.Name] = cell;
                break;
            }
            case VarDecl variable:
            {
                var cell = new Cell(Resolve(variable.Type, frame));
                Initialize(cell, variable.Inz, frame);
                if (variable.Name.Length > 0) frame.Variables[variable.Name] = cell;
                // An unqualified data structure's subfields are variables in their own right.
                if (cell.Type is { Kind: RpgKind.Ds, Dim: 0, Ds.Qualified: false })
                {
                    for (var i = 0; i < cell.Ds.Cells.Count; i++)
                    {
                        if (cell.Ds.Layout.Fields[i].Name.Length > 0) frame.Variables[cell.Ds.Layout.Fields[i].Name] = cell.Ds.Cells[i];
                    }
                }
                break;
            }
        }
    }

    private void Initialize(Cell cell, Expr? inz, Frame frame)
    {
        if (cell.Elements is not null)
        {
            foreach (var element in cell.Elements) Initialize(element, inz, frame);
            return;
        }
        if (cell.Type.Kind == RpgKind.Ds)
        {
            for (var i = 0; i < cell.Ds.Cells.Count; i++) Initialize(cell.Ds.Cells[i], cell.Ds.Layout.Fields[i].Inz, frame);
            return;
        }
        if (inz is not null) cell.Set(Evaluate(inz, frame));
    }

    private int Number(Expr? e, Frame frame, int fallback = 0) => e is null ? fallback : (int)Values.ToDecimal(Evaluate(e, frame));

    private RpgType Resolve(TypeSpec spec, Frame frame)
    {
        var dim = Number(spec.Dim, frame);
        if (spec.Dim is not null && dim is < 1 or > 1_000_000) throw new RpgError(100, "DIM has to be between 1 and 1000000");
        if ((spec.Like ?? spec.LikeDs) is { } like)
        {
            var source = Find(like, frame) ?? throw new RpgError(100, $"{like} isn't defined, for LIKE or LIKEDS");
            var type = source.Type with { Dim = dim };
            return spec.LikeDs is not null && type.Ds is { } layout ? type with { Ds = layout with { Qualified = true } } : type;
        }
        switch (spec.Keyword)
        {
            case "CHAR":
                return new RpgType(RpgKind.Char, Number(spec.Length, frame), Dim: dim);
            case "VARCHAR":
                return new RpgType(RpgKind.Varchar, Number(spec.Length, frame), Dim: dim);
            case "PACKED" or "ZONED" or "BINDEC":
            {
                var (digits, decimals) = (Number(spec.Length, frame), Number(spec.Decimals, frame));
                if (digits is < 1 or > 63 || decimals < 0 || decimals > digits) throw new RpgError(100, $"{spec.Keyword}({digits}:{decimals}) isn't a valid type");
                if (digits > 28) throw new RpgError(100, "numbers of more than 28 digits aren't supported");
                return new RpgType(RpgKind.Decimal, digits, decimals, Dim: dim);
            }
            case "INT" or "UNS":
                return new RpgType(RpgKind.Int, Number(spec.Length, frame), Unsigned: spec.Keyword == "UNS", Dim: dim);
            case "FLOAT":
                return new RpgType(RpgKind.Float, Number(spec.Length, frame), Dim: dim);
            case "IND":
                return RpgType.Indicator with { Dim = dim };
            case "DATE":
                return new RpgType(RpgKind.Date, Dim: dim);
            case "TIME":
                return new RpgType(RpgKind.Time, Dim: dim);
            case "TIMESTAMP":
                return new RpgType(RpgKind.Timestamp, Dim: dim);
            case "DS":
            {
                var fields = new List<DsField>();
                if (spec.ExtName is { } table)
                {
                    // EXTNAME('LIB/TABLE') or ('LIB.TABLE') names the library too.
                    var named = table.ToUpperInvariant().Split('/', '.');
                    var columns = named.Length == 2 ? _machine.Store.Columns(named[1], named[0]) : _machine.Store.Columns(table);
                    if (columns.Count == 0) throw new RpgError(100, $"EXTNAME({table}): there's no such table");
                    fields.AddRange(columns.Select(c => new DsField(c.Name.ToUpperInvariant(), SqlType(c.Type), null, 0)));
                }
                foreach (var field in spec.Fields ?? []) fields.Add(new DsField(field.Name, Resolve(field.Type, frame), field.Inz, field.Position));
                return new RpgType(RpgKind.Ds, Ds: new DsLayout(fields, spec.Qualified), Dim: dim);
            }
            default:
                throw new RpgError(100, "a declaration has no type");
        }
    }

    /// <summary>The RPG type for a column, from the type its table was created with.</summary>
    private static RpgType SqlType(string declared)
    {
        var text = declared.ToUpperInvariant();
        var open = text.IndexOf('(');
        var name = (open < 0 ? text : text[..open]).Trim();
        var numbers = open < 0 ? [] : text[(open + 1)..].TrimEnd(')', ' ').Split(',').Select(n => int.TryParse(n.Trim(), out var v) ? v : 0).ToArray();
        int Arg(int i, int fallback) => i < numbers.Length && numbers[i] > 0 ? numbers[i] : fallback;
        return name switch
        {
            "CHAR" or "CHARACTER" or "NCHAR" or "GRAPHIC" => RpgType.Char(Arg(0, 1)),
            "VARCHAR" or "NVARCHAR" or "VARGRAPHIC" or "CLOB" or "TEXT" => new RpgType(RpgKind.Varchar, Arg(0, 256)),
            "DECIMAL" or "DEC" or "NUMERIC" or "NUM" => RpgType.Number(Math.Min(Arg(0, 5), 28), numbers.Length > 1 ? numbers[1] : 0),
            "SMALLINT" => new RpgType(RpgKind.Int, 5),
            "INTEGER" or "INT" => new RpgType(RpgKind.Int, 10),
            "BIGINT" => new RpgType(RpgKind.Int, 20),
            "REAL" or "DOUBLE" or "FLOAT" => new RpgType(RpgKind.Float, 8),
            "DATE" => new RpgType(RpgKind.Date),
            "TIME" => new RpgType(RpgKind.Time),
            "TIMESTAMP" => new RpgType(RpgKind.Timestamp),
            _ => new RpgType(RpgKind.Varchar, 256),
        };
    }

    // ---- statements ----

    private Signal Execute(IReadOnlyList<Stmt> body, Frame frame)
    {
        foreach (var statement in body)
        {
            var signal = Execute(statement, frame);
            if (signal != Signal.None) return signal;
        }
        return Signal.None;
    }

    private Signal Execute(Stmt statement, Frame frame)
    {
        _line = statement.Line;
        if ((++_statements & 0x3ff) == 0)
        {
            _host.Cancelled.ThrowIfCancellationRequested();
            if (_statements > StatementLimit) throw new RpgHalt($"{_name} was stopped at line {_line}: it ran {StatementLimit:N0} statements without ending");
        }
        switch (statement)
        {
            case AssignStmt assign:
                Assign(assign, frame);
                return Signal.None;
            case IfStmt branch:
                foreach (var (condition, body) in branch.Branches)
                {
                    if (Values.ToBool(Evaluate(condition, frame))) return Execute(body, frame);
                }
                return branch.Else is null ? Signal.None : Execute(branch.Else, frame);
            case LoopStmt loop:
                while (loop.Until || Values.ToBool(Evaluate(loop.Condition, frame)))
                {
                    var signal = Execute(loop.Body, frame);
                    if (signal == Signal.Leave) break;
                    if (signal is Signal.Return or Signal.LeaveSr) return signal;
                    if (loop.Until && Values.ToBool(Evaluate(loop.Condition, frame))) break;
                    _line = loop.Line;
                    Tick();
                }
                return Signal.None;
            case ForStmt loop:
            {
                var variable = Locate(loop.Variable, frame);
                if (loop.Start is not null) variable.Set(Evaluate(loop.Start, frame));
                var by = loop.By is null ? 1m : Values.ToDecimal(Evaluate(loop.By, frame));
                while (true)
                {
                    if (loop.Limit is not null)
                    {
                        var compared = Values.Compare(variable.Get(), Evaluate(loop.Limit, frame));
                        if (loop.Down ? compared < 0 : compared > 0) break;
                    }
                    var signal = Execute(loop.Body, frame);
                    if (signal == Signal.Leave) break;
                    if (signal is Signal.Return or Signal.LeaveSr) return signal;
                    variable.Set(Values.ToDecimal(variable.Get()) + (loop.Down ? -by : by));
                    _line = loop.Line;
                    Tick();
                }
                return Signal.None;
            }
            case ForEachStmt loop:
            {
                var variable = Locate(loop.Variable, frame);
                if (Evaluate(loop.Array, frame) is not Cell { Elements: { } elements }) throw new RpgError(100, "FOR-EACH needs an array");
                foreach (var element in elements)
                {
                    variable.Set(element.Get());
                    var signal = Execute(loop.Body, frame);
                    if (signal == Signal.Leave) break;
                    if (signal is Signal.Return or Signal.LeaveSr) return signal;
                }
                return Signal.None;
            }
            case SelectStmt select:
                foreach (var (condition, body) in select.Whens)
                {
                    if (Values.ToBool(Evaluate(condition, frame))) return Execute(body, frame);
                }
                return select.Other is null ? Signal.None : Execute(select.Other, frame);
            case JumpStmt jump:
                if (jump.Value is not null) frame.Result = Evaluate(jump.Value, frame);
                return jump.Signal;
            case ExsrStmt exsr:
            {
                if (!frame.Procedure.Subroutines.TryGetValue(exsr.Name, out var body) && !_program.Main.Subroutines.TryGetValue(exsr.Name, out body))
                    throw new RpgError(100, $"the subroutine {exsr.Name} isn't defined");
                var signal = Execute(body, frame);
                return signal == Signal.Return ? signal : Signal.None;
            }
            case CallStmt call:
                Call((CallExpr)call.Call, frame, statement: true);
                return Signal.None;
            case DsplyStmt dsply:
                Dsply(dsply, frame);
                return Signal.None;
            case ClearStmt clear:
                if (clear.Target is NameExpr record && Find(record.Name, frame) is null && _displays.Any(d => d.ClearRecord(record.Name))) return Signal.None;
                Locate(clear.Target, frame).Clear();
                return Signal.None;
            case MonitorStmt monitor:
                try
                {
                    return Execute(monitor.Body, frame);
                }
                catch (RpgError e) when (monitor.Handlers.Any(h => h.Statuses.Count == 0 || h.Statuses.Contains(e.Status)))
                {
                    _status = e.Status;
                    return Execute(monitor.Handlers.First(h => h.Statuses.Count == 0 || h.Statuses.Contains(e.Status)).Body, frame);
                }
            case SqlStmt sql:
                ExecuteSql(sql.Sql, frame);
                return Signal.None;
            case PrintStmt print:
                Print(print, frame);
                return Signal.None;
            case DisplayStmt display:
                Display(display, frame);
                return Signal.None;
            case UnsupportedStmt unsupported:
                throw new RpgHalt($"{_name} reached line {unsupported.Line}, which isn't supported: {unsupported.Message}");
            default:
                throw new InvalidOperationException(statement.GetType().Name);
        }
    }

    private void Tick()
    {
        if ((++_statements & 0x3ff) != 0) return;
        _host.Cancelled.ThrowIfCancellationRequested();
        if (_statements > StatementLimit) throw new RpgHalt($"{_name} was stopped at line {_line}: it ran {StatementLimit:N0} statements without ending");
    }

    private void Assign(AssignStmt assign, Frame frame)
    {
        var value = Evaluate(assign.Value, frame);
        if (assign.Target is BifExpr { Name: "%SUBST" } subst)
        {
            if (subst.Args.Count < 2 || assign.Op != "=") throw new RpgError(100, "%SUBST on the left needs a variable and a start");
            var target = Locate(subst.Args[0], frame);
            var current = Values.ToText(target.Get());
            var start = (int)Values.ToDecimal(Evaluate(subst.Args[1], frame));
            var length = subst.Args.Count > 2 ? (int)Values.ToDecimal(Evaluate(subst.Args[2], frame)) : current.Length - start + 1;
            if (start < 1 || length < 0 || start + length - 1 > current.Length) throw new RpgError(100, "Value out of range for string operation.");
            var piece = Values.ToText(value);
            piece = piece.Length >= length ? piece[..length] : piece.PadRight(length);
            target.Set(current[..(start - 1)] + piece + current[(start - 1 + length)..]);
            return;
        }
        var cell = Locate(assign.Target, frame);
        cell.Set(assign.Op == "=" ? value : Arithmetic(assign.Op[..1], cell.Get(), value), assign.HalfAdjust, assign.Right);
    }

    private void Dsply(DsplyStmt dsply, Frame frame)
    {
        var message = dsply.Message is null ? "" : Evaluate(dsply.Message, frame);
        var text = Values.ToText(message).TrimEnd();
        if (text.Length > 52) text = text[..52];
        var response = dsply.Response is null ? null : Locate(dsply.Response, frame);
        var reply = _host.Dsply(text, response is null ? 0 : Math.Clamp(Values.ByteLength(response.Type), 1, 52));
        if (response is null || reply is null) return;
        response.Set(response.Type.IsNumeric ? Values.ParseNumber(reply) : reply);
    }

    // ---- variables ----

    /// <summary>The variable, subfield or array element an expression names.</summary>
    private Cell Locate(Expr e, Frame frame)
    {
        switch (e)
        {
            case NameExpr name:
                return Find(name.Name, frame) ?? throw new RpgError(100, $"{name.Name} isn't defined");
            case SpecialExpr { Name: "*INLR" }:
                return _lastRecord;
            case SpecialExpr special when special.Name.StartsWith("*IN") && special.Name.Length == 5 && int.TryParse(special.Name[3..], out var n) && n > 0:
                return _indicators[n];
            case MemberExpr member:
            {
                var target = Locate(member.Target, frame);
                if (target.Type.Kind != RpgKind.Ds || target.Elements is not null) throw new RpgError(100, $"{Describe(member.Target)} isn't a data structure");
                return target.Ds.Field(member.Field) ?? throw new RpgError(100, $"{Describe(member.Target)} has no subfield {member.Field}");
            }
            case CallExpr call:
            {
                var array = call.Target is SpecialExpr { Name: "*IN" } ? null : Locate(call.Target, frame);
                if (call.Args.Count != 1) throw new RpgError(100, $"{Describe(call.Target)} takes one index");
                var index = (int)Values.ToDecimal(Evaluate(call.Args[0], frame));
                var elements = array is null ? _indicators[1..] : array.Elements ?? throw new RpgError(100, $"{Describe(call.Target)} isn't an array");
                if (index < 1 || index > elements.Length) throw new RpgError(121, "An array index is out of range.");
                return elements[index - 1];
            }
            default:
                throw new RpgError(100, "this isn't a variable");
        }
    }

    private static string Describe(Expr e) => e switch
    {
        NameExpr n => n.Name,
        MemberExpr m => $"{Describe(m.Target)}.{m.Field}",
        CallExpr c => Describe(c.Target),
        SpecialExpr s => s.Name,
        _ => "the expression",
    };

    // ---- expressions ----

    private object Evaluate(Expr e, Frame frame)
    {
        switch (e)
        {
            case Literal literal:
                return literal.Value;
            case NameExpr name:
                if (Find(name.Name, frame) is { } cell) return cell.Get();
                if (_program.Procedures.ContainsKey(name.Name) || _program.Prototypes.ContainsKey(name.Name))
                    return Call(new CallExpr(name, []), frame, statement: false)!;
                throw new RpgError(100, $"{name.Name} isn't defined");
            case SpecialExpr special:
                if (special.Name == "*INLR" || special.Name.StartsWith("*IN") && special.Name.Length == 5 && char.IsDigit(special.Name[3])) return Locate(e, frame).Get();
                return new SpecialValue(special.Name, special.All);
            case MemberExpr:
                return Locate(e, frame).Get();
            case CallExpr call:
                if (call.Target is NameExpr target && Find(target.Name, frame) is null) return Call(call, frame, statement: false)!;
                return Locate(e, frame).Get();
            case BifExpr bif:
                return Builtin(bif, frame);
            case UnaryExpr { Op: "NOT" } not:
                return !Values.ToBool(Evaluate(not.Operand, frame));
            case UnaryExpr negate:
            {
                var operand = Evaluate(negate.Operand, frame);
                return operand is double d ? -d : -Values.ToDecimal(operand);
            }
            case BinaryExpr { Op: "AND" } and:
                return Values.ToBool(Evaluate(and.Left, frame)) && Values.ToBool(Evaluate(and.Right, frame));
            case BinaryExpr { Op: "OR" } or:
                return Values.ToBool(Evaluate(or.Left, frame)) || Values.ToBool(Evaluate(or.Right, frame));
            case BinaryExpr binary:
            {
                var left = Evaluate(binary.Left, frame);
                var right = Evaluate(binary.Right, frame);
                return binary.Op switch
                {
                    "=" => Values.Compare(left, right) == 0,
                    "<>" => Values.Compare(left, right) != 0,
                    "<" => Values.Compare(left, right) < 0,
                    ">" => Values.Compare(left, right) > 0,
                    "<=" => Values.Compare(left, right) <= 0,
                    ">=" => Values.Compare(left, right) >= 0,
                    _ => Arithmetic(binary.Op, left, right),
                };
            }
            case UnsupportedExpr unsupported:
                throw new RpgHalt($"{_name} reached line {_line}, which isn't supported: {unsupported.Message}");
            default:
                throw new InvalidOperationException(e.GetType().Name);
        }
    }

    private static object Arithmetic(string op, object left, object right)
    {
        if (op == "+" && left is string or bool && right is string or bool) return Values.ToText(left) + Values.ToText(right);
        if (left is DateOnly or TimeOnly or DateTime && right is Duration duration)
        {
            if (op is not ("+" or "-")) throw new RpgError(100, "a duration can only be added or subtracted");
            return AddDuration(left, duration, op == "-" ? -1 : 1);
        }
        if (right is DateOnly or TimeOnly or DateTime && left is Duration added && op == "+") return AddDuration(right, added, 1);
        if (left is string or bool || right is string or bool || !(Values.IsNumber(left) && Values.IsNumber(right)))
            throw new RpgError(100, $"{op} needs two numbers{(op == "+" ? " or two character values" : "")}");
        try
        {
            if (left is double || right is double)
            {
                var (a, b) = (System.Convert.ToDouble(left), System.Convert.ToDouble(right));
                return op switch
                {
                    "+" => a + b,
                    "-" => a - b,
                    "*" => a * b,
                    "/" => b == 0 ? throw new RpgError(102, "Attempt to divide by zero.") : a / b,
                    _ => Math.Pow(a, b),
                };
            }
            var (x, y) = ((decimal)left, (decimal)right);
            return op switch
            {
                "+" => x + y,
                "-" => x - y,
                "*" => x * y,
                "/" => y == 0 ? throw new RpgError(102, "Attempt to divide by zero.") : x / y,
                "**" => y == decimal.Truncate(y) && Math.Abs(y) <= 64 ? Power(x, (int)y) : (decimal)Math.Pow((double)x, (double)y),
                _ => throw new InvalidOperationException(op),
            };
        }
        catch (OverflowException)
        {
            throw new RpgError(103, "The target for a numeric operation is too small to hold the result.");
        }
    }

    private static decimal Power(decimal x, int y)
    {
        var result = 1m;
        for (var i = 0; i < Math.Abs(y); i++) result *= x;
        if (y >= 0) return result;
        return result == 0 ? throw new RpgError(102, "Attempt to divide by zero.") : 1 / result;
    }

    private static object AddDuration(object moment, Duration duration, int sign)
    {
        var count = (double)(duration.Count * sign);
        var whole = (int)(duration.Count * sign);
        try
        {
            var stamp = moment switch
            {
                DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                TimeOnly t => new DateTime(2000, 1, 1).Add(t.ToTimeSpan()),
                _ => (DateTime)moment,
            };
            stamp = duration.Unit switch
            {
                "*YEARS" => stamp.AddYears(whole),
                "*MONTHS" => stamp.AddMonths(whole),
                "*DAYS" => stamp.AddDays(whole),
                "*HOURS" => stamp.AddHours(count),
                "*MINUTES" => stamp.AddMinutes(count),
                "*SECONDS" => stamp.AddSeconds(count),
                _ => stamp.AddTicks((long)(count * 10)),
            };
            return moment switch
            {
                DateOnly => DateOnly.FromDateTime(stamp),
                TimeOnly => TimeOnly.FromDateTime(stamp),
                _ => stamp,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new RpgError(113, "Date overflow or underflow.");
        }
    }

    // ---- calls ----

    private object? Call(CallExpr call, Frame frame, bool statement)
    {
        if (call.Target is not NameExpr { Name: var name })
        {
            if (statement) throw new RpgError(100, "this isn't a statement");
            return Locate(call, frame).Get();
        }
        if (_program.Procedures.TryGetValue(name, out var procedure)) return CallProcedure(procedure, call.Args, frame);
        if (_program.Prototypes.TryGetValue(name, out var prototype) && prototype.Program is { } program)
        {
            var arguments = new List<Cell>();
            var copyBack = new List<(Cell Target, Cell Passed)>();
            for (var i = 0; i < call.Args.Count; i++)
            {
                var parameter = i < prototype.Params.Count ? prototype.Params[i] : null;
                var value = Evaluate(call.Args[i], frame);
                var passed = new Cell(parameter is null ? TypeOf(value) : Resolve(parameter.Type, frame));
                passed.Set(value);
                arguments.Add(passed);
                if (parameter is { Const: false, ByValue: false } && IsVariable(call.Args[i])) copyBack.Add((Locate(call.Args[i], frame), passed));
            }
            try
            {
                _machine.Call(program, arguments, _host, _depth + 1);
            }
            catch (RpgHalt e)
            {
                throw new RpgError(202, $"The call to {program} ended in error: {e.Message}");
            }
            foreach (var (target, passed) in copyBack) target.Set(passed.Get());
            return null;
        }
        if (statement && Find(name, frame) is not null) throw new RpgError(100, $"{name} is a variable, not a procedure");
        throw new RpgError(100, prototype is not null
            ? $"{name} has a prototype but isn't in this program: only EXTPGM prototypes call out of it"
            : $"{name} isn't defined");
    }

    private static bool IsVariable(Expr e) => e is NameExpr or MemberExpr or CallExpr or SpecialExpr { Name: "*INLR" };

    private static RpgType TypeOf(object value) => value switch
    {
        string s => RpgType.Char(Math.Max(s.Length, 1)),
        decimal => RpgType.Number(15, 5),
        bool => RpgType.Indicator,
        DateOnly => new RpgType(RpgKind.Date),
        TimeOnly => new RpgType(RpgKind.Time),
        DateTime => new RpgType(RpgKind.Timestamp),
        double => new RpgType(RpgKind.Float, 8),
        _ => throw new RpgError(100, "this value can't be passed as a parameter"),
    };

    private object? CallProcedure(Procedure procedure, IReadOnlyList<Expr> arguments, Frame caller)
    {
        if (arguments.Count > procedure.Params.Count)
            throw new RpgError(100, $"{procedure.Name} takes {procedure.Params.Count} parameter{(procedure.Params.Count == 1 ? "" : "s")}, not {arguments.Count}");
        if (++_calls > DepthLimit)
        {
            _calls--;
            throw new RpgError(100, $"procedures are calling each other more than {DepthLimit} deep");
        }
        try
        {
            var frame = new Frame(procedure, []) { Parameters = arguments.Count };
            var copyBack = new List<(Cell Target, Cell Passed)>();
            for (var i = 0; i < arguments.Count; i++)
            {
                var parameter = procedure.Params[i];
                var type = Resolve(parameter.Type, frame.Variables.Count == 0 ? _main : frame);
                Cell passed;
                // By reference: the caller's own variable when it has the parameter's type, otherwise a copy written back on return.
                if (!parameter.Const && !parameter.ByValue && IsVariable(arguments[i]) && Find(Root(arguments[i]), caller) is not null)
                {
                    var source = Locate(arguments[i], caller);
                    if (source.Type == type)
                    {
                        passed = source;
                    }
                    else
                    {
                        passed = new Cell(type);
                        passed.Set(source.Get());
                        copyBack.Add((source, passed));
                    }
                }
                else
                {
                    passed = new Cell(type);
                    passed.Set(Evaluate(arguments[i], caller));
                }
                if (parameter.Name.Length > 0) frame.Variables[parameter.Name] = passed;
            }
            foreach (var declaration in procedure.Locals) Declare(declaration, frame);
            var line = _line;
            Execute(procedure.Body, frame);
            _line = line;
            foreach (var (target, passed) in copyBack) target.Set(passed.Get());
            if (procedure.Returns is null) return null;
            var result = new Cell(Resolve(procedure.Returns, frame));
            if (frame.Result is not null) result.Set(frame.Result);
            return result.Get();
        }
        finally
        {
            _calls--;
        }
    }

    /// <summary>The variable name an expression starts from, or "" when it doesn't start from one.</summary>
    private static string Root(Expr e) => e switch
    {
        NameExpr n => n.Name,
        MemberExpr m => Root(m.Target),
        CallExpr c => Root(c.Target),
        _ => "",
    };

    // ---- display files ----

    private void OpenDisplay(FileDecl file)
    {
        _line = file.Line;
        var definition = _machine.LoadDisplayFile(file.Name, _host.User) ?? throw new RpgError(1211, $"display file {file.Name} doesn't exist: create it first with CRTDSPF");
        // Each named field is a variable of the program, unless the program declares it.
        foreach (var field in definition.Records.Values.SelectMany(r => r.Fields).Where(f => f.Name is not null))
        {
            _globals.TryAdd(field.Name!, new Cell(field.Numeric ? RpgType.Number(field.Length, field.Decimals) : RpgType.Char(field.Length)));
        }
        _displays.Add(new Dds.DisplaySession(definition, file,
            name => _globals.GetValueOrDefault(name),
            Indicator,
            _host.User));
    }

    /// <summary>A display file's indicator: the INDDS subfield at that position, or *IN.</summary>
    private Cell Indicator(FileDecl file, int number)
    {
        if (file.IndDs is null) return _indicators[number];
        if (_globals.GetValueOrDefault(file.IndDs) is not { Type.Kind: RpgKind.Ds, Elements: null } ds) throw new RpgError(100, $"the INDDS data structure {file.IndDs} isn't defined");
        for (var i = 0; i < ds.Ds.Layout.Fields.Count; i++)
        {
            if (ds.Ds.Layout.Fields[i].Position == number) return ds.Ds.Cells[i];
        }
        // An indicator the structure doesn't name still has to be somewhere.
        return _spare.TryGetValue((file.Name, number), out var cell) ? cell : _spare[(file.Name, number)] = new Cell(RpgType.Indicator);
    }

    private readonly Dictionary<(string File, int Number), Cell> _spare = [];

    private void Display(DisplayStmt statement, Frame frame)
    {
        var session = _displays.FirstOrDefault(d => d.File.Records.ContainsKey(statement.Record))
                      ?? throw new RpgError(1211, $"{statement.Record} isn't a record format of a display file in this program");
        var workstation = _host.Workstation ?? throw new RpgError(1255, $"{statement.Op} {statement.Record} needs a display, and this job has none");
        _error = false;
        try
        {
            switch (statement.Op)
            {
                case "WRITE":
                    session.Write(statement.Record, workstation);
                    break;
                case "EXFMT":
                    session.Write(statement.Record, workstation, read: true);
                    break;
                case "READ":
                    session.Read(statement.Record, workstation);
                    _eof = false;
                    break;
                case "READC":
                    _eof = !session.ReadChanged(statement.Record);
                    break;
                case "CHAIN":
                    _found = session.Chain(statement.Record, (int)Values.ToDecimal(Evaluate(statement.Key!, frame)));
                    break;
                case "UPDATE":
                    session.Update(statement.Record);
                    break;
            }
        }
        catch (RpgError e) when (statement.ErrorExtender)
        {
            _error = true;
            _status = e.Status;
        }
    }
}
