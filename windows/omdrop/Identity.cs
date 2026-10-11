using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Omdrop;

/// <summary>
/// The receiver's TLS certificate: self-signed, RSA 2048, CN = the computer
/// name, as OpenDrop makes it. Apple devices accept a self-signed receiver in
/// Everyone mode. Kept in %LOCALAPPDATA%\omdrop so it stays the same.
/// </summary>
public static class Identity
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "omdrop");

    public static X509Certificate2 Load(string computerName)
    {
        var path = Path.Combine(Dir, "receiver.pfx");
        if (File.Exists(path))
        {
            var existing = new X509Certificate2(path, (string?)null, X509KeyStorageFlags.UserKeySet);
            if (existing.NotAfter > DateTime.Now.AddDays(7))
                return existing;
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={computerName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2));
        Directory.CreateDirectory(Dir);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        Log.Info($"made a receiver certificate: {path}");
        // Loaded back from the PFX: SChannel can't use an ephemeral key.
        return new X509Certificate2(path, (string?)null, X509KeyStorageFlags.UserKeySet);
    }
}
