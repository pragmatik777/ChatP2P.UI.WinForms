using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChatP2P.Client
{
    // ===== MODÈLES DE DONNÉES =====
    
    public class Peer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Fingerprint { get; set; }
        public bool Verified { get; set; } = false;
        public DateTime? CreatedUtc { get; set; }
        public DateTime? LastSeenUtc { get; set; }
        public bool Trusted { get; set; } = false;
        public string? TrustNote { get; set; }
        public string? DtlsFingerprint { get; set; }
        public DateTime? VerifiedUtc { get; set; }
        public string? Note { get; set; }
        public bool Pinned { get; set; } = false;
    }

    public class Message
    {
        public int Id { get; set; }
        public string PeerName { get; set; } = "";
        public string Sender { get; set; } = "";
        public string Body { get; set; } = "";
        public bool IsP2P { get; set; } = false;
        public string Direction { get; set; } = "";  // 'send' / 'recv'
        public DateTime CreatedUtc { get; set; }
    }

    public class PeerKey
    {
        public int Id { get; set; }
        public string PeerName { get; set; } = "";
        public string Kind { get; set; } = "";  // 'DTLS', 'PQ', 'X25519', 'Ed25519'
        public byte[]? Public { get; set; }
        public DateTime CreatedUtc { get; set; }
        public bool Revoked { get; set; } = false;
        public DateTime? RevokedUtc { get; set; }
        public string? Note { get; set; }
    }

    public class Identity
    {
        public int Id { get; set; } = 1;
        public byte[]? Ed25519Pub { get; set; }
        public byte[]? Ed25519Priv { get; set; }
    }

    public class SecurityEvent
    {
        public int Id { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string PeerName { get; set; } = "";
        public string Kind { get; set; } = "";  // 'PUBKEY_MISMATCH', 'TOFU_RESET', 'PIN', 'UNPIN'
        public string? Details { get; set; }
    }

    public class Session
    {
        public int Id { get; set; }
        public string PeerName { get; set; } = "";
        public byte[]? State { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    // ===== SERVICE DE BASE DE DONNÉES =====
    
    public class DatabaseService
    {
        private static DatabaseService? _instance;
        private static readonly object _lock = new object();
        private readonly string _connectionString;
        private readonly string _dbPath;

        public static DatabaseService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new DatabaseService();
                        }
                    }
                }
                return _instance;
            }
        }

        private DatabaseService()
        {
            var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChatP2P");
            Directory.CreateDirectory(appDataPath);
            
            _dbPath = Path.Combine(appDataPath, "chat.db");
            _connectionString = $"Data Source={_dbPath};Version=3;Journal Mode=WAL;";
            
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var connection = new SQLiteConnection(_connectionString);
            connection.Open();

            // Créer toutes les tables selon le schéma original
            var commands = new[]
            {
                // Table Peers - Gestion des contacts
                @"CREATE TABLE IF NOT EXISTS Peers(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL UNIQUE,
                    Fingerprint TEXT NULL,
                    Verified INTEGER NOT NULL DEFAULT 0,
                    CreatedUtc TEXT NULL,
                    LastSeenUtc TEXT NULL,
                    Trusted INTEGER NOT NULL DEFAULT 0,
                    TrustNote TEXT NULL,
                    DtlsFingerprint TEXT NULL,
                    VerifiedUtc TEXT NULL,
                    Note TEXT NULL,
                    Pinned INTEGER DEFAULT 0
                );",

                // Table Messages - Historique des conversations
                @"CREATE TABLE IF NOT EXISTS Messages(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PeerName TEXT NOT NULL,
                    Sender TEXT NOT NULL,
                    Body TEXT NOT NULL,
                    IsP2P INTEGER NOT NULL,
                    Direction TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL
                );",
                
                @"CREATE INDEX IF NOT EXISTS IX_Messages_Peer_Created ON Messages(PeerName, CreatedUtc);",

                // Table Identities - Clés cryptographiques locales
                @"CREATE TABLE IF NOT EXISTS Identities(
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    Ed25519Pub BLOB NULL,
                    Ed25519Priv BLOB NULL
                );",
                
                @"INSERT OR IGNORE INTO Identities(Id) VALUES(1);",

                // Table Kv - Stockage clé-valeur générique
                @"CREATE TABLE IF NOT EXISTS Kv(
                    K TEXT PRIMARY KEY,
                    V TEXT NULL
                );",

                // Table PeerKeys - Historique des clés par peer (TOFU)
                @"CREATE TABLE IF NOT EXISTS PeerKeys(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PeerName TEXT NOT NULL,
                    Kind TEXT NOT NULL,
                    Public BLOB NULL,
                    CreatedUtc TEXT NOT NULL,
                    Revoked INTEGER NOT NULL DEFAULT 0,
                    RevokedUtc TEXT NULL,
                    Note TEXT NULL
                );",
                
                @"CREATE INDEX IF NOT EXISTS IX_PeerKeys_Peer ON PeerKeys(PeerName, CreatedUtc);",

                // Table Sessions - État des sessions P2P
                @"CREATE TABLE IF NOT EXISTS Sessions(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PeerName TEXT NOT NULL UNIQUE,
                    State BLOB NULL,
                    CreatedUtc TEXT NOT NULL
                );",

                // Table SecurityEvents - Audit sécurité
                @"CREATE TABLE IF NOT EXISTS SecurityEvents(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CreatedUtc TEXT NOT NULL,
                    PeerName TEXT NOT NULL,
                    Kind TEXT NOT NULL,
                    Details TEXT
                );"
            };

            foreach (var cmd in commands)
            {
                using var command = new SQLiteCommand(cmd, connection);
                command.ExecuteNonQuery();
            }

            Console.WriteLine($"Database initialized at: {_dbPath}");
        }

        // Helper methods pour les conversions SQLite
        private static int GetSafeInt(System.Data.Common.DbDataReader reader, string columnName)
        {
            var value = reader[columnName];
            return value == DBNull.Value ? 0 : Convert.ToInt32(value);
        }

        private static bool GetSafeBool(System.Data.Common.DbDataReader reader, string columnName)
        {
            var value = reader[columnName];
            return value != DBNull.Value && Convert.ToInt32(value) == 1;
        }

        private static string GetSafeString(System.Data.Common.DbDataReader reader, string columnName)
        {
            var value = reader[columnName];
            return value == DBNull.Value ? "" : Convert.ToString(value) ?? "";
        }

        private static string? GetSafeStringOrNull(System.Data.Common.DbDataReader reader, string columnName)
        {
            var value = reader[columnName];
            return value == DBNull.Value ? null : Convert.ToString(value);
        }

        private static DateTime? GetSafeDateTimeOrNull(System.Data.Common.DbDataReader reader, string columnName)
        {
            var value = reader[columnName];
            if (value == DBNull.Value) return null;
            var str = Convert.ToString(value);
            return string.IsNullOrEmpty(str) ? null : DateTime.Parse(str);
        }

        private static byte[]? GetSafeBytesOrNull(System.Data.Common.DbDataReader reader, string columnName)
        {
            var value = reader[columnName];
            return value == DBNull.Value ? null : (byte[])value;
        }

        // ===== GESTION PEERS =====
        
        public async Task<Peer?> GetPeer(string name)
        {
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                "SELECT * FROM Peers WHERE Name = @name", connection);
            command.Parameters.AddWithValue("@name", name);
            
            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new Peer
                {
                    Id = GetSafeInt(reader, "Id"),
                    Name = GetSafeString(reader, "Name"),
                    Fingerprint = GetSafeStringOrNull(reader, "Fingerprint"),
                    Verified = GetSafeBool(reader, "Verified"),
                    CreatedUtc = GetSafeDateTimeOrNull(reader, "CreatedUtc"),
                    LastSeenUtc = GetSafeDateTimeOrNull(reader, "LastSeenUtc"),
                    Trusted = GetSafeBool(reader, "Trusted"),
                    TrustNote = GetSafeStringOrNull(reader, "TrustNote"),
                    DtlsFingerprint = GetSafeStringOrNull(reader, "DtlsFingerprint"),
                    VerifiedUtc = GetSafeDateTimeOrNull(reader, "VerifiedUtc"),
                    Note = GetSafeStringOrNull(reader, "Note"),
                    Pinned = GetSafeBool(reader, "Pinned")
                };
            }
            return null;
        }

        public async Task<List<Peer>> GetTrustedPeers()
        {
            var peers = new List<Peer>();
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                "SELECT * FROM Peers WHERE Trusted = 1 ORDER BY Name", connection);
            
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                peers.Add(new Peer
                {
                    Id = GetSafeInt(reader, "Id"),
                    Name = GetSafeString(reader, "Name"),
                    Fingerprint = GetSafeStringOrNull(reader, "Fingerprint"),
                    Verified = GetSafeBool(reader, "Verified"),
                    CreatedUtc = GetSafeDateTimeOrNull(reader, "CreatedUtc"),
                    LastSeenUtc = GetSafeDateTimeOrNull(reader, "LastSeenUtc"),
                    Trusted = GetSafeBool(reader, "Trusted"),
                    TrustNote = GetSafeStringOrNull(reader, "TrustNote"),
                    DtlsFingerprint = GetSafeStringOrNull(reader, "DtlsFingerprint"),
                    VerifiedUtc = GetSafeDateTimeOrNull(reader, "VerifiedUtc"),
                    Note = GetSafeStringOrNull(reader, "Note"),
                    Pinned = GetSafeBool(reader, "Pinned")
                });
            }
            return peers;
        }

        public async Task<bool> EnsurePeer(string name)
        {
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                @"INSERT OR IGNORE INTO Peers(Name, CreatedUtc) 
                  VALUES(@name, @created)", connection);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            
            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }

        public async Task<bool> SetPeerTrusted(string name, bool trusted, string? trustNote = null)
        {
            await EnsurePeer(name);
            
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                @"UPDATE Peers SET Trusted = @trusted, TrustNote = @note 
                  WHERE Name = @name", connection);
            command.Parameters.AddWithValue("@trusted", trusted ? 1 : 0);
            command.Parameters.AddWithValue("@note", trustNote);
            command.Parameters.AddWithValue("@name", name);
            
            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }

        public async Task<bool> SetPeerVerified(string name, bool verified)
        {
            await EnsurePeer(name);
            
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                @"UPDATE Peers SET Verified = @verified, VerifiedUtc = @verifiedUtc 
                  WHERE Name = @name", connection);
            command.Parameters.AddWithValue("@verified", verified ? 1 : 0);
            command.Parameters.AddWithValue("@verifiedUtc", verified ? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") : null);
            command.Parameters.AddWithValue("@name", name);
            
            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }

        public async Task<bool> SetPeerPinned(string name, bool pinned)
        {
            await EnsurePeer(name);
            
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                @"UPDATE Peers SET Pinned = @pinned WHERE Name = @name", connection);
            command.Parameters.AddWithValue("@pinned", pinned ? 1 : 0);
            command.Parameters.AddWithValue("@name", name);
            
            var rowsAffected = await command.ExecuteNonQueryAsync();
            
            // Log security event
            if (rowsAffected > 0)
            {
                await LogSecurityEvent(name, pinned ? "PIN" : "UNPIN", 
                    pinned ? "Peer pinned by user" : "Peer unpinned by user");
            }
            
            return rowsAffected > 0;
        }

        // ===== GESTION MESSAGES =====
        
        public async Task<bool> SaveMessage(string peerName, string sender, string body, bool isP2P, string direction)
        {
            await EnsurePeer(peerName);
            
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                @"INSERT INTO Messages(PeerName, Sender, Body, IsP2P, Direction, CreatedUtc) 
                  VALUES(@peer, @sender, @body, @isP2P, @direction, @created)", connection);
            command.Parameters.AddWithValue("@peer", peerName);
            command.Parameters.AddWithValue("@sender", sender);
            command.Parameters.AddWithValue("@body", body);
            command.Parameters.AddWithValue("@isP2P", isP2P ? 1 : 0);
            command.Parameters.AddWithValue("@direction", direction);
            command.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            
            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }

        public async Task<List<Message>> GetLastMessages(string peerName, int limit = 50)
        {
            var messages = new List<Message>();
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                @"SELECT * FROM Messages WHERE PeerName = @peer 
                  ORDER BY CreatedUtc DESC LIMIT @limit", connection);
            command.Parameters.AddWithValue("@peer", peerName);
            command.Parameters.AddWithValue("@limit", limit);
            
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                messages.Add(new Message
                {
                    Id = GetSafeInt(reader, "Id"),
                    PeerName = GetSafeString(reader, "PeerName"),
                    Sender = GetSafeString(reader, "Sender"),
                    Body = GetSafeString(reader, "Body"),
                    IsP2P = GetSafeBool(reader, "IsP2P"),
                    Direction = GetSafeString(reader, "Direction"),
                    CreatedUtc = DateTime.Parse(GetSafeString(reader, "CreatedUtc"))
                });
            }
            
            messages.Reverse(); // Ordre chronologique
            return messages;
        }

        /// <summary>
        /// Supprime tous les messages avec un peer spécifique
        /// </summary>
        public async Task<bool> DeleteAllMessagesWithPeer(string peerName)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                
                using var command = new SQLiteCommand(
                    "DELETE FROM Messages WHERE PeerName = @peer", connection);
                command.Parameters.AddWithValue("@peer", peerName);
                
                var deletedCount = await command.ExecuteNonQueryAsync();
                
                Console.WriteLine($"🗑️ [DB-DELETE] Deleted {deletedCount} messages with peer: {peerName}");
                return deletedCount > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [DB-DELETE] Error deleting messages for {peerName}: {ex.Message}");
                return false;
            }
        }

        // ===== GESTION CLÉS CRYPTO =====
        
        public async Task<Identity?> GetIdentity()
        {
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                "SELECT * FROM Identities WHERE Id = 1", connection);
            
            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new Identity
                {
                    Id = GetSafeInt(reader, "Id"),
                    Ed25519Pub = GetSafeBytesOrNull(reader, "Ed25519Pub"),
                    Ed25519Priv = GetSafeBytesOrNull(reader, "Ed25519Priv")
                };
            }
            return null;
        }

        public async Task<bool> SetIdentityEd25519(byte[] publicKey, byte[] privateKey)
        {
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                @"UPDATE Identities SET Ed25519Pub = @pub, Ed25519Priv = @priv 
                  WHERE Id = 1", connection);
            command.Parameters.AddWithValue("@pub", publicKey);
            command.Parameters.AddWithValue("@priv", privateKey);
            
            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }

        public async Task<bool> AddPeerKey(string peerName, string kind, byte[] publicKey, string? note = null)
        {
            await EnsurePeer(peerName);
            
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                @"INSERT INTO PeerKeys(PeerName, Kind, Public, CreatedUtc, Note) 
                  VALUES(@peer, @kind, @public, @created, @note)", connection);
            command.Parameters.AddWithValue("@peer", peerName);
            command.Parameters.AddWithValue("@kind", kind);
            command.Parameters.AddWithValue("@public", publicKey);
            command.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("@note", note);
            
            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }

        public async Task<List<PeerKey>> GetPeerKeys(string peerName, string? kind = null)
        {
            var keys = new List<PeerKey>();
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            string sql = "SELECT * FROM PeerKeys WHERE PeerName = @peer";
            if (kind != null)
                sql += " AND Kind = @kind";
            sql += " ORDER BY CreatedUtc DESC";
            
            using var command = new SQLiteCommand(sql, connection);
            command.Parameters.AddWithValue("@peer", peerName);
            if (kind != null)
                command.Parameters.AddWithValue("@kind", kind);
            
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                keys.Add(new PeerKey
                {
                    Id = GetSafeInt(reader, "Id"),
                    PeerName = GetSafeString(reader, "PeerName"),
                    Kind = GetSafeString(reader, "Kind"),
                    Public = GetSafeBytesOrNull(reader, "Public"),
                    CreatedUtc = DateTime.Parse(GetSafeString(reader, "CreatedUtc")),
                    Revoked = GetSafeBool(reader, "Revoked"),
                    RevokedUtc = GetSafeDateTimeOrNull(reader, "RevokedUtc"),
                    Note = GetSafeStringOrNull(reader, "Note")
                });
            }
            return keys;
        }

        public async Task<bool> RevokePeerKey(int keyId, string reason)
        {
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                @"UPDATE PeerKeys SET Revoked = 1, RevokedUtc = @revoked, Note = @reason 
                  WHERE Id = @id", connection);
            command.Parameters.AddWithValue("@revoked", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("@reason", reason);
            command.Parameters.AddWithValue("@id", keyId);
            
            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }

        // ===== GESTION SÉCURITÉ =====
        
        public async Task<bool> LogSecurityEvent(string peerName, string kind, string? details = null)
        {
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                @"INSERT INTO SecurityEvents(CreatedUtc, PeerName, Kind, Details) 
                  VALUES(@created, @peer, @kind, @details)", connection);
            command.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("@peer", peerName);
            command.Parameters.AddWithValue("@kind", kind);
            command.Parameters.AddWithValue("@details", details);
            
            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }

        public async Task<List<SecurityEvent>> GetSecurityEvents(string? peerName = null, int limit = 100)
        {
            var events = new List<SecurityEvent>();
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            string sql = "SELECT * FROM SecurityEvents";
            if (peerName != null)
                sql += " WHERE PeerName = @peer";
            sql += " ORDER BY CreatedUtc DESC LIMIT @limit";
            
            using var command = new SQLiteCommand(sql, connection);
            if (peerName != null)
                command.Parameters.AddWithValue("@peer", peerName);
            command.Parameters.AddWithValue("@limit", limit);
            
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                events.Add(new SecurityEvent
                {
                    Id = GetSafeInt(reader, "Id"),
                    CreatedUtc = DateTime.Parse(GetSafeString(reader, "CreatedUtc")),
                    PeerName = GetSafeString(reader, "PeerName"),
                    Kind = GetSafeString(reader, "Kind"),
                    Details = GetSafeStringOrNull(reader, "Details")
                });
            }
            return events;
        }

        // ===== GESTION SESSIONS =====
        
        public async Task<bool> SaveSession(string peerName, byte[] state)
        {
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                @"INSERT OR REPLACE INTO Sessions(PeerName, State, CreatedUtc) 
                  VALUES(@peer, @state, @created)", connection);
            command.Parameters.AddWithValue("@peer", peerName);
            command.Parameters.AddWithValue("@state", state);
            command.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            
            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }

        public async Task<Session?> GetSession(string peerName)
        {
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                "SELECT * FROM Sessions WHERE PeerName = @peer", connection);
            command.Parameters.AddWithValue("@peer", peerName);
            
            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new Session
                {
                    Id = GetSafeInt(reader, "Id"),
                    PeerName = GetSafeString(reader, "PeerName"),
                    State = GetSafeBytesOrNull(reader, "State"),
                    CreatedUtc = DateTime.Parse(GetSafeString(reader, "CreatedUtc"))
                };
            }
            return null;
        }

        // ===== GESTION KV =====
        
        public async Task<string?> GetKv(string key)
        {
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                "SELECT V FROM Kv WHERE K = @key", connection);
            command.Parameters.AddWithValue("@key", key);
            
            var result = await command.ExecuteScalarAsync();
            return result?.ToString();
        }

        public async Task<bool> SetKv(string key, string? value)
        {
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                @"INSERT OR REPLACE INTO Kv(K, V) VALUES(@key, @value)", connection);
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@value", value);
            
            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }

        // ===== UTILITAIRES =====
        
        public string GetDatabasePath() => _dbPath;
        
        public async Task<int> GetTrustedPeerCount()
        {
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = new SQLiteCommand(
                "SELECT COUNT(*) FROM Peers WHERE Trusted = 1", connection);
            
            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }
    }
}