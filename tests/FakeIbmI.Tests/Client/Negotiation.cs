// Telnet option negotiation for TN5250E (RFC 1143, 1572, 4777).
// The client only answers: the IBM i starts every negotiation.

namespace FakeIbmI.Tests.Client;

/// <param name="TerminalType">Sent as TERMINAL-TYPE IS, for example IBM-3477-FC.</param>
/// <param name="DeviceName">Display device to request. Without one the IBM i assigns a QPADEVnnnn device.</param>
public sealed record NegotiationConfig(string TerminalType, string? DeviceName = null);

public abstract record NegotiationEvent
{
    public abstract string Type { get; }

    /// <summary>BINARY and END-OF-RECORD are on in both directions, so 5250 records can flow.</summary>
    public sealed record Ready : NegotiationEvent
    {
        public override string Type => "ready";
    }

    /// <summary>
    /// The IBM i asked for DEVNAME again because the requested device is in use.
    /// The same name was repeated, which makes the IBM i close the connection.
    /// </summary>
    public sealed record DeviceCollision(string DeviceName) : NegotiationEvent
    {
        public override string Type => "device-collision";
    }
}

public sealed class NegotiationResult
{
    public List<byte[]> Send { get; } = [];
    public List<NegotiationEvent> Events { get; } = [];
}

public sealed class TelnetNegotiator
{
    // Subnegotiation verbs (RFC 1091, RFC 1572).
    private const byte Is = 0;
    private const byte SendVerb = 1;
    // NEW-ENVIRON codes (RFC 1572).
    private const byte Value = 1;
    private const byte Esc = 2;
    private const byte UserVar = 3;

    /// <summary>Options the client enables on its own side (answers DO with WILL).</summary>
    private static readonly HashSet<int> LocalOptions =
    [
        TelnetOption.Binary,
        TelnetOption.EndOfRecord,
        TelnetOption.TerminalType,
        TelnetOption.NewEnviron,
    ];

    /// <summary>Options the client lets the IBM i enable (answers WILL with DO).</summary>
    private static readonly HashSet<int> RemoteOptions = [TelnetOption.Binary, TelnetOption.EndOfRecord];

    private static readonly byte[] DevnameRequest = [SendVerb, UserVar, .. Ascii("DEVNAME")];

    private readonly NegotiationConfig _config;

    /// <summary>Options on at each end, tracked so repeated requests don't loop (RFC 1143).</summary>
    private readonly HashSet<int> _local = [];
    private readonly HashSet<int> _remote = [];
    private bool _terminalTypeSent;
    private bool _environmentSent;

    public TelnetNegotiator(NegotiationConfig config)
    {
        if (config.DeviceName is not null && !IsValidDeviceName(config.DeviceName))
        {
            throw new ArgumentException($"Not a valid device name: {config.DeviceName}", nameof(config));
        }
        _config = config;
    }

    public bool IsReady { get; private set; }

    /// <summary>Up to 10 characters: A–Z, 0–9, $, #, @ and _, not starting with a digit or _ (RFC 4777).</summary>
    public static bool IsValidDeviceName(string name)
    {
        static bool First(char c) => c is (>= 'A' and <= 'Z') or '$' or '#' or '@';
        static bool Rest(char c) => First(c) || c is (>= '0' and <= '9') or '_';
        if (name.Length < 1 || name.Length > 10 || !First(name[0])) return false;
        for (var i = 1; i < name.Length; i++)
        {
            if (!Rest(name[i])) return false;
        }
        return true;
    }

    /// <summary>Handles negotiate and subnegotiation events; any other event is ignored.</summary>
    public NegotiationResult Handle(TelnetEvent telnetEvent)
    {
        var result = new NegotiationResult();
        if (telnetEvent is TelnetEvent.Negotiate n) Negotiate(n.Command, n.Option, result);
        else if (telnetEvent is TelnetEvent.Subnegotiation s) Subnegotiate(s.Option, s.Data, result);

        if (!IsReady && _terminalTypeSent && Transparent())
        {
            IsReady = true;
            result.Events.Add(new NegotiationEvent.Ready());
        }
        return result;
    }

    private bool Transparent() =>
        _local.Contains(TelnetOption.Binary)
        && _local.Contains(TelnetOption.EndOfRecord)
        && _remote.Contains(TelnetOption.Binary)
        && _remote.Contains(TelnetOption.EndOfRecord);

    private void Negotiate(NegotiationCommand command, int option, NegotiationResult result)
    {
        void Reply(NegotiationCommand answer) => result.Send.Add(Telnet.EncodeNegotiation(answer, option));
        switch (command)
        {
            case NegotiationCommand.Do:
                if (option == TelnetOption.TimingMark)
                {
                    // The IBM i uses timing marks to check the client is alive: answer every one.
                    Reply(NegotiationCommand.Will);
                }
                else if (!LocalOptions.Contains(option))
                {
                    Reply(NegotiationCommand.Wont);
                }
                else if (_local.Add(option))
                {
                    Reply(NegotiationCommand.Will);
                }
                break;
            case NegotiationCommand.Dont:
                if (_local.Remove(option)) Reply(NegotiationCommand.Wont);
                break;
            case NegotiationCommand.Will:
                if (!RemoteOptions.Contains(option))
                {
                    Reply(NegotiationCommand.Dont);
                }
                else if (_remote.Add(option))
                {
                    Reply(NegotiationCommand.Do);
                }
                break;
            case NegotiationCommand.Wont:
                if (_remote.Remove(option)) Reply(NegotiationCommand.Dont);
                break;
        }
    }

    private void Subnegotiate(int option, byte[] data, NegotiationResult result)
    {
        if (data.Length == 0 || data[0] != SendVerb) return;

        if (option == TelnetOption.TerminalType)
        {
            byte[] isReply = [Is, .. Ascii(_config.TerminalType)];
            result.Send.Add(Telnet.EncodeSubnegotiation(TelnetOption.TerminalType, isReply));
            _terminalTypeSent = true;
            return;
        }

        if (option == TelnetOption.NewEnviron)
        {
            // SEND lists may carry a binary IBMRSEED seed, so they are not parsed in general.
            // The only request that matters is DEVNAME on its own after our reply: a collision.
            var deviceName = _config.DeviceName;
            if (_environmentSent && deviceName is not null && IsDeviceNameRequest(data))
            {
                result.Events.Add(new NegotiationEvent.DeviceCollision(deviceName));
                result.Send.Add(EncodeEnvironment([("DEVNAME", deviceName)]));
                return;
            }
            result.Send.Add(EncodeEnvironment(Variables()));
            _environmentSent = true;
        }
    }

    private List<(string Name, string Value)> Variables()
    {
        var variables = new List<(string, string)>();
        if (_config.DeviceName is not null) variables.Add(("DEVNAME", _config.DeviceName));
        // Pinned to the system's QKBDTYPE and QCHRID, and to the engine's CCSID 37 table.
        variables.Add(("KBDTYPE", "USB"));
        variables.Add(("CODEPAGE", "37"));
        variables.Add(("CHARSET", "697"));
        // Ask for a Startup Response Record, which reports success or why the device failed.
        variables.Add(("IBMSENDCONFREC", "YES"));
        return variables;
    }

    private static bool IsDeviceNameRequest(byte[] data) => data.AsSpan().SequenceEqual(DevnameRequest);

    /// <summary>NEW-ENVIRON IS with each variable as USERVAR name VALUE value (RFC 1572, RFC 4777).</summary>
    public static byte[] EncodeEnvironment(IEnumerable<(string Name, string Value)> variables)
    {
        var bytes = new List<byte> { Is };
        foreach (var (name, value) in variables)
        {
            bytes.Add(UserVar);
            bytes.AddRange(EscapeEnvironment(Ascii(name)));
            bytes.Add(Value);
            bytes.AddRange(EscapeEnvironment(Ascii(value)));
        }
        return Telnet.EncodeSubnegotiation(TelnetOption.NewEnviron, bytes.ToArray());
    }

    /// <summary>Bytes that look like VAR, VALUE, ESC or USERVAR are sent after an ESC.</summary>
    private static IEnumerable<byte> EscapeEnvironment(byte[] bytes) =>
        bytes.SelectMany(b => b <= UserVar ? new[] { Esc, b } : [b]);

    private static byte[] Ascii(string text) =>
        text.Select(c => c > 0x7e ? throw new ArgumentException($"Not ASCII: {text}") : (byte)c).ToArray();
}
