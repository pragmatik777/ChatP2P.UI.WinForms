using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ChatP2P.Server
{
    public class DatabaseService
    {
        private static DatabaseService? _instance;
        private static readonly object _lock = new();

        public static DatabaseService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new DatabaseService();
                    }
                }
                return _instance;
            }
        }

        private DatabaseService()
        {
            try
            {
                LocalDb.Init("ChatP2P", "server.db");
                LocalDbExtensionsSecurity.EnsurePeerExtraColumns();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Database initialization error: {ex.Message}");
            }
        }

        // Friend Request methods
        public void StoreFriendRequest(string fromPeer, string toPeer, string message, string publicKey)
        {
            try
            {
                var requestData = new FriendRequestData
                {
                    FromPeer = fromPeer,
                    ToPeer = toPeer,
                    Message = message,
                    PublicKey = publicKey,
                    Status = "pending",
                    RequestDate = DateTime.UtcNow
                };

                var json = System.Text.Json.JsonSerializer.Serialize(requestData);
                LocalDbExtensions.KvSet($"friend_request_{fromPeer}_{toPeer}_{DateTime.UtcNow.Ticks}", json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error storing friend request: {ex.Message}");
            }
        }

        public List<FriendRequestData> GetFriendRequestsFor(string toPeer)
        {
            var requests = new List<FriendRequestData>();
            try
            {
                var dt = LocalDb.Query("SELECT K, V FROM Kv WHERE K LIKE @pattern;", 
                                                   LocalDb.P("@pattern", "friend_request_%"));
                
                foreach (DataRow row in dt.Rows)
                {
                    var key = row["K"].ToString();
                    var value = row["V"].ToString();
                    
                    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) continue;
                    
                    try
                    {
                        var request = System.Text.Json.JsonSerializer.Deserialize<FriendRequestData>(value);
                        if (request != null && request.ToPeer == toPeer && request.Status == "pending")
                        {
                            request.RequestKey = key;
                            requests.Add(request);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error deserializing friend request: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting friend requests: {ex.Message}");
            }
            return requests;
        }

        public bool AcceptFriendRequest(string requester, string accepter)
        {
            try
            {
                // The friend request exists in ContactManager JSON, not in DatabaseService SQLite
                // We just need to add the trust relationship to SQLite database
                Console.WriteLine($"DatabaseService: Processing friend request {requester} -> {accepter}");

                // Add both users as contacts in the database
                EnsurePeerExists(requester);
                EnsurePeerExists(accepter);
                
                // Only mark the requester as trusted by the accepter
                // The accepter (who is accepting) trusts the requester (who requested)
                SetPeerTrusted(requester, true);
                
                Console.WriteLine($"DatabaseService: Marked {requester} as trusted (accepted by {accepter})");
                
                Console.WriteLine($"DatabaseService: Friend request accepted successfully: {requester} -> {accepter}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DatabaseService: Error accepting friend request: {ex.Message}");
            }
            return false;
        }

        public bool RejectFriendRequest(string requester, string rejecter)
        {
            try
            {
                var requests = GetFriendRequestsFor(rejecter);
                var request = requests.Find(r => r.FromPeer == requester);
                
                if (request != null)
                {
                    request.Status = "rejected";
                    var json = System.Text.Json.JsonSerializer.Serialize(request);
                    LocalDbExtensions.KvSet(request.RequestKey, json);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error rejecting friend request: {ex.Message}");
            }
            return false;
        }

        // Security methods
        public List<PeerSecurityData> GetSecurityPeerList(string searchFilter = "")
        {
            var peers = new List<PeerSecurityData>();
            try
            {
                var dt = LocalDbExtensionsSecurity.PeerList();
                
                foreach (DataRow row in dt.Rows)
                {
                    var peer = new PeerSecurityData
                    {
                        Name = GetRowString(row, "Name"),
                        Trusted = GetRowBool(row, "Trusted"),
                        AuthOk = GetRowBool(row, "AuthOk"),
                        Fingerprint = GetRowString(row, "Fingerprint"),
                        CreatedUtc = GetRowString(row, "CreatedUtc"),
                        LastSeenUtc = GetRowString(row, "LastSeenUtc"),
                        Note = GetRowString(row, "Note")
                    };
                    
                    if (string.IsNullOrEmpty(searchFilter) || 
                        peer.Name.Contains(searchFilter, StringComparison.OrdinalIgnoreCase) ||
                        peer.Fingerprint.Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        peers.Add(peer);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting security peer list: {ex.Message}");
            }
            return peers;
        }

        public string GetMyFingerprint()
        {
            try
            {
                byte[] pk = null!, sk = null!;
                LocalDbExtensions.IdentityEnsureEd25519(ref pk, ref sk);
                
                if (pk != null && pk.Length > 0)
                {
                    return FormatFingerprint(ComputeFingerprint(pk));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting fingerprint: {ex.Message}");
            }
            return "Unknown";
        }

        public void SetPeerTrusted(string peerName, bool trusted)
        {
            try
            {
                EnsurePeerExists(peerName);
                LocalDbExtensionsSecurity.PeerSetTrusted(peerName, trusted);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting peer trust: {ex.Message}");
            }
        }

        public void SetPeerNote(string peerName, string note)
        {
            try
            {
                EnsurePeerExists(peerName);
                LocalDbExtensionsSecurity.PeerSetNote(peerName, note ?? "");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting peer note: {ex.Message}");
            }
        }

        public void ResetPeerTofu(string peerName)
        {
            try
            {
                LocalDbExtensionsSecurity.PeerForgetEd25519(peerName);
                LocalDbExtensionsSecurity.PeerMarkUnverified(peerName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error resetting TOFU: {ex.Message}");
            }
        }

        public void ImportPeerKey(string peerName, string publicKeyB64)
        {
            try
            {
                var keyBytes = Convert.FromBase64String(publicKeyB64);
                if (keyBytes.Length != 32)
                    throw new ArgumentException("Invalid key length for Ed25519");
                
                EnsurePeerExists(peerName);
                LocalDbExtensions.PeerSetEd25519_Tofu(peerName, keyBytes);
                LocalDbExtensionsSecurity.PeerMarkUnverified(peerName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error importing peer key: {ex.Message}");
                throw;
            }
        }

        public (string PublicKeyB64, string Fingerprint) ExportMyKey()
        {
            try
            {
                byte[] pk = null!, sk = null!;
                LocalDbExtensions.IdentityEnsureEd25519(ref pk, ref sk);
                
                if (pk != null && pk.Length > 0)
                {
                    var publicKeyB64 = Convert.ToBase64String(pk);
                    var fingerprint = FormatFingerprint(ComputeFingerprint(pk));
                    return (publicKeyB64, fingerprint);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error exporting key: {ex.Message}");
            }
            return ("", "");
        }

        // Contact management
        public List<string> GetTrustedContacts()
        {
            var contacts = new List<string>();
            try
            {
                var dt = LocalDb.Query("SELECT Name FROM Peers WHERE Trusted = 1;");
                foreach (DataRow row in dt.Rows)
                {
                    var name = GetRowString(row, "Name");
                    if (!string.IsNullOrEmpty(name))
                        contacts.Add(name);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting trusted contacts: {ex.Message}");
            }
            return contacts;
        }

        public void EnsurePeerExists(string peerName)
        {
            if (string.IsNullOrEmpty(peerName)) return;
            
            try
            {
                var existing = LocalDb.ExecScalar<object>(
                    "SELECT COUNT(*) FROM Peers WHERE Name = @name;", 
                    LocalDb.P("@name", peerName));
                
                var count = Convert.ToInt32(existing);
                if (count == 0)
                {
                    var nowUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    LocalDb.ExecNonQuery(
                        "INSERT INTO Peers (Name, Verified, CreatedUtc, LastSeenUtc, Trusted, TrustNote) VALUES (@name, 0, @created, @seen, 0, '');",
                        LocalDb.P("@name", peerName),
                        LocalDb.P("@created", nowUtc),
                        LocalDb.P("@seen", nowUtc));
                }
                else
                {
                    // Update LastSeenUtc
                    var nowUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    LocalDb.ExecNonQuery(
                        "UPDATE Peers SET LastSeenUtc = @seen WHERE Name = @name;",
                        LocalDb.P("@name", peerName),
                        LocalDb.P("@seen", nowUtc));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error ensuring peer exists: {ex.Message}");
            }
        }

        // Helper methods
        private static string GetRowString(DataRow row, string columnName)
        {
            try
            {
                if (row.Table.Columns.Contains(columnName))
                {
                    var value = row[columnName];
                    return value == DBNull.Value ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                }
            }
            catch { }
            return "";
        }

        private static bool GetRowBool(DataRow row, string columnName)
        {
            try
            {
                if (row.Table.Columns.Contains(columnName))
                {
                    var value = row[columnName];
                    if (value != DBNull.Value)
                    {
                        return Convert.ToInt32(value) != 0;
                    }
                }
            }
            catch { }
            return false;
        }

        private static byte[] ComputeFingerprint(byte[] publicKey)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(publicKey);
        }

        private static string FormatFingerprint(byte[] fingerprint)
        {
            var hex = BitConverter.ToString(fingerprint).Replace("-", "");
            var sb = new StringBuilder();
            for (int i = 0; i < hex.Length; i += 4)
            {
                if (sb.Length > 0) sb.Append("-");
                var take = Math.Min(4, hex.Length - i);
                sb.Append(hex.Substring(i, take));
            }
            return sb.ToString();
        }
    }

    // Data classes
    public class FriendRequestData
    {
        [JsonPropertyName("from_peer")]
        public string FromPeer { get; set; } = "";
        
        [JsonPropertyName("to_peer")]
        public string ToPeer { get; set; } = "";
        
        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
        
        [JsonPropertyName("public_key")]
        public string PublicKey { get; set; } = "";
        
        [JsonPropertyName("status")]
        public string Status { get; set; } = "pending";
        
        [JsonPropertyName("request_date")]
        public DateTime RequestDate { get; set; } = DateTime.UtcNow;
        
        [JsonIgnore]
        public string RequestKey { get; set; } = "";
    }

    public class PeerSecurityData
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        
        [JsonPropertyName("trusted")]
        public bool Trusted { get; set; } = false;
        
        [JsonPropertyName("auth_ok")]
        public bool AuthOk { get; set; } = false;
        
        [JsonPropertyName("fingerprint")]
        public string Fingerprint { get; set; } = "";
        
        [JsonPropertyName("created_utc")]
        public string CreatedUtc { get; set; } = "";
        
        [JsonPropertyName("last_seen_utc")]
        public string LastSeenUtc { get; set; } = "";
        
        [JsonPropertyName("note")]
        public string Note { get; set; } = "";
    }
}