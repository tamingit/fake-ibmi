// A fake of the IBM i server port mapper (as-svrmap, port 449). A client sends a service name in
// ASCII (as-signon, as-netprt, or as-signon-s for its TLS port). JTOpen sends no terminator; ACS
// sends a byte after the name. A trailing NUL, blank or line end is ignored. The client gets
// 5 bytes back: '+' and the port, big-endian, or '-' and zeros when there is no such service.
// One question per connection, then it closes, as JTOpen expects.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FakeIbmI;

public sealed class PortMapper : HostServer
{
    /// <summary>IBM's port for it, below 1024: listening there takes root or CAP_NET_BIND_SERVICE outside a container.</summary>
    public const int DefaultPort = 449;

    private readonly IReadOnlyDictionary<string, int> _services;
    private int _lookups;

    private PortMapper(int port, IReadOnlyDictionary<string, int> services, Action<string>? log, IPAddress? address)
        : base(port, log, address)
    {
        _services = services;
    }

    /// <summary>
    /// Maps each service name in <paramref name="services"/> to its port, and nothing else.
    /// Port 0 picks a free port (see <see cref="HostServer.Port"/>).
    /// </summary>
    public static Task<PortMapper> StartAsync(int port, IReadOnlyDictionary<string, int> services,
        Action<string>? log = null, IPAddress? address = null)
    {
        var mapper = new PortMapper(port, new Dictionary<string, int>(services), log, address);
        mapper.Start();
        return Task.FromResult(mapper);
    }

    /// <summary>Questions answered so far, mapped or not.</summary>
    public int Lookups => Volatile.Read(ref _lookups);

    protected override string LogName => "mapper";

    /// <summary>Its one line per question is enough.</summary>
    protected override bool LogsConnections => false;

    protected override async Task ServeAsync(Stream stream, int id, bool tls)
    {
        // The name is whatever the first read brings, as on an IBM i: clients send it in one write.
        var buffer = new byte[64];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var count = await stream.ReadAsync(buffer, timeout.Token);
        if (count == 0) return;
        var sent = Encoding.ASCII.GetString(buffer, 0, count);
        var name = sent.TrimEnd('\0', ' ', '\r', '\n', '\t');

        var reply = new byte[5];
        var found = _services.TryGetValue(name, out var port);
        reply[0] = (byte)(found ? '+' : '-');
        if (found) BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(1), port);
        await stream.WriteAsync(reply, Stopping);
        Interlocked.Increment(ref _lookups);
        Log(found ? $"mapper #{id} {Printable(sent)} -> port {port}" : $"mapper #{id} {Printable(sent)}: no such service here");
    }

    /// <summary>The name as sent, with anything unprintable (such as a terminating NUL) as \xNN.</summary>
    private static string Printable(string name) =>
        string.Concat(name.Take(32).Select(c => c is > ' ' and <= '~' ? c.ToString() : $"\\x{(int)c:X2}"));
}
