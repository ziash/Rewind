using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rewind.OpenApi;

/// <summary>
/// The TLS certificate of Rewind's Open API server (spec §9 T3, §10). The cTrader library TM uses always encrypts and
/// accepts only certificates the system trusts, so Rewind makes, once, a small certificate authority ("Rewind local
/// CA") and with it a certificate for localhost. The authority's private key is never stored: it signs once and is gone.
/// TM trusts it through CTRADER_CA_FILE = certs/ca.pem in its secrets.env (Trader open-code-pieces v1.5), on Linux and
/// Windows alike. certs/bundle.pem (the system's authorities plus Rewind's, for SSL_CERT_FILE) is still made for an
/// older TM, which only works that way on Linux. Nothing in the system changes.
/// </summary>
public static class Certificates
{
    private const string SystemBundle = "/etc/ssl/certs/ca-certificates.crt";

    public static string ServerFile(string dir) => Path.Combine(dir, "server.pfx");
    public static string BundleFile(string dir) => Path.Combine(dir, "bundle.pem");
    public static string CaFile(string dir) => Path.Combine(dir, "ca.pem");

    /// <summary>The server certificate (with its private key), made on first use.</summary>
    public static X509Certificate2 Ensure(string dir, Action<string> log)
    {
        var pfx = ServerFile(dir);
        if (!File.Exists(pfx) || !File.Exists(BundleFile(dir)) || !File.Exists(CaFile(dir)))
            Create(dir, log);
        var cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, null);
        if (cert.NotAfter < DateTime.Now.AddDays(30))
        {
            log("Rewind's certificate expires soon; making a new one");
            Create(dir, log);
            cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, null);
        }
        return cert;
    }

    private static void Create(string dir, Action<string> log)
    {
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.UtcNow.AddDays(-1);

        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caReq = new CertificateRequest("CN=Rewind local CA (localhost only)", caKey, HashAlgorithmName.SHA256);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caReq.PublicKey, false));
        using var ca = caReq.CreateSelfSigned(now, now.AddYears(10));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server authentication
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        using var signed = req.Create(ca, now, now.AddYears(5), RandomNumberGenerator.GetBytes(16));
        using var server = signed.CopyWithPrivateKey(key);

        Write(ServerFile(dir), server.Export(X509ContentType.Pfx), private_: true);
        var caPem = ca.ExportCertificatePem() + "\n";
        Write(CaFile(dir),System.Text.Encoding.ASCII.GetBytes(caPem), private_: false);
        var system = File.Exists(SystemBundle) ? File.ReadAllText(SystemBundle).TrimEnd() + "\n" : "";
        Write(BundleFile(dir), System.Text.Encoding.ASCII.GetBytes(system + caPem), private_: false);
        log($"Made Rewind's TLS certificate for localhost in {dir}");
    }

    private static void Write(string path, byte[] bytes, bool private_)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, private_ ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(tmp, path, overwrite: true);
    }
}
