using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VTalk.Core;

/// <summary>
/// Diskteki hassas verileri korur: 256 bit veri anahtarı Windows DPAPI (CurrentUser) ile sarılır,
/// içerik AES-256-GCM ile şifrelenir. Başka bir Windows kullanıcısı veya kopyalanan disk bu veriyi okuyamaz.
/// </summary>
public sealed class LocalProtector : IDisposable
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VTalk.LocalProtector.v1");
    private const byte Version = 1;
    private readonly byte[] _key;

    private LocalProtector(byte[] key) => _key = key;

    public static LocalProtector LoadOrCreate()
    {
        var file = AppPaths.LocalKeyFile;
        if (File.Exists(file))
        {
            var key = Unprotect(File.ReadAllBytes(file));
            if (key.Length != 32) throw new CryptographicException("Yerel anahtar bozuk.");
            return new LocalProtector(key);
        }
        var fresh = RandomNumberGenerator.GetBytes(32);
        WriteAtomic(file, Protect(fresh));
        return new LocalProtector(fresh);
    }

    public static byte[] Protect(byte[] data) =>
        ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(byte[] data) =>
        ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        var output = new byte[1 + 12 + plaintext.Length + 16];
        output[0] = Version;
        var nonce = output.AsSpan(1, 12);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(_key, 16);
        gcm.Encrypt(nonce, plaintext, output.AsSpan(13, plaintext.Length), output.AsSpan(13 + plaintext.Length, 16), associatedData);
        return output;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> associatedData = default)
    {
        if (blob.Length < 29 || blob[0] != Version) throw new CryptographicException("Geçersiz şifreli veri.");
        var len = blob.Length - 29;
        var plain = new byte[len];
        using var gcm = new AesGcm(_key, 16);
        gcm.Decrypt(blob.Slice(1, 12), blob.Slice(13, len), blob.Slice(13 + len, 16), plain, associatedData);
        return plain;
    }

    public byte[] EncryptString(string text, string context) =>
        Encrypt(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes(context));

    public string DecryptString(byte[] blob, string context) =>
        Encoding.UTF8.GetString(Decrypt(blob, Encoding.UTF8.GetBytes(context)));

    public static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}
