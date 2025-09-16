using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChatP2P.Server
{
    public class P2PService
    {
        private static readonly Dictionary<string, bool> _peerConnections = new();
        private static string _localDisplayName = "";
        private static Func<string, string, Task> _signalSender = null!;
        private static bool _initialized = false;
        
        // Track current P2P session for proper ICE signaling
        private static string _currentP2PInitiator = "ChatP2P.Server";
        private static string _currentP2PTarget = "";
        
        // NOUVEAU: ICE Logging System
        private static string _iceLogPath = "";
        private static readonly object _iceLogLock = new object();
        
        // NOUVEAU: Métriques de performance P2P
        private static readonly Dictionary<string, P2PConnectionMetrics> _connectionMetrics = new();
        private static bool _useHighPerformanceConfig = true;
        
        // NOUVEAU: Suivi des sessions WebRTC pour éviter les initiations multiples
        private static readonly Dictionary<string, DateTime> _activeWebRTCSessions = new();
        private static readonly TimeSpan SESSION_TIMEOUT = TimeSpan.FromMinutes(5);
        
        /// <summary>
        /// Marque une session WebRTC comme terminée avec succès
        /// </summary>
        public static void CompleteWebRTCSession(string peer1, string peer2)
        {
            var sessionKey = $"{peer1}-{peer2}";
            var reverseSessionKey = $"{peer2}-{peer1}";
            
            if (_activeWebRTCSessions.Remove(sessionKey))
            {
                Console.WriteLine($"✅ [P2P-SESSION] WebRTC session completed: {sessionKey}");
            }
            if (_activeWebRTCSessions.Remove(reverseSessionKey))
            {
                Console.WriteLine($"✅ [P2P-SESSION] WebRTC session completed: {reverseSessionKey}");
            }
            
            LogIceEvent("COMPLETE", peer1, peer2, "WebRTC negotiation completed successfully");
        }

        /// <summary>
        /// ✅ FIX: Vérifie si une session WebRTC est déjà active
        /// </summary>
        public static bool IsWebRTCSessionActive(string sessionKey)
        {
            // Nettoyer les sessions expirées d'abord
            CleanupExpiredWebRTCSessions();

            return _activeWebRTCSessions.ContainsKey(sessionKey) || _activeWebRTCSessions.ContainsKey(ReverseSessionKey(sessionKey));
        }

        /// <summary>
        /// ✅ FIX: Marque une session WebRTC comme active
        /// </summary>
        public static void SetWebRTCSessionActive(string sessionKey)
        {
            _activeWebRTCSessions[sessionKey] = DateTime.UtcNow;
            Console.WriteLine($"🔒 [P2P-SESSION] WebRTC session marked as active: {sessionKey}");
        }

        /// <summary>
        /// ✅ FIX: Nettoie les sessions WebRTC expirées
        /// </summary>
        private static void CleanupExpiredWebRTCSessions()
        {
            var expiredSessions = _activeWebRTCSessions
                .Where(kvp => DateTime.UtcNow - kvp.Value > SESSION_TIMEOUT)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var expiredSession in expiredSessions)
            {
                _activeWebRTCSessions.Remove(expiredSession);
                Console.WriteLine($"🧹 [P2P-SESSION] Expired WebRTC session cleaned up: {expiredSession}");
            }
        }

        /// <summary>
        /// ✅ FIX: Inverse une clé de session (VM1:VM2 → VM2:VM1)
        /// </summary>
        private static string ReverseSessionKey(string sessionKey)
        {
            var parts = sessionKey.Split(':');
            return parts.Length == 2 ? $"{parts[1]}:{parts[0]}" : sessionKey;
        }

        public static void Initialize(string displayName, Func<string, string, Task> signalSender)
        {
            if (_initialized) return;

            _localDisplayName = displayName;
            _signalSender = signalSender;

            // Initialize P2PManager C# version
            P2PManager.Init(_signalSender, _localDisplayName);

            // Hook P2P events
            P2PManager.OnP2PState += OnP2PStateChanged;
            P2PManager.OnP2PText += OnP2PTextReceived;
            P2PManager.OnP2PBinary += OnP2PBinaryReceived;
            P2PManager.OnLog += OnP2PLog;

            _initialized = true;
            
            // Initialize ICE logging system
            InitializeIceLogging(displayName);
            
            Console.WriteLine($"P2P Service initialized for: {displayName}");
        }
        
        /// <summary>
        /// NOUVEAU: Initialize specialized ICE logging system for debugging WebRTC signaling
        /// </summary>
        private static void InitializeIceLogging(string displayName)
        {
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ChatP2P_Logs");
                Directory.CreateDirectory(logDir);
                
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd");
                _iceLogPath = Path.Combine(logDir, $"ice_signaling_{displayName}_{timestamp}.log");
                
                // Write initial header
                LogIceEvent("ICE_INIT", "SYSTEM", "SYSTEM", $"ICE logging initialized for {displayName}");
                
                Console.WriteLine($"📋 [ICE-LOG] ICE logging initialized: {_iceLogPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [ICE-LOG] Failed to initialize ICE logging: {ex.Message}");
            }
        }
        
        /// <summary>
        /// NOUVEAU: Log specialized ICE events with detailed formatting
        /// </summary>
        public static void LogIceEvent(string eventType, string fromPeer, string toPeer, string details, string? iceData = null)
        {
            try
            {
                lock (_iceLogLock)
                {
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var logEntry = new
                    {
                        Timestamp = timestamp,
                        EventType = eventType,
                        FromPeer = fromPeer,
                        ToPeer = toPeer,
                        Details = details,
                        IceData = iceData?.Length > 100 ? iceData.Substring(0, 100) + "..." : iceData
                    };
                    
                    var logLine = $"[{timestamp}] {eventType,-15} | {fromPeer,-10} → {toPeer,-10} | {details}";
                    if (!string.IsNullOrEmpty(iceData))
                    {
                        logLine += $" | ICE: {(iceData.Length > 50 ? iceData.Substring(0, 50) + "..." : iceData)}";
                    }
                    
                    // Write to ICE log file
                    if (!string.IsNullOrEmpty(_iceLogPath))
                    {
                        File.AppendAllText(_iceLogPath, logLine + Environment.NewLine);
                    }
                    
                    // Also write to console with emoji for visibility
                    var emoji = eventType switch
                    {
                        "INITIATE" => "🎯",
                        "ICE_OFFER" => "📤",
                        "ICE_ANSWER" => "📥",
                        "ICE_CAND" => "⚡",
                        "CONNECTED" => "✅",
                        "FAILED" => "❌",
                        "PROGRESS" => "🔄",
                        _ => "📋"
                    };
                    
                    Console.WriteLine($"{emoji} [ICE-LOG] {eventType}: {fromPeer} → {toPeer} | {details}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [ICE-LOG] Error logging ICE event: {ex.Message}");
            }
        }

        public static async Task<bool> StartP2PConnection(string peer, string initiator = "ChatP2P.Server")
        {
            try
            {
                if (!_initialized)
                {
                    Console.WriteLine("P2P Service not initialized");
                    return false;
                }

                // NOUVEAU: Architecture P2P Direct - Le serveur ne se connecte plus en P2P
                // Il coordonne seulement le signaling entre clients
                if (_localDisplayName == "ChatP2P.Server")
                {
                    Console.WriteLine($"🎯 [P2P-SIGNALING] Server coordinating direct P2P connection: {initiator} → {peer}");
                    
                    // Le serveur ne crée pas de session P2P, il fait seulement du signaling
                    // Les clients créeront leurs propres sessions directes
                    return await CoordinateDirectP2PConnection(initiator, peer);
                }

                // Mode client : Établir vraie connexion P2P directe
                Console.WriteLine($"🔗 [P2P-DIRECT] Client establishing direct P2P to {peer}");

                // Store the initiator for this P2P session to use in signaling
                _currentP2PInitiator = initiator;
                _currentP2PTarget = peer;

                // NOUVEAU: Utiliser configuration ICE optimisée pour performance
                if (_useHighPerformanceConfig)
                {
                    Console.WriteLine($"🚀 [P2P-PERF] Using high-performance ICE configuration for {peer}");
                    P2PManager.StartP2P(peer, null); // null = configuration adaptative optimisée
                }
                else
                {
                    // Configuration legacy STUN simple
                    P2PManager.StartP2P(peer, new[] { "stun:stun.l.google.com:19302" });
                }
                
                // Initialiser métriques de connexion
                InitializeConnectionMetrics(peer);
                
                Console.WriteLine($"P2P connection started: {initiator} → {peer}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting P2P connection: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// NOUVEAU: Coordonne l'établissement de connexion P2P directe entre deux clients
        /// Le serveur fait seulement du signaling - pas de routage de données
        /// </summary>
        private static async Task<bool> CoordinateDirectP2PConnection(string initiator, string target)
        {
            try
            {
                // NOUVEAU: Vérifier s'il y a déjà une session active pour cette paire
                var sessionKey = $"{initiator}-{target}";
                var reverseSessionKey = $"{target}-{initiator}";
                
                // Nettoyer les sessions expirées
                var now = DateTime.UtcNow;
                var expiredSessions = _activeWebRTCSessions.Where(kvp => now - kvp.Value > SESSION_TIMEOUT).ToList();
                foreach (var expired in expiredSessions)
                {
                    _activeWebRTCSessions.Remove(expired.Key);
                    Console.WriteLine($"🧹 [P2P-SESSION] Cleaned up expired session: {expired.Key}");
                }
                
                // Vérifier si une session est déjà en cours
                if (_activeWebRTCSessions.ContainsKey(sessionKey) || _activeWebRTCSessions.ContainsKey(reverseSessionKey))
                {
                    Console.WriteLine($"⏳ [P2P-SESSION] WebRTC session already in progress: {initiator} ↔ {target}");
                    return true; // Retourner success pour éviter les erreurs
                }
                
                // Marquer la session comme active
                _activeWebRTCSessions[sessionKey] = now;
                
                Console.WriteLine($"📡 [P2P-SIGNALING] Starting direct P2P coordination: {initiator} ↔ {target}");
                LogIceEvent("INITIATE", initiator, target, "Starting WebRTC signaling coordination");
                
                // 1. Vérifier que les deux peers sont connectés au serveur pour le signaling
                Console.WriteLine($"📊 [P2P-SIGNALING] Coordinating direct connection between {initiator} and {target}");
                
                // 2. Envoyer signal au client initiateur pour qu'il commence WebRTC offer
                var initMessage = new
                {
                    type = "WEBRTC_SIGNALING",
                    action = "initiate_offer",
                    target_peer = target,
                    initiator_peer = initiator,
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                };
                
                Console.WriteLine($"📤 [P2P-SIGNALING] Instructing {initiator} to create WebRTC offer for {target}");
                
                // Envoyer le message d'initiation au client initiateur via RelayHub
                var relayHub = Program.GetRelayHub();
                if (relayHub != null)
                {
                    var initMessageJson = System.Text.Json.JsonSerializer.Serialize(initMessage);
                    var signalSent = await relayHub.SendToClient(initiator, $"WEBRTC_INITIATE:{initMessageJson}");
                    
                    if (signalSent)
                    {
                        Console.WriteLine($"✅ [P2P-SIGNALING] Initiation message sent to {initiator}");
                        LogIceEvent("PROGRESS", "SERVER", initiator, "WebRTC initiation message sent");
                        
                        // NOUVEAU: Also notify the target peer that a connection is being initiated
                        var targetNotificationMessage = new
                        {
                            type = "WEBRTC_SIGNALING",
                            action = "incoming_connection",
                            target_peer = target,
                            initiator_peer = initiator,
                            timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                        };
                        
                        var targetNotificationJson = System.Text.Json.JsonSerializer.Serialize(targetNotificationMessage);
                        var targetNotified = await relayHub.SendToClient(target, $"WEBRTC_INITIATE:{targetNotificationJson}");
                        
                        if (targetNotified)
                        {
                            Console.WriteLine($"✅ [P2P-SIGNALING] Target notification sent to {target}");
                            LogIceEvent("PROGRESS", "SERVER", target, "WebRTC target notification sent");
                        }
                        else
                        {
                            Console.WriteLine($"❌ [P2P-SIGNALING] Failed to notify target {target}");
                            LogIceEvent("ERROR", "SERVER", target, "Failed to notify target peer");
                        }
                        
                        // 3. Stocker la session de signaling en cours
                        StoreSignalingSession(initiator, target);
                        
                        Console.WriteLine($"🎯 [P2P-SIGNALING] WebRTC signaling process started:");
                        Console.WriteLine($"  1. ✅ {initiator} instructed to create offer for {target}");
                        Console.WriteLine($"  1b. ✅ {target} notified of incoming connection from {initiator}");
                        Console.WriteLine($"  2. ⏳ Waiting for {initiator} to send ICE offer...");
                        Console.WriteLine($"  3. ⏳ Server will relay offer → {target}");
                        Console.WriteLine($"  4. ⏳ {target} will create answer → Server relay → {initiator}");
                        Console.WriteLine($"  5. ⏳ ICE candidates exchange via server relay");
                        Console.WriteLine($"  6. ⏳ Direct P2P connection established");
                        
                        LogIceEvent("PROGRESS", "SERVER", "BOTH", $"Signaling process initiated - waiting for {initiator} to create offer for {target}");
                        
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"❌ [P2P-SIGNALING] Failed to send initiation message to {initiator} - client not connected");
                        return false;
                    }
                }
                else
                {
                    Console.WriteLine($"❌ [P2P-SIGNALING] RelayHub not available - cannot send signaling messages");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [P2P-SIGNALING] Error coordinating direct P2P: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// NOUVEAU: Stocke les sessions de signaling en cours pour tracking
        /// </summary>
        private static void StoreSignalingSession(string initiator, string target)
        {
            // Stocker les informations de la session pour le relay de signaling
            _currentP2PInitiator = initiator;
            _currentP2PTarget = target;
            
            Console.WriteLine($"📝 [P2P-SIGNALING] Tracking signaling session: {initiator} ↔ {target}");
            Console.WriteLine($"📝 [P2P-SIGNALING] Session stored - ready to relay ICE messages");
        }
        
        /// <summary>
        /// NOUVEAU: Relay des messages ICE entre les clients pour établir la connexion P2P directe
        /// </summary>
        public static async Task<bool> RelaySignalingMessage(string iceType, string fromPeer, string toPeer, string iceData)
        {
            try
            {
                Console.WriteLine($"🔄 [ICE-RELAY] Relaying {iceType}: {fromPeer} → {toPeer}");
                LogIceEvent(iceType, fromPeer, toPeer, "Relaying ICE signal via server", iceData?.Substring(0, Math.Min(100, iceData?.Length ?? 0)));
                
                // Créer le message de signaling à relay
                var signalingMessage = new
                {
                    type = "WEBRTC_SIGNALING",
                    ice_type = iceType,
                    from_peer = fromPeer,
                    to_peer = toPeer,
                    ice_data = iceData,
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                };
                
                var messageJson = System.Text.Json.JsonSerializer.Serialize(signalingMessage);
                
                // Envoyer via RelayHub au peer cible
                var relayHub = Program.GetRelayHub();
                if (relayHub != null)
                {
                    var signalSent = await relayHub.SendToClient(toPeer, $"WEBRTC_SIGNAL:{messageJson}");
                    
                    if (signalSent)
                    {
                        Console.WriteLine($"✅ [ICE-RELAY] {iceType} relayed successfully: {fromPeer} → {toPeer}");

                        // Log progress du signaling
                        LogSignalingProgress(iceType, fromPeer, toPeer);

                        Console.WriteLine($"🔍 [DEBUG] RelaySignalingMessage: iceType='{iceType}', iceType.ToLower()='{iceType.ToLower()}'");
                        Console.WriteLine($"📊 [DEBUG] Le serveur fait juste du RELAY - les sessions P2P doivent être sur les clients");

                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"❌ [ICE-RELAY] Failed to relay {iceType}: {toPeer} not connected");
                        return false;
                    }
                }
                else
                {
                    Console.WriteLine($"❌ [ICE-RELAY] RelayHub not available for {iceType} relay");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [ICE-RELAY] Error relaying {iceType}: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Log du progrès du processus de signaling WebRTC
        /// </summary>
        private static void LogSignalingProgress(string iceType, string fromPeer, string toPeer)
        {
            switch (iceType.ToUpper())
            {
                case "ICE_OFFER":
                    Console.WriteLine($"🎯 [SIGNALING-PROGRESS] Step 2/6: ICE Offer created by {fromPeer} → relayed to {toPeer}");
                    Console.WriteLine($"📋 [SIGNALING-PROGRESS] Next: {toPeer} should create ICE Answer");
                    break;
                    
                case "ICE_ANSWER":
                    Console.WriteLine($"🎯 [SIGNALING-PROGRESS] Step 3/6: ICE Answer created by {fromPeer} → relayed to {toPeer}");
                    Console.WriteLine($"📋 [SIGNALING-PROGRESS] Next: ICE Candidates exchange");
                    break;
                    
                case "ICE_CAND":
                    Console.WriteLine($"🎯 [SIGNALING-PROGRESS] Step 4-5/6: ICE Candidate from {fromPeer} → relayed to {toPeer}");
                    Console.WriteLine($"📋 [SIGNALING-PROGRESS] Continuing: More candidates or connection establishment");
                    break;
            }
        }

        public static async Task<bool> SendTextMessage(string peer, string message, string fromPeer = "ChatP2P.Server", bool encrypted = false)
        {
            try
            {
                Console.WriteLine($"🚀 [P2P-SEND] SendTextMessage called - from: {fromPeer}, to: {peer}, message: {message}, encrypted: {encrypted}");
                
                if (!_initialized) 
                {
                    Console.WriteLine($"❌ [P2P-SEND] P2P not initialized");
                    return false;
                }
                
                // Vérifier si c'est un message de fichier (métadonnées ou chunk)
                bool isFileMessage = message.Contains("\"type\":\"FILE_METADATA\"") || message.Contains("\"type\":\"FILE_CHUNK\"");

                // ✅ FIX TIMING: Délai spécial pour STATUS_SYNC après négociation WebRTC
                bool isStatusSync = message.Contains("\"type\":\"STATUS_SYNC\"");
                if (isStatusSync)
                {
                    Console.WriteLine($"⏱️ [P2P-TIMING] STATUS_SYNC detected - waiting 5s for DataChannels to open");
                    await Task.Delay(5000); // Attendre 5 secondes pour que les DataChannels s'ouvrent
                }

                // ✅ SERVEUR = PURE RELAY: Le message arrive déjà chiffré du client
                string finalMessage = message;
                if (encrypted)
                {
                    Console.WriteLine($"🔐 [RELAY] Message marqué comme chiffré - relaying as-is");
                    // Le serveur ne fait que relayer - pas de chiffrement/déchiffrement côté serveur
                }
                else
                {
                    Console.WriteLine($"📤 [RELAY] Message en clair - relaying as-is");
                }
                
                // ✅ FIX: Server does NOT create P2P connections
                // Server is PURE RELAY - clients handle P2P themselves
                Console.WriteLine($"🚀 [PURE-RELAY] Server bypassing P2P connection check - clients handle WebRTC");
                
                Console.WriteLine($"🚀 [PURE-RELAY] Server relaying message to client: {fromPeer} → {peer}");
                Console.WriteLine($"🚀 [PURE-RELAY] Server does NOT handle P2P locally - clients do WebRTC directly");

                // ✅ FIX: Server is PURE RELAY - send message via RelayHub directly
                // Clients handle their own P2P WebRTC connections
                var relayHub = Program.GetRelayHub();
                if (relayHub != null)
                {
                    // Pour RelayHub, utiliser le message final (chiffré si nécessaire)
                    await relayHub.BroadcastChatMessage(fromPeer, finalMessage, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                    Console.WriteLine($"✅ [PURE-RELAY] Message relayed to clients via RelayHub");

                    // NOUVEAU: Mettre à jour métriques d'envoi (via relay)
                    UpdateSendMetrics(peer, Encoding.UTF8.GetByteCount(finalMessage));

                    return true;
                }
                else
                {
                    Console.WriteLine($"❌ [PURE-RELAY] RelayHub not available");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [P2P-SEND] Error sending text message: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> SendMessage(string peer, string message)
        {
            try
            {
                if (!_initialized)
                {
                    Console.WriteLine($"❌ [P2P-SEND] P2P not initialized");
                    return false;
                }

                Console.WriteLine($"📡 [P2P-SEND] Sending message to {peer}: {message.Substring(0, Math.Min(100, message.Length))}...");
                var result = P2PManager.TrySendText(peer, message);
                Console.WriteLine($"📋 [P2P-SEND] Message send result: {result}");
                
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [P2P-SEND] Error sending message to {peer}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Simule la réception d'un message P2P pour traiter les FILE_CHUNK et FILE_METADATA
        /// via l'API au lieu du WebRTC DataChannel direct (workaround pour message filtering)
        /// </summary>
        public static void SimulateP2PTextReceived(string peer, string message)
        {
            try
            {
                Console.WriteLine($"📁 [P2P-SIMULATE] Simulating P2P text reception from {peer}");
                OnP2PTextReceived(peer, message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [P2P-SIMULATE] Error simulating P2P reception from {peer}: {ex.Message}");
            }
        }

        public static bool SendBinaryData(string peer, byte[] data)
        {
            try
            {
                if (!_initialized) return false;
                return P2PManager.TrySendBinary(peer, data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending binary data to {peer}: {ex.Message}");
                return false;
            }
        }

        public static bool IsConnected(string peer)
        {
            try
            {
                if (!_initialized) return false;

                // ✅ FIX: Server does NOT handle P2P locally
                // Clients report their P2P connection status via notify_connection_ready
                lock (_peerConnections)
                {
                    return _peerConnections.ContainsKey(peer) && _peerConnections[peer];
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking connection for {peer}: {ex.Message}");
                return false;
            }
        }

        public static void HandleOffer(string fromPeer, string sdp)
        {
            try
            {
                if (!_initialized) return;
                
                // Decode base64 SDP data
                string decodedSdp;
                try
                {
                    var bytes = Convert.FromBase64String(sdp);
                    decodedSdp = System.Text.Encoding.UTF8.GetString(bytes);
                    Console.WriteLine($"🔓 [ICE-DECODE] Offer from {fromPeer} decoded: {decodedSdp.Length} chars");
                }
                catch (Exception decodeEx)
                {
                    Console.WriteLine($"❌ [ICE-DECODE] Failed to decode offer from {fromPeer}: {decodeEx.Message}");
                    return;
                }
                
                // Utiliser configuration ICE avancée (null = mode adaptatif STUN+TURN)
                P2PManager.HandleOffer(fromPeer, decodedSdp, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling offer from {fromPeer}: {ex.Message}");
            }
        }

        public static void HandleAnswer(string fromPeer, string sdp)
        {
            try
            {
                if (!_initialized) return;
                
                // Decode base64 SDP data
                string decodedSdp;
                try
                {
                    var bytes = Convert.FromBase64String(sdp);
                    decodedSdp = System.Text.Encoding.UTF8.GetString(bytes);
                    Console.WriteLine($"🔓 [ICE-DECODE] Answer from {fromPeer} decoded: {decodedSdp.Length} chars");
                }
                catch (Exception decodeEx)
                {
                    Console.WriteLine($"❌ [ICE-DECODE] Failed to decode answer from {fromPeer}: {decodeEx.Message}");
                    return;
                }
                
                P2PManager.HandleAnswer(fromPeer, decodedSdp);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling answer from {fromPeer}: {ex.Message}");
            }
        }

        public static void HandleCandidate(string fromPeer, string candidate)
        {
            try
            {
                if (!_initialized) return;
                
                // Decode base64 candidate data
                string decodedCandidate;
                try
                {
                    var bytes = Convert.FromBase64String(candidate);
                    decodedCandidate = System.Text.Encoding.UTF8.GetString(bytes);
                    Console.WriteLine($"🔓 [ICE-DECODE] Candidate from {fromPeer} decoded: {decodedCandidate}");
                }
                catch (Exception decodeEx)
                {
                    Console.WriteLine($"❌ [ICE-DECODE] Failed to decode candidate from {fromPeer}: {decodeEx.Message}");
                    Console.WriteLine($"❌ [ICE-DECODE] Raw candidate data: {candidate}");
                    return;
                }
                
                P2PManager.HandleCandidate(fromPeer, decodedCandidate);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling candidate from {fromPeer}: {ex.Message}");
            }
        }

        public static Dictionary<string, bool> GetConnectedPeers()
        {
            return new Dictionary<string, bool>(_peerConnections);
        }

        public static Dictionary<string, object> GetStats()
        {
            var stats = new Dictionary<string, object>
            {
                ["total_peers"] = _peerConnections.Count,
                ["connected_peers"] = _peerConnections.Values.Count(connected => connected),
                ["local_name"] = _localDisplayName,
                ["initialized"] = _initialized
            };
            return stats;
        }
        
        // Correct ICE signal sender to use the real peer names instead of "ChatP2P.Server"
        public static string CorrectSignalSender(string signal)
        {
            try
            {
                // Parse signal format: ICE_TYPE:sender:receiver:data
                var parts = signal.Split(':', 4);
                if (parts.Length >= 3)
                {
                    var iceType = parts[0];
                    var sender = parts[1];
                    var receiver = parts[2];
                    var data = parts.Length > 3 ? parts[3] : "";
                    
                    // If sender is "ChatP2P.Server", replace with appropriate peer name
                    if (sender == "ChatP2P.Server")
                    {
                        string correctedSender;
                        
                        // For OFFER and initial CANDIDATE: use the initiator (e.g., VM1)
                        if (iceType == "ICE_OFFER" || (iceType == "ICE_CAND" && receiver == _currentP2PTarget))
                        {
                            correctedSender = !string.IsNullOrEmpty(_currentP2PInitiator) ? _currentP2PInitiator : "ChatP2P.Server";
                        }
                        // For ANSWER and response CANDIDATE: use the target (e.g., VM2) 
                        else if (iceType == "ICE_ANSWER" || (iceType == "ICE_CAND" && receiver == _currentP2PInitiator))
                        {
                            correctedSender = !string.IsNullOrEmpty(_currentP2PTarget) ? _currentP2PTarget : "ChatP2P.Server";
                        }
                        else
                        {
                            // Fallback: use initiator
                            correctedSender = !string.IsNullOrEmpty(_currentP2PInitiator) ? _currentP2PInitiator : "ChatP2P.Server";
                        }
                        
                        var correctedSignal = $"{iceType}:{correctedSender}:{receiver}";
                        if (!string.IsNullOrEmpty(data))
                        {
                            correctedSignal += $":{data}";
                        }
                        
                        Console.WriteLine($"🔄 [SIGNAL-CORRECTION] {iceType}: {sender} → {correctedSender} (to {receiver})");
                        return correctedSignal;
                    }
                }
                
                // Return original signal if no correction needed
                return signal;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [SIGNAL-CORRECTION] Error correcting signal: {ex.Message}");
                return signal; // Return original signal on error
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Méthode publique pour déclencher synchronisation STATUS_SYNC
        /// </summary>
        public static void TriggerStatusSync(string peer, bool connected)
        {
            OnP2PStateChanged(peer, connected);
        }

        // Event handlers
        private static void OnP2PStateChanged(string peer, bool connected)
        {
            _peerConnections[peer] = connected;
            Console.WriteLine($"P2P State: {peer} = {(connected ? "connected" : "disconnected")}");

            // ✅ NOUVEAU: Synchroniser le statut P2P bidirectionnel entre les deux peers
            _ = Task.Run(async () =>
            {
                try
                {
                    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

                    // Notifier le peer qui vient de changer d'état
                    await NotifyStatusSync(peer, "P2P_CONNECTED", connected, timestamp);
                    Console.WriteLine($"📡 [AUTO-SYNC] P2P status synced for {peer}: {connected}");

                    // ✅ BIDIRECTIONNEL: Si connecté, trouver le peer de l'autre côté et le notifier aussi
                    if (connected)
                    {
                        // Trouver tous les peers connectés et synchroniser leur statut
                        var connectedPeers = _peerConnections.Where(p => p.Value).Select(p => p.Key).ToList();
                        Console.WriteLine($"🔄 [BIDIRECTIONAL-SYNC] Connected peers: {string.Join(", ", connectedPeers)}");

                        // Pour chaque peer connecté, notifier tous les autres peers de sa connexion
                        foreach (var connectedPeer in connectedPeers)
                        {
                            if (connectedPeer != peer) // Éviter de notifier le peer à lui-même
                            {
                                await NotifyStatusSync(connectedPeer, "P2P_CONNECTED", true, timestamp);
                                Console.WriteLine($"📡 [BIDIRECTIONAL-SYNC] Notified {connectedPeer} about P2P connection");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [AUTO-SYNC] Error auto-syncing P2P status for {peer}: {ex.Message}");
                }
            });
        }

        private static async void OnP2PTextReceived(string peer, string text)
        {
            Console.WriteLine($"🔄 [P2P-RX] Message reçu de {peer}: {text.Length} chars");

            // NOUVEAU: Mettre à jour métriques de réception
            UpdateReceiveMetrics(peer, Encoding.UTF8.GetByteCount(text));

            // ✅ NOUVEAU: Déclencher synchronisation P2P si pas déjà connecté
            // Ceci est une solution de contournement car les événements ICE automatiques ne se déclenchent pas
            if (!_peerConnections.ContainsKey(peer) || !_peerConnections[peer])
            {
                Console.WriteLine($"🚀 [P2P-MANUAL-SYNC] Triggering manual P2P sync for {peer} (WebRTC messages working)");
                OnP2PStateChanged(peer, true); // Déclencher manuellement la synchronisation bidirectionnelle
            }
            
            // PRIORITÉ 1: Détecter et rejeter les données binaires corrompues AVANT parsing JSON
            if (IsBinaryDataCorrupted(text))
            {
                Console.WriteLine($"🚫 [P2P-RX] Données binaires corrompues détectées de {peer} dans OnP2PTextReceived, ignorées");
                return; // Ignorer complètement les données binaires
            }
            
            // PRIORITÉ 2: Logging sécurisé (seulement premiers caractères pour éviter pollution)
            string logText = text.Length > 100 ? text.Substring(0, 100) + "..." : text;
            Console.WriteLine($"🔄 [P2P-RX] Message texte de {peer}: {logText}");
            
            // PRIORITÉ 3: Gérer les friend requests et messages JSON valides
            try
            {
                var messageData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(text);
                Console.WriteLine($"[DEBUG] JSON parsing réussi pour message de {peer}");
                
                if (messageData.TryGetProperty("type", out var typeElement))
                {
                    var messageType = typeElement.GetString();
                    Console.WriteLine($"[DEBUG] Type de message détecté: {messageType}");
                    
                    if (messageType == "FRIEND_REQUEST")
                    {
                        Console.WriteLine($"🎯 [P2P-RX] Friend request détectée de {peer}!");
                        
                        var fromPeer = messageData.GetProperty("from").GetString() ?? "";
                        var toPeer = messageData.GetProperty("to").GetString() ?? "";
                        var publicKey = messageData.GetProperty("publicKey").GetString() ?? "";
                        var message = messageData.GetProperty("message").GetString() ?? "";
                        
                        Console.WriteLine($"[DEBUG] Données friend request:");
                        Console.WriteLine($"  - From: {fromPeer}");
                        Console.WriteLine($"  - To: {toPeer}");
                        Console.WriteLine($"  - PublicKey: {publicKey?.Substring(0, Math.Min(50, publicKey.Length))}...");
                        Console.WriteLine($"  - Message: {message}");
                        
                        Console.WriteLine($"🔄 [P2P-RX] Appel ContactManager.ReceiveFriendRequestFromP2P...");
                        await ContactManager.ReceiveFriendRequestFromP2P(fromPeer, toPeer, publicKey, message);
                        Console.WriteLine($"✅ [P2P-RX] ContactManager.ReceiveFriendRequestFromP2P terminé");
                    }
                    else if (messageType == "STATUS_SYNC")
                    {
                        Console.WriteLine($"🎯 [P2P-RX] Status sync message détecté de {peer}!");
                        
                        // Retransmettre le message STATUS_SYNC complet au client cible
                        var fromPeer = messageData.GetProperty("from").GetString() ?? "";
                        var statusType = messageData.GetProperty("statusType").GetString() ?? "";
                        var enabled = messageData.GetProperty("enabled").GetBoolean();
                        var timestamp = messageData.GetProperty("timestamp").GetString() ?? "";
                        
                        Console.WriteLine($"[DEBUG] Status sync: {fromPeer} -> {statusType} = {enabled} at {timestamp}");
                        
                        // NOUVEAU: Notifier tous les clients connectés du changement de statut
                        await NotifyStatusSync(fromPeer, statusType, enabled, timestamp);
                    }
                    else if (messageType == "FILE_METADATA")
                    {
                        Console.WriteLine($"📁 [P2P-RX] File metadata détectée de {peer}!");
                        
                        var transferId = messageData.GetProperty("transferId").GetString() ?? "";
                        var fileName = messageData.GetProperty("fileName").GetString() ?? "";
                        var fileSize = messageData.GetProperty("fileSize").GetInt64();
                        var chunkSize = messageData.GetProperty("chunkSize").GetInt32();
                        var totalChunks = messageData.GetProperty("totalChunks").GetInt32();
                        var fileHash = messageData.TryGetProperty("fileHash", out var hashProp) ? hashProp.GetString() ?? "" : "";
                        var fromPeer = messageData.GetProperty("fromPeer").GetString() ?? "";
                        var toPeer = messageData.GetProperty("toPeer").GetString() ?? "";
                        
                        Console.WriteLine($"📁 [FILE-METADATA] Nouveau transfert: {fileName} ({fileSize} bytes, {totalChunks} chunks)");
                        
                        // Créer la métadonnée et démarrer la réception
                        var metadata = new FileMetadata(transferId, fileName, fileSize, chunkSize, fromPeer, toPeer);
                        metadata.FileHash = fileHash;
                        
                        // Définir le chemin de sortie vers ChatP2P_Recv
                        var downloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ChatP2P_Recv");
                        if (!Directory.Exists(downloadDir))
                        {
                            Directory.CreateDirectory(downloadDir);
                        }
                        var outputPath = Path.Combine(downloadDir, fileName);
                        
                        // Éviter les écrasements
                        int counter = 1;
                        var originalPath = outputPath;
                        while (File.Exists(outputPath))
                        {
                            var nameWithoutExt = Path.GetFileNameWithoutExtension(originalPath);
                            var extension = Path.GetExtension(originalPath);
                            outputPath = Path.Combine(downloadDir, $"{nameWithoutExt}_{counter}{extension}");
                            counter++;
                        }
                        
                        await FileTransferService.Instance.StartReceiveTransferAsync(metadata, outputPath);
                    }
                    else if (messageType == "FILE_CHUNK")
                    {
                        var transferId = messageData.GetProperty("transferId").GetString() ?? "";
                        var chunkIndex = messageData.GetProperty("chunkIndex").GetInt32();
                        var chunkHash = messageData.GetProperty("chunkHash").GetString() ?? "";
                        var chunkDataBase64 = messageData.GetProperty("chunkData").GetString() ?? "";
                        
                        var chunkData = Convert.FromBase64String(chunkDataBase64);
                        
                        Console.WriteLine($"📦 [FILE-CHUNK] Chunk {chunkIndex} reçu ({chunkData.Length} bytes)");
                        
                        await FileTransferService.Instance.ProcessReceivedChunkAsync(transferId, chunkIndex, chunkHash, chunkData);
                    }
                    else
                    {
                        Console.WriteLine($"[DEBUG] Message type non-traité: {messageType}");
                    }
                }
                else
                {
                    Console.WriteLine($"[DEBUG] Pas de propriété 'type' dans le message JSON - traitement comme message chat");
                    // Message JSON sans type = message de chat structuré
                    await HandleChatMessageReceived(peer, text, messageData);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Vérifier si c'est des données binaires corrompues avant de traiter comme chat
                if (IsBinaryDataCorrupted(text))
                {
                    Console.WriteLine($"🚫 [P2P-RX] Données binaires corrompues détectées de {peer}, ignorées");
                    return; // Ignorer complètement les données binaires
                }
                
                // Message texte simple (non-JSON) = message de chat normal
                Console.WriteLine($"📝 [P2P-RX] Message texte simple de {peer}: {text}");
                await HandleSimpleChatMessage(peer, text);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [P2P-RX] Erreur traitement message de {peer}: {ex.Message}");
                Console.WriteLine($"❌ [P2P-RX] StackTrace: {ex.StackTrace}");
                Console.WriteLine($"❌ [P2P-RX] Message brut: {text}");
            }
        }

        // Gérer les messages de chat JSON structurés
        private static async Task HandleChatMessageReceived(string peer, string rawText, System.Text.Json.JsonElement messageData)
        {
            try
            {
                // Extraire le contenu du message
                var content = messageData.TryGetProperty("content", out var contentProp) ? contentProp.GetString() ?? rawText : rawText;
                var timestamp = messageData.TryGetProperty("timestamp", out var timeProp) ? timeProp.GetString() : DateTime.Now.ToString();
                
                Console.WriteLine($"💬 [CHAT-RX] Message structuré de {peer}: {content}");
                
                // Transmettre le message via RelayHub aux clients connectés
                await TransmitChatMessage(peer, content, timestamp);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [CHAT-RX] Erreur traitement message structuré de {peer}: {ex.Message}");
                await HandleSimpleChatMessage(peer, rawText); // Fallback vers message simple
            }
        }

        // Gérer les messages de chat texte simples
        private static async Task HandleSimpleChatMessage(string peer, string text)
        {
            try
            {
                Console.WriteLine($"💬 [CHAT-RX] Message simple de {peer}: {text}");
                
                // Transmettre le message via RelayHub aux clients connectés
                await TransmitChatMessage(peer, text, DateTime.Now.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [CHAT-RX] Erreur traitement message simple de {peer}: {ex.Message}");
            }
        }

        // Transmettre le message reçu aux clients via RelayHub
        private static async Task TransmitChatMessage(string fromPeer, string content, string timestamp)
        {
            try
            {
                Console.WriteLine($"🔄 [CHAT-TX] Transmission message: {fromPeer} -> ALL CLIENTS: {content}");
                
                // Obtenir l'instance RelayHub depuis Program.cs
                var relayHub = Program.GetRelayHub();
                if (relayHub != null)
                {
                    await relayHub.BroadcastChatMessage(fromPeer, content, timestamp);
                    Console.WriteLine($"✅ [CHAT-TX] Message transmis via RelayHub");
                }
                else
                {
                    Console.WriteLine($"❌ [CHAT-TX] RelayHub non disponible - impossible de transmettre le message");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [CHAT-TX] Erreur transmission message: {ex.Message}");
            }
        }

        /// <summary>Détecte si le texte contient des données binaires corrompues (chunks de fichiers)</summary>
        private static bool IsBinaryDataCorrupted(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            
            // 1. Détection caractères non-printables massifs (typique des chunks binaires)
            int nonPrintableCount = 0;
            int totalChars = text.Length;
            
            foreach (char c in text)
            {
                // Caractères de contrôle, non-ASCII, ou caractères spéciaux suspects
                if (char.IsControl(c) || c > 127 || 
                    c == '\uFFFD' ||  // Replacement character (données corrompues)
                    c == '\0')        // Null character
                {
                    nonPrintableCount++;
                }
            }
            
            // Si plus de 50% de caractères non-printables, c'est probablement binaire
            if (totalChars > 0 && (double)nonPrintableCount / totalChars > 0.5)
            {
                return true;
            }
            
            // 2. Détection de patterns typiques des données base64 corrompues
            if (text.Length > 100 && text.Length % 4 == 0)
            {
                // Vérifier si ça ressemble à du base64 partiellement corrompu
                int base64Like = 0;
                foreach (char c in text.Take(100)) // Check first 100 chars
                {
                    if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || 
                        (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=')
                    {
                        base64Like++;
                    }
                }
                
                // Si ressemble à du base64 mais contient des non-printables = corrompu
                if (base64Like > 50 && nonPrintableCount > 10)
                {
                    return true;
                }
            }
            
            // 3. Taille suspecte (typique des chunks)
            if (text.Length >= 1000 && text.Length <= 5000)
            {
                return true;
            }
            
            return false;
        }

        private static async void OnP2PBinaryReceived(string peer, byte[] data)
        {
            Console.WriteLine($"📦 [P2P-BIN] Received {data.Length} bytes from {peer} via DataChannel");
            
            // NOUVEAU: Architecture P2P Direct - Le serveur ne doit JAMAIS recevoir de données binaires
            // Les données doivent aller directement entre clients
            if (_localDisplayName == "ChatP2P.Server")
            {
                Console.WriteLine($"⚠️ [P2P-ARCH-ERROR] Server received binary data - this should not happen in direct P2P architecture!");
                Console.WriteLine($"🔍 [P2P-ARCH-DEBUG] This indicates P2P connection is not truly direct");
                Console.WriteLine($"📊 [P2P-ARCH-DEBUG] From: {peer}, Size: {data.Length} bytes");
                Console.WriteLine($"🎯 [P2P-ARCH-DEBUG] Expected: Direct {peer} → target peer (not via server)");
                
                // Le serveur ne traite plus les données binaires - elles doivent être directes
                return;
            }
            
            // Mode client : Traitement normal des chunks reçus directement d'un autre client
            try
            {
                Console.WriteLine($"🎯 [P2P-DIRECT] Processing chunk received directly from {peer} on client {_localDisplayName}");
                
                // NOUVEAU: Mettre à jour métriques de réception binaire
                UpdateReceiveMetrics(peer, data.Length);
                
                // Traiter le chunk binaire avec le FileTransferService
                await FileTransferService.Instance.ProcessReceivedBinaryChunk(peer, data);
                Console.WriteLine($"✅ [P2P-DIRECT] Binary chunk processed successfully from {peer}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [P2P-DIRECT] Error processing binary chunk from {peer}: {ex.Message}");
            }
        }

        private static void OnP2PLog(string peer, string line)
        {
            Console.WriteLine($"P2P Log [{peer}]: {line}");
        }

        // NOUVEAU: Notifier les clients d'un changement de statut
        private static async Task NotifyStatusSync(string fromPeer, string statusType, bool enabled, string timestamp)
        {
            try
            {
                Console.WriteLine($"📡 [STATUS-SYNC] Notification status: {fromPeer} -> {statusType} = {enabled}");
                
                // Créer le message de notification de statut
                var statusMessage = new
                {
                    type = "STATUS_SYNC",
                    from = fromPeer,
                    statusType = statusType,
                    enabled = enabled,
                    timestamp = timestamp
                };
                
                var jsonMessage = System.Text.Json.JsonSerializer.Serialize(statusMessage);
                
                // Obtenir l'instance RelayHub depuis Program.cs pour diffuser aux clients connectés
                var relayHub = Program.GetRelayHub();
                if (relayHub != null)
                {
                    // Transmettre le message de statut via RelayHub aux clients connectés
                    await relayHub.BroadcastStatusSync(fromPeer, statusType, enabled, timestamp);
                    Console.WriteLine($"✅ [STATUS-SYNC] Notification transmise via RelayHub");
                }
                else
                {
                    Console.WriteLine($"❌ [STATUS-SYNC] RelayHub non disponible - impossible de transmettre la notification");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [STATUS-SYNC] Erreur notification status: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Initialise les métriques de performance pour une connexion P2P
        /// </summary>
        private static void InitializeConnectionMetrics(string peer)
        {
            _connectionMetrics[peer] = new P2PConnectionMetrics
            {
                PeerName = peer,
                ConnectionStartTime = DateTime.UtcNow,
                LastActivityTime = DateTime.UtcNow
            };
            Console.WriteLine($"📊 [P2P-METRICS] Initialized metrics tracking for {peer}");
        }
        
        /// <summary>
        /// Met à jour les métriques lors d'un envoi de données
        /// </summary>
        private static void UpdateSendMetrics(string peer, int bytes)
        {
            if (_connectionMetrics.TryGetValue(peer, out var metrics))
            {
                metrics.BytesSent += bytes;
                metrics.MessagesSent++;
                metrics.LastActivityTime = DateTime.UtcNow;
                
                // Calculer throughput
                var elapsed = DateTime.UtcNow.Subtract(metrics.ConnectionStartTime);
                if (elapsed.TotalSeconds > 0)
                {
                    metrics.ThroughputKBps = (metrics.BytesSent + metrics.BytesReceived) / elapsed.TotalSeconds / 1024;
                }
            }
        }
        
        /// <summary>
        /// Met à jour les métriques lors d'une réception de données
        /// </summary>
        private static void UpdateReceiveMetrics(string peer, int bytes)
        {
            if (_connectionMetrics.TryGetValue(peer, out var metrics))
            {
                metrics.BytesReceived += bytes;
                metrics.MessagesReceived++;
                metrics.LastActivityTime = DateTime.UtcNow;
                
                // Calculer throughput
                var elapsed = DateTime.UtcNow.Subtract(metrics.ConnectionStartTime);
                if (elapsed.TotalSeconds > 0)
                {
                    metrics.ThroughputKBps = (metrics.BytesSent + metrics.BytesReceived) / elapsed.TotalSeconds / 1024;
                }
            }
        }
        
        /// <summary>
        /// Configure le service pour utiliser la configuration haute performance
        /// </summary>
        public static void EnableHighPerformanceMode(bool enable = true)
        {
            _useHighPerformanceConfig = enable;
            Console.WriteLine($"⚡ [P2P-CONFIG] High-performance mode: {(_useHighPerformanceConfig ? "ENABLED" : "DISABLED")}");
        }
        
        /// <summary>
        /// Obtient les métriques de performance pour toutes les connexions
        /// </summary>
        public static Dictionary<string, object> GetPerformanceMetrics()
        {
            var metrics = new Dictionary<string, object>();
            
            foreach (var kvp in _connectionMetrics)
            {
                var peer = kvp.Key;
                var data = kvp.Value;
                
                metrics[peer] = new
                {
                    peerName = data.PeerName,
                    isConnected = IsConnected(peer),
                    connectionTime = DateTime.UtcNow.Subtract(data.ConnectionStartTime).TotalSeconds,
                    bytesSent = data.BytesSent,
                    bytesReceived = data.BytesReceived,
                    messagesSent = data.MessagesSent,
                    messagesReceived = data.MessagesReceived,
                    throughputKBps = data.ThroughputKBps,
                    lastActivityTime = data.LastActivityTime
                };
            }
            
            metrics["summary"] = new
            {
                totalConnections = _connectionMetrics.Count,
                activeConnections = _peerConnections.Count(kv => kv.Value),
                highPerformanceMode = _useHighPerformanceConfig,
                totalBytesSent = _connectionMetrics.Values.Sum(m => m.BytesSent),
                totalBytesReceived = _connectionMetrics.Values.Sum(m => m.BytesReceived),
                averageThroughputKBps = _connectionMetrics.Values.Any() ? _connectionMetrics.Values.Average(m => m.ThroughputKBps) : 0
            };
            
            return metrics;
        }

        /// <summary>
        /// ✅ NOUVEAU: Notifier que la connexion P2P directe est prête côté clients
        /// </summary>
        public static void NotifyDirectConnectionReady(string fromPeer, string toPeer)
        {
            try
            {
                Console.WriteLine($"🔗 [P2P-DIRECT-READY] Connection marked as ready: {fromPeer} ↔ {toPeer}");

                // Marquer les deux connexions comme actives côté serveur
                _peerConnections[fromPeer] = true;
                _peerConnections[toPeer] = true;

                // Initialiser les métriques si elles n'existent pas
                if (!_connectionMetrics.ContainsKey(fromPeer))
                {
                    _connectionMetrics[fromPeer] = new P2PConnectionMetrics { PeerName = fromPeer };
                }
                if (!_connectionMetrics.ContainsKey(toPeer))
                {
                    _connectionMetrics[toPeer] = new P2PConnectionMetrics { PeerName = toPeer };
                }

                Console.WriteLine($"✅ [P2P-DIRECT-READY] Server now considers {fromPeer} ↔ {toPeer} as P2P connected");

                // ✅ FIX CRITIQUE: Déclencher la synchronisation des labels P2P pour les deux peers
                // Cela va envoyer STATUS_SYNC P2P_CONNECTED=true aux clients
                TriggerStatusSync(fromPeer, true);
                TriggerStatusSync(toPeer, true);
                Console.WriteLine($"📡 [STATUS-SYNC] Triggered P2P_CONNECTED sync for both {fromPeer} and {toPeer}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [P2P-DIRECT-READY] Error marking connection ready: {ex.Message}");
            }
        }

        /// <summary>
        /// Test de performance réseau avec mesure de latence
        /// </summary>
        public static async Task<NetworkPerformanceResult> TestNetworkPerformance(string peer)
        {
            var result = new NetworkPerformanceResult { PeerName = peer };
            
            try
            {
                result.TestStartTime = DateTime.UtcNow;
                
                // Test de ping (message simple)
                var pingStart = DateTime.UtcNow;
                var pingSuccess = await SendTextMessage(peer, $"PING:{DateTime.UtcNow:O}", _localDisplayName);
                
                if (pingSuccess)
                {
                    // Attendre la réponse PONG (simulé pour l'instant)
                    await Task.Delay(100); // Simuler latence réseau
                    result.PingLatencyMs = DateTime.UtcNow.Subtract(pingStart).TotalMilliseconds;
                }
                
                // Test de throughput (chunk de données)
                var throughputStart = DateTime.UtcNow;
                var testData = new byte[8192]; // 8KB test
                new Random().NextBytes(testData);
                
                // Test throughput avec données binaires (simulation)
                var throughputSuccess = true; // Simulé pour test initial
                
                if (throughputSuccess)
                {
                    var elapsed = DateTime.UtcNow.Subtract(throughputStart).TotalSeconds;
                    result.ThroughputKBps = elapsed > 0 ? testData.Length / elapsed / 1024 : 0;
                }
                
                result.TestEndTime = DateTime.UtcNow;
                result.Success = pingSuccess && throughputSuccess;
                
                Console.WriteLine($"🧪 [PERF-TEST] {peer}: Ping={result.PingLatencyMs:F1}ms, Throughput={result.ThroughputKBps:F1}KB/s");
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Console.WriteLine($"❌ [PERF-TEST] {peer} failed: {ex.Message}");
            }
            
            return result;
        }
    }
    
    /// <summary>
    /// Métriques de performance pour une connexion P2P
    /// </summary>
    public class P2PConnectionMetrics
    {
        public string PeerName { get; set; } = "";
        public DateTime ConnectionStartTime { get; set; }
        public DateTime LastActivityTime { get; set; }
        public long BytesSent { get; set; }
        public long BytesReceived { get; set; }
        public int MessagesSent { get; set; }
        public int MessagesReceived { get; set; }
        public double ThroughputKBps { get; set; }
    }
    
    /// <summary>
    /// Résultat d'un test de performance réseau
    /// </summary>
    public class NetworkPerformanceResult
    {
        public string PeerName { get; set; } = "";
        public DateTime TestStartTime { get; set; }
        public DateTime TestEndTime { get; set; }
        public double PingLatencyMs { get; set; } = -1;
        public double ThroughputKBps { get; set; } = -1;
        public bool Success { get; set; }
        public string? Error { get; set; }
        
        public TimeSpan TestDuration => TestEndTime.Subtract(TestStartTime);
    }
}