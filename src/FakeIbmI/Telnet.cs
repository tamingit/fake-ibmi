// Telnet framing for TN5250 (RFC 854, 856, 885, 1091, 1572).
// 5250 records end with IAC EOR, and 0xFF data bytes are sent doubled (IAC IAC).

namespace FakeIbmI;

public static class TelnetOption
{
    public const int Binary = 0;
    public const int SuppressGoAhead = 3;
    public const int TimingMark = 6;
    public const int TerminalType = 24;
    public const int EndOfRecord = 25;
    public const int NewEnviron = 39;
}

public enum NegotiationCommand
{
    Will,
    Wont,
    Do,
    Dont,
}

public abstract record TelnetEvent
{
    /// <summary>The TS event type: record, negotiate, subnegotiation or command.</summary>
    public abstract string Type { get; }

    /// <summary>A complete 5250 record with IAC doubling removed and the IAC EOR stripped.</summary>
    public sealed record Record(byte[] Data) : TelnetEvent
    {
        public override string Type => "record";

        public bool Equals(Record? other) => other is not null && Data.AsSpan().SequenceEqual(other.Data);

        public override int GetHashCode() => Data.Length;
    }

    public sealed record Negotiate(NegotiationCommand Command, int Option) : TelnetEvent
    {
        public override string Type => "negotiate";
    }

    public sealed record Subnegotiation(int Option, byte[] Data) : TelnetEvent
    {
        public override string Type => "subnegotiation";

        public bool Equals(Subnegotiation? other) =>
            other is not null && Option == other.Option && Data.AsSpan().SequenceEqual(other.Data);

        public override int GetHashCode() => HashCode.Combine(Option, Data.Length);
    }

    /// <summary>Any other two-byte command, such as NOP.</summary>
    public sealed record Control(int Code) : TelnetEvent
    {
        public override string Type => "command";
    }
}

public static class Telnet
{
    public const byte Iac = 0xff;
    public const byte Dont = 0xfe;
    public const byte Do = 0xfd;
    public const byte Wont = 0xfc;
    public const byte Will = 0xfb;
    public const byte Sb = 0xfa;
    public const byte Se = 0xf0;
    public const byte Eor = 0xef;

    internal static NegotiationCommand CommandName(int code) =>
        code switch
        {
            Will => NegotiationCommand.Will,
            Wont => NegotiationCommand.Wont,
            Do => NegotiationCommand.Do,
            _ => NegotiationCommand.Dont,
        };

    private static byte CommandCode(NegotiationCommand command) =>
        command switch
        {
            NegotiationCommand.Will => Will,
            NegotiationCommand.Wont => Wont,
            NegotiationCommand.Do => Do,
            _ => Dont,
        };

    public static string OptionName(int option) =>
        option switch
        {
            TelnetOption.Binary => "BINARY",
            TelnetOption.SuppressGoAhead => "SUPPRESS-GO-AHEAD",
            TelnetOption.TimingMark => "TIMING-MARK",
            TelnetOption.TerminalType => "TERMINAL-TYPE",
            TelnetOption.EndOfRecord => "END-OF-RECORD",
            TelnetOption.NewEnviron => "NEW-ENVIRON",
            _ => $"option {option}",
        };

    /// <summary>The bytes of a decoded event, framed again. Used to record host data event by event.</summary>
    public static byte[] EncodeTelnetEvent(TelnetEvent telnetEvent) =>
        telnetEvent switch
        {
            TelnetEvent.Record r => EncodeRecord(r.Data),
            TelnetEvent.Negotiate n => EncodeNegotiation(n.Command, n.Option),
            TelnetEvent.Subnegotiation s => EncodeSubnegotiation(s.Option, s.Data),
            TelnetEvent.Control c => [Iac, (byte)c.Code],
            _ => throw new ArgumentOutOfRangeException(nameof(telnetEvent)),
        };

    /// <summary>Frames a 5250 record for sending: doubles each 0xFF and appends IAC EOR.</summary>
    public static byte[] EncodeRecord(ReadOnlySpan<byte> data)
    {
        var output = new ByteBuilder(data.Length + 2);
        PushEscaped(output, data);
        output.Push(Iac);
        output.Push(Eor);
        return output.Take();
    }

    public static byte[] EncodeNegotiation(NegotiationCommand command, int option) =>
        [Iac, CommandCode(command), (byte)option];

    /// <summary>IAC SB option data IAC SE, with 0xFF in the data doubled.</summary>
    public static byte[] EncodeSubnegotiation(int option, ReadOnlySpan<byte> data)
    {
        var output = new ByteBuilder(data.Length + 5);
        output.Push(Iac);
        output.Push(Sb);
        output.Push((byte)option);
        PushEscaped(output, data);
        output.Push(Iac);
        output.Push(Se);
        return output.Take();
    }

    private static void PushEscaped(ByteBuilder output, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            output.Push(b);
            if (b == Iac) output.Push(Iac);
        }
    }
}

/// <summary>
/// Splits the byte stream from the host into records and Telnet commands.
/// Chunks may break anywhere, including between IAC and the byte after it.
/// </summary>
public sealed class TelnetDecoder
{
    private enum State
    {
        Data,
        Iac,
        Negotiate,
        SbOption,
        SbData,
        SbIac,
    }

    private State _state = State.Data;
    private readonly ByteBuilder _record = new();
    private readonly ByteBuilder _sub = new();
    private int _command;
    private int _subOption;

    public List<TelnetEvent> Push(ReadOnlySpan<byte> chunk)
    {
        var events = new List<TelnetEvent>();
        foreach (var b in chunk)
        {
            switch (_state)
            {
                case State.Data:
                    if (b == Telnet.Iac) _state = State.Iac;
                    else _record.Push(b);
                    break;
                case State.Iac:
                    AfterIac(b, events);
                    break;
                case State.Negotiate:
                    events.Add(new TelnetEvent.Negotiate(Telnet.CommandName(_command), b));
                    _state = State.Data;
                    break;
                case State.SbOption:
                    _subOption = b;
                    _state = State.SbData;
                    break;
                case State.SbData:
                    if (b == Telnet.Iac) _state = State.SbIac;
                    else _sub.Push(b);
                    break;
                case State.SbIac:
                    if (b == Telnet.Iac)
                    {
                        _sub.Push(Telnet.Iac);
                        _state = State.SbData;
                    }
                    else
                    {
                        // IAC SE ends the subnegotiation. Any other command also ends it,
                        // and is then handled as a normal command.
                        events.Add(new TelnetEvent.Subnegotiation(_subOption, _sub.Take()));
                        if (b == Telnet.Se) _state = State.Data;
                        else AfterIac(b, events);
                    }
                    break;
            }
        }
        return events;
    }

    private void AfterIac(byte b, List<TelnetEvent> events)
    {
        _state = State.Data;
        if (b == Telnet.Iac)
        {
            _record.Push(Telnet.Iac);
        }
        else if (b == Telnet.Eor)
        {
            events.Add(new TelnetEvent.Record(_record.Take()));
        }
        else if (b >= Telnet.Will && b <= Telnet.Dont)
        {
            _command = b;
            _state = State.Negotiate;
        }
        else if (b == Telnet.Sb)
        {
            _state = State.SbOption;
        }
        else
        {
            events.Add(new TelnetEvent.Control(b));
        }
    }
}

internal sealed class ByteBuilder(int capacity = 256)
{
    private byte[] _bytes = new byte[Math.Max(capacity, 16)];
    private int _length;

    public void Push(byte b)
    {
        if (_length == _bytes.Length) Array.Resize(ref _bytes, _bytes.Length * 2);
        _bytes[_length++] = b;
    }

    /// <summary>Returns the bytes collected so far and starts again empty.</summary>
    public byte[] Take()
    {
        var output = _bytes.AsSpan(0, _length).ToArray();
        _length = 0;
        return output;
    }
}
