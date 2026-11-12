// The 5250 orders and commands the fake's screens are built from.

namespace FakeIbmI;

internal static class Stream5250
{
    public const byte Esc = 0x04;
    public const int EnterAid = 0xf1;
    public const int F3Aid = 0x33;
    public const int F6Aid = 0x36;
    public const int F12Aid = 0x3c;
    public const int RollDownAid = 0xf4;
    public const int RollUpAid = 0xf5;

    // Display attributes.
    public const byte Normal = 0x20;
    public const byte Highlight = 0x22;
    public const byte Underline = 0x24;

    public static byte[] Text(string s) => Ebcdic.Encode(s);

    public static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Set buffer address.</summary>
    public static byte[] Sba(int row, int column) => [0x11, (byte)row, (byte)column];

    /// <summary>Insert cursor.</summary>
    public static byte[] Ic(int row, int column) => [0x13, (byte)row, (byte)column];

    /// <summary>SF with a field format word, attribute and length.</summary>
    public static byte[] Sf(int ffw, int attribute, int length) =>
        [0x1d, (byte)(ffw >> 8), (byte)(ffw & 0xff), (byte)attribute, (byte)(length >> 8), (byte)(length & 0xff)];

    /// <summary>Start of header, with the row errors show on.</summary>
    public static byte[] Header(int errorRow) => [0x01, 0x07, 0x00, 0x00, 0x00, (byte)errorRow, 0x00, 0x00, 0x00];

    /// <summary>CLEAR UNIT, then WTD (unlock keyboard, 0x00 0x18) with orders, then READ MDT FIELDS.</summary>
    public static byte[] Screen5250(params byte[][] orders) => Screen5250Clear([Esc, 0x40], orders);

    /// <summary>The same after CLEAR UNIT ALTERNATE 0x00: a 27 x 132 screen.</summary>
    public static byte[] Screen5250Wide(params byte[][] orders) => Screen5250Clear([Esc, 0x20, 0x00], orders);

    private static byte[] Screen5250Clear(byte[] clear, byte[][] orders) =>
        HostRecord(0x03, Join(clear, WriteToDisplay(true, orders), ReadMdtFields));

    public static readonly byte[] ClearUnit = [Esc, 0x40];

    public static readonly byte[] ReadMdtFields = [Esc, 0x52, 0x00, 0x00];

    /// <summary>WTD with orders, unlocking the keyboard or leaving it locked.</summary>
    public static byte[] WriteToDisplay(bool unlock, params byte[][] orders) =>
        Join([Esc, 0x11, 0x00, (byte)(unlock ? 0x18 : 0x00)], Join(orders));

    /// <summary>A message on the error row, highlighted.</summary>
    public static byte[] Message(string? message) =>
        message is null ? [] : Join(Sba(24, 2), [Highlight], Text(message.Length > 77 ? message[..77] : message));

    /// <summary>A 5250 record from the host, framed for Telnet. Opcode 3 is put/get, 2 output only.</summary>
    public static byte[] HostRecord(int opcode, byte[] data)
    {
        var length = 10 + data.Length;
        byte[] header = [(byte)(length >> 8), (byte)(length & 0xff), 0x12, 0xa0, 0, 0, 4, 0, 0, (byte)opcode];
        return Telnet.EncodeRecord([.. header, .. data]);
    }
}
