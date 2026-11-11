// A scripted fake of the IBM i remote command host server (as-rmtcmd, server ID E008, port 8475),
// laid out as JTOpen's client reads and writes it:
//   7001/7002 start server (see HostServer.StartServerAsync), checking the system's Users
//   1001 exchange attributes -> 8001: return code, CCSID 37, NLV 2924, version, data stream level
//   1002 run command         -> 8002: return code 0, no messages
//   1003 call program        -> 8003: return code 0, each output parameter zero-filled
// It runs nothing. Every command and program succeeds, and a program's output is zeros, which an
// IBM API reads as an empty error code: success with nothing returned. It logs each command's
// name and each program's name and parameter sizes, but never a command's text or a
// parameter's contents, which can hold passwords.

using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace FakeIbmI;

public sealed class RemoteCommandServer : HostServer
{
    /// <summary>IBM's port for it.</summary>
    public const int DefaultPort = 8475;

    private const ushort ServerId = 0xE008;
    private const int Ccsid = 37;

    /// <summary>The data stream level it claims. 10 and up sends commands in UTF-16 (CCSID 1200).</summary>
    private const ushort DataStreamLevel = 10;

    // Parameter usages in a call program request, as JTOpen sends them.
    private const int Output = 22;
    private const int InOutCompressed = 23;
    private const int InOut = 13;
    private const int InOutLevel5 = 33;
    private const int InputCompressed = 21;

    private readonly List<string> _calls = [];
    private int _signOns;
    private int _refusals;

    private RemoteCommandServer(int port, Action<string>? log, IPAddress? address, Users? users, TlsOptions? tls)
        : base(port, log, address, tls)
    {
        Users = users ?? Users.Defaults();
    }

    /// <summary>
    /// Listens on the loopback interface, or on <paramref name="address"/>. Port 0 picks a free port
    /// (see <see cref="HostServer.Port"/>). Pass the other servers' <paramref name="users"/> to share
    /// them (the default is <see cref="Users.Defaults"/>). With <paramref name="tls"/>, it also listens for TLS.
    /// </summary>
    public static Task<RemoteCommandServer> StartAsync(int port = 0, Action<string>? log = null, IPAddress? address = null,
        Users? users = null, TlsOptions? tls = null)
    {
        var server = new RemoteCommandServer(port, log, address, users, tls);
        server.Start();
        return Task.FromResult(server);
    }

    /// <summary>The user profiles it signs on.</summary>
    public Users Users { get; }

    /// <summary>Successful sign-ons so far.</summary>
    public int SignOns => Volatile.Read(ref _signOns);

    /// <summary>Refused sign-ons so far.</summary>
    public int Refusals => Volatile.Read(ref _refusals);

    /// <summary>What it has been asked so far: "CMD name" for a command, "PGM LIB/NAME" for a program call.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls) return [.. _calls];
        }
    }

    protected override string LogName => "rmtcmd";

    protected override async Task ServeAsync(Stream stream, int id, bool tls)
    {
        var profile = await StartServerAsync(stream, id, ServerId, Users);
        Interlocked.Increment(ref profile is null ? ref _refusals : ref _signOns);
        if (profile is null) return;

        while (true)
        {
            var request = await ReadFrameAsync(stream, Stopping);
            if (ServerIdOf(request) != ServerId) throw new InvalidDataException("not a remote command request");
            var correlation = CorrelationOf(request);
            byte[] reply = RequestIdOf(request) switch
            {
                0x1001 => Frame(ServerId, 0x8001,
                    [0, 0, .. BigEndian(Ccsid), .. Ebcdic.Encode("2924"), .. BigEndian(1), DataStreamLevel >> 8, DataStreamLevel & 0xFF],
                    correlation),
                0x1002 => RunCommand(request, id, correlation),
                0x1003 => CallProgram(request, id, correlation),
                // 0x0205: invalid request ID.
                var other => Frame(ServerId, (ushort)(other | 0x8000), [0x02, 0x05, 0, 0], correlation),
            };
            await stream.WriteAsync(reply, Stopping);
        }
    }

    /// <summary>1002: the message option byte, then the command: 0x1104 (CCSID, then text) or 0x1101 (EBCDIC).</summary>
    private byte[] RunCommand(byte[] request, int id, int correlation)
    {
        var points = CodePoints(request);
        string command;
        if (points.TryGetValue(0x1104, out var withCcsid) && withCcsid.Length >= 4)
        {
            var ccsid = BinaryPrimitives.ReadInt32BigEndian(withCcsid);
            command = ccsid == 1200 ? Encoding.BigEndianUnicode.GetString(withCcsid, 4, withCcsid.Length - 4) : Ebcdic.Decode(withCcsid.AsSpan(4));
        }
        else command = Ebcdic.Decode(points.GetValueOrDefault(0x1101) ?? []);
        var name = CommandName(command);
        Record($"CMD {name}");
        Log($"rmtcmd #{id} command {name}: ran nothing, answered success");
        // Template: return code (2 bytes), message count (2 bytes).
        return Frame(ServerId, 0x8002, [0, 0, 0, 0], correlation);
    }

    /// <summary>
    /// 1003: program (10, EBCDIC), library (10), message option, parameter count; then each
    /// parameter as 0x1103: max length (4), usage (2), data. The reply has the return code and
    /// message count, then each output and in-out parameter as 0x1103: length (4), usage (2), data.
    /// </summary>
    private byte[] CallProgram(byte[] request, int id, int correlation)
    {
        var program = Ebcdic.Decode(request.AsSpan(20, 10)).TrimEnd();
        var library = Ebcdic.Decode(request.AsSpan(30, 10)).TrimEnd();
        var count = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(41));

        var outputs = new List<(int Id, byte[] Data)>();
        var sizes = new List<string>();
        var at = 43;
        for (var i = 0; i < count; i++)
        {
            if (at + 12 > request.Length) throw new InvalidDataException("parameter past the end");
            var length = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(at));
            if (length < 12 || at + length > request.Length) throw new InvalidDataException("bad parameter length");
            var maxLength = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(at + 6));
            var usage = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(at + 10));
            var data = request.AsSpan(at + 12, length - 12);
            at += length;

            switch (usage)
            {
                case Output:
                    sizes.Add($"out {maxLength}");
                    if (maxLength > 0) outputs.Add((0x1103, OutputParameter(maxLength, 2, [])));
                    break;
                case InOut or InOutLevel5 or InOutCompressed:
                    sizes.Add($"in-out {maxLength}");
                    // In-out data comes back as it went (trailing zeros were left off it).
                    var input = usage == InOutCompressed ? Rle.Decompress(data, maxLength) : data.ToArray();
                    if (maxLength > 0) outputs.Add((0x1103, OutputParameter(maxLength, 3, input)));
                    break;
                case 0xFF:
                    sizes.Add("null");
                    break;
                default:
                    sizes.Add($"in {maxLength}{(usage == InputCompressed ? " (compressed)" : "")}");
                    break;
            }
        }

        Record($"PGM {library}/{program}");
        Log($"rmtcmd #{id} call {library}/{program} ({(sizes.Count == 0 ? "no parameters" : string.Join(", ", sizes))}): ran nothing, answered success");
        return Frame(ServerId, 0x8003, [0, 0, 0, 0], correlation, 0, [.. outputs]);
    }

    /// <summary>An output parameter's code point data: its length, usage, then the data (the client zero-fills the rest).</summary>
    private static byte[] OutputParameter(int length, ushort usage, byte[] data)
    {
        var sent = data.Length > length ? data[..length] : data;
        return [.. BigEndian(length), (byte)(usage >> 8), (byte)usage, .. sent];
    }

    /// <summary>The command's name, without its library or parameters.</summary>
    private static string CommandName(string command)
    {
        var name = command.TrimStart().Split([' ', '('], 2)[0];
        name = name[(name.LastIndexOf('/') + 1)..].ToUpperInvariant();
        return name.Length is > 0 and <= 10 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '#' or '$' or '@' or '_')
            ? name
            : "(not a command name)";
    }

    private void Record(string call)
    {
        lock (_calls) _calls.Add(call);
    }
}

/// <summary>JTOpen's run-length compression for large parameters: 1B 1B is a 1B; 1B a b nn nn is a b, nnnn times.</summary>
internal static class Rle
{
    private const byte Escape = 0x1B;

    public static byte[] Decompress(ReadOnlySpan<byte> source, int length)
    {
        var result = new byte[length];
        var j = 0;
        for (var i = 0; i < source.Length && j < length;)
        {
            if (source[i] != Escape)
            {
                result[j++] = source[i++];
            }
            else if (i + 1 < source.Length && source[i + 1] == Escape)
            {
                result[j++] = Escape;
                i += 2;
            }
            else if (i + 4 < source.Length)
            {
                var count = (source[i + 3] << 8) | source[i + 4];
                for (var k = 0; k < count && j < length; k++)
                {
                    result[j++] = source[i + 1];
                    if (j < length) result[j++] = source[i + 2];
                }
                i += 5;
            }
            else throw new InvalidDataException("bad compressed parameter");
        }
        return result;
    }
}
