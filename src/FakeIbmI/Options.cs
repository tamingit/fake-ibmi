using System.Net;

namespace FakeIbmI;

/// <summary>
/// The fake IBM i's command line: --port (2323), --print-port (none: no Network Print server),
/// --signon-port (none: no sign-on server), --mapper-port (none: no port mapper), --bind
/// (loopback; the container passes 0.0.0.0 so it can be reached from outside it) and --trace
/// (log each Telnet negotiation step, and each record's length).
/// </summary>
public sealed record Options(
    int Port, int? PrintPort, IPAddress Bind, bool Trace = false, int? SignonPort = null, int? MapperPort = null)
{
    public static Options Parse(IReadOnlyList<string> args)
    {
        var options = new Options(2323, null, IPAddress.Loopback);
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--trace")
            {
                options = options with { Trace = true };
                continue;
            }
            if (i + 1 >= args.Count) break;
            var (option, value) = (args[i], args[++i]);
            int PortValue() => int.TryParse(value, out var p) && p is >= 0 and <= 65535
                ? p
                : throw new ArgumentException($"{option} {value} is not a port");
            options = option switch
            {
                "--port" => options with { Port = PortValue() },
                "--print-port" => options with { PrintPort = PortValue() },
                "--signon-port" => options with { SignonPort = PortValue() },
                "--mapper-port" => options with { MapperPort = PortValue() },
                "--bind" => options with
                {
                    Bind = IPAddress.TryParse(value, out var address) ? address : throw new ArgumentException($"--bind {value} is not an IP address"),
                },
                _ => options,
            };
        }
        return options;
    }
}
