// The IBM i password substitutes the host servers check at sign-on, and the protected passwords
// a password change carries. QPWDLVL 0-1 use DES on the upper-case EBCDIC password (a CBC MAC
// here, not IBM's code); 2-3 use SHA-1 on the UTF-16 password. Both follow JTOpen's client.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FakeIbmI;

internal static class Passwords
{
    /// <summary>The password token the substitutes are made with.</summary>
    public static byte[] Token(int level, string user, string password)
    {
        if (level >= 2)
            return SHA1.HashData([.. ShaUserId(user), .. Encoding.BigEndianUnicode.GetBytes(password)]);

        // DES: the (folded) user ID enciphered under the password, shifted; a password over
        // eight characters enciphers it twice and XORs the two.
        var pwd = DesPassword(password);
        var folded = Fold(DesUserId(user));
        var length = Array.FindIndex(pwd, b => b == 0x40) is var n and >= 0 ? n : 10;
        var token = Des(Key(pwd.AsSpan(0, Math.Min(8, length))), folded);
        if (length > 8) token = Xor(token, Des(Key(pwd.AsSpan(8, length - 8)), folded));
        return token;
    }

    /// <summary>The substitute for one sequence number: 1 for the password itself, then 2, 3 and on for protecting.</summary>
    public static byte[] Substitute(int level, byte[] token, string user, byte[] clientSeed, byte[] serverSeed, ulong sequence)
    {
        var seq = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seq, sequence);
        if (level >= 2) return SHA1.HashData([.. token, .. serverSeed, .. clientSeed, .. ShaUserId(user), .. seq]);

        // DES: a CBC MAC under the token over RDrSEQ (server seed + sequence), the client seed, the
        // user ID in two halves XORed with RDrSEQ, and the sequence.
        var rdr = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(rdr, BinaryPrimitives.ReadUInt64BigEndian(serverSeed) + sequence);
        var userId = DesUserId(user);
        byte[] blocks =
        [
            .. rdr,
            .. clientSeed,
            .. Xor(userId[..8], rdr),
            .. Xor([userId[8], userId[9], 0x40, 0x40, 0x40, 0x40, 0x40, 0x40], rdr),
            .. seq,
        ];
        using var des = DES.Create();
        des.Key = token;
        return des.EncryptCbc(blocks, new byte[8], PaddingMode.None)[^8..];
    }

    /// <summary>
    /// The password bytes a change carries, protected: SHA-1 levels send the UTF-16 password in
    /// 20-byte blocks, DES levels the EBCDIC password in one or two 8-byte blocks.
    /// </summary>
    public static byte[] PasswordBytes(int level, string password)
    {
        if (level >= 2) return Encoding.BigEndianUnicode.GetBytes(password);
        var pwd = DesPassword(password);
        return pwd[8] == 0x40 && pwd[9] == 0x40 ? pwd[..8] : [.. pwd[..8], pwd[8], pwd[9], 0x40, 0x40, 0x40, 0x40, 0x40, 0x40];
    }

    /// <summary>
    /// XORs <paramref name="data"/> with the substitutes for <paramref name="sequence"/> onwards, a
    /// block each (20 bytes for SHA-1, 8 for DES), and advances the sequence. It protects and
    /// unprotects alike; a short last block is padded with the substitute itself, as the client does.
    /// </summary>
    public static byte[] Protect(int level, byte[] token, string user, byte[] clientSeed, byte[] serverSeed, ref ulong sequence, byte[] data)
    {
        var block = level >= 2 ? 20 : 8;
        var result = new byte[Math.Max(1, (data.Length + block - 1) / block) * block];
        for (var at = 0; at < result.Length; at += block)
        {
            var substitute = Substitute(level, token, user, clientSeed, serverSeed, ++sequence);
            for (var i = 0; i < block; i++)
                result[at + i] = (byte)(substitute[i] ^ (at + i < data.Length ? data[at + i] : 0));
        }
        return result;
    }

    /// <summary>A password unprotected from <see cref="PasswordBytes"/>, or null if it isn't one.</summary>
    public static string? Password(int level, byte[] bytes, int length)
    {
        if (level >= 2)
            return length % 2 == 0 && length <= bytes.Length ? Encoding.BigEndianUnicode.GetString(bytes, 0, length) : null;
        if (bytes.Length is not (8 or 16)) return null;
        byte[] pwd = bytes.Length == 8 ? bytes : [.. bytes[..8], bytes[8], bytes[9]];
        var text = Ebcdic.Decode(pwd).TrimEnd();
        // A DES password that starts with a digit goes with a Q in front.
        return text.Length > 1 && text[0] == 'Q' && char.IsAsciiDigit(text[1]) ? text[1..] : text;
    }

    private static byte[] ShaUserId(string user) => Encoding.BigEndianUnicode.GetBytes(user.ToUpperInvariant().PadRight(10));

    private static byte[] DesUserId(string user) => Ebcdic.Encode(user.ToUpperInvariant().PadRight(10));

    private static byte[] DesPassword(string password)
    {
        password = password.ToUpperInvariant();
        if (password.Length > 0 && char.IsAsciiDigit(password[0])) password = "Q" + password;
        return Ebcdic.Encode(password.PadRight(10)[..10]);
    }

    /// <summary>Characters 9 and 10 of a long user ID, two bits at a time, into the top of bytes 1 to 8.</summary>
    private static byte[] Fold(byte[] userId)
    {
        var folded = userId[..8];
        if (userId[8] == 0x40) return folded;
        for (var bit = 0; bit < 16; bit += 2)
        {
            var source = userId[8 + bit / 8];
            var pair = (source >> (6 - bit % 8)) & 0x03;
            folded[bit / 2] ^= (byte)(pair << 6);
        }
        return folded;
    }

    private static byte[] Key(ReadOnlySpan<byte> part)
    {
        var block = new byte[8];
        block.AsSpan().Fill(0x40);
        part.CopyTo(block);
        var value = (BinaryPrimitives.ReadUInt64BigEndian(block) ^ 0x5555555555555555UL) << 1;
        BinaryPrimitives.WriteUInt64BigEndian(block, value);
        return block;
    }

    private static byte[] Des(byte[] key, byte[] block)
    {
        using var des = DES.Create();
        des.Key = key;
        return des.EncryptEcb(block, PaddingMode.None);
    }

    private static byte[] Xor(byte[] a, byte[] b) => [.. a.Select((x, i) => (byte)(x ^ b[i]))];
}
