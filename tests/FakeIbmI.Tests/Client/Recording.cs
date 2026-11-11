// Session recordings for offline replay. Recordings of real sessions stay in
// test/recordings/ (gitignored); only reviewed or synthetic ones become fixtures.
// `Connection` records a session itself: host data as whole Telnet events, and client
// data with nondisplay input (or all input) blanked.
//
// File layout: "G5250REC", format version (u8), then one entry per WebSocket chunk:
//   direction u8 (0 host to client, 1 client to host) | ms since start u32 | length u32 | bytes

using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace FakeIbmI.Tests.Client;

[JsonConverter(typeof(JsonStringEnumConverter<Direction>))]
public enum Direction
{
    [JsonStringEnumMemberName("host")]
    Host,

    [JsonStringEnumMemberName("client")]
    Client,
}

/// <param name="Ms">Milliseconds since the recording started.</param>
public sealed record RecordedChunk(Direction Direction, long Ms, byte[] Bytes)
{
    public bool Equals(RecordedChunk? other) =>
        other is not null && Direction == other.Direction && Ms == other.Ms && Bytes.AsSpan().SequenceEqual(other.Bytes);

    public override int GetHashCode() => HashCode.Combine(Direction, Ms, Bytes.Length);
}

public sealed class Recorder
{
    private readonly Func<double> _now;
    private readonly double _start;
    private readonly List<RecordedChunk> _chunks = [];

    public Recorder(Func<double> now)
    {
        _now = now;
        _start = now();
    }

    /// <summary>Adds a chunk. Sessions record through `Connection`, which scrubs what it adds.</summary>
    public void Add(Direction direction, ReadOnlySpan<byte> bytes)
    {
        // Math.round in JavaScript rounds halves up.
        var ms = (long)Math.Max(0, Math.Floor(_now() - _start + 0.5));
        _chunks.Add(new RecordedChunk(direction, ms, bytes.ToArray()));
    }

    public IReadOnlyList<RecordedChunk> Chunks => _chunks;

    public byte[] ToBytes() => RecordingCodec.Encode(_chunks);
}

public static class RecordingCodec
{
    private static readonly byte[] Magic = "G5250REC"u8.ToArray();
    private const byte Version = 1;
    private const int EntryHeader = 9;

    public static byte[] Encode(IReadOnlyList<RecordedChunk> chunks)
    {
        var size = chunks.Aggregate(Magic.Length + 1, (n, c) => n + EntryHeader + c.Bytes.Length);
        var output = new byte[size];
        Magic.CopyTo(output, 0);
        output[Magic.Length] = Version;
        var offset = Magic.Length + 1;
        foreach (var chunk in chunks)
        {
            output[offset] = (byte)(chunk.Direction == Direction.Host ? 0 : 1);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset + 1), unchecked((uint)chunk.Ms));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset + 5), (uint)chunk.Bytes.Length);
            chunk.Bytes.CopyTo(output, offset + EntryHeader);
            offset += EntryHeader + chunk.Bytes.Length;
        }
        return output;
    }

    public static List<RecordedChunk> Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Magic.Length || !bytes[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a GreenGlow Connect recording");
        }
        if (bytes.Length <= Magic.Length || bytes[Magic.Length] != Version)
        {
            var version = bytes.Length > Magic.Length ? bytes[Magic.Length].ToString() : "undefined";
            throw new InvalidDataException($"Unsupported recording version {version}");
        }
        var chunks = new List<RecordedChunk>();
        long offset = Magic.Length + 1;
        while (offset < bytes.Length)
        {
            if (offset + EntryHeader > bytes.Length) throw new InvalidDataException("Recording is truncated");
            var at = (int)offset;
            var direction = bytes[at] == 0 ? Direction.Host : Direction.Client;
            var ms = BinaryPrimitives.ReadUInt32BigEndian(bytes[(at + 1)..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[(at + 5)..]);
            var start = offset + EntryHeader;
            if (start + length > bytes.Length) throw new InvalidDataException("Recording is truncated");
            chunks.Add(new RecordedChunk(direction, ms, bytes.Slice((int)start, (int)length).ToArray()));
            offset = start + length;
        }
        return chunks;
    }
}
