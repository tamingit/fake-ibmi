using System.Net;

namespace FakeIbmI.Tests;

/// <summary>The fake IBM i's command line, as the tests and the demo container start it.</summary>
public class OptionsTests
{
    [Fact]
    public void ByDefaultItListensOnLoopbackPort2323WithoutHostServers() =>
        Assert.Equal(new Options(2323, null, IPAddress.Loopback, Trace: false), Options.Parse([]));

    [Fact]
    public void TheDemoContainerListensOnEveryInterface() =>
        Assert.Equal(new Options(2323, 8474, IPAddress.Any, SignonPort: 8476, MapperPort: 449),
            Options.Parse(["--bind", "0.0.0.0", "--port", "2323", "--print-port", "8474", "--signon-port", "8476", "--mapper-port", "449"]));

    [Fact]
    public void TraceIsASwitch() =>
        Assert.Equal(new Options(23, null, IPAddress.Any, Trace: true),
            Options.Parse(["--trace", "--port", "23", "--bind", "0.0.0.0"]));

    [Fact]
    public void TlsPortsAndTheCertificateAreOptions() =>
        Assert.Equal(new Options(2323, 8474, IPAddress.Loopback, SignonPort: 8476, TlsPort: 992, SignonTlsPort: 9476,
                PrintTlsPort: 9474, TlsCert: "/certs/fake.pfx", TlsNames: "ibmi.example,10.0.0.5"),
            Options.Parse(["--print-port", "8474", "--signon-port", "8476", "--tls-port", "992", "--signon-tls-port", "9476",
                "--print-tls-port", "9474", "--tls-cert", "/certs/fake.pfx", "--tls-name", "ibmi.example", "--tls-name", "10.0.0.5"]));

    [Fact]
    public void TheRemoteCommandServerIsAnOption() =>
        Assert.Equal(new Options(2323, null, IPAddress.Loopback, CommandPort: 8475, CommandTlsPort: 9475),
            Options.Parse(["--command-port", "8475", "--command-tls-port", "9475"]));

    [Theory]
    [InlineData("--signon-tls-port")]
    [InlineData("--print-tls-port")]
    [InlineData("--command-tls-port")]
    public void ATlsPortNeedsItsServer(string option) =>
        Assert.Throws<ArgumentException>(() => Options.Parse([option, "9476"]));

    [Theory]
    [InlineData("--port", "x")]
    [InlineData("--tls-port", "x")]
    [InlineData("--print-port", "x")]
    [InlineData("--signon-port", "x")]
    [InlineData("--mapper-port", "70000")]
    [InlineData("--bind", "not-an-address")]
    public void ABadValueStopsTheStart(string option, string value) =>
        Assert.Throws<ArgumentException>(() => Options.Parse([option, value]));

    [Fact]
    public async Task ItCanListenOnAnotherAddress()
    {
        await using var host = await TelnetServer.StartAsync(0, address: IPAddress.Any);
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, host.Port);
        Assert.True(client.Connected);
    }
}
