using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VTalk.Core;

namespace VTalk.Security;

/// <summary>
/// Organizasyon güven kökü ve imzalı rol dizinini yönetir.
/// Güven kökü öncelik sırası:
///   1) BT tarafından exe yanına veya ProgramData\VTalk'a konmuş org.pub (değiştirilemez, en güvenli)
///   2) İlk görülen geçerli dizinin anahtarı (TOFU) — sonrasında sabitlenir, farklı anahtar kabul edilmez.
/// </summary>
public sealed class DirectoryService
{
    /// <summary>Base64 + JSON kodlamasından sonra tek şifreli çerçeveye (1 MiB) sığmalı.</summary>
    public const int MaxPayloadBytes = 600 * 1024;
    private readonly object _gate = new();

    public byte[]? PinnedOrgKey { get; private set; }
    public bool IsDeployedTrustRoot { get; private set; }
    public string? OrgFingerprint => PinnedOrgKey == null ? null : CryptoUtil.Fingerprint(PinnedOrgKey);
    private OrgDirectory? _current;
    private IReadOnlyDictionary<string, OrgMember> _membersById = new Dictionary<string, OrgMember>();

    /// <summary>Uygulanan dizin. Üye listesi yerinde değiştirilmez (yeni dizin kopyayla imzalanır); indeks atamada kurulur.</summary>
    public OrgDirectory? Current
    {
        get => _current;
        private set
        {
            _membersById = BuildMemberIndex(value);
            _current = value;
        }
    }
    public SignedDirectory? CurrentSigned { get; private set; }
    public int Version => Current?.Version ?? 0;
    public bool MembersOnly => Current?.MembersOnly == true;
    /// <summary>Ağda farklı bir organizasyon anahtarı görüldü (olası sahte admin).</summary>
    public bool ConflictDetected { get; private set; }

    public event Action? Changed;

    /// <summary>Parola unutulunca yeni anahtar kurulana kadar ağdaki dizin bu bilgisayara sabitlenmesin.</summary>
    private bool _acceptNewOrganization;
    private static string NewOrgMarker => Path.Combine(AppPaths.DataDir, "new-org.pending");

    public void Load()
    {
        foreach (var candidate in AppPaths.DeployedOrgKeyCandidates())
        {
            if (!File.Exists(candidate)) continue;
            if (!FileTrust.IsProtectedFromOtherUsers(candidate, out var reason))
            {
                Log.Warn($"Dağıtılmış organizasyon anahtarı yok sayıldı ({candidate}): {reason}");
                continue;
            }
            var key = TryReadPublicKeyFile(candidate);
            if (key != null)
            {
                PinnedOrgKey = key;
                IsDeployedTrustRoot = true;
                Log.Info("Dağıtılmış organizasyon anahtarı kullanılıyor: " + candidate);
                break;
            }
        }
        if (PinnedOrgKey == null && File.Exists(NewOrgMarker))
        {
            _acceptNewOrganization = true;
            Log.Info("Yeni organizasyon kurulması bekleniyor; ağdaki dizin kabul edilmeyecek.");
        }
        else if (PinnedOrgKey == null)
        {
            PinnedOrgKey = TryReadPublicKeyFile(AppPaths.PinnedOrgKeyFile);
        }

        try
        {
            if (PinnedOrgKey != null && File.Exists(AppPaths.DirectoryFile))
            {
                var signed = JsonSerializer.Deserialize<SignedDirectory>(File.ReadAllText(AppPaths.DirectoryFile));
                string? err = "Boş dosya.";
                if (signed != null && Verify(signed, out var dir, out err) && dir != null)
                {
                    Current = dir;
                    CurrentSigned = signed;
                }
                else
                {
                    Log.Warn("Kayıtlı dizin doğrulanamadı: " + err);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Dizin okunamadı: " + ex.Message);
        }
    }

    public static byte[]? TryReadPublicKeyFile(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            var text = File.ReadAllText(path).Trim();
            byte[] spki;
            if (text.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
            {
                using var tmp = ECDsa.Create();
                tmp.ImportFromPem(text);
                spki = tmp.ExportSubjectPublicKeyInfo();
            }
            else
            {
                spki = Convert.FromBase64String(text);
            }
            using var key = CryptoUtil.ImportP256PublicKey(spki);
            return key == null ? null : spki;
        }
        catch (Exception ex)
        {
            Log.Warn($"Organizasyon anahtarı okunamadı ({path}): {ex.Message}");
            return null;
        }
    }

    public static string ToPem(byte[] spki)
    {
        using var key = CryptoUtil.ImportP256PublicKey(spki) ?? throw new CryptographicException("Geçersiz anahtar.");
        return key.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>İmzayı ve içeriği doğrular. Sabitlenmiş anahtar yoksa yalnızca imzanın kendi içinde tutarlı olduğunu denetler.</summary>
    private bool Verify(SignedDirectory signed, out OrgDirectory? dir, out string? error)
    {
        dir = null;
        byte[] payload, sig, orgKey;
        try
        {
            payload = Convert.FromBase64String(signed.Payload);
            sig = Convert.FromBase64String(signed.Signature);
        }
        catch (FormatException)
        {
            error = "Bozuk kodlama.";
            return false;
        }
        if (payload.Length > MaxPayloadBytes)
        {
            error = "Dizin çok büyük.";
            return false;
        }
        try
        {
            dir = JsonSerializer.Deserialize<OrgDirectory>(payload);
            orgKey = Convert.FromBase64String(dir?.OrgKey ?? "");
        }
        catch (Exception)
        {
            error = "Bozuk dizin.";
            return false;
        }
        if (dir == null)
        {
            error = "Boş dizin.";
            return false;
        }
        if (PinnedOrgKey != null && !CryptographicOperations.FixedTimeEquals(orgKey, PinnedOrgKey))
        {
            error = "Dizin, güvenilen organizasyon anahtarıyla imzalanmamış.";
            return false;
        }
        if (!CryptoUtil.Verify(orgKey, payload, sig))
        {
            error = "İmza geçersiz.";
            return false;
        }
        error = dir.Validate();
        return error == null;
    }

    /// <summary>Ağdan gelen dizini uygular. Yalnızca daha yeni sürümler kabul edilir.</summary>
    public bool TryApply(SignedDirectory signed, out string? error)
    {
        lock (_gate)
        {
            if (_acceptNewOrganization && PinnedOrgKey == null)
            {
                error = "Yeni organizasyon kuruluyor.";
                return false;
            }
            if (!Verify(signed, out var dir, out error) || dir == null) return false;
            if (dir.Version <= Version)
            {
                error = "Eski sürüm.";
                return false;
            }
            if (PinnedOrgKey == null)
            {
                var orgKey = Convert.FromBase64String(dir.OrgKey);
                PinnedOrgKey = orgKey;
                LocalProtector.WriteAtomic(AppPaths.PinnedOrgKeyFile, Encoding.ASCII.GetBytes(Convert.ToBase64String(orgKey)));
                Log.Info("Organizasyon anahtarı sabitlendi (TOFU): " + CryptoUtil.Fingerprint(orgKey));
            }
            Current = dir;
            CurrentSigned = signed;
            LocalProtector.WriteAtomic(AppPaths.DirectoryFile, JsonSerializer.SerializeToUtf8Bytes(signed));
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Admin tarafından: yeni dizini organizasyon anahtarıyla imzalar ve yerelde uygular.</summary>
    public SignedDirectory SignAndApply(OrgDirectory dir, ECDsa orgKey)
    {
        var orgPub = orgKey.ExportSubjectPublicKeyInfo();
        dir.OrgKey = Convert.ToBase64String(orgPub);
        dir.Version = Math.Max(Version, dir.Version) + 1;
        dir.IssuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var err = dir.Validate();
        if (err != null) throw new InvalidOperationException(err);

        var payload = JsonSerializer.SerializeToUtf8Bytes(dir);
        if (payload.Length > MaxPayloadBytes) throw new InvalidOperationException("Dizin çok büyük.");
        var signed = new SignedDirectory
        {
            Payload = Convert.ToBase64String(payload),
            Signature = Convert.ToBase64String(orgKey.SignData(payload, HashAlgorithmName.SHA256)),
        };
        if (!TryApply(signed, out var error)) throw new InvalidOperationException(error);
        return signed;
    }

    /// <summary>Yeni organizasyon kurulurken güven kökünü yerelde sabitler.</summary>
    public void PinOrgKey(byte[] orgPub)
    {
        lock (_gate)
        {
            if (PinnedOrgKey != null && !CryptographicOperations.FixedTimeEquals(PinnedOrgKey, orgPub))
                throw new InvalidOperationException("Bu bilgisayar zaten başka bir organizasyon anahtarına güveniyor.");
            PinnedOrgKey = orgPub;
            _acceptNewOrganization = false;
            TryDelete(NewOrgMarker);
            if (!IsDeployedTrustRoot)
                LocalProtector.WriteAtomic(AppPaths.PinnedOrgKeyFile, Encoding.ASCII.GetBytes(Convert.ToBase64String(orgPub)));
        }
    }

    /// <summary>
    /// Bu bilgisayardaki organizasyon güvenini bırakır. Kurulumla dağıtılan anahtar (exe yanı / ProgramData) kalkmadan yeni anahtar kurulamaz.
    /// </summary>
    public bool BeginNewOrganization(out string? error)
    {
        if (IsDeployedTrustRoot)
        {
            error = "Kurulumdaki organizasyon anahtarı duruyor. Yeni organizasyon, o anahtarın olmadığı kopyadan kurulmalı.";
            return false;
        }
        lock (_gate)
        {
            PinnedOrgKey = null;
            Current = null;
            CurrentSigned = null;
            ConflictDetected = false;
            _acceptNewOrganization = true;
            TryDelete(AppPaths.PinnedOrgKeyFile);
            TryDelete(AppPaths.DirectoryFile);
            AppPaths.EnsureCreated();
            File.WriteAllBytes(NewOrgMarker, []);
        }
        Log.Info("Yerel organizasyon güveni bırakıldı; yeni anahtar kurulana kadar dizin kabul edilmeyecek.");
        Changed?.Invoke();
        error = null;
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Dosya silinemedi ({Path.GetFileName(path)}): {ex.GetType().Name}");
        }
    }

    public void ReportOrgFingerprintSeen(string? fp)
    {
        if (fp == null || OrgFingerprint == null || fp == OrgFingerprint || ConflictDetected) return;
        ConflictDetected = true;
        Log.Warn("Ağda farklı bir organizasyon anahtarı görüldü: " + fp);
        Changed?.Invoke();
    }

    /// <summary>Sunum, yetki ve filtrelemede sık çağrılır; binlerce üyelik dizinde doğrusal tarama yapılmaz.</summary>
    public OrgMember? GetMember(string? peerId) => peerId == null ? null : _membersById.GetValueOrDefault(peerId);

    private static Dictionary<string, OrgMember> BuildMemberIndex(OrgDirectory? dir)
    {
        var index = new Dictionary<string, OrgMember>(StringComparer.Ordinal);
        // Yinelenen kimlikte önceki davranış (FirstOrDefault) korunur: ilk kayıt geçerlidir.
        foreach (var member in dir?.Members ?? []) index.TryAdd(member.PeerId, member);
        return index;
    }

    public Role GetRole(string peerId) => GetMember(peerId)?.Role ?? Role.User;

    public string? GetUnitName(string? unitId) =>
        unitId == null ? null : Current?.Units.FirstOrDefault(u => u.Id == unitId)?.Name;

    public IEnumerable<OrgMember> MembersOfUnit(string unitId) =>
        Current?.Members.Where(m => m.UnitId == unitId) ?? [];
}
