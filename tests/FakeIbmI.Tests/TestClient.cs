using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

using FakeIbmI.Tests.Client;

namespace FakeIbmI.Tests;

/// <summary>A real engine Connection driven over TCP, with waiting helpers for tests.</summary>
internal sealed class TestClient : IAsyncDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly TcpClient _tcp = new();
    private readonly Connection _connection;
    private readonly object _gate = new();
    private readonly List<ConnectionEvent> _events = [];
    private Task _pump = Task.CompletedTask;
    private Stream? _stream;

    public TestClient(string? device)
    {
        _connection = new Connection(new ConnectionConfig { TerminalType = "IBM-3477-FC", DeviceName = device });
    }

    public bool HostClosed { get; private set; }

    public static async Task<TestClient> ConnectAsync(TelnetServer host, string? device)
    {
        var client = new TestClient(device);
        await client._tcp.ConnectAsync("127.0.0.1", host.Port);
        client._stream = client._tcp.GetStream();
        client._pump = Task.Run(client.PumpAsync);
        return client;
    }

    /// <summary>Connects to the TLS port, trusting only <paramref name="trusted"/>, for the name localhost.</summary>
    public static async Task<TestClient> ConnectTlsAsync(TelnetServer host, string? device, X509Certificate2 trusted)
    {
        var client = new TestClient(device);
        await client._tcp.ConnectAsync("127.0.0.1", host.TlsPort!.Value);
        var tls = new SslStream(client._tcp.GetStream());
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            CertificateChainPolicy = Trusting(trusted),
        });
        client._stream = tls;
        client._pump = Task.Run(client.PumpAsync);
        return client;
    }

    /// <summary>A chain policy that trusts this one certificate and nothing else.</summary>
    public static X509ChainPolicy Trusting(X509Certificate2 trusted)
    {
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        policy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(trusted.RawData));
        return policy;
    }

    /// <summary>Drops the connection with a reset, as a client that crashed or lost its network would.</summary>
    public async Task AbortAsync()
    {
        _tcp.Client.LingerState = new LingerOption(true, 0);
        _tcp.Close();
        await _pump;
    }

    public List<ConnectionEvent> Events
    {
        get
        {
            lock (_gate) return [.. _events];
        }
    }

    public ScreenSnapshot Screen
    {
        get
        {
            lock (_gate) return _connection.Screen;
        }
    }

    /// <summary>The screen as text, one line per row, nulls as blanks.</summary>
    public string Text()
    {
        var screen = Screen;
        var text = Ebcdic.Decode(screen.Buffer).Replace('\0', ' ');
        return string.Join("\n", Enumerable.Range(0, screen.Rows).Select(r => text.Substring(r * screen.Columns, screen.Columns).TrimEnd()));
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var count = await _stream!.ReadAsync(buffer);
                if (count == 0) break;
                ReceiveResult result;
                lock (_gate)
                {
                    result = _connection.Receive(buffer.AsSpan(0, count));
                    _events.AddRange(result.Events);
                }
                foreach (var send in result.Send) await _stream.WriteAsync(send.Bytes);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
        }
        lock (_gate)
        {
            _events.AddRange(_connection.Closed());
            HostClosed = true;
        }
    }

    public async Task PressAsync(params Key[] keys)
    {
        foreach (var key in keys)
        {
            ReceiveResult result;
            lock (_gate)
            {
                result = _connection.Key(key);
                _events.AddRange(result.Events);
            }
            foreach (var send in result.Send) await _stream!.WriteAsync(send.Bytes);
        }
    }

    public Task TypeAsync(string text) => PressAsync(text.Select(c => (Key)new CharacterKey(c.ToString())).ToArray());

    public Task EnterAsync() => PressAsync(new FunctionKey("Enter"));

    public async Task WaitForAsync(Func<TestClient, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition(this))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for {what}\n{Text()}");
            await Task.Delay(10);
        }
    }

    /// <summary>Waits for the screen text to contain this, with the keyboard unlocked.</summary>
    public Task WaitForTextAsync(string text) =>
        WaitForAsync(c => c.Text().Contains(text) && !c.Screen.KeyboardLocked, $"screen text '{text}'");

    public async ValueTask DisposeAsync()
    {
        _tcp.Close();
        await _pump;
    }
}
