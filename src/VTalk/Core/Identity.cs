using System.IO;
using System.Security.Cryptography;

namespace VTalk.Core;

/// <summary>
/// Bu bilgisayar/kullanıcı için kalıcı ECDSA P-256 kimlik anahtarı.
/// PeerId açık anahtarın özetidir; bu yüzden kimlik taklit edilemez.
/// </summary>
public sealed class Identity : IDisposable
{
    public ECDsa Key { get; }
    public byte[] PublicKey { get; }
    public string PeerId { get; }
    public string Fingerprint { get; }

    private Identity(ECDsa key)
    {
        Key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        PeerId = CryptoUtil.PeerIdFromPublicKey(PublicKey);
        Fingerprint = CryptoUtil.Fingerprint(PublicKey);
    }

    /// <summary>Kalıcı olmayan kimlik (testler ve araçlar için).</summary>
    public static Identity CreateEphemeral() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static Identity LoadOrCreate()
    {
        var file = AppPaths.IdentityFile;
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(file))
        {
            var pkcs8 = LocalProtector.Unprotect(File.ReadAllBytes(file));
            try
            {
                key.ImportPkcs8PrivateKey(pkcs8, out _);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }
            return new Identity(key);
        }

        var export = key.ExportPkcs8PrivateKey();
        try
        {
            LocalProtector.WriteAtomic(file, LocalProtector.Protect(export));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(export);
        }
        Log.Info("Yeni kimlik anahtarı oluşturuldu.");
        return new Identity(key);
    }

    public byte[] Sign(ReadOnlySpan<byte> data) => Key.SignData(data, HashAlgorithmName.SHA256);

    public void Dispose() => Key.Dispose();
}
