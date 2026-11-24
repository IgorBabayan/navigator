using System.Security.Cryptography;

namespace Navigator.Updater;

/// <summary>
/// ECDSA P-256 / SHA-256 signatures over the exact bytes of update-manifest.json.
/// The private key exists only as a GitHub Actions secret; the public key is compiled into release builds.
/// </summary>
public static class ManifestSignature
{
    public static string Sign(ReadOnlySpan<byte> manifest, string privateKeyPkcs8Base64)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out _);
        var signature = key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Convert.ToBase64String(signature);
    }

    public static bool Verify(ReadOnlySpan<byte> manifest, string signatureBase64, string publicKeySpkiBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            return key.VerifyData(manifest, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Creates a new key pair: (PKCS#8 private key, SubjectPublicKeyInfo public key), both base64.</summary>
    public static (string PrivateKey, string PublicKey) CreateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportPkcs8PrivateKey()), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }
}
