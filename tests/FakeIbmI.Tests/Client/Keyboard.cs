// The operator's side of the display: keystrokes against the format table (IBM 5494
// Functions Reference SC30-3533-04, tables 15-6 and 15-7, and chapter 16). No I/O: an
// AID key that needs the host comes back as an action for the connection to send.
//
// Operator error codes follow the 5250 numbering (0005 cursor in a protected area, and
// so on). They are checked against ACS on the test system before anyone relies on them.

using System.Globalization;
using System.Text;

namespace FakeIbmI.Tests.Client;

/// <summary>A keystroke.</summary>
public abstract record Key
{
    /// <summary>The TS key type: character, function or cursor.</summary>
    public abstract string Type { get; }
}

/// <summary>A character typed into a field.</summary>
public sealed record CharacterKey(string Char) : Key
{
    public override string Type => "character";
}

/// <summary>A named key: an AID key, an edit key or a signal key (see <see cref="Keyboard.KeyNames"/>).</summary>
public sealed record FunctionKey(string Name) : Key
{
    public override string Type => "function";
}

/// <summary>Puts the cursor at a position, as a click does. 1-based.</summary>
public sealed record CursorKey(int Row, int Column) : Key
{
    public override string Type => "cursor";
}

public static class OperatorError
{
    public const int DataNotAllowed = 4;
    public const int CursorProtected = 5;
    public const int MandatoryEnterExit = 7;
    public const int AlphaOnly = 8;
    public const int NumericOnly = 9;
    public const int DigitsOnly = 10;
    public const int SignPosition = 11;
    public const int NoRoom = 12;
    public const int MandatoryFill = 14;
    public const int FieldMinus = 16;
    public const int ExitKeyRequired = 18;
    public const int DupNotAllowed = 19;
    public const int EnterNotAllowed = 20;
    public const int MandatoryEnter = 21;
    public const int LowOrderNotDigit = 26;
}

public abstract record KeyAction
{
    public abstract string Type { get; }

    /// <summary>An AID key with a READ pending: the connection sends the reply now.</summary>
    public sealed record Aid(int Code) : KeyAction
    {
        public override string Type => "aid";
    }

    public sealed record Attention : KeyAction
    {
        public override string Type => "attention";
    }

    public sealed record SystemRequest : KeyAction
    {
        public override string Type => "system-request";
    }

    /// <summary>Help in an error state: the error code goes to the host (the HLP flag).</summary>
    public sealed record Help(byte[] Code) : KeyAction
    {
        public override string Type => "help";

        public bool Equals(Help? other) => other is not null && Code.AsSpan().SequenceEqual(other.Code);

        public override int GetHashCode() => Code.Length;
    }
}

public sealed class KeyResult
{
    /// <summary>The screen or keyboard state changed, so a new snapshot is due.</summary>
    public bool Changed { get; set; }

    public bool Alarm { get; set; }
    public List<KeyAction> Actions { get; } = [];
}

public static class Keyboard
{
    public static readonly IReadOnlyList<string> FunctionKeys =
        Enumerable.Range(1, 24).Select(n => $"F{n}").ToArray();

    /// <summary>AID codes (table 16-1), by key name.</summary>
    public static readonly IReadOnlyDictionary<string, int> AidCodes = BuildAidCodes();

    /// <summary>Keys that send an attention identifier (AID) to the host.</summary>
    public static readonly IReadOnlySet<string> AidKeyNames = AidCodes.Keys.ToHashSet();

    public static readonly IReadOnlySet<string> EditKeyNames = new HashSet<string>
    {
        "FieldExit",
        "FieldPlus",
        "FieldMinus",
        "Dup",
        "EraseEof",
        "EraseInput",
        "Insert",
        "Delete",
        // Moves left and deletes the character there, within a field.
        "Backspace",
        "Home",
        "End",
        "Tab",
        "Backtab",
        "NewLine",
        "Left",
        "Right",
        "Up",
        "Down",
        "Reset",
    };

    public static readonly IReadOnlySet<string> SignalKeyNames = new HashSet<string> { "Attention", "SystemRequest" };

    /// <summary>Every key name a <see cref="FunctionKey"/> may carry.</summary>
    public static readonly IReadOnlySet<string> KeyNames =
        AidKeyNames.Concat(EditKeyNames).Concat(SignalKeyNames).ToHashSet();

    /// <summary>True for a known key name. Use it to validate key names from untrusted input.</summary>
    public static bool IsKeyName(string name) => KeyNames.Contains(name);

    private static Dictionary<string, int> BuildAidCodes()
    {
        var codes = new Dictionary<string, int>
        {
            ["Enter"] = 0xf1,
            ["Help"] = 0xf3,
            ["RollDown"] = 0xf4,
            ["RollUp"] = 0xf5,
            ["Print"] = 0xf6,
            ["RecordBackspace"] = 0xf8,
            ["Clear"] = 0xbd,
        };
        for (var i = 0; i < 24; i++) codes[$"F{i + 1}"] = i < 12 ? 0x31 + i : 0xb1 + i - 12;
        return codes;
    }

    private static int Aid(string name) => AidCodes[name];

    /// <summary>Keys a field-exit-required field accepts while the cursor waits on its last position.</summary>
    private static readonly HashSet<string> FieldExitKeys =
        new[] { "FieldExit", "FieldPlus", "FieldMinus", "Tab", "Backtab", "Reset" }.Concat(AidKeyNames).ToHashSet();

    private const byte Blank = 0x40;
    private const byte DupCharacter = 0x1c;
    private const byte Minus = 0x60;
    private const byte Zero = 0xf0;

    private static string? NameOf(Key key) => key is FunctionKey f ? f.Name : null;

    /// <summary>Handles one keystroke in whatever state the keyboard is in.</summary>
    public static KeyResult HandleKey(Screen screen, Key key)
    {
        var output = new KeyResult();
        var name = NameOf(key);
        // Attn works in every state and changes nothing on the display (page 16-8).
        if (name == "Attention")
        {
            output.Actions.Add(new KeyAction.Attention());
            return output;
        }
        // A key that clears an error goes on in the state the keyboard was in before it.
        if (screen.KeyboardState == KeyboardState.Error && !ErrorStateKey(screen, name, output)) return output;
        switch (screen.KeyboardState)
        {
            case KeyboardState.Error:
                break; // Not reached: the error was cleared above.
            case KeyboardState.Locked:
                if (name == "SystemRequest")
                {
                    output.Actions.Add(new KeyAction.SystemRequest());
                }
                else if (name == "Reset")
                {
                    // Reset empties the type-ahead buffer (page 16-13); the host still has the keyboard.
                    output.Changed = screen.TypeAhead.Count > 0;
                    screen.TypeAhead = [];
                }
                else if (screen.TypeAhead.Count >= Screen.TypeAheadLimit)
                {
                    output.Alarm = true;
                }
                else
                {
                    screen.TypeAhead.Add(key);
                    output.Changed = true;
                }
                break;
            case KeyboardState.Unlocked:
                UnlockedKey(screen, key, output);
                break;
        }
        return output;
    }

    /// <summary>Processes keys typed ahead while the keyboard was locked, until it locks again.</summary>
    public static KeyResult DrainTypeAhead(Screen screen)
    {
        var output = new KeyResult();
        while (screen.KeyboardState == KeyboardState.Unlocked && screen.TypeAhead.Count > 0)
        {
            var key = screen.TypeAhead[0];
            screen.TypeAhead.RemoveAt(0);
            UnlockedKey(screen, key, output);
            output.Changed = true;
        }
        return output;
    }

    /// <summary>
    /// True when this AID sends field data: Enter, the Roll keys, and PF keys whose bit in
    /// SOH bytes 5–7 is off (page 15-13). The master MDT must also be on for data to go.
    /// </summary>
    public static bool AidSendsData(Screen screen, int aid)
    {
        if (aid == Aid("Enter") || aid == Aid("RollUp") || aid == Aid("RollDown")) return true;
        var pf = aid >= 0x31 && aid <= 0x3c ? aid - 0x30 : aid >= 0xb1 && aid <= 0xbc ? aid - 0xb0 + 12 : 0;
        if (pf == 0) return false;
        var header = screen.Header;
        if (header.Length < 7) return true;
        // Our header array starts at the flag byte, so the manual's byte n is header[n - 1].
        var (mask, bit) = pf >= 17 ? (header[4], pf - 17) : pf >= 9 ? (header[5], pf - 9) : (header[6], pf - 1);
        return (mask & (1 << bit)) == 0;
    }

    /// <summary>
    /// A key in an operator or host error. Returns true when the key cleared the error and
    /// should now do its normal job.
    ///
    /// This follows ACS, not the 5494, which ignores every key but Reset, Help and Attn
    /// (page 16-3). In ACS any key except Help, Attn and SysReq clears the error, so Tab after
    /// 0005 moves to the next field and Page Down after a host error sends Roll Up at once.
    /// (tn5250's `uninhibited` option does this for fewer keys.)
    /// </summary>
    private static bool ErrorStateKey(Screen screen, string? name, KeyResult output)
    {
        var error = screen.Error;
        if (error is null) return true;
        // Between Help and the host's answer only Attn works (page 16-3), and Attn came first.
        if (error.Help || name == "SystemRequest")
        {
            output.Alarm = true;
            return false;
        }
        if (name == "Help")
        {
            output.Actions.Add(new KeyAction.Help(HelpCode(screen)));
            screen.Error = error with { Help = true };
            output.Changed = true;
            return false;
        }
        screen.ClearError();
        output.Changed = true;
        return name != "Reset";
    }

    /// <summary>
    /// The code sent with Help in an error state: the operator error as packed decimal
    /// (0010 is 00 10), or after a host error the dezoned, packed columns 2–5 of the error line.
    /// </summary>
    private static byte[] HelpCode(Screen screen)
    {
        int[] digits = screen.Error?.Kind == ErrorKind.Operator
            ? screen.Error.Code.ToString(CultureInfo.InvariantCulture).PadLeft(4, '0').Select(c => c - '0').ToArray()
            : screen.ReadErrorLine().AsSpan(1, 4).ToArray().Select(b => b & 0x0f).ToArray();
        return [(byte)((digits[0] << 4) | digits[1]), (byte)((digits[2] << 4) | digits[3])];
    }

    private static void UnlockedKey(Screen screen, Key key, KeyResult output)
    {
        var name = NameOf(key);
        if (screen.FieldExitPending)
        {
            // The cursor waits on the last position of a field exit required field.
            if (name is "Left" or "Backspace")
            {
                screen.FieldExitPending = false;
                output.Changed = true;
                return;
            }
            if (key is CursorKey || name is "Up" or "Down" or "Right")
            {
                screen.FieldExitPending = false;
            }
            else if (name is null || !FieldExitKeys.Contains(name))
            {
                OperatorErrorPosted(screen, OperatorError.ExitKeyRequired, output);
                return;
            }
        }
        switch (key)
        {
            case CharacterKey character:
                TypeCharacter(screen, character.Char, output);
                return;
            case CursorKey cursor:
                if (screen.Index(cursor.Row, cursor.Column) is int index) MoveCursor(screen, index, output);
                return;
            case FunctionKey function when AidCodes.TryGetValue(function.Name, out var aid):
                AidKey(screen, aid, output);
                return;
            case FunctionKey function:
                EditKey(screen, function.Name, output);
                return;
        }
    }

    private static void EditKey(Screen screen, string name, KeyResult output)
    {
        var size = screen.Size;
        switch (name)
        {
            case "SystemRequest":
                output.Actions.Add(new KeyAction.SystemRequest());
                return;
            case "Attention":
                return; // Handled before the keyboard state.
            case "FieldExit":
                FieldExit(screen, ExitVariant.Exit, output);
                return;
            case "FieldPlus":
                FieldExit(screen, ExitVariant.Plus, output);
                return;
            case "FieldMinus":
                FieldExit(screen, ExitVariant.Minus, output);
                return;
            case "Dup":
                Dup(screen, output);
                return;
            case "EraseEof":
                EraseToEnd(screen, output);
                return;
            case "EraseInput":
                EraseInput(screen, output);
                return;
            case "Insert":
                screen.InsertMode = !screen.InsertMode;
                output.Changed = true;
                return;
            case "Delete":
                DeleteCharacter(screen, output);
                return;
            case "Backspace":
                Backspace(screen, output);
                return;
            case "Reset":
                screen.InsertMode = false;
                screen.FieldExitPending = false;
                output.Changed = true;
                return;
            case "Home":
                // Already home, Home is Record Backspace (page 16-10); ACS sends it too.
                if (screen.Cursor == screen.HomePosition()) AidKey(screen, Aid("RecordBackspace"), output);
                else MoveCursor(screen, screen.HomePosition(), output);
                return;
            case "End":
                EndOfField(screen, output);
                return;
            case "Tab":
                FieldAdvance(screen, output);
                return;
            case "Backtab":
                FieldBackspace(screen, output);
                return;
            case "NewLine":
                NewLine(screen, output);
                return;
            case "Left":
                MoveCursor(screen, (screen.Cursor - 1 + size) % size, output);
                return;
            case "Right":
                MoveCursor(screen, (screen.Cursor + 1) % size, output);
                return;
            case "Up":
                MoveCursor(screen, (screen.Cursor - screen.Columns + size) % size, output);
                return;
            case "Down":
                MoveCursor(screen, (screen.Cursor + screen.Columns) % size, output);
                return;
        }
    }

    private static void OperatorErrorPosted(Screen screen, int code, KeyResult output)
    {
        screen.Error = new ErrorState(ErrorKind.Operator, code, false);
        // An operator error empties the type-ahead buffer (page 16-14).
        screen.TypeAhead = [];
        output.Alarm = true;
        output.Changed = true;
    }

    /// <summary>The input field at the cursor, or an operator error if there is none.</summary>
    private static FieldEntry? InputFieldAtCursor(Screen screen, KeyResult output)
    {
        var field = screen.FieldAt(screen.Cursor);
        if (field is null || (field.Ffw & FieldFormat.FfwBypass) != 0)
        {
            OperatorErrorPosted(screen, OperatorError.CursorProtected, output);
            return null;
        }
        return field;
    }

    /// <summary>Positions that take data: all of them, except the sign position of a signed numeric field.</summary>
    private static int DataLength(FieldEntry field, FieldFormat format) =>
        format.Shift == "signed-numeric" ? field.Length - 1 : field.Length;

    private static bool IsEmpty(byte b) => b is 0 or Blank;

    private static void TypeCharacter(Screen screen, string input, KeyResult output)
    {
        var field = InputFieldAtCursor(screen, output);
        if (field is null) return;
        var format = FieldFormat.Decode(field.Ffw);
        // A string comparison, as in JavaScript: by UTF-16 code units.
        var lowercase = string.CompareOrdinal(input, "a") >= 0 && string.CompareOrdinal(input, "z") <= 0;
        var ch = format.Monocase && lowercase ? input.ToUpperInvariant() : input;
        var shiftError = CheckShift(format, ch);
        if (shiftError != 0)
        {
            OperatorErrorPosted(screen, shiftError, output);
            return;
        }
        if (ToEbcdic(ch) is not byte b)
        {
            output.Alarm = true;
            return;
        }
        var offset = screen.Cursor - field.Start;
        var last = field.Length - 1;
        if (format.Shift == "signed-numeric" && offset == last)
        {
            OperatorErrorPosted(screen, OperatorError.SignPosition, output);
            return;
        }
        var data = screen.Buffer.AsSpan(field.Start, field.Length);
        if (screen.InsertMode)
        {
            var end = DataLength(field, format) - 1;
            if (!IsEmpty(data[end]))
            {
                OperatorErrorPosted(screen, OperatorError.NoRoom, output);
                return;
            }
            JsArrays.CopyWithin(data, offset + 1, offset, end);
        }
        data[offset] = b;
        field.Ffw |= FieldFormat.FfwModified;
        screen.ActiveField = field.Start;
        output.Changed = true;
        if (offset < last)
        {
            screen.Cursor += 1;
        }
        else if (format.FieldExitRequired)
        {
            screen.FieldExitPending = true;
        }
        else
        {
            Adjust(screen, field, format);
            LeaveField(screen, format, output);
        }
    }

    private static int CheckShift(FieldFormat format, string ch)
    {
        static bool Single(string s, string allowed) => s.Length == 1 && allowed.Contains(s[0]);
        switch (format.Shift)
        {
            case "alpha-only":
                // A letter (one code point, as /^[\p{L},.\- ]$/u matches), comma, period, minus or space.
                return IsSingleLetter(ch) || Single(ch, ",.- ") ? 0 : OperatorError.AlphaOnly;
            case "numeric-only":
                return Single(ch, "0123456789+,.- ") ? 0 : OperatorError.NumericOnly;
            case "digits-only":
            case "signed-numeric":
                return Single(ch, "0123456789") ? 0 : OperatorError.DigitsOnly;
            case "io":
                return OperatorError.DataNotAllowed;
            default:
                return 0;
        }
    }

    private static bool IsSingleLetter(string text)
    {
        if (text.Length == 0 || Rune.DecodeFromUtf16(text, out var rune, out var used) != System.Buffers.OperationStatus.Done)
        {
            return false;
        }
        return used == text.Length && Rune.IsLetter(rune);
    }

    /// <summary>The EBCDIC byte for a typed character, or null if CCSID 37 has no displayable one.</summary>
    private static byte? ToEbcdic(string ch)
    {
        if (ch.Length != 1) return null;
        var b = Ebcdic.Encode(ch)[0];
        if (b < 0x40 || b == 0xff || (b == Ebcdic.Substitute && ch != "?")) return null;
        return b;
    }

    /// <summary>Right-adjusts a field as its format word says; signed numeric fields always adjust.</summary>
    private static void Adjust(Screen screen, FieldEntry field, FieldFormat format) =>
        RightAdjust(screen.Buffer.AsSpan(field.Start, field.Length), format, field);

    private static void RightAdjust(Span<byte> data, FieldFormat format, FieldEntry field)
    {
        byte? fill = format.Adjust == "right-zero"
            ? Zero
            : format.Adjust == "right-blank" || format.Shift == "signed-numeric"
                ? Blank
                : null;
        if (fill is not byte value) return;
        var length = DataLength(field, format);
        var first = 0;
        while (first < length && IsEmpty(data[first])) first++;
        var end = length;
        while (end > first && IsEmpty(data[end - 1])) end--;
        var content = data[first..end].ToArray();
        JsArrays.Fill(data, value, 0, length - content.Length);
        content.CopyTo(data[(length - content.Length)..]);
    }

    /// <summary>After a field is finished: auto-enter sends Enter, otherwise the cursor goes to the next field.</summary>
    private static void LeaveField(Screen screen, FieldFormat format, KeyResult output)
    {
        screen.ActiveField = null;
        if (format.AutoEnter)
        {
            AidKey(screen, Aid("Enter"), output);
            return;
        }
        var next = NextInputField(screen, screen.Cursor);
        if (next is not null) screen.Cursor = next.Start;
    }

    private enum ExitVariant
    {
        Exit,
        Plus,
        Minus,
    }

    private static void FieldExit(Screen screen, ExitVariant variant, KeyResult output)
    {
        var field = InputFieldAtCursor(screen, output);
        if (field is null) return;
        var format = FieldFormat.Decode(field.Ffw);
        var signed = format.Shift == "signed-numeric";
        if (variant == ExitVariant.Minus && !signed && format.Shift != "numeric-only")
        {
            OperatorErrorPosted(screen, OperatorError.FieldMinus, output);
            return;
        }
        if (format.MandatoryEnter && (field.Ffw & FieldFormat.FfwModified) == 0)
        {
            OperatorErrorPosted(screen, OperatorError.MandatoryEnterExit, output);
            return;
        }
        var length = DataLength(field, format);
        // With field exit required pending, the cursor sits on the last character typed: keep it.
        var from = screen.Cursor - field.Start + (screen.FieldExitPending ? 1 : 0);
        // Work on a copy so an error leaves the field as it was.
        var work = screen.Buffer.AsSpan(field.Start, field.Length).ToArray();
        if (format.Adjust == "mandatory-fill" && from > 0 && from < length)
        {
            if (work.AsSpan(0, from).ContainsAnyExcept((byte)0))
            {
                OperatorErrorPosted(screen, OperatorError.MandatoryFill, output);
                return;
            }
        }
        if (from < length) JsArrays.Fill(work, 0, from, length);
        RightAdjust(work, format, field);
        if (signed)
        {
            if (variant == ExitVariant.Minus) work[field.Length - 1] = Minus;
            else if (variant == ExitVariant.Plus) work[field.Length - 1] = Blank;
        }
        else if (variant == ExitVariant.Minus)
        {
            // Numeric only: the zone of the low-order digit becomes D.
            var low = field.Length - 1;
            while (low >= 0 && work[low] == 0) low--;
            if (low < 0 || work[low] < 0xf0 || work[low] > 0xf9)
            {
                OperatorErrorPosted(screen, OperatorError.LowOrderNotDigit, output);
                return;
            }
            work[low] = (byte)(0xd0 | (work[low] & 0x0f));
        }
        work.CopyTo(screen.Buffer, field.Start);
        field.Ffw |= FieldFormat.FfwModified;
        screen.FieldExitPending = false;
        output.Changed = true;
        LeaveField(screen, format, output);
    }

    private static void Dup(Screen screen, KeyResult output)
    {
        var field = InputFieldAtCursor(screen, output);
        if (field is null) return;
        var format = FieldFormat.Decode(field.Ffw);
        if (!format.DupEnable)
        {
            OperatorErrorPosted(screen, OperatorError.DupNotAllowed, output);
            return;
        }
        JsArrays.Fill(screen.Buffer, DupCharacter, screen.Cursor, field.Start + DataLength(field, format));
        field.Ffw |= FieldFormat.FfwModified;
        output.Changed = true;
        if (format.FieldExitRequired)
        {
            screen.Cursor = field.Start + field.Length - 1;
            screen.FieldExitPending = true;
            screen.ActiveField = field.Start;
            return;
        }
        Adjust(screen, field, format);
        LeaveField(screen, format, output);
    }

    private static void EraseToEnd(Screen screen, KeyResult output)
    {
        var field = InputFieldAtCursor(screen, output);
        if (field is null) return;
        JsArrays.Fill(screen.Buffer, 0, screen.Cursor, field.Start + field.Length);
        field.Ffw |= FieldFormat.FfwModified;
        output.Changed = true;
    }

    /// <summary>Clears every modified input field; their MDT stays on (page 16-9).</summary>
    private static void EraseInput(Screen screen, KeyResult output)
    {
        foreach (var field in screen.Fields)
        {
            if ((field.Ffw & FieldFormat.FfwBypass) == 0 && (field.Ffw & FieldFormat.FfwModified) != 0)
            {
                JsArrays.Fill(screen.Buffer, 0, field.Start, field.Start + field.Length);
            }
        }
        screen.ActiveField = null;
        var first = screen.Fields.Find(f => (f.Ffw & FieldFormat.FfwBypass) == 0);
        screen.Cursor = first?.Start ?? 0;
        output.Changed = true;
    }

    private static void DeleteCharacter(Screen screen, KeyResult output)
    {
        var field = InputFieldAtCursor(screen, output);
        if (field is null) return;
        var format = FieldFormat.Decode(field.Ffw);
        var end = field.Start + DataLength(field, format);
        if (screen.Cursor < end)
        {
            JsArrays.CopyWithin(screen.Buffer, screen.Cursor, screen.Cursor + 1, end);
            screen.Buffer[end - 1] = 0;
        }
        else
        {
            screen.Buffer[screen.Cursor] = 0;
        }
        field.Ffw |= FieldFormat.FfwModified;
        output.Changed = true;
    }

    private static void Backspace(Screen screen, KeyResult output)
    {
        var field = screen.FieldAt(screen.Cursor);
        if (field is null || (field.Ffw & FieldFormat.FfwBypass) != 0)
        {
            MoveCursor(screen, (screen.Cursor - 1 + screen.Size) % screen.Size, output);
            return;
        }
        if (screen.Cursor == field.Start)
        {
            // At the start of a field, go to the last position of the previous one.
            var previous = PreviousInputField(screen, field.Start);
            if (previous is not null) MoveCursor(screen, previous.Start + previous.Length - 1, output);
            return;
        }
        screen.Cursor -= 1;
        DeleteCharacter(screen, output);
    }

    private static void MoveCursor(Screen screen, int index, KeyResult output)
    {
        screen.Cursor = index;
        screen.FieldExitPending = false;
        output.Changed = true;
    }

    private static void EndOfField(Screen screen, KeyResult output)
    {
        var field = screen.FieldAt(screen.Cursor);
        if (field is null || (field.Ffw & FieldFormat.FfwBypass) != 0) return;
        var format = FieldFormat.Decode(field.Ffw);
        var end = DataLength(field, format);
        while (end > 0 && IsEmpty(screen.Buffer[field.Start + end - 1])) end--;
        MoveCursor(screen, field.Start + Math.Min(end, field.Length - 1), output);
    }

    /// <summary>True when a mandatory-fill field the operator is keying has some positions still empty.</summary>
    private static bool UnfinishedMandatoryFill(Screen screen, FieldEntry? field)
    {
        if (field is null || field.Start != screen.ActiveField) return false;
        var format = FieldFormat.Decode(field.Ffw);
        if (format.Adjust != "mandatory-fill") return false;
        var data = screen.Buffer.AsSpan(field.Start, DataLength(field, format));
        return data.ContainsAnyExcept((byte)0) && data.Contains((byte)0);
    }

    private static void FieldAdvance(Screen screen, KeyResult output)
    {
        if (UnfinishedMandatoryFill(screen, screen.FieldAt(screen.Cursor)))
        {
            OperatorErrorPosted(screen, OperatorError.MandatoryFill, output);
            return;
        }
        screen.ActiveField = null;
        var next = NextInputField(screen, screen.Cursor);
        MoveCursor(screen, next?.Start ?? screen.Cursor, output);
    }

    private static void FieldBackspace(Screen screen, KeyResult output)
    {
        var current = screen.FieldAt(screen.Cursor);
        var inInput = current is not null && (current.Ffw & FieldFormat.FfwBypass) == 0;
        if (inInput && screen.Cursor > current!.Start)
        {
            MoveCursor(screen, current.Start, output);
            return;
        }
        if (UnfinishedMandatoryFill(screen, current))
        {
            OperatorErrorPosted(screen, OperatorError.MandatoryFill, output);
            return;
        }
        screen.ActiveField = null;
        var previous = PreviousInputField(screen, inInput ? current!.Start : screen.Cursor);
        MoveCursor(screen, previous?.Start ?? screen.Cursor, output);
    }

    private static void NewLine(Screen screen, KeyResult output)
    {
        var nextRow = (screen.Cursor / screen.Columns + 1) * screen.Columns;
        var inputs = InputFields(screen);
        var target = inputs.Find(f => f.Start >= nextRow)?.Start ?? (inputs.Count > 0 ? inputs[0].Start : nextRow % screen.Size);
        screen.ActiveField = null;
        MoveCursor(screen, target, output);
    }

    private static List<FieldEntry> InputFields(Screen screen) =>
        screen.Fields.FindAll(f => (f.Ffw & FieldFormat.FfwBypass) == 0);

    /// <summary>The first input field starting after `index`, wrapping to the first one.</summary>
    private static FieldEntry? NextInputField(Screen screen, int index)
    {
        var inputs = InputFields(screen);
        return inputs.Find(f => f.Start > index) ?? inputs.FirstOrDefault();
    }

    /// <summary>The last input field starting before `index`, wrapping to the last one.</summary>
    private static FieldEntry? PreviousInputField(Screen screen, int index)
    {
        var inputs = InputFields(screen);
        return inputs.FindLast(f => f.Start < index) ?? inputs.LastOrDefault();
    }

    /// <summary>True for Enter and the command (PF) keys, which check the fields before they go (16-6, 16-8).</summary>
    private static bool ChecksFields(int aid) =>
        aid == Aid("Enter") || (aid >= 0x31 && aid <= 0x3c) || (aid >= 0xb1 && aid <= 0xbc);

    /// <summary>True when the cursor is in a right-adjust field the operator has begun keying.</summary>
    private static bool InActiveRightAdjust(Screen screen)
    {
        var field = screen.FieldAt(screen.Cursor);
        if (field is null || field.Start != screen.ActiveField) return false;
        var adjust = FieldFormat.Decode(field.Ffw).Adjust;
        return adjust is "right-zero" or "right-blank";
    }

    private static void AidKey(Screen screen, int aid, KeyResult output)
    {
        if (ChecksFields(aid))
        {
            // AID keys don't work in an active right-adjust field (page 16-5): Field Exit first.
            // ACS posts "Enter key not allowed in field" (0020) here.
            if (InActiveRightAdjust(screen))
            {
                OperatorErrorPosted(screen, OperatorError.EnterNotAllowed, output);
                return;
            }
            if (UnfinishedMandatoryFill(screen, screen.FieldAt(screen.Cursor)))
            {
                OperatorErrorPosted(screen, OperatorError.MandatoryFill, output);
                return;
            }
            if (screen.Modified)
            {
                var unentered = screen.Fields.Find(f =>
                    (f.Ffw & FieldFormat.FfwBypass) == 0
                    && (f.Ffw & FieldFormat.FfwModified) == 0
                    && FieldFormat.Decode(f.Ffw).MandatoryEnter);
                if (unentered is not null)
                {
                    screen.Cursor = unentered.Start;
                    OperatorErrorPosted(screen, OperatorError.MandatoryEnter, output);
                    return;
                }
            }
        }
        screen.KeyboardLocked = true;
        screen.InsertMode = false;
        screen.FieldExitPending = false;
        screen.ActiveField = null;
        output.Changed = true;
        // With no READ pending the AID waits for one (page 16-5).
        if (screen.PendingRead is null) screen.HeldAid = aid;
        else output.Actions.Add(new KeyAction.Aid(aid));
    }
}
