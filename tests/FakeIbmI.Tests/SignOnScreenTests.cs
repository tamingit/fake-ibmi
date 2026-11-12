using FakeIbmI.Tests.Client;

namespace FakeIbmI.Tests;

/// <summary>The 5250 sign-on screen checking the same users as the host servers.</summary>
public class SignOnScreenTests
{
    private static Key Click(int row, int column) => new CursorKey(row, column);

    private static async Task TypeAtAsync(TestClient client, int row, string text)
    {
        await client.PressAsync(Click(row, 53));
        await client.TypeAsync(text);
    }

    private static async Task SignOnAsync(TestClient client, string user, string password)
    {
        await client.WaitForTextAsync("Sign On");
        await TypeAtAsync(client, 6, user);
        await TypeAtAsync(client, 7, password);
        await client.EnterAsync();
    }

    [Theory]
    [InlineData("NOBODY", "Secret12", "User NOBODY does not exist.")]
    [InlineData("PRTUSER", "secret12", "Password not correct for user profile.")]
    [InlineData("LOCKUSER", "Locked12", "User profile LOCKUSER cannot sign on.")]
    public async Task ARefusedSignOnStaysOnTheSignOnScreenWithIbmIsMessage(string user, string password, string message)
    {
        await using var host = await TelnetServer.StartAsync();
        await using var client = await TestClient.ConnectAsync(host, null);

        await SignOnAsync(client, user, password);

        await client.WaitForTextAsync(message);
        Assert.Contains("Sign On", client.Text());
    }

    [Fact]
    public async Task AnExpiredPasswordIsChangedBeforeSignOnAndHoldsForTheHostServers()
    {
        var users = Users.Defaults();
        var lines = new List<string>();
        await using var host = await TelnetServer.StartAsync(log: line => { lock (lines) lines.Add(line); }, users: users);
        await using var signon = await SignOnServer.StartAsync(users: users);
        await using var client = await TestClient.ConnectAsync(host, null);

        await SignOnAsync(client, "EXPUSER", "Expired1");
        await client.WaitForTextAsync("Password has expired. Password must be changed to continue sign-on request.");
        Assert.Contains("Change Password", client.Text());

        await TypeAtAsync(client, 8, "Expired1");
        await TypeAtAsync(client, 10, "Fresh2pw");
        await TypeAtAsync(client, 12, "Mismatch");
        await client.EnterAsync();
        await client.WaitForTextAsync("New password and verify password do not match.");

        await TypeAtAsync(client, 8, "Expired1");
        await TypeAtAsync(client, 10, "Fresh2pw");
        await TypeAtAsync(client, 12, "Fresh2pw");
        await client.EnterAsync();
        await client.WaitForTextAsync("IBM i Main Menu");
        Assert.Contains("Password changed successfully.", client.Text());

        var profile = signon.Users.Find("EXPUSER")!;
        Assert.Equal("Fresh2pw", profile.Password);
        Assert.False(profile.PasswordExpired);
        // The passwords were never recorded or logged.
        Assert.DoesNotContain(host.Inputs.SelectMany(i => i.Fields), f => f.Text.Contains("Fresh2pw") || f.Text.Contains("Expired1"));
        lock (lines) Assert.DoesNotContain(lines, l => l.Contains("Fresh2pw") || l.Contains("Expired1"));
    }

    [Fact]
    public async Task TheChangeNeedsTheCurrentPasswordAndCanBeCancelled()
    {
        await using var host = await TelnetServer.StartAsync();
        await using var client = await TestClient.ConnectAsync(host, null);
        await SignOnAsync(client, "EXPUSER", "Expired1");
        await client.WaitForTextAsync("Change Password");

        await TypeAtAsync(client, 8, "wrong");
        await TypeAtAsync(client, 10, "Fresh2pw");
        await TypeAtAsync(client, 12, "Fresh2pw");
        await client.EnterAsync();
        await client.WaitForTextAsync("Current password not correct.");

        await client.PressAsync(new FunctionKey("F12"));
        await client.WaitForTextAsync("Sign On");
        Assert.True(host.Users.Find("EXPUSER")!.PasswordExpired);
    }

    [Fact]
    public async Task APasswordChangedThroughTheSignOnServerSignsOnAt5250()
    {
        var users = Users.Defaults();
        await using var host = await TelnetServer.StartAsync(users: users);
        users.Main.ChangePassword("Changed9");
        await using var client = await TestClient.ConnectAsync(host, null);

        await SignOnAsync(client, "PRTUSER", "Secret12");
        await client.WaitForTextAsync("Password not correct for user profile.");
        await SignOnAsync(client, "PRTUSER", "Changed9");
        await client.WaitForTextAsync("IBM i Main Menu");
    }
}
