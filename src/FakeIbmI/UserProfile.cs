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

    /// <summary>The user name, upper case.</summary>
    public string User
    {
        get { lock (_gate) return _user; }
        set { lock (_gate) _user = value.ToUpperInvariant(); }
    }

    public string Password
    {
        get { lock (_gate) return _password; }
        set { lock (_gate) _password = value; }
    }

    /// <summary>When set, the right password signs on only to be told it has expired. Changing it clears this.</summary>
    public bool PasswordExpired
    {
        get { lock (_gate) return _passwordExpired; }
        set { lock (_gate) _passwordExpired = value; }
    }

    /// <summary>When set (STATUS(*DISABLED), as after too many wrong passwords), it can't sign on at all.</summary>
    public bool Disabled
    {
        get { lock (_gate) return _disabled; }
        set { lock (_gate) _disabled = value; }
    }

    internal void ChangePassword(string password)
    {
        lock (_gate)
        {
            _password = password;
            _passwordExpired = false;
        }
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
    private int _passwordLevel = 2;

    public Users(UserProfile main, params UserProfile[] others)
    {
        Main = main;
        All = [main, .. others];
        if (All.Select(p => p.User).Distinct().Count() != All.Count)
            throw new ArgumentException("each user needs a different name");
    }

    /// <summary>The user that signs on normally.</summary>
    public UserProfile Main { get; }

    public IReadOnlyList<UserProfile> All { get; }

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
        return All.FirstOrDefault(p => p.User == name);
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
