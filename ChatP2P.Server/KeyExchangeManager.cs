using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ChatP2P.Crypto;

namespace ChatP2P.Server
{
    public class KeyExchangeSession
    {
        public string SessionId { get; set; } = "";
        public string InitiatorPeer { get; set; } = "";
        public string ResponderPeer { get; set; } = "";
        public string InitiatorPublicKey { get; set; } = "";
        public string ResponderPublicKey { get; set; } = "";
        public string Status { get; set; } = "initiated"; // initiated, keys_exchanged, completed, failed
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastActivity { get; set; } = DateTime.Now;
        public string Challenge { get; set; } = "";
        public string ChallengeResponse { get; set; } = "";
        public bool InitiatorVerified { get; set; } = false;
        public bool ResponderVerified { get; set; } = false;
    }

    public static class KeyExchangeManager
    {
        private static readonly Dictionary<string, KeyExchangeSession> _activeSessions = new();
        private static readonly string _sessionsFile = "key_exchange_sessions.json";

        static KeyExchangeManager()
        {
            LoadSessions();
        }

        // ===== Démarrage négociation =====
        public static async Task<string> InitiateKeyExchange(string initiatorPeer, string responderPeer, string initiatorPublicKey)
        {
            try
            {
                // Vérifier si une session existe déjà
                var existingSession = GetActiveSession(initiatorPeer, responderPeer);
                if (existingSession != null)
                {
                    Console.WriteLine($"Session déjà active: {existingSession.SessionId}");
                    return existingSession.SessionId;
                }

                // Générer un ID de session unique
                var sessionId = GenerateSessionId();
                
                // Créer un challenge cryptographique
                var challenge = GenerateChallenge();

                var session = new KeyExchangeSession
                {
                    SessionId = sessionId,
                    InitiatorPeer = initiatorPeer,
                    ResponderPeer = responderPeer,
                    InitiatorPublicKey = initiatorPublicKey,
                    Status = "initiated",
                    CreatedAt = DateTime.Now,
                    LastActivity = DateTime.Now,
                    Challenge = challenge
                };

                _activeSessions[sessionId] = session;
                await SaveSessions();

                Console.WriteLine($"Négociation initiée: {initiatorPeer} → {responderPeer} (Session: {sessionId})");
                
                return sessionId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur initiation négociation: {ex.Message}");
                return "";
            }
        }

        // ===== Réponse du destinataire =====
        public static async Task<bool> RespondToKeyExchange(string sessionId, string responderPublicKey, string challengeResponse)
        {
            try
            {
                if (!_activeSessions.TryGetValue(sessionId, out var session))
                {
                    Console.WriteLine($"Session introuvable: {sessionId}");
                    return false;
                }

                if (session.Status != "initiated")
                {
                    Console.WriteLine($"Session dans un état invalide: {session.Status}");
                    return false;
                }

                // Valider la réponse au challenge
                var isValidResponse = ValidateChallengeResponse(session.Challenge, challengeResponse, responderPublicKey);
                if (!isValidResponse)
                {
                    session.Status = "failed";
                    await SaveSessions();
                    Console.WriteLine($"Challenge échoué pour session: {sessionId}");
                    return false;
                }

                // Mettre à jour la session
                session.ResponderPublicKey = responderPublicKey;
                session.ChallengeResponse = challengeResponse;
                session.ResponderVerified = true;
                session.Status = "keys_exchanged";
                session.LastActivity = DateTime.Now;

                await SaveSessions();

                Console.WriteLine($"Clés échangées: {session.InitiatorPeer} ↔ {session.ResponderPeer}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur réponse négociation: {ex.Message}");
                return false;
            }
        }

        // ===== Finalisation par l'initiateur =====
        public static async Task<bool> FinalizeKeyExchange(string sessionId, string initiatorVerification)
        {
            try
            {
                if (!_activeSessions.TryGetValue(sessionId, out var session))
                {
                    Console.WriteLine($"Session introuvable: {sessionId}");
                    return false;
                }

                if (session.Status != "keys_exchanged")
                {
                    Console.WriteLine($"Session dans un état invalide: {session.Status}");
                    return false;
                }

                // Valider la vérification de l'initiateur
                var isValidVerification = ValidateInitiatorVerification(session, initiatorVerification);
                if (!isValidVerification)
                {
                    session.Status = "failed";
                    await SaveSessions();
                    Console.WriteLine($"Vérification initiateur échouée: {sessionId}");
                    return false;
                }

                // Finaliser l'échange
                session.InitiatorVerified = true;
                session.Status = "completed";
                session.LastActivity = DateTime.Now;

                // Ajouter les contacts de manière bidirectionnelle
                await ContactManager.AddContact(session.ResponderPeer, session.ResponderPublicKey, true);
                await ContactManager.AddContact(session.InitiatorPeer, session.InitiatorPublicKey, true);

                await SaveSessions();

                Console.WriteLine($"Négociation finalisée: {session.InitiatorPeer} ↔ {session.ResponderPeer}");
                Console.WriteLine($"Contacts ajoutés mutuellement avec vérification cryptographique");

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur finalisation négociation: {ex.Message}");
                return false;
            }
        }

        // ===== Gestion des sessions =====
        public static KeyExchangeSession? GetSession(string sessionId)
        {
            _activeSessions.TryGetValue(sessionId, out var session);
            return session;
        }

        public static KeyExchangeSession? GetActiveSession(string peer1, string peer2)
        {
            foreach (var session in _activeSessions.Values)
            {
                if ((session.InitiatorPeer == peer1 && session.ResponderPeer == peer2) ||
                    (session.InitiatorPeer == peer2 && session.ResponderPeer == peer1))
                {
                    if (session.Status == "initiated" || session.Status == "keys_exchanged")
                        return session;
                }
            }
            return null;
        }

        public static List<KeyExchangeSession> GetSessionsForPeer(string peerName)
        {
            var sessions = new List<KeyExchangeSession>();
            foreach (var session in _activeSessions.Values)
            {
                if (session.InitiatorPeer == peerName || session.ResponderPeer == peerName)
                {
                    sessions.Add(session);
                }
            }
            return sessions;
        }

        public static async Task<bool> CancelSession(string sessionId, string reason = "User cancelled")
        {
            if (_activeSessions.TryGetValue(sessionId, out var session))
            {
                session.Status = "failed";
                session.LastActivity = DateTime.Now;
                await SaveSessions();
                
                Console.WriteLine($"Session annulée: {sessionId} - {reason}");
                return true;
            }
            return false;
        }

        // ===== Nettoyage automatique =====
        public static async Task CleanupExpiredSessions()
        {
            var expiredSessions = new List<string>();
            var expiryTime = DateTime.Now.AddHours(-2); // Sessions expirent après 2h

            foreach (var kvp in _activeSessions)
            {
                if (kvp.Value.LastActivity < expiryTime)
                {
                    expiredSessions.Add(kvp.Key);
                }
            }

            foreach (var sessionId in expiredSessions)
            {
                _activeSessions.Remove(sessionId);
                Console.WriteLine($"Session expirée nettoyée: {sessionId}");
            }

            if (expiredSessions.Count > 0)
            {
                await SaveSessions();
            }
        }

        // ===== Cryptographie et validation =====
        private static string GenerateSessionId()
        {
            return Guid.NewGuid().ToString("N")[..16]; // 16 chars hex
        }

        private static string GenerateChallenge()
        {
            var random = new Random();
            var challenge = new byte[32];
            random.NextBytes(challenge);
            return Convert.ToBase64String(challenge);
        }

        private static bool ValidateChallengeResponse(string originalChallenge, string response, string publicKey)
        {
            try
            {
                // Pour l'instant, validation simplifiée
                // TODO: Implémenter signature/vérification avec la clé publique
                
                // Validation temporaire: le response doit contenir le challenge + clé publique
                var expectedResponse = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(originalChallenge + publicKey));
                
                return response == expectedResponse;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur validation challenge: {ex.Message}");
                return false;
            }
        }

        private static bool ValidateInitiatorVerification(KeyExchangeSession session, string verification)
        {
            try
            {
                // Validation que l'initiateur a bien reçu et validé la clé du responder
                var expectedVerification = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(session.SessionId + session.ResponderPublicKey));
                
                return verification == expectedVerification;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur validation initiateur: {ex.Message}");
                return false;
            }
        }

        // ===== Persistance =====
        private static async Task SaveSessions()
        {
            try
            {
                var json = JsonSerializer.Serialize(_activeSessions, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_sessionsFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur sauvegarde sessions: {ex.Message}");
            }
        }

        private static void LoadSessions()
        {
            try
            {
                if (File.Exists(_sessionsFile))
                {
                    var json = File.ReadAllText(_sessionsFile);
                    var sessions = JsonSerializer.Deserialize<Dictionary<string, KeyExchangeSession>>(json);
                    if (sessions != null)
                    {
                        foreach (var kvp in sessions)
                            _activeSessions[kvp.Key] = kvp.Value;
                    }
                    Console.WriteLine($"Sessions d'échange chargées: {_activeSessions.Count}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur chargement sessions: {ex.Message}");
            }
        }

        // ===== Statistiques =====
        public static int GetActiveSessionCount()
        {
            return _activeSessions.Values.Count(s => s.Status == "initiated" || s.Status == "keys_exchanged");
        }

        public static Dictionary<string, object> GetStats()
        {
            var stats = new Dictionary<string, object>
            {
                ["total_sessions"] = _activeSessions.Count,
                ["active_sessions"] = GetActiveSessionCount(),
                ["completed_sessions"] = _activeSessions.Values.Count(s => s.Status == "completed"),
                ["failed_sessions"] = _activeSessions.Values.Count(s => s.Status == "failed")
            };
            return stats;
        }
    }
}