namespace VTalk.Core;

/// <summary>Karşı tarafın bildirdiği mesaj zamanını yerel kayda uygun hale getirir.</summary>
public static class RemoteTimestamp
{
    /// <summary>Saat farkı toleransı; bunun ötesindeki "gelecek" zamanlar sahte ya da saati bozuk kabul edilir.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gönderenin zamanı geçmişte (eşitlemeyle yeniden gelen eski mesajlar) veya tolerans içinde ise korunur;
    /// sıfır/negatif ya da tolerans ötesinde gelecekteyse alış zamanı kullanılır. Böylece bir üye mesajını
    /// sonsuza dek "en yeni" gösteremez ve arayüzün tarih dönüşümünü (yıl 9999 sınırı) patlatamaz.
    /// </summary>
    public static long Sanitize(long remoteMs, long nowMs) =>
        remoteMs > 0 && remoteMs <= nowMs + (long)MaxClockSkew.TotalMilliseconds ? remoteMs : nowMs;

    public static long Sanitize(long remoteMs) => Sanitize(remoteMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static readonly long MinUnixMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly long MaxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>
    /// Kayıtlı zamanı yerel saate çevirir; aralık dışı değerde istisna atmaz. Bu düzeltmeden önce
    /// veritabanına girmiş bozuk zamanlar sohbet penceresini çökertmesin diye.
    /// </summary>
    public static DateTime ToLocalTime(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(unixMs, MinUnixMs, MaxUnixMs)).LocalDateTime;
}
