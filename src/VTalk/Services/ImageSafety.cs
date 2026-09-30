using System.IO;
using System.Windows.Media.Imaging;

namespace VTalk.Services;

/// <summary>
/// Güvenilmeyen resimlerin çözülmesi. Windows Imaging Component, çözücüyü dosya uzantısına değil içeriğe göre seçer;
/// bu yüzden ".png" adlı bir dosya kurulu herhangi bir codec'e (TIFF, RAW, HEIF, üçüncü taraf eklentiler) yönlendirilebilir.
/// Burada yalnızca imzası (magic bytes) PNG/JPEG/GIF/BMP olan ve piksel boyutu makul resimler kabul edilir.
/// </summary>
public static class ImageSafety
{
    public const int MaxSide = 16384;
    public const long MaxPixels = 100_000_000;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Gif87 = "GIF87a"u8.ToArray();
    private static readonly byte[] Gif89 = "GIF89a"u8.ToArray();
    private static readonly byte[] Bmp = "BM"u8.ToArray();

    public static bool HasAllowedSignature(ReadOnlySpan<byte> header) =>
        header.StartsWith(Png) || header.StartsWith(Jpeg) || header.StartsWith(Gif87) || header.StartsWith(Gif89) || header.StartsWith(Bmp);

    public static bool HasAllowedSignature(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[8];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int n = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return HasAllowedSignature(header[..n]);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Resmin izinli formatta, çözülebilir ve boyut sınırları içinde olduğunu doğrular.</summary>
    public static bool IsSafeImage(string path, int maxSide = MaxSide)
    {
        if (!HasAllowedSignature(path)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return IsSafeImage(fs, maxSide);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSafeImage(byte[] data, int maxSide = MaxSide)
    {
        if (!HasAllowedSignature(data)) return false;
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            return IsSafeImage(ms, maxSide);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafeImage(Stream stream, int maxSide)
    {
        try
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder or GifBitmapDecoder or BmpBitmapDecoder)) return false;
            if (decoder.Frames.Count is 0 or > 500) return false;
            var f = decoder.Frames[0];
            return f.PixelWidth is > 0 && f.PixelHeight is > 0 && f.PixelWidth <= maxSide && f.PixelHeight <= maxSide &&
                   (long)f.PixelWidth * f.PixelHeight <= MaxPixels;
        }
        catch
        {
            return false;
        }
    }
}
