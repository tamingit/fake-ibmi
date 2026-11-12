using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace FakeIbmI.Tests;

/// <summary>
/// The sign-on server, driven with requests laid out as JTOpen 21.0.7 builds them. The
/// substitutes and protected passwords were computed by JTOpen itself (its private
/// AS400ImplRemote methods, called by reflection) for these seeds, so they don't share the
/// fake's password code.
/// </summary>
public class SignOnServerTests
{
    private static readonly byte[] ClientSeed = Convert.FromHexString("0102030405060708");
    private static readonly byte[] ServerSeed = Convert.FromHexString("1112131415161718");

    private const int PasswordIncorrect = 0x0003000B;

    private static async Task<SignOnServer> StartAsync(int level = 2, string user = "PRTUSER", string password = "Secret12")
    {
        var users = new Users(new UserProfile(user, password), new UserProfile("LOCKUSER", password, disabled: true))
        {
            PasswordLevel = level,
        };
        var server = await SignOnServer.StartAsync(users: users);
        server.ServerSeeds = () => ServerSeed;
        return server;
    }

    [Fact]
    public async Task TheAttributeReplyIsLaidOutAsJTOpenReadsIt()
    {
        await using var server = await StartAsync(level: 3);
        await using var client = await Client.ConnectAsync(server.Port);

        var reply = await client.ExchangeAttributesAsync();

        Assert.Equal(0xF003, RequestId(reply));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(20)));
        // JTOpen reads the version and level at fixed offsets, then looks for code points from 42.
        Assert.Equal(0x00070500, BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(30)));
        Assert.Equal(10, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(40)));
        var points = CodePoints(reply, 42);
        Assert.Equal(ServerSeed, points[0x1103]);
        Assert.Equal([3], points[0x1119]);
        Assert.EndsWith("/QUSER/QZSOSIGN", Ebcdic.Decode(points[0x111F].AsSpan(4)));
    }

    [Theory]
    [InlineData(2, "PRTUSER", "Secret12", "68C71B05D26AFAF4E78E0BA7CFFFB8BBAF94ABB8")]
    [InlineData(3, "PRTUSER", "Secret12", "68C71B05D26AFAF4E78E0BA7CFFFB8BBAF94ABB8")]
    [InlineData(0, "PRTUSER", "Secret12", "0CF9D5C8B2A29256")]
    [InlineData(1, "LONGUSER10", "Secret12", "8B852EBA4013AF79")]
    [InlineData(0, "PRTUSER", "12ab", "C0C6F696E6258556")]
    [InlineData(0, "PRTUSER", "TENCHARSPW", "4A655844F0BA73EE")]
    public async Task ItSignsOnWithJTOpensSubstitute(int level, string user, string password, string substitute)
    {
        await using var server = await StartAsync(level, user, password);
        await using var client = await Client.ConnectAsync(server.Port);
        await client.ExchangeAttributesAsync();

        var reply = await client.SignOnAsync(level, user, substitute);

        Assert.Equal(0xF004, RequestId(reply));
        Assert.Equal(0, ReturnCode(reply));
        var points = CodePoints(reply, 24);
        Assert.Equal(37, BinaryPrimitives.ReadInt32BigEndian(points[0x1114]));
        Assert.Equal(7, points[0x1106].Length);
        Assert.Equal(user.PadRight(10), Ebcdic.Decode(points[0x1104].AsSpan(4)));
        Assert.Equal(1, server.SignOns);
    }

    [Fact]
    public async Task AWrongPasswordIsIncorrect()
    {
        await using var server = await StartAsync(password: "Other123");
        await using var client = await Client.ConnectAsync(server.Port);
        await client.ExchangeAttributesAsync();

        var reply = await client.SignOnAsync(2, "PRTUSER", "68C71B05D26AFAF4E78E0BA7CFFFB8BBAF94ABB8");

        Assert.Equal(PasswordIncorrect, ReturnCode(reply));
        Assert.Equal(24, reply.Length);
        Assert.Equal(1, server.Refusals);
    }

    [Fact]
    public async Task AnotherUserIsUnknown()
    {
        await using var server = await StartAsync(user: "SOMEONE");
        await using var client = await Client.ConnectAsync(server.Port);
        await client.ExchangeAttributesAsync();

        var reply = await client.SignOnAsync(2, "PRTUSER", "68C71B05D26AFAF4E78E0BA7CFFFB8BBAF94ABB8");

        Assert.Equal(0x00020001, ReturnCode(reply));
    }

    [Fact]
    public async Task ADisabledUserIsRefusedEvenWithItsPassword()
    {
        // LOCKUSER's password is Secret12 too, so this is its right substitute... for user PRTUSER. The
        // fake answers before it checks the substitute, as an IBM i does for a disabled profile.
        await using var server = await StartAsync();
        await using var client = await Client.ConnectAsync(server.Port);
        await client.ExchangeAttributesAsync();

        var reply = await client.SignOnAsync(2, "LOCKUSER", "68C71B05D26AFAF4E78E0BA7CFFFB8BBAF94ABB8");

        Assert.Equal(0x00020002, ReturnCode(reply));
        Assert.Equal(1, server.Refusals);
    }

    [Fact]
    public async Task ADesSubstituteAtASha1LevelIsRefused()
    {
        await using var server = await StartAsync(level: 2);
        await using var client = await Client.ConnectAsync(server.Port);
        await client.ExchangeAttributesAsync();

        var reply = await client.SignOnAsync(0, "PRTUSER", "0CF9D5C8B2A29256");

        Assert.NotEqual(0, ReturnCode(reply));
        Assert.Equal(0, server.SignOns);
    }

    [Theory]
    [InlineData(2, "Newpass34", "68C71B05D26AFAF4E78E0BA7CFFFB8BBAF94ABB8",
        "EAF4A8BB493216E7D49BFBADFD5EFCA13958482C", "A5EE1B745DE42693C13B81AD21C2B93FD6B75A4F", 16, 18)]
    [InlineData(2, "A much longer passphrase!", "68C71B05D26AFAF4E78E0BA7CFFFB8BBAF94ABB8",
        "939BBD0A773490E4A573204815457027786C92BD",
        "A5E11B315DFE2696C13981B62191B960D6EC5A21EAF1414671320FDA1FADE630989351C327967763C7438054D79058AE7769F895B84B787D484954C4",
        16, 50)]
    [InlineData(0, "NEWPASS34", "0CF9D5C8B2A29256", "B01B4237B0DA5805", "12BCE16D75425E88555A360BA00DBAD2", 0, 0)]
    [InlineData(0, "NEWPW", "0CF9D5C8B2A29256", "6D777CC3A5F560F3", "12BCE16D52E0FC3B", 0, 0)]
    public async Task ItChangesThePasswordWithJTOpensProtectedPasswords(
        int level, string newPassword, string substitute, string oldProtected, string newProtected, int oldLength, int newLength)
    {
        await using var server = await StartAsync(level);
        server.Users.Main.PasswordExpired = true;
        await using var client = await Client.ConnectAsync(server.Port);
        await client.ExchangeAttributesAsync();

        var reply = await client.ChangePasswordAsync(level, "PRTUSER", substitute, oldProtected, newProtected, oldLength, newLength);

        Assert.Equal(0xF005, RequestId(reply));
        Assert.Equal(0, ReturnCode(reply));
        Assert.Equal(newPassword, server.Users.Main.Password);
        Assert.False(server.Users.Main.PasswordExpired);
        Assert.Equal(1, server.PasswordChanges);
    }

    [Fact]
    public async Task AChangeWhoseOldPasswordDoesNotMatchIsRefused()
    {
        await using var server = await StartAsync();
        await using var client = await Client.ConnectAsync(server.Port);
        await client.ExchangeAttributesAsync();

        // JTOpen's request for Secret12 -> Newpass34, with the protected old password's first byte changed.
        var reply = await client.ChangePasswordAsync(2, "PRTUSER", "68C71B05D26AFAF4E78E0BA7CFFFB8BBAF94ABB8",
            "EBF4A8BB493216E7D49BFBADFD5EFCA13958482C", "A5EE1B745DE42693C13B81AD21C2B93FD6B75A4F", 16, 18);

        Assert.Equal(PasswordIncorrect, ReturnCode(reply));
        Assert.Equal("Secret12", server.Users.Main.Password);
    }

    [Fact]
    public async Task AnExpiredPasswordSaysSoOnlyOnceItIsRight()
    {
        await using var server = await StartAsync();
        server.Users.Main.PasswordExpired = true;
        await using var client = await Client.ConnectAsync(server.Port);
        await client.ExchangeAttributesAsync();

        Assert.Equal(0x0003000D, ReturnCode(await client.SignOnAsync(2, "PRTUSER", "68C71B05D26AFAF4E78E0BA7CFFFB8BBAF94ABB8")));
        Assert.Equal(PasswordIncorrect, ReturnCode(await client.SignOnAsync(2, "PRTUSER", new string('0', 40))));
    }

    [Fact]
    public async Task APasswordChangedThroughSignOnHoldsForNetworkPrint()
    {
        var users = Users.Defaults();
        await using var signon = await SignOnServer.StartAsync(users: users);
        await using var print = await NetworkPrintServer.StartAsync(users: users);
        signon.ServerSeeds = () => ServerSeed;
        await using var client = await Client.ConnectAsync(signon.Port);
        await client.ExchangeAttributesAsync();

        await client.ChangePasswordAsync(2, "PRTUSER", "68C71B05D26AFAF4E78E0BA7CFFFB8BBAF94ABB8",
            "EAF4A8BB493216E7D49BFBADFD5EFCA13958482C", "A5EE1B745DE42693C13B81AD21C2B93FD6B75A4F", 16, 18);

        Assert.Equal("Newpass34", print.Password);
    }

    [Fact]
    public async Task EndJobClosesTheConnection()
    {
        await using var server = await StartAsync();
        await using var client = await Client.ConnectAsync(server.Port);
        await client.ExchangeAttributesAsync();

        await client.SendAsync(Request(0x7006, []));

        Assert.True(await client.ClosedAsync());
    }

    [Fact]
    public async Task AnUnknownRequestGetsInvalidRequestId()
    {
        await using var server = await StartAsync();
        await using var client = await Client.ConnectAsync(server.Port);
        await client.ExchangeAttributesAsync();

        await client.SendAsync(Request(0x7007, [0x01]));
        var reply = await client.ReadFrameAsync();

        Assert.Equal(0xF007, RequestId(reply));
        Assert.Equal(0x00010003, ReturnCode(reply));
    }

    // ---- requests as JTOpen builds them ----

    private static byte[] Request(ushort requestId, byte[] template, params (int Id, byte[] Data)[] codePoints)
    {
        var frame = new byte[20 + template.Length + codePoints.Sum(c => 6 + c.Data.Length)];
        BinaryPrimitives.WriteInt32BigEndian(frame, frame.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), 0xE009);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12), 7);
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
        return frame;
    }

    private static byte[] Int(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte Scheme(int level) => level < 2 ? (byte)0x01 : (byte)0x03;

    private static byte[] UserId(string user) => Ebcdic.Encode(user.PadRight(10));

    private static int RequestId(byte[] reply) => BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(18));

    private static int ReturnCode(byte[] reply) => BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(20));

    private static Dictionary<int, byte[]> CodePoints(byte[] frame, int at)
    {
        var points = new Dictionary<int, byte[]>();
        while (at + 6 <= frame.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(at));
            points[BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(at + 4))] = frame.AsSpan(at + 6, length - 6).ToArray();
            at += length;
        }
        return points;
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

        public static async Task<Client> ConnectAsync(int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            return new Client(tcp);
        }

        /// <summary>SignonExchangeAttributeReq: client version 1, data stream level 10, the client seed.</summary>
        public async Task<byte[]> ExchangeAttributesAsync()
        {
            await SendAsync(Request(0x7003, [], (0x1101, Int(1)), (0x1102, [0, 10]), (0x1103, ClientSeed)));
            return await ReadFrameAsync();
        }

        /// <summary>SignonInfoReq: the scheme; the client CCSID, the substitute, the user ID and "return messages".</summary>
        public async Task<byte[]> SignOnAsync(int level, string user, string substitute)
        {
            await SendAsync(Request(0x7004, [Scheme(level)],
                (0x1113, Int(1200)), (0x1105, Convert.FromHexString(substitute)), (0x1104, UserId(user)), (0x1128, [1])));
            return await ReadFrameAsync();
        }

        /// <summary>ChangePasswordReq: SHA-1 adds the two lengths (bytes of UTF-16) and their CCSID.</summary>
        public async Task<byte[]> ChangePasswordAsync(int level, string user, string substitute, string oldProtected,
            string newProtected, int oldLength, int newLength)
        {
            List<(int, byte[])> points =
            [
                (0x1104, UserId(user)), (0x1105, Convert.FromHexString(substitute)),
                (0x110C, Convert.FromHexString(oldProtected)), (0x110D, Convert.FromHexString(newProtected)),
            ];
            if (level >= 2) points.AddRange([(0x111C, Int(oldLength)), (0x111D, Int(newLength)), (0x111E, Int(13488))]);
            points.Add((0x1128, [1]));
            await SendAsync(Request(0x7005, [Scheme(level)], [.. points]));
            return await ReadFrameAsync();
        }

        public async Task SendAsync(byte[] frame) => await _stream.WriteAsync(frame);

        public async Task<byte[]> ReadFrameAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var header = new byte[20];
            await _stream.ReadExactlyAsync(header, timeout.Token);
            var frame = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
            header.CopyTo(frame, 0);
            await _stream.ReadExactlyAsync(frame.AsMemory(20), timeout.Token);
            Assert.Equal(7, BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(12)));
            return frame;
        }

        public async Task<bool> ClosedAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await _stream.ReadAsync(new byte[1], timeout.Token) == 0;
        }

        public ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
