// Inbound data for the host: the replies to READ commands (IBM 5494 Functions Reference
// SC30-3533-04, pages 15-105 to 15-112). When a session is recorded, each reply is built
// a second time with sensitive fields blanked, and only that copy is recorded.

using System.Text.Json.Serialization;

namespace FakeIbmI.Tests.Client;

/// <summary>What a recorded copy leaves out: nothing, nondisplay input, or all input.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Blank>))]
public enum Blank
{
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("nondisplay")]
    Nondisplay,

    [JsonStringEnumMemberName("all-input")]
    AllInput,
}

public static class ReadCommand
{
    public const int ReadInputFields = 0x42;
    public const int ReadMdtFields = 0x52;
    public const int ReadMdtAlternate = 0x82;
    public const int ReadImmediate = 0x72;
    public const int ReadModifiedImmediateAlternate = 0x83;
}

public static class Reply
{
    private const byte Sba = 0x11;
    private const byte BlankByte = 0x40;
    private const byte Minus = 0x60;

    private enum FieldStyle
    {
        Input,
        Mdt,
        MdtAlternate,
    }

    /// <summary>
    /// The data of a reply to a READ command: cursor row and column, the AID, then field
    /// data. READ IMMEDIATE and READ MODIFIED IMMEDIATE ALTERNATE use AID 0.
    /// </summary>
    public static byte[] EncodeReadReply(Screen screen, int command, int aid, Blank blank = Blank.None)
    {
        var output = new List<byte>
        {
            (byte)(screen.Cursor / screen.Columns + 1),
            (byte)(screen.Cursor % screen.Columns + 1),
            (byte)aid,
        };
        var sensitive = SensitiveFields(screen, blank);
        void Add(FieldEntry field, FieldStyle style)
        {
            if (style != FieldStyle.Input)
            {
                var row = field.Start / screen.Columns + 1;
                output.Add(Sba);
                output.Add((byte)row);
                output.Add((byte)(field.Start % screen.Columns + 1));
            }
            output.AddRange(FieldBytes(screen, field, style, sensitive.Contains(field)));
        }
        static bool IsModified(FieldEntry f) => (f.Ffw & FieldFormat.FfwModified) != 0;
        switch (command)
        {
            case ReadCommand.ReadInputFields:
                if (screen.Modified && Keyboard.AidSendsData(screen, aid))
                {
                    foreach (var f in screen.Fields) Add(f, FieldStyle.Input);
                }
                break;
            case ReadCommand.ReadMdtFields:
            case ReadCommand.ReadMdtAlternate:
                if (Keyboard.AidSendsData(screen, aid))
                {
                    var style = command == ReadCommand.ReadMdtFields ? FieldStyle.Mdt : FieldStyle.MdtAlternate;
                    foreach (var f in screen.Fields.Where(IsModified).ToList()) Add(f, style);
                }
                break;
            case ReadCommand.ReadImmediate:
                if (screen.Modified)
                {
                    foreach (var f in screen.Fields) Add(f, FieldStyle.Input);
                }
                break;
            case ReadCommand.ReadModifiedImmediateAlternate:
                foreach (var f in screen.Fields.Where(IsModified).ToList()) Add(f, FieldStyle.MdtAlternate);
                break;
        }
        return output.ToArray();
    }

    /// <summary>
    /// One field's data as a READ returns it.
    /// - input: the whole field, nulls as blanks;
    /// - mdt: trailing nulls stripped, leading and embedded nulls as blanks;
    /// - mdt-alternate: trailing nulls stripped, other nulls kept.
    /// A signed numeric field never sends its sign position: a minus there sets the zone of
    /// the digit before it to D. A blanked field sends blanks for its whole length.
    /// </summary>
    private static byte[] FieldBytes(Screen screen, FieldEntry field, FieldStyle style, bool blanked)
    {
        var signed = FieldFormat.Decode(field.Ffw).Shift == "signed-numeric";
        var length = signed ? field.Length - 1 : field.Length;
        if (blanked) return Enumerable.Repeat(BlankByte, Math.Max(length, 0)).ToArray();
        var data = JsArrays.Sub(screen.Buffer, field.Start, field.Start + length).ToArray();
        if (signed && JsArrays.At(screen.Buffer, field.Start + length) == Minus && length > 0)
        {
            var last = data[length - 1];
            if (last >= 0xf0 && last <= 0xf9) data[length - 1] = (byte)(0xd0 | (last & 0x0f));
        }
        if (style == FieldStyle.Input) return data.Select(b => b == 0 ? BlankByte : b).ToArray();
        var end = data.Length;
        while (end > 0 && data[end - 1] == 0) end--;
        var kept = data[..end];
        return style == FieldStyle.Mdt ? kept.Select(b => b == 0 ? BlankByte : b).ToArray() : kept;
    }

    /// <summary>
    /// The fields a recorded copy blanks. Nondisplay means the field's own attribute, or the
    /// attribute now on screen anywhere in it, since the host can rewrite that byte as data.
    /// </summary>
    public static HashSet<FieldEntry> SensitiveFields(Screen screen, Blank blank) =>
        SensitiveFieldsOf(screen.Buffer, screen.Fields, blank);

    public static HashSet<FieldEntry> SensitiveFieldsOf(byte[] buffer, IReadOnlyList<FieldEntry> fields, Blank blank)
    {
        if (blank == Blank.None) return [];
        if (blank == Blank.AllInput) return [.. fields];
        var attributes = Display.AttributesInEffect(buffer);
        return
        [
            .. fields.Where(f =>
                Display.IsNondisplay(f.Attribute)
                || JsArrays.Sub(attributes, f.Start, f.Start + f.Length).ToArray().Any(a => Display.IsNondisplay(a))),
        ];
    }

    /// <summary>Format table features a reply doesn't honour yet, for an `unsupported` event.</summary>
    public static List<string> ReplyLimitations(Screen screen)
    {
        var notes = new List<string>();
        var fcws = screen.Fields.SelectMany(f => f.Fcws).ToList();
        if ((JsArrays.At(screen.Header, 2) ?? 0) != 0 || fcws.Any(w => w >> 8 == 0x80))
        {
            notes.Add("field resequencing");
        }
        if (fcws.Any(w => w >> 8 == 0x84)) notes.Add("transparent fields");
        if (fcws.Any(w => w >= 0x8601 && w <= 0x8603)) notes.Add("continued fields");
        return notes;
    }
}
