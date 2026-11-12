// A program's parameters as a host server client sends them: bytes laid out as on an IBM i.
// Characters are EBCDIC, integers big-endian, and decimals packed, or zoned when the parameter is
// as long as its digits.

using System.Buffers.Binary;
using System.Globalization;

namespace FakeIbmI.Rpg;

internal static class ParameterBytes
{
    /// <summary>Sets the parameter from the client's bytes.</summary>
    public static void Load(Cell cell, ReadOnlySpan<byte> data)
    {
        var type = cell.Type;
        if (cell.Elements is not null || data.Length == 0) return;
        switch (type.Kind)
        {
            case RpgKind.Decimal when data.Length == type.Length && type.Length != type.Length / 2 + 1:
            {
                // Zoned: a digit in each byte, the sign in the last one's zone.
                var digits = string.Concat(data.ToArray().Select(b => (char)('0' + (b & 0x0f))));
                var value = decimal.Parse(digits, CultureInfo.InvariantCulture) / Values.Pow10(type.Decimals);
                cell.Set((data[^1] & 0xf0) is 0xd0 or 0xb0 ? -value : value);
                break;
            }
            case RpgKind.Decimal:
            {
                var hex = Convert.ToHexString(data);
                var digits = hex[..^1];
                if (!digits.All(char.IsAsciiDigit)) throw new RpgError(907, "A packed decimal parameter doesn't hold a number.");
                var value = decimal.Parse(digits, CultureInfo.InvariantCulture) / Values.Pow10(type.Decimals);
                cell.Set(hex[^1] is 'D' or 'B' ? -value : value);
                break;
            }
            case RpgKind.Int:
                cell.Set(data.Length switch
                {
                    1 => type.Unsigned ? data[0] : (decimal)(sbyte)data[0],
                    2 => type.Unsigned ? BinaryPrimitives.ReadUInt16BigEndian(data) : BinaryPrimitives.ReadInt16BigEndian(data),
                    4 => type.Unsigned ? BinaryPrimitives.ReadUInt32BigEndian(data) : BinaryPrimitives.ReadInt32BigEndian(data),
                    8 => type.Unsigned ? BinaryPrimitives.ReadUInt64BigEndian(data) : BinaryPrimitives.ReadInt64BigEndian(data),
                    _ => throw new RpgError(907, $"An integer parameter is {data.Length} bytes long."),
                });
                break;
            case RpgKind.Float:
                cell.Set(data.Length == 4 ? BinaryPrimitives.ReadSingleBigEndian(data) : data.Length == 8 ? BinaryPrimitives.ReadDoubleBigEndian(data) : 0d);
                break;
            case RpgKind.Date or RpgKind.Time or RpgKind.Timestamp:
            {
                // A value never set arrives as zeros or blanks: left at its default.
                var text = Ebcdic.Decode(data).Replace('\0', ' ').Trim();
                if (text.Length > 0) cell.Set(text);
                break;
            }
            default:
                cell.Set(Ebcdic.Decode(data).Replace('\0', ' '));
                break;
        }
    }

    /// <summary>The parameter as bytes for the client, <paramref name="length"/> long.</summary>
    public static byte[] Store(Cell cell, int length)
    {
        var type = cell.Type;
        var result = new byte[length];
        if (cell.Elements is not null) return result;
        switch (type.Kind)
        {
            case RpgKind.Decimal:
            {
                var value = (decimal)cell.Value;
                var digits = decimal.Truncate(Math.Abs(value) * Values.Pow10(type.Decimals)).ToString(CultureInfo.InvariantCulture);
                if (length == type.Length && type.Length != type.Length / 2 + 1)
                {
                    digits = digits.PadLeft(length, '0');
                    for (var i = 0; i < length; i++) result[i] = (byte)(0xf0 | digits[i] - '0');
                    if (value < 0) result[^1] = (byte)(0xd0 | result[^1] & 0x0f);
                    break;
                }
                var hex = digits.PadLeft(length * 2 - 1, '0') + (value < 0 ? 'D' : 'F');
                if (hex.Length == length * 2) Convert.FromHexString(hex).CopyTo(result, 0);
                break;
            }
            case RpgKind.Int:
            {
                var value = (decimal)cell.Value;
                switch (length)
                {
                    case 1:
                        result[0] = type.Unsigned ? (byte)value : (byte)(sbyte)value;
                        break;
                    case 2:
                        if (type.Unsigned) BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)value);
                        else BinaryPrimitives.WriteInt16BigEndian(result, (short)value);
                        break;
                    case 4:
                        if (type.Unsigned) BinaryPrimitives.WriteUInt32BigEndian(result, (uint)value);
                        else BinaryPrimitives.WriteInt32BigEndian(result, (int)value);
                        break;
                    case 8:
                        if (type.Unsigned) BinaryPrimitives.WriteUInt64BigEndian(result, (ulong)value);
                        else BinaryPrimitives.WriteInt64BigEndian(result, (long)value);
                        break;
                }
                break;
            }
            case RpgKind.Float:
                if (length == 4) BinaryPrimitives.WriteSingleBigEndian(result, (float)(double)cell.Value);
                else if (length == 8) BinaryPrimitives.WriteDoubleBigEndian(result, (double)cell.Value);
                break;
            default:
            {
                var bytes = Ebcdic.Encode(Values.ToText(cell.Get()));
                Array.Fill(result, (byte)0x40);
                bytes.AsSpan(0, Math.Min(bytes.Length, length)).CopyTo(result);
                break;
            }
        }
        return result;
    }
}
