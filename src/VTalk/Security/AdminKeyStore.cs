using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VTalk.Core;

namespace VTalk.Security;

/// <summary>
/// Organizasyon (admin) imzalama anahtarının saklanması.
/// Özel anahtar, admin parolasından PBKDF2-SHA256 (600.000 tur) ile türetilen anahtarla AES-256-GCM şifrelenir.
/// Yerelde ayrıca DPAPI ile sarılır. Dışa aktarılan dosya yalnızca parola ile açılabilir.
/// </summary>
public static class AdminKeyStore
{
    public const int MinPasswordLength = 10;
    private const int Iterations = 600_000;
    private static int _failedAttempts;
    private static DateTime _lockedUntil = DateTime.MinValue;

    private sealed class KeyBlob
    {
        [JsonPropertyName("v")] public int V { get; set; } = 1;
        [JsonPropertyName("iter")] public int Iterations { get; set; }
        [JsonPropertyName("salt")] public string Salt { get; set; } = "";
        [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
        [JsonPropertyName("ct")] public string Ciphertext { get; set; } = "";
        [JsonPropertyName("tag")] public string Tag { get; set; } = "";
        [JsonPropertyName("pub")] public string PublicKey { get; set; } = "";
    }

    public static bool HasLocalKey => File.Exists(AppPaths.AdminKeyFile);

    public static string? ValidatePasswordStrength(string password)
    {
        if (password.Length < MinPasswordLength) return $"Parola en az {MinPasswordLength} karakter olmalı.";
        int classes = 0;
        if (password.Any(char.IsLower)) classes++;
        if (password.Any(char.IsUpper)) classes++;
        if (password.Any(char.IsDigit)) classes++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) classes++;
        return classes < 3 ? "Parola büyük harf, küçük harf, rakam ve sembolden en az üçünü içermeli." : null;
    }

    public static ECDsa CreateOrganizationKey(string password)
    {
        var err = ValidatePasswordStrength(password);
        if (err != null) throw new InvalidOperationException(err);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        SaveLocal(Seal(key, password));
        Log.Info("Organizasyon anahtarı oluşturuldu.");
        return key;
    }

    public static byte[]? LocalPublicKey()
    {
        try
        {
            return HasLocalKey ? Convert.FromBase64String(ReadLocal().PublicKey) : null;
        }
        catch
        {
            return null;
        }
    }

    public static ECDsa Unlock(string password)
    {
        if (DateTime.UtcNow < _lockedUntil)
            throw new UnauthorizedAccessException($"Çok fazla hatalı deneme. {(int)(_lockedUntil - DateTime.UtcNow).TotalSeconds + 1} sn bekleyin.");
        try
        {
            var key = Open(ReadLocal(), password);
            _failedAttempts = 0;
            return key;
        }
        catch (CryptographicException)
        {
            _failedAttempts++;
            if (_failedAttempts >= 3)
                _lockedUntil = DateTime.UtcNow.AddSeconds(Math.Min(300, 10 * Math.Pow(2, _failedAttempts - 3)));
            Log.Warn("Hatalı admin parolası denemesi.");
            throw new UnauthorizedAccessException("Parola hatalı.");
        }
    }

    public static void ChangePassword(ECDsa unlockedKey, string newPassword)
    {
        var err = ValidatePasswordStrength(newPassword);
        if (err != null) throw new InvalidOperationException(err);
        SaveLocal(Seal(unlockedKey, newPassword));
    }

    public static void Export(string path)
    {
        var blob = ReadLocal();
        File.WriteAllText(path, JsonSerializer.Serialize(blob, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Başka bir admin bilgisayarından dışa aktarılmış anahtarı içe alır; güvenilen kök ile eşleşmelidir.</summary>
    public static void Import(string path, string password, byte[]? expectedOrgKey)
    {
        if (new FileInfo(path).Length > 16 * 1024) throw new InvalidOperationException("Geçersiz anahtar dosyası.");
        var blob = JsonSerializer.Deserialize<KeyBlob>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Geçersiz anahtar dosyası.");
        using var key = Open(blob, password);
        var pub = key.ExportSubjectPublicKeyInfo();
        if (expectedOrgKey != null && !CryptographicOperations.FixedTimeEquals(pub, expectedOrgKey))
            throw new InvalidOperationException("Bu anahtar, bilgisayarın güvendiği organizasyona ait değil.");
        SaveLocal(blob);
    }

    private static KeyBlob Seal(ECDsa key, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var kek = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        var pub = key.ExportSubjectPublicKeyInfo();
        var ct = new byte[pkcs8.Length];
        var tag = new byte[16];
        try
        {
            using var gcm = new AesGcm(kek, 16);
            gcm.Encrypt(nonce, pkcs8, ct, tag, pub);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(pkcs8);
        }
        return new KeyBlob
        {
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(ct),
            Tag = Convert.ToBase64String(tag),
            PublicKey = Convert.ToBase64String(pub),
        };
    }

    private static ECDsa Open(KeyBlob blob, string password)
    {
        if (blob.V != 1 || blob.Iterations is < 100_000 or > 10_000_000) throw new CryptographicException("Desteklenmeyen anahtar biçimi.");
        byte[] salt, nonce, ct, tag, pub;
        try
        {
            salt = Convert.FromBase64String(blob.Salt);
            nonce = Convert.FromBase64String(blob.Nonce);
            ct = Convert.FromBase64String(blob.Ciphertext);
            tag = Convert.FromBase64String(blob.Tag);
            pub = Convert.FromBase64String(blob.PublicKey);
        }
        catch (FormatException)
        {
            throw new CryptographicException("Bozuk anahtar dosyası.");
        }
        if (nonce.Length != 12 || tag.Length != 16 || ct.Length > 4096) throw new CryptographicException("Bozuk anahtar dosyası.");

        var kek = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, blob.Iterations, HashAlgorithmName.SHA256, 32);
        var pkcs8 = new byte[ct.Length];
        try
        {
            using var gcm = new AesGcm(kek, 16);
            gcm.Decrypt(nonce, ct, tag, pkcs8, pub);
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            if (!CryptographicOperations.FixedTimeEquals(key.ExportSubjectPublicKeyInfo(), pub))
            {
                key.Dispose();
                throw new CryptographicException("Anahtar tutarsız.");
            }
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private static KeyBlob ReadLocal()
    {
        var raw = LocalProtector.Unprotect(File.ReadAllBytes(AppPaths.AdminKeyFile));
        return JsonSerializer.Deserialize<KeyBlob>(raw) ?? throw new CryptographicException("Bozuk yerel anahtar.");
    }

    private static void SaveLocal(KeyBlob blob) =>
        LocalProtector.WriteAtomic(AppPaths.AdminKeyFile, LocalProtector.Protect(JsonSerializer.SerializeToUtf8Bytes(blob)));
}
