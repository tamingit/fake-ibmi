// SAVE SCREEN data. The 5494 manual leaves the format to the terminal and says the host
// must return it unchanged (SC30-3533-04 page 15-113), so the engine uses its own
// versioned layout: it holds keyboard and error state that a WTD stream can't express.
// Keep a decoder for every version ever shipped: the IBM i may hand a saved screen back
// after a page reload, to a newer client.
//
// Version 1, all numbers big-endian:
//   'G' 'H' version | rows columns | flags | cursor(2) insertCursor(2, FFFF none) | errorRow
//   pendingRead readCc0 readCc1 heldAid (0 none) | activeField(2, FFFF none)
//   errorKind(0 none, 1 operator, 2 host) errorCode(2) errorHelp
//   savedErrorLine: length(1, 0 none) bytes | header: length(1) bytes
//   messageLine(columns) | buffer(rows x columns)
//   fieldCount(2), then per field: start(2) length(2) ffw(2) attribute fcwCount fcws(2 each)
//
// The layout is byte-for-byte the TS engine's, so data saved by either can be restored by
// the other.

namespace FakeIbmI.Tests.Client;

/// <summary>Everything a restore puts back.</summary>
public sealed record SavedScreen(
    int Rows,
    int Columns,
    int Flags,
    int Cursor,
    int? InsertCursor,
    int ErrorRow,
    int? PendingRead,
    (int Cc0, int Cc1) ReadControl,
    int? HeldAid,
    int? ActiveField,
    ErrorState? Error,
    byte[]? SavedErrorLine,
    byte[] Header,
    byte[] MessageLine,
    byte[] Buffer,
    List<FieldEntry> Fields);

public static class SavedScreens
{
    private static readonly byte[] Magic = [0x47, 0x48];
    private const int Version = 1;
    private const int None = 0xffff;

    private static class Flag
    {
        public const int KeyboardLocked = 0x01;
        public const int InsertMode = 0x02;
        public const int FieldExitPending = 0x04;
        public const int DeferredUnlock = 0x08;
        public const int DeferredMovesCursor = 0x10;
    }

    public static byte[] Encode(Screen screen)
    {
        var output = new List<byte> { Magic[0], Magic[1], Version, (byte)screen.Rows, (byte)screen.Columns };
        void U16(int n)
        {
            output.Add((byte)(n >> 8));
            output.Add((byte)(n & 0xff));
        }
        var deferred = screen.DeferredUnlock;
        output.Add(
            (byte)((screen.KeyboardLocked ? Flag.KeyboardLocked : 0)
                | (screen.InsertMode ? Flag.InsertMode : 0)
                | (screen.FieldExitPending ? Flag.FieldExitPending : 0)
                | (deferred is not null ? Flag.DeferredUnlock : 0)
                | (deferred?.MoveCursor == true ? Flag.DeferredMovesCursor : 0)));
        U16(screen.Cursor);
        U16(screen.InsertCursor ?? None);
        output.Add((byte)screen.ErrorRow);
        output.Add((byte)(screen.PendingRead ?? 0));
        output.Add((byte)screen.ReadControl.Cc0);
        output.Add((byte)screen.ReadControl.Cc1);
        output.Add((byte)(screen.HeldAid ?? 0));
        U16(screen.ActiveField ?? None);
        var error = screen.Error;
        output.Add((byte)(error is null ? 0 : error.Kind == ErrorKind.Operator ? 1 : 2));
        U16(error?.Code ?? 0);
        output.Add((byte)(error?.Help == true ? 1 : 0));
        var saved = screen.SavedErrorLine;
        output.Add((byte)(saved?.Length ?? 0));
        if (saved is not null) output.AddRange(saved);
        output.Add((byte)screen.Header.Length);
        output.AddRange(screen.Header);
        output.AddRange(screen.MessageLine);
        output.AddRange(screen.Buffer);
        U16(screen.Fields.Count);
        foreach (var f in screen.Fields)
        {
            U16(f.Start);
            U16(f.Length);
            U16(f.Ffw);
            output.Add((byte)f.Attribute);
            output.Add((byte)f.Fcws.Count);
            foreach (var fcw in f.Fcws) U16(fcw);
        }
        return output.ToArray();
    }

    private sealed class Truncated() : Exception("it is truncated");

    /// <summary>Reads saved data, or returns null and why it can't be used.</summary>
    public static SavedScreen? Decode(byte[] bytes, out string error)
    {
        var (saved, reason) = DecodeOrExplain(bytes);
        error = reason;
        return saved;
    }

    private static (SavedScreen? Saved, string Error) DecodeOrExplain(byte[] bytes)
    {
        var offset = 0;
        void Need(int count)
        {
            if (offset + count > bytes.Length) throw new Truncated();
        }
        int U8()
        {
            Need(1);
            return bytes[offset++];
        }
        int U16() => (U8() << 8) | U8();
        byte[] Take(int count)
        {
            Need(count);
            offset += count;
            return bytes.AsSpan(offset - count, count).ToArray();
        }
        static (SavedScreen?, string) Fail(string reason) => (null, reason);

        try
        {
            if (bytes.Length < 2 || bytes[0] != Magic[0] || bytes[1] != Magic[1]) return Fail("it was not saved by this client");
            offset = 2;
            var version = U8();
            if (version != Version) return Fail($"version {version} is not known");
            var rows = U8();
            var columns = U8();
            if (!((rows == 24 && columns == 80) || (rows == 27 && columns == 132)))
            {
                return Fail($"screen size {rows} x {columns} is not valid");
            }
            var size = rows * columns;
            var flags = U8();
            var cursor = U16();
            var insertCursor = U16();
            var errorRow = U8();
            var pendingRead = U8();
            var readControl = (U8(), U8());
            var heldAid = U8();
            var activeField = U16();
            var errorKind = U8();
            var errorCode = U16();
            var errorHelp = U8();
            var savedLength = U8();
            var savedErrorLine = savedLength != 0 ? Take(savedLength) : null;
            var header = Take(U8());
            var messageLine = Take(columns);
            var buffer = Take(size);
            var fields = new List<FieldEntry>();
            for (var count = U16(); count > 0; count--)
            {
                var start = U16();
                var length = U16();
                var ffw = U16();
                var attribute = U8();
                var fcws = new List<int>();
                for (var n = U8(); n > 0; n--) fcws.Add(U16());
                if (start + length > size) return Fail("a field runs off the screen");
                fields.Add(new FieldEntry { Start = start, Length = length, Ffw = ffw, Fcws = fcws, Attribute = attribute });
            }
            if (offset != bytes.Length) return Fail("it has extra bytes");
            if (cursor >= size || (insertCursor != None && insertCursor >= size))
            {
                return Fail("the cursor is off the screen");
            }
            if (errorRow < 1 || errorRow > rows + 1) return Fail($"error row {errorRow} is not valid");
            var saved = new SavedScreen(
                Rows: rows,
                Columns: columns,
                Flags: flags,
                Cursor: cursor,
                InsertCursor: insertCursor == None ? null : insertCursor,
                ErrorRow: errorRow,
                PendingRead: pendingRead != 0 ? pendingRead : null,
                ReadControl: readControl,
                HeldAid: heldAid != 0 ? heldAid : null,
                ActiveField: activeField == None ? null : activeField,
                Error: errorKind == 0
                    ? null
                    : new ErrorState(errorKind == 1 ? ErrorKind.Operator : ErrorKind.Host, errorCode, errorHelp == 1),
                SavedErrorLine: savedErrorLine,
                Header: header,
                MessageLine: messageLine,
                Buffer: buffer,
                Fields: fields);
            return (saved, "");
        }
        catch (Truncated truncated)
        {
            return Fail(truncated.Message);
        }
    }

    /// <summary>Puts a saved screen back. The message light and type-ahead buffer are not part of it.</summary>
    public static void Restore(Screen screen, SavedScreen saved)
    {
        screen.Rows = saved.Rows;
        screen.Columns = saved.Columns;
        screen.Buffer = (byte[])saved.Buffer.Clone();
        screen.MessageLine = (byte[])saved.MessageLine.Clone();
        screen.Fields = saved.Fields
            .Select(f => new FieldEntry { Start = f.Start, Length = f.Length, Ffw = f.Ffw, Fcws = [.. f.Fcws], Attribute = f.Attribute })
            .ToList();
        screen.Header = (byte[])saved.Header.Clone();
        screen.Address = 0;
        screen.Cursor = saved.Cursor;
        screen.InsertCursor = saved.InsertCursor;
        screen.ErrorRow = saved.ErrorRow;
        screen.PendingRead = saved.PendingRead;
        screen.ReadControl = saved.ReadControl;
        screen.HeldAid = saved.HeldAid;
        screen.ActiveField = saved.ActiveField;
        screen.Error = saved.Error;
        screen.SavedErrorLine = (byte[]?)saved.SavedErrorLine?.Clone();
        screen.KeyboardLocked = (saved.Flags & Flag.KeyboardLocked) != 0;
        screen.InsertMode = (saved.Flags & Flag.InsertMode) != 0;
        screen.FieldExitPending = (saved.Flags & Flag.FieldExitPending) != 0;
        screen.DeferredUnlock = (saved.Flags & Flag.DeferredUnlock) != 0
            ? new DeferredUnlock((saved.Flags & Flag.DeferredMovesCursor) != 0)
            : null;
    }

    /// <summary>
    /// The copy of saved data a recording keeps: sensitive fields and nondisplay text become
    /// blanks, same length. Data that can't be decoded is zeroed entirely.
    /// </summary>
    public static byte[] Scrub(byte[] bytes, Blank blank)
    {
        if (blank == Blank.None) return (byte[])bytes.Clone();
        var saved = Decode(bytes, out _);
        if (saved is null) return new byte[bytes.Length];
        var buffer = BlankSensitive(saved.Buffer, saved.Fields, blank);
        var output = (byte[])bytes.Clone();
        // The buffer is the last block before the field list.
        buffer.CopyTo(output, bytes.Length - FieldListLength(saved.Fields) - buffer.Length);
        return output;
    }

    /// <summary>A copy of a regeneration buffer with sensitive fields and nondisplay text blanked.</summary>
    public static byte[] BlankSensitive(byte[] buffer, IReadOnlyList<FieldEntry> fields, Blank blank)
    {
        var output = (byte[])buffer.Clone();
        if (blank == Blank.None) return output;
        foreach (var f in Reply.SensitiveFieldsOf(buffer, fields, blank)) JsArrays.Fill(output, 0x40, f.Start, f.Start + f.Length);
        var attributes = Display.AttributesInEffect(buffer);
        for (var i = 0; i < output.Length; i++)
        {
            if (Display.IsNondisplay(attributes[i]) && !Display.IsAttribute(output[i]) && output[i] != 0) output[i] = 0x40;
        }
        return output;
    }

    private static int FieldListLength(List<FieldEntry> fields) => 2 + fields.Sum(f => 8 + 2 * f.Fcws.Count);
}
