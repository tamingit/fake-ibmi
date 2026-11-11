using System.Net;

namespace FakeIbmI;

/// <summary>
/// The fake IBM i's command line: --port (2323), --print-port (none: no Network Print server)
/// and --bind (loopback; the demo container passes 0.0.0.0 so the server's container reaches it).
/// </summary>
public sealed record Options(int Port, int? PrintPort, IPAddress Bind)
{
    public static Options Parse(IReadOnlyList<string> args)
    {
        var (port, printPort, bind) = (2323, (int?)null, IPAddress.Loopback);
        for (var i = 0; i + 1 < args.Count; i++)
        {
            var value = args[i + 1];
            switch (args[i])
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
        return new Options(port, printPort, bind);
    }
}
