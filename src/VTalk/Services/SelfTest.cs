using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using VTalk.Core;
using VTalk.Data;
using VTalk.Net;
using VTalk.Security;

namespace VTalk.Services;

/// <summary>
/// "VTalk.exe --selftest [rapor.txt]": yayımlanan (karartılmış) exe'nin uçtan uca çalıştığını doğrular.
/// Geçici, ayrı bir profil kullanır; iki örnek yalnızca 127.0.0.1 üzerinden konuşur, yerel ağa hiçbir şey yayınlamaz.
/// Çıkış kodu 0 = başarılı.
/// </summary>
internal static class SelfTest
{
    private const string TinyPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    public static int Run(string? reportPath)
    {
        var report = new StringBuilder();
        int code;
        try
        {
            code = Task.Run(() => RunAsync(report)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            report.AppendLine("HATA: " + ex);
            code = 99;
        }
        report.AppendLine(code == 0 ? "SONUÇ: BAŞARILI" : $"SONUÇ: BAŞARISIZ ({code})");
        if (!string.IsNullOrEmpty(reportPath))
        {
            try { File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8); } catch { }
        }
        try { Directory.Delete(AppPaths.DataDir, recursive: true); } catch { }
        return code;
    }

    private static async Task<int> RunAsync(StringBuilder r)
    {
        AppPaths.EnsureCreated();
        int step = 0;
        void Ok(string what) => r.AppendLine($"[{++step}] OK  {what}");

        // 1) Ayarlar JSON gidiş-dönüş (özellik adları korunmalı)
        var s = new Settings { DisplayName = "Öz Sınama", ShowMessagePreview = false, AutoAcceptMaxMb = 7 };
        s.Save();
        var loaded = Settings.Load();
        if (loaded.DisplayName != "Öz Sınama" || loaded.ShowMessagePreview || loaded.AutoAcceptMaxMb != 7) return Fail(r, 1, "Ayarlar");
        Ok("Ayarlar kaydet/yükle");

        // 2) Yerel şifreleme + veritabanı
        using var protector = LocalProtector.LoadOrCreate();
        using (var db = new Database(Path.Combine(AppPaths.DataDir, "st.db"), protector))
        {
            var m = new StoredMessage
            {
                PeerId = new string('c', 32), MsgId = "st1", Direction = MessageDirection.Incoming, Kind = MessageKind.File,
                Timestamp = 1, State = MessageState.Delivered,
                Data = new MessageData { FileName = "rapor.pdf", FileSize = 42, FileState = FileState.Completed },
            };
            if (!db.InsertMessage(m)) return Fail(r, 2, "Veritabanı ekleme");
            var h = db.LoadHistory(m.PeerId, 5);
            if (h.Count != 1 || h[0].Data.FileName != "rapor.pdf" || h[0].Data.FileState != FileState.Completed) return Fail(r, 2, "Veritabanı okuma");
        }
        Ok("Şifreli veritabanı");

        // 3) İmzalı dizin (sıkı mod bayrağı dahil) başka bir örnekte doğrulanmalı
        using (var org = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var a = new DirectoryService();
            a.PinOrgKey(org.ExportSubjectPublicKeyInfo());
            var member = new string('d', 32);
            var signed = a.SignAndApply(new OrgDirectory
            {
                MembersOnly = true,
                Units = [new OrgUnit { Id = "it", Name = "BT" }],
                Members = [new OrgMember { PeerId = member, Name = "Müdür", Role = Role.UnitManager, UnitId = "it" }],
            }, org);
            var b = new DirectoryService();
            b.PinOrgKey(org.ExportSubjectPublicKeyInfo());
            if (!b.TryApply(signed, out var err) || !b.MembersOnly || b.GetRole(member) != Role.UnitManager)
                return Fail(r, 3, "Dizin doğrulama: " + err);
        }
        Ok("İmzalı dizin + sıkı mod");

        // 4) Admin anahtar deposu
        using (var key = AdminKeyStore.CreateOrganizationKey("Oz.Sinama-2026!"))
        using (var unlocked = AdminKeyStore.Unlock("Oz.Sinama-2026!"))
        {
            if (!key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(unlocked.ExportSubjectPublicKeyInfo())) return Fail(r, 4, "Admin anahtarı");
        }
        Ok("Admin anahtarı oluştur/aç");

        // 5) İki örnek arasında şifreli kanal: mesaj, resim (otomatik), dosya (onaylı)
        using var idA = Identity.CreateEphemeral();
        using var idB = Identity.CreateEphemeral();
        using var dbA = new Database(Path.Combine(AppPaths.DataDir, "a.db"), protector);
        using var dbB = new Database(Path.Combine(AppPaths.DataDir, "b.db"), protector);
        var dlB = Path.Combine(AppPaths.DataDir, "dl-b");
        using var chatA = new ChatService(idA, new Settings { DisplayName = "A", PreferredTcpPort = 0, DownloadFolder = Path.Combine(AppPaths.DataDir, "dl-a") },
            dbA, new DirectoryService(), loopbackOnly: true);
        using var chatB = new ChatService(idB, new Settings { DisplayName = "B", PreferredTcpPort = 0, DownloadFolder = dlB, AutoAcceptImages = true },
            dbB, new DirectoryService(), loopbackOnly: true);
        chatA.Start();
        chatB.Start();

        void Introduce()
        {
            chatA.Registry.Upsert(new PeerSnapshot
            {
                PeerId = idB.PeerId, PublicKey = idB.PublicKey, Name = "B", Status = PresenceStatus.Online,
                Address = IPAddress.Loopback, Port = chatB.TcpPort, LastSeenUtc = DateTime.UtcNow,
            });
            chatB.Registry.Upsert(new PeerSnapshot
            {
                PeerId = idA.PeerId, PublicKey = idA.PublicKey, Name = "A", Status = PresenceStatus.Online,
                Address = IPAddress.Loopback, Port = chatA.TcpPort, LastSeenUtc = DateTime.UtcNow,
            });
        }

        Introduce();
        StoredMessage? text = null, image = null, file = null;
        chatB.MessageAdded += m =>
        {
            if (m.Kind == MessageKind.Text) text = m;
            else if (m.Kind == MessageKind.File && m.Data.IsImage) image = m;
            else if (m.Kind == MessageKind.File) file = m;
        };

        var sent = chatA.SendText(idB.PeerId, "Merhaba, öz sınama! 🔐")!;
        if (await WaitFor(() => text) is not { Data.Text: "Merhaba, öz sınama! 🔐" }) return Fail(r, 5, "Mesaj alınmadı");
        if (await WaitFor(() => dbA.GetMessage(idB.PeerId, sent.MsgId, MessageDirection.Outgoing) is { State: MessageState.Delivered } x ? x : null) == null)
            return Fail(r, 5, "Teslim onayı yok");
        Ok("Şifreli mesaj + teslim onayı");

        Introduce();
        var png = Path.Combine(AppPaths.DataDir, "resim.png");
        await File.WriteAllBytesAsync(png, Convert.FromBase64String(TinyPng));
        await chatA.SendFileAsync(idB.PeerId, png);
        var io = await WaitFor(() => image);
        if (io == null) return Fail(r, 6, "Resim teklifi alınmadı");
        var img = await WaitFor(() => dbB.GetMessage(idA.PeerId, io.MsgId, MessageDirection.Incoming) is { Data.FileState: FileState.Completed } x ? x : null);
        if (img is not { Data.IsImage: true }) return Fail(r, 6, "Resim otomatik alınmadı / doğrulanmadı");
        Ok("Resim otomatik alma + format doğrulama");

        Introduce();
        var bin = Path.Combine(AppPaths.DataDir, "veri.bin");
        var content = RandomNumberGenerator.GetBytes(700 * 1024);
        await File.WriteAllBytesAsync(bin, content);
        await chatA.SendFileAsync(idB.PeerId, bin);
        var fo = await WaitFor(() => file);
        if (fo == null) return Fail(r, 7, "Dosya teklifi alınmadı");
        chatB.Files.Accept(fo);
        var done = await WaitFor(() => dbB.GetMessage(idA.PeerId, fo.MsgId, MessageDirection.Incoming) is { Data.FileState: FileState.Completed } x ? x : null);
        if (done?.Data.FilePath == null || !(await File.ReadAllBytesAsync(done.Data.FilePath)).AsSpan().SequenceEqual(content))
            return Fail(r, 7, "Dosya aktarımı / bütünlük");
        r.AppendLine(File.Exists(done.Data.FilePath + ":Zone.Identifier") ? "     (MOTW eklendi)" : "     (uyarı: MOTW doğrulanamadı)");
        Ok("Dosya aktarımı + SHA-256");

        return 0;
    }

    private static int Fail(StringBuilder r, int code, string what)
    {
        r.AppendLine($"[{code}] HATA {what}");
        return code;
    }

    private static async Task<T?> WaitFor<T>(Func<T?> probe, int timeoutMs = 15000) where T : class
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } v) return v;
            await Task.Delay(100);
        }
        return null;
    }
}
