using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VTalk.Security;

/// <summary>
/// Güven kökü gibi hassas dosyaların başka kullanıcılar tarafından yerleştirilip değiştirilemeyeceğini denetler.
/// Örn. standart kullanıcılar C:\ProgramData altında klasör oluşturabilir; oraya önceden konmuş sahte bir org.pub
/// o bilgisayardaki herkesi sahte bir organizasyona bağlardı.
/// Dosya ve üst klasörün sahibi ile yazma yetkisi olanlar yalnızca SYSTEM, Administrators, TrustedInstaller
/// ve geçerli kullanıcı olabilir.
/// </summary>
public static class FileTrust
{
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public static bool IsProtectedFromOtherUsers(string path, out string? reason)
    {
        reason = null;
        try
        {
            var trusted = new HashSet<SecurityIdentifier>
            {
                new(WellKnownSidType.LocalSystemSid, null),
                new(WellKnownSidType.BuiltinAdministratorsSid, null),
                TrustedInstaller,
            };
            if (WindowsIdentity.GetCurrent().User is { } me) trusted.Add(me);

            var file = new FileInfo(path);
            if (!Check(file.GetAccessControl(), trusted, out reason)) return false;
            if (file.Directory != null && !Check(file.Directory.GetAccessControl(), trusted, out reason))
            {
                reason = "Klasör: " + reason;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = "İzinler okunamadı: " + ex.Message;
            return false;
        }
    }

    private static bool Check(FileSystemSecurity security, HashSet<SecurityIdentifier> trusted, out string? reason)
    {
        reason = null;
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !trusted.Contains(owner))
        {
            reason = "Sahibi güvenilir değil: " + Describe(owner);
            return false;
        }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            if ((rule.FileSystemRights & WriteRights) == 0) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && trusted.Contains(sid)) continue;
            reason = "Yazma yetkisi olan güvenilmeyen hesap: " + Describe(rule.IdentityReference);
            return false;
        }
        return true;
    }

    private static string Describe(IdentityReference id)
    {
        try { return id.Translate(typeof(NTAccount)).Value; }
        catch { return id.Value; }
    }
}
