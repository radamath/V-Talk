using System.IO;
using VTalk.Core;
using VTalk.Data;
using VTalk.Security;

namespace VTalk.Tests;

public class SearchTests
{
    private const string Ali = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Veli = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static void Add(Database db, string peer, string text, MessageKind kind = MessageKind.Text) =>
        db.InsertMessage(new StoredMessage
        {
            PeerId = peer, MsgId = CryptoUtil.NewId(), Direction = MessageDirection.Incoming, Kind = kind,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), State = MessageState.Delivered,
            Data = kind == MessageKind.File ? new MessageData { FileName = text } : new MessageData { Text = text },
        });

    [Fact]
    public void Search_IsTurkishCaseInsensitive_ScopedAndCoversFileNames()
    {
        using var protector = LocalProtector.LoadOrCreate();
        using var db = new Database(Path.Combine(AppPaths.DataDir, $"search-{Guid.NewGuid():N}.db"), protector);
        Add(db, Ali, "Toplantı İSTANBUL ofisinde");
        Add(db, Ali, "ıspanak tarifi");
        Add(db, Veli, "istanbul'a gidiyorum");
        Add(db, Veli, "Bütçe_Raporu_2026.xlsx", MessageKind.File);

        // Türkçe İ/i ve I/ı eşleşmesi: "istanbul" İSTANBUL'u bulur, "ISPANAK" ıspanak'ı bulur.
        Assert.Equal(2, db.SearchMessages(null, "istanbul", 50, 1000).Count);
        Assert.Single(db.SearchMessages(null, "ISPANAK", 50, 1000));
        Assert.Single(db.SearchMessages(Ali, "istanbul", 50, 1000));
        Assert.Single(db.SearchMessages(null, "bütçe", 50, 1000));
        Assert.Empty(db.SearchMessages(null, "   ", 50, 1000));
        Assert.Equal(9, Database.MatchIndex("Toplantı İSTANBUL ofisinde", "istanbul"));
    }

    [Fact]
    public void Search_CrossesBatches_NewestFirst_AndHonorsLimits()
    {
        using var protector = LocalProtector.LoadOrCreate();
        using var db = new Database(Path.Combine(AppPaths.DataDir, $"search-{Guid.NewGuid():N}.db"), protector);
        Add(db, Ali, "en eski anahtar sözcük");
        for (var i = 0; i < 2500; i++) Add(db, Ali, $"dolgu mesajı {i}");
        Add(db, Ali, "en yeni anahtar sözcük");

        var all = db.SearchMessages(Ali, "anahtar", 50, 10_000);
        Assert.Equal(["en yeni anahtar sözcük", "en eski anahtar sözcük"], all.Select(m => m.Data.Text));
        Assert.Single(db.SearchMessages(Ali, "anahtar", 1, 10_000));
        // Taranan satır sınırı: yalnızca en yeni 1000 satıra bakılırsa en eski eşleşme bulunmaz.
        Assert.Single(db.SearchMessages(Ali, "anahtar", 50, 1000));
    }
}
