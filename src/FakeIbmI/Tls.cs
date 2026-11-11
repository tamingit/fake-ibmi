// TLS for the fake's servers, as an IBM i offers it: the same server on a second port (992 for
// Telnet, 9476 for sign-on, 9474 for Network Print), with a server certificate and no client
// certificate. The port mapper stays in the clear on 449, as on an IBM i.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FakeIbmI;

/// <summary>A TLS listener for a server: its port (0 picks a free one) and certificate.</summary>
public sealed record TlsOptions(int Port, X509Certificate2 Certificate);

public static class Tls
{
    private static readonly TimeSpan HandshakeLimit = TimeSpan.FromSeconds(10);

    /// <summary>The subject of the certificates the fake makes, so it knows them again.</summary>
    public const string Subject = "CN=Fake IBM i, O=fake-ibmi";

    /// <summary>
    /// The certificate in <paramref name="path"/> (PKCS #12, no password). When there's no such
    /// file it makes a self-signed one and saves it there, with its public part beside it as
    /// PEM (the same name with .crt) for clients to trust. Without a path the certificate lasts
    /// only as long as the process.
    /// </summary>
    public static X509Certificate2 LoadOrCreate(string? path, IEnumerable<string> names, Action<string>? log = null)
    {
        if (path is not null && File.Exists(path))
        {
            var loaded = X509CertificateLoader.LoadPkcs12FromFile(path, null);
            if (!loaded.HasPrivateKey) throw new ArgumentException($"{path} has no private key");
            log?.Invoke($"TLS certificate from {path}: {Describe(loaded)}");
            if (loaded.NotAfter < DateTime.Now)
                log?.Invoke($"TLS certificate in {path} has expired; delete it to make a new one");
            return loaded;
        }

        var made = CreateSelfSigned(names);
        if (path is null)
        {
            log?.Invoke($"TLS certificate made for this run only (pass --tls-cert to keep one): {Describe(made)}");
            return made;
        }
        File.WriteAllBytes(path, made.Export(X509ContentType.Pkcs12));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var crt = Path.ChangeExtension(path, ".crt");
        File.WriteAllText(crt, made.ExportCertificatePem() + "\n");
        log?.Invoke($"TLS certificate made and saved in {path}; trust {crt}: {Describe(made)}");
        // Loaded back from the file, so it's the same as later runs get.
        return X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }

    /// <summary>A self-signed server certificate for these host names and addresses, good for 397 days.</summary>
    public static X509Certificate2 CreateSelfSigned(IEnumerable<string> names)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var alternatives = new SubjectAlternativeNameBuilder();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IPAddress.TryParse(name, out var address)) alternatives.AddIpAddress(address);
            else alternatives.AddDnsName(name);
        }
        request.CertificateExtensions.Add(alternatives.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(397));
    }

    /// <summary>
    /// The names a certificate the fake makes is for: localhost, the loopback addresses, this
    /// machine's name, the address it listens on (unless every one) and any given.
    /// </summary>
    public static List<string> Names(IPAddress bind, IEnumerable<string> extra)
    {
        List<string> names = ["localhost", "127.0.0.1", "::1", Dns.GetHostName()];
        if (!bind.Equals(IPAddress.Any) && !bind.Equals(IPAddress.IPv6Any)) names.Add(bind.ToString());
        names.AddRange(extra);
        return names;
    }

    /// <summary>The server side of the handshake, or null (logged) when it fails.</summary>
    internal static async Task<SslStream?> AcceptAsync(NetworkStream stream, X509Certificate2 certificate,
        string label, Action<string> log, CancellationToken cancel)
    {
        var tls = new SslStream(stream, leaveInnerStreamOpen: false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(HandshakeLimit);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.None, // the system's choice: TLS 1.2 and 1.3
            }, limit.Token);
            log($"{label} TLS {Protocol(tls.SslProtocol)}");
            return tls;
        }
        catch (Exception e) when (e is AuthenticationException or IOException or OperationCanceledException or SocketException)
        {
            // The usual reason is a client that doesn't trust the certificate, or one that isn't speaking TLS.
            var reason = e is OperationCanceledException ? "timed out" : Innermost(e).Message;
            var untrusted = reason.Contains("certificate unknown") || reason.Contains("unknown ca") || reason.Contains("bad certificate");
            log($"{label} TLS handshake failed: {reason}{(untrusted ? " (the client doesn't trust the certificate)" : "")}");
            await tls.DisposeAsync();
            return null;
        }
    }

    /// <summary>The innermost error, which names the TLS alert (such as "certificate unknown").</summary>
    private static Exception Innermost(Exception e)
    {
        while (e.InnerException is { } inner) e = inner;
        return e;
    }

    private static string Describe(X509Certificate2 certificate) =>
        $"{certificate.Subject}, SHA-256 {certificate.GetCertHashString(HashAlgorithmName.SHA256)}, until {certificate.NotAfter:yyyy-MM-dd}";

#pragma warning disable SYSLIB0039 // naming old protocols, not enabling them
    private static string Protocol(SslProtocols protocol) => protocol switch
    {
        SslProtocols.Tls13 => "1.3",
        SslProtocols.Tls12 => "1.2",
        SslProtocols.Tls11 => "1.1",
        SslProtocols.Tls => "1.0",
        _ => protocol.ToString(),
    };
#pragma warning restore SYSLIB0039
}
