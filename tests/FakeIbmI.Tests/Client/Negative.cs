// Negative responses: what the client sends when it can't process a host record, as a
// 5494 does (IBM 5494 Functions Reference SC30-3533-04, table 13-26). The code is 4 bytes:
// category, modifier and two user bytes. Over Telnet it follows a header with the ERR flag
// (RFC 1205, section 3). RFC 1205 names no opcode; tn5250 and tn5250j send No Operation.

namespace FakeIbmI.Tests.Client;

public static class NegativeResponse
{
    public const int CommandNotValid = 0x10030101;
    public const int ClearUnitAlternateNotValid = 0x10030105;
    public const int StructuredFieldNotValid = 0x10050111;
    public const int PrematureEnd = 0x10050121;
    public const int AddressNotValid = 0x10050122;

    /// <summary>Repeat to Address before the current address; also used for Erase to Address.</summary>
    public const int AddressBeforeCurrent = 0x10050123;

    public const int FieldLengthNotValid = 0x10050125;
    public const int RestoreDataNotValid = 0x10050127;
    public const int FieldPastEnd = 0x10050128;
    public const int HeaderLengthNotValid = 0x1005012b;
    public const int RollNotValid = 0x1005012c;
    public const int ExtendedAttributeTypeNotValid = 0x1005012d;
    public const int FieldAttributeNotValid = 0x10050130;
    public const int NoEscape = 0x10050131;
    public const int ErrorWindowNotValid = 0x10050132;
    public const int ErrorWindowLineInUse = 0x10050133;
    public const int DataNotValid = 0x10050142;

    /// <summary>The negative response record, framed for Telnet.</summary>
    public static byte[] Encode(int code)
    {
        var c = unchecked((uint)code);
        byte[] bytes = [(byte)(c >> 24), (byte)((c >> 16) & 0xff), (byte)((c >> 8) & 0xff), (byte)(c & 0xff)];
        return Records.EncodeClientRecord(Opcode.NoOperation, RecordFlag.Err, bytes);
    }

    /// <summary>The code as the manual writes it, for example 1005 0142.</summary>
    public static string FormatResponseCode(int code)
    {
        var hex = code.ToString("X8");
        return $"{hex[..4]} {hex[4..]}";
    }
}
