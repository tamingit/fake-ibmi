// The 5250 QUERY command and its reply (IBM 5494 Functions Reference SC30-3533-04,
// section 15.27, tables 15-46 to 15-48). The IBM i sends a QUERY before the first
// screen and waits for the reply, which describes the display's capabilities.

namespace FakeIbmI.Tests.Client;

/// <param name="TerminalType">TERMINAL-TYPE name, for example IBM-3477-FC.</param>
public sealed record TerminalModel(
    string TerminalType,
    string MachineType,
    string Model,
    int Rows,
    int Columns,
    bool Wide,
    bool Colour);

public static class Query
{
    private const byte Esc = 0x04;
    private const byte WriteStructuredField = 0xf3;
    private const byte Class5250 = 0xd9;
    private const byte TypeQuery = 0x70;

    /// <summary>AID byte for an inbound structured field such as the QUERY reply.</summary>
    private const byte AidInboundStructuredField = 0x88;

    public static readonly IReadOnlyDictionary<string, TerminalModel> Terminals = new Dictionary<string, TerminalModel>
    {
        ["IBM-3477-FC"] = new("IBM-3477-FC", "3477", "FC", 27, 132, Wide: true, Colour: true),
        ["IBM-3179-2"] = new("IBM-3179-2", "3179", "2", 24, 80, Wide: false, Colour: true),
    };

    /// <summary>True when the data begins with WRITE STRUCTURED FIELD, class D9, type 70.</summary>
    public static bool IsQuery(ReadOnlySpan<byte> data) =>
        data.Length >= 6
        && data[0] == Esc
        && data[1] == WriteStructuredField
        && data[4] == Class5250
        && data[5] == TypeQuery;

    /// <summary>The complete QUERY reply record, framed for Telnet.</summary>
    public static byte[] EncodeQueryReply(TerminalModel terminal)
    {
        var model = terminal.Model.Length > 0 && terminal.Model.All(char.IsAsciiDigit)
            ? terminal.Model.PadLeft(3, '0')
            : terminal.Model.PadRight(3, ' ');

        // Device capabilities (table 15-48, 22 bytes). Bits are numbered from the left.
        var capabilities = new byte[22];
        // Byte 0: READ MDT ALTERNATE (bit 2), Move Cursor / Transparent Data orders (bit 6),
        // READ MODIFIED IMMEDIATE ALTERNATE (bit 7). No Row 1/Column 1 support.
        capabilities[0] = 0x20 | 0x02 | 0x01;
        // Byte 1: 27 x 132 (bit 2), 24 x 80 (bit 3, always set), colour (bits 6-7 = 01).
        capabilities[1] = (byte)((terminal.Wide ? 0x20 : 0) | 0x10 | (terminal.Colour ? 0x01 : 0));
        // Bytes 2-21 stay zero: no extended attributes, graphics, GUI (enhanced) constructs,
        // image/fax or grid lines. Declare enhanced support only once the client handles it.

        List<byte> field =
        [
            // Structured field header: total length X'0044', class, type, fixed flag X'80'.
            0x00, 0x44, Class5250, TypeQuery, 0x80,
            // Workstation control unit: X'0600', another 5250 emulator (as tn5250 and tn5250j
            // send; the 5494 manual lists only its own X'0043'). Code level 1.1.0.
            0x06, 0x00, 0x01, 0x01, 0x00,
            .. new byte[16],
            // Workstation type: display station.
            0x01,
            .. Ebcdic.Encode(terminal.MachineType.PadLeft(4, '0')),
            .. Ebcdic.Encode(model),
            // Keyboard ID (standard 5250 keyboard), extended keyboard ID, reserved.
            0x02, 0x00, 0x00,
            // Serial number: none.
            0x00, 0x00, 0x00, 0x00,
            // Maximum input fields: 256.
            0x01, 0x00,
            // Control unit customization, reserved (2 bytes).
            0x00, 0x00, 0x00,
            .. capabilities,
        ];
        if (field.Count != 0x44) throw new InvalidOperationException($"QUERY reply field is {field.Count} bytes");

        // Inbound data: cursor row and column (zero), AID, then the structured field.
        byte[] data = [0x00, 0x00, AidInboundStructuredField, .. field];
        return Records.EncodeClientRecord(Opcode.NoOperation, 0, data);
    }
}
