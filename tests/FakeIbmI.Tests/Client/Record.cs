// 5250 records inside Telnet (RFC 1205, RFC 4777).
//
//   LL LL  12 A0  00 00  | 04  flags  00  opcode | data...
//   length GDS    flow   | variable header
//
// The length counts the whole record before IAC doubling, without the IAC EOR.

namespace FakeIbmI.Tests.Client;

public static class Opcode
{
    public const int NoOperation = 0x00;
    public const int Invite = 0x01;
    public const int OutputOnly = 0x02;
    public const int PutGet = 0x03;
    public const int SaveScreen = 0x04;
    public const int RestoreScreen = 0x05;
    public const int ReadImmediate = 0x06;
    public const int ReadScreen = 0x08;
    public const int CancelInvite = 0x0a;
    public const int MessageLightOn = 0x0b;
    public const int MessageLightOff = 0x0c;
}

/// <summary>Bits in the first flag byte (RFC 1205 bits 0–7, most significant first).</summary>
public static class RecordFlag
{
    /// <summary>Data stream output error: a negative response code follows.</summary>
    public const int Err = 0x80;

    /// <summary>The Attention key was pressed.</summary>
    public const int Atn = 0x40;

    /// <summary>The System Request key was pressed.</summary>
    public const int Srq = 0x04;

    /// <summary>Test Request.</summary>
    public const int Trq = 0x02;

    /// <summary>Help in error state: a four-digit packed error code follows.</summary>
    public const int Hlp = 0x01;
}

/// <param name="Data">The 5250 data after the variable header.</param>
public sealed record HostRecord(int Flags, int Opcode, byte[] Data)
{
    public bool Equals(HostRecord? other) =>
        other is not null && Flags == other.Flags && Opcode == other.Opcode && Data.AsSpan().SequenceEqual(other.Data);

    public override int GetHashCode() => HashCode.Combine(Flags, Opcode, Data.Length);
}

/// <param name="Code">Four characters, for example I902.</param>
public sealed record StartupResponse(string Code, string SystemName, string DeviceName);

public abstract record ParsedRecord
{
    public sealed record Data(HostRecord Record) : ParsedRecord;

    public sealed record Startup(StartupResponse Response) : ParsedRecord;

    public sealed record Invalid(string Reason) : ParsedRecord;
}

public static class Records
{
    private const int HeaderLength = 10;

    // After LL LL 12 A0, a Startup Response Record has these fixed bytes (RFC 4777 section 10).
    private static readonly byte[] StartupFixed = [0x90, 0x00, 0x05, 0x60, 0x06, 0x00, 0x20, 0xc0, 0x00, 0x3d, 0x00, 0x00];

    public static ParsedRecord Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength) return new ParsedRecord.Invalid($"record of {bytes.Length} bytes is too short");
        var length = (bytes[0] << 8) | bytes[1];
        if (length != bytes.Length)
        {
            return new ParsedRecord.Invalid($"length field says {length} bytes but the record has {bytes.Length}");
        }
        if (bytes[2] != 0x12 || bytes[3] != 0xa0) return new ParsedRecord.Invalid("record type is not 12A0");

        if (bytes.Length >= 38 && bytes.Slice(4, StartupFixed.Length).SequenceEqual(StartupFixed))
        {
            return new ParsedRecord.Startup(
                new StartupResponse(
                    Ebcdic.Decode(bytes[16..20]),
                    JsArrays.TrimEnd(Ebcdic.Decode(bytes[20..28])),
                    JsArrays.TrimEnd(Ebcdic.Decode(bytes[28..38]))));
        }

        int variableLength = bytes[6];
        if (variableLength < 3 || 6 + variableLength > bytes.Length)
        {
            return new ParsedRecord.Invalid($"variable header length {variableLength} is not valid");
        }
        return new ParsedRecord.Data(
            new HostRecord(bytes[7], bytes[6 + variableLength - 1], bytes[(6 + variableLength)..].ToArray()));
    }

    /// <summary>Builds a client record and frames it for Telnet (IAC doubling, IAC EOR).</summary>
    public static byte[] EncodeClientRecord(int opcode, int flags, ReadOnlySpan<byte> data)
    {
        var length = HeaderLength + data.Length;
        var bytes = new byte[length];
        bytes[0] = (byte)(length >> 8);
        bytes[1] = (byte)(length & 0xff);
        bytes[2] = 0x12;
        bytes[3] = 0xa0;
        bytes[6] = 0x04;
        bytes[7] = (byte)flags;
        bytes[9] = (byte)opcode;
        data.CopyTo(bytes.AsSpan(HeaderLength));
        return Telnet.EncodeRecord(bytes);
    }
}
