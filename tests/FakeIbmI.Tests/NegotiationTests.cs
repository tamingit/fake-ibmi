using System.Net.Sockets;
using System.Threading.Channels;

namespace FakeIbmI.Tests;

/// <summary>
/// Telnet negotiation as an IBM i does it, checked with a strict client: one that answers only what
/// it is asked, and ignores a request for an option's value before it has agreed to the option
/// (RFC 1091, 1572). Clients such as IBM i Access Client Solutions may be this strict.
/// </summary>
public sealed class NegotiationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task ItAsksForOneOptionAtATimeAndForValuesOnlyOnceTheClientAgrees()
    {
        await using var host = await TelnetServer.StartAsync();
        await using var client = await StrictClient.ConnectAsync(host.Port);

        Assert.Equal(Negotiate(NegotiationCommand.Do, TelnetOption.NewEnviron), await client.NextAsync());
        await client.ExpectNothingAsync();

        await client.SendAsync(Telnet.EncodeNegotiation(NegotiationCommand.Will, TelnetOption.NewEnviron));
        Assert.Equal(new TelnetEvent.Subnegotiation(TelnetOption.NewEnviron, [1, 0, 3]), await client.NextAsync());
        await client.ExpectNothingAsync();

        await client.SendAsync(Telnet.EncodeSubnegotiation(TelnetOption.NewEnviron, [0, 0, .. "DEVNAME"u8, 1, .. "TESTDEV1"u8]));
        Assert.Equal(Negotiate(NegotiationCommand.Do, TelnetOption.TerminalType), await client.NextAsync());
        await client.ExpectNothingAsync();

        await client.SendAsync(Telnet.EncodeNegotiation(NegotiationCommand.Will, TelnetOption.TerminalType));
        Assert.Equal(new TelnetEvent.Subnegotiation(TelnetOption.TerminalType, [1]), await client.NextAsync());

        await client.SendAsync(Telnet.EncodeSubnegotiation(TelnetOption.TerminalType, [0, .. "IBM-3179-2"u8]));
        Assert.Equal(Negotiate(NegotiationCommand.Do, TelnetOption.EndOfRecord), await client.NextAsync());
        Assert.Equal(Negotiate(NegotiationCommand.Will, TelnetOption.EndOfRecord), await client.NextAsync());
        Assert.Equal(Negotiate(NegotiationCommand.Do, TelnetOption.Binary), await client.NextAsync());
        Assert.Equal(Negotiate(NegotiationCommand.Will, TelnetOption.Binary), await client.NextAsync());

        await client.AgreeToRecordsAsync();
        Assert.Equal(("I902", "TESTDEV1"), StartupResponse(await client.NextAsync()));
        Assert.IsType<TelnetEvent.Record>(await client.NextAsync());
    }

    [Fact]
    public async Task AClientWithoutNewEnvironGetsASessionOnAMadeUpDevice()
    {
        await using var host = await TelnetServer.StartAsync();
        await using var client = await StrictClient.ConnectAsync(host.Port);

        Assert.Equal(Negotiate(NegotiationCommand.Do, TelnetOption.NewEnviron), await client.NextAsync());
        await client.SendAsync(Telnet.EncodeNegotiation(NegotiationCommand.Wont, TelnetOption.NewEnviron));
        Assert.Equal(Negotiate(NegotiationCommand.Do, TelnetOption.TerminalType), await client.NextAsync());

        await client.SendAsync(Telnet.EncodeNegotiation(NegotiationCommand.Will, TelnetOption.TerminalType));
        Assert.Equal(new TelnetEvent.Subnegotiation(TelnetOption.TerminalType, [1]), await client.NextAsync());
        await client.SendAsync(Telnet.EncodeSubnegotiation(TelnetOption.TerminalType, [0, .. "IBM-3477-FC"u8]));
        for (var i = 0; i < 4; i++) await client.NextAsync();
        await client.AgreeToRecordsAsync();

        Assert.Equal(("I902", "QPADEV0001"), StartupResponse(await client.NextAsync()));
    }

    [Fact]
    public async Task AClientThatOffersTheTerminalTypeFirstStillWaitsForNewEnviron()
    {
        await using var host = await TelnetServer.StartAsync();
        await using var client = await StrictClient.ConnectAsync(host.Port);
        Assert.Equal(Negotiate(NegotiationCommand.Do, TelnetOption.NewEnviron), await client.NextAsync());

        await client.SendAsync(Telnet.EncodeNegotiation(NegotiationCommand.Will, TelnetOption.TerminalType));
        Assert.Equal(Negotiate(NegotiationCommand.Do, TelnetOption.TerminalType), await client.NextAsync());
        await client.ExpectNothingAsync();

        await client.SendAsync(Telnet.EncodeNegotiation(NegotiationCommand.Will, TelnetOption.NewEnviron));
        Assert.Equal(new TelnetEvent.Subnegotiation(TelnetOption.NewEnviron, [1, 0, 3]), await client.NextAsync());
        await client.SendAsync(Telnet.EncodeSubnegotiation(TelnetOption.NewEnviron, [0]));
        Assert.Equal(new TelnetEvent.Subnegotiation(TelnetOption.TerminalType, [1]), await client.NextAsync());
    }

    [Fact]
    public async Task ATimingMarkIsAnsweredAndOtherOptionsAreRefused()
    {
        await using var host = await TelnetServer.StartAsync();
        await using var client = await StrictClient.ConnectAsync(host.Port);
        Assert.Equal(Negotiate(NegotiationCommand.Do, TelnetOption.NewEnviron), await client.NextAsync());

        await client.SendAsync(Telnet.EncodeNegotiation(NegotiationCommand.Do, TelnetOption.TimingMark));
        Assert.Equal(Negotiate(NegotiationCommand.Will, TelnetOption.TimingMark), await client.NextAsync());

        const int windowSize = 31;
        await client.SendAsync(Telnet.EncodeNegotiation(NegotiationCommand.Will, windowSize));
        Assert.Equal(Negotiate(NegotiationCommand.Dont, windowSize), await client.NextAsync());

        await client.SendAsync(Telnet.EncodeNegotiation(NegotiationCommand.Do, TelnetOption.SuppressGoAhead));
        Assert.Equal(Negotiate(NegotiationCommand.Wont, TelnetOption.SuppressGoAhead), await client.NextAsync());
    }

    [Fact]
    public async Task AClientThatRefusesTheTerminalTypeIsDisconnected()
    {
        var lines = new List<string>();
        await using var host = await TelnetServer.StartAsync(log: line => { lock (lines) lines.Add(line); });
        await using var client = await StrictClient.ConnectAsync(host.Port);
        await client.NextAsync();
        await client.SendAsync(Telnet.EncodeNegotiation(NegotiationCommand.Wont, TelnetOption.NewEnviron));
        await client.NextAsync();

        await client.SendAsync(Telnet.EncodeNegotiation(NegotiationCommand.Wont, TelnetOption.TerminalType));

        await client.ExpectClosedAsync();
        lock (lines) Assert.Contains(lines, l => l.Contains("won't use TERMINAL-TYPE"));
    }

    [Fact]
    public async Task TheTraceShowsEachStepButNoSecrets()
    {
        var lines = new List<string>();
        await using var host = await TelnetServer.StartAsync(log: line => { lock (lines) lines.Add(line); }, trace: true);
        await using var client = await StrictClient.ConnectAsync(host.Port);
        await client.NextAsync();
        await client.SendAsync(Telnet.EncodeNegotiation(NegotiationCommand.Will, TelnetOption.NewEnviron));
        await client.NextAsync();

        await client.SendAsync(Telnet.EncodeSubnegotiation(TelnetOption.NewEnviron,
            [0, 3, .. "DEVNAME"u8, 1, .. "TESTDEV2"u8, 3, .. "IBMSUBSPW"u8, 1, .. "Hunter2x"u8]));
        await client.NextAsync();

        lock (lines)
        {
            Assert.Contains(lines, l => l.EndsWith("-> DO NEW-ENVIRON"));
            Assert.Contains(lines, l => l.EndsWith("<- WILL NEW-ENVIRON"));
            Assert.Contains(lines, l => l.Contains("USERVAR DEVNAME=TESTDEV2"));
            Assert.Contains(lines, l => l.Contains("USERVAR IBMSUBSPW=(8 characters, not shown)"));
            Assert.DoesNotContain(lines, l => l.Contains("Hunter2x"));
        }
    }

    private static TelnetEvent.Negotiate Negotiate(NegotiationCommand command, int option) => new(command, option);

    /// <summary>The code and device name of a Startup Response Record.</summary>
    private static (string Code, string Device) StartupResponse(TelnetEvent e)
    {
        var data = Assert.IsType<TelnetEvent.Record>(e).Data;
        return (Ebcdic.Decode(data.AsSpan(16, 4)), Ebcdic.Decode(data.AsSpan(28, 10)).TrimEnd());
    }

    /// <summary>A raw Telnet client: sends exactly what a test tells it to, and queues what arrives.</summary>
    private sealed class StrictClient : IAsyncDisposable
    {
        private readonly TcpClient _tcp = new();
        private readonly Channel<TelnetEvent> _events = Channel.CreateUnbounded<TelnetEvent>();
        private NetworkStream _stream = null!;
        private Task _pump = Task.CompletedTask;

        public static async Task<StrictClient> ConnectAsync(int port)
        {
            var client = new StrictClient();
            await client._tcp.ConnectAsync("127.0.0.1", port);
            client._stream = client._tcp.GetStream();
            client._pump = Task.Run(client.PumpAsync);
            return client;
        }

        private async Task PumpAsync()
        {
            var decoder = new TelnetDecoder();
            var buffer = new byte[4096];
            try
            {
                int count;
                while ((count = await _stream.ReadAsync(buffer)) > 0)
                {
                    foreach (var e in decoder.Push(buffer.AsSpan(0, count))) await _events.Writer.WriteAsync(e);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
            {
            }
            _events.Writer.TryComplete();
        }

        public async Task SendAsync(byte[] bytes) => await _stream.WriteAsync(bytes);

        /// <summary>Agrees to end of record and binary, both ways.</summary>
        public async Task AgreeToRecordsAsync()
        {
            foreach (var option in new[] { TelnetOption.EndOfRecord, TelnetOption.Binary })
            {
                await SendAsync(Telnet.EncodeNegotiation(NegotiationCommand.Will, option));
                await SendAsync(Telnet.EncodeNegotiation(NegotiationCommand.Do, option));
            }
        }

        public async Task<TelnetEvent> NextAsync()
        {
            using var timeout = new CancellationTokenSource(Wait);
            return await _events.Reader.ReadAsync(timeout.Token);
        }

        public async Task ExpectNothingAsync()
        {
            await Task.Delay(Quiet);
            Assert.False(_events.Reader.TryRead(out var extra), $"the host sent {extra} before it should have");
        }

        public async Task ExpectClosedAsync()
        {
            using var timeout = new CancellationTokenSource(Wait);
            await foreach (var _ in _events.Reader.ReadAllAsync(timeout.Token))
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            await _pump;
        }
    }
}
