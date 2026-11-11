// One TN5250E session as a pure state machine: host bytes and keystrokes in, client
// bytes and events out. It has no I/O and no threads, so any transport can drive it.
// It is not thread-safe: drive each Connection from one thread at a time.

using System.Text.Json.Serialization;

namespace FakeIbmI.Tests.Client;

public sealed record ConnectionConfig
{
    /// <summary>A key of <see cref="Query.Terminals"/>, for example IBM-3477-FC.</summary>
    public required string TerminalType { get; init; }

    public string? DeviceName { get; init; }

    /// <summary>Record the session for offline replay (dev only).</summary>
    public RecordingOptions? Recording { get; init; }
}

/// <param name="Now">The clock, in milliseconds.</param>
/// <param name="Blank">
/// What recorded client data leaves out: nondisplay input fields, or every input field.
/// Blanked fields become blanks for their whole length. <see cref="Blank.None"/> is refused.
/// </param>
public sealed record RecordingOptions(Func<double> Now, Blank Blank);

/// <summary>
/// A recording the connection makes itself, so nothing else can add raw bytes to it.
/// Host data is stored as whole Telnet events; client data as scrubbed copies of each send.
/// </summary>
public sealed class SessionRecording
{
    internal SessionRecording(Recorder recorder) => Recorder = recorder;

    internal Recorder Recorder { get; }

    public IReadOnlyList<RecordedChunk> Chunks => Recorder.Chunks;

    public byte[] ToBytes() => Recorder.ToBytes();
}

/// <summary>
/// What a send is for.
/// - negotiation: Telnet option replies.
/// - query-reply: the answer to the 5250 QUERY.
/// - protocol: 5250 replies that carry no user or screen data: the Cancel Invite reply, and
///   negative responses to host records the client can't process (a 4-byte code).
/// - host-read: answers to host reads that need no key press (SAVE SCREEN, READ SCREEN,
///   READ IMMEDIATE); they carry screen or field contents.
/// - input: anything a key the user pressed caused; it only ever follows the user.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutboundKind>))]
public enum OutboundKind
{
    [JsonStringEnumMemberName("negotiation")]
    Negotiation,

    [JsonStringEnumMemberName("query-reply")]
    QueryReply,

    [JsonStringEnumMemberName("protocol")]
    Protocol,

    [JsonStringEnumMemberName("host-read")]
    HostRead,

    [JsonStringEnumMemberName("input")]
    Input,
}

/// <param name="Bytes">Always a fresh, unshared buffer, so it can go straight to the socket.</param>
public sealed record Outbound(OutboundKind Kind, byte[] Bytes)
{
    public bool Equals(Outbound? other) => other is not null && Kind == other.Kind && Bytes.AsSpan().SequenceEqual(other.Bytes);

    public override int GetHashCode() => HashCode.Combine(Kind, Bytes.Length);
}

/// <summary>
/// Everything a connection reports. <see cref="Type"/> is the TS event type string, which
/// the server sends to the browser.
/// </summary>
public abstract record ConnectionEvent
{
    public abstract string Type { get; }

    /// <summary>Telnet negotiation is complete and 5250 records can flow.</summary>
    public sealed record Negotiated : ConnectionEvent
    {
        public override string Type => "negotiated";
    }

    public sealed record Startup(StartupResult Result) : ConnectionEvent
    {
        public override string Type => "startup";
    }

    /// <summary>The requested device is held by another session.</summary>
    public sealed record DeviceInUse(string DeviceName) : ConnectionEvent
    {
        public override string Type => "device-in-use";
    }

    /// <summary>The IBM i started the session on a different device from the one requested.</summary>
    public sealed record DeviceMismatch(string Requested, string Actual) : ConnectionEvent
    {
        public override string Type => "device-mismatch";
    }

    public sealed record QueryAnswered : ConnectionEvent
    {
        public override string Type => "query-answered";
    }

    /// <summary>Every 5250 data record from the host, for tools and diagnostics.</summary>
    public sealed record Record([property: JsonPropertyName("record")] HostRecord HostRecord) : ConnectionEvent
    {
        public override string Type => "record";
    }

    /// <summary>The screen after a record or a keystroke changed it.</summary>
    public sealed record Screen(ScreenSnapshot Snapshot) : ConnectionEvent
    {
        public override string Type => "screen";
    }

    /// <summary>
    /// A key was typed ahead while a READ is pending and the keyboard is locked with no
    /// error, so the host waits for input the user can't give. Reported once per host
    /// record. A diagnostic: some emulators unlock on every READ.
    /// </summary>
    public sealed record KeyboardStuck : ConnectionEvent
    {
        public override string Type => "keyboard-stuck";
    }

    /// <summary>A negative response went to the host; `Code` is from table 13-26.</summary>
    public sealed record NegativeResponse(int Code) : ConnectionEvent
    {
        public override string Type => "negative-response";
    }

    /// <summary>
    /// Something the engine doesn't handle. A command or structured field ends the record
    /// with a negative response; a WDSF order is skipped and the record goes on.
    /// </summary>
    public sealed record Unsupported(string What) : ScreenEvent
    {
        public override string Type => "unsupported";
    }

    /// <summary>The record broke the data stream rules; the rest was rejected with a negative response.</summary>
    public sealed record DataStreamError(string Message) : ScreenEvent
    {
        public override string Type => "data-stream-error";
    }

    public sealed record Alarm : ScreenEvent
    {
        public override string Type => "alarm";
    }

    public sealed record ProtocolError(string Message) : ConnectionEvent
    {
        public override string Type => "protocol-error";
    }
}

/// <summary>
/// The events applying a host record can raise (datastream.ts): <see cref="ConnectionEvent.Unsupported"/>,
/// <see cref="ConnectionEvent.DataStreamError"/> and <see cref="ConnectionEvent.Alarm"/>.
/// </summary>
public abstract record ScreenEvent : ConnectionEvent;

public sealed class ReceiveResult
{
    public List<Outbound> Send { get; } = [];
    public List<ConnectionEvent> Events { get; } = [];
}

public sealed class Connection
{
    private const byte Esc = 0x04;

    private readonly string? _deviceName;
    private readonly TelnetDecoder _decoder = new();
    private readonly TelnetNegotiator _negotiator;
    private readonly Screen _screen = new();
    private readonly SessionRecording? _recording;
    private readonly Blank _blank;

    /// <summary>Host bytes waiting to be recorded, and client sends recorded after them.</summary>
    private List<byte[]> _hostLog = [];

    private List<byte[]> _clientLog = [];
    private bool _collision;
    private bool _inUseReported;
    private bool _stuckReported;

    public Connection(ConnectionConfig config)
    {
        if (!Query.Terminals.TryGetValue(config.TerminalType, out var terminal))
        {
            throw new ArgumentException($"Unsupported terminal type {config.TerminalType}", nameof(config));
        }
        if (config.Recording is { Blank: Blank.None })
        {
            throw new ArgumentException("A recording must blank nondisplay or all input", nameof(config));
        }
        Terminal = terminal;
        _deviceName = config.DeviceName;
        _negotiator = new TelnetNegotiator(new NegotiationConfig(terminal.TerminalType, config.DeviceName));
        _recording = config.Recording is { } options ? new SessionRecording(new Recorder(options.Now)) : null;
        _blank = config.Recording?.Blank ?? Blank.None;
    }

    public TerminalModel Terminal { get; }

    /// <summary>The session recording, when the connection was created with `Recording`.</summary>
    public SessionRecording? Recording => _recording;

    private Recorder? Recorder => _recording?.Recorder;

    public ReceiveResult Receive(ReadOnlySpan<byte> bytes)
    {
        var result = new ReceiveResult();
        foreach (var telnetEvent in _decoder.Push(bytes))
        {
            if (telnetEvent is TelnetEvent.Record record)
            {
                Log(Telnet.EncodeRecord(HandleRecord(record.Data, result)));
                continue;
            }
            Log(Telnet.EncodeTelnetEvent(telnetEvent));
            var negotiation = _negotiator.Handle(telnetEvent);
            foreach (var chunk in negotiation.Send) Send(result, OutboundKind.Negotiation, chunk);
            foreach (var e in negotiation.Events)
            {
                if (e is NegotiationEvent.Ready) result.Events.Add(new ConnectionEvent.Negotiated());
                if (e is NegotiationEvent.DeviceCollision) _collision = true;
            }
        }
        FlushLog();
        return result;
    }

    /// <summary>One keystroke. Keys typed while the keyboard is locked wait in the type-ahead buffer.</summary>
    public ReceiveResult Key(Key key)
    {
        var result = new ReceiveResult();
        var handled = Keyboard.HandleKey(_screen, key);
        KeyActions(handled, result);
        CheckStuck(result);
        if (handled.Changed) Snapshot(result);
        FlushLog();
        return result;
    }

    public ScreenSnapshot Screen => _screen.Snapshot();

    /// <summary>Call when the transport closes; reports a collision that ended the session.</summary>
    public IReadOnlyList<ConnectionEvent> Closed()
    {
        if (_collision && !_inUseReported && _deviceName is not null)
        {
            _inUseReported = true;
            return [new ConnectionEvent.DeviceInUse(_deviceName)];
        }
        return [];
    }

    /// <summary>
    /// Queues a send. `recordable` is the copy a recording keeps: the same bytes unless the
    /// send carries screen or field contents, which are built again with fields blanked.
    /// </summary>
    private void Send(ReceiveResult result, OutboundKind kind, byte[] bytes, byte[]? recordable = null)
    {
        result.Send.Add(new Outbound(kind, bytes));
        if (Recorder is not null) _clientLog.Add(recordable ?? bytes);
    }

    /// <summary>Notes host bytes for the recording; client sends made meanwhile are recorded after them.</summary>
    private void Log(byte[] host)
    {
        if (Recorder is null) return;
        _hostLog.Add(host);
        if (_clientLog.Count > 0) FlushLog();
    }

    private void FlushLog()
    {
        var recorder = Recorder;
        if (recorder is null) return;
        if (_hostLog.Count > 0) recorder.Add(Direction.Host, JsArrays.Concat(_hostLog));
        foreach (var chunk in _clientLog) recorder.Add(Direction.Client, chunk);
        _hostLog = [];
        _clientLog = [];
    }

    private void Snapshot(ReceiveResult result) => result.Events.Add(new ConnectionEvent.Screen(_screen.Snapshot()));

    /// <summary>Handles one host record. Returns the bytes a recording keeps for it.</summary>
    private byte[] HandleRecord(byte[] bytes, ReceiveResult result)
    {
        if (!_negotiator.IsReady)
        {
            result.Events.Add(new ConnectionEvent.ProtocolError("5250 record before negotiation finished"));
            return bytes;
        }
        switch (Records.Parse(bytes))
        {
            case ParsedRecord.Invalid invalid:
                result.Events.Add(new ConnectionEvent.ProtocolError(invalid.Reason));
                return bytes;
            case ParsedRecord.Startup parsed:
            {
                var startup = StartupCodes.Describe(parsed.Response);
                result.Events.Add(new ConnectionEvent.Startup(startup));
                var requested = _deviceName;
                if (startup.Code == StartupCodes.DeviceInUseCode && requested is not null)
                {
                    _inUseReported = true;
                    result.Events.Add(new ConnectionEvent.DeviceInUse(requested));
                }
                else if (startup.Ok && requested is not null && startup.DeviceName != requested)
                {
                    result.Events.Add(new ConnectionEvent.DeviceMismatch(requested, startup.DeviceName));
                }
                return bytes;
            }
            case ParsedRecord.Data parsed:
            {
                result.Events.Add(new ConnectionEvent.Record(parsed.Record));
                var applied = DataStream.ApplyRecord(_screen, parsed.Record, Terminal.Wide);
                result.Events.AddRange(applied.Events);
                foreach (var request in applied.Requests) Answer(request, result);
                _stuckReported = false;
                // Keys typed ahead go once the whole record has been applied (page 16-13).
                var drained = Keyboard.DrainTypeAhead(_screen);
                KeyActions(drained, result);
                if (applied.Changed || drained.Changed) Snapshot(result);
                return Recorder is not null ? ScrubRestores(bytes, parsed.Record.Data, applied) : bytes;
            }
            default:
                throw new InvalidOperationException("unknown parsed record");
        }
    }

    /// <summary>
    /// Reports a real hang only: a key waits in the type-ahead buffer while the host waits
    /// for input. A READ with the keyboard still locked is often followed by a record that
    /// unlocks it, and replays never see the keys that answered a READ.
    /// </summary>
    private void CheckStuck(ReceiveResult result)
    {
        var screen = _screen;
        if (!_stuckReported
            && screen.TypeAhead.Count > 0
            && screen.PendingRead is not null
            && screen.KeyboardState == KeyboardState.Locked
            && screen.HeldAid is null)
        {
            _stuckReported = true;
            result.Events.Add(new ConnectionEvent.KeyboardStuck());
        }
    }

    private void Answer(HostRequest request, ReceiveResult result)
    {
        switch (request)
        {
            case HostRequest.Query:
                Send(result, OutboundKind.QueryReply, Client.Query.EncodeQueryReply(Terminal));
                result.Events.Add(new ConnectionEvent.QueryAnswered());
                return;
            case HostRequest.CancelInvite:
                Send(result, OutboundKind.Protocol, Records.EncodeClientRecord(Opcode.CancelInvite, 0, []));
                return;
            case HostRequest.NegativeResponse negative:
                Send(result, OutboundKind.Protocol, Client.NegativeResponse.Encode(negative.Code));
                result.Events.Add(new ConnectionEvent.NegativeResponse(negative.Code));
                return;
            case HostRequest.Read:
            {
                var aid = _screen.HeldAid;
                _screen.HeldAid = null;
                if (aid is int held) SendRead(held, result);
                return;
            }
            case HostRequest.Save save:
            {
                var saved = save.Saved;
                var length = saved.Length + 2;
                byte[] head = save.Partial ? [Esc, 0x13, (byte)(length >> 8), (byte)(length & 0xff)] : [Esc, 0x12];
                byte[] Build(byte[] data) => Records.EncodeClientRecord(Opcode.SaveScreen, 0, [.. head, .. data]);
                Send(
                    result,
                    OutboundKind.HostRead,
                    Build(saved),
                    Recordable(() => Build(SavedScreens.Scrub(saved, _blank))));
                return;
            }
            case HostRequest.ReadScreen:
            {
                var buffer = _screen.Buffer;
                var fields = _screen.Fields;
                byte[] Build(byte[] data) => Records.EncodeClientRecord(Opcode.NoOperation, 0, data);
                Send(
                    result,
                    OutboundKind.HostRead,
                    Build((byte[])buffer.Clone()),
                    Recordable(() => Build(SavedScreens.BlankSensitive(buffer, fields, _blank))));
                return;
            }
            case HostRequest.ReadImmediate immediate:
            {
                byte[] Build(Blank blank) =>
                    Records.EncodeClientRecord(Opcode.PutGet, 0, Reply.EncodeReadReply(_screen, immediate.Command, 0, blank));
                Send(result, OutboundKind.HostRead, Build(Blank.None), Recordable(() => Build(_blank)));
                return;
            }
        }
    }

    /// <summary>Builds the recording's copy of a send only when there is a recording.</summary>
    private byte[]? Recordable(Func<byte[]> build) => Recorder is not null ? build() : null;

    /// <summary>
    /// The recording's copy of a host record: saved-screen data in it, which echoes what the
    /// client saved (typed nondisplay input included), is blanked. Saved data the parser
    /// rejected or never reached, after an error earlier in the record, is zeroed to the end.
    /// </summary>
    private byte[] ScrubRestores(byte[] bytes, byte[] data, ApplyResult applied)
    {
        var offset = bytes.Length - data.Length;
        var copy = (byte[])bytes.Clone();
        foreach (var (start, end) in applied.Restores)
        {
            SavedScreens.Scrub(data[start..end], _blank).CopyTo(copy, offset + start);
        }
        for (var i = 0; i + 1 < data.Length; i++)
        {
            var restore = data[i] == Esc && (data[i + 1] == 0x12 || data[i + 1] == 0x13);
            if (restore && !applied.Restores.Any(r => i >= r.Start - 4 && i < r.End))
            {
                JsArrays.Fill(copy, 0, offset + i + 2, copy.Length);
                break;
            }
        }
        return copy;
    }

    private void KeyActions(KeyResult handled, ReceiveResult result)
    {
        if (handled.Alarm) result.Events.Add(new ConnectionEvent.Alarm());
        foreach (var action in handled.Actions)
        {
            switch (action)
            {
                case KeyAction.Aid aid:
                    SendRead(aid.Code, result);
                    break;
                case KeyAction.Attention:
                    Signal(RecordFlag.Atn, [], result);
                    break;
                case KeyAction.SystemRequest:
                    Signal(RecordFlag.Srq, [], result);
                    break;
                case KeyAction.Help help:
                    Signal(RecordFlag.Hlp, help.Code, result);
                    break;
            }
        }
    }

    /// <summary>Attn, SysReq and Help in error state: a flag in the record header (RFC 1205).</summary>
    private void Signal(int flag, byte[] data, ReceiveResult result) =>
        Send(result, OutboundKind.Input, Records.EncodeClientRecord(Opcode.NoOperation, flag, data));

    /// <summary>Answers the pending READ with this AID, then applies the READ's control characters.</summary>
    private void SendRead(int aid, ReceiveResult result)
    {
        var screen = _screen;
        if (screen.PendingRead is not int command) return;
        foreach (var what in Reply.ReplyLimitations(screen)) result.Events.Add(new ConnectionEvent.Unsupported(what));
        byte[] Build(Blank blank) => Records.EncodeClientRecord(Opcode.PutGet, 0, Reply.EncodeReadReply(screen, command, aid, blank));
        Send(result, OutboundKind.Input, Build(Blank.None), Recordable(() => Build(_blank)));
        screen.PendingRead = null;
        var (cc0, cc1) = screen.ReadControl;
        screen.ApplyControlByte0(cc0);
        if (screen.ApplyControlByte1(cc1)) result.Events.Add(new ConnectionEvent.Alarm());
    }
}
