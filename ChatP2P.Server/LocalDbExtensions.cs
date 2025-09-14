using System;
using System.Data;
using System.Data.SQLite;

namespace ChatP2P.Server
{
    /// <summary>
    /// Port C# of VB.NET LocalDbExtensions - Extended database operations
    /// </summary>
    public static class LocalDbExtensions
    {
        public static void KvSet(string key, string value)
        {
            LocalDb.ExecNonQuery("INSERT OR REPLACE INTO Kv(K, V) VALUES(@k, @v);",
                LocalDb.P("@k", key), LocalDb.P("@v", value));
        }

        public static string? KvGet(string key)
        {
            return LocalDb.ExecScalar<string?>("SELECT V FROM Kv WHERE K = @k;", LocalDb.P("@k", key));
        }

        public static void IdentityEnsureEd25519(ref byte[]? publicKey, ref byte[]? secretKey)
        {
            var dt = LocalDb.Query("SELECT Ed25519Pub, Ed25519Priv FROM Identities WHERE Id = 1;");
            if (dt.Rows.Count > 0)
            {
                var row = dt.Rows[0];
                if (row["Ed25519Pub"] != DBNull.Value) publicKey = (byte[])row["Ed25519Pub"];
                if (row["Ed25519Priv"] != DBNull.Value) secretKey = (byte[])row["Ed25519Priv"];
            }

            if (publicKey == null || secretKey == null)
            {
                // Generate new Ed25519 key pair (placeholder - would use actual crypto library)
                publicKey = new byte[32]; // Placeholder
                secretKey = new byte[64]; // Placeholder

                LocalDb.ExecNonQuery("UPDATE Identities SET Ed25519Pub = @pub, Ed25519Priv = @priv WHERE Id = 1;",
                    LocalDb.P("@pub", publicKey), LocalDb.P("@priv", secretKey));
            }
        }

        public static byte[]? PeerGetEd25519(string peerName)
        {
            return LocalDb.ExecScalar<byte[]?>("SELECT Ed25519Pub FROM PeerKeys WHERE PeerName = @name AND IsRevoked = 0 ORDER BY Id DESC LIMIT 1;",
                LocalDb.P("@name", peerName));
        }

        public static void PeerSetEd25519_Tofu(string peerName, byte[] publicKey)
        {
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            LocalDb.ExecNonQuery(@"INSERT INTO PeerKeys(PeerName, Ed25519Pub, CreatedUtc, IsRevoked)
                                  VALUES(@name, @pub, @created, 0);",
                LocalDb.P("@name", peerName), LocalDb.P("@pub", publicKey), LocalDb.P("@created", now));
        }
    }

    /// <summary>
    /// Security-related database extensions
    /// </summary>
    public static class LocalDbExtensionsSecurity
    {
        public static void EnsurePeerExtraColumns()
        {
            // Schema migration would go here
        }

        public static DataTable PeerList()
        {
            return LocalDb.Query("SELECT Name, Trusted, CreatedUtc, LastSeenUtc FROM Peers ORDER BY Name;");
        }

        public static void PeerSetTrusted(string peerName, bool trusted)
        {
            LocalDb.ExecNonQuery("UPDATE Peers SET Trusted = @trusted WHERE Name = @name;",
                LocalDb.P("@name", peerName), LocalDb.P("@trusted", trusted ? 1 : 0));
        }

        public static void PeerSetNote(string peerName, string note)
        {
            LocalDb.ExecNonQuery("UPDATE Peers SET TrustNote = @note WHERE Name = @name;",
                LocalDb.P("@name", peerName), LocalDb.P("@note", note));
        }

        public static void PeerForgetEd25519(string peerName)
        {
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            LocalDb.ExecNonQuery("UPDATE PeerKeys SET IsRevoked = 1, RevokedUtc = @revoked WHERE PeerName = @name;",
                LocalDb.P("@name", peerName), LocalDb.P("@revoked", now));
        }

        public static void PeerMarkUnverified(string peerName)
        {
            LocalDb.ExecNonQuery("UPDATE Peers SET Verified = 0 WHERE Name = @name;",
                LocalDb.P("@name", peerName));
        }

        public static void PeerMarkVerified(string peerName)
        {
            LocalDb.ExecNonQuery("UPDATE Peers SET Verified = 1 WHERE Name = @name;",
                LocalDb.P("@name", peerName));
        }

        public static string? PeerGetField(string peerName, string fieldName)
        {
            var sql = $"SELECT {fieldName} FROM Peers WHERE Name = @name;";
            return LocalDb.ExecScalar<string?>(sql, LocalDb.P("@name", peerName));
        }

        public static void PeerSetPinned(string peerName, bool pinned)
        {
            // Placeholder for pinned status
        }

        public static bool PeerGetPinned(string peerName)
        {
            // Placeholder for pinned status
            return false;
        }

        public static DataTable GetSecurityEvents(string peerName)
        {
            return LocalDb.Query("SELECT EventType, Details, CreatedUtc FROM SecurityEvents WHERE PeerName = @name ORDER BY CreatedUtc DESC;",
                LocalDb.P("@name", peerName));
        }
    }
}