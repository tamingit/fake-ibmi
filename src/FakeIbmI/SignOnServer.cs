// A scripted fake of the IBM i sign-on host server (as-signon, server ID E009, port 8476), laid
// out as JTOpen's client reads and writes it:
//   7003 exchange attributes -> F003: version, level, server seed, password level, job name
//   7004 sign-on info        -> F004: return code; on success, sign-on dates and the server CCSID
//   7005 change password     -> F005: return code
//   7006 end job             -> closes, no reply
// It checks passwords against the system's Users with the DES (QPWDLVL 0-1) or SHA-1 (2-3)
// substitute (see Passwords). Tokens and Kerberos aren't supported. It never logs a password,
// a substitute or a protected password.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace FakeIbmI;

public sealed class SignOnServer : HostServer
{
    /// <summary>IBM's port for it.</summary>
    public const int DefaultPort = 8476;

    private const ushort ServerId = 0xE009;

    /// <summary>VRM 7.5.0.</summary>
    private const int ServerVersion = 0x00070500;

    /// <summary>The data stream level it claims. 5 and up asks clients to send 0x1128 (return messages).</summary>
    private const ushort ServerLevel = 10;

    private const int Ccsid = 37;

    // Return codes, as JTOpen names them.
    private const int RequestIdNotValid = 0x00010003;
    private const int RequestDataNotValid = 0x00010009;
    private const int UserIdUnknown = 0x00020001;
    private const int UserIdDisabled = 0x00020002;
    private const int NewPasswordTooLong = 0x00030001;
    private const int NewPasswordTooShort = 0x00030002;
    private const int NewPasswordPreviouslyUsed = 0x00030006;
    private const int PasswordIncorrect = 0x0003000B;
    private const int PasswordExpired = 0x0003000D;

    private int _signOns;
    private int _refusals;
    private int _passwordChanges;

    private SignOnServer(int port, Action<string>? log, IPAddress? address, Users? users, TlsOptions? tls)
        : base(port, log, address, tls)
    {
        Users = users ?? Users.Defaults();
    }

    /// <summary>
    /// Listens on the loopback interface, or on <paramref name="address"/>. Port 0 picks a free port
    /// (see <see cref="HostServer.Port"/>). Pass the other servers' <paramref name="users"/> to share
    /// them (the default is <see cref="Users.Defaults"/>). With <paramref name="tls"/>, it also listens for TLS.
    /// </summary>
    public static Task<SignOnServer> StartAsync(int port = 0, Action<string>? log = null, IPAddress? address = null,
        Users? users = null, TlsOptions? tls = null)
    {
        var server = new SignOnServer(port, log, address, users, tls);
        server.Start();
        return Task.FromResult(server);
    }

    /// <summary>The user profiles it signs on, and changes the passwords of.</summary>
    public Users Users { get; }

    /// <summary>Makes each connection's server seed. Random unless a test fixes it.</summary>
    public Func<byte[]> ServerSeeds { get; set; } = () => RandomNumberGenerator.GetBytes(8);

    /// <summary>When set, sign-on info and change password answer with this return code whatever is sent.</summary>
    public int? Refuse { get; set; }

    /// <summary>Successful sign-ons so far.</summary>
    public int SignOns => Volatile.Read(ref _signOns);

    /// <summary>Refused sign-ons and password changes so far.</summary>
    public int Refusals => Volatile.Read(ref _refusals);

    /// <summary>Successful password changes so far.</summary>
    public int PasswordChanges => Volatile.Read(ref _passwordChanges);

    protected override string LogName => "signon";

    protected override async Task ServeAsync(Stream stream, int id, bool tls)
    {
        var exchange = await ReadFrameAsync(stream, Stopping);
        if (ServerIdOf(exchange) != ServerId || RequestIdOf(exchange) != 0x7003)
            throw new InvalidDataException("expected exchange attributes");
        // A client signing on with a password sends its seed; one using a token doesn't.
        var clientSeed = CodePoints(exchange).GetValueOrDefault(0x1103) ?? new byte[8];
        var serverSeed = ServerSeeds();
        var level = Users.PasswordLevel;
        await stream.WriteAsync(Frame(ServerId, 0xF003, BigEndian(0), CorrelationOf(exchange), 0,
            (0x1101, BigEndian(ServerVersion)),
            (0x1102, [ServerLevel >> 8, ServerLevel & 0xFF]),
            (0x1103, serverSeed),
            (0x1119, [(byte)level]),
            (0x111F, [.. BigEndian(Ccsid), .. Ebcdic.Encode($"{100000 + id % 900000}/QUSER/QZSOSIGN")])), Stopping);

        var session = new Session(id, clientSeed, serverSeed, level);
        while (true)
        {
            var request = await ReadFrameAsync(stream, Stopping);
            if (ServerIdOf(request) != ServerId) throw new InvalidDataException("not a sign-on request");
            var correlation = CorrelationOf(request);
            switch (RequestIdOf(request))
            {
                case 0x7004:
                {
                    var (rc, user) = SignOn(request, session);
                    await stream.WriteAsync(rc == 0
                        ? Frame(ServerId, 0xF004, BigEndian(0), correlation, 0,
                            (0x1106, Date(DateTime.Now)),
                            (0x1107, Date(session.LastSignOn ?? DateTime.Now)),
                            (0x1114, BigEndian(Ccsid)),
                            (0x1104, [.. BigEndian(Ccsid), .. Ebcdic.Encode(user.PadRight(10))]))
                        : Frame(ServerId, 0xF004, BigEndian(rc), correlation), Stopping);
                    if (rc == 0) session.LastSignOn = DateTime.Now;
                    break;
                }
                case 0x7005:
                    await stream.WriteAsync(Frame(ServerId, 0xF005, BigEndian(ChangePassword(request, session)), correlation), Stopping);
                    break;
                case 0x7006:
                    return;
                default:
                    await stream.WriteAsync(Frame(ServerId, (ushort)(RequestIdOf(request) | 0x8000), BigEndian(RequestIdNotValid), correlation), Stopping);
                    break;
            }
        }
    }

    /// <summary>One connection's seeds and password level, fixed by the attribute exchange.</summary>
    private sealed record Session(int Id, byte[] ClientSeed, byte[] ServerSeed, int Level)
    {
        public DateTime? LastSignOn { get; set; }
    }

    /// <summary>7004: template is the scheme byte; then 0x1113 CCSID, 0x1105 substitute, 0x1104 user, 0x1128.</summary>
    private (int Rc, string User) SignOn(byte[] request, Session session)
    {
        var points = CodePoints(request);
        var user = points.TryGetValue(0x1104, out var userId) ? Ebcdic.Decode(userId).TrimEnd() : "";
        var (rc, profile) = Check(request, points, session, user);
        if (rc == 0 && profile!.PasswordExpired) rc = PasswordExpired;
        if (rc != 0)
        {
            Interlocked.Increment(ref _refusals);
            Log($"signon #{session.Id} sign-on refused ({rc:X8})");
            return (rc, user);
        }
        Interlocked.Increment(ref _signOns);
        Log($"signon #{session.Id} signed on as {user}");
        return (0, user);
    }

    /// <summary>
    /// 7005: the scheme byte; then 0x1104 user, 0x1105 substitute (old password), 0x110C old password
    /// protected under the new one's token, 0x110D new password protected under the old one's; for
    /// SHA-1, 0x111C and 0x111D their lengths in bytes and 0x111E their CCSID (13488).
    /// </summary>
    private int ChangePassword(byte[] request, Session session)
    {
        var points = CodePoints(request);
        var user = points.TryGetValue(0x1104, out var userId) ? Ebcdic.Decode(userId).TrimEnd() : "";
        string? newPassword = null;
        var (rc, profile) = Check(request, points, session, user);
        if (rc == 0) rc = NewPassword(points, session, profile!, out newPassword);
        if (rc != 0 || newPassword is null)
        {
            Interlocked.Increment(ref _refusals);
            Log($"signon #{session.Id} password change refused ({rc:X8})");
            return rc;
        }
        profile!.ChangePassword(newPassword);
        Interlocked.Increment(ref _passwordChanges);
        Log($"signon #{session.Id} password changed for {user}");
        return 0;
    }

    /// <summary>
    /// Whether the request's user and substitute match a profile: 0 with it, or the return code. A
    /// disabled profile is refused whatever the password, as on an IBM i.
    /// </summary>
    private (int Rc, UserProfile? Profile) Check(byte[] request, Dictionary<int, byte[]> points, Session session, string user)
    {
        if (Refuse is { } refuse) return (refuse, null);
        if (Users.Find(user) is not { } profile) return (UserIdUnknown, null);
        if (profile.Disabled) return (UserIdDisabled, profile);
        if (request.Length < 21 || request[20] != (session.Level < 2 ? 0x01 : 0x03)) return (RequestDataNotValid, profile);
        if (!points.TryGetValue(0x1105, out var substitute)) return (RequestDataNotValid, profile);
        var token = Passwords.Token(session.Level, profile.User, profile.Password);
        var expected = Passwords.Substitute(session.Level, token, profile.User, session.ClientSeed, session.ServerSeed, 1);
        return (CryptographicOperations.FixedTimeEquals(substitute, expected) ? 0 : PasswordIncorrect, profile);
    }

    /// <summary>The new password from a change whose substitute checked out: 0 with it, or the return code.</summary>
    private int NewPassword(Dictionary<int, byte[]> points, Session session, UserProfile profile, out string? newPassword)
    {
        newPassword = null;
        var user = profile.User;
        var level = session.Level;
        if (!points.TryGetValue(0x110C, out var oldProtected) || !points.TryGetValue(0x110D, out var newProtected))
            return RequestDataNotValid;
        var oldPassword = profile.Password;
        int oldLength, newLength;
        if (level >= 2)
        {
            if (!points.TryGetValue(0x111C, out var oldLen) || !points.TryGetValue(0x111D, out var newLen)
                || oldLen.Length != 4 || newLen.Length != 4)
                return RequestDataNotValid;
            (oldLength, newLength) = (BinaryPrimitives.ReadInt32BigEndian(oldLen), BinaryPrimitives.ReadInt32BigEndian(newLen));
        }
        else (oldLength, newLength) = (oldProtected.Length, newProtected.Length);

        // The client protects the new password first (sequence 2 on), then the old, carrying on the sequence.
        ulong sequence = 1;
        var oldToken = Passwords.Token(level, user, oldPassword);
        var candidate = Passwords.Password(level,
            Passwords.Protect(level, oldToken, user, session.ClientSeed, session.ServerSeed, ref sequence, newProtected), newLength);
        if (candidate is null || newProtected.Length % (level >= 2 ? 20 : 8) != 0) return RequestDataNotValid;
        var newToken = Passwords.Token(level, user, candidate);
        var oldBytes = Passwords.PasswordBytes(level, oldPassword);
        var oldSent = Passwords.Protect(level, newToken, user, session.ClientSeed, session.ServerSeed, ref sequence, oldProtected);
        if (oldLength != oldBytes.Length || !oldSent.AsSpan(0, Math.Min(oldLength, oldSent.Length)).SequenceEqual(oldBytes))
            return PasswordIncorrect;

        if (candidate.Length == 0) return NewPasswordTooShort;
        if (candidate.Length > (level >= 2 ? 128 : 10)) return NewPasswordTooLong;
        if (level >= 2 ? candidate == oldPassword : candidate.Equals(oldPassword, StringComparison.OrdinalIgnoreCase))
            return NewPasswordPreviouslyUsed;
        newPassword = candidate;
        return 0;
    }

    /// <summary>A date code point: year (2 bytes), month, day, hour, minute, second, as JTOpen reads it.</summary>
    private static byte[] Date(DateTime t) =>
        [(byte)(t.Year >> 8), (byte)t.Year, (byte)t.Month, (byte)t.Day, (byte)t.Hour, (byte)t.Minute, (byte)t.Second];
}
