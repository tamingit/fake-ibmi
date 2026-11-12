// A scripted fake of the IBM i remote command host server (as-rmtcmd, server ID E008, port 8475),
// laid out as JTOpen's client reads and writes it:
//   7001/7002 start server (see HostServer.StartServerAsync), checking the system's Users
//   1001 exchange attributes -> 8001: return code, CCSID 37, NLV 2924, version, data stream level
//   1002 run command         -> 8002: return code 0, no messages
//   1003 call program        -> 8003: return code 0, each output parameter zero-filled
// It runs nothing. Every command and program succeeds. A few programs get real answers (see
// Answer): QWCRTVCA the job's name, QSYCKUFU "allowed". Any other program's output is zeros,
// which an IBM API reads as an empty error code: success with nothing returned. It logs each
// command's name and each program's name and parameter sizes, but never a command's text or a
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

    protected override string JobName(int id) => $"{JobNumber(id)}/QUSER/QZRCSRVS";

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

        var parameters = new List<Parameter>();
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
            // Input comes with its trailing zeros left off, or compressed: back to its full length.
            var input = usage is InputCompressed or InOutCompressed ? Rle.Decompress(data, maxLength) : Padded(data, maxLength);
            var kind = usage switch
            {
                Output => ParameterKind.Output,
                InOut or InOutLevel5 or InOutCompressed => ParameterKind.InOut,
                0xFF => ParameterKind.Null,
                _ => ParameterKind.Input,
            };
            parameters.Add(new Parameter(kind, maxLength, kind == ParameterKind.Output ? [] : input));
        }

        var name = $"{library}/{program}";
        var (answers, answered) = Answer(name, parameters, id);
        var outputs = new List<(int Id, byte[] Data)>();
        for (var i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            if (p.Kind is not (ParameterKind.Output or ParameterKind.InOut) || p.MaxLength == 0) continue;
            // In-out data comes back as it went unless the program answered it.
            var data = answers.GetValueOrDefault(i) ?? (p.Kind == ParameterKind.InOut ? p.Input : []);
            outputs.Add((0x1103, OutputParameter(p.MaxLength, p.Kind == ParameterKind.Output ? (ushort)2 : (ushort)3, data)));
        }

        Record($"PGM {name}");
        var sizes = parameters.Select(p => p.Kind switch
        {
            ParameterKind.Output => $"out {p.MaxLength}",
            ParameterKind.InOut => $"in-out {p.MaxLength}",
            ParameterKind.Null => "null",
            _ => $"in {p.MaxLength}",
        }).ToList();
        Log($"rmtcmd #{id} call {name} ({(sizes.Count == 0 ? "no parameters" : string.Join(", ", sizes))}): ran nothing, {answered}");
        return Frame(ServerId, 0x8003, [0, 0, 0, 0], correlation, 0, [.. outputs]);
    }

    private enum ParameterKind
    {
        Input,
        Output,
        InOut,
        Null,
    }

    /// <summary>One program parameter: its usage, length, and the input it brought (full length).</summary>
    private sealed record Parameter(ParameterKind Kind, int MaxLength, byte[] Input);

    /// <summary>
    /// The output a program gives, by parameter index (any other output is zeros), and what the log
    /// says about it. Only these programs answer; their inputs are names, not secrets.
    /// </summary>
    private (Dictionary<int, byte[]> Outputs, string Answered) Answer(string program, List<Parameter> parameters, int id)
    {
        string Text(int index, int length) =>
            index < parameters.Count ? Ebcdic.Decode(parameters[index].Input.AsSpan(0, Math.Min(length, parameters[index].Input.Length))).TrimEnd() : "";

        switch (program)
        {
            // Retrieve Current Attributes, format RTVC0100: the number of attributes, then each as
            // length, key, type ('C'), 3 reserved, data length, data. Key 1009 is the job's name:
            // name (10), user (10), number (6). JTOpen asks for it when it doesn't know its server job.
            case "QSYS/QWCRTVCA" when parameters.Count >= 5 && parameters[0].Kind == ParameterKind.Output
                                      && BinaryPrimitives.ReadInt32BigEndian(Padded(parameters[4].Input, 4)) == 1009:
            {
                var job = JobName(id).Split('/');
                byte[] data = [.. Ebcdic.Encode($"{job[2],-10}{job[1],-10}{job[0],-6}")];
                byte[] receiver = [.. BigEndian(1), .. BigEndian(16 + data.Length), .. BigEndian(1009), 0xC3, 0, 0, 0, .. BigEndian(data.Length), .. data];
                return (new() { [0] = receiver }, $"answered the job name {job[0]}/{job[1]}/{job[2]}");
            }
            // Check User Function Usage: usage indicator, function ID (30), user (10), error code. The
            // indicator is '1' when the user is NOT allowed to use the function and '2' when it is
            // (IBM's API documentation); ACS refuses the feature on '1'.
            case "QSYS/QSYCKUFU" when parameters.Count >= 3 && parameters[0].Kind == ParameterKind.Output:
                return (new() { [0] = [0xF2] }, $"answered {Text(1, 30)} allowed for {Text(2, 10)}");
            default:
                return ([], "answered success with zeros");
        }
    }

    private static byte[] Padded(ReadOnlySpan<byte> data, int length)
    {
        var result = new byte[Math.Max(length, data.Length)];
        data.CopyTo(result);
        return result;
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
