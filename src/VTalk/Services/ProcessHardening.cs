using System.Runtime.InteropServices;
using VTalk.Core;

namespace VTalk.Services;

/// <summary>
/// Sürecin başlangıcında uygulanan savunma amaçlı sertleştirmeler.
/// Yalnızca DLL ele geçirme (DLL hijacking / planting) saldırı yüzeyini kapatır:
///  - Çalışma dizini DLL arama sırasından çıkarılır (SetDllDirectory("")).
///  - İmzasız/güvensiz dizinlerden DLL yüklenmesi yerine System32 tercih edilir ve
///    düşük bütünlük etiketli görüntülerin yüklenmesi engellenir (ProcessImageLoadPolicy).
/// Bu önlemler standart, savunma odaklı güvenlik iyileştirmeleridir.
/// </summary>
internal static class ProcessHardening
{
    private const int ProcessImageLoadPolicy = 10;
    private const uint NoLowMandatoryLabelImages = 0x2;
    private const uint PreferSystem32Images = 0x4;

    public static void Apply()
    {
        try
        {
            SetDllDirectoryW("");
            uint flags = NoLowMandatoryLabelImages | PreferSystem32Images;
            SetProcessMitigationPolicy(ProcessImageLoadPolicy, ref flags, sizeof(uint));
        }
        catch (Exception ex)
        {
            Log.Warn("DLL arama sertleştirmesi uygulanamadı: " + ex.Message);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref uint buffer, int length);
}
