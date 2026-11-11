// What the fake's host servers (port mapper, sign-on, Network Print) have in common: a listener
// that runs each client until it or the server closes, and the host server data stream's frames.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace FakeIbmI;

public abstract class HostServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly List<Task> _tasks = [];
    private readonly HashSet<TcpClient> _clients = [];
    private readonly CancellationTokenSource _stop = new();
    private Task _acceptLoop = Task.CompletedTask;
    private int _nextId;

    /// <summary>
    /// Listens on the loopback interface, or on <paramref name="address"/> (the demo container
    /// listens on every interface). Port 0 picks a free port (see <see cref="Port"/>).
    /// </summary>
    protected HostServer(int port, Action<string>? log, IPAddress? address)
    {
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
        _log = log;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    protected CancellationToken Stopping => _stop.Token;

    protected void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    protected void Log(string line) => _log?.Invoke(line);

    /// <summary>Serves one client. Returning, or a dropped or malformed connection, closes it.</summary>
    protected abstract Task ServeAsync(NetworkStream stream, int id);

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
            await ServeAsync(client.GetStream(), id);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException
                                      or EndOfStreamException or InvalidDataException)
        {
        }
        finally
        {
            lock (_gate) _clients.Remove(client);
            client.Close();
        }
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

    protected static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken cancel)
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
