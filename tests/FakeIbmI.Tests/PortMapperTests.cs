using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FakeIbmI.Tests;

/// <summary>The port mapper, asked as JTOpen asks it: the bare ASCII name, then 5 bytes back.</summary>
public class PortMapperTests
{
    private static async Task<byte[]> AskAsync(int port, string service)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(service));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = new byte[5];
        await stream.ReadExactlyAsync(reply, timeout.Token);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], timeout.Token));
        return reply;
    }

    [Fact]
    public async Task ItTellsAServicesPort()
    {
        await using var mapper = await PortMapper.StartAsync(0, new Dictionary<string, int> { ["as-signon"] = 8476 });

        var reply = await AskAsync(mapper.Port, "as-signon");

        Assert.Equal((byte)'+', reply[0]);
        Assert.Equal(8476, BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(1)));
    }

    [Theory]
    [InlineData("as-central")]
    [InlineData("as-signon-s")]
    public async Task AServiceItDoesNotRunGetsAMinus(string service)
    {
        await using var mapper = await PortMapper.StartAsync(0, new Dictionary<string, int> { ["as-signon"] = 8476 });

        Assert.Equal([(byte)'-', 0, 0, 0, 0], await AskAsync(mapper.Port, service));
        Assert.Equal(1, mapper.Lookups);
    }

    [Fact]
    public async Task ItLogsTheServiceAsked()
    {
        var lines = new List<string>();
        await using var mapper = await PortMapper.StartAsync(0, new Dictionary<string, int>(), line => { lock (lines) lines.Add(line); });

        await AskAsync(mapper.Port, "as-central");

        lock (lines) Assert.Contains(lines, l => l.Contains("as-central: no such service"));
    }
}
