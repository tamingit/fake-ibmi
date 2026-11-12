// A parsed RPG program: declarations, procedures and statements.

namespace FakeIbmI.Rpg;

// ---- expressions ----

internal abstract record Expr;

/// <summary>A literal: a string or a decimal.</summary>
internal sealed record Literal(object Value) : Expr;

internal sealed record NameExpr(string Name) : Expr;

/// <summary>A special word, such as *ON, *BLANKS or *INLR. <see cref="All"/> is the literal of *ALL'x'.</summary>
internal sealed record SpecialExpr(string Name, string? All = null) : Expr;

/// <summary>name(args): a procedure call or an array element, told apart when it runs.</summary>
internal sealed record CallExpr(Expr Target, IReadOnlyList<Expr> Args) : Expr;

/// <summary>A subfield: target.field.</summary>
internal sealed record MemberExpr(Expr Target, string Field) : Expr;

internal sealed record BifExpr(string Name, IReadOnlyList<Expr> Args) : Expr;

internal sealed record UnaryExpr(string Op, Expr Operand) : Expr;

internal sealed record BinaryExpr(string Op, Expr Left, Expr Right) : Expr;

/// <summary>An expression the fake can't evaluate. Reaching it ends the program with this message.</summary>
internal sealed record UnsupportedExpr(string Message) : Expr;

// ---- statements ----

internal abstract record Stmt(int Line);

internal sealed record AssignStmt(int Line, Expr Target, string Op, Expr Value, bool HalfAdjust, bool Right) : Stmt(Line);

internal sealed record IfStmt(int Line, IReadOnlyList<(Expr Condition, IReadOnlyList<Stmt> Body)> Branches, IReadOnlyList<Stmt>? Else) : Stmt(Line);

/// <summary>DOW, or DOU when <see cref="Until"/>.</summary>
internal sealed record LoopStmt(int Line, Expr Condition, bool Until, IReadOnlyList<Stmt> Body) : Stmt(Line);

internal sealed record ForStmt(int Line, Expr Variable, Expr? Start, Expr? Limit, Expr? By, bool Down, IReadOnlyList<Stmt> Body) : Stmt(Line);

internal sealed record ForEachStmt(int Line, Expr Variable, Expr Array, IReadOnlyList<Stmt> Body) : Stmt(Line);

internal sealed record SelectStmt(int Line, IReadOnlyList<(Expr Condition, IReadOnlyList<Stmt> Body)> Whens, IReadOnlyList<Stmt>? Other) : Stmt(Line);

/// <summary>LEAVE, ITER, LEAVESR, or RETURN with its value.</summary>
internal sealed record JumpStmt(int Line, Signal Signal, Expr? Value = null) : Stmt(Line);

internal sealed record ExsrStmt(int Line, string Name) : Stmt(Line);

internal sealed record CallStmt(int Line, Expr Call) : Stmt(Line);

internal sealed record DsplyStmt(int Line, Expr? Message, Expr? Response) : Stmt(Line);

internal sealed record ClearStmt(int Line, Expr Target) : Stmt(Line);

internal sealed record MonitorStmt(int Line, IReadOnlyList<Stmt> Body, IReadOnlyList<(IReadOnlyList<int> Statuses, IReadOnlyList<Stmt> Body)> Handlers) : Stmt(Line);

internal sealed record SqlStmt(int Line, SqlTranslation Sql) : Stmt(Line);

/// <summary>A display file operation: EXFMT, WRITE, READ, READC, CHAIN or UPDATE on a record format.</summary>
internal sealed record DisplayStmt(int Line, string Op, string Record, Expr? Key, bool ErrorExtender) : Stmt(Line);

/// <summary>WRITE to a printer file: one line of output.</summary>
internal sealed record PrintStmt(int Line, string File, Expr Data) : Stmt(Line);

/// <summary>A statement the fake can't run. Reaching it ends the program with this message.</summary>
internal sealed record UnsupportedStmt(int Line, string Message) : Stmt(Line);

internal enum Signal
{
    None,
    Leave,
    Iter,
    LeaveSr,
    Return,
}

// ---- declarations ----

/// <summary>
/// A type as written: a keyword (CHAR, PACKED ...) with its length and decimals, LIKE or LIKEDS, or
/// a data structure's subfields. Lengths are expressions, as they can name constants.
/// </summary>
internal sealed class TypeSpec
{
    public string? Keyword { get; set; }
    public Expr? Length { get; set; }
    public Expr? Decimals { get; set; }
    public string? Like { get; set; }
    public string? LikeDs { get; set; }
    public string? ExtName { get; set; }
    public Expr? Dim { get; set; }
    public bool Qualified { get; set; }
    public List<VarDecl>? Fields { get; set; }
}

internal abstract record Decl(int Line, string Name);

/// <summary>A variable, subfield or parameter. <see cref="Position"/> is POS(n) of an indicator subfield, or 0.</summary>
internal sealed record VarDecl(int Line, string Name, TypeSpec Type, Expr? Inz = null, int Position = 0,
    bool Const = false, bool ByValue = false) : Decl(Line, Name);

internal sealed record ConstDecl(int Line, string Name, Expr Value) : Decl(Line, Name);

/// <summary>A workstation file. <see cref="Subfiles"/> maps a subfile record to its relative record number field.</summary>
/// <param name="Printer">The line width of a printer file, or 0 when it's a display file.</param>
internal sealed record FileDecl(int Line, string Name, string? IndDs, IReadOnlyDictionary<string, string> Subfiles, int Printer = 0);

/// <summary>A prototype. <see cref="Program"/> is the program EXTPGM names, or null for a procedure.</summary>
internal sealed record ProtoDecl(string Name, string? Program, IReadOnlyList<VarDecl> Params);

internal sealed class Procedure(string name)
{
    public string Name { get; } = name;
    public List<VarDecl> Params { get; set; } = [];
    public TypeSpec? Returns { get; set; }
    public List<Decl> Locals { get; } = [];
    public List<Stmt> Body { get; set; } = [];
    public Dictionary<string, IReadOnlyList<Stmt>> Subroutines { get; } = [];
}

internal sealed class RpgProgram
{
    /// <summary>The main procedure: the global declarations, the program's parameters and its mainline.</summary>
    public Procedure Main { get; } = new("*MAIN");

    /// <summary>CTL-OPT MAIN(name): the procedure that runs instead of a mainline.</summary>
    public string? MainProcedure { get; set; }

    public List<FileDecl> Files { get; } = [];
    public Dictionary<string, Procedure> Procedures { get; } = [];
    public Dictionary<string, ProtoDecl> Prototypes { get; } = [];

    /// <summary>Every DECLARE CURSOR, by cursor name: declared before the program runs, wherever it's written.</summary>
    public Dictionary<string, SqlTranslation> Cursors { get; } = [];

    public List<Diagnostic> Diagnostics { get; } = [];
}
