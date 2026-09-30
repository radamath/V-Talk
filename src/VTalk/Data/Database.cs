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
            """);
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
    }

    public void UpsertContact(ContactRecord c)
    {
        var info = JsonSerializer.Serialize(new ContactInfo
        {
            Name = c.Name, User = c.User, Machine = c.Machine, Note = c.Note, AvatarHash = c.AvatarHash,
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
