using System;
using System.Data;
using System.Data.SQLite;
using System.IO;

namespace ChatP2P.Server
{
    /// <summary>
    /// Port C# of VB.NET LocalDb - SQLite database helpers
    /// </summary>
    public static class LocalDb
    {
        private static string? _dbPath;
        private static string? _connString;
        private static readonly object _gate = new();

        public static string DbPath => _dbPath ?? "";

        public static void Init(string appFolderName = "ChatP2P", string dbFileName = "chat.db")
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appFolderName);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            _dbPath = Path.Combine(dir, dbFileName);
            _connString = $"Data Source={_dbPath};Version=3;Pooling=True;Journal Mode=WAL;";

            bool isNew = !File.Exists(_dbPath);
            if (isNew)
            {
                SQLiteConnection.CreateFile(_dbPath);
            }

            // PRAGMA setup
            using var cn = Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
            cmd.ExecuteNonQuery();

            if (isNew)
            {
                CreateSchema();
            }
        }

        private static void CreateSchema()
        {
            const string ddl = @"
CREATE TABLE IF NOT EXISTS Peers(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL UNIQUE,
    Fingerprint TEXT NULL,
    Verified INTEGER NOT NULL DEFAULT 0,
    CreatedUtc TEXT NULL,
    LastSeenUtc TEXT NULL,
    Trusted INTEGER NOT NULL DEFAULT 0,
    TrustNote TEXT NULL,
    DtlsFingerprint TEXT NULL
);

CREATE TABLE IF NOT EXISTS Messages(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PeerName TEXT NOT NULL,
    Sender TEXT NOT NULL,
    Body TEXT NOT NULL,
    IsP2P INTEGER NOT NULL,
    Direction TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_Messages_Peer_Created ON Messages(PeerName, CreatedUtc);

CREATE TABLE IF NOT EXISTS Identities(
    Id INTEGER PRIMARY KEY CHECK (Id = 1),
    Ed25519Pub BLOB NULL,
    Ed25519Priv BLOB NULL
);

INSERT OR IGNORE INTO Identities(Id) VALUES(1);

CREATE TABLE IF NOT EXISTS Kv(
    K TEXT PRIMARY KEY,
    V TEXT NULL
);";

            using var cn = Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }

        public static SQLiteConnection Open()
        {
            if (string.IsNullOrEmpty(_connString))
                throw new InvalidOperationException("LocalDb not initialized");

            var cn = new SQLiteConnection(_connString);
            cn.Open();
            return cn;
        }

        public static DataTable Query(string sql, params SQLiteParameter[] parameters)
        {
            using var cn = Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            if (parameters != null) cmd.Parameters.AddRange(parameters);

            using var adapter = new SQLiteDataAdapter(cmd);
            var dt = new DataTable();
            adapter.Fill(dt);
            return dt;
        }

        public static int ExecNonQuery(string sql, params SQLiteParameter[] parameters)
        {
            using var cn = Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            if (parameters != null) cmd.Parameters.AddRange(parameters);
            return cmd.ExecuteNonQuery();
        }

        public static T ExecScalar<T>(string sql, params SQLiteParameter[] parameters)
        {
            using var cn = Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            if (parameters != null) cmd.Parameters.AddRange(parameters);
            var result = cmd.ExecuteScalar();
            return result == null || result == DBNull.Value ? default(T)! : (T)result;
        }

        public static SQLiteParameter P(string name, object? value)
        {
            return new SQLiteParameter(name, value ?? DBNull.Value);
        }

        public static bool GetPeerTrusted(string peerName)
        {
            var result = ExecScalar<object>("SELECT Trusted FROM Peers WHERE Name = @name;", P("@name", peerName));
            return result != null && Convert.ToInt32(result) != 0;
        }

        public static void SetPeerDtlsFp(string peerName, string fingerprint)
        {
            ExecNonQuery("UPDATE Peers SET DtlsFingerprint = @fp WHERE Name = @name;",
                P("@name", peerName), P("@fp", fingerprint));
        }
    }
}