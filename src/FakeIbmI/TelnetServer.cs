// A scripted fake IBM i (TN5250E host). It negotiates, answers with a Startup Response Record,
// shows a sign-on screen and a main menu, and records what the client sends. Placeholder text
// only. It never logs or exposes a nondisplay field's contents.

using System.Net;
using System.Net.Sockets;

namespace FakeIbmI;

/// <summary>One field the client sent with a read reply. Nondisplay fields come back as asterisks.</summary>
public sealed record ReceivedField(int Row, int Column, string Text);

/// <summary>One 5250 data record from the client: AID, cursor (1-based) and the modified fields.</summary>
public sealed record ReceivedInput(int Aid, int CursorRow, int CursorColumn, IReadOnlyList<ReceivedField> Fields)
{
    /// <summary>The text of the field at this position, or null when it was not sent.</summary>
    public string? FieldAt(int row, int column) => Fields.FirstOrDefault(f => f.Row == row && f.Column == column)?.Text;
}

public sealed class TelnetServer : IAsyncDisposable
{
    private const byte Esc = 0x04;
    private const int EnterAid = 0xf1;
    private const int F3Aid = 0x33;
    private const int RollDownAid = 0xf4;
    private const int RollUpAid = 0xf5;

    private enum Screen
    {
        SignOn,
        Main,

        /// <summary>A 27 x 132 screen (CLEAR UNIT ALTERNATE), from option 132 on the main menu.</summary>
        Wide,

        /// <summary>A list of placeholder items, three pages of five, from option 4 on the main menu.</summary>
        List1,
        List2,
        List3,
    }

    private readonly TcpListener _listener;
    private readonly Action<string>? _log;
    private readonly bool _trace;
    private readonly object _gate = new();
    private readonly HashSet<string> _devices = [];
    private readonly List<ReceivedInput> _inputs = [];
    private readonly List<Task> _tasks = [];
    private readonly HashSet<TcpClient> _clients = [];
    private readonly CancellationTokenSource _stop = new();
    private Task _acceptLoop = Task.CompletedTask;
    private int _connections;
    private int _open;
    private int _nextId;

    private TelnetServer(TcpListener listener, Action<string>? log, bool trace)
    {
        _listener = listener;
        _log = log;
        _trace = trace;
    }

    /// <summary>
    /// Listens on the loopback interface, or on <paramref name="address"/> (the demo container
    /// listens on every interface). Port 0 picks a free port (see <see cref="Port"/>). With
    /// <paramref name="trace"/>, each Telnet negotiation step is logged, and each record's length
    /// (never its contents, which may hold a password).
    /// </summary>
    public static Task<TelnetServer> StartAsync(
        int port = 0, Action<string>? log = null, IPAddress? address = null, bool trace = false)
    {
        var listener = new TcpListener(address ?? IPAddress.Loopback, port);
        listener.Start();
        var host = new TelnetServer(listener, log, trace);
        host._acceptLoop = host.AcceptLoopAsync();
        return Task.FromResult(host);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Connections accepted so far.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Connections open now.</summary>
    public int Open => Volatile.Read(ref _open);

    /// <summary>Every client data record received so far (a copy).</summary>
    public IReadOnlyList<ReceivedInput> Inputs
    {
        get
        {
            lock (_gate) return _inputs.ToList();
        }
    }

    /// <summary>Waits until at least <paramref name="n"/> connections have been accepted.</summary>
    public async Task WaitForConnectionsAsync(int n, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Connections < n)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"only {Connections} of {n} connections");
            await Task.Delay(10);
        }
    }

    /// <summary>Drops every connection from the host side, as if the host closed.</summary>
    public async Task CloseAllAsync()
    {
        TcpClient[] clients;
        lock (_gate) clients = [.. _clients];
        foreach (var client in clients) Close(client);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Open > 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await CloseAllAsync();
        try
        {
            await _acceptLoop;
        }
        catch (Exception e) when (e is ObjectDisposedException or SocketException or OperationCanceledException)
        {
        }
        Task[] tasks;
        lock (_gate) tasks = [.. _tasks];
        await Task.WhenAll(tasks);
        _stop.Dispose();
    }

    private static void Close(TcpClient client)
    {
        try
        {
            client.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Log(string line) => _log?.Invoke(line);

    private void Trace(int id, string line)
    {
        if (_trace) Log($"#{id} {line}");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is ObjectDisposedException or SocketException or OperationCanceledException)
            {
                return;
            }
            var id = Interlocked.Increment(ref _nextId);
            Interlocked.Increment(ref _connections);
            Interlocked.Increment(ref _open);
            lock (_gate)
            {
                _clients.Add(client);
                _tasks.Add(Task.Run(() => RunConnectionAsync(client, id)));
            }
        }
    }

    // ---- one connection ----

    private async Task RunConnectionAsync(TcpClient client, int id)
    {
        string? device = null;
        try
        {
            Log($"#{id} connected");
            device = await ServeAsync(client, id);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            if (device is not null)
            {
                lock (_gate) _devices.Remove(device);
            }
            lock (_gate) _clients.Remove(client);
            Close(client);
            Interlocked.Decrement(ref _open);
            Log($"#{id} closed");
        }
    }

    /// <summary>
    /// Runs one session. Returns the device name still held when it ends, if any.
    /// It negotiates as an IBM i does: one option at a time, asking for an option's value only once
    /// the client has agreed to the option (RFC 1091, 1572). NEW-ENVIRON first, then the terminal
    /// type, then end of record and binary both ways. A client that refuses NEW-ENVIRON still gets
    /// a session, on a device name made up for it.
    /// </summary>
    private async Task<string?> ServeAsync(TcpClient client, int id)
    {
        var stream = client.GetStream();
        var decoder = new TelnetDecoder();
        string? requested = null;
        var environment = false;
        var terminalType = false;
        var environmentRequested = false;
        var terminalTypeRequested = false;
        // DO and WILL sent by this side, and the client's WILL and DO.
        var asked = new HashSet<int>();
        var offered = new HashSet<int>();
        var clientWill = new HashSet<int>();
        var clientDo = new HashSet<int>();
        var started = false;
        string? device = null;
        var screen = Screen.SignOn;
        var buffer = new byte[4096];

        async Task SendAsync(byte[] bytes, string what)
        {
            Trace(id, $"-> {what}");
            await stream.WriteAsync(bytes, _stop.Token);
        }
        Task NegotiateAsync(NegotiationCommand command, int option) =>
            SendAsync(Telnet.EncodeNegotiation(command, option), $"{CommandText(command)} {Telnet.OptionName(option)}");
        async Task AskAsync(int option)
        {
            if (asked.Add(option)) await NegotiateAsync(NegotiationCommand.Do, option);
        }
        async Task OfferAsync(int option)
        {
            if (offered.Add(option)) await NegotiateAsync(NegotiationCommand.Will, option);
        }
        // The terminal type comes after NEW-ENVIRON: asked for, then its value once the client agrees.
        async Task TerminalTypeAsync()
        {
            await AskAsync(TelnetOption.TerminalType);
            if (!clientWill.Contains(TelnetOption.TerminalType) || terminalTypeRequested) return;
            terminalTypeRequested = true;
            await SendAsync(Telnet.EncodeSubnegotiation(TelnetOption.TerminalType, [1]), "SB TERMINAL-TYPE SEND");
        }

        await AskAsync(TelnetOption.NewEnviron);
        while (true)
        {
            var count = await stream.ReadAsync(buffer, _stop.Token);
            if (count == 0) return device;
            foreach (var e in decoder.Push(buffer.AsSpan(0, count)))
            {
                switch (e)
                {
                    case TelnetEvent.Negotiate n:
                        Trace(id, $"<- {CommandText(n.Command)} {Telnet.OptionName(n.Option)}");
                        break;
                    case TelnetEvent.Subnegotiation s:
                        Trace(id, $"<- {DescribeSubnegotiation(s)}");
                        break;
                    case TelnetEvent.Record r:
                        Trace(id, $"<- record, {r.Data.Length} bytes{(started ? "" : " (before the session started: ignored)")}");
                        break;
                    case TelnetEvent.Control c:
                        Trace(id, $"<- IAC {c.Code}");
                        break;
                }

                switch (e)
                {
                    case TelnetEvent.Negotiate { Command: NegotiationCommand.Will } n when Supported(n.Option):
                        clientWill.Add(n.Option);
                        await AskAsync(n.Option);
                        if (n.Option == TelnetOption.NewEnviron && !environmentRequested)
                        {
                            environmentRequested = true;
                            // SEND VAR USERVAR: every variable the client has.
                            await SendAsync(Telnet.EncodeSubnegotiation(TelnetOption.NewEnviron, [1, 0, 3]), "SB NEW-ENVIRON SEND VAR USERVAR");
                        }
                        if (n.Option == TelnetOption.TerminalType && environment) await TerminalTypeAsync();
                        break;
                    case TelnetEvent.Negotiate { Command: NegotiationCommand.Will } n:
                        await NegotiateAsync(NegotiationCommand.Dont, n.Option);
                        break;
                    case TelnetEvent.Negotiate { Command: NegotiationCommand.Wont } n:
                        clientWill.Remove(n.Option);
                        if (n.Option == TelnetOption.NewEnviron && !environment)
                        {
                            Log($"#{id} the client won't use NEW-ENVIRON: no device name from it");
                            environment = true;
                            await TerminalTypeAsync();
                        }
                        else if (!started && n.Option is TelnetOption.TerminalType or TelnetOption.EndOfRecord or TelnetOption.Binary)
                        {
                            Log($"#{id} the client won't use {Telnet.OptionName(n.Option)}: it isn't a 5250 client");
                            return device;
                        }
                        break;
                    case TelnetEvent.Negotiate { Command: NegotiationCommand.Do } n
                        when n.Option is TelnetOption.EndOfRecord or TelnetOption.Binary:
                        clientDo.Add(n.Option);
                        await OfferAsync(n.Option);
                        break;
                    case TelnetEvent.Negotiate { Command: NegotiationCommand.Do, Option: TelnetOption.TimingMark }:
                        // RFC 860: the reply is the mark. Clients use it to check the host is there.
                        await NegotiateAsync(NegotiationCommand.Will, TelnetOption.TimingMark);
                        break;
                    case TelnetEvent.Negotiate { Command: NegotiationCommand.Do } n:
                        await NegotiateAsync(NegotiationCommand.Wont, n.Option);
                        break;
                    case TelnetEvent.Negotiate { Command: NegotiationCommand.Dont } n:
                        clientDo.Remove(n.Option);
                        offered.Remove(n.Option);
                        if (!started && n.Option is TelnetOption.EndOfRecord or TelnetOption.Binary)
                        {
                            Log($"#{id} the client won't take {Telnet.OptionName(n.Option)}: it isn't a 5250 client");
                            return device;
                        }
                        break;
                    case TelnetEvent.Subnegotiation { Option: TelnetOption.NewEnviron } s
                        when s.Data.Length > 0 && s.Data[0] == 0 && !environment:
                        environment = true;
                        requested = DevName(EnvironmentVariables(s.Data));
                        await TerminalTypeAsync();
                        break;
                    case TelnetEvent.Subnegotiation { Option: TelnetOption.TerminalType } s
                        when s.Data.Length > 0 && s.Data[0] == 0 && !terminalType:
                        terminalType = true;
                        Log($"#{id} terminal type {System.Text.Encoding.ASCII.GetString(s.Data, 1, s.Data.Length - 1)}");
                        await AskAsync(TelnetOption.EndOfRecord);
                        await OfferAsync(TelnetOption.EndOfRecord);
                        await AskAsync(TelnetOption.Binary);
                        await OfferAsync(TelnetOption.Binary);
                        break;
                    case TelnetEvent.Record r when started:
                        var (next, message) = HandleRecord(r.Data, screen);
                        if (next is null) return device;
                        screen = next.Value;
                        await SendAsync(Show(screen, message), $"screen {screen}");
                        break;
                }
                if (started || !terminalType || !environment) continue;
                if (!(clientWill.Contains(TelnetOption.EndOfRecord) && clientWill.Contains(TelnetOption.Binary)
                    && clientDo.Contains(TelnetOption.EndOfRecord) && clientDo.Contains(TelnetOption.Binary)))
                {
                    continue;
                }
                started = true;
                string? collision = null;
                lock (_gate)
                {
                    if (requested is not null && _devices.Contains(requested))
                    {
                        collision = requested;
                    }
                    else
                    {
                        device = requested ?? NextDefaultDevice();
                        _devices.Add(device);
                    }
                }
                if (collision is not null)
                {
                    Log($"#{id} device {collision} is in use");
                    await SendAsync(StartupRecord("8902", collision), "startup response 8902 (device in use)");
                    return null;
                }
                Log($"#{id} session started on {device}");
                await SendAsync(StartupRecord("I902", device!), "startup response I902");
                await SendAsync(Show(Screen.SignOn, null), $"screen {Screen.SignOn}");
            }
        }
    }

    /// <summary>The options this side takes from the client.</summary>
    private static bool Supported(int option) =>
        option is TelnetOption.NewEnviron or TelnetOption.TerminalType or TelnetOption.EndOfRecord or TelnetOption.Binary;

    private static string CommandText(NegotiationCommand command) => command.ToString().ToUpperInvariant();

    /// <summary>
    /// A subnegotiation for the trace. NEW-ENVIRON variables show their names, and values only for
    /// those that can't hold a secret (IBMRSEED, IBMSUBSPW and a password-carrying USERVAR can).
    /// </summary>
    private static string DescribeSubnegotiation(TelnetEvent.Subnegotiation s)
    {
        var verb = s.Data.Length == 0 ? "" : s.Data[0] switch { 0 => " IS", 1 => " SEND", 2 => " INFO", _ => $" {s.Data[0]}" };
        if (s.Option == TelnetOption.TerminalType && s.Data.Length > 0 && s.Data[0] == 0)
            return $"SB TERMINAL-TYPE IS {System.Text.Encoding.ASCII.GetString(s.Data, 1, s.Data.Length - 1)}";
        if (s.Option == TelnetOption.NewEnviron && s.Data.Length > 0 && s.Data[0] is 0 or 2)
        {
            var variables = EnvironmentVariables(s.Data).Select(v =>
                v.Value is null ? $"{v.Kind} {v.Name}"
                : TraceableVariables.Contains(v.Name) ? $"{v.Kind} {v.Name}={v.Value}"
                : $"{v.Kind} {v.Name}=({v.Value.Length} characters, not shown)");
            return $"SB NEW-ENVIRON{verb} {string.Join(", ", variables)}".TrimEnd();
        }
        return $"SB {Telnet.OptionName(s.Option)}{verb} ({s.Data.Length} bytes)";
    }

    private static readonly HashSet<string> TraceableVariables =
        ["DEVNAME", "KBDTYPE", "CODEPAGE", "CHARSET", "USER", "IBMFONT", "IBMTRANSFORM", "IBMMFRTYPMDL",
         "IBMMSGQNAME", "IBMMSGQLIB", "IBMASSOCPRT", "IBMPAGESIZE", "IBMWIDTH"];

    private string NextDefaultDevice()
    {
        for (var i = 1; ; i++)
        {
            var name = $"QPADEV{i:0000}";
            if (!_devices.Contains(name)) return name;
        }
    }

    /// <summary>
    /// The variables of a NEW-ENVIRON IS or INFO (RFC 1572): VAR or USERVAR, name, and value (null
    /// when none was given). ESC (2) quotes the next byte.
    /// </summary>
    private static List<(string Kind, string Name, string? Value)> EnvironmentVariables(byte[] data)
    {
        var variables = new List<(string Kind, string Name, string? Value)>();
        var name = new List<byte>();
        var value = new List<byte>();
        string? kind = null;
        var inValue = false;
        void Finish()
        {
            if (kind is not null)
            {
                variables.Add((kind, System.Text.Encoding.ASCII.GetString([.. name]),
                    inValue ? System.Text.Encoding.ASCII.GetString([.. value]) : null));
            }
            name.Clear();
            value.Clear();
            inValue = false;
            kind = null;
        }
        for (var i = 1; i < data.Length; i++)
        {
            var b = data[i];
            if (b == 2 && i + 1 < data.Length)
            {
                (inValue ? value : name).Add(data[++i]);
            }
            else if (b is 0 or 3)
            {
                Finish();
                kind = b == 0 ? "VAR" : "USERVAR";
            }
            else if (b == 1)
            {
                inValue = true;
            }
            else
            {
                (inValue ? value : name).Add(b);
            }
        }
        Finish();
        return variables;
    }

    /// <summary>The device name the client asks for (DEVNAME), or null.</summary>
    private static string? DevName(List<(string Kind, string Name, string? Value)> variables) =>
        variables.FirstOrDefault(v => v.Name == "DEVNAME" && !string.IsNullOrEmpty(v.Value)).Value;

    /// <summary>
    /// Applies one client record. Returns the screen to show next (null to close the
    /// connection) and an error-row message for it. A record that is not a read reply changes nothing.
    /// </summary>
    private (Screen? Next, string? Message) HandleRecord(byte[] bytes, Screen screen)
    {
        if (Records.Parse(bytes) is not ParsedRecord.Data { Record: var record }) return (screen, null);
        // A read reply: cursor row, cursor column, AID, then SBA row column data for each field.
        // Anything with no cursor (such as a query reply) is not input.
        var data = record.Data;
        if (record.Opcode is not (Opcode.NoOperation or Opcode.PutGet) || data.Length < 3 || data[0] == 0) return (screen, null);
        var aid = data[2];
        var fields = new List<ReceivedField>();
        var i = 3;
        while (i < data.Length)
        {
            if (data[i] != 0x11 || i + 2 >= data.Length) break;
            var row = data[i + 1];
            var column = data[i + 2];
            i += 3;
            var start = i;
            while (i < data.Length && data[i] != 0x11) i++;
            var text = Ebcdic.Decode(data.AsSpan(start, i - start)).Replace('\0', ' ').TrimEnd();
            var secret = screen == Screen.SignOn && row == PasswordRow && column == FieldColumn;
            fields.Add(new ReceivedField(row, column, secret ? new string('*', text.Length) : text));
        }
        var input = new ReceivedInput(aid, data[0], data[1], fields);
        lock (_gate) _inputs.Add(input);

        if (screen == Screen.SignOn)
        {
            var user = input.FieldAt(UserRow, FieldColumn)?.Trim() ?? "";
            if (aid == EnterAid && user.Length > 0)
            {
                Log($"sign-on by {user}");
                return (Screen.Main, null);
            }
            if (aid != EnterAid) return (Screen.SignOn, null);
            Log("sign-on rejected: user required");
            return (Screen.SignOn, "User required");
        }

        if (screen == Screen.Wide)
        {
            Log("wide screen: back to the menu");
            return (Screen.Main, null);
        }

        if (screen is Screen.List1 or Screen.List2 or Screen.List3)
        {
            var page = screen - Screen.List1;
            if (aid == RollUpAid)
                return page == ListPages - 1 ? (screen, "Already at the last page (placeholder)") : (screen + 1, null);
            if (aid == RollDownAid)
                return page == 0 ? (screen, "Already at the first page (placeholder)") : (screen - 1, null);
            Log("list: back to the menu");
            return (Screen.Main, null);
        }

        if (aid == F3Aid)
        {
            Log("sign-off (F3)");
            return (Screen.SignOn, null);
        }
        if (aid == EnterAid)
        {
            var text = input.FieldAt(SelectionRow, SelectionColumn)?.Trim() ?? "";
            if (text == "90" || text.Equals("signoff", StringComparison.OrdinalIgnoreCase))
            {
                Log("sign-off (SIGNOFF)");
                return (null, null);
            }
            if (text == "4")
            {
                Log("main menu: list");
                return (Screen.List1, null);
            }
            if (text == "132")
            {
                Log("main menu: wide screen");
                return (Screen.Wide, null);
            }
            if (text.Length > 0)
            {
                Log("main menu: invalid option");
                return (Screen.Main, $"Option {text} is not valid (placeholder)");
            }
        }
        return (Screen.Main, null);
    }

    private static byte[] Show(Screen screen, string? message) => screen switch
    {
        Screen.SignOn => SignOnScreen(message),
        Screen.Wide => WideScreen(),
        Screen.List1 or Screen.List2 or Screen.List3 => ListScreen(screen - Screen.List1, message),
        _ => MainScreen(message),
    };

    // ---- screens ----

    private const int UserRow = 6;
    private const int PasswordRow = 7;
    private const int FieldColumn = 53;
    private const int SelectionRow = 20;
    private const int SelectionColumn = 7;

    private static byte[] Text(string s) => Ebcdic.Encode(s);

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Sba(int row, int column) => [0x11, (byte)row, (byte)column];

    /// <summary>SF with a field format word, attribute and length.</summary>
    private static byte[] Sf(int ffw, int attribute, int length) =>
        [0x1d, (byte)(ffw >> 8), (byte)(ffw & 0xff), (byte)attribute, (byte)(length >> 8), (byte)(length & 0xff)];

    private static byte[] Header(int errorRow) => [0x01, 0x07, 0x00, 0x00, 0x00, (byte)errorRow, 0x00, 0x00, 0x00];

    /// <summary>CLEAR UNIT, then WTD (unlock keyboard, 0x00 0x18) with orders, then READ MDT FIELDS.</summary>
    private static byte[] Screen5250(params byte[][] orders) => Screen5250Clear([Esc, 0x40], orders);

    /// <summary>The same after CLEAR UNIT ALTERNATE 0x00: a 27 x 132 screen.</summary>
    private static byte[] Screen5250Wide(params byte[][] orders) => Screen5250Clear([Esc, 0x20, 0x00], orders);

    private static byte[] Screen5250Clear(byte[] clear, byte[][] orders) =>
        HostRecord(
            0x03,
            Join(
                clear,
                [Esc, 0x11, 0x00, 0x18],
                Join(orders),
                [Esc, 0x52, 0x00, 0x00]));

    private static byte[] Message(string? message) =>
        message is null ? [] : Join(Sba(24, 2), [0x22], Text(message));

    private static byte[] SignOnScreen(string? message) =>
        Screen5250(
            Header(0x18),
            Sba(1, 34),
            [0x22],
            Text("Sign On"),
            [0x20],
            Sba(6, 17),
            Text("User"),
            Sba(UserRow, FieldColumn - 1),
            Sf(0x4020, 0x24, 10),
            Sba(PasswordRow, 17),
            Text("Password"),
            Sba(PasswordRow, FieldColumn - 1),
            Sf(0x4020, 0x27, 10),
            Message(message));

    private static byte[] MainScreen(string? message) =>
        Screen5250(
            Header(0x18),
            Sba(1, 2),
            Text("MAIN"),
            Sba(1, 33),
            [0x22],
            Text("IBM i Main Menu"),
            [0x20],
            Sba(4, 2),
            Text("Select one of the following:"),
            Sba(6, 5),
            Text("1. Placeholder option one"),
            Sba(7, 5),
            Text("2. Placeholder option two"),
            Sba(8, 5),
            Text("3. Placeholder option three"),
            Sba(9, 5),
            Text("4. Placeholder list (3 pages)"),
            Sba(10, 4),
            Text("90. Sign off"),
            Sba(11, 3),
            Text("132. Wide placeholder screen (27 x 132)"),
            Sba(19, 2),
            Text("Selection or command"),
            Sba(SelectionRow, 2),
            Text("===>"),
            Sba(SelectionRow, SelectionColumn - 1),
            Sf(0x4000, 0x24, 60),
            Sba(22, 2),
            Text("F3=Exit   F4=Prompt   F12=Cancel"),
            Message(message));

    private const int ListPages = 3;
    private const int ListPageSize = 5;
    private const int ListFirstRow = 7;

    /// <summary>
    /// One page of the placeholder list: an option field per record, a heading row, the
    /// option legend and More... or Bottom, as a subfile shows them.
    /// </summary>
    private static byte[] ListScreen(int page, string? message)
    {
        var rows = new List<byte[]>();
        for (var i = 0; i < ListPageSize; i++)
        {
            var n = page * ListPageSize + i + 1;
            var row = ListFirstRow + i;
            rows.Add(Join(
                Sba(row, 2),
                Sf(0x4000, 0x24, 1),
                Sba(row, 4),
                [0x20],
                Sba(row, 7),
                Text($"ITEM{n:000}"),
                Sba(row, 19),
                Text($"Placeholder item {n}"),
                Sba(row, 50),
                Text($"{n * 3,6}.00{(n % 4 == 0 ? "-" : " ")}")));
        }
        return Screen5250(
            Header(0x18),
            Sba(1, 2),
            Text("LIST"),
            Sba(1, 28),
            [0x22],
            Text("Work with Placeholder Items"),
            [0x20],
            Sba(3, 2),
            Text("Type options, press Enter."),
            Sba(4, 3),
            Text("2=Change   4=Delete   5=Display"),
            // The attribute in column 1, so each heading starts over its column.
            Sba(6, 1),
            [0x22],
            Text("Opt  Item        Description                    Quantity"),
            [0x20],
            Join([.. rows]),
            Sba(ListFirstRow + ListPageSize, 70),
            Text(page == ListPages - 1 ? "Bottom" : "More..."),
            Sba(22, 2),
            Text("F3=Exit   F12=Cancel"),
            Message(message));
    }

    private static byte[] WideScreen() =>
        Screen5250Wide(
            Header(27),
            Sba(1, 55),
            [0x22],
            Text("Wide Placeholder Screen"),
            [0x20],
            Sba(3, 2),
            Text("Left edge"),
            // Ends in column 132, the last.
            Sba(3, 123),
            Text("Right edge"),
            Sba(26, 2),
            Text("F3=Exit   F12=Cancel"));

    // ---- Telnet and 5250 framing ----

    private static byte[] HostRecord(int opcode, byte[] data)
    {
        var length = 10 + data.Length;
        byte[] header = [(byte)(length >> 8), (byte)(length & 0xff), 0x12, 0xa0, 0, 0, 4, 0, 0, (byte)opcode];
        return Telnet.EncodeRecord([.. header, .. data]);
    }

    private static byte[] StartupRecord(string code, string deviceName)
    {
        var record = new byte[0x49];
        byte[] head = [0x00, 0x49, 0x12, 0xa0, 0x90, 0x00, 0x05, 0x60, 0x06, 0x00, 0x20, 0xc0, 0x00, 0x3d, 0x00, 0x00];
        head.CopyTo(record, 0);
        Ebcdic.Encode(code).CopyTo(record, 16);
        Ebcdic.Encode("SYSTEM  ").CopyTo(record, 20);
        Ebcdic.Encode(deviceName.PadRight(10, ' ')).CopyTo(record, 28);
        return Telnet.EncodeRecord(record);
    }
}
