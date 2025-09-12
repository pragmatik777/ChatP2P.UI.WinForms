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
            return "REQUEST_CREATED";
        }

        public static List<ContactRequest> GetPendingRequests(string forPeer)
        {
            return _pendingRequests.FindAll(r => r.ToPeer == forPeer && r.Status == "pending");
        }

        public static List<ContactRequest> GetSentRequests(string fromPeer)
        {
            return _pendingRequests.FindAll(r => r.FromPeer == fromPeer && r.Status == "pending");
        }

        public static async Task<bool> AcceptContactRequest(string fromPeer, string toPeer)
        {
            var request = _pendingRequests.Find(r => 
                r.FromPeer == fromPeer && r.ToPeer == toPeer && r.Status == "pending");
            
            if (request == null)
                return false;

            // Ajouter le contact des deux côtés
            await AddContact(fromPeer, request.PublicKey, true);
            
            // Marquer la demande comme acceptée
            request.Status = "accepted";
            await SaveRequests();
            
            Console.WriteLine($"Demande acceptée: {fromPeer} ↔ {toPeer}");
            return true;
        }

        public static async Task<bool> RejectContactRequest(string fromPeer, string toPeer)
        {
            var request = _pendingRequests.Find(r => 
                r.FromPeer == fromPeer && r.ToPeer == toPeer && r.Status == "pending");
            
            if (request == null)
                return false;

            request.Status = "rejected";
            await SaveRequests();
            
            Console.WriteLine($"Demande rejetée: {fromPeer} → {toPeer}");
            return true;
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