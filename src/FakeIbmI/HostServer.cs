// What the fake's host servers (port mapper, sign-on, Network Print) have in common: a listener,
// and optionally a second one for TLS, that runs each client until it or the server closes, and
// the host server data stream's frames.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace FakeIbmI;

public abstract class HostServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly TcpListener? _tlsListener;
    private readonly TlsOptions? _tls;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly List<Task> _tasks = [];
    private readonly HashSet<TcpClient> _clients = [];
    private readonly CancellationTokenSource _stop = new();
    private Task[] _acceptLoops = [];
    private int _nextId;

    /// <summary>
    /// Listens on the loopback interface, or on <paramref name="address"/> (the demo container
    /// listens on every interface). Port 0 picks a free port (see <see cref="Port"/>). With
    /// <paramref name="tls"/>, it also listens for TLS on that port (see <see cref="TlsPort"/>).
    /// </summary>
    protected HostServer(int port, Action<string>? log, IPAddress? address, TlsOptions? tls = null)
    {
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
        if (tls is not null) _tlsListener = new TcpListener(address ?? IPAddress.Loopback, tls.Port);
        _tls = tls;
        _log = log;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The TLS port, when it listens for TLS.</summary>
    public int? TlsPort => (_tlsListener?.LocalEndpoint as IPEndPoint)?.Port;

    /// <summary>The name its log lines start with.</summary>
    protected abstract string LogName { get; }

    /// <summary>Whether it logs each connection opening and closing.</summary>
    protected virtual bool LogsConnections => true;

    protected CancellationToken Stopping => _stop.Token;

    protected void Start()
    {
        _listener.Start();
        try
        {
            _tlsListener?.Start();
        }
        catch
        {
            _listener.Stop();
            throw;
        }
        _acceptLoops = _tlsListener is null
            ? [AcceptLoopAsync(_listener, tls: false)]
            : [AcceptLoopAsync(_listener, tls: false), AcceptLoopAsync(_tlsListener, tls: true)];
    }

    protected void Log(string line) => _log?.Invoke(line);

    /// <summary>
    /// Serves one client, over TLS when <paramref name="tls"/> (the handshake is done). Returning,
    /// or a dropped or malformed connection, closes it.
    /// </summary>
    protected abstract Task ServeAsync(Stream stream, int id, bool tls);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        _tlsListener?.Stop();
        TcpClient[] clients;
        lock (_gate) clients = [.. _clients];
        foreach (var client in clients) client.Close();
        try
        {
            await Task.WhenAll(_acceptLoops);
        }
        catch (Exception e) when (e is ObjectDisposedException or SocketException or OperationCanceledException)
        {
        }
        Task[] tasks;
        lock (_gate) tasks = [.. _tasks];
        await Task.WhenAll(tasks);
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, bool tls)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is ObjectDisposedException or SocketException or OperationCanceledException)
            {
                return;
            }
            var id = Interlocked.Increment(ref _nextId);
            lock (_gate)
            {
                _clients.Add(client);
                _tasks.Add(Task.Run(() => RunAsync(client, id, tls)));
            }
        }
    }

    private async Task RunAsync(TcpClient client, int id, bool tls)
    {
        if (LogsConnections) Log($"{LogName} #{id} connected{(tls ? " (TLS)" : "")}");
        try
        {
            if (!tls)
            {
                await ServeAsync(client.GetStream(), id, tls: false);
                return;
            }
            await using var secure = await Tls.AcceptAsync(client.GetStream(), _tls!.Certificate, $"{LogName} #{id}",
                Log, _stop.Token);
            if (secure is not null) await ServeAsync(secure, id, tls: true);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException
                                      or EndOfStreamException or InvalidDataException)
        {
        }
        finally
        {
            lock (_gate) _clients.Remove(client);
            client.Close();
            if (LogsConnections) Log($"{LogName} #{id} closed");
        }
    }

    // ---- Start server: how the host servers other than sign-on sign a client on ----

    /// <summary>
    /// Exchange random seeds (7001, its reply carrying the password level in header byte 5) and
    /// start server (7002: the scheme byte, then 0x1104 user and 0x1105 substitute), as JTOpen
    /// opens every host server but sign-on. Returns the profile signed on, or null (logged) when
    /// it was refused with an IBM i's return code, or with <paramref name="refuse"/> when given.
    /// </summary>
    protected async Task<UserProfile?> StartServerAsync(Stream stream, int id, ushort serverId, Users users, int? refuse = null)
    {
        var seedRequest = await ReadFrameAsync(stream, Stopping);
        Require(seedRequest, serverId, 0x7001);
        var clientSeed = seedRequest.AsSpan(20, 8).ToArray();
        var serverSeed = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
        var level = users.PasswordLevel;
        await stream.WriteAsync(Frame(serverId, 0xF001, [0, 0, 0, 0, .. serverSeed], CorrelationOf(seedRequest), (byte)level), Stopping);

        var start = await ReadFrameAsync(stream, Stopping);
        Require(start, serverId, 0x7002);
        var scheme = start[20];
        var points = CodePoints(start);
        var user = points.TryGetValue(0x1104, out var userId) ? Ebcdic.Decode(userId).TrimEnd() : "";
        var substitute = points.GetValueOrDefault(0x1105) ?? [];

        int rc;
        var profile = users.Find(user);
        if (refuse is { } refused) rc = refused;
        else if (profile is null) rc = 0x00020001;
        else if (profile.Disabled) rc = 0x00020002;
        else if (scheme != (level < 2 ? 0x01 : 0x03)) rc = 0x00010009;
        else if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(substitute,
                     Passwords.Substitute(level, Passwords.Token(level, profile.User, profile.Password), profile.User, clientSeed, serverSeed, 1)))
            rc = 0x0003000B;
        else rc = profile.PasswordExpired ? 0x0003000D : 0;
        await stream.WriteAsync(Frame(serverId, 0xF002, BigEndian(rc), CorrelationOf(start)), Stopping);
        if (rc != 0)
        {
            Log($"{LogName} #{id} sign-on refused ({rc:X8})");
            return null;
        }
        Log($"{LogName} #{id} signed on as {profile!.User}");
        return profile;
    }

    private static void Require(byte[] frame, ushort serverId, ushort requestId)
    {
        if (ServerIdOf(frame) != serverId || RequestIdOf(frame) != requestId)
            throw new InvalidDataException($"expected request {requestId:X4}");
    }

    // ---- Host server frames: a 20-byte header, a template, then code points ----

    /// <summary>
    /// A reply: the header (with the request's correlation ID), the template, then each code point
    /// as a 4-byte length, a 2-byte ID and its data.
    /// </summary>
    protected static byte[] Frame(ushort serverId, ushort replyId, byte[] template, int correlation = 0,
        byte serverAttributes = 0, params (int Id, byte[] Data)[] codePoints)
    {
        var frame = new byte[20 + template.Length + codePoints.Sum(c => 6 + c.Data.Length)];
        BinaryPrimitives.WriteInt32BigEndian(frame, frame.Length);
        frame[5] = serverAttributes;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), serverId);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12), correlation);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), (ushort)template.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18), replyId);
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

    protected static ushort ServerIdOf(byte[] frame) => BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(6));

    protected static int CorrelationOf(byte[] frame) => BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(12));

    protected static ushort RequestIdOf(byte[] frame) => BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(18));

    /// <summary>The code points after the template, by ID.</summary>
    protected static Dictionary<int, byte[]> CodePoints(byte[] frame) =>
        CodePoints(frame, 20 + BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(16)));

    protected static Dictionary<int, byte[]> CodePoints(byte[] frame, int at)
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

    protected static byte[] BigEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    protected static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancel)
    {
        var header = new byte[20];
        await stream.ReadExactlyAsync(header, cancel);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 20 || length > 1 << 20) throw new InvalidDataException("bad length");
        var frame = new byte[length];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(20), cancel);
        if (BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(16)) > length - 20) throw new InvalidDataException("bad template length");
        return frame;
    }
}
