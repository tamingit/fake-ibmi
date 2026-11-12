using System.Text.RegularExpressions;

namespace FakeIbmI;

/// <summary>One user profile: its name, password, and whether the password has expired or the profile is disabled.</summary>
public sealed class UserProfile(string user, string password, bool passwordExpired = false, bool disabled = false)
{
    private readonly object _gate = new();
    private string _user = user.ToUpperInvariant();
    private string _password = password;
    private bool _passwordExpired = passwordExpired;
    private bool _disabled = disabled;

    /// <summary>The library this user's names are looked for in first, and new source goes to (QGPL unless set).</summary>
    public string DefaultLibrary { get; init; } = "QGPL";

    /// <summary>Called after a password or flag changes, to keep it (the database's users do).</summary>
    internal Action<UserProfile>? Changed { get; set; }

    /// <summary>The user name, upper case.</summary>
    public string User
    {
        get { lock (_gate) return _user; }
        set { lock (_gate) _user = value.ToUpperInvariant(); }
    }

    public string Password
    {
        get { lock (_gate) return _password; }
        set
        {
            lock (_gate) _password = value;
            Changed?.Invoke(this);
        }
    }

    /// <summary>When set, the right password signs on only to be told it has expired. Changing it clears this.</summary>
    public bool PasswordExpired
    {
        get { lock (_gate) return _passwordExpired; }
        set
        {
            lock (_gate) _passwordExpired = value;
            Changed?.Invoke(this);
        }
    }

    /// <summary>When set (STATUS(*DISABLED), as after too many wrong passwords), it can't sign on at all.</summary>
    public bool Disabled
    {
        get { lock (_gate) return _disabled; }
        set
        {
            lock (_gate) _disabled = value;
            Changed?.Invoke(this);
        }
    }

    internal void ChangePassword(string password)
    {
        lock (_gate)
        {
            _password = password;
            _passwordExpired = false;
        }
        Changed?.Invoke(this);
    }

    public override string ToString() =>
        User + (Disabled ? " (disabled)" : PasswordExpired ? " (password expired)" : "");
}

/// <summary>How a sign-on with a user and password turned out.</summary>
public enum SignOnResult
{
    SignedOn,
    UnknownUser,
    Disabled,
    WrongPassword,

    /// <summary>The password is right, but has to be changed before it signs on.</summary>
    PasswordExpired,
}

/// <summary>
/// The system's user profiles and password level (QPWDLVL), which every server checks sign-ons
/// against: the 5250 sign-on screen, the sign-on server and Network Print. A password changed
/// through one holds for the others, until the fake stops.
/// </summary>
public sealed class Users
{
    public const string DefaultUser = "PRTUSER";
    public const string DefaultPassword = "Secret12";
    public const string DefaultExpiredUser = "EXPUSER";
    public const string DefaultExpiredPassword = "Expired1";
    public const string DefaultLockedUser = "LOCKUSER";
    public const string DefaultLockedPassword = "Locked12";

    /// <summary>The environment variables that set the users, as .env.example lists them.</summary>
    public static readonly string[] Variables =
    [
        "FAKE_IBMI_USER", "FAKE_IBMI_PASSWORD",
        "FAKE_IBMI_EXPIRED_USER", "FAKE_IBMI_EXPIRED_PASSWORD",
        "FAKE_IBMI_LOCKED_USER", "FAKE_IBMI_LOCKED_PASSWORD",
    ];

    private static readonly Regex UserName = new("^[A-Z#$@][A-Z0-9#$@_]{0,9}$");

    private readonly object _gate = new();
    private readonly Store? _store;
    private readonly UserProfile _main;
    private readonly IReadOnlyList<UserProfile> _fixed = [];
    private int _passwordLevel = 2;

    public Users(UserProfile main, params UserProfile[] others)
    {
        _main = main;
        _fixed = [main, .. others];
        if (_fixed.Select(p => p.User).Distinct().Count() != _fixed.Count)
            throw new ArgumentException("each user needs a different name");
    }

    private Users(Store store, UserProfile main)
    {
        _store = store;
        _main = main;
    }

    /// <summary>The user that signs on normally. For users in the database, it's the one the settings name, as the database has it now.</summary>
    public UserProfile Main => _store is null ? _main : Find(_main.User) ?? _main;

    /// <summary>Every user. The database's are read when asked: users can be added, changed and removed while the fake runs.</summary>
    public IReadOnlyList<UserProfile> All => _store is null ? _fixed : [.. _store.UserRows().Select(Profile)];

    /// <summary>
    /// The users, kept in the database's table QFAKE_USER, where the user maintenance program (or any SQL)
    /// can add, change and remove them. The three the settings name are put in the table at each start,
    /// as the settings give them; every other user in it is kept as it is.
    /// </summary>
    public static Users Open(Store store, IReadOnlyDictionary<string, string> settings)
    {
        var defaults = FromSettings(settings);
        foreach (var profile in defaults._fixed)
        {
            var text = profile == defaults._main ? "Signs on normally" : profile.Disabled ? "Disabled" : "Password expired";
            var existing = store.FindUser(profile.User);
            store.SaveUser(new UserRow(profile.User, profile.Password, profile.Disabled ? "*DISABLED" : "*ENABLED",
                profile.PasswordExpired ? "*YES" : "*NO", existing?.Text is { Length: > 0 } kept ? kept : text,
                existing?.DefaultLibrary ?? Store.Current));
        }
        return new Users(store, defaults._main);
    }

    private UserProfile Profile(UserRow row) =>
        new(row.User, row.Password, row.PasswordExpired == "*YES", row.Status == "*DISABLED")
        {
            DefaultLibrary = row.DefaultLibrary,
            Changed = p => _store!.UpdateUser(p.User, p.Password, p.Disabled, p.PasswordExpired),
        };

    /// <summary>QPWDLVL: 0 or 1 for DES substitutes (passwords up to 10 characters, any case), 2 or 3 for SHA-1.</summary>
    public int PasswordLevel
    {
        get { lock (_gate) return _passwordLevel; }
        set { lock (_gate) _passwordLevel = value; }
    }

    /// <summary>The default users: PRTUSER / Secret12, EXPUSER / Expired1 (expired) and LOCKUSER / Locked12 (disabled).</summary>
    public static Users Defaults() => FromSettings(new Dictionary<string, string>());

    /// <summary>
    /// The users from the FAKE_IBMI_* settings (see <see cref="Variables"/>), each falling back to
    /// its default. A user name is 1 to 10 characters: letters, digits, #, $, @ and _, not starting
    /// with a digit or _.
    /// </summary>
    public static Users FromSettings(IReadOnlyDictionary<string, string> settings)
    {
        string Get(string name, string fallback) =>
            settings.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
        UserProfile Profile(string prefix, string user, string password, bool expired = false, bool disabled = false)
        {
            var name = Get($"FAKE_IBMI_{prefix}USER", user).ToUpperInvariant();
            if (!UserName.IsMatch(name)) throw new ArgumentException($"FAKE_IBMI_{prefix}USER {name} is not an IBM i user name");
            return new UserProfile(name, Get($"FAKE_IBMI_{prefix}PASSWORD", password), expired, disabled);
        }
        return new Users(
            Profile("", DefaultUser, DefaultPassword),
            Profile("EXPIRED_", DefaultExpiredUser, DefaultExpiredPassword, expired: true),
            Profile("LOCKED_", DefaultLockedUser, DefaultLockedPassword, disabled: true));
    }

    public UserProfile? Find(string user)
    {
        var name = user.Trim().ToUpperInvariant();
        if (_store is not null) return _store.FindUser(name) is { } row ? Profile(row) : null;
        return _fixed.FirstOrDefault(p => p.User == name);
    }

    /// <summary>
    /// A sign-on with a clear password, as the 5250 sign-on screen takes it: case-sensitive at
    /// password levels 2 and 3, not at 0 and 1. A disabled profile can't sign on whatever the password.
    /// </summary>
    public (SignOnResult Result, UserProfile? Profile) Check(string user, string password)
    {
        var profile = Find(user);
        if (profile is null) return (SignOnResult.UnknownUser, null);
        if (profile.Disabled) return (SignOnResult.Disabled, profile);
        if (!PasswordMatches(profile, password)) return (SignOnResult.WrongPassword, profile);
        return (profile.PasswordExpired ? SignOnResult.PasswordExpired : SignOnResult.SignedOn, profile);
    }

    public bool PasswordMatches(UserProfile profile, string password) =>
        PasswordLevel >= 2
            ? password == profile.Password
            : password.Equals(profile.Password, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a new password won't do, or null when it will.</summary>
    public string? NewPasswordProblem(UserProfile profile, string newPassword)
    {
        if (newPassword.Length == 0) return "New password required.";
        if (newPassword.Length > (PasswordLevel >= 2 ? 128 : 10)) return "New password is too long.";
        if (PasswordMatches(profile, newPassword)) return "New password must be different from the current password.";
        return null;
    }
}
