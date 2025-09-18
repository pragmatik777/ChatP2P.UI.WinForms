using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChatP2P.Server
{
    public class Contact
    {
        public string PeerName { get; set; } = "";
        public string PublicKey { get; set; } = "";
        public bool IsVerified { get; set; } = false;
        public DateTime AddedDate { get; set; } = DateTime.Now;
        public string Status { get; set; } = "offline"; // online, offline, connecting
        public string LastSeen { get; set; } = "";
    }

    public class ContactRequest
    {
        public string FromPeer { get; set; } = "";
        public string ToPeer { get; set; } = "";
        public string PublicKey { get; set; } = "";
        public DateTime RequestDate { get; set; } = DateTime.Now;
        public string Status { get; set; } = "pending"; // pending, accepted, rejected
        public string Message { get; set; } = "";
    }

    public static class ContactManager
    {
        private static readonly Dictionary<string, Contact> _contacts = new();
        private static readonly List<ContactRequest> _pendingRequests = new();
        private static readonly string _contactsFile = "contacts.json";
        private static readonly string _requestsFile = "contact_requests.json";

        static ContactManager()
        {
            LoadContacts();
            LoadRequests();
        }

        // ===== Gestion des contacts =====
        public static List<Contact> GetAllContacts()
        {
            return new List<Contact>(_contacts.Values);
        }

        public static Contact? GetContact(string peerName)
        {
            _contacts.TryGetValue(peerName, out var contact);
            return contact;
        }

        public static async Task<bool> AddContact(string peerName, string publicKey, bool isVerified = false)
        {
            if (_contacts.ContainsKey(peerName))
                return false; // Contact déjà existant

            var contact = new Contact
            {
                PeerName = peerName,
                PublicKey = publicKey,
                IsVerified = isVerified,
                AddedDate = DateTime.Now,
                Status = "offline"
            };

            _contacts[peerName] = contact;
            await SaveContacts();
            
            Console.WriteLine($"Contact ajouté: {peerName} (Verified: {isVerified})");
            return true;
        }

        public static async Task<bool> RemoveContact(string peerName)
        {
            if (!_contacts.ContainsKey(peerName))
                return false;

            _contacts.Remove(peerName);
            await SaveContacts();
            
            Console.WriteLine($"Contact supprimé: {peerName}");
            return true;
        }

        public static async Task UpdateContactStatus(string peerName, string status)
        {
            if (_contacts.TryGetValue(peerName, out var contact))
            {
                contact.Status = status;
                if (status == "offline")
                    contact.LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                
                await SaveContacts();
            }
        }

        // ===== Demandes de contact =====
        public static async Task<string> CreateContactRequest(string fromPeer, string toPeer, string publicKey, string message = "")
        {
            // Vérifier si une demande existe déjà
            var existing = _pendingRequests.Find(r => 
                r.FromPeer == fromPeer && r.ToPeer == toPeer && r.Status == "pending");
            
            if (existing != null)
                return "REQUEST_ALREADY_EXISTS";

            // Vérifier si le contact existe déjà
            if (_contacts.ContainsKey(toPeer))
                return "CONTACT_ALREADY_EXISTS";

            var request = new ContactRequest
            {
                FromPeer = fromPeer,
                ToPeer = toPeer,
                PublicKey = publicKey,
                RequestDate = DateTime.Now,
                Status = "pending",
                Message = message
            };

            _pendingRequests.Add(request);
            await SaveRequests();
            
            Console.WriteLine($"Demande de contact créée: {fromPeer} → {toPeer}");
            
            // NOUVEAU: Transmettre la demande via RelayHub au peer cible
            await SendFriendRequestViaRelay(request);
            
            return "REQUEST_CREATED";
        }

        // NOUVELLE MÉTHODE: Transmet friend request via RelayHub  
        private static async Task SendFriendRequestViaRelay(ContactRequest request)
        {
            try
            {
                Console.WriteLine($"🔄 [RELAY] SendFriendRequestViaRelay: {request.FromPeer} → {request.ToPeer}");
                
                // Obtenir une référence au RelayHub depuis Program.cs
                var relayHub = Program.GetRelayHub();
                if (relayHub == null)
                {
                    Console.WriteLine($"❌ [RELAY] RelayHub non disponible");
                    return;
                }
                
                // Vérifier si le destinataire est connecté au RelayHub
                if (!relayHub.IsClientConnected(request.ToPeer))
                {
                    Console.WriteLine($"⚠️  [RELAY] Client {request.ToPeer} non connecté au RelayHub, demande stockée localement");
                    return;
                }
                
                // Format protocole RelayHub: FRIEND_REQ:fromPeer:toPeer:publicKey:message
                var relayMessage = $"FRIEND_REQ:{request.FromPeer}:{request.ToPeer}:{request.PublicKey}:{request.Message}";
                Console.WriteLine($"[DEBUG] Message relay: {relayMessage}");
                
                // Envoyer via RelayHub canal Friend Requests (port 7777)
                var success = await relayHub.SendFriendRequestToClient(request.ToPeer, relayMessage);
                
                if (success)
                {
                    Console.WriteLine($"✅ [RELAY] Friend request transmise avec succès: {request.FromPeer} → {request.ToPeer}");
                    
                    // Marquer comme transmise dans le système
                    MarkRequestAsTransmitted(request.FromPeer, request.ToPeer);
                }
                else
                {
                    Console.WriteLine($"❌ [RELAY] Échec transmission friend request: {request.FromPeer} → {request.ToPeer}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [RELAY] Erreur envoi friend request: {ex.Message}");
                Console.WriteLine($"❌ [RELAY] StackTrace: {ex.StackTrace}");
            }
        }


        // NOUVELLE MÉTHODE: Recevoir une friend request via P2P
        public static async Task ReceiveFriendRequestFromP2P(string fromPeer, string toPeer, string publicKey, string message)
        {
            try
            {
                // Vérifier si la demande n'existe pas déjà
                var existing = _pendingRequests.Find(r => 
                    r.FromPeer == fromPeer && r.ToPeer == toPeer && r.Status == "pending");
                
                if (existing != null)
                {
                    Console.WriteLine($"Friend request de {fromPeer} déjà existante");
                    return;
                }

                var request = new ContactRequest
                {
                    FromPeer = fromPeer,
                    ToPeer = toPeer,
                    PublicKey = publicKey,
                    RequestDate = DateTime.Now,
                    Status = "pending",
                    Message = message
                };

                _pendingRequests.Add(request);
                await SaveRequests();
                
                Console.WriteLine($"Friend request reçue via P2P: {fromPeer} → {toPeer}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur réception friend request P2P: {ex.Message}");
            }
        }

        public static List<ContactRequest> GetPendingRequests(string forPeer)
        {
            return _pendingRequests.FindAll(r => r.ToPeer == forPeer && r.Status == "pending");
        }

        public static List<ContactRequest> GetSentRequests(string fromPeer)
        {
            return _pendingRequests.FindAll(r => r.FromPeer == fromPeer && r.Status == "pending");
        }
        
        public static List<ContactRequest> GetAllSentRequests(string fromPeer)
        {
            return _pendingRequests.FindAll(r => r.FromPeer == fromPeer);
        }

        public static List<ContactRequest> GetAllReceivedRequests(string toPeer)
        {
            // Only return PENDING requests to avoid loops after acceptance
            return _pendingRequests.FindAll(r => r.ToPeer == toPeer && r.Status == "pending");
        }

        public static async Task<bool> AcceptContactRequest(string fromPeer, string toPeer)
        {
            var request = _pendingRequests.Find(r => 
                r.FromPeer == fromPeer && r.ToPeer == toPeer && r.Status == "pending");
            
            if (request == null)
                return false;

            // Ajouter le contact dans ContactManager (fichier JSON)
            await AddContact(fromPeer, request.PublicKey, true);
            
            // Server only manages the JSON request state, clients handle their own contacts
            Console.WriteLine($"Server: Friend request accepted by {toPeer}, will notify {fromPeer}");
            
            
            // Supprimer la demande de la liste des pending requests (acceptée = terminée)
            _pendingRequests.Remove(request);
            await SaveRequests();
            
            Console.WriteLine($"Demande acceptée et supprimée: {fromPeer} ↔ {toPeer}");
            return true;
        }

        public static async Task<bool> RejectContactRequest(string fromPeer, string toPeer)
        {
            var request = _pendingRequests.Find(r => 
                r.FromPeer == fromPeer && r.ToPeer == toPeer && r.Status == "pending");
            
            if (request == null)
                return false;

            // Supprimer la demande de la liste des pending requests (rejetée = terminée)
            _pendingRequests.Remove(request);
            await SaveRequests();
            
            Console.WriteLine($"Demande rejetée et supprimée: {fromPeer} → {toPeer}");
            return true;
        }

        // NOUVELLE MÉTHODE: Marquer une friend request comme transmise (pour RelayHub)
        public static void MarkRequestAsTransmitted(string fromPeer, string toPeer)
        {
            try
            {
                var request = _pendingRequests.Find(r => 
                    r.FromPeer == fromPeer && r.ToPeer == toPeer && r.Status == "pending");
                
                if (request != null)
                {
                    request.Status = "transmitted";
                    _ = Task.Run(async () => await SaveRequests());
                    Console.WriteLine($"📤 [RELAY] Request marquée comme transmise: {fromPeer} → {toPeer}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error marking request as transmitted: {ex.Message}");
            }
        }

        public static async Task CleanupTransmittedRequests()
        {
            try
            {
                var transmittedRequests = _pendingRequests.FindAll(r => 
                    r.Status == "transmitted" || 
                    (r.Status == "pending" && (DateTime.Now - r.RequestDate).TotalDays > 30));
                
                foreach (var request in transmittedRequests)
                {
                    _pendingRequests.Remove(request);
                }
                
                if (transmittedRequests.Count > 0)
                {
                    await SaveRequests();
                    Console.WriteLine($"ContactManager: Cleaned up {transmittedRequests.Count} old/transmitted requests");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error cleaning up transmitted requests: {ex.Message}");
            }
        }

        // ===== Persistance =====
        private static async Task SaveContacts()
        {
            try
            {
                var json = JsonSerializer.Serialize(_contacts, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_contactsFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur sauvegarde contacts: {ex.Message}");
            }
        }

        private static async Task SaveRequests()
        {
            try
            {
                var json = JsonSerializer.Serialize(_pendingRequests, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_requestsFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur sauvegarde demandes: {ex.Message}");
            }
        }

        private static void LoadContacts()
        {
            try
            {
                if (File.Exists(_contactsFile))
                {
                    var json = File.ReadAllText(_contactsFile);
                    var contacts = JsonSerializer.Deserialize<Dictionary<string, Contact>>(json);
                    if (contacts != null)
                    {
                        foreach (var kvp in contacts)
                            _contacts[kvp.Key] = kvp.Value;
                    }
                    Console.WriteLine($"Contacts chargés: {_contacts.Count}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur chargement contacts: {ex.Message}");
            }
        }

        private static void LoadRequests()
        {
            try
            {
                if (File.Exists(_requestsFile))
                {
                    var json = File.ReadAllText(_requestsFile);
                    var requests = JsonSerializer.Deserialize<List<ContactRequest>>(json);
                    if (requests != null)
                    {
                        _pendingRequests.AddRange(requests);
                    }
                    Console.WriteLine($"Demandes chargées: {_pendingRequests.Count}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur chargement demandes: {ex.Message}");
            }
        }

        // ===== Utilitaires =====
        public static bool IsContactVerified(string peerName)
        {
            return _contacts.TryGetValue(peerName, out var contact) && contact.IsVerified;
        }

        public static int GetContactCount()
        {
            return _contacts.Count;
        }

        public static int GetPendingRequestCount(string forPeer)
        {
            return _pendingRequests.FindAll(r => r.ToPeer == forPeer && r.Status == "pending").Count;
        }
    }
}