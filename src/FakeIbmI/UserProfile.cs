namespace FakeIbmI;

/// <summary>
/// The one user profile the host servers sign on. The servers share it, as an IBM i's servers
/// share its profiles: a password changed through the sign-on server holds for Network Print too.
/// </summary>
public sealed class UserProfile
{
    public const string DefaultUser = "PRTUSER";
    public const string DefaultPassword = "Secret12";

    private readonly object _gate = new();
    private string _user = DefaultUser;
    private string _password = DefaultPassword;
    private int _passwordLevel = 2;
    private bool _passwordExpired;

    /// <summary>The user that may sign on, upper case.</summary>
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

    /// <summary>QPWDLVL: 0 or 1 for DES substitutes, 2 or 3 for SHA-1.</summary>
    public int PasswordLevel
    {
        get { lock (_gate) return _passwordLevel; }
        set { lock (_gate) _passwordLevel = value; }
    }

    /// <summary>When set, a correct password signs on only to be told it has expired. Changing it clears this.</summary>
    public bool PasswordExpired
    {
        get { lock (_gate) return _passwordExpired; }
        set { lock (_gate) _passwordExpired = value; }
    }

    internal void ChangePassword(string password)
    {
        lock (_gate)
        {
            _password = password;
            _passwordExpired = false;
        }
    }
}
