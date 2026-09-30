using System.Security.Cryptography;
using System.Text;

namespace VTalk.Core;

public static class CryptoUtil
{
    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>Açık anahtardan (SPKI) türetilen, kendi kendini doğrulayan kimlik (128 bit, hex).</summary>
    public static string PeerIdFromPublicKey(ReadOnlySpan<byte> spki) =>
        Convert.ToHexString(SHA256.HashData(spki)[..16]).ToLowerInvariant();

    /// <summary>İnsan tarafından karşılaştırılabilir parmak izi: "AB12-CD34-...".</summary>
    public static string Fingerprint(ReadOnlySpan<byte> spki)
    {
        var hex = Convert.ToHexString(SHA256.HashData(spki)[..12]);
        var sb = new StringBuilder();
        for (int i = 0; i < hex.Length; i += 4)
        {
            if (sb.Length > 0) sb.Append('-');
            sb.Append(hex, i, 4);
        }
        return sb.ToString();
    }

    /// <summary>Yalnızca P-256 eğrisindeki geçerli açık anahtarları kabul eder.</summary>
    public static ECDsa? ImportP256PublicKey(ReadOnlySpan<byte> spki)
    {
        if (spki.Length is < 50 or > 200) return null;
        ECDsa? key = null;
        try
        {
            key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out int read);
            if (read != spki.Length) throw new CryptographicException("Fazladan veri");
            var p = key.ExportParameters(false);
            if (p.Curve.Oid?.Value != P256Oid && p.Curve.Oid?.FriendlyName is not ("nistP256" or "ECDSA_P256"))
                throw new CryptographicException("Desteklenmeyen eğri");
            return key;
        }
        catch
        {
            key?.Dispose();
            return null;
        }
    }

    public static bool Verify(ReadOnlySpan<byte> spki, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64) return false;
        using var key = ImportP256PublicKey(spki);
        if (key == null) return false;
        try
        {
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    public static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
