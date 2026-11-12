// A scripted fake of the IBM i Network Print server (as-netprt, server ID E003) for automated
// tests: the random seed exchange and Start Server, checking one placeholder user and password
// (the system's Users) with the DES (QPWDLVL 0-1) or SHA-1 (2-3) password substitute. It checks
// substitutes its own way (see Passwords), not with the server's code. It never logs a substitute.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace FakeIbmI;

public sealed class NetworkPrintServer : HostServer
{
    public const string DefaultUser = Users.DefaultUser;
    public const string DefaultPassword = Users.DefaultPassword;

    private const ushort ServerId = 0xE003;

    private int _signOns;
    private int _refusals;

    private NetworkPrintServer(int port, Action<string>? log, IPAddress? address, Users? users, TlsOptions? tls, Machine? machine)
        : base(port, log, address, tls)
    {
        Users = users ?? Users.Defaults();
        Machine = machine;
    }

    /// <summary>The machine whose programs' printed output it lists after <see cref="Files"/>, or null.</summary>
    public Machine? Machine { get; }

    /// <summary>The placeholder files, then what programs have printed (each as an *SCS file).</summary>
    private List<SpooledFile> AllFiles() => [.. Files, .. Machine?.Store.Spools().Select(SpooledFile.From) ?? []];

    /// <summary>
    /// Listens on the loopback interface, or on <paramref name="address"/> (the demo container
    /// listens on every interface). Port 0 picks a free port (see <see cref="HostServer.Port"/>).
    /// Pass the other servers' <paramref name="users"/> to share them (the default is
    /// <see cref="Users.Defaults"/>). With
    /// <paramref name="tls"/>, it also listens for TLS.
    /// </summary>
    public static Task<NetworkPrintServer> StartAsync(int port = 0, Action<string>? log = null, IPAddress? address = null,
        Users? users = null, TlsOptions? tls = null, Machine? machine = null)
    {
        var server = new NetworkPrintServer(port, log, address, users, tls, machine);
        server.Start();
        return Task.FromResult(server);
    }

    /// <summary>The user profiles it signs on.</summary>
    public Users Users { get; }

    /// <summary>The main user's name (see <see cref="Users.Main"/>), upper case.</summary>
    public string User
    {
        get => Users.Main.User;
        set => Users.Main.User = value;
    }

    /// <summary>The main user's password.</summary>
    public string Password
    {
        get => Users.Main.Password;
        set => Users.Main.Password = value;
    }

    /// <summary>QPWDLVL: 0 or 1 for DES substitutes, 2 or 3 for SHA-1.</summary>
    public int PasswordLevel
    {
        get => Users.PasswordLevel;
        set => Users.PasswordLevel = value;
    }

    /// <summary>When set, Start Server answers with this return code whatever is sent.</summary>
    public int? Refuse { get; set; }

    /// <summary>Successful sign-ons so far.</summary>
    public int SignOns => Volatile.Read(ref _signOns);

    /// <summary>Refused sign-ons so far.</summary>
    public int Refusals => Volatile.Read(ref _refusals);

    protected override string LogName => "print";

    protected override string JobName(int id) => $"{JobNumber(id)}/QUSER/QNPSERVS";

    protected override async Task ServeAsync(Stream stream, int id, bool tls)
    {
        if (!await SignOnAsync(stream, id)) return;
        var open = new Dictionary<int, (SpooledFile File, int Offset)>();
        while (true)
        {
            var request = await ReadFrameAsync(stream, Stopping);
            foreach (var reply in Answer(request, open)) await stream.WriteAsync(reply, Stopping);
        }
    }

    private async Task<bool> SignOnAsync(Stream stream, int id)
    {
        var profile = await StartServerAsync(stream, id, ServerId, Users, Refuse);
        Interlocked.Increment(ref profile is null ? ref _refusals : ref _signOns);
        return profile is not null;
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
        var points = CodePoints(request);
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
                var files = AllFiles();
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
                var file = AllFiles().FirstOrDefault(f => Equals(key.GetValueOrDefault(0x0068), f.Name)
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
}

/// <summary>One spooled file the fake lists and serves. Placeholder text only.</summary>
public sealed record SpooledFile(
    string Name, int Number, string JobName, string JobNumber, string DeviceType, int Pages, string UserData, string Queue,
    string Date, string Time, byte[] Data)
{
    /// <summary>Opening it answers with a CPF message instead.</summary>
    public bool Unauthorized { get; init; }

    /// <summary>Who owns it: the user of the program that printed it. Placeholder files belong to the server's main user.</summary>
    public string? User { get; init; }

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

    /// <summary>What a program printed, as an *SCS file: lines of EBCDIC, 60 to a page, a form feed ending each.</summary>
    public static SpooledFile From(SpoolRecord r)
    {
        List<byte> scs = [0x2B, 0xC1, 0x02, (byte)Math.Min(r.Width, 255), 0x2B, 0xC2, 0x02, 66];
        var lines = r.Text.Length == 0 ? [] : r.Text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0 && i % 60 == 0) scs.Add(0x0C);
            scs.AddRange(Ebcdic.Encode(lines[i]));
            scs.Add(0x15);
        }
        scs.Add(0x0C);
        var c = r.Created;
        return new SpooledFile(r.Name, (int)r.Id, "FAKEIBMI", ((int)(r.Id % 900000) + 100000).ToString(), "*SCS", r.Pages, "", "QPRINT",
            $"{(c.Year - 1900) / 100}{c.Year % 100:00}{c.Month:00}{c.Day:00}", c.ToString("HHmmss"), [.. scs]) { User = r.User };
    }

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
