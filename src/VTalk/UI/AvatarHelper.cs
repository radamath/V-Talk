using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VTalk.Core;

namespace VTalk.UI;

public static class AvatarHelper
{
    private const int Size = 96;

    /// <summary>Seçilen resmi kare kırpar, 96x96 PNG olarak yeniden kodlar (metadata ve gömülü içerik atılır).</summary>
    public static void SaveAvatar(string sourcePath)
    {
        if (new FileInfo(sourcePath).Length > 20 * 1024 * 1024) throw new InvalidOperationException("Resim 20 MB'tan küçük olmalı.");
        if (!Services.ImageSafety.IsSafeImage(sourcePath)) throw new InvalidOperationException("Yalnızca PNG, JPEG, GIF veya BMP resimleri kullanılabilir.");

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.DecodePixelWidth = 512;
        bmp.UriSource = new Uri(sourcePath, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();

        int side = Math.Min(bmp.PixelWidth, bmp.PixelHeight);
        if (side <= 0) throw new InvalidOperationException("Geçersiz resim.");
        var crop = new CroppedBitmap(bmp, new Int32Rect((bmp.PixelWidth - side) / 2, (bmp.PixelHeight - side) / 2, side, side));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            dc.DrawImage(crop, new Rect(0, 0, Size, Size));
        }
        var target = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        LocalProtector.WriteAtomic(AppPaths.AvatarFile, ms.ToArray());
    }
}
