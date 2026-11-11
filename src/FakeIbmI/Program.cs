// Runs the scripted fake IBM i until Ctrl+C:
//   dotnet run --project src/FakeIbmI -- --port 2323 [--print-port 2324] [--bind 0.0.0.0]
// --print-port also runs its Network Print server (printer output), for PRTUSER / Secret12.
// --bind listens on another address than loopback (the demo container: 0.0.0.0).

using FakeIbmI;

var options = Options.Parse(args);
await using var host = await TelnetServer.StartAsync(options.Port, line => Console.WriteLine(line), options.Bind);
Console.WriteLine($"fake IBM i listening on {options.Bind} port {host.Port}; Ctrl+C to stop");
await using var print = options.PrintPort is { } pport
    ? await NetworkPrintServer.StartAsync(pport, line => Console.WriteLine(line), options.Bind)
    : null;
if (print is not null) Console.WriteLine($"fake Network Print server listening on port {print.Port}");

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
