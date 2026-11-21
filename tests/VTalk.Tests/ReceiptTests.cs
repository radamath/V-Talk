using System.IO;
using System.Net;
using VTalk.Core;
using VTalk.Data;
using VTalk.Net;
using VTalk.Security;
using VTalk.Services;

namespace VTalk.Tests;

public class ReceiptTests
{
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
            Db = new Database(Path.Combine(AppPaths.DataDir, $"rcpt-{name}-{Guid.NewGuid():N}.db"), _protector);
            Chat = new ChatService(Id, new Settings { DisplayName = name, PreferredTcpPort = 0 }, Db, new DirectoryService(), loopbackOnly: true);
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

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 15000)
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
    public async Task DirectMessage_TurnsDelivered_ThenRead()
    {
        using var alice = new Peer("Alice");
        using var bob = new Peer("Bob");
        Introduce(alice, bob);

        var sent = alice.Chat.SendText(bob.Id.PeerId, "Merhaba")!;
        StoredMessage Current() => alice.Db.GetMessage(bob.Id.PeerId, sent.MsgId, MessageDirection.Outgoing)!;
        await WaitUntil(() => alice.Chat.GetReceiptStatus(Current()).State == ReceiptState.Delivered);

        bob.Chat.SendReadReceipts(alice.Id.PeerId);
        await WaitUntil(() => alice.Chat.GetReceiptStatus(Current()).State == ReceiptState.Read);
    }

    [Fact]
    public async Task GroupMessage_IsBlueOnlyWhenEveryMemberRead()
    {
        using var alice = new Peer("Alice");
        using var bob = new Peer("Bob");
        using var carol = new Peer("Carol");
        Introduce(alice, bob, carol);

        var group = alice.Chat.CreateChatGroup("Ekip", [bob.Id.PeerId, carol.Id.PeerId])!;
        await WaitUntil(() => bob.Db.GetChatGroup(group.Id) != null && carol.Db.GetChatGroup(group.Id) != null);

        var sent = alice.Chat.SendGroupText(group.Id, "Toplantı 3'te")!;
        await WaitUntil(() => alice.Chat.GetReceiptStatus(sent) is { State: ReceiptState.Delivered, Recipients: 2, ReadCount: 0 });
        await WaitUntil(() => bob.Db.LoadHistory(group.ConversationId, 10).Any(m => m.MsgId == sent.MsgId) &&
                              carol.Db.LoadHistory(group.ConversationId, 10).Any(m => m.MsgId == sent.MsgId));

        bob.Chat.SendReadReceipts(group.ConversationId);
        await WaitUntil(() => alice.Chat.GetReceiptStatus(sent).ReadCount == 1);
        Assert.Equal(ReceiptState.Delivered, alice.Chat.GetReceiptStatus(sent).State);

        carol.Chat.SendReadReceipts(group.ConversationId);
        await WaitUntil(() => alice.Chat.GetReceiptStatus(sent) is { State: ReceiptState.Read, ReadCount: 2 });
    }

    [Fact]
    public async Task OfflineMember_ReceivesEveryMissedGroupMessage_WhenBackOnline()
    {
        using var alice = new Peer("Alice");
        using var bob = new Peer("Bob");
        using var carol = new Peer("Carol");
        Introduce(alice, bob, carol);
        // Carol, Alice için çevrimdışı: davet ve mesajlar ona ulaşamaz.
        alice.Chat.Registry.MarkOffline(carol.Id.PeerId);

        var group = alice.Chat.CreateChatGroup("Ekip", [bob.Id.PeerId, carol.Id.PeerId])!;
        await WaitUntil(() => bob.Db.GetChatGroup(group.Id) != null);
        const int count = 90; // eski eşitleme yalnızca son 80 mesajı yeniden gönderiyordu
        // Yerel "grup oluşturuldu" notu da gelen olarak saklanır; yalnızca Alice'in metinleri sayılır.
        bool FromAlice(StoredMessage m) => m.Direction == MessageDirection.Incoming && m.Kind == MessageKind.Text &&
                                           m.Data.SenderPeerId == alice.Id.PeerId && (m.Data.Text ?? "").StartsWith("Mesaj ");
        for (var i = 0; i < count; i++) alice.Chat.SendGroupText(group.Id, $"Mesaj {i}");
        int BobCount() => bob.Db.LoadHistory(group.ConversationId, 300).Count(m => FromAlice(m));
        try { await WaitUntil(() => BobCount() == count, 90000); }
        catch (TimeoutException) { Assert.Fail($"Bob {BobCount()}/{count} mesaj aldı"); }
        Assert.Null(carol.Db.GetChatGroup(group.Id));

        // Carol çevrimiçi olur; Alice'in teslim döngüsü (burada 1:1 mesajla tetiklenir) grup kuyruğunu da boşaltır.
        // Testte UDP duyurusu yok: gerçek ağdaki 5 sn'lik duyuruların yerine kayıtlar canlı tutulur (yoksa ~20 sn'de düşer).
        using var keepAlive = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!keepAlive.IsCancellationRequested)
            {
                Introduce(alice, bob, carol);
                await Task.Delay(2000);
            }
        });
        try
        {
            alice.Chat.SendText(carol.Id.PeerId, "geldin mi?");
            int CarolCount() => carol.Db.LoadHistory(group.ConversationId, 300).Count(m => FromAlice(m));
            try { await WaitUntil(() => CarolCount() == count, 90000); }
            catch (TimeoutException) { Assert.Fail($"Carol {CarolCount()}/{count} mesaj aldı, grup={(carol.Db.GetChatGroup(group.Id) != null)}"); }
            Assert.Equal("Mesaj 0", carol.Db.LoadHistory(group.ConversationId, 300).First(m => FromAlice(m)).Data.Text);
        }
        finally
        {
            keepAlive.Cancel();
        }
    }

    [Fact]
    public async Task Typing_ReachesPeer_IsThrottled_AndGroupRequiresMembership()
    {
        using var alice = new Peer("Alice");
        using var bob = new Peer("Bob");
        using var carol = new Peer("Carol");
        Introduce(alice, bob, carol);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(string Conversation, string From)>();
        bob.Chat.TypingReceived += (conversation, from) => seen.Enqueue((conversation, from));

        alice.Chat.NotifyTyping(bob.Id.PeerId);
        alice.Chat.NotifyTyping(bob.Id.PeerId); // 3 sn içinde ikinci çağrı gönderilmez
        await WaitUntil(() => seen.Count >= 1);
        await Task.Delay(500);
        Assert.Single(seen);
        Assert.Equal((alice.Id.PeerId, alice.Id.PeerId), seen.Single());

        // Grup: üye olan yazınca grup sohbetine düşer; üye olmayan Carol'ın göstergesi reddedilir.
        var group = alice.Chat.CreateChatGroup("Ekip", [bob.Id.PeerId])!;
        await WaitUntil(() => bob.Db.GetChatGroup(group.Id) != null);
        alice.Chat.NotifyTyping(group.ConversationId);
        await WaitUntil(() => seen.Any(s => s.Conversation == group.ConversationId));
        carol.Db.UpsertChatGroup(group); // Carol grubu biliyor ama Bob'un kaydında üye değil
        carol.Chat.NotifyTyping(group.ConversationId);
        await Task.Delay(800);
        Assert.DoesNotContain(seen, s => s.From == carol.Id.PeerId);
    }

    [Fact]
    public async Task Reply_IsCarried_InDirectAndGroupMessages_AndInvalidReferenceIsDropped()
    {
        using var alice = new Peer("Alice");
        using var bob = new Peer("Bob");
        Introduce(alice, bob);

        var original = bob.Chat.SendText(alice.Id.PeerId, "Toplantı kaçta?")!;
        await WaitUntil(() => alice.Db.GetMessage(bob.Id.PeerId, original.MsgId, MessageDirection.Incoming) != null);

        var reply = alice.Chat.SendText(bob.Id.PeerId, "Üçte", new ReplyRef(original.MsgId, bob.Id.PeerId, "Toplantı kaçta?"))!;
        await WaitUntil(() => bob.Db.GetMessage(alice.Id.PeerId, reply.MsgId, MessageDirection.Incoming) != null);
        var received = bob.Db.GetMessage(alice.Id.PeerId, reply.MsgId, MessageDirection.Incoming)!;
        Assert.Equal(original.MsgId, received.Data.ReplyToMsgId);
        Assert.Equal(bob.Id.PeerId, received.Data.ReplyToSender);
        Assert.Equal("Toplantı kaçta?", received.Data.ReplyToText);

        var group = alice.Chat.CreateChatGroup("Ekip", [bob.Id.PeerId])!;
        await WaitUntil(() => bob.Db.GetChatGroup(group.Id) != null);
        var groupReply = alice.Chat.SendGroupText(group.Id, "Grupta yanıt", new ReplyRef(original.MsgId, bob.Id.PeerId, "Toplantı kaçta?"))!;
        await WaitUntil(() => bob.Db.GetMessage(group.ConversationId, groupReply.MsgId, MessageDirection.Incoming) != null);
        Assert.Equal(original.MsgId, bob.Db.GetMessage(group.ConversationId, groupReply.MsgId, MessageDirection.Incoming)!.Data.ReplyToMsgId);

        // Geçersiz başvuru (bozuk kimlik) yok sayılır, mesajın kendisi yine kabul edilir.
        Assert.Null(ReplyRef.TryCreate("../../x", bob.Id.PeerId, "a"));
        Assert.Null(ReplyRef.TryCreate(original.MsgId, "kimlik-değil", "a"));
        Assert.Equal(ReplyRef.MaxQuoteLength, ReplyRef.TryCreate(original.MsgId, bob.Id.PeerId, new string('x', 500))!.Text.Length);
    }

    [Fact]
    public async Task ReadReceipt_CannotMarkSomeoneElsesConversation()
    {
        using var alice = new Peer("Alice");
        using var bob = new Peer("Bob");
        using var carol = new Peer("Carol");
        Introduce(alice, bob, carol);

        // Alice → Carol mesajı; Bob kendi 1:1 sohbetinde bu kimliği "okudum" diye bildirse de işaretlenmemeli.
        var toCarol = alice.Chat.SendText(carol.Id.PeerId, "Yalnız Carol")!;
        StoredMessage Current() => alice.Db.GetMessage(carol.Id.PeerId, toCarol.MsgId, MessageDirection.Outgoing)!;
        await WaitUntil(() => alice.Chat.GetReceiptStatus(Current()).State == ReceiptState.Delivered);

        bob.Db.InsertMessage(new StoredMessage
        {
            PeerId = alice.Id.PeerId, MsgId = toCarol.MsgId, Direction = MessageDirection.Incoming, Kind = MessageKind.Text,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), State = MessageState.Delivered, Data = new MessageData { Text = "sahte" },
        });
        bob.Chat.SendReadReceipts(alice.Id.PeerId);
        await WaitUntil(() => bob.Db.GetReceiptsSentRowId(alice.Id.PeerId) > 0);
        await Task.Delay(300);

        Assert.Equal(ReceiptState.Delivered, alice.Chat.GetReceiptStatus(Current()).State);
    }
}
