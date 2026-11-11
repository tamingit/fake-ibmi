// Runs the scripted fake IBM i until Ctrl+C:
//   dotnet run --project src/FakeIbmI -- --port 2323 [--print-port 8474] [--signon-port 8476]
//       [--mapper-port 449] [--bind 0.0.0.0] [--trace]
// --print-port also runs its Network Print server (printer output), for PRTUSER / Secret12.
// --signon-port also runs its sign-on server, for the same user and password, which it can change.
// --mapper-port also runs its port mapper, which tells clients the other servers' ports.
// --bind listens on another address than loopback (0.0.0.0: every interface).
// --trace logs each Telnet negotiation step, and each record's length (never its contents).

using FakeIbmI;

var options = Options.Parse(args);
void Log(string line) => Console.WriteLine(line);

try
{
    await using var host = await TelnetServer.StartAsync(options.Port, Log, options.Bind, options.Trace);
    Console.WriteLine($"fake IBM i listening on {options.Bind} port {host.Port}; Ctrl+C to stop");

    // The host servers share one user profile, so a password changed through sign-on holds for print.
    var profile = new UserProfile();
    await using var print = options.PrintPort is { } printPort
        ? await NetworkPrintServer.StartAsync(printPort, Log, options.Bind, profile)
        : null;
    if (print is not null) Console.WriteLine($"fake Network Print server listening on port {print.Port}");
    await using var signon = options.SignonPort is { } signonPort
        ? await SignOnServer.StartAsync(signonPort, Log, options.Bind, profile)
        : null;
    if (signon is not null) Console.WriteLine($"fake sign-on server listening on port {signon.Port}");

    var services = new Dictionary<string, int>();
    if (print is not null) services["as-netprt"] = print.Port;
    if (signon is not null) services["as-signon"] = signon.Port;
    await using var mapper = options.MapperPort is { } mapperPort
        ? await PortMapper.StartAsync(mapperPort, services, Log, options.Bind)
        : null;
    if (mapper is not null)
        Console.WriteLine($"fake port mapper listening on port {mapper.Port}, for {(services.Count == 0 ? "no services" : string.Join(", ", services.Keys))}");

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };
    try
    {
        await Task.Delay(Timeout.Infinite, stop.Token);
    }
    catch (OperationCanceledException)
    {
    }
}
catch (System.Net.Sockets.SocketException e)
{
    // Most often a port in use, or one below 1024 (the port mapper's 449) without root.
    Console.Error.WriteLine($"fake IBM i can't listen: {e.Message}");
    if (e.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied)
        Console.Error.WriteLine("Ports below 1024 need root or CAP_NET_BIND_SERVICE; in a container, Docker allows them.");
    return 1;
}
return 0;
