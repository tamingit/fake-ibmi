using System.Net;

namespace FakeIbmI;

/// <summary>
/// The fake IBM i's command line: --port (2323), --print-port (none: no Network Print server),
/// --bind (loopback; the container passes 0.0.0.0 so it can be reached from outside it) and
/// --trace (log each Telnet negotiation step, and each record's length).
/// </summary>
public sealed record Options(int Port, int? PrintPort, IPAddress Bind, bool Trace = false)
{
    public static Options Parse(IReadOnlyList<string> args)
    {
        var (port, printPort, bind, trace) = (2323, (int?)null, IPAddress.Loopback, false);
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--trace")
            {
                trace = true;
                continue;
            }
            if (i + 1 >= args.Count) break;
            var value = args[++i];
            switch (args[i - 1])
            {
                case "--port":
                    port = int.TryParse(value, out var p) ? p : throw new ArgumentException($"--port {value} is not a port");
                    break;
                case "--print-port":
                    printPort = int.TryParse(value, out var pp) ? pp : throw new ArgumentException($"--print-port {value} is not a port");
                    break;
                case "--bind":
                    bind = IPAddress.TryParse(value, out var address) ? address : throw new ArgumentException($"--bind {value} is not an IP address");
                    break;
            }
        }
        return new Options(port, printPort, bind, trace);
    }
}
