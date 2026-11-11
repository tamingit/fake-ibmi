
using FakeIbmI.Tests.Client;

namespace FakeIbmI.Tests;

public class TelnetServerTests
{
    private static Key Click(int row, int column) => new CursorKey(row, column);

    private static async Task SignOnAsync(TestClient client, string user, string password)
    {
        await client.WaitForTextAsync("Sign On");
        await client.PressAsync(Click(6, 53));
        await client.TypeAsync(user);
        await client.PressAsync(Click(7, 53));
        await client.TypeAsync(password);
        await client.EnterAsync();
    }

    [Fact(DisplayName = "TelnetServer: negotiates, starts the requested device and shows the sign-on screen")]
    public async Task ShowsSignOn()
    {
        await using var host = await TelnetServer.StartAsync();
        await using var client = await TestClient.ConnectAsync(host, "GGTEST001");
        await client.WaitForTextAsync("Sign On");

        var events = client.Events;
        Assert.Contains(events, e => e is ConnectionEvent.Negotiated);
        var startup = Assert.Single(events.OfType<ConnectionEvent.Startup>()).Result;
        Assert.True(startup.Ok);
        Assert.Equal("I902", startup.Code);
        Assert.Equal("GGTEST001", startup.DeviceName);
        Assert.DoesNotContain(events, e => e is ConnectionEvent.DeviceMismatch or ConnectionEvent.ProtocolError);

        var fields = client.Screen.Fields.Where(f => (f.Ffw & 0x4000) != 0).ToList();
        Assert.Equal(2, fields.Count);
        Assert.Equal((6, 53), (fields[0].Row, fields[0].Column));
        Assert.Equal((7, 53), (fields[1].Row, fields[1].Column));
        Assert.NotEqual(0x27, fields[0].Attribute);
        Assert.Equal(0x27, fields[1].Attribute);
        Assert.Equal(1, host.Connections);
        Assert.Equal(1, host.Open);
    }

    [Fact(DisplayName = "TelnetServer: assigns a QPADEV device when none is requested")]
    public async Task AssignsDefaultDevice()
    {
        await using var host = await TelnetServer.StartAsync();
        await using var client = await TestClient.ConnectAsync(host, null);
        await client.WaitForTextAsync("Sign On");
        Assert.Equal("QPADEV0001", Assert.Single(client.Events.OfType<ConnectionEvent.Startup>()).Result.DeviceName);
    }

    [Fact(DisplayName = "TelnetServer: sign-on, main menu, F3 back to sign-on, then 90 closes")]
    public async Task WalksTheScript()
    {
        await using var host = await TelnetServer.StartAsync();
        await using var client = await TestClient.ConnectAsync(host, "GGTEST001");

        await SignOnAsync(client, "prtuser", "Secret12");
        await client.WaitForTextAsync("IBM i Main Menu");
        var text = client.Text();
        Assert.Contains("MAIN", text);
        Assert.Contains("Selection or command", text);
        Assert.Contains("F3=Exit   F4=Prompt   F12=Cancel", text);

        var input = Assert.Single(host.Inputs);
        Assert.Equal(0xf1, input.Aid);
        Assert.Equal("PRTUSER", input.FieldAt(6, 53)); // the user field is monocase
        Assert.DoesNotContain("Secret12", input.FieldAt(7, 53));
        Assert.Equal(8, input.FieldAt(7, 53)!.Length);

        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("Sign On");
        Assert.Equal(0x33, host.Inputs[^1].Aid);

        await SignOnAsync(client, "PRTUSER", "Secret12");
        await client.WaitForTextAsync("IBM i Main Menu");
        await client.TypeAsync("90");
        await client.EnterAsync();
        await client.WaitForAsync(c => c.HostClosed, "host close");
        Assert.Equal("90", host.Inputs[^1].FieldAt(20, 7));
        await WaitOpenAsync(host, 0);
    }

    [Fact(DisplayName = "TelnetServer: signoff command in any case closes the connection")]
    public async Task SignoffClosesCaseInsensitively()
    {
        await using var host = await TelnetServer.StartAsync();
        await using var client = await TestClient.ConnectAsync(host, "GGTEST001");
        await SignOnAsync(client, "PRTUSER", "Secret12");
        await client.WaitForTextAsync("IBM i Main Menu");
        await client.TypeAsync(" SignOff ");
        await client.EnterAsync();
        await client.WaitForAsync(c => c.HostClosed, "host close");
    }

    [Fact(DisplayName = "TelnetServer: an invalid main menu option and a blank user show error-row messages")]
    public async Task ShowsErrors()
    {
        await using var host = await TelnetServer.StartAsync();
        await using var client = await TestClient.ConnectAsync(host, "GGTEST001");

        await client.WaitForTextAsync("Sign On");
        await client.EnterAsync();
        await client.WaitForTextAsync("User required");

        await SignOnAsync(client, "PRTUSER", "Secret12");
        await client.WaitForTextAsync("IBM i Main Menu");
        await client.PressAsync(Click(20, 7));
        await client.TypeAsync("77");
        await client.EnterAsync();
        await client.WaitForTextAsync("Option 77 is not valid (placeholder)");
        Assert.False(client.HostClosed);
    }

    [Fact(DisplayName = "TelnetServer: a second connection with a device in use is refused")]
    public async Task RefusesDeviceInUse()
    {
        await using var host = await TelnetServer.StartAsync();
        await using var first = await TestClient.ConnectAsync(host, "GGTEST001");
        await first.WaitForTextAsync("Sign On");

        await using var second = await TestClient.ConnectAsync(host, "GGTEST001");
        await second.WaitForAsync(c => c.HostClosed, "second connection closed");
        Assert.Contains(second.Events, e => e is ConnectionEvent.DeviceInUse { DeviceName: "GGTEST001" });
        Assert.DoesNotContain("Sign On", second.Text());
        Assert.Equal(2, host.Connections);
        await WaitOpenAsync(host, 1);

        // The name is free again once the first session ends.
        await first.DisposeAsync();
        await WaitOpenAsync(host, 0);
        await using var third = await TestClient.ConnectAsync(host, "GGTEST001");
        await third.WaitForTextAsync("Sign On");
    }

    [Fact(DisplayName = "TelnetServer: CloseAllAsync drops every connection and counters follow")]
    public async Task ClosesAll()
    {
        await using var host = await TelnetServer.StartAsync();
        await using var a = await TestClient.ConnectAsync(host, "GGTEST001");
        await using var b = await TestClient.ConnectAsync(host, "GGTEST002");
        await host.WaitForConnectionsAsync(2, TimeSpan.FromSeconds(5));
        await a.WaitForTextAsync("Sign On");
        await b.WaitForTextAsync("Sign On");
        Assert.Equal(2, host.Open);

        await host.CloseAllAsync();
        await a.WaitForAsync(c => c.HostClosed, "a closed");
        await b.WaitForAsync(c => c.HostClosed, "b closed");
        Assert.Equal(0, host.Open);
        Assert.Equal(2, host.Connections);
    }

    private static async Task WaitOpenAsync(TelnetServer host, int open)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (host.Open != open && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(open, host.Open);
    }
}
