using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

using FakeIbmI.Tests.Client;

namespace FakeIbmI.Tests;

/// <summary>TLS: the certificate the fake makes, and its servers' TLS ports.</summary>
public class TlsTests : IDisposable
{
    private static readonly X509Certificate2 Certificate = Tls.CreateSelfSigned(Tls.Names(IPAddress.Loopback, []));

    private readonly string _directory = Directory.CreateTempSubdirectory("fake-ibmi-tls-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ItMakesACertificateOnceAndKeepsUsingIt()
    {
        var path = Path.Combine(_directory, "fake.pfx");
        var lines = new List<string>();

        var made = Tls.LoadOrCreate(path, ["localhost", "127.0.0.1", "ibmi.example"], lines.Add);
        var loaded = Tls.LoadOrCreate(path, ["other"], lines.Add);

        Assert.Equal(made.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
        var crt = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(_directory, "fake.crt"));
        Assert.Equal(made.Thumbprint, crt.Thumbprint);
        Assert.False(crt.HasPrivateKey);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Contains("made and saved", lines[0]);
        Assert.Contains("from", lines[1]);
    }

    [Fact]
    public void ItsCertificateIsForServersAndTheNamesGiven()
    {
        var certificate = Tls.CreateSelfSigned(["localhost", "127.0.0.1", "ibmi.example"]);

        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("ibmi.example", names.EnumerateDnsNames());
        Assert.Contains(IPAddress.Loopback, names.EnumerateIPAddresses());
        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Equal("1.3.6.1.5.5.7.3.1", Assert.Single(usage.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>()).Value);
        Assert.True(certificate.MatchesHostname("ibmi.example"));
        Assert.True(certificate.MatchesHostname("127.0.0.1"));
    }

    [Fact]
    public void WithoutAPathItSavesNothing()
    {
        var certificate = Tls.LoadOrCreate(null, ["localhost"]);

        Assert.True(certificate.HasPrivateKey);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public async Task A5250SessionRunsOverTls()
    {
        var lines = new List<string>();
        await using var host = await TelnetServer.StartAsync(log: line => { lock (lines) lines.Add(line); }, tls: new TlsOptions(0, Certificate));
        await using var client = await TestClient.ConnectTlsAsync(host, "GGTLS001", Certificate);

        await client.WaitForTextAsync("Sign On");

        Assert.Equal("GGTLS001", Assert.Single(client.Events.OfType<ConnectionEvent.Startup>()).Result.DeviceName);
        lock (lines)
        {
            Assert.Contains("#1 connected (TLS)", lines);
            Assert.Contains(lines, l => l.StartsWith("#1 TLS 1."));
        }
    }

    [Fact]
    public async Task TheClearAndTlsPortsShareDeviceNames()
    {
        await using var host = await TelnetServer.StartAsync(tls: new TlsOptions(0, Certificate));
        await using var clear = await TestClient.ConnectAsync(host, "GGTLS001");
        await clear.WaitForTextAsync("Sign On");

        await using var secure = await TestClient.ConnectTlsAsync(host, "GGTLS001", Certificate);

        await secure.WaitForAsync(c => c.HostClosed, "TLS connection refused");
        Assert.Contains(secure.Events, e => e is ConnectionEvent.DeviceInUse { DeviceName: "GGTLS001" });
    }

    [Fact]
    public async Task ADroppedSessionFreesItsDeviceName()
    {
        await using var host = await TelnetServer.StartAsync(tls: new TlsOptions(0, Certificate));
        var first = await TestClient.ConnectTlsAsync(host, "GGTLS001", Certificate);
        await first.WaitForTextAsync("Sign On");

        await first.AbortAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.Open > 0 && DateTime.UtcNow < deadline) await Task.Delay(10);

        await using var second = await TestClient.ConnectTlsAsync(host, "GGTLS001", Certificate);
        await second.WaitForTextAsync("Sign On");
    }

    [Fact]
    public async Task AClientThatIsNotSpeakingTlsIsLoggedAndTheServerCarriesOn()
    {
        var lines = new List<string>();
        await using var server = await SignOnServer.StartAsync(log: line => { lock (lines) lines.Add(line); },
            tls: new TlsOptions(0, Certificate));

        using (var plain = new TcpClient())
        {
            await plain.ConnectAsync(IPAddress.Loopback, server.TlsPort!.Value);
            await plain.GetStream().WriteAsync(new byte[64]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                while (await plain.GetStream().ReadAsync(new byte[64], timeout.Token) > 0) { }
            }
            catch (IOException)
            {
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (lines) if (lines.Any(l => l.Contains("TLS handshake failed"))) break;
            await Task.Delay(10);
        }
        lock (lines) Assert.Contains(lines, l => l.StartsWith("signon #1 TLS handshake failed"));

        using var next = new TcpClient();
        await next.ConnectAsync(IPAddress.Loopback, server.TlsPort!.Value);
        await using var tls = new SslStream(next.GetStream());
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            CertificateChainPolicy = TestClient.Trusting(Certificate),
        });
        Assert.True(tls.IsAuthenticated);
    }
}
