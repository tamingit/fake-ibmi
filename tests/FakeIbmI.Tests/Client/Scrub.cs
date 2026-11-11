// An independent check on recordings: every client reply must carry blanks, for the
// whole field length, in the fields that were nondisplay on the screen it answered.
// The replay tool runs it over each recording; the recorder itself never relies on it.

namespace FakeIbmI.Tests.Client;

public static class Scrub
{
    private const byte Sba = 0x11;
    private const byte BlankByte = 0x40;

    /// <summary>The AID of an inbound structured field, such as the QUERY reply.</summary>
    private const byte AidStructuredField = 0x88;

    /// <summary>Problems with one client record, given the screen it answered. Empty when it is clean.</summary>
    public static List<string> ScrubProblems(ScreenSnapshot snapshot, HostRecord record)
    {
        var data = record.Data;
        // A negative response carries only its 4-byte code.
        if ((record.Flags & RecordFlag.Err) != 0) return [];
        if (record.Opcode == Opcode.SaveScreen) return SavedDataProblems(data, "save reply");
        if (record.Opcode == Opcode.NoOperation && data.Length == snapshot.Rows * snapshot.Columns)
        {
            // A READ SCREEN reply: the whole regeneration buffer.
            var fields = snapshot.Fields
                .Select(f => new FieldEntry
                {
                    Start = (f.Row - 1) * snapshot.Columns + f.Column - 1,
                    Length = f.Length,
                    Ffw = f.Ffw,
                    Fcws = [.. f.Fcws],
                    Attribute = f.Attribute,
                })
                .ToList();
            return BufferProblems(data, fields, "read screen reply");
        }
        if (record.Opcode != Opcode.PutGet) return [];
        if (data.Length <= 3 || data[2] == AidStructuredField) return [];
        var problems = new List<string>();
        void Check(SnapshotField field, ReadOnlySpan<byte> bytes)
        {
            if (!Sensitive(snapshot, field)) return;
            if (bytes.Length != DataLength(field) || bytes.ContainsAnyExcept(BlankByte))
            {
                problems.Add($"nondisplay field at row {field.Row}, column {field.Column} is not blanked");
            }
        }
        if (data[3] == Sba)
        {
            // READ MDT FIELDS and its variants: SBA row column, then the field's data.
            var i = 3;
            while (i < data.Length)
            {
                if (data[i] != Sba || i + 2 >= data.Length)
                {
                    problems.Add($"reply byte {i} should start an SBA");
                    break;
                }
                int row = data[i + 1], column = data[i + 2];
                var end = i + 3;
                while (end < data.Length && data[end] != Sba) end++;
                var field = snapshot.Fields.FirstOrDefault(f => f.Row == row && f.Column == column);
                if (field is not null) Check(field, JsArrays.Sub(data, i + 3, end));
                i = end;
            }
        }
        else
        {
            // READ INPUT FIELDS and READ IMMEDIATE: every field in order, at its fixed length.
            var i = 3;
            foreach (var field in snapshot.Fields)
            {
                var length = DataLength(field);
                Check(field, JsArrays.Sub(data, i, i + length));
                i += length;
            }
        }
        return problems;
    }

    private static int DataLength(SnapshotField field) =>
        field.Format.Shift == "signed-numeric" ? field.Length - 1 : field.Length;

    private static bool Sensitive(ScreenSnapshot snapshot, SnapshotField field)
    {
        var start = (field.Row - 1) * snapshot.Columns + field.Column - 1;
        return Display.IsNondisplay(field.Attribute)
            || JsArrays.Sub(snapshot.Attributes, start, start + field.Length).ToArray().Any(a => Display.IsNondisplay(a));
    }

    /// <summary>Problems with a host record that echoes saved-screen data back (RESTORE SCREEN).</summary>
    public static List<string> RestoreProblems(HostRecord record)
    {
        var data = record.Data;
        if (data.Length < 2 || data[0] != 0x04 || (data[1] != 0x12 && data[1] != 0x13)) return [];
        return SavedDataProblems(data, "restore record");
    }

    /// <summary>Checks `ESC 12 data` or `ESC 13 LL LL data`, as a save reply or a restore record has it.</summary>
    private static List<string> SavedDataProblems(byte[] data, string what)
    {
        var partial = JsArrays.At(data, 1) == 0x13;
        var skip = partial ? 4 : 2;
        var end = partial ? 2 + (((JsArrays.At(data, 2) ?? 0) << 8) | (JsArrays.At(data, 3) ?? 0)) : data.Length;
        var bytes = JsArrays.Sub(data, skip, end).ToArray();
        if (bytes.All(b => b == 0)) return []; // Zeroed by the recorder.
        var saved = SavedScreens.Decode(bytes, out var error);
        if (saved is null) return [$"{what}: saved data can't be checked ({error})"];
        return BufferProblems(saved.Buffer, saved.Fields, what);
    }

    /// <summary>Typed characters left in nondisplay fields or under nondisplay attributes.</summary>
    private static List<string> BufferProblems(byte[] buffer, List<FieldEntry> fields, string what)
    {
        var problems = new List<string>();
        static bool Clear(byte b) => b is 0 or BlankByte;
        var inField = new bool[buffer.Length];
        var sensitive = Reply.SensitiveFieldsOf(buffer, fields, Blank.Nondisplay);
        // In format table order, as the TS Set iterates.
        foreach (var f in fields.Where(sensitive.Contains))
        {
            for (var i = Math.Max(0, f.Start); i < Math.Min(buffer.Length, f.Start + f.Length); i++) inField[i] = true;
            if (!JsArrays.Sub(buffer, f.Start, f.Start + f.Length).ToArray().All(Clear))
            {
                problems.Add($"{what}: nondisplay field at position {f.Start} is not blanked");
            }
        }
        // Nondisplay text outside those fields, such as hidden output the host wrote.
        var attributes = Display.AttributesInEffect(buffer);
        var hidden = -1;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (!inField[i] && Display.IsNondisplay(attributes[i]) && !Display.IsAttribute(buffer[i]) && !Clear(buffer[i]))
            {
                hidden = i;
                break;
            }
        }
        if (hidden >= 0) problems.Add($"{what}: nondisplay text at position {hidden} is not blanked");
        return problems;
    }
}
