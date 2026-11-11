// 5250 display commands and WRITE TO DISPLAY orders (IBM 5494 Functions Reference
// SC30-3533-04, chapter 15). Each command starts with ESC; a WTD runs to the next ESC.

namespace FakeIbmI.Tests.Client;

public static class Command
{
    public const int ClearUnit = 0x40;
    public const int ClearUnitAlternate = 0x20;
    public const int ClearFormatTable = 0x50;
    public const int WriteToDisplay = 0x11;
    public const int WriteErrorCode = 0x21;
    public const int WriteErrorCodeToWindow = 0x22;
    public const int ReadInputFields = 0x42;
    public const int ReadMdtFields = 0x52;
    public const int ReadMdtAlternate = 0x82;
    public const int ReadScreen = 0x62;
    public const int ReadImmediate = 0x72;
    public const int ReadImmediateAlternate = 0x83;
    public const int SaveScreen = 0x02;
    public const int SavePartialScreen = 0x03;
    public const int RestoreScreen = 0x12;
    public const int RestorePartialScreen = 0x13;
    public const int Roll = 0x23;
    public const int WriteStructuredField = 0xf3;
}

/// <summary>Something the host asked for that the connection answers once the record is applied.</summary>
public abstract record HostRequest
{
    public abstract string Type { get; }

    public sealed record Query : HostRequest
    {
        public override string Type => "query";
    }

    public sealed record CancelInvite : HostRequest
    {
        public override string Type => "cancel-invite";
    }

    /// <summary>A READ arrived while an AID key was waiting for one: the reply goes now.</summary>
    public sealed record Read : HostRequest
    {
        public override string Type => "read";
    }

    /// <summary>SAVE SCREEN or SAVE PARTIAL SCREEN, with the state taken when the command was reached.</summary>
    public sealed record Save(bool Partial, byte[] Saved) : HostRequest
    {
        public override string Type => "save";

        public bool Equals(Save? other) =>
            other is not null && Partial == other.Partial && Saved.AsSpan().SequenceEqual(other.Saved);

        public override int GetHashCode() => HashCode.Combine(Partial, Saved.Length);
    }

    public sealed record ReadScreen : HostRequest
    {
        public override string Type => "read-screen";
    }

    /// <summary>READ IMMEDIATE or READ MODIFIED IMMEDIATE ALTERNATE: no AID key involved.</summary>
    public sealed record ReadImmediate(int Command) : HostRequest
    {
        public override string Type => "read-immediate";
    }

    /// <summary>The record was rejected from this point on; the code is from table 13-26.</summary>
    public sealed record NegativeResponse(int Code) : HostRequest
    {
        public override string Type => "negative-response";
    }
}

/// <summary>Where saved-screen data sits in a record's data.</summary>
public readonly record struct DataRange(int Start, int End);

public sealed class ApplyResult
{
    public List<ScreenEvent> Events { get; } = [];

    /// <summary>The screen or keyboard changed, so a new snapshot is due.</summary>
    public bool Changed { get; set; }

    public List<HostRequest> Requests { get; } = [];

    /// <summary>Where saved-screen data sits in the record's data, so a recording can blank it.</summary>
    public List<DataRange> Restores { get; } = [];
}

/// <summary>A data stream error, with the negative response code a 5494 would send for it.</summary>
internal sealed class StreamError(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

public static class DataStream
{
    private const byte Esc = 0x04;

    private static class Order
    {
        public const byte Soh = 0x01;
        public const byte Ra = 0x02;
        public const byte Ea = 0x03;
        public const byte Td = 0x10;
        public const byte Sba = 0x11;
        public const byte Wea = 0x12;
        public const byte Ic = 0x13;
        public const byte Mc = 0x14;
        public const byte Wdsf = 0x15;
        public const byte Sf = 0x1d;
    }

    private static string Hex(int b) => $"0x{b:x2}";

    /// <summary>Applies one host record to the screen. `wide` says whether 27 x 132 is allowed.</summary>
    public static ApplyResult ApplyRecord(Screen screen, HostRecord record, bool wide)
    {
        var result = new ApplyResult();
        switch (record.Opcode)
        {
            case Opcode.MessageLightOn:
            case Opcode.MessageLightOff:
                screen.MessageWaiting = record.Opcode == Opcode.MessageLightOn;
                result.Changed = true;
                return result;
            case Opcode.CancelInvite:
                // The host takes back its READ; the client answers with a Cancel Invite (RFC 1205 4.2).
                screen.PendingRead = null;
                result.Requests.Add(new HostRequest.CancelInvite());
                result.Changed = true;
                return result;
            case Opcode.OutputOnly:
                // After a System Request the host may send 2 bytes here that aren't commands.
                if ((record.Flags & RecordFlag.Srq) != 0) return result;
                break;
        }
        // Other opcodes (Put/Get, Save, Restore, Read Immediate, Read Screen) carry commands.
        // At an error the 5494 keeps what it has done, rejects the rest of the record and sends a
        // negative response (page 15-8). A READ later in the record is never reached.
        try
        {
            ApplyCommands(screen, record.Data, wide, result);
        }
        catch (StreamError error)
        {
            result.Events.Add(new ScreenEvent.DataStreamError(error.Message));
            result.Requests.Add(new HostRequest.NegativeResponse(error.Code));
        }
        return result;
    }

    private static void ApplyCommands(Screen screen, byte[] data, bool wide, ApplyResult result)
    {
        var reader = new Reader(data);

        // SAVE SCREEN, READ SCREEN and the immediate reads must end the record. The 5494 rejects
        // them otherwise (15-107, 15-113) but names no code.
        void MustBeLast(string name)
        {
            if (!reader.Done)
            {
                throw new StreamError(NegativeResponse.CommandNotValid, $"{name} must be the last command in the record");
            }
        }

        while (!reader.Done)
        {
            var escape = reader.Byte();
            if (escape != Esc) throw new StreamError(NegativeResponse.NoEscape, $"expected ESC but found {Hex(escape)}");
            var command = reader.Byte();
            switch (command)
            {
                case Command.ClearUnit:
                    screen.Clear(24, 80);
                    result.Changed = true;
                    break;
                case Command.ClearUnitAlternate:
                {
                    var parameter = reader.Byte();
                    if (parameter == 0x00 && wide) screen.Clear(27, 132);
                    else if (parameter == 0x80) screen.Clear(screen.Rows, screen.Columns);
                    else
                        // 27 x 132 on a display without it is a command that is not valid (page 15-5).
                        throw new StreamError(
                            parameter == 0x00 ? NegativeResponse.CommandNotValid : NegativeResponse.ClearUnitAlternateNotValid,
                            $"CLEAR UNIT ALTERNATE parameter {Hex(parameter)} is not valid here");
                    result.Changed = true;
                    break;
                }
                case Command.ClearFormatTable:
                    screen.ClearFormatTable();
                    result.Changed = true;
                    break;
                case Command.WriteToDisplay:
                    WriteToDisplay(screen, reader, result);
                    result.Changed = true;
                    break;
                case Command.WriteErrorCode:
                case Command.WriteErrorCodeToWindow:
                    WriteErrorCode(screen, reader, command == Command.WriteErrorCodeToWindow);
                    // No alarm: ACS doesn't sound one for a host error, unlike a keying error.
                    result.Changed = true;
                    break;
                case Command.Roll:
                    Roll(screen, reader.Bytes(3));
                    result.Changed = true;
                    break;
                case Command.ReadInputFields:
                case Command.ReadMdtFields:
                case Command.ReadMdtAlternate:
                {
                    // The control character applies once the reply to the READ has been sent.
                    var cc = reader.Bytes(2);
                    screen.PendingRead = command;
                    screen.ReadControl = (cc[0], cc[1]);
                    if (screen.HeldAid is not null) result.Requests.Add(new HostRequest.Read());
                    result.Changed = true;
                    break;
                }
                case Command.ReadScreen:
                    MustBeLast("READ SCREEN");
                    result.Requests.Add(new HostRequest.ReadScreen());
                    break;
                case Command.ReadImmediate:
                case Command.ReadImmediateAlternate:
                    MustBeLast("READ IMMEDIATE");
                    result.Requests.Add(new HostRequest.ReadImmediate(command));
                    break;
                case Command.SaveScreen:
                    MustBeLast("SAVE SCREEN");
                    result.Requests.Add(new HostRequest.Save(false, SavedScreens.Encode(screen)));
                    break;
                case Command.SavePartialScreen:
                    // Flag, top row, left column, depth, width: the 5494 saves the whole screen anyway.
                    reader.Bytes(5);
                    result.Requests.Add(new HostRequest.Save(true, SavedScreens.Encode(screen)));
                    break;
                case Command.RestoreScreen:
                {
                    // The saved data runs to the end of the record (15-116).
                    var start = reader.Offset;
                    Restore(screen, reader.Bytes(data.Length - start), result);
                    result.Restores.Add(new DataRange(start, data.Length));
                    break;
                }
                case Command.RestorePartialScreen:
                {
                    var lengthBytes = reader.Bytes(2);
                    var length = (lengthBytes[0] << 8) | lengthBytes[1]; // Counts its own 2 bytes.
                    if (length < 2)
                    {
                        throw new StreamError(
                            NegativeResponse.RestoreDataNotValid,
                            $"RESTORE PARTIAL SCREEN length {length} is not valid");
                    }
                    var start = reader.Offset;
                    Restore(screen, reader.Bytes(length - 2), result);
                    result.Restores.Add(new DataRange(start, start + length - 2));
                    break;
                }
                case Command.WriteStructuredField:
                {
                    var field = reader.PeekBytes(4);
                    var length = (field[0] << 8) | field[1];
                    if (field[2] == 0xd9 && field[3] == 0x70)
                    {
                        reader.Bytes(length); // The 5250 QUERY: the connection answers it.
                        result.Requests.Add(new HostRequest.Query());
                        break;
                    }
                    // Others, such as QUERY STATION STATE, would leave the host waiting for a reply.
                    result.Events.Add(new ScreenEvent.Unsupported($"structured field {Hex(field[2])} {Hex(field[3])}"));
                    result.Requests.Add(new HostRequest.NegativeResponse(NegativeResponse.StructuredFieldNotValid));
                    return;
                }
                default:
                    // Also valid commands the engine lacks, such as READ SCREEN TO PRINT: controllers
                    // without one reject it the same way (table 15-1, note 1).
                    result.Events.Add(new ScreenEvent.Unsupported($"command {Hex(command)}"));
                    result.Requests.Add(new HostRequest.NegativeResponse(NegativeResponse.CommandNotValid));
                    return;
            }
        }
    }

    /// <summary>Saved data that can't be decoded is rejected, as a 5494 does, and the screen stays.</summary>
    private static void Restore(Screen screen, byte[] bytes, ApplyResult result)
    {
        var saved = SavedScreens.Decode(bytes, out var error);
        if (saved is null)
        {
            throw new StreamError(NegativeResponse.RestoreDataNotValid, $"saved screen not restored: {error}");
        }
        SavedScreens.Restore(screen, saved);
        result.Changed = true;
    }

    /// <summary>
    /// WRITE ERROR CODE (15-103): the host's message on the error line, in the pre-help
    /// error state. Clearing the error restores the line. TO WINDOW limits it to a range of
    /// columns.
    /// </summary>
    private static void WriteErrorCode(Screen screen, Reader reader, bool toWindow)
    {
        var first = 0;
        var end = screen.Columns;
        if (toWindow)
        {
            var columns = reader.Bytes(2);
            int startColumn = columns[0], endColumn = columns[1];
            if (screen.ErrorRow > screen.Rows)
            {
                throw new StreamError(
                    NegativeResponse.ErrorWindowLineInUse,
                    "WRITE ERROR CODE TO WINDOW cannot use the separate message line");
            }
            if (startColumn < 1 || endColumn <= startColumn || endColumn > screen.Columns)
            {
                throw new StreamError(
                    NegativeResponse.ErrorWindowNotValid,
                    $"error window columns {startColumn} to {endColumn} are not valid");
            }
            first = startColumn - 1;
            end = endColumn;
        }
        var message = new List<byte>();
        while (!reader.Done && reader.Peek() != Esc)
        {
            var b = reader.Byte();
            if (b == Order.Ic)
            {
                // Moves the cursor now, without changing the IC address the WTD set.
                var address = reader.Bytes(2);
                screen.Cursor = Address(screen, address[0], address[1]);
            }
            else
            {
                message.Add(b);
            }
        }
        screen.SavedErrorLine ??= screen.ReadErrorLine();
        var line = screen.ReadErrorLine();
        JsArrays.Fill(line, 0, first, end);
        var text = message.Take(end - first).ToArray();
        text.CopyTo(line, first);
        screen.WriteErrorLine(line);
        // It clears a locked keyboard unless Help was pressed, so clearing the error leaves it
        // unlocked.
        if (screen.Error?.Help != true) screen.KeyboardLocked = false;
        screen.Error = new ErrorState(ErrorKind.Host, 0, false);
        screen.InsertMode = false;
        screen.FieldExitPending = false;
        screen.HeldAid = null;
        screen.TypeAhead = [];
    }

    /// <summary>ROLL (15-117): moves lines up or down within an area. Vacated lines keep their contents.</summary>
    private static void Roll(Screen screen, byte[] parameters)
    {
        int parameter = parameters[0], top = parameters[1], bottom = parameters[2];
        var down = (parameter & 0x80) != 0;
        var count = parameter & 0x1f;
        if (top == 0 || bottom > screen.Rows || top >= bottom || count > bottom - top)
        {
            throw new StreamError(NegativeResponse.RollNotValid, $"ROLL of {count} lines from {top} to {bottom} is not valid");
        }
        var buffer = screen.Buffer;
        var columns = screen.Columns;
        int Row(int n) => (n - 1) * columns;
        if (down)
        {
            for (var r = bottom; r >= top + count; r--) JsArrays.CopyWithin(buffer, Row(r), Row(r - count), Row(r - count + 1));
        }
        else
        {
            for (var r = top; r <= bottom - count; r++) JsArrays.CopyWithin(buffer, Row(r), Row(r + count), Row(r + count + 1));
        }
    }

    private static void WriteToDisplay(Screen screen, Reader reader, ApplyResult result)
    {
        var cc = reader.Bytes(2);
        int cc0 = cc[0], cc1 = cc[1];
        screen.ApplyControlByte0(cc0);
        int? moveCursor = null;

        while (!reader.Done && reader.Peek() != Esc)
        {
            var b = reader.Byte();
            switch (b)
            {
                case Order.Sba:
                {
                    var a = reader.Bytes(2);
                    int row = a[0], column = a[1];
                    // Row 1, column 0 is allowed only when an SF follows: its attribute sits off-screen.
                    if (row == 1 && column == 0 && reader.Peek() == Order.Sf) screen.Address = -1;
                    else screen.Address = Address(screen, row, column);
                    break;
                }
                case Order.Ic:
                {
                    var a = reader.Bytes(2);
                    screen.InsertCursor = Address(screen, a[0], a[1]);
                    moveCursor = null; // An IC negates earlier MC orders.
                    break;
                }
                case Order.Mc:
                {
                    var a = reader.Bytes(2);
                    moveCursor = Address(screen, a[0], a[1]);
                    break;
                }
                case Order.Ra:
                {
                    var a = reader.Bytes(3);
                    var target = Address(screen, a[0], a[1]);
                    var character = a[2];
                    if (target < screen.Address)
                    {
                        throw new StreamError(
                            NegativeResponse.AddressBeforeCurrent,
                            "repeat to address is before the current address");
                    }
                    // Repeats up to and including the target address.
                    for (var count = target - screen.Address + 1; count > 0; count--) screen.Put(character);
                    break;
                }
                case Order.Ea:
                {
                    var a = reader.Bytes(3);
                    int row = a[0], column = a[1], length = a[2];
                    // The manual lists no codes for EA (page 15-13): these are the nearest ones.
                    if (length < 2 || length > 5)
                    {
                        throw new StreamError(
                            NegativeResponse.ExtendedAttributeTypeNotValid,
                            $"erase to address length {length} is not valid");
                    }
                    var types = reader.Bytes(length - 1);
                    var target = Address(screen, row, column);
                    if (target < screen.Address)
                    {
                        throw new StreamError(
                            NegativeResponse.AddressBeforeCurrent,
                            "erase to address is before the current address");
                    }
                    // Type 00 (display screen) or FF (everything) clears characters; extended
                    // attribute types have nothing to clear, since none are supported.
                    var clearsScreen = types.Contains((byte)0x00) || types.Contains((byte)0xff);
                    if (clearsScreen) JsArrays.Fill(screen.Buffer, 0, Math.Max(0, screen.Address), target + 1);
                    screen.Address = (target + 1) % screen.Size;
                    break;
                }
                case Order.Soh:
                {
                    var length = reader.Byte();
                    if (length < 1 || length > 7)
                    {
                        throw new StreamError(
                            NegativeResponse.HeaderLengthNotValid,
                            $"start of header length {length} is not valid");
                    }
                    // SOH clears the format table first, then keeps the header (error row, PF masks).
                    screen.StartHeader(reader.Bytes(length));
                    break;
                }
                case Order.Td:
                {
                    var a = reader.Bytes(2);
                    foreach (var data in reader.Bytes((a[0] << 8) | a[1])) screen.Put(data);
                    break;
                }
                case Order.Wea:
                    // Extended attributes are not declared in the QUERY reply, so they are ignored.
                    reader.Bytes(2);
                    break;
                case Order.Wdsf:
                {
                    var header = reader.PeekBytes(4);
                    var length = (header[0] << 8) | header[1];
                    result.Events.Add(new ScreenEvent.Unsupported($"WDSF structured field {Hex(header[2])} {Hex(header[3])}"));
                    reader.Bytes(length);
                    break;
                }
                case Order.Sf:
                    StartOfField(screen, reader);
                    break;
                default:
                    // Every other byte is data, even below 0x20 (a source line in SEU can hold one).
                    // Only X'FF' gets a negative response (1005 0142, table 13-26).
                    if (b == 0xff) throw new StreamError(NegativeResponse.DataNotValid, "0xff is not a valid data character");
                    screen.Put(b);
                    break;
            }
        }

        // Control character byte 1 (table 15-3).
        if (screen.ApplyControlByte1(cc1)) result.Events.Add(new ScreenEvent.Alarm());
        if (moveCursor is int cursor) screen.Cursor = cursor;
    }

    private static void StartOfField(Screen screen, Reader reader)
    {
        int? ffw = null;
        var fcws = new List<int>();
        // The first two bits say what follows: 01 is a field format word.
        if ((reader.Peek() & 0xc0) == 0x40)
        {
            var word = reader.Bytes(2);
            ffw = (word[0] << 8) | word[1];
            // Field control words start with bit 0 on.
            while (!reader.Done && (reader.Peek() & 0x80) != 0)
            {
                var fcw = reader.Bytes(2);
                fcws.Add((fcw[0] << 8) | fcw[1]);
            }
        }
        var attribute = reader.Byte();
        if ((attribute & 0xe0) != 0x20)
        {
            throw new StreamError(NegativeResponse.FieldAttributeNotValid, $"start of field attribute {Hex(attribute)} is not valid");
        }
        var lengthBytes = reader.Bytes(2);
        var error = screen.StartField(ffw, fcws, attribute, (lengthBytes[0] << 8) | lengthBytes[1]);
        if (error is { } e) throw new StreamError(e.Code, e.Message);
    }

    private static int Address(Screen screen, int row, int column) =>
        screen.Index(row, column)
        ?? throw new StreamError(NegativeResponse.AddressNotValid, $"row {row}, column {column} is off the screen");

    private sealed class Reader(byte[] data)
    {
        public int Offset { get; private set; }

        public bool Done => Offset >= data.Length;

        public byte Peek()
        {
            if (Done) throw new StreamError(NegativeResponse.PrematureEnd, "record ended in the middle of an order");
            return data[Offset];
        }

        public byte[] PeekBytes(int count)
        {
            if (Offset + count > data.Length)
            {
                throw new StreamError(NegativeResponse.PrematureEnd, "record ended in the middle of an order");
            }
            return data.AsSpan(Offset, count).ToArray();
        }

        public byte Byte()
        {
            var value = Peek();
            Offset++;
            return value;
        }

        public byte[] Bytes(int count)
        {
            var value = PeekBytes(count);
            Offset += count;
            return value;
        }
    }
}
