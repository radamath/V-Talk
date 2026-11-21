using System.Collections.Concurrent;

namespace VTalk.Services;

/// <summary>
/// UDP duyurusu gelen ama TCP ile ulaşılamayan eşe (farklı alt ağ, güvenlik duvarı) arka plan teslim denemelerini
/// seyreltir: 30 sn, 1, 2, 4, 8, en fazla 10 dk. Kullanıcının yeni mesajı, eşin duyurusu veya bize bağlanması
/// beklemeden denenir; yalnızca zamanlayıcıdaki toplu yeniden deneme bekler. Günlüğe yalnızca ilk hata ve kurtulma yazılır.
/// </summary>
internal sealed class DeliveryBackoff
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(10);
    private const int MaxDoublings = 10;

    private readonly ConcurrentDictionary<string, (int Failures, DateTime NextTryUtc)> _state = new();

    /// <summary>Zamanlayıcı bu eşi şimdilik atlamalı mı.</summary>
    public bool ShouldWait(string peerId, DateTime nowUtc) =>
        _state.TryGetValue(peerId, out var s) && nowUtc < s.NextTryUtc;

    public bool IsFailing(string peerId) => _state.ContainsKey(peerId);

    /// <returns>Art arda hata sayısı (1 ise günlüğe yazılmalı).</returns>
    public int RecordFailure(string peerId, DateTime nowUtc)
    {
        var updated = _state.AddOrUpdate(peerId,
            _ => (1, nowUtc + FirstDelay),
            (_, s) => (s.Failures + 1, nowUtc + DelayFor(s.Failures + 1)));
        return updated.Failures;
    }

    /// <returns>Eş daha önce ulaşılamaz durumdaysa true (kurtulma günlüğe yazılmalı).</returns>
    public bool RecordSuccess(string peerId) => _state.TryRemove(peerId, out _);

    public static TimeSpan DelayFor(int failures)
    {
        var doublings = Math.Clamp(failures - 1, 0, MaxDoublings);
        var delay = TimeSpan.FromTicks(FirstDelay.Ticks << doublings);
        return delay > MaxDelay ? MaxDelay : delay;
    }
}
