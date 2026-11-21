using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace VTalk.Security;

/// <summary>
/// Kurulum paketinin bu ürünün imza sertifikasıyla imzalandığını ve aynı ürün ailesi olduğunu doğrular.
/// İmza özeti (Authenticode) her zaman doğrulanır; kök sertifika istemcilerde güvenilir olmasa da
/// geçerli imza + sabit yayıncı parmak izi + UpgradeCode + sürüm yeterli kabul edilir.
/// </summary>
internal static class PackageTrust
{
    /// <summary>V-Controller Code Signing. Sertifika değişirse bu sabitle birlikte yeni bir sürüm dağıtılmalıdır.</summary>
    public const string PublisherThumbprint = "1D62A262EE49AC352D746B1829A0FF6BEC309988";
    public const string UpgradeCode = "{F88389BB-4BB2-4D69-9C99-4F074EF700E5}";

    private const int Trusted = 0;
    private const int UntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEChaining = unchecked((int)0x800B010A);

    public static bool IsVTalkUpdate(string path, out Version version) =>
        IsVTalkUpdate(path, out version, out _);

    public static bool IsVTalkUpdate(string path, out Version version, out string? reason)
    {
        version = new Version(0, 0);
        reason = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            reason = "Dosya yok";
            return false;
        }

        // 1) MSI kimliği (COM veya msi.dll) — Authenticode'dan bağımsız; paket=0.0 burada kalmasın.
        if (!TryReadIdentity(path, out var upgrade, out var parsed))
        {
            reason = "MSI ProductVersion/UpgradeCode okunamadı";
            return false;
        }
        if (!string.Equals(NormalizeGuid(upgrade), NormalizeGuid(UpgradeCode), StringComparison.OrdinalIgnoreCase))
        {
            reason = "UpgradeCode uyuşmuyor";
            return false;
        }

        // 2) Yayıncı parmak izi (dosyadaki imza sertifikası)
        if (!TryGetSignerThumbprint(path, out var thumb) ||
            !string.Equals(thumb, PublisherThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            reason = string.IsNullOrEmpty(thumb) ? "İmza sertifikası okunamadı" : "Yayıncı parmak izi uyuşmuyor";
            return false;
        }

        // 3) WinVerifyTrust: imza özeti dosya içeriğiyle eşleşmeli. CreateFromSignedFile yalnızca sertifikayı okur,
        //    içeriği doğrulamaz; içeriği değiştirilmiş paket (TRUST_E_BAD_DIGEST vb.) burada reddedilir.
        //    İç CA kökü istemcilerde güvenilir olmayabilir: UntrustedRoot/Chaining yalnızca özet doğrulandıktan sonra
        //    döner ve imzalayan zaten sabit parmak iziyle eşleştiği için kabul edilir.
        var status = VerifyAuthenticode(path);
        if (status != Trusted && status != UntrustedRoot && status != CertEChaining)
        {
            reason = $"Authenticode doğrulaması başarısız (0x{status:X8})";
            return false;
        }

        version = parsed;
        return true;
    }

    private static bool TryGetSignerThumbprint(string path, out string thumbprint)
    {
        thumbprint = "";
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            thumbprint = cert.Thumbprint ?? "";
            return thumbprint.Length == 40;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string NormalizeGuid(string value)
    {
        var hex = new string(value.Where(Uri.IsHexDigit).ToArray());
        return hex.Length == 32 ? hex.ToUpperInvariant() : "";
    }

    private static bool TryReadIdentity(string path, out string upgrade, out Version version)
    {
        // msi.dll tanıtıcıları hemen kapatır; COM nesnesi ise dosyayı çöp toplayıcıya kadar açık tutabilir
        // (doğrulamadan hemen sonra paketi taşıma/silme "dosya kullanımda" ile başarısız olurdu).
        if (TryReadIdentityMsi(path, out upgrade, out version)) return true;
        return TryReadIdentityCom(path, out upgrade, out version);
    }

    private static bool TryReadIdentityCom(string path, out string upgrade, out Version version)
    {
        upgrade = "";
        version = new Version(0, 0);
        var comObjects = new List<object>();
        try
        {
            var installerType = Type.GetTypeFromProgID("WindowsInstaller.Installer");
            if (installerType == null) return false;
            var installer = Track(comObjects, Activator.CreateInstance(installerType));
            if (installer == null) return false;
            var db = Track(comObjects, installerType.InvokeMember("OpenDatabase", BindingFlags.InvokeMethod, null, installer, [path, 0]));
            if (db == null) return false;
            var up = QueryCom(db, "SELECT `Value` FROM `Property` WHERE `Property`='UpgradeCode'", comObjects);
            var ver = QueryCom(db, "SELECT `Value` FROM `Property` WHERE `Property`='ProductVersion'", comObjects);
            if (string.IsNullOrWhiteSpace(up) || !Version.TryParse(ver, out var parsed)) return false;
            upgrade = up;
            version = parsed;
            return true;
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            // Son oluşturulandan başlayarak bırakılır: kayıt → görünüm → veritabanı (dosya tanıtıcısı) → yükleyici.
            for (var i = comObjects.Count - 1; i >= 0; i--)
                if (Marshal.IsComObject(comObjects[i])) Marshal.FinalReleaseComObject(comObjects[i]);
        }
    }

    private static object? Track(List<object> comObjects, object? value)
    {
        if (value != null) comObjects.Add(value);
        return value;
    }

    private static string? QueryCom(object database, string sql, List<object> comObjects)
    {
        var view = Track(comObjects, database.GetType().InvokeMember("OpenView", BindingFlags.InvokeMethod, null, database, [sql]));
        if (view == null) return null;
        view.GetType().InvokeMember("Execute", BindingFlags.InvokeMethod, null, view, null);
        var record = Track(comObjects, view.GetType().InvokeMember("Fetch", BindingFlags.InvokeMethod, null, view, null));
        if (record == null) return null;
        return record.GetType().InvokeMember("StringData", BindingFlags.GetProperty, null, record, [1]) as string;
    }

    private static bool TryReadIdentityMsi(string path, out string upgrade, out Version version)
    {
        upgrade = "";
        version = new Version(0, 0);
        IntPtr db = IntPtr.Zero;
        try
        {
            // MSIDBOPEN_READONLY = 0
            if (MsiOpenDatabaseW(path, new IntPtr(0), out db) != 0 || db == IntPtr.Zero) return false;
            if (!MsiQuery(db, "SELECT `Value` FROM `Property` WHERE `Property`='UpgradeCode'", out var up)) return false;
            if (!MsiQuery(db, "SELECT `Value` FROM `Property` WHERE `Property`='ProductVersion'", out var ver)) return false;
            if (string.IsNullOrWhiteSpace(up) || !Version.TryParse(ver, out var parsed)) return false;
            upgrade = up!;
            version = parsed;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // msi.dll yoksa COM yolu denenir.
            return false;
        }
        finally
        {
            if (db != IntPtr.Zero) MsiCloseHandle(db);
        }
    }

    private static bool MsiQuery(IntPtr db, string sql, out string? value)
    {
        value = null;
        IntPtr view = IntPtr.Zero;
        IntPtr record = IntPtr.Zero;
        try
        {
            if (MsiDatabaseOpenViewW(db, sql, out view) != 0 || view == IntPtr.Zero) return false;
            if (MsiViewExecute(view, IntPtr.Zero) != 0) return false;
            if (MsiViewFetch(view, out record) != 0 || record == IntPtr.Zero) return false;
            uint len = 512;
            var sb = new StringBuilder((int)len);
            if (MsiRecordGetStringW(record, 1, sb, ref len) != 0)
            {
                sb = new StringBuilder((int)len + 1);
                len++;
                if (MsiRecordGetStringW(record, 1, sb, ref len) != 0) return false;
            }
            value = sb.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }
        finally
        {
            if (record != IntPtr.Zero) MsiCloseHandle(record);
            if (view != IntPtr.Zero) MsiCloseHandle(view);
        }
    }

    private static int VerifyAuthenticode(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var fileInfo = new WintrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            pcwszFilePath = Marshal.StringToCoTaskMemUni(path),
        };
        var data = new WintrustData
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
            dwUIChoice = 2,
            fdwRevocationChecks = 0,
            dwUnionChoice = 1,
            dwStateAction = 1,
            dwProvFlags = 0x00000100,
        };
        var filePtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustFileInfo>());
        var dataPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustData>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            data.pFile = filePtr;
            Marshal.StructureToPtr(data, dataPtr, false);
            var status = WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);
            data = Marshal.PtrToStructure<WintrustData>(dataPtr);
            data.dwStateAction = 2;
            Marshal.StructureToPtr(data, dataPtr, false);
            WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);
            return status;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
            Marshal.FreeCoTaskMem(filePtr);
            Marshal.FreeCoTaskMem(dataPtr);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, IntPtr data);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiOpenDatabaseW(string szDatabasePath, IntPtr szPersist, out IntPtr ph);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiDatabaseOpenViewW(IntPtr hDatabase, string szQuery, out IntPtr phView);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiViewExecute(IntPtr hView, IntPtr hRecord);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiViewFetch(IntPtr hView, out IntPtr phRecord);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiRecordGetStringW(IntPtr hRecord, uint iField, StringBuilder szValueBuf, ref uint pcchValueBuf);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(IntPtr hAny);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }
}
