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
/// names the folder of the libraries' SQLite databases, one file for each (default: in memory), --source-dir a
/// folder of source to read at start-up, a folder for each library, and --check reads that folder, reports what isn't
/// supported and stops. Settings (FAKE_IBMI_PORT, FAKE_IBMI_BIND, FAKE_IBMI_SOURCE_DIR, FAKE_IBMI_DATABASE_DIR and the rest, see .env.example) give
/// what the command line doesn't: the command line wins.
/// </summary>
public sealed record Options(
    int Port, int? PrintPort, IPAddress Bind, bool Trace = false, int? SignonPort = null, int? MapperPort = null,
    int? TlsPort = null, int? SignonTlsPort = null, int? PrintTlsPort = null, string? TlsCert = null, string? TlsNames = null,
    string? EnvFile = null, int? CommandPort = null, int? CommandTlsPort = null,
    string? DatabaseDir = null, string? SourceDir = null, bool Check = false)
{
    /// <summary>Whether any server listens for TLS, and so needs a certificate.</summary>
    public bool AnyTls => TlsPort is not null || SignonTlsPort is not null || PrintTlsPort is not null || CommandTlsPort is not null;

    /// <summary>The environment variables that set the ports, the address and the certificate, as .env.example lists them.</summary>
    public static readonly string[] Variables =
    [
        "FAKE_IBMI_BIND", "FAKE_IBMI_PORT", "FAKE_IBMI_PRINT_PORT", "FAKE_IBMI_SIGNON_PORT", "FAKE_IBMI_COMMAND_PORT",
        "FAKE_IBMI_MAPPER_PORT", "FAKE_IBMI_TLS_PORT", "FAKE_IBMI_PRINT_TLS_PORT", "FAKE_IBMI_SIGNON_TLS_PORT",
        "FAKE_IBMI_COMMAND_TLS_PORT", "FAKE_IBMI_TLS_CERT", "FAKE_IBMI_TLS_NAMES", "FAKE_IBMI_SOURCE_DIR", "FAKE_IBMI_DATABASE_DIR",
    ];

    /// <summary>The file --env-file names, which has to be known before the settings in it can be read.</summary>
    public static string? EnvFileIn(IReadOnlyList<string> args)
    {
        var at = args.ToList().LastIndexOf("--env-file");
        return at >= 0 && at + 1 < args.Count ? args[at + 1] : null;
    }

    /// <summary>
    /// The options from the command line. <paramref name="settings"/> (see <see cref="Variables"/>) give what
    /// the command line doesn't: a blank setting is no setting, and a server with no port doesn't run.
    /// </summary>
    public static Options Parse(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? settings = null)
    {
        string? Setting(string name) =>
            settings is not null && settings.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        int? SettingPort(string name) => Setting(name) is { } v
            ? int.TryParse(v, out var p) && p is >= 0 and <= 65535 ? p : throw new ArgumentException($"{name} {v} is not a port")
            : null;
        var options = new Options(SettingPort("FAKE_IBMI_PORT") ?? 2323, SettingPort("FAKE_IBMI_PRINT_PORT"),
            Setting("FAKE_IBMI_BIND") is { } bind
                ? IPAddress.TryParse(bind, out var bindAddress) ? bindAddress : throw new ArgumentException($"FAKE_IBMI_BIND {bind} is not an IP address")
                : IPAddress.Loopback,
            SignonPort: SettingPort("FAKE_IBMI_SIGNON_PORT"), MapperPort: SettingPort("FAKE_IBMI_MAPPER_PORT"),
            TlsPort: SettingPort("FAKE_IBMI_TLS_PORT"), SignonTlsPort: SettingPort("FAKE_IBMI_SIGNON_TLS_PORT"),
            PrintTlsPort: SettingPort("FAKE_IBMI_PRINT_TLS_PORT"), TlsCert: Setting("FAKE_IBMI_TLS_CERT"),
            TlsNames: Setting("FAKE_IBMI_TLS_NAMES"), CommandPort: SettingPort("FAKE_IBMI_COMMAND_PORT"),
            CommandTlsPort: SettingPort("FAKE_IBMI_COMMAND_TLS_PORT"),
            DatabaseDir: Setting("FAKE_IBMI_DATABASE_DIR"), SourceDir: Setting("FAKE_IBMI_SOURCE_DIR"));
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
                "--database-dir" => options with { DatabaseDir = value },
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
