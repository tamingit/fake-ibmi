using System.Net;

namespace FakeIbmI;

/// <summary>
/// The fake IBM i's command line: --port (2323), --print-port (none: no Network Print server),
/// --signon-port (none: no sign-on server), --command-port (none: no remote command server),
/// --mapper-port (none: no port mapper), --bind
/// (loopback; the container passes 0.0.0.0 so it can be reached from outside it) and --trace
/// (log each Telnet negotiation step, and each record's length). For TLS: --tls-port,
/// --signon-tls-port, --print-tls-port and --command-tls-port (each none), --tls-cert (a PKCS #12 file, made when
/// missing) and --tls-name (more names for a certificate it makes, comma-separated). --env-file
/// names the file of settings, such as the users (default: .env, when there is one). --database
/// names the SQLite file for source, programs and tables (default: in memory), --source-dir a
/// folder of source to read at start-up, and --check reads that folder, reports what isn't
/// supported and stops.
/// </summary>
public sealed record Options(
    int Port, int? PrintPort, IPAddress Bind, bool Trace = false, int? SignonPort = null, int? MapperPort = null,
    int? TlsPort = null, int? SignonTlsPort = null, int? PrintTlsPort = null, string? TlsCert = null, string? TlsNames = null,
    string? EnvFile = null, int? CommandPort = null, int? CommandTlsPort = null,
    string? Database = null, string? SourceDir = null, bool Check = false)
{
    /// <summary>Whether any server listens for TLS, and so needs a certificate.</summary>
    public bool AnyTls => TlsPort is not null || SignonTlsPort is not null || PrintTlsPort is not null || CommandTlsPort is not null;

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
            if (args[i] == "--check")
            {
                options = options with { Check = true };
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
                "--command-port" => options with { CommandPort = PortValue() },
                "--command-tls-port" => options with { CommandTlsPort = PortValue() },
                "--tls-port" => options with { TlsPort = PortValue() },
                "--signon-tls-port" => options with { SignonTlsPort = PortValue() },
                "--print-tls-port" => options with { PrintTlsPort = PortValue() },
                "--tls-cert" => options with { TlsCert = value },
                "--env-file" => options with { EnvFile = value },
                "--database" => options with { Database = value },
                "--source-dir" => options with { SourceDir = value },
                "--tls-name" => options with { TlsNames = options.TlsNames is null ? value : $"{options.TlsNames},{value}" },
                "--bind" => options with
                {
                    Bind = IPAddress.TryParse(value, out var address) ? address : throw new ArgumentException($"--bind {value} is not an IP address"),
                },
                _ => options,
            };
        }
        // A TLS port is a second port for a server, so the server must run.
        if (options.SignonTlsPort is not null && options.SignonPort is null)
            throw new ArgumentException("--signon-tls-port needs --signon-port");
        if (options.PrintTlsPort is not null && options.PrintPort is null)
            throw new ArgumentException("--print-tls-port needs --print-port");
        if (options.CommandTlsPort is not null && options.CommandPort is null)
            throw new ArgumentException("--command-tls-port needs --command-port");
        if (options.Check && options.SourceDir is null) throw new ArgumentException("--check needs --source-dir");
        return options;
    }

    /// <summary>The extra names for a certificate the fake makes.</summary>
    public IEnumerable<string> ExtraTlsNames =>
        (TlsNames ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
