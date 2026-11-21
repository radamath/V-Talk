using System.IO;
using System.Net;
using VTalk.Core;
using VTalk.Data;
using VTalk.Net;
using VTalk.Security;
using VTalk.Services;

namespace VTalk.Tests;

public class GroupMembershipTests
{
    private const string Me = "11111111111111111111111111111111";
    private const string Creator = "22222222222222222222222222222222";
    private const string Member = "33333333333333333333333333333333";
    private const string Stranger = "44444444444444444444444444444444";
    private const string Newcomer = "55555555555555555555555555555555";

    private static ChatGroup Existing() => new()
    {
        Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        Name = "Test",
        CreatedBy = Creator,
        MemberIds = [Creator, Me, Member],
    };

    [Fact]
    public void UnknownGroup_IsAcceptedOnlyWhenItIncludesMe()
    {
        Assert.Equal(GroupUpdateDecision.Apply, ChatGroup.EvaluateUpdate(null, Stranger, Me, [Stranger, Me]));
        Assert.Equal(GroupUpdateDecision.Ignore, ChatGroup.EvaluateUpdate(null, Stranger, Me, [Stranger]));
        Assert.Equal(GroupUpdateDecision.Ignore, ChatGroup.EvaluateUpdate(null, Stranger, Me, []));
    }

    [Fact]
    public void NonMember_CannotChangeKnownGroup()
    {
        // Gruptan çıkarılmış eski üye veya grup kimliğini öğrenen yabancı: geri katılamaz, kimseyi çıkaramaz.
        Assert.Equal(GroupUpdateDecision.Forbidden,
            ChatGroup.EvaluateUpdate(Existing(), Stranger, Me, [Creator, Me, Member, Stranger]));
        Assert.Equal(GroupUpdateDecision.Forbidden, ChatGroup.EvaluateUpdate(Existing(), Stranger, Me, []));
    }

    [Fact]
    public void OnlyCreator_CanRemoveMeOrDissolve()
    {
        Assert.Equal(GroupUpdateDecision.Forbidden, ChatGroup.EvaluateUpdate(Existing(), Member, Me, [Creator, Member]));
        Assert.Equal(GroupUpdateDecision.Forbidden, ChatGroup.EvaluateUpdate(Existing(), Member, Me, []));
        Assert.Equal(GroupUpdateDecision.RemovedMe, ChatGroup.EvaluateUpdate(Existing(), Creator, Me, [Creator, Member]));
        Assert.Equal(GroupUpdateDecision.RemovedMe, ChatGroup.EvaluateUpdate(Existing(), Creator, Me, []));
    }

    [Fact]
    public void OnlyCreator_CanRemoveOthers_ButAnyMemberCanAddOrLeave()
    {
        Assert.Equal(GroupUpdateDecision.Forbidden, ChatGroup.EvaluateUpdate(Existing(), Member, Me, [Member, Me]));
        Assert.Equal(GroupUpdateDecision.Apply, ChatGroup.EvaluateUpdate(Existing(), Creator, Me, [Creator, Me]));
        Assert.Equal(GroupUpdateDecision.Apply,
            ChatGroup.EvaluateUpdate(Existing(), Member, Me, [Creator, Me, Member, Newcomer]));
        // Gönderenin kendini listeden çıkarması ayrılmadır.
        Assert.Equal(GroupUpdateDecision.Apply, ChatGroup.EvaluateUpdate(Existing(), Member, Me, [Creator, Me]));
    }

    [Fact]
    public void NormalizeMembers_DropsInvalidIds_AndLimitsCount()
    {
        var normalized = ChatGroup.NormalizeMembers(
            [" " + Me.ToUpperInvariant() + " ", Me, "../../etc", "", "zz", Creator]);
        Assert.Equal([Me, Creator], normalized);

        var many = Enumerable.Range(0, ChatGroup.MaxMembers + 10).Select(i => i.ToString("x32"));
        Assert.Equal(ChatGroup.MaxMembers, ChatGroup.NormalizeMembers(many).Count);
    }

    // ---------------------------------------------------------------- Uçtan uca (127.0.0.1)

    private sealed class Peer : IDisposable
    {
        public Identity Id { get; } = Identity.CreateEphemeral();
        public Database Db { get; }
        public ChatService Chat { get; }
        public string Name { get; }
        private readonly LocalProtector _protector = LocalProtector.LoadOrCreate();

        public Peer(string name)
        {
            Name = name;
            Db = new Database(Path.Combine(AppPaths.DataDir, $"grp-{name}-{Guid.NewGuid():N}.db"), _protector);
            var settings = new Settings { DisplayName = name, PreferredTcpPort = 0 };
            Chat = new ChatService(Id, settings, Db, new DirectoryService(), loopbackOnly: true);
            Chat.Start();
        }

        public void Dispose()
        {
            Chat.Dispose();
            Db.Dispose();
            _protector.Dispose();
            Id.Dispose();
        }
    }

    private static void Introduce(params Peer[] peers)
    {
        foreach (var a in peers)
        foreach (var b in peers)
        {
            if (ReferenceEquals(a, b)) continue;
            a.Chat.Registry.Upsert(new PeerSnapshot
            {
                PeerId = b.Id.PeerId, PublicKey = b.Id.PublicKey, Name = b.Name, Status = PresenceStatus.Online,
                Address = IPAddress.Loopback, Port = b.Chat.TcpPort, LastSeenUtc = DateTime.UtcNow,
            });
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Beklenen durum oluşmadı.");
    }

    [Fact]
    public async Task ForgedRemoval_IsRejected_AndCreatorRemoval_KeepsHistory()
    {
        using var alice = new Peer("Alice");
        using var bob = new Peer("Bob");
        using var carol = new Peer("Carol");
        Introduce(alice, bob, carol);

        var group = alice.Chat.CreateChatGroup("Ekip", [bob.Id.PeerId, carol.Id.PeerId])!;
        var conv = group.ConversationId;
        await WaitUntil(() => carol.Db.GetChatGroup(group.Id) != null && bob.Db.GetChatGroup(group.Id) != null);
        await WaitUntil(() => carol.Db.LoadHistory(conv, 10).Count > 0);

        // Grup yeniden eşitlemeyle hemen geri oluşturulabildiği için silinme anında yakalanır.
        var carolLostGroup = false;
        carol.Chat.GroupsChanged += () => { if (carol.Db.GetChatGroup(group.Id) == null) carolLostGroup = true; };

        // Kuran olmayan Bob, kendi yerel kaydını değiştirip Carol'ı çıkarmaya çalışır.
        // created_by upsert'te değişmediği için kayıt silinip yeniden eklenir.
        var forged = bob.Db.GetChatGroup(group.Id)!;
        forged.CreatedBy = bob.Id.PeerId;
        bob.Db.DeleteChatGroup(group.Id, keepMessages: true);
        bob.Db.UpsertChatGroup(forged);
        Assert.Equal(bob.Id.PeerId, bob.Db.GetChatGroup(group.Id)!.CreatedBy);
        bob.Chat.RemoveMemberFromChatGroup(group.Id, carol.Id.PeerId);
        await Task.Delay(1500);
        Assert.False(carolLostGroup);
        Assert.Contains(carol.Id.PeerId, carol.Db.GetChatGroup(group.Id)!.MemberIds);
        Assert.True(carol.Db.LoadHistory(conv, 10).Count > 0);

        // Kuran Alice çıkarınca Carol gruptan düşer ama geçmişi silinmez.
        alice.Chat.RemoveMemberFromChatGroup(group.Id, carol.Id.PeerId);
        await WaitUntil(() => carol.Db.GetChatGroup(group.Id) == null);
        Assert.True(carol.Db.LoadHistory(conv, 10).Count > 0);
        await WaitUntil(() => bob.Db.GetChatGroup(group.Id) is { } g && !g.MemberIds.Contains(carol.Id.PeerId));
    }

    private static int UnusedTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task FailedDelivery_DoesNotConsumeGroupSync_SoReconnectDeliversMissedGroup()
    {
        using var alice = new Peer("Alice");
        using var bob = new Peer("Bob");
        Introduce(alice, bob);
        var bobAtWrongPort = alice.Chat.Registry.Get(bob.Id.PeerId)! with { Port = UnusedTcpPort() };
        alice.Chat.Registry.Upsert(bobAtWrongPort);

        // Davet yayını olmadan yalnızca Alice'te var olan grup: Bob'a ancak eşitlemeyle ulaşabilir.
        var group = new ChatGroup
        {
            Id = CryptoUtil.NewId(), Name = "Kaçan", CreatedBy = alice.Id.PeerId,
            CreatedUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MemberIds = [alice.Id.PeerId, bob.Id.PeerId],
        };
        alice.Db.UpsertChatGroup(group);

        // Bob'a ulaşılamazken yapılan teslim denemesi grup eşitleme hakkını tüketmemeli.
        // Windows'ta kapalı porta bağlanma ~2 sn sürer; bekleme, eski davranıştaki eşitlemenin (3 deneme) tamamen
        // bitmesine yetecek kadar uzun tutulur. Aksi halde o eşitleme port düzeltilince tesadüfen başarır.
        alice.Chat.SendText(bob.Id.PeerId, "bir");
        await Task.Delay(12000);
        Assert.Null(bob.Db.GetChatGroup(group.Id));

        alice.Chat.Registry.Upsert(bobAtWrongPort with { Port = bob.Chat.TcpPort, LastSeenUtc = DateTime.UtcNow });
        alice.Chat.SendText(bob.Id.PeerId, "iki");
        await WaitUntil(() => bob.Db.LoadHistory(alice.Id.PeerId, 10).Count == 2, timeoutMs: 5000);
        // Eşitleme aralığından (20 sn) çok kısa sürede gelmeli.
        await WaitUntil(() => bob.Db.GetChatGroup(group.Id) != null, timeoutMs: 5000);
    }
}
