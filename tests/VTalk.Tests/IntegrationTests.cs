using System.IO;
using System.Net;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VTalk.Core;
using VTalk.Data;
using VTalk.Net;
using VTalk.Security;
using VTalk.Services;

namespace VTalk.Tests;

public class IntegrationTests
{
    private sealed class Peer : IDisposable
    {
        public Identity Id { get; } = Identity.CreateEphemeral();
        public Database Db { get; }
        public ChatService Chat { get; }
        public string Name { get; }
        public string Downloads { get; }
        private readonly LocalProtector _protector = LocalProtector.LoadOrCreate();

        public Peer(string name)
        {
            Name = name;
            Downloads = Path.Combine(AppPaths.DataDir, "dl-" + Guid.NewGuid().ToString("N"));
            Db = new Database(Path.Combine(AppPaths.DataDir, $"it-{name}-{Guid.NewGuid():N}.db"), _protector);
            // Testler yerel makinenin ağ yapılandırmasından bağımsız olmalı: yalnızca 127.0.0.1 kullanılır ve
            // eşler Introduce ile elle tanıtılır (yerel ağ dinlenmez, gerçek duyurular karışmaz).
            var settings = new Settings { DisplayName = name, PreferredTcpPort = 0, DownloadFolder = Downloads, AutoAcceptImages = true };
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

    // Eşleri birbirine 127.0.0.1 üzerinden elle tanıtır (yerel ağ keşfi yerine, ortamdan bağımsız test için).
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

    private static async Task<T> WaitFor<T>(Func<T?> probe, int timeoutMs = 20000) where T : class
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            var v = probe();
            if (v != null) return v;
            await Task.Delay(100);
        }
        throw new TimeoutException("Beklenen durum oluşmadı.");
    }

    [Fact]
    public async Task TwoPeers_Discover_Message_File_AndImage()
    {
        using var alice = new Peer("Alice");
        using var bob = new Peer("Bob");

        // İki taraf birbirini tanır (127.0.0.1 üzerinden).
        Introduce(alice, bob);
        Assert.True(alice.Chat.Registry.Get(bob.Id.PeerId) is { IsOnline: true });
        Assert.Equal("Alice", bob.Chat.Registry.Get(alice.Id.PeerId)!.Name);

        // Metin mesajı + teslim onayı
        StoredMessage? received = null;
        bob.Chat.MessageAdded += m => { if (m.Kind == MessageKind.Text) received = m; };
        var sent = alice.Chat.SendText(bob.Id.PeerId, "Merhaba Bob! 👋")!;
        var got = await WaitFor(() => received);
        Assert.Equal("Merhaba Bob! 👋", got.Data.Text);
        Assert.Equal(alice.Id.PeerId, got.PeerId);
        await WaitFor(() => alice.Db.GetMessage(bob.Id.PeerId, sent.MsgId, MessageDirection.Outgoing) is { State: MessageState.Delivered } m ? m : null);

        // Dosya: teklif -> kabul -> SHA-256 doğrulamalı aktarım
        var srcFile = Path.Combine(AppPaths.DataDir, $"rapor-{Guid.NewGuid():N}.bin");
        var content = new byte[3 * 1024 * 1024 + 123];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(srcFile, content);

        StoredMessage? offer = null;
        bob.Chat.MessageAdded += m => { if (m.Kind == MessageKind.File && !m.Data.IsImage) offer = m; };
        Introduce(alice, bob);
        await alice.Chat.SendFileAsync(bob.Id.PeerId, srcFile);
        var o = await WaitFor(() => offer);
        Assert.Equal(FileState.Offered, o.Data.FileState);
        bob.Chat.Files.Accept(o);
        var done = await WaitFor(() => bob.Db.GetMessage(alice.Id.PeerId, o.MsgId, MessageDirection.Incoming) is { Data.FileState: FileState.Completed } m ? m : null);
        Assert.Equal(content, await File.ReadAllBytesAsync(done.Data.FilePath!));
        Assert.StartsWith(Path.GetFullPath(bob.Downloads), Path.GetFullPath(done.Data.FilePath!));
        Assert.True(File.Exists(done.Data.FilePath + ":Zone.Identifier"));

        // Resim: otomatik kabul edilip satır içinde gösterilebilir olmalı
        var png = Path.Combine(AppPaths.DataDir, $"resim-{Guid.NewGuid():N}.png");
        var bmp = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 4 * 4], 16);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        await using (var fs = File.Create(png)) enc.Save(fs);

        StoredMessage? imageOffer = null;
        bob.Chat.MessageAdded += m => { if (m.Kind == MessageKind.File && m.Data.IsImage) imageOffer = m; };
        Introduce(alice, bob);
        await alice.Chat.SendFileAsync(bob.Id.PeerId, png);
        var io = await WaitFor(() => imageOffer);
        var img = await WaitFor(() => bob.Db.GetMessage(alice.Id.PeerId, io.MsgId, MessageDirection.Incoming) is { Data.FileState: FileState.Completed } m ? m : null);
        Assert.True(img.Data.IsImage);

        // Yetkisiz duyuru: dizin yokken kimse duyuru gönderemez
        Assert.False(alice.Chat.CanSendBulletin(ChatService.ScopeAll));
        Assert.False(bob.Chat.IsBulletinAuthorized(alice.Id.PeerId, ChatService.ScopeAll));
    }

    [Fact]
    public async Task AdminDirectory_Propagates_AndBulletinsAreRoleChecked()
    {
        using var admin = new Peer("Admin");
        using var manager = new Peer("Mudur");
        using var user = new Peer("Kullanici");
        Introduce(admin, manager, user);

        using var orgKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        admin.Chat.Directory.PinOrgKey(orgKey.ExportSubjectPublicKeyInfo());
        var signed = admin.Chat.Directory.SignAndApply(new OrgDirectory
        {
            Units = [new OrgUnit { Id = "it", Name = "Bilgi İşlem" }, new OrgUnit { Id = "ik", Name = "İnsan Kaynakları" }],
            Members =
            [
                new OrgMember { PeerId = admin.Id.PeerId, Name = "Admin", Role = Role.Admin },
                new OrgMember { PeerId = manager.Id.PeerId, Name = "Müdür", Role = Role.UnitManager, UnitId = "it" },
                new OrgMember { PeerId = user.Id.PeerId, Name = "Kullanıcı", Role = Role.User, UnitId = "ik" },
            ],
        }, orgKey);

        // İmzalı dizin diğer eşlerde doğrulanıp uygulanabilmeli.
        Assert.True(manager.Chat.Directory.TryApply(signed, out _));
        Assert.True(user.Chat.Directory.TryApply(signed, out _));
        Assert.Equal(Role.UnitManager, user.Chat.Directory.GetRole(manager.Id.PeerId));

        Assert.True(admin.Chat.CanSendBulletin(ChatService.ScopeAll));
        Assert.True(manager.Chat.CanSendBulletin(ChatService.ScopeUnit));
        Assert.False(manager.Chat.CanSendBulletin(ChatService.ScopeAll));
        Assert.False(user.Chat.CanSendBulletin(ChatService.ScopeUnit));
        Assert.Throws<UnauthorizedAccessException>(() => user.Chat.SendBulletin(ChatService.ScopeAll, "sahte duyuru"));

        // Alıcı tarafı denetimi: farklı birimdeki müdürün birim duyurusu kabul edilmez.
        Assert.False(user.Chat.IsBulletinAuthorized(manager.Id.PeerId, ChatService.ScopeUnit));
        Assert.True(user.Chat.IsBulletinAuthorized(admin.Id.PeerId, ChatService.ScopeAll));

        StoredMessage? bulletin = null;
        user.Chat.BulletinReceived += m => bulletin = m;
        admin.Chat.SendBulletin(ChatService.ScopeAll, "Yarın sistem bakımı var.");
        var b = await WaitFor(() => bulletin);
        Assert.Equal("Yarın sistem bakımı var.", b.Data.Text);
        Assert.Equal(admin.Id.PeerId, b.PeerId);
    }
}
