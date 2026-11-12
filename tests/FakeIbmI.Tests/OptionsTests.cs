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

    [Fact]
    public void TheDatabaseAndSourceFolderAreOptions()
    {
        Assert.Equal(new Options(2323, null, IPAddress.Loopback, DatabaseDir: "libs", SourceDir: "src", Check: true),
            Options.Parse(["--database-dir", "libs", "--check", "--source-dir", "src"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--check"]));
    }

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

    [Fact]
    public void SettingsGivePortsAndTheAddressAndTheCommandLineWins()
    {
        var settings = new Dictionary<string, string>
        {
            ["FAKE_IBMI_BIND"] = "0.0.0.0", ["FAKE_IBMI_PORT"] = "2324", ["FAKE_IBMI_PRINT_PORT"] = "8474",
            ["FAKE_IBMI_SIGNON_PORT"] = "8476", ["FAKE_IBMI_COMMAND_PORT"] = "8475", ["FAKE_IBMI_MAPPER_PORT"] = "",
            ["FAKE_IBMI_TLS_PORT"] = "992", ["FAKE_IBMI_PRINT_TLS_PORT"] = "9474", ["FAKE_IBMI_SIGNON_TLS_PORT"] = "9476",
            ["FAKE_IBMI_COMMAND_TLS_PORT"] = "9475", ["FAKE_IBMI_TLS_CERT"] = "fake.pfx", ["FAKE_IBMI_TLS_NAMES"] = "ibmi.example",
            ["FAKE_IBMI_SOURCE_DIR"] = "./source", ["FAKE_IBMI_DATABASE_DIR"] = "./data",
        };

        Assert.Equal(new Options(2324, 8474, IPAddress.Any, SignonPort: 8476, CommandPort: 8475, TlsPort: 992, PrintTlsPort: 9474,
                SignonTlsPort: 9476, CommandTlsPort: 9475, TlsCert: "fake.pfx", TlsNames: "ibmi.example",
                DatabaseDir: "./data", SourceDir: "./source"),
            Options.Parse([], settings));
        Assert.Equal("elsewhere", Options.Parse(["--source-dir", "elsewhere"], settings).SourceDir);
        var overridden = Options.Parse(["--port", "23", "--bind", "127.0.0.1"], settings);
        Assert.Equal((23, IPAddress.Loopback, 8474), (overridden.Port, overridden.Bind, overridden.PrintPort));
        Assert.Throws<ArgumentException>(() => Options.Parse([], new Dictionary<string, string> { ["FAKE_IBMI_PORT"] = "http" }));
        Assert.Throws<ArgumentException>(() => Options.Parse([], new Dictionary<string, string> { ["FAKE_IBMI_BIND"] = "localhost" }));
        Assert.Equal("x.env", Options.EnvFileIn(["--trace", "--env-file", "x.env"]));
        Assert.Null(Options.EnvFileIn(["--trace"]));
    }

    [Fact]
    public void EnvExampleListsEveryPortAndAddressSetting()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, ".env.example"))) folder = folder.Parent;
        var example = Settings.Parse(File.ReadLines(Path.Combine(folder!.FullName, ".env.example")));

        Assert.All(Options.Variables, v => Assert.Contains(v, example.Keys));
        // As it is, it starts every server on IBM's ports, with TLS on each, but for the two below 1024 (they need root).
        var options = Options.Parse([], example);
        Assert.Equal((2323, 8474, 8476, 8475), (options.Port, options.PrintPort, options.SignonPort, options.CommandPort));
        Assert.Equal((9474, 9476, 9475), (options.PrintTlsPort, options.SignonTlsPort, options.CommandTlsPort));
        Assert.Null(options.MapperPort);
        Assert.Null(options.TlsPort);
    }
}
