// A scripted fake of the IBM i Network Print server (as-netprt, server ID E003) for automated
// tests: the random seed exchange and Start Server, checking one placeholder user and password
// with the DES (QPWDLVL 0-1) or SHA-1 (2-3) password substitute. It checks substitutes its own
// way (the DES one as a CBC MAC), not with the server's code. It never logs a substitute.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace FakeIbmI;

public sealed class NetworkPrintServer : IAsyncDisposable
{
    public const string DefaultUser = "PRTUSER";
    public const string DefaultPassword = "Secret12";

    private const ushort ServerId = 0xE003;

    private readonly TcpListener _listener;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly List<Task> _tasks = [];
    private readonly HashSet<TcpClient> _clients = [];
    private readonly CancellationTokenSource _stop = new();
    private Task _acceptLoop = Task.CompletedTask;
    private int _signOns;
    private int _refusals;
    private int _nextId;

    private NetworkPrintServer(TcpListener listener, Action<string>? log)
    {
        _listener = listener;
        _log = log;
    }

    /// <summary>
    /// Listens on the loopback interface, or on <paramref name="address"/> (the demo container
    /// listens on every interface). Port 0 picks a free port (see <see cref="Port"/>).
    /// </summary>
    public static Task<NetworkPrintServer> StartAsync(int port = 0, Action<string>? log = null, IPAddress? address = null)
    {
        var listener = new TcpListener(address ?? IPAddress.Loopback, port);
        listener.Start();
        var server = new NetworkPrintServer(listener, log);
        server._acceptLoop = server.AcceptLoopAsync();
        return Task.FromResult(server);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The one user that may sign on, upper case.</summary>
    public string User { get; set; } = DefaultUser;

    public string Password { get; set; } = DefaultPassword;

    /// <summary>QPWDLVL: 0 or 1 for DES substitutes, 2 or 3 for SHA-1.</summary>
    public int PasswordLevel { get; set; } = 2;

    /// <summary>When set, Start Server answers with this return code whatever is sent.</summary>
    public int? Refuse { get; set; }

    /// <summary>Successful sign-ons so far.</summary>
    public int SignOns => Volatile.Read(ref _signOns);

    /// <summary>Refused sign-ons so far.</summary>
    public int Refusals => Volatile.Read(ref _refusals);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        TcpClient[] clients;
        lock (_gate) clients = [.. _clients];
        foreach (var client in clients) client.Close();
        try
        {
            await _acceptLoop;
        }
        catch (Exception e) when (e is ObjectDisposedException or SocketException or OperationCanceledException)
        {
        }
        Task[] tasks;
        lock (_gate) tasks = [.. _tasks];
        await Task.WhenAll(tasks);
        _stop.Dispose();
    }

    private void Log(string line) => _log?.Invoke(line);

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is ObjectDisposedException or SocketException or OperationCanceledException)
            {
                return;
            }
            var id = Interlocked.Increment(ref _nextId);
            lock (_gate)
            {
                _clients.Add(client);
                _tasks.Add(Task.Run(() => RunAsync(client, id)));
            }
        }
    }

    private async Task RunAsync(TcpClient client, int id)
    {
        try
        {
            Log($"print #{id} connected");
            var stream = client.GetStream();
            if (!await SignOnAsync(stream, id)) return;
            var open = new Dictionary<int, (SpooledFile File, int Offset)>();
            while (true)
            {
                var request = await ReadFrameAsync(stream, _stop.Token);
                foreach (var reply in Answer(request, open)) await stream.WriteAsync(reply, _stop.Token);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException
                                      or EndOfStreamException or InvalidDataException)
        {
        }
        finally
        {
            lock (_gate) _clients.Remove(client);
            client.Close();
            Log($"print #{id} closed");
        }
    }

    private async Task<bool> SignOnAsync(NetworkStream stream, int id)
    {
        var seedRequest = await ReadFrameAsync(stream, _stop.Token);
        Require(seedRequest, 0x7001);
        var clientSeed = seedRequest.AsSpan(20, 8).ToArray();
        var serverSeed = RandomNumberGenerator.GetBytes(8);
        var level = PasswordLevel;
        var seedReply = Frame(0xF001, [0, 0, 0, 0, .. serverSeed], (byte)level);
        await stream.WriteAsync(seedReply, _stop.Token);

        var start = await ReadFrameAsync(stream, _stop.Token);
        Require(start, 0x7002);
        var scheme = start[20];
        var points = CodePoints(start, 20 + BinaryPrimitives.ReadUInt16BigEndian(start.AsSpan(16)));
        var user = Ebcdic.Decode(points[0x1104]).TrimEnd();
        var substitute = points[0x1105];

        int rc;
        if (Refuse is { } refuse) rc = refuse;
        else if (user != User) rc = 0x00020001;
        else if (scheme != (level < 2 ? 0x01 : 0x03)) rc = 0x00010009;
        else rc = substitute.AsSpan().SequenceEqual(Expected(level, clientSeed, serverSeed)) ? 0 : 0x0003000B;
        await stream.WriteAsync(Frame(0xF002, [(byte)(rc >> 24), (byte)(rc >> 16), (byte)(rc >> 8), (byte)rc]), _stop.Token);
        if (rc != 0)
        {
            Interlocked.Increment(ref _refusals);
            Log($"print #{id} sign-on refused ({rc:X8})");
            return false;
        }
        Interlocked.Increment(ref _signOns);
        Log($"print #{id} signed on as {user}");
        return true;
    }

    // ---- Network Print requests, after sign-on ----

    private const ushort SpooledFileObject = 0x0001;
    private const ushort ServerObject = 0x0008;

    /// <summary>The replies to one request: a 12-byte template (action, flags, return code) and code points.</summary>
    private IEnumerable<byte[]> Answer(byte[] request, Dictionary<int, (SpooledFile File, int Offset)> open)
    {
        var objectType = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(18));
        var correlation = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(12));
        var action = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(20));
        var points = CodePoints(request, 20 + BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(16)));
        Interlocked.Increment(ref _requests);

        byte[] Reply(int rc, bool last = true, params (int Id, byte[] Data)[] codePoints) =>
            NpFrame((ushort)(objectType | 0x8000), correlation, action, rc, last, codePoints);

        switch (objectType, action)
        {
            case (ServerObject, 0x000F):
                yield return Reply(0, true, (0x0008, Attributes((0x008A, 37))));
                yield break;

            case (SpooledFileObject, 0x0016):
            {
                var selection = ReadAttributes(points.GetValueOrDefault(0x0006) ?? []);
                if (selection.GetValueOrDefault(0x003E) as string != "*CURRENT")
                {
                    yield return Reply(0x0007);
                    yield break;
                }
                var files = Files.ToList();
                if (files.Count == 0)
                {
                    yield return Reply(0x0014);
                    yield break;
                }
                for (var i = 0; i < files.Count; i++)
                {
                    var f = files[i];
                    yield return Reply(0, i == files.Count - 1,
                        (0x0001, Attributes((0x003B, f.JobName), (0x003E, User), (0x003C, f.JobNumber), (0x0068, f.Name), (0x0069, f.Number))),
                        (0x0008, Attributes((0x0022, f.Date), (0x0030, "*STD"), (0x003B, f.JobName), (0x003C, f.JobNumber),
                            (0x003E, User), (0x0049, "QUSRSYS"), (0x004A, f.Queue), (0x005A, f.DeviceType), (0x0068, f.Name),
                            (0x0069, f.Number), (0x006A, "*READY"), (0x006E, f.Time), (0x006F, f.Pages), (0x0074, f.UserData),
                            (0x00FB, "FAKEIBMI"))));
                }
                yield break;
            }

            case (SpooledFileObject, 0x0002):
            {
                var key = ReadAttributes(points.GetValueOrDefault(0x0001) ?? []);
                var file = Files.FirstOrDefault(f => Equals(key.GetValueOrDefault(0x0068), f.Name)
                                                     && Equals(key.GetValueOrDefault(0x003C), f.JobNumber)
                                                     && Equals(key.GetValueOrDefault(0x0069), f.Number));
                if (file is null)
                {
                    yield return Reply(0x001E);
                    yield break;
                }
                if (file.Unauthorized)
                {
                    yield return Reply(0x0009, true,
                        (0x0008, Attributes((0x0093, "CPF3303"), (0x0080, "Not authorized to spooled file (placeholder)."))));
                    yield break;
                }
                var handle = Interlocked.Increment(ref _nextHandle);
                open[handle] = (file, 0);
                yield return Reply(0, true, (0x000C, BigEndian(handle)));
                yield break;
            }

            case (SpooledFileObject, 0x0003):
            {
                var handle = BinaryPrimitives.ReadInt32BigEndian(points[0x000C]);
                var wanted = ReadAttributes(points.GetValueOrDefault(0x0008) ?? []).GetValueOrDefault(0x007D) as int? ?? 0;
                var (file, offset) = open[handle];
                if (offset >= file.Data.Length)
                {
                    yield return Reply(0x0013);
                    yield break;
                }
                var chunk = file.Data.AsSpan(offset, Math.Min(Math.Min(wanted, ReadChunk), file.Data.Length - offset)).ToArray();
                open[handle] = (file, offset + chunk.Length);
                yield return Reply(0, true, (0x0009, chunk));
                yield break;
            }

            case (SpooledFileObject, 0x0005):
                open.Remove(BinaryPrimitives.ReadInt32BigEndian(points[0x000C]));
                Interlocked.Increment(ref _closes);
                yield return Reply(0);
                yield break;

            default:
                yield return Reply(0x0003);
                yield break;
        }
    }

    /// <summary>Read replies carry at most this much, so files take several reads.</summary>
    public int ReadChunk { get; set; } = 4096;

    /// <summary>The user's spooled files, in list order.</summary>
    public List<SpooledFile> Files { get; } = SpooledFile.Defaults();

    /// <summary>Network Print requests answered so far, after sign-on.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Spooled files closed so far.</summary>
    public int Closes => Volatile.Read(ref _closes);

    private int _requests;
    private int _closes;
    private int _nextHandle;

    private static byte[] NpFrame(ushort replyId, int correlation, ushort action, int rc, bool last, (int Id, byte[] Data)[] codePoints)
    {
        var length = 20 + 12 + codePoints.Sum(c => 6 + c.Data.Length);
        var frame = new byte[length];
        BinaryPrimitives.WriteInt32BigEndian(frame, length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), ServerId);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12), correlation);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), 12);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18), replyId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(20), action);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(22), last ? 0u : 0x80000000u);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(26), (ushort)rc);
        var at = 32;
        foreach (var (id, data) in codePoints)
        {
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(at), 6 + data.Length);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(at + 4), (ushort)id);
            data.CopyTo(frame, at + 6);
            at += 6 + data.Length;
        }
        return frame;
    }

    /// <summary>An attribute code point's data: count, entry size 12, entries, then values (offsets count the 6-byte prefix).</summary>
    private static byte[] Attributes(params (int Id, object Value)[] attributes)
    {
        var entries = new List<byte>();
        var values = new List<byte>();
        var valuesStart = 6 + 4 + 12 * attributes.Length;
        foreach (var (id, value) in attributes)
        {
            byte[] bytes = value is int n ? BigEndian(n) : [.. Ebcdic.Encode((string)value), 0];
            ushort type = value is int ? (ushort)3 : (ushort)5;
            entries.AddRange([(byte)(id >> 8), (byte)id, (byte)(type >> 8), (byte)type]);
            entries.AddRange(BigEndian(bytes.Length));
            entries.AddRange(BigEndian(valuesStart + values.Count));
            values.AddRange(bytes);
        }
        return [(byte)(attributes.Length >> 8), (byte)attributes.Length, 0, 12, .. entries, .. values];
    }

    private static Dictionary<int, object> ReadAttributes(byte[] data)
    {
        var result = new Dictionary<int, object>();
        if (data.Length < 4) return result;
        var count = BinaryPrimitives.ReadUInt16BigEndian(data);
        for (var i = 0; i < count; i++)
        {
            var entry = data.AsSpan(4 + 12 * i, 12);
            var id = BinaryPrimitives.ReadUInt16BigEndian(entry);
            var type = BinaryPrimitives.ReadUInt16BigEndian(entry[2..]);
            var length = BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
            var value = data.AsSpan(BinaryPrimitives.ReadInt32BigEndian(entry[8..]) - 6, length);
            result[id] = type == 3 ? BinaryPrimitives.ReadInt32BigEndian(value) : Ebcdic.Decode(value.TrimEnd((byte)0));
        }
        return result;
    }

    private static byte[] BigEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private byte[] Expected(int level, byte[] clientSeed, byte[] serverSeed)
    {
        var id = User.PadRight(10);
        if (level >= 2)
        {
            var idBytes = Encoding.BigEndianUnicode.GetBytes(id);
            var token = SHA1.HashData([.. idBytes, .. Encoding.BigEndianUnicode.GetBytes(Password)]);
            return SHA1.HashData([.. token, .. serverSeed, .. clientSeed, .. idBytes, 0, 0, 0, 0, 0, 0, 0, 1]);
        }

        // DES: the token enciphers the (folded) user ID under the password, shifted; the
        // substitute is a CBC MAC under the token over five blocks.
        var userId = Ebcdic.Encode(id);
        var password = Password.ToUpperInvariant();
        if (char.IsAsciiDigit(password[0])) password = "Q" + password;
        var pwd = Ebcdic.Encode(password.PadRight(10));
        var folded = Fold(userId);
        var length = Array.FindIndex(pwd, b => b == 0x40) is var n and >= 0 ? n : 10;
        var desToken = Des(Key(pwd.AsSpan(0, Math.Min(8, length))), folded);
        if (length > 8)
        {
            var second = Des(Key(pwd.AsSpan(8, length - 8)), folded);
            desToken = BitConverter.GetBytes(BitConverter.ToUInt64(desToken) ^ BitConverter.ToUInt64(second));
        }
        var seq = BinaryPrimitives.ReadUInt64BigEndian(serverSeed) + 1;
        var rdr = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(rdr, seq);
        byte[] blocks =
        [
            .. rdr,
            .. clientSeed,
            .. userId.Take(8).Select((b, i) => (byte)(b ^ rdr[i])),
            .. new byte[] { userId[8], userId[9], 0x40, 0x40, 0x40, 0x40, 0x40, 0x40 }.Select((b, i) => (byte)(b ^ rdr[i])),
            0, 0, 0, 0, 0, 0, 0, 1,
        ];
        using var des = System.Security.Cryptography.DES.Create();
        des.Key = desToken;
        return des.EncryptCbc(blocks, new byte[8], PaddingMode.None)[^8..];
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
        using var des = System.Security.Cryptography.DES.Create();
        des.Key = key;
        return des.EncryptEcb(block, PaddingMode.None);
    }

    private static Dictionary<int, byte[]> CodePoints(byte[] frame, int at)
    {
        var points = new Dictionary<int, byte[]>();
        while (at + 6 <= frame.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(at));
            if (length < 6 || at + length > frame.Length) throw new InvalidDataException("bad code point");
            points[BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(at + 4))] = frame.AsSpan(at + 6, length - 6).ToArray();
            at += length;
        }
        return points;
    }

    private static void Require(byte[] frame, ushort requestId)
    {
        if (BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(6)) != ServerId
            || BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(18)) != requestId)
            throw new InvalidDataException($"expected request {requestId:X4}");
    }

    private static byte[] Frame(ushort replyId, byte[] template, byte serverAttributes = 0)
    {
        var frame = new byte[20 + template.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, frame.Length);
        frame[5] = serverAttributes;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), ServerId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), (ushort)template.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18), replyId);
        template.CopyTo(frame, 20);
        return frame;
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken cancel)
    {
        var header = new byte[20];
        await stream.ReadExactlyAsync(header, cancel);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 20 || length > 1 << 20) throw new InvalidDataException("bad length");
        var frame = new byte[length];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(20), cancel);
        return frame;
    }
}

/// <summary>One spooled file the fake lists and serves. Placeholder text only.</summary>
public sealed record SpooledFile(
    string Name, int Number, string JobName, string JobNumber, string DeviceType, int Pages, string UserData, string Queue,
    string Date, string Time, byte[] Data)
{
    /// <summary>Opening it answers with a CPF message instead.</summary>
    public bool Unauthorized { get; init; }

    /// <summary>
    /// A two-page *SCS report 132 columns wide, an *AFPDS file (bytes that aren't SCS) and a
    /// *USERASCII PDF.
    /// </summary>
    public static List<SpooledFile> Defaults() =>
    [
        new("QSYSPRT", 1, "QPADEV0001", "123456", "*SCS", 2, "PLACEHOLDR", "QPRINT", "1261012", "093000", WideReport()),
        new("QPRTAFP", 2, "QPADEV0001", "123456", "*AFPDS", 1, "", "QPRINT", "1261012", "093500",
            [0x5A, 0x00, 0x10, 0xD3, 0xA8, 0xA8, 0x00, 0x00, 0x00, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8]),
        new("QPDFOUT", 3, "BATCHJOB", "123457", "*USERASCII", 1, "PDFDATA", "QPRINT", "1261012", "100000",
            "%PDF-1.4\n% placeholder spooled PDF\n%%EOF\n"u8.ToArray()),
    ];

    /// <summary>SCS: forms 132 by 66, a heading and lines on page one, a form feed, one line on page two.</summary>
    public static byte[] WideReport()
    {
        List<byte> scs = [0x2B, 0xC1, 0x02, 132, 0x2B, 0xC2, 0x02, 66];
        void Line(string text)
        {
            scs.AddRange(Ebcdic.Encode(text));
            scs.Add(0x15);
        }
        Line("PLACEHOLDER REPORT".PadLeft(75) + "PAGE 1".PadLeft(57));
        for (var i = 1; i <= 40; i++) Line($"ITEM {i:D4}  {new string('X', 20)}  {i * 3,10}");
        scs.Add(0x0C);
        Line("PLACEHOLDER REPORT".PadLeft(75) + "PAGE 2".PadLeft(57));
        Line("END OF REPORT");
        scs.Add(0x0C);
        return [.. scs];
    }
}
