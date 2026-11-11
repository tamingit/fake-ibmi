// The display state the IBM i writes into: regeneration buffer, format table (fields),
// cursor and keyboard (IBM 5494 Functions Reference SC30-3533-04, chapters 15 and 16).
// `Screen` is mutable and internal; `ScreenSnapshot` is the immutable view for renderers.
//
// Property names map 1:1 to the TS camelCase names when serialized with System.Text.Json
// and a camelCase naming policy; enums carry their TS strings.

using System.Text;
using System.Text.Json.Serialization;

namespace FakeIbmI.Tests.Client;

/// <param name="Row">1-based.</param>
/// <param name="Column">1-based.</param>
public sealed record Position(int Row, int Column);

/// <summary>The field format word, decoded (table 15-6).</summary>
/// <param name="Shift">alpha-shift, alpha-only, numeric-shift, numeric-only, katakana-shift, digits-only, io or signed-numeric.</param>
/// <param name="Adjust">none, right-zero, right-blank or mandatory-fill.</param>
public sealed record FieldFormat(
    bool Bypass,
    bool DupEnable,
    bool Modified,
    string Shift,
    bool AutoEnter,
    bool FieldExitRequired,
    bool Monocase,
    bool MandatoryEnter,
    string Adjust)
{
    public const int FfwBypass = 0x2000;
    public const int FfwModified = 0x0800;

    private static readonly string[] Shifts =
    [
        "alpha-shift",
        "alpha-only",
        "numeric-shift",
        "numeric-only",
        "katakana-shift",
        "digits-only",
        "io",
        "signed-numeric",
    ];

    public static FieldFormat Decode(int ffw)
    {
        var adjust = ffw & 0x0007;
        return new FieldFormat(
            Bypass: (ffw & FfwBypass) != 0,
            DupEnable: (ffw & 0x1000) != 0,
            Modified: (ffw & FfwModified) != 0,
            Shift: Shifts[(ffw >> 8) & 0x07],
            AutoEnter: (ffw & 0x0080) != 0,
            FieldExitRequired: (ffw & 0x0040) != 0,
            Monocase: (ffw & 0x0020) != 0,
            MandatoryEnter: (ffw & 0x0008) != 0,
            Adjust: adjust switch
            {
                5 => "right-zero",
                6 => "right-blank",
                7 => "mandatory-fill",
                _ => "none",
            });
    }
}

/// <param name="Attribute">The leading field attribute (0x20–0x3F).</param>
public sealed record SnapshotField(
    int Row,
    int Column,
    int Length,
    int Ffw,
    IReadOnlyList<int> Fcws,
    int Attribute,
    FieldFormat Format)
{
    public bool Equals(SnapshotField? other) =>
        other is not null
        && Row == other.Row
        && Column == other.Column
        && Length == other.Length
        && Ffw == other.Ffw
        && Fcws.SequenceEqual(other.Fcws)
        && Attribute == other.Attribute
        && Format == other.Format;

    public override int GetHashCode() => HashCode.Combine(Row, Column, Length, Ffw, Attribute);
}

/// <summary>
/// Unlocked: the operator can type. Locked: input inhibited while the host works.
/// Error: an operator error or a host WRITE ERROR CODE, cleared by Reset or, as in ACS, by
/// any key except Help, Attn and SysReq.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<KeyboardState>))]
public enum KeyboardState
{
    [JsonStringEnumMemberName("unlocked")]
    Unlocked,

    [JsonStringEnumMemberName("locked")]
    Locked,

    [JsonStringEnumMemberName("error")]
    Error,
}

/// <summary>An operator (keying) error, or an error the host wrote with WRITE ERROR CODE.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ErrorKind>))]
public enum ErrorKind
{
    [JsonStringEnumMemberName("operator")]
    Operator,

    [JsonStringEnumMemberName("host")]
    Host,
}

/// <param name="Code">The operator error code as its four decimal digits read as a number (0005 is 5); 0 for host errors.</param>
/// <param name="Help">Help has been pressed (post-help error state): only Attn works until the host answers.</param>
public sealed record ErrorState(ErrorKind Kind, int Code, bool Help);

/// <summary>The immutable view of the display for renderers.</summary>
/// <param name="Buffer">Regeneration buffer, row by row: EBCDIC characters, attribute bytes (0x20–0x3F) and nulls.</param>
/// <param name="Attributes">The field attribute in effect at each position: an attribute lasts until the next one.</param>
/// <param name="KeyboardLocked">True unless the keyboard is unlocked (input inhibited).</param>
/// <param name="FieldExitRequired">The cursor waits on the last position of a field that needs a field exit key.</param>
/// <param name="TypeAhead">Keystrokes waiting for the keyboard to unlock.</param>
/// <param name="ErrorRow">Where error messages go. It can be one row below the screen (a separate message line).</param>
/// <param name="MessageLine">The separate message line, when the error row is below the screen.</param>
/// <param name="PendingRead">A READ command waiting for the user to press an AID key, if any.</param>
public sealed record ScreenSnapshot(
    int Rows,
    int Columns,
    byte[] Buffer,
    byte[] Attributes,
    IReadOnlyList<SnapshotField> Fields,
    Position Cursor,
    KeyboardState Keyboard,
    bool KeyboardLocked,
    ErrorState? Error,
    bool InsertMode,
    bool FieldExitRequired,
    int TypeAhead,
    bool MessageWaiting,
    int ErrorRow,
    byte[]? MessageLine,
    int? PendingRead)
{
    public bool Equals(ScreenSnapshot? other) =>
        other is not null
        && Rows == other.Rows
        && Columns == other.Columns
        && Buffer.AsSpan().SequenceEqual(other.Buffer)
        && Attributes.AsSpan().SequenceEqual(other.Attributes)
        && Fields.SequenceEqual(other.Fields)
        && Cursor == other.Cursor
        && Keyboard == other.Keyboard
        && KeyboardLocked == other.KeyboardLocked
        && Error == other.Error
        && InsertMode == other.InsertMode
        && FieldExitRequired == other.FieldExitRequired
        && TypeAhead == other.TypeAhead
        && MessageWaiting == other.MessageWaiting
        && ErrorRow == other.ErrorRow
        && (MessageLine is null
            ? other.MessageLine is null
            : other.MessageLine is not null && MessageLine.AsSpan().SequenceEqual(other.MessageLine))
        && PendingRead == other.PendingRead;

    public override int GetHashCode() => HashCode.Combine(Rows, Columns, Cursor, Keyboard, ErrorRow, PendingRead);
}

/// <summary>A format table entry. Mutable, and compared by reference.</summary>
public sealed class FieldEntry
{
    /// <summary>Buffer index of the first data position; the leading attribute is one before it.</summary>
    public int Start { get; set; }

    public int Length { get; set; }
    public int Ffw { get; set; }
    public List<int> Fcws { get; set; } = [];
    public int Attribute { get; set; }
}

/// <summary>An unlock the host asked for during an error.</summary>
public sealed record DeferredUnlock(bool MoveCursor);

public sealed class Screen
{
    /// <summary>The most keystrokes kept while the keyboard is locked, as on the 5494 (page 16-13).</summary>
    public const int TypeAheadLimit = 32;

    public int Rows { get; set; } = 24;
    public int Columns { get; set; } = 80;
    public byte[] Buffer { get; set; } = new byte[24 * 80];
    public List<FieldEntry> Fields { get; set; } = [];

    /// <summary>Current display address; -1 is row 1 column 0, allowed only before an SF.</summary>
    public int Address { get; set; }

    public int Cursor { get; set; }

    /// <summary>Set by IC orders; null means none has been given since the screen was cleared.</summary>
    public int? InsertCursor { get; set; }

    /// <summary>Input inhibited while the host works. An error state is kept separately.</summary>
    public bool KeyboardLocked { get; set; } = true;

    public ErrorState? Error { get; set; }

    /// <summary>An unlock the host asked for during an error; it happens when the error is cleared.</summary>
    public DeferredUnlock? DeferredUnlock { get; set; }

    /// <summary>The error line as it was before an error was written over it.</summary>
    public byte[]? SavedErrorLine { get; set; }

    public bool InsertMode { get; set; }

    /// <summary>The cursor waits on the last position of a field-exit-required field.</summary>
    public bool FieldExitPending { get; set; }

    /// <summary>Start of the field the operator has begun keying into, for mandatory-fill checks.</summary>
    public int? ActiveField { get; set; }

    /// <summary>An AID key pressed with no READ pending; it goes when the next READ arrives.</summary>
    public int? HeldAid { get; set; }

    public bool MessageWaiting { get; set; }
    public int ErrorRow { get; set; } = 24;

    /// <summary>The separate message line (row 25 or 28), used when the error row is below the screen.</summary>
    public byte[] MessageLine { get; set; } = new byte[80];

    /// <summary>The last SOH header: flag byte, reserved, resequencing, error row, PF key masks.</summary>
    public byte[] Header { get; set; } = [];

    public int? PendingRead { get; set; }

    /// <summary>The control characters of the pending READ; they apply after the reply is sent.</summary>
    public (int Cc0, int Cc1) ReadControl { get; set; } = (0, 0);

    public List<Key> TypeAhead { get; set; } = [];

    public int Size => Rows * Columns;

    public KeyboardState KeyboardState =>
        Error is not null ? KeyboardState.Error : KeyboardLocked ? KeyboardState.Locked : KeyboardState.Unlocked;

    /// <summary>The master MDT: true when any field has its modified data tag on.</summary>
    public bool Modified => Fields.Any(f => (f.Ffw & FieldFormat.FfwModified) != 0);

    /// <summary>CLEAR UNIT (24 x 80) and CLEAR UNIT ALTERNATE (27 x 132, or same size), page 15-4.</summary>
    public void Clear(int rows, int columns)
    {
        Rows = rows;
        Columns = columns;
        Buffer = new byte[rows * columns];
        MessageLine = new byte[columns];
        ClearFormatTable();
        // CLEAR UNIT writes a normal attribute at row 1, column 2 and homes the cursor.
        Buffer[1] = Display.NormalAttribute;
        Address = 0;
        Cursor = 0;
        InsertCursor = null;
        ErrorRow = rows;
        Error = null;
        DeferredUnlock = null;
        SavedErrorLine = null;
        InsertMode = false;
    }

    /// <summary>
    /// CLEAR FORMAT TABLE: the fields go, the screen contents stay. Also clears any AID
    /// not yet serviced and the pending READ, but not an error state.
    /// </summary>
    public void ClearFormatTable()
    {
        Fields = [];
        Header = [];
        KeyboardLocked = true;
        PendingRead = null;
        HeldAid = null;
        ActiveField = null;
        FieldExitPending = false;
    }

    /// <summary>Buffer index for a 1-based address, or null if it is off the screen.</summary>
    public int? Index(int row, int column)
    {
        if (row < 1 || row > Rows || column < 1 || column > Columns) return null;
        return (row - 1) * Columns + (column - 1);
    }

    /// <summary>Writes one byte at the current address and moves on, wrapping at the end of the screen.</summary>
    public void Put(byte b)
    {
        if (Address >= 0) Buffer[Address] = b;
        Address = (Address + 1) % Size;
    }

    /// <summary>WRITE TO DISPLAY control character byte 0 (table 15-2). READ commands use it too.</summary>
    public void ApplyControlByte0(int cc)
    {
        var mode = cc >> 5;
        if (mode == 0) return;
        // Every mode except 000 resets a pending AID and locks the keyboard.
        KeyboardLocked = true;
        HeldAid = null;
        var resetNonBypass = mode is 2 or 5 or 6;
        var resetAll = mode is 3 or 7;
        var nullModified = mode is 4 or 6;
        var nullAll = mode is 5 or 7;
        foreach (var field in Fields)
        {
            var bypass = (field.Ffw & FieldFormat.FfwBypass) != 0;
            if (!bypass && (nullAll || (nullModified && (field.Ffw & FieldFormat.FfwModified) != 0)))
            {
                JsArrays.Fill(Buffer, 0, field.Start, field.Start + field.Length);
            }
            if (resetAll || (resetNonBypass && !bypass)) field.Ffw &= ~FieldFormat.FfwModified;
        }
    }

    /// <summary>
    /// Control character byte 1 (table 15-3), apart from the cursor flag's effect on an MC
    /// order. Returns true when the alarm should sound.
    /// </summary>
    public bool ApplyControlByte1(int cc)
    {
        if ((cc & 0x02) != 0) MessageWaiting = false;
        if ((cc & 0x01) != 0) MessageWaiting = true;
        if ((cc & 0x08) != 0)
        {
            var moveCursor = (cc & 0x40) == 0;
            // During an error the unlock waits until the error is cleared (table 15-3, note 3).
            if (Error is not null) DeferredUnlock = new DeferredUnlock(moveCursor);
            else if (KeyboardLocked) Unlock(moveCursor);
        }
        return (cc & 0x04) != 0;
    }

    /// <summary>Unlocks the keyboard, drops any AID not yet serviced and, unless told not to, homes the cursor.</summary>
    public void Unlock(bool moveCursor)
    {
        KeyboardLocked = false;
        HeldAid = null;
        if (moveCursor) Cursor = HomePosition();
    }

    /// <summary>SOH: clears the format table, then keeps the header (PF masks) and the error row.</summary>
    public void StartHeader(ReadOnlySpan<byte> header)
    {
        Fields = [];
        Header = header.ToArray();
        KeyboardLocked = true;
        ActiveField = null;
        FieldExitPending = false;
        if (header.Length > 3)
        {
            int errorRow = header[3];
            if (errorRow >= 1 && errorRow <= Rows + 1) ErrorRow = errorRow;
        }
    }

    /// <summary>Defines a field at the current address (the SF order). Returns an error, if any.</summary>
    public (int Code, string Message)? StartField(int? ffw, List<int> fcws, int attribute, int length)
    {
        var attributeAt = Address;
        var start = attributeAt + 1;
        if (attributeAt >= 0) Buffer[attributeAt] = (byte)attribute;
        Address = start % Size;
        if (ffw is not int formatWord) return null; // An output field has no format table entry.

        KeyboardLocked = true;
        var existing = Fields.Find(f => f.Start == start);
        if (existing is not null)
        {
            // FCWs are ignored when an existing field is modified.
            existing.Ffw = formatWord;
            existing.Attribute = attribute;
            return null;
        }
        if (length < 1)
        {
            return (NegativeResponse.FieldLengthNotValid, $"field at {Describe(start)} has length {length}");
        }
        if (start + length > Size)
        {
            return (NegativeResponse.FieldPastEnd, $"field at {Describe(start)} with length {length} runs off the screen");
        }
        var entry = new FieldEntry { Start = start, Length = length, Ffw = formatWord, Fcws = fcws, Attribute = attribute };
        // Kept sorted by start; starts are unique, so the order is the same as a stable sort.
        var at = Fields.FindIndex(f => f.Start > start);
        Fields.Insert(at < 0 ? Fields.Count : at, entry);
        // A new input field also gets an ending attribute after its last position.
        var end = start + length;
        if (end < Size) Buffer[end] = Display.NormalAttribute;
        return null;
    }

    /// <summary>The field whose data positions include `index`, if any.</summary>
    public FieldEntry? FieldAt(int index) => Fields.Find(f => index >= f.Start && index < f.Start + f.Length);

    /// <summary>Where the cursor goes when the keyboard unlocks: IC address, first input field, or home.</summary>
    public int HomePosition()
    {
        if (InsertCursor is int insertCursor) return insertCursor;
        return Fields.Find(f => (f.Ffw & FieldFormat.FfwBypass) == 0)?.Start ?? 0;
    }

    /// <summary>The error line's contents: a screen row, or the separate message line.</summary>
    public byte[] ReadErrorLine()
    {
        if (ErrorRow > Rows) return (byte[])MessageLine.Clone();
        var start = (ErrorRow - 1) * Columns;
        return Buffer.AsSpan(start, Columns).ToArray();
    }

    public void WriteErrorLine(ReadOnlySpan<byte> line)
    {
        var data = line[..Math.Min(line.Length, Columns)];
        if (ErrorRow > Rows) data.CopyTo(MessageLine);
        else data.CopyTo(Buffer.AsSpan((ErrorRow - 1) * Columns));
    }

    /// <summary>Leaves an error: restores the error line and makes any unlock the host asked for.</summary>
    public void ClearError()
    {
        if (SavedErrorLine is not null) WriteErrorLine(SavedErrorLine);
        SavedErrorLine = null;
        Error = null;
        var deferred = DeferredUnlock;
        DeferredUnlock = null;
        if (deferred is not null && KeyboardLocked) Unlock(deferred.MoveCursor);
    }

    public string Describe(int index) => $"row {index / Columns + 1}, column {index % Columns + 1}";

    public ScreenSnapshot Snapshot()
    {
        Position PositionOf(int index) => new(index / Columns + 1, index % Columns + 1);
        var keyboard = KeyboardState;
        return new ScreenSnapshot(
            Rows: Rows,
            Columns: Columns,
            Buffer: (byte[])Buffer.Clone(),
            Attributes: Display.AttributesInEffect(Buffer),
            Fields: Fields
                .Select(f =>
                {
                    var p = PositionOf(f.Start);
                    return new SnapshotField(p.Row, p.Column, f.Length, f.Ffw, f.Fcws.ToArray(), f.Attribute, FieldFormat.Decode(f.Ffw));
                })
                .ToArray(),
            Cursor: PositionOf(Cursor),
            Keyboard: keyboard,
            KeyboardLocked: keyboard != KeyboardState.Unlocked,
            Error: Error,
            InsertMode: InsertMode,
            FieldExitRequired: FieldExitPending,
            TypeAhead: TypeAhead.Count,
            MessageWaiting: MessageWaiting,
            ErrorRow: ErrorRow,
            MessageLine: ErrorRow > Rows ? (byte[])MessageLine.Clone() : null,
            PendingRead: PendingRead);
    }
}

/// <summary>How the screen shows: attributes in effect, and text as ACS shows it.</summary>
public static class Display
{
    /// <summary>The normal field attribute: green, no highlighting.</summary>
    public const byte NormalAttribute = 0x20;

    /// <summary>The field attribute in effect at each position: an attribute lasts until the next one.</summary>
    public static byte[] AttributesInEffect(ReadOnlySpan<byte> buffer)
    {
        var attributes = new byte[buffer.Length];
        var current = NormalAttribute;
        for (var i = 0; i < buffer.Length; i++)
        {
            var b = buffer[i];
            if (IsAttribute(b)) current = b;
            attributes[i] = current;
        }
        return attributes;
    }

    public static bool IsAttribute(int b) => (b & 0xe0) == 0x20;

    /// <summary>True for attributes that hide text: underscore, high intensity and reverse all set.</summary>
    public static bool IsNondisplay(int attribute) => (attribute & 0x07) == 0x07;

    /// <summary>
    /// How a buffer byte shows on the screen, as ACS shows it. Attributes, nulls, Field Mark,
    /// SO and SI show as spaces and Dup as an asterisk. Any other byte below 0x20 shows as a
    /// solid block: ACS does this for 0x1F (checked on test, 30 September 2026), and the
    /// others are assumed to match.
    /// </summary>
    public static string DisplayCharacter(byte b)
    {
        if (b == 0x1c) return "*";
        if (b > 0x00 && b < 0x20 && b is not (0x1e or 0x0e or 0x0f)) return "█";
        if (b < 0x40 || b == 0xff) return " ";
        return Ebcdic.ToChar(b).ToString();
    }

    /// <summary>The screen as plain text, one string per row. Nondisplay text shows as spaces.</summary>
    public static List<string> ScreenText(ScreenSnapshot snapshot)
    {
        var lines = new List<string>();
        for (var row = 0; row < snapshot.Rows; row++)
        {
            var line = new StringBuilder();
            for (var column = 0; column < snapshot.Columns; column++)
            {
                var i = row * snapshot.Columns + column;
                line.Append(IsNondisplay(snapshot.Attributes[i]) ? " " : DisplayCharacter(snapshot.Buffer[i]));
            }
            lines.Add(line.ToString());
        }
        return lines;
    }

    /// <summary>A field's current contents, with nulls removed.</summary>
    public static string FieldText(ScreenSnapshot snapshot, SnapshotField field)
    {
        var start = (field.Row - 1) * snapshot.Columns + (field.Column - 1);
        var bytes = JsArrays.Sub(snapshot.Buffer, start, start + field.Length).ToArray().Where(b => b != 0).ToArray();
        return Ebcdic.Decode(bytes);
    }
}
