using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FakeIbmI.Tests;

/// <summary>
/// The remote command server, driven with requests laid out as JTOpen 21.0.7 builds them. JTOpen's
/// CommandCall and ProgramCall were also run against the image by hand. The start-server substitute
/// uses the fake's own password code, which the sign-on server tests check against JTOpen's.
/// </summary>
public class RemoteCommandServerTests
{
    private static readonly byte[] ClientSeed = Convert.FromHexString("0102030405060708");

    [Fact]
    public async Task TheAttributeReplyIsLaidOutAsJTOpenReadsIt()
    {
        await using var server = await RemoteCommandServer.StartAsync();
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        var reply = await client.RequestAsync(0x1001, [0, 0, 0x04, 0xB0, 0xF2, 0xF9, 0xF2, 0xF4, 0, 0, 0, 1]);

        Assert.Equal(0x8001, RequestId(reply));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20)));
        Assert.Equal(37, BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(22)));
        Assert.Equal(10, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(34)));
    }

    [Fact]
    public async Task ACommandSucceedsAndOnlyItsNameIsLogged()
    {
        var lines = new List<string>();
        await using var server = await RemoteCommandServer.StartAsync(log: line => { lock (lines) lines.Add(line); });
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");
        await client.RequestAsync(0x1001, [0, 0, 0x04, 0xB0, 0xF2, 0xF9, 0xF2, 0xF4, 0, 0, 0, 1]);

        // Data stream level 10: the command in UTF-16, after its CCSID.
        var text = Encoding.BigEndianUnicode.GetBytes("QSYS/CHGUSRPRF USRPRF(X) PASSWORD(Hush1234)");
        var reply = await client.RequestAsync(0x1002, [5], (0x1104, [0, 0, 0x04, 0xB0, .. text]));

        Assert.Equal(0x8002, RequestId(reply));
        Assert.Equal([0, 0, 0, 0], reply.AsSpan(20, 4).ToArray());
        Assert.Equal(["CMD CHGUSRPRF"], server.Calls);
        lock (lines)
        {
            Assert.Contains(lines, l => l.Contains("command CHGUSRPRF"));
            Assert.DoesNotContain(lines, l => l.Contains("Hush1234"));
        }
    }

    [Fact]
    public async Task AProgramCallZeroFillsItsOutputAndReturnsItsInOutData()
    {
        await using var server = await RemoteCommandServer.StartAsync();
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        // In (11), out (22), in-out (33), in-out compressed (23: 0x33 0x40 repeated 512 times), null (255).
        byte[] compressed = [0x1B, 0x33, 0x40, 0x02, 0x00];
        var reply = await client.CallAsync("QLZAREQ", "QSYS",
            (4, 11, [0, 0, 0, 7]),
            (100, 22, []),
            (8, 33, [0x11, 0x22]),
            (1024, 23, compressed),
            (0, 255, []));

        Assert.Equal(0x8003, RequestId(reply));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20)));
        var outputs = Parameters(reply);
        Assert.Equal(3, outputs.Count);
        Assert.Equal(100, outputs[0].Length);
        Assert.Empty(outputs[0].Data); // the client zero-fills up to the length
        Assert.Equal(8, outputs[1].Length);
        Assert.Equal([0x11, 0x22, 0, 0, 0, 0, 0, 0], outputs[1].Data);
        Assert.Equal(1024, outputs[2].Length);
        Assert.Equal(Enumerable.Repeat(new byte[] { 0x33, 0x40 }, 512).SelectMany(b => b), outputs[2].Data);
        Assert.Equal(["PGM QSYS/QLZAREQ"], server.Calls);
    }

    [Fact]
    public async Task StartServerNamesTheServerJob()
    {
        await using var server = await RemoteCommandServer.StartAsync();
        await using var client = await Client.ConnectAsync(server.Port);

        var start = await client.StartReplyAsync("PRTUSER", "Secret12");

        // 0x111F after the return code: CCSID 37, then number/user/name, as JTOpen splits it.
        Assert.Equal(0x111F, BinaryPrimitives.ReadUInt16BigEndian(start.AsSpan(28)));
        Assert.Equal(37, BinaryPrimitives.ReadInt32BigEndian(start.AsSpan(30)));
        Assert.Matches("^[0-9]{6}/QUSER/QZRCSRVS$", Ebcdic.Decode(start.AsSpan(34)));
    }

    [Fact]
    public async Task RetrieveCurrentAttributesAnswersTheJobName()
    {
        await using var server = await RemoteCommandServer.StartAsync();
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        // As JTOpen's getJobInfo calls it: receiver (46), its length, RTVC0100, 1 attribute, key 1009, error code.
        var reply = await client.CallAsync("QWCRTVCA", "QSYS",
            (46, 22, []), (4, 11, [0, 0, 0, 46]), (8, 11, Ebcdic.Encode("RTVC0100")), (4, 11, [0, 0, 0, 1]),
            (4, 11, [0, 0, 0x03, 0xF1]), (8, 11, []));

        var receiver = Assert.Single(Parameters(reply)).Data;
        Assert.Equal(46, receiver.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(receiver));
        Assert.Equal(1009, BinaryPrimitives.ReadInt32BigEndian(receiver.AsSpan(8)));
        Assert.Matches("^QZRCSRVS  QUSER     [0-9]{6}$", Ebcdic.Decode(receiver.AsSpan(20, 26)));
    }

    [Fact]
    public async Task CheckUserFunctionUsageSaysAllowedAndLogsTheFunction()
    {
        var lines = new List<string>();
        await using var server = await RemoteCommandServer.StartAsync(log: line => { lock (lines) lines.Add(line); });
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        var reply = await client.CallAsync("QSYCKUFU", "QSYS",
            (1, 22, []), (30, 11, Ebcdic.Encode("QIBM_XE1_EXAMPLE".PadRight(30))), (10, 11, Ebcdic.Encode("PRTUSER   ")), (4, 11, []));

        Assert.Equal([0xF1], Assert.Single(Parameters(reply)).Data); // EBCDIC '1': allowed
        lock (lines) Assert.Contains(lines, l => l.Contains("QSYS/QSYCKUFU") && l.Contains("QIBM_XE1_EXAMPLE allowed for PRTUSER"));
    }

    [Fact]
    public async Task ADisabledUserIsRefusedAtStartServer()
    {
        await using var server = await RemoteCommandServer.StartAsync();

        var rc = await Client.StartServerAsync(server.Port, "LOCKUSER", "Locked12");

        Assert.Equal(0x00020002, rc);
        Assert.Equal(1, server.Refusals);
    }

    [Fact]
    public async Task AnUnknownRequestGetsInvalidRequestId()
    {
        await using var server = await RemoteCommandServer.StartAsync();
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        var reply = await client.RequestAsync(0x1009, []);

        Assert.Equal(0x9009, RequestId(reply));
        Assert.Equal(0x0205, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20)));
    }

    [Fact]
    public void RunLengthCompressionDecompressesAsJTOpenWritesIt()
    {
        byte[] source = [0x01, 0x1B, 0x1B, 0x1B, 0x00, 0x00, 0x00, 0x03, 0x02];

        Assert.Equal([0x01, 0x1B, 0, 0, 0, 0, 0, 0, 0x02, 0], Rle.Decompress(source, 10));
    }

    private static int RequestId(byte[] reply) => BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(18));

    /// <summary>The reply's parameters after the return code and message count: (length, data) each.</summary>
    private static List<(int Length, byte[] Data)> Parameters(byte[] reply)
    {
        var list = new List<(int, byte[])>();
        for (var at = 24; at + 12 <= reply.Length;)
        {
            var ll = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(at));
            Assert.Equal(0x1103, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(at + 4)));
            list.Add((BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(at + 6)), reply.AsSpan(at + 12, ll - 12).ToArray()));
            at += ll;
        }
        return list;
    }

    private sealed class Client : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;

        private Client(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
        }

        /// <summary>Exchange random seeds and start server, as JTOpen opens the server; returns the start server return code.</summary>
        public static async Task<int> StartServerAsync(int port, string user, string password)
        {
            await using var client = await ConnectAsync(port);
            return await client.StartAsync(user, password);
        }

        public static async Task<Client> SignOnAsync(int port, string user, string password)
        {
            var client = await ConnectAsync(port);
            Assert.Equal(0, await client.StartAsync(user, password));
            return client;
        }

        public static async Task<Client> ConnectAsync(int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            return new Client(tcp);
        }

        private async Task<int> StartAsync(string user, string password) =>
            BinaryPrimitives.ReadInt32BigEndian((await StartReplyAsync(user, password)).AsSpan(20));

        public async Task<byte[]> StartReplyAsync(string user, string password)
        {
            var seeds = await RequestAsync(0x7001, ClientSeed);
            var level = seeds[5];
            var serverSeed = seeds.AsSpan(24, 8).ToArray();
            var substitute = Passwords.Substitute(level, Passwords.Token(level, user, password), user, ClientSeed, serverSeed, 1);
            return await RequestAsync(0x7002, [level < 2 ? (byte)1 : (byte)3, 1, 0],
                (0x1105, substitute), (0x1104, Ebcdic.Encode(user.PadRight(10))));
        }

        /// <summary>RCCallProgramRequestDataStream: program, library, message option, count, then each parameter.</summary>
        public Task<byte[]> CallAsync(string program, string library, params (int MaxLength, int Usage, byte[] Data)[] parameters)
        {
            byte[] template = [.. Ebcdic.Encode(program.PadRight(10)), .. Ebcdic.Encode(library.PadRight(10)), 5,
                (byte)(parameters.Length >> 8), (byte)parameters.Length];
            var points = parameters.Select(p =>
            {
                var data = new byte[6 + p.Data.Length];
                BinaryPrimitives.WriteInt32BigEndian(data, p.MaxLength);
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), (ushort)p.Usage);
                p.Data.CopyTo(data, 6);
                return (0x1103, data);
            }).ToArray();
            return RequestAsync(0x1003, template, points);
        }

        public async Task<byte[]> RequestAsync(ushort requestId, byte[] template, params (int Id, byte[] Data)[] codePoints)
        {
            var frame = new byte[20 + template.Length + codePoints.Sum(c => 6 + c.Data.Length)];
            BinaryPrimitives.WriteInt32BigEndian(frame, frame.Length);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), 0xE008);
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12), 3);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), (ushort)template.Length);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18), requestId);
            template.CopyTo(frame, 20);
            var at = 20 + template.Length;
            foreach (var (id, data) in codePoints)
            {
                BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(at), 6 + data.Length);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(at + 4), (ushort)id);
                data.CopyTo(frame, at + 6);
                at += 6 + data.Length;
            }
            await _stream.WriteAsync(frame);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var header = new byte[20];
            await _stream.ReadExactlyAsync(header, timeout.Token);
            var reply = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
            header.CopyTo(reply, 0);
            await _stream.ReadExactlyAsync(reply.AsMemory(20), timeout.Token);
            Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(12)));
            return reply;
        }

        public ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
