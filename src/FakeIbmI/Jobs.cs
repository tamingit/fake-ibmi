// The jobs programs run in: one with no display (the remote command server's), and one on a
// 5250 session, where the program runs on its own thread and the session passes screens out and
// the user's input in.

using System.Collections.Concurrent;
using System.Threading.Channels;
using FakeIbmI.Rpg;
using static FakeIbmI.Stream5250;

namespace FakeIbmI;

/// <summary>A job with no display: DSPLY's messages are kept, and nobody replies.</summary>
internal sealed class BatchJob(string user, CancellationToken cancelled) : IJobHost
{
    private readonly List<string> _messages = [];

    public string User => user;
    public CancellationToken Cancelled => cancelled;
    public IWorkstation? Workstation => null;

    /// <summary>What DSPLY showed, in order.</summary>
    public IReadOnlyList<string> Messages => _messages;

    public string? Dsply(string message, int replyLength)
    {
        _messages.Add(message);
        return null;
    }
}

/// <summary>What a running program wants of its 5250 session next.</summary>
/// <param name="Screen">A record to send, or null when the program has ended.</param>
/// <param name="Wait">Whether the program now waits for the user's input.</param>
/// <param name="Message">When it has ended: why it failed, or null when it didn't.</param>
internal sealed record RunEvent(byte[]? Screen, bool Wait, string? Message);

/// <summary>A program running in a 5250 session.</summary>
internal sealed class InteractiveJob : IJobHost, IWorkstation
{
    private readonly Channel<RunEvent> _events = Channel.CreateUnbounded<RunEvent>();
    private readonly BlockingCollection<ReceivedInput> _inputs = [];
    private readonly CancellationTokenSource _cancel;
    private readonly List<string> _messages = [];
    private int _shown;

    private InteractiveJob(string user, CancellationToken stop)
    {
        User = user;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
    }

    public string User { get; }
    public CancellationToken Cancelled => _cancel.Token;
    public IWorkstation Workstation => this;

    /// <summary>
    /// Starts <paramref name="body"/> on its own thread. It returns why it failed, or null. The
    /// session then takes <see cref="NextAsync"/> until the program has ended.
    /// </summary>
    public static InteractiveJob Start(string user, Func<InteractiveJob, string?> body, CancellationToken stop)
    {
        var job = new InteractiveJob(user, stop);
        // Its own thread, with room for deep recursion in the program.
        var thread = new Thread(() =>
        {
            string? message = null;
            try
            {
                message = body(job);
                // Messages DSPLY left that nobody has seen: shown before the program's screen goes.
                if (job._shown < job._messages.Count) job.Exchange(job.MessagesScreen(0));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                message = $"The fake failed running the program: {e.GetType().Name}: {e.Message}";
            }
            job._events.Writer.TryWrite(new RunEvent(null, false, message));
        }, 16 * 1024 * 1024)
        {
            IsBackground = true,
            Name = "program",
        };
        thread.Start();
        return job;
    }

    /// <summary>The next thing the program wants: a screen, or (Screen null) that it has ended.</summary>
    public ValueTask<RunEvent> NextAsync(CancellationToken stop) => _events.Reader.ReadAsync(stop);

    /// <summary>What the user did on the screen the program waits on.</summary>
    public void Reply(ReceivedInput input) => _inputs.Add(input);

    /// <summary>Ends the program at its next statement or input, as when the session closes.</summary>
    public void Cancel() => _cancel.Cancel();

    public void Send(byte[] record) => _events.Writer.TryWrite(new RunEvent(record, false, null));

    public ReceivedInput Exchange(byte[] record)
    {
        _events.Writer.TryWrite(new RunEvent(record, true, null));
        return _inputs.Take(_cancel.Token);
    }

    public string? Dsply(string message, int replyLength)
    {
        _messages.Add(message);
        if (replyLength == 0) return null;
        return Exchange(MessagesScreen(replyLength)).FieldAt(ReplyRow, ReplyColumn) ?? "";
    }

    private const int ReplyRow = 21;
    private const int ReplyColumn = 19;

    /// <summary>Display Program Messages: the last of DSPLY's messages, and a reply field when one is wanted.</summary>
    private byte[] MessagesScreen(int replyLength)
    {
        _shown = _messages.Count;
        var rows = _messages.Skip(Math.Max(0, _messages.Count - 15)).Select((m, i) => Join(Sba(4 + i, 2), Text(m))).ToArray();
        return Screen5250(
            Header(24),
            Sba(1, 28),
            [Highlight],
            Text("Display Program Messages"),
            [Normal],
            Join(rows),
            replyLength > 0
                ? Join(Sba(20, 2), Text("Type reply, press Enter."), Sba(ReplyRow, 4), Text("Reply . . ."), Sba(ReplyRow, ReplyColumn - 1), Sf(0x4000, Underline, replyLength))
                : Join(Sba(21, 2), Text("Press Enter to continue.")));
    }
}
