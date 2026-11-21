namespace VTalk.Core;

public enum ReceiptState
{
    /// <summary>Gelen mesaj veya henüz tüm alıcılara teslim edilmedi: tik yok.</summary>
    None,
    /// <summary>Tüm alıcılara teslim edildi: soluk tik.</summary>
    Delivered,
    /// <summary>Tüm alıcılar okudu: mavi tik.</summary>
    Read,
}

/// <param name="ReadCount">Okuyan alıcı sayısı (grupta ipucu metni için).</param>
/// <param name="Recipients">Alıcı sayısı (1:1'de 1, grupta kendimiz hariç üyeler).</param>
public readonly record struct ReceiptStatus(ReceiptState State, int ReadCount, int Recipients)
{
    public static ReceiptStatus None { get; } = new(ReceiptState.None, 0, 0);
}
