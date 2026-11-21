using System.Text.Json;
using Microsoft.Data.Sqlite;
using VTalk.Core;

namespace VTalk.Data;

public sealed class ContactRecord
{
    public string PeerId { get; set; } = "";
    public byte[] PublicKey { get; set; } = [];
    public string Name { get; set; } = "";
    public string User { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Note { get; set; } = "";
    public string? AvatarHash { get; set; }
    public long LastSeen { get; set; }
    /// <summary>İmzalı duyurudan öğrenilen son TCP uç noktası (yeniden başlatmadan sonra erişilebilirlik yoklaması için).</summary>
    public string? Address { get; set; }
    public int Port { get; set; }
}

/// <summary>
/// Yerel sohbet geçmişi. Mesaj içerikleri (metin, dosya adı/yolu) AES-GCM ile şifrelenir;
/// ilişkilendirilmiş veri (AAD) olarak satır kimliği bilgisi kullanılır, böylece satırlar arası kopyalama tespit edilir.
/// Tüm sorgular parametrelidir (SQL enjeksiyonu yok).
/// </summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly LocalProtector _protector;
    private readonly object _gate = new();

    public Database(string path, LocalProtector protector)
    {
        _protector = protector;
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA foreign_keys=ON;");
        Exec("PRAGMA secure_delete=ON;");
        Migrate();
    }

    private void Migrate()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS contacts (
                peer_id     TEXT PRIMARY KEY,
                public_key  BLOB NOT NULL,
                info        BLOB NOT NULL,
                last_seen   INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS messages (
                row_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                peer_id     TEXT NOT NULL,
                msg_id      TEXT NOT NULL,
                direction   INTEGER NOT NULL,
                kind        INTEGER NOT NULL,
                ts          INTEGER NOT NULL,
                state       INTEGER NOT NULL,
                data        BLOB NOT NULL,
                UNIQUE(peer_id, msg_id, direction)
            );
            CREATE INDEX IF NOT EXISTS ix_messages_peer ON messages(peer_id, row_id);
            CREATE INDEX IF NOT EXISTS ix_messages_pending ON messages(state, direction);
            CREATE TABLE IF NOT EXISTS chat_groups (
                group_id    TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                created_by  TEXT NOT NULL,
                created_ts  INTEGER NOT NULL,
                members     BLOB NOT NULL
            );
            """);
        TryAddColumn("chat_groups", "temporary", "INTEGER NOT NULL DEFAULT 0");
        Exec("""
            CREATE TABLE IF NOT EXISTS conversation_reads (
                peer_id TEXT PRIMARY KEY,
                last_read_row_id INTEGER NOT NULL DEFAULT 0
            );
            """);
        // Giden mesajların alıcı başına teslim/okunma zamanı (1:1'de alıcı karşı taraf, grupta her üye).
        // receipts_sent: bu sohbette okundu bildirimi gönderilen son gelen mesajın satırı.
        Exec("""
            CREATE TABLE IF NOT EXISTS message_receipts (
                peer_id      TEXT NOT NULL,
                msg_id       TEXT NOT NULL,
                member       TEXT NOT NULL,
                delivered_ts INTEGER NOT NULL DEFAULT 0,
                read_ts      INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(peer_id, msg_id, member)
            );
            CREATE TABLE IF NOT EXISTS receipts_sent (
                peer_id TEXT PRIMARY KEY,
                row_id  INTEGER NOT NULL DEFAULT 0
            );
            """);
    }

    // ---- Teslim / okundu bilgisi ----

    /// <summary>Alıcının teslim aldığını ve isteğe bağlı olarak okuduğunu kaydeder; zamanlar yalnızca ileri gider.</summary>
    public void MarkReceipt(string conversationId, string msgId, string member, bool read)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO message_receipts(peer_id, msg_id, member, delivered_ts, read_ts) VALUES($p, $m, $who, $now, $read)
                ON CONFLICT(peer_id, msg_id, member) DO UPDATE SET
                  delivered_ts = CASE WHEN message_receipts.delivered_ts > 0 THEN message_receipts.delivered_ts ELSE excluded.delivered_ts END,
                  read_ts = CASE WHEN message_receipts.read_ts > 0 THEN message_receipts.read_ts ELSE excluded.read_ts END;
                """;
            cmd.Parameters.AddWithValue("$p", conversationId);
            cmd.Parameters.AddWithValue("$m", msgId);
            cmd.Parameters.AddWithValue("$who", member);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$read", read ? now : 0);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Bir mesajın alıcı başına durumu: üye → (teslim, okundu).</summary>
    public Dictionary<string, (bool Delivered, bool Read)> GetReceipts(string conversationId, string msgId)
    {
        var result = new Dictionary<string, (bool, bool)>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT member, delivered_ts, read_ts FROM message_receipts WHERE peer_id = $p AND msg_id = $m;";
            cmd.Parameters.AddWithValue("$p", conversationId);
            cmd.Parameters.AddWithValue("$m", msgId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[r.GetString(0)] = (r.GetInt64(1) > 0 || r.GetInt64(2) > 0, r.GetInt64(2) > 0);
        }
        return result;
    }

    public long GetReceiptsSentRowId(string conversationId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT row_id FROM receipts_sent WHERE peer_id = $p;";
            cmd.Parameters.AddWithValue("$p", conversationId);
            return cmd.ExecuteScalar() is long l ? l : 0;
        }
    }

    public void SetReceiptsSentRowId(string conversationId, long rowId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO receipts_sent(peer_id, row_id) VALUES($p, $r)
                ON CONFLICT(peer_id) DO UPDATE SET row_id = max(receipts_sent.row_id, excluded.row_id);
                """;
            cmd.Parameters.AddWithValue("$p", conversationId);
            cmd.Parameters.AddWithValue("$r", rowId);
            cmd.ExecuteNonQuery();
        }
    }

    private const int SearchBatchSize = 1000;
    private static readonly System.Globalization.CompareInfo SearchCompare = System.Globalization.CultureInfo.GetCultureInfo("tr-TR").CompareInfo;

    /// <summary>
    /// Mesajlarda arama (yeniden eskiye). İçerik şifreli saklandığı için SQL ile aranamaz: satırlar 1000'erli
    /// gruplar halinde çözülüp süzülür; kilit yalnızca grup okunurken tutulur, gelen mesajlar beklemez.
    /// Büyük/küçük harf ve Türkçe İ/ı duyarsızdır. Metin, dosya adı ve duyurular aranır.
    /// </summary>
    public List<StoredMessage> SearchMessages(string? conversationId, string query, int maxResults, int maxScanned, CancellationToken ct = default)
    {
        var results = new List<StoredMessage>();
        query = query.Trim();
        if (query.Length == 0) return results;
        var before = long.MaxValue;
        var scanned = 0;
        while (results.Count < maxResults && scanned < maxScanned && !ct.IsCancellationRequested)
        {
            var batch = new List<StoredMessage>();
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = conversationId == null
                    ? "SELECT row_id, peer_id, msg_id, direction, kind, ts, state, data FROM messages WHERE row_id < $before ORDER BY row_id DESC LIMIT $limit;"
                    : "SELECT row_id, peer_id, msg_id, direction, kind, ts, state, data FROM messages WHERE peer_id = $p AND row_id < $before ORDER BY row_id DESC LIMIT $limit;";
                cmd.Parameters.AddWithValue("$before", before);
                cmd.Parameters.AddWithValue("$limit", SearchBatchSize);
                if (conversationId != null) cmd.Parameters.AddWithValue("$p", conversationId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    before = r.GetInt64(0);
                    if (ReadMessage(r) is { } m) batch.Add(m);
                }
            }
            if (batch.Count == 0) break;
            scanned += batch.Count;
            foreach (var m in batch)
            {
                if (!Matches(m, query)) continue;
                results.Add(m);
                if (results.Count >= maxResults) break;
            }
        }
        return results;
    }

    private static bool Matches(StoredMessage m, string query)
    {
        var text = m.Kind == MessageKind.File ? m.Data.FileName : m.Data.Text;
        return text != null && SearchCompare.IndexOf(text, query, System.Globalization.CompareOptions.IgnoreCase) >= 0;
    }

    /// <summary>Aramada eşleşmenin metindeki konumu (vurgu ve alıntı için); yoksa -1.</summary>
    public static int MatchIndex(string text, string query) =>
        SearchCompare.IndexOf(text, query.Trim(), System.Globalization.CompareOptions.IgnoreCase);

    /// <summary>
    /// Grup çevrimdışı kuyruğu: üyenin henüz teslim almadığı giden grup metinleri (eskiden yeniye). Teslim bilgisi
    /// message_receipts'ten gelir; üye onaylayınca kuyruktan kendiliğinden düşer.
    /// </summary>
    public List<StoredMessage> LoadUndeliveredGroupMessages(string conversationId, string member, long sinceTs, int limit)
    {
        var list = new List<StoredMessage>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT row_id, peer_id, msg_id, direction, kind, ts, state, data FROM messages m
                WHERE m.peer_id = $p AND m.direction = $dir AND m.kind = $text AND m.ts >= $since
                  AND NOT EXISTS (SELECT 1 FROM message_receipts r
                                  WHERE r.peer_id = m.peer_id AND r.msg_id = m.msg_id AND r.member = $who
                                    AND (r.delivered_ts > 0 OR r.read_ts > 0))
                ORDER BY m.row_id LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$p", conversationId);
            cmd.Parameters.AddWithValue("$dir", (int)MessageDirection.Outgoing);
            cmd.Parameters.AddWithValue("$text", (int)MessageKind.Text);
            cmd.Parameters.AddWithValue("$since", sinceTs);
            cmd.Parameters.AddWithValue("$who", member);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (ReadMessage(r) is { } m) list.Add(m);
        }
        return list;
    }

    /// <summary>Okundu bildirimi henüz gönderilmemiş gelen metin/dosya mesajları (eskiden yeniye).</summary>
    public List<StoredMessage> LoadIncomingAfter(string conversationId, long afterRowId, int limit)
    {
        var list = new List<StoredMessage>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT row_id, peer_id, msg_id, direction, kind, ts, state, data FROM messages
                WHERE peer_id = $p AND row_id > $after AND direction = $dir AND kind IN ($text, $file)
                ORDER BY row_id LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$p", conversationId);
            cmd.Parameters.AddWithValue("$after", afterRowId);
            cmd.Parameters.AddWithValue("$dir", (int)MessageDirection.Incoming);
            cmd.Parameters.AddWithValue("$text", (int)MessageKind.Text);
            cmd.Parameters.AddWithValue("$file", (int)MessageKind.File);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (ReadMessage(r) is { } m) list.Add(m);
        }
        return list;
    }

    public long GetLastReadRowId(string peerId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT last_read_row_id FROM conversation_reads WHERE peer_id = $p;";
            cmd.Parameters.AddWithValue("$p", peerId);
            var v = cmd.ExecuteScalar();
            return v is long l ? l : v is int i ? i : 0;
        }
    }

    public long GetMaxMessageRowId(string peerId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT IFNULL(MAX(row_id), 0) FROM messages WHERE peer_id = $p;";
            cmd.Parameters.AddWithValue("$p", peerId);
            var v = cmd.ExecuteScalar();
            return v is long l ? l : Convert.ToInt64(v ?? 0);
        }
    }

    public void SetLastReadRowId(string peerId, long rowId)
    {
        if (rowId < 0) rowId = 0;
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO conversation_reads(peer_id, last_read_row_id) VALUES($p, $r)
                ON CONFLICT(peer_id) DO UPDATE SET
                  last_read_row_id = max(conversation_reads.last_read_row_id, excluded.last_read_row_id);
                """;
            cmd.Parameters.AddWithValue("$p", peerId);
            cmd.Parameters.AddWithValue("$r", rowId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Şema yükseltmesi; tablo/sütun adları sabit koddan gelir, kullanıcı girdisi değildir.</summary>
    private void TryAddColumn(string table, string column, string decl)
    {
        if (ColumnExists(table, column)) return;
        Exec($"ALTER TABLE {table} ADD COLUMN {column} {decl};");
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
        cmd.Parameters.AddWithValue("$table", table);
        cmd.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    // ---- Sohbet grupları ----

    public List<ChatGroup> ListChatGroups()
    {
        var list = new List<ChatGroup>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT group_id, name, created_by, created_ts, members, IFNULL(temporary,0) FROM chat_groups ORDER BY name COLLATE NOCASE;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var g = ReadChatGroup(r);
                if (g != null) list.Add(g);
            }
        }
        return list;
    }

    public ChatGroup? GetChatGroup(string groupId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT group_id, name, created_by, created_ts, members, IFNULL(temporary,0) FROM chat_groups WHERE group_id = $id;";
            cmd.Parameters.AddWithValue("$id", groupId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadChatGroup(r) : null;
        }
    }

    public void UpsertChatGroup(ChatGroup group)
    {
        var members = _protector.EncryptString(JsonSerializer.Serialize(group.MemberIds), "chatgroup:" + group.Id);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO chat_groups(group_id, name, created_by, created_ts, members, temporary)
                VALUES($id, $name, $by, $ts, $members, $tmp)
                ON CONFLICT(group_id) DO UPDATE SET
                  name = $name,
                  members = $members,
                  temporary = CASE WHEN chat_groups.temporary != 0 OR $tmp != 0 THEN 1 ELSE 0 END;
                """;
            cmd.Parameters.AddWithValue("$id", group.Id);
            cmd.Parameters.AddWithValue("$name", group.Name);
            cmd.Parameters.AddWithValue("$by", group.CreatedBy);
            cmd.Parameters.AddWithValue("$ts", group.CreatedUtc);
            cmd.Parameters.AddWithValue("$members", members);
            cmd.Parameters.AddWithValue("$tmp", group.Temporary ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    /// <param name="keepMessages">
    /// Uzaktan çıkarılmada true: grup listeden kalkar, geçmiş korunur ve aynı gruba yeniden davet edilince görünür.
    /// Kullanıcı gruptan kendisi ayrıldığında false: geçmiş de silinir.
    /// </param>
    public void DeleteChatGroup(string groupId, bool keepMessages = false)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            if (!keepMessages)
            {
                cmd.CommandText = "DELETE FROM messages WHERE peer_id = $conv;";
                cmd.Parameters.AddWithValue("$conv", ChatGroup.ToConversationId(groupId));
                cmd.ExecuteNonQuery();
            }
            cmd.CommandText = "DELETE FROM chat_groups WHERE group_id = $id;";
            cmd.Parameters.AddWithValue("$id", groupId);
            cmd.ExecuteNonQuery();
        }
    }

    private ChatGroup? ReadChatGroup(SqliteDataReader r)
    {
        try
        {
            var id = r.GetString(0);
            var members = JsonSerializer.Deserialize<List<string>>(
                _protector.DecryptString((byte[])r[4], "chatgroup:" + id)) ?? [];
            return new ChatGroup
            {
                Id = id,
                Name = r.GetString(1),
                CreatedBy = r.GetString(2),
                CreatedUtc = r.GetInt64(3),
                MemberIds = members,
                Temporary = r.FieldCount > 5 && !r.IsDBNull(5) && r.GetInt64(5) != 0,
            };
        }
        catch (Exception ex)
        {
            Log.Warn("Sohbet grubu okunamadı: " + ex.Message);
            return null;
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---- Kişiler ----

    private sealed class ContactInfo
    {
        public string Name { get; set; } = "";
        public string User { get; set; } = "";
        public string Machine { get; set; } = "";
        public string Note { get; set; } = "";
        public string? AvatarHash { get; set; }
        public string? Address { get; set; }
        public int Port { get; set; }
    }

    public void UpsertContact(ContactRecord c)
    {
        var info = JsonSerializer.Serialize(new ContactInfo
        {
            Name = c.Name, User = c.User, Machine = c.Machine, Note = c.Note, AvatarHash = c.AvatarHash,
            Address = c.Address, Port = c.Port,
        });
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO contacts(peer_id, public_key, info, last_seen) VALUES($id, $pk, $info, $seen)
                ON CONFLICT(peer_id) DO UPDATE SET info = excluded.info, last_seen = excluded.last_seen;
                """;
            cmd.Parameters.AddWithValue("$id", c.PeerId);
            cmd.Parameters.AddWithValue("$pk", c.PublicKey);
            cmd.Parameters.AddWithValue("$info", _protector.EncryptString(info, "contact:" + c.PeerId));
            cmd.Parameters.AddWithValue("$seen", c.LastSeen);
            cmd.ExecuteNonQuery();
        }
    }

    public List<ContactRecord> LoadContacts()
    {
        var list = new List<ContactRecord>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT peer_id, public_key, info, last_seen FROM contacts;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var peerId = r.GetString(0);
                var pk = (byte[])r[1];
                if (CryptoUtil.PeerIdFromPublicKey(pk) != peerId) continue;
                try
                {
                    var info = JsonSerializer.Deserialize<ContactInfo>(_protector.DecryptString((byte[])r[2], "contact:" + peerId)) ?? new();
                    list.Add(new ContactRecord
                    {
                        PeerId = peerId, PublicKey = pk, Name = info.Name, User = info.User,
                        Machine = info.Machine, Note = info.Note, AvatarHash = info.AvatarHash, LastSeen = r.GetInt64(3),
                        Address = info.Address, Port = info.Port,
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn($"Kişi kaydı çözülemedi ({peerId[..8]}): {ex.Message}");
                }
            }
        }
        return list;
    }

    /// <summary>Kişiyi ve onunla olan yerel mesaj kayıtlarını siler.</summary>
    public void DeleteContact(string peerId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM messages WHERE peer_id = $id;";
            cmd.Parameters.AddWithValue("$id", peerId);
            cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM contacts WHERE peer_id = $id;";
            cmd.ExecuteNonQuery();
        }
    }

    // ---- Mesajlar ----

    private static string Aad(string peerId, string msgId, MessageDirection dir) => $"msg:{peerId}:{msgId}:{(int)dir}";

    /// <summary>Mesajı ekler. Aynı mesaj zaten varsa (yinelenen teslim) false döner.</summary>
    public bool InsertMessage(StoredMessage m)
    {
        var data = _protector.EncryptString(JsonSerializer.Serialize(m.Data), Aad(m.PeerId, m.MsgId, m.Direction));
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO messages(peer_id, msg_id, direction, kind, ts, state, data)
                VALUES($peer, $msg, $dir, $kind, $ts, $state, $data);
                SELECT changes(), last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$peer", m.PeerId);
            cmd.Parameters.AddWithValue("$msg", m.MsgId);
            cmd.Parameters.AddWithValue("$dir", (int)m.Direction);
            cmd.Parameters.AddWithValue("$kind", (int)m.Kind);
            cmd.Parameters.AddWithValue("$ts", m.Timestamp);
            cmd.Parameters.AddWithValue("$state", (int)m.State);
            cmd.Parameters.AddWithValue("$data", data);
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.GetInt64(0) == 0) return false;
            m.RowId = r.GetInt64(1);
            return true;
        }
    }

    public void UpdateMessage(StoredMessage m)
    {
        var data = _protector.EncryptString(JsonSerializer.Serialize(m.Data), Aad(m.PeerId, m.MsgId, m.Direction));
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE messages SET state = $state, data = $data WHERE row_id = $row;";
            cmd.Parameters.AddWithValue("$state", (int)m.State);
            cmd.Parameters.AddWithValue("$data", data);
            cmd.Parameters.AddWithValue("$row", m.RowId);
            cmd.ExecuteNonQuery();
        }
    }

    public StoredMessage? GetMessage(string peerId, string msgId, MessageDirection dir)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT row_id, peer_id, msg_id, direction, kind, ts, state, data FROM messages
                WHERE peer_id = $peer AND msg_id = $msg AND direction = $dir;
                """;
            cmd.Parameters.AddWithValue("$peer", peerId);
            cmd.Parameters.AddWithValue("$msg", msgId);
            cmd.Parameters.AddWithValue("$dir", (int)dir);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadMessage(r) : null;
        }
    }

    /// <summary>Bir kişiyle olan son mesajları (eskiden yeniye) döndürür.</summary>
    public List<StoredMessage> LoadHistory(string peerId, int limit, long beforeRowId = long.MaxValue)
    {
        var list = new List<StoredMessage>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT row_id, peer_id, msg_id, direction, kind, ts, state, data FROM messages
                WHERE peer_id = $peer AND row_id < $before ORDER BY row_id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$peer", peerId);
            cmd.Parameters.AddWithValue("$before", beforeRowId);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var m = ReadMessage(r);
                if (m != null) list.Add(m);
            }
        }
        list.Reverse();
        return list;
    }

    public List<StoredMessage> LoadPendingOutgoing(string peerId)
    {
        var list = new List<StoredMessage>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT row_id, peer_id, msg_id, direction, kind, ts, state, data FROM messages
                WHERE peer_id = $peer AND direction = 1 AND state = 0 ORDER BY row_id;
                """;
            cmd.Parameters.AddWithValue("$peer", peerId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var m = ReadMessage(r);
                if (m != null) list.Add(m);
            }
        }
        return list;
    }

    public HashSet<string> PeersWithPendingOutgoing()
    {
        var set = new HashSet<string>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT peer_id FROM messages WHERE direction = 1 AND state = 0;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
        }
        return set;
    }

    public void DeleteHistory(string peerId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM messages WHERE peer_id = $peer;";
            cmd.Parameters.AddWithValue("$peer", peerId);
            cmd.ExecuteNonQuery();
        }
    }

    private StoredMessage? ReadMessage(SqliteDataReader r)
    {
        var m = new StoredMessage
        {
            RowId = r.GetInt64(0),
            PeerId = r.GetString(1),
            MsgId = r.GetString(2),
            Direction = (MessageDirection)r.GetInt32(3),
            Kind = (MessageKind)r.GetInt32(4),
            Timestamp = r.GetInt64(5),
            State = (MessageState)r.GetInt32(6),
        };
        try
        {
            m.Data = JsonSerializer.Deserialize<MessageData>(
                _protector.DecryptString((byte[])r[7], Aad(m.PeerId, m.MsgId, m.Direction))) ?? new();
            return m;
        }
        catch (Exception ex)
        {
            Log.Warn($"Mesaj çözülemedi (satır {m.RowId}): {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate) _conn.Dispose();
    }
}
