using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace ChatP2P.Server
{
    // ===== PROTOCOL TAGS =====
    public static class ProtocolTags
    {
        // Base protocol
        public const string TAG_PEERS = "PEERS:";
        public const string TAG_MSG = "MSG:";
        public const string TAG_PRIV = "PRIV:";
        public const string TAG_NAME = "NAME:";
        
        // Friend requests (nouveau - canal ouvert)
        public const string TAG_FRIEND_REQ = "FRIEND_REQ:";
        public const string TAG_FRIEND_ACCEPT = "FRIEND_ACCEPT:";
        public const string TAG_FRIEND_ACCEPT_DUAL = "FRIEND_ACCEPT_DUAL:";
        public const string TAG_FRIEND_REJECT = "FRIEND_REJECT:";
        
        // ICE/P2P Signaling
        public const string TAG_ICE_OFFER = "ICE_OFFER:";
        public const string TAG_ICE_ANSWER = "ICE_ANSWER:";
        public const string TAG_ICE_CAND = "ICE_CAND:";
        
        // NOUVEAU: WebRTC Signaling Protocol
        public const string TAG_WEBRTC_INITIATE = "WEBRTC_INITIATE:";
        public const string TAG_WEBRTC_SIGNAL = "WEBRTC_SIGNAL:";
        
        // File Transfer
        public const string TAG_FILEMETA = "FILEMETA:";
        public const string TAG_FILECHUNK = "FILECHUNK:";
        public const string TAG_FILEEND = "FILEEND:";
        
        // Crypto Relay (optionnel)
        public const string TAG_ENC_HELLO = "[ENCHELLO]";
        public const string TAG_ENC_ACK = "[ENCACK]";
        public const string TAG_ENC_PREFIX = "ENC1:";
    }

    // ===== CLIENT CONNECTION =====
    public class ClientConnection
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string? Name { get; set; }
        public TcpClient TcpClient { get; set; }
        public NetworkStream Stream { get; set; }
        public StreamWriter Writer { get; set; }
        public StreamReader Reader { get; set; }
        public bool IsTrustedChannel { get; set; } = false; // false = friend requests only, true = full access
        public DateTime LastActivity { get; set; } = DateTime.Now;
        public CancellationTokenSource CancellationToken { get; set; } = new();

        public ClientConnection(TcpClient client)
        {
            TcpClient = client;
            Stream = client.GetStream();
            Writer = new StreamWriter(Stream, Encoding.UTF8) { AutoFlush = true };
            Reader = new StreamReader(Stream, Encoding.UTF8);
        }

        public async Task SendAsync(string message)
        {
            try
            {
                Console.WriteLine($"[DEBUG] SendAsync to {Name}: Sending message: {message}");
                await Writer.WriteLineAsync(message);
                await Writer.FlushAsync();  // Force flush to ensure delivery
                LastActivity = DateTime.Now;
                Console.WriteLine($"[DEBUG] SendAsync to {Name}: Message sent successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error sending to {Name}: {ex.Message}");
                Console.WriteLine($"❌ StackTrace: {ex.StackTrace}");
            }
        }

        public void Dispose()
        {
            try
            {
                CancellationToken?.Cancel();
                Writer?.Dispose();
                Reader?.Dispose();
                Stream?.Dispose();
                TcpClient?.Dispose();
            }
            catch { }
        }
    }

    // ===== RELAY HUB CENTRALISÉ =====
    public class RelayHub
    {
        private readonly int _friendRequestPort;  // Canal ouvert (ex: 7777)
        private readonly int _messagesPort;       // Canal messages (ex: 8888)
        private readonly int _filesPort;          // ✅ NOUVEAU: Canal fichiers (ex: 8891)

        // Messages clients (port 8888)
        private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
        private readonly ConcurrentDictionary<string, string> _nameToId = new(StringComparer.OrdinalIgnoreCase);

        // Friend Request clients (port 7777)
        private readonly ConcurrentDictionary<string, ClientConnection> _friendRequestClients = new();
        private readonly ConcurrentDictionary<string, string> _friendRequestNameToId = new(StringComparer.OrdinalIgnoreCase);

        // ✅ NOUVEAU: File Transfer clients (port 8891)
        private readonly ConcurrentDictionary<string, ClientConnection> _fileClients = new();
        private readonly ConcurrentDictionary<string, string> _fileNameToId = new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, string> _fileRoutes = new(); // transferId -> destName

        private TcpListener? _friendRequestListener;
        private TcpListener? _messagesListener;
        private TcpListener? _filesListener;        // ✅ NOUVEAU: Listener fichiers
        private bool _isRunning = false;

        // Events pour notifier l'extérieur
        public event Action<List<string>>? PeerListUpdated;
        public event Action<string, string>? MessageArrived;
        public event Action<string, string, string>? PrivateArrived;
        public event Action<string>? FileSignal;
        public event Action<string>? IceSignal;
        public event Action<string, string, string, string>? FriendRequestReceived; // from, to, publicKey, message

        public RelayHub(int friendRequestPort = 7777, int messagesPort = 8888, int filesPort = 8891)
        {
            _friendRequestPort = friendRequestPort;
            _messagesPort = messagesPort;
            _filesPort = filesPort;
        }

        public async Task StartAsync()
        {
            if (_isRunning) return;

            // NOUVEAU: Initialiser les clés tunnel persistantes du serveur
            await InitializeServerTunnelKeys();

            _friendRequestListener = new TcpListener(IPAddress.Any, _friendRequestPort);
            _messagesListener = new TcpListener(IPAddress.Any, _messagesPort);
            _filesListener = new TcpListener(IPAddress.Any, _filesPort);  // ✅ NOUVEAU

            _friendRequestListener.Start();
            _messagesListener.Start();
            _filesListener.Start();  // ✅ NOUVEAU
            _isRunning = true;

            Console.WriteLine($"RelayHub started:");
            Console.WriteLine($"  - Friend Requests: *:{_friendRequestPort} (open access)");
            Console.WriteLine($"  - Messages/P2P: *:{_messagesPort} (trusted only)");
            Console.WriteLine($"  - File Transfers: *:{_filesPort} (high bandwidth)");  // ✅ NOUVEAU

            // ✅ NOUVEAU: Accepter connexions sur les trois canaux
            _ = Task.Run(async () => await AcceptFriendRequestConnections());
            _ = Task.Run(async () => await AcceptMessageConnections());
            _ = Task.Run(async () => await AcceptFileConnections());  // ✅ NOUVEAU
        }

        public async Task StopAsync()
        {
            _isRunning = false;
            
            // Fermer tous les clients
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }
            _clients.Clear();
            _nameToId.Clear();

            _friendRequestListener?.Stop();
            _messagesListener?.Stop();
            _filesListener?.Stop();  // ✅ NOUVEAU
        }

        // ===== CANAL FRIEND REQUESTS (PORT OUVERT) =====
        private async Task AcceptFriendRequestConnections()
        {
            if (_friendRequestListener == null) return;

            while (_isRunning)
            {
                try
                {
                    var tcpClient = await _friendRequestListener.AcceptTcpClientAsync();
                    var clientConn = new ClientConnection(tcpClient)
                    {
                        IsTrustedChannel = false // Canal ouvert
                    };
                    
                    _friendRequestClients[clientConn.Id] = clientConn;
                    Console.WriteLine($"Friend Request client connected: {clientConn.Id}");
                    
                    _ = Task.Run(async () => await HandleFriendRequestClient(clientConn));
                }
                catch (ObjectDisposedException)
                {
                    break; // Arrêt normal
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error accepting friend request connection: {ex.Message}");
                }
            }
        }

        // ===== CANAL MESSAGES (PORT RESTREINT) =====  
        private async Task AcceptMessageConnections()
        {
            if (_messagesListener == null) return;

            while (_isRunning)
            {
                try
                {
                    var tcpClient = await _messagesListener.AcceptTcpClientAsync();
                    var clientConn = new ClientConnection(tcpClient)
                    {
                        IsTrustedChannel = true // Canal restreint
                    };
                    
                    _clients[clientConn.Id] = clientConn;
                    Console.WriteLine($"Messages client connected: {clientConn.Id}");
                    
                    _ = Task.Run(async () => await HandleMessageClient(clientConn));
                }
                catch (ObjectDisposedException)
                {
                    break; // Arrêt normal
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error accepting message connection: {ex.Message}");
                }
            }
        }

        // ✅ NOUVEAU: CANAL FILE TRANSFERS (PORT HAUTE PERFORMANCE)
        private async Task AcceptFileConnections()
        {
            if (_filesListener == null) return;

            while (_isRunning)
            {
                try
                {
                    var tcpClient = await _filesListener.AcceptTcpClientAsync();
                    var clientConn = new ClientConnection(tcpClient)
                    {
                        IsTrustedChannel = true // Canal haute performance
                    };

                    _fileClients[clientConn.Id] = clientConn;
                    Console.WriteLine($"File transfer client connected: {clientConn.Id}");

                    _ = Task.Run(async () => await HandleFileClient(clientConn));
                }
                catch (ObjectDisposedException)
                {
                    break; // Arrêt normal
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error accepting file connection: {ex.Message}");
                }
            }
        }

        // ===== GESTION CLIENT FRIEND REQUESTS =====
        private async Task HandleFriendRequestClient(ClientConnection client)
        {
            try
            {
                while (_isRunning && !client.CancellationToken.Token.IsCancellationRequested)
                {
                    var message = await client.Reader.ReadLineAsync();
                    if (message == null) break;

                    client.LastActivity = DateTime.Now;
                    await ProcessFriendRequestMessage(client, message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling friend request client {client.Id}: {ex.Message}");
            }
            finally
            {
                DisconnectClient(client);
            }
        }

        // ===== GESTION CLIENT MESSAGES =====
        private async Task HandleMessageClient(ClientConnection client)
        {
            try
            {
                while (_isRunning && !client.CancellationToken.Token.IsCancellationRequested)
                {
                    var message = await client.Reader.ReadLineAsync();
                    if (message == null) break;

                    client.LastActivity = DateTime.Now;
                    await ProcessTrustedMessage(client, message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling message client {client.Id}: {ex.Message}");
            }
            finally
            {
                DisconnectClient(client);
            }
        }

        // ✅ NOUVEAU: GESTION CLIENT FILE TRANSFERS
        private async Task HandleFileClient(ClientConnection client)
        {
            try
            {
                while (_isRunning && !client.CancellationToken.Token.IsCancellationRequested)
                {
                    var message = await client.Reader.ReadLineAsync();
                    if (message == null) break;

                    client.LastActivity = DateTime.Now;
                    // ✅ OPTIMISÉ: Traitement fichiers sans logs excessifs
                    await ProcessFileMessage(client, message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling file client {client.Id}: {ex.Message}");
            }
            finally
            {
                DisconnectFileClient(client);
            }
        }

        // ===== TRAITEMENT MESSAGES FRIEND REQUESTS =====
        private async Task ProcessFriendRequestMessage(ClientConnection client, string message)
        {
            // Seuls les tags autorisés sur le canal ouvert
            if (message.StartsWith(ProtocolTags.TAG_NAME))
            {
                await HandleNameRegistration(client, message);
            }
            else if (message.StartsWith(ProtocolTags.TAG_FRIEND_REQ))
            {
                await HandleFriendRequest(client, message);
            }
            else if (message.StartsWith(ProtocolTags.TAG_FRIEND_ACCEPT))
            {
                await HandleFriendAccept(client, message);
            }
            else if (message.StartsWith(ProtocolTags.TAG_FRIEND_ACCEPT_DUAL))
            {
                await HandleFriendAcceptDual(client, message);
            }
            else if (message.StartsWith(ProtocolTags.TAG_FRIEND_REJECT))
            {
                await HandleFriendReject(client, message);
            }
            else if (message.Contains("{\"type\":\"SECURE_HANDSHAKE_REQUEST\"") || message.Contains("{\"type\":\"SECURE_TUNNEL_MESSAGE\"") || message.Contains("{\"type\":\"TUNNEL_KEY_EXCHANGE\""))
            {
                // NOUVEAU: Messages JSON pour le tunnel sécurisé PQC (peut contenir multiples messages concaténés)
                Console.WriteLine($"🔐 [SECURE-TUNNEL] Detected JSON message(s): {message.Substring(0, Math.Min(100, message.Length))}...");
                await HandleConcatenatedSecureTunnelMessages(client, message);
            }
            else
            {
                // DEBUG: Analyser les messages non reconnus
                Console.WriteLine($"❌ [DEBUG] Unauthorized message from {client.Name}");
                Console.WriteLine($"❌ [DEBUG] Message length: {message.Length}");
                Console.WriteLine($"❌ [DEBUG] First 10 chars: '{message.Substring(0, Math.Min(10, message.Length))}'");
                Console.WriteLine($"❌ [DEBUG] Starts with '?{{': {message.StartsWith("?{")}");
                Console.WriteLine($"❌ [DEBUG] Starts with '{{': {message.StartsWith("{")}");
                Console.WriteLine($"❌ [DEBUG] Full message: {message}");
            }
        }

        // ===== TRAITEMENT MESSAGES TRUSTED =====
        private async Task ProcessTrustedMessage(ClientConnection client, string message)
        {
            // Tous les tags autorisés sur le canal restreint
            if (message.StartsWith(ProtocolTags.TAG_NAME))
            {
                await HandleNameRegistration(client, message);
            }
            else if (message.StartsWith(ProtocolTags.TAG_PRIV))
            {
                await HandlePrivateMessage(client, message);
            }
            else if (message.StartsWith(ProtocolTags.TAG_MSG))
            {
                await HandlePublicMessage(client, message);
            }
            else if (message.StartsWith(ProtocolTags.TAG_ICE_OFFER) || 
                     message.StartsWith(ProtocolTags.TAG_ICE_ANSWER) ||
                     message.StartsWith(ProtocolTags.TAG_ICE_CAND))
            {
                await HandleIceSignaling(client, message);
            }
            else if (message.StartsWith(ProtocolTags.TAG_WEBRTC_INITIATE) ||
                     message.StartsWith(ProtocolTags.TAG_WEBRTC_SIGNAL))
            {
                await HandleWebRTCSignaling(client, message);
            }
            else if (message.StartsWith(ProtocolTags.TAG_FILEMETA) ||
                     message.StartsWith(ProtocolTags.TAG_FILECHUNK) ||
                     message.StartsWith(ProtocolTags.TAG_FILEEND))
            {
                await HandleFileTransfer(client, message);
            }
            else
            {
                Console.WriteLine($"Unknown message from {client.Name}: {message}");
            }
        }

        // ===== HANDLERS SPÉCIFIQUES =====

        private async Task HandleNameRegistration(ClientConnection client, string message)
        {
            var name = message.Substring(ProtocolTags.TAG_NAME.Length).Trim();
            if (string.IsNullOrEmpty(name)) return;

            // Unregister old name if exists
            if (!string.IsNullOrEmpty(client.Name))
            {
                // Supprimer de l'ancien dictionnaire
                if (client.IsTrustedChannel)
                {
                    _nameToId.TryRemove(client.Name, out _);
                }
                else
                {
                    _friendRequestNameToId.TryRemove(client.Name, out _);
                }
            }

            client.Name = name;
            
            // Ajouter au bon dictionnaire selon le type de canal
            if (client.IsTrustedChannel)
            {
                _nameToId[name] = client.Id;
            }
            else
            {
                _friendRequestNameToId[name] = client.Id;
            }
            
            Console.WriteLine($"Client registered: {name} on {(client.IsTrustedChannel ? "trusted" : "friend-req")} channel");
            
            // Send peer list update (only for trusted channel)
            if (client.IsTrustedChannel)
            {
                await BroadcastPeerList();
            }
        }

        private async Task HandleFriendRequest(ClientConnection client, string message)
        {
            // Format: FRIEND_REQ:fromPeer:toPeer:publicKey:message
            var parts = message.Substring(ProtocolTags.TAG_FRIEND_REQ.Length).Split(':', 4);
            if (parts.Length < 4) return;

            var fromPeer = parts[0];
            var toPeer = parts[1]; 
            var publicKey = parts[2];
            var requestMessage = parts[3];

            Console.WriteLine($"Friend request: {fromPeer} → {toPeer}");

            // DEBUG: Vérifier les clients connectés sur le canal Friend Request
            Console.WriteLine($"[DEBUG] Recherche client '{toPeer}' dans {_friendRequestNameToId.Count} clients friend request connectés");
            foreach (var kvp in _friendRequestNameToId)
            {
                Console.WriteLine($"[DEBUG] Client friend request connecté: '{kvp.Key}' -> ID: {kvp.Value}");
            }

            // Trouver le client destinataire et lui transmettre via le canal Friend Request
            if (_friendRequestNameToId.TryGetValue(toPeer, out var targetClientId))
            {
                Console.WriteLine($"[DEBUG] Client '{toPeer}' trouvé avec ID: {targetClientId} sur canal friend request");
                
                if (_friendRequestClients.TryGetValue(targetClientId, out var targetClient))
                {
                    Console.WriteLine($"[DEBUG] Envoi message à {toPeer} via client ID {targetClientId}");
                    var fullMessage = $"{ProtocolTags.TAG_FRIEND_REQ}{fromPeer}:{toPeer}:{publicKey}:{requestMessage}";
                    Console.WriteLine($"[DEBUG] Message complet: {fullMessage}");
                    
                    await targetClient.SendAsync(fullMessage);
                    
                    // NOUVEAU: Marquer comme transmise dans ContactManager
                    ContactManager.MarkRequestAsTransmitted(fromPeer, toPeer);
                    Console.WriteLine($"✅ Friend request transmitted and marked: {fromPeer} → {toPeer}");
                }
                else
                {
                    Console.WriteLine($"❌ [DEBUG] Client ID {targetClientId} non trouvé dans _friendRequestClients");
                }
            }
            else
            {
                Console.WriteLine($"❌ [DEBUG] Client '{toPeer}' non trouvé dans _friendRequestNameToId");
            }

            // Event pour l'extérieur
            FriendRequestReceived?.Invoke(fromPeer, toPeer, publicKey, requestMessage);
        }

        private async Task HandleFriendAccept(ClientConnection client, string message)
        {
            // Format: FRIEND_ACCEPT:fromPeer:toPeer OR FRIEND_ACCEPT:fromPeer:toPeer:pqcPublicKey
            var parts = message.Substring(ProtocolTags.TAG_FRIEND_ACCEPT.Length).Split(':', 3);
            if (parts.Length < 2) return;

            var fromPeer = parts[0];
            var toPeer = parts[1];
            var pqcPublicKey = parts.Length >= 3 ? parts[2] : null; // ✅ PQC key optional

            Console.WriteLine($"Friend request accepted: {fromPeer} ← {toPeer}");

            // NOUVEAU: Accepter la demande côté serveur (ajout contact + suppression request)
            try
            {
                var success = await ContactManager.AcceptContactRequest(toPeer, fromPeer);
                if (success)
                {
                    Console.WriteLine($"✅ Friend request acceptée et supprimée côté serveur: {toPeer} ↔ {fromPeer}");
                }
                else
                {
                    Console.WriteLine($"⚠️ Friend request introuvable côté serveur: {fromPeer} → {toPeer}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Erreur acceptation friend request: {ex.Message}");
            }

            // ✅ PQC: Transmettre la réponse au demandeur original avec clé PQC si fournie
            if (_friendRequestNameToId.TryGetValue(fromPeer, out var targetClientId) &&
                _friendRequestClients.TryGetValue(targetClientId, out var targetClient))
            {
                Console.WriteLine($"[DEBUG] Envoi FRIEND_ACCEPT à {fromPeer} via canal friend request (ID: {targetClientId})");
                var acceptMessage = string.IsNullOrEmpty(pqcPublicKey)
                    ? $"{ProtocolTags.TAG_FRIEND_ACCEPT}{fromPeer}:{toPeer}"
                    : $"{ProtocolTags.TAG_FRIEND_ACCEPT}{fromPeer}:{toPeer}:{pqcPublicKey}";
                await targetClient.SendAsync(acceptMessage);
                Console.WriteLine($"[DEBUG] FRIEND_ACCEPT envoyé avec succès à {fromPeer} (PQC: {!string.IsNullOrEmpty(pqcPublicKey)})");
            }
            else
            {
                Console.WriteLine($"[ERROR] Impossible de trouver {fromPeer} sur canal friend request pour FRIEND_ACCEPT");
            }
        }

        private async Task HandleFriendAcceptDual(ClientConnection client, string message)
        {
            // Format: FRIEND_ACCEPT_DUAL:fromPeer:toPeer:ed25519Key:pqcKey
            var parts = message.Substring(ProtocolTags.TAG_FRIEND_ACCEPT_DUAL.Length).Split(':', 4);
            if (parts.Length < 4) return;

            var fromPeer = parts[0];
            var toPeer = parts[1];
            var ed25519Key = parts[2];
            var pqcKey = parts[3];

            Console.WriteLine($"Dual key friend request accepted: {fromPeer} ← {toPeer}");

            // Accept the request on server side (add contact + remove request)
            try
            {
                var success = await ContactManager.AcceptContactRequest(toPeer, fromPeer);
                if (success)
                {
                    Console.WriteLine($"✅ Dual key friend request accepted and removed server-side: {toPeer} ↔ {fromPeer}");
                }
                else
                {
                    Console.WriteLine($"⚠️ Dual key friend request not found server-side: {fromPeer} → {toPeer}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error accepting dual key friend request: {ex.Message}");
            }

            // Forward the dual key acceptance to the original requester (toPeer, not fromPeer!)
            if (_friendRequestNameToId.TryGetValue(toPeer, out var targetClientId) &&
                _friendRequestClients.TryGetValue(targetClientId, out var targetClient))
            {
                Console.WriteLine($"[DEBUG] Sending FRIEND_ACCEPT_DUAL to {toPeer} via friend request channel (ID: {targetClientId})");
                var acceptMessage = $"{ProtocolTags.TAG_FRIEND_ACCEPT_DUAL}{fromPeer}:{toPeer}:{ed25519Key}:{pqcKey}";
                await targetClient.SendAsync(acceptMessage);
                Console.WriteLine($"[DEBUG] FRIEND_ACCEPT_DUAL sent successfully to {toPeer} with both keys");
            }
            else
            {
                Console.WriteLine($"[ERROR] Unable to find {toPeer} on friend request channel for FRIEND_ACCEPT_DUAL");
            }
        }

        private async Task HandleFriendReject(ClientConnection client, string message)
        {
            // Similar à HandleFriendAccept mais pour rejet
            var parts = message.Substring(ProtocolTags.TAG_FRIEND_REJECT.Length).Split(':');
            if (parts.Length < 2) return;

            var fromPeer = parts[0];
            var toPeer = parts[1];

            Console.WriteLine($"Friend request rejected: {fromPeer} ← {toPeer}");

            // NOUVEAU: Supprimer la demande de la liste côté serveur
            try
            {
                var success = await ContactManager.RejectContactRequest(fromPeer, toPeer);
                if (success)
                {
                    Console.WriteLine($"✅ Friend request supprimée côté serveur: {fromPeer} → {toPeer}");
                }
                else
                {
                    Console.WriteLine($"⚠️ Friend request introuvable côté serveur: {fromPeer} → {toPeer}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Erreur suppression friend request: {ex.Message}");
            }

            // Transmettre la réponse au demandeur original
            if (_friendRequestNameToId.TryGetValue(fromPeer, out var targetClientId) &&
                _friendRequestClients.TryGetValue(targetClientId, out var targetClient))
            {
                Console.WriteLine($"[DEBUG] Envoi FRIEND_REJECT à {fromPeer} via canal friend request (ID: {targetClientId})");
                await targetClient.SendAsync($"{ProtocolTags.TAG_FRIEND_REJECT}{fromPeer}:{toPeer}");
                Console.WriteLine($"[DEBUG] FRIEND_REJECT envoyé avec succès à {fromPeer}");
            }
            else
            {
                Console.WriteLine($"[ERROR] Impossible de trouver {fromPeer} sur canal friend request pour FRIEND_REJECT");
            }
        }

        /// <summary>
        /// NOUVEAU: Gestion des messages tunnel concaténés (plusieurs JSON dans un string)
        /// </summary>
        private async Task HandleConcatenatedSecureTunnelMessages(ClientConnection client, string concatenatedMessage)
        {
            try
            {
                Console.WriteLine($"🔐 [CONCAT-PARSER] Processing concatenated message: {concatenatedMessage.Length} chars");

                // Diviser les messages JSON concaténés en cherchant les patterns ?{ et {
                var messages = new List<string>();
                var currentPos = 0;

                while (currentPos < concatenatedMessage.Length)
                {
                    // Chercher le prochain début de JSON (? ou {)
                    var nextJson = concatenatedMessage.IndexOfAny(new[] { '?', '{' }, currentPos);
                    if (nextJson == -1) break;

                    // Si c'est un ?, passer au { (peut y avoir plusieurs ?)
                    if (concatenatedMessage[nextJson] == '?')
                    {
                        // Ignorer tous les ? consécutifs
                        while (nextJson < concatenatedMessage.Length && concatenatedMessage[nextJson] == '?')
                        {
                            nextJson++;
                        }

                        if (nextJson >= concatenatedMessage.Length || concatenatedMessage[nextJson] != '{')
                            break;
                    }

                    // Trouver la fin du JSON en comptant les {}
                    var braceCount = 0;
                    var jsonStart = nextJson;
                    var jsonEnd = nextJson;

                    for (int i = nextJson; i < concatenatedMessage.Length; i++)
                    {
                        if (concatenatedMessage[i] == '{') braceCount++;
                        else if (concatenatedMessage[i] == '}') braceCount--;

                        if (braceCount == 0)
                        {
                            jsonEnd = i;
                            break;
                        }
                    }

                    if (braceCount == 0)
                    {
                        var singleMessage = concatenatedMessage.Substring(jsonStart, jsonEnd - jsonStart + 1);
                        messages.Add(singleMessage);
                        Console.WriteLine($"🔐 [CONCAT-PARSER] Extracted JSON: {singleMessage.Substring(0, Math.Min(50, singleMessage.Length))}...");
                    }

                    currentPos = jsonEnd + 1;
                }

                Console.WriteLine($"🔐 [CONCAT-PARSER] Found {messages.Count} JSON messages");

                // Traiter chaque message individuellement
                foreach (var singleMessage in messages)
                {
                    await HandleSecureTunnelMessage(client, singleMessage);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [CONCAT-PARSER] Error parsing concatenated messages: {ex.Message}");
            }
        }

        /// <summary>
        /// NOUVEAU: Gestion du tunnel sécurisé PQC pour friend requests
        /// </summary>
        private async Task HandleSecureTunnelMessage(ClientConnection client, string message)
        {
            try
            {
                // Nettoyer le message (supprimer les ? en début si présents)
                var cleanMessage = message.TrimStart('?');

                Console.WriteLine($"🔐 [SECURE-TUNNEL] Processing secure tunnel message from {client.Name}");
                Console.WriteLine($"🔐 [SECURE-TUNNEL] Message: {cleanMessage.Substring(0, Math.Min(100, cleanMessage.Length))}...");

                var jsonData = JsonSerializer.Deserialize<JsonElement>(cleanMessage);
                var messageType = jsonData.GetProperty("type").GetString();

                switch (messageType)
                {
                    case "SECURE_HANDSHAKE_REQUEST":
                        await HandleSecureHandshakeRequest(client, jsonData);
                        break;

                    case "TUNNEL_KEY_EXCHANGE":
                        await HandleTunnelKeyExchange(client, jsonData);
                        break;

                    case "SECURE_TUNNEL_MESSAGE":
                        await HandleSecureTunneledMessage(client, jsonData);
                        break;

                    default:
                        Console.WriteLine($"❌ [SECURE-TUNNEL] Unknown message type: {messageType}");
                        break;
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"❌ [SECURE-TUNNEL] JSON parsing error: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [SECURE-TUNNEL] Error handling secure tunnel message: {ex.Message}");
            }
        }

        /// <summary>
        /// Gestion de l'échange de clés publiques P2P
        /// </summary>
        private async Task HandleTunnelKeyExchange(ClientConnection client, JsonElement keyData)
        {
            try
            {
                var fromPeer = keyData.GetProperty("fromPeer").GetString();
                var toPeer = keyData.GetProperty("toPeer").GetString();
                var publicKey = keyData.GetProperty("publicKey").GetString();

                Console.WriteLine($"🔑 [KEY-EXCHANGE] Relaying public key from {fromPeer} to {toPeer}");

                // Relayer l'échange de clés au destinataire via le canal friend request
                if (!string.IsNullOrEmpty(toPeer))
                {
                    var relayMessage = new
                    {
                        type = "TUNNEL_KEY_EXCHANGE",
                        fromPeer = fromPeer,
                        toPeer = toPeer,
                        publicKey = publicKey
                    };

                    var relayJson = JsonSerializer.Serialize(relayMessage);

                    // Envoyer au destinataire spécifique
                    if (_friendRequestNameToId.TryGetValue(toPeer, out var targetClientId) &&
                        _friendRequestClients.TryGetValue(targetClientId, out var targetClient))
                    {
                        await targetClient.SendAsync(relayJson);
                        Console.WriteLine($"✅ [KEY-EXCHANGE] Public key relayed to {toPeer}");
                    }
                    else
                    {
                        Console.WriteLine($"❌ [KEY-EXCHANGE] Target peer {toPeer} not found");
                    }
                }
                else
                {
                    Console.WriteLine($"❌ [KEY-EXCHANGE] No target peer specified");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [KEY-EXCHANGE] Error handling key exchange: {ex.Message}");
            }
        }

        /// <summary>
        /// Gestion de l'établissement du tunnel sécurisé (handshake)
        /// </summary>
        private async Task HandleSecureHandshakeRequest(ClientConnection client, JsonElement requestData)
        {
            try
            {
                var clientName = requestData.GetProperty("clientName").GetString();
                var clientPublicKey = requestData.GetProperty("publicKey").GetString();

                Console.WriteLine($"🤝 [SECURE-TUNNEL] Handshake request from {clientName}");
                Console.WriteLine($"🔑 [SECURE-TUNNEL] Client public key: {clientPublicKey!.Substring(0, 40)}...");

                // NOUVEAU: Utiliser la clé publique persistante du serveur
                if (_serverTunnelPublicKey == null)
                {
                    await InitializeServerTunnelKeys();
                }

                // Envoyer la réponse avec notre clé publique persistante
                var handshakeResponse = new
                {
                    type = "SECURE_HANDSHAKE_RESPONSE",
                    publicKey = Convert.ToBase64String(_serverTunnelPublicKey!)
                };

                var responseJson = JsonSerializer.Serialize(handshakeResponse);

                // CORRECTION: Utiliser le format texte pour compatibilité avec StreamReader client
                await client.SendAsync(responseJson);

                Console.WriteLine($"✅ [SECURE-TUNNEL] Handshake response sent to {clientName} (using persistent key)");

                // Stocker le nom du client
                if (!string.IsNullOrEmpty(clientName))
                {
                    client.Name = clientName;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [SECURE-TUNNEL] Error in handshake: {ex.Message}");
            }
        }

        /// <summary>
        /// Gestion des messages chiffrés via tunnel sécurisé
        /// </summary>
        private async Task HandleSecureTunneledMessage(ClientConnection client, JsonElement tunnelData)
        {
            try
            {
                var encryptedDataB64 = tunnelData.GetProperty("encryptedData").GetString();
                // Le targetPeer n'est pas dans le message JSON du tunnel, on doit le déduire
                // Pour l'instant, on utilise une approche simple : relayer à tous les autres clients connectés
                var encryptedBytes = Convert.FromBase64String(encryptedDataB64!);

                Console.WriteLine($"🔓 [SECURE-TUNNEL] Received encrypted message ({encryptedBytes.Length} bytes)");

                // Pour simplifier l'implémentation, on va utiliser une approche relay opaque:
                // Le serveur retransmet le message chiffré au destinataire qui le déchiffrera

                // NOUVEAU: Transmettre le message chiffré à tous les autres clients connectés
                // Le message est chiffré, donc seul le bon destinataire pourra le déchiffrer
                var relayMessage = new
                {
                    type = "SECURE_TUNNEL_MESSAGE",
                    encryptedData = encryptedDataB64,
                    fromPeer = client.Name ?? "Unknown"
                };

                var relayJson = JsonSerializer.Serialize(relayMessage);

                // Relayer à tous les clients friend request connectés (sauf l'expéditeur)
                int relayedCount = 0;
                foreach (var kvp in _friendRequestNameToId)
                {
                    var peerName = kvp.Key;
                    var peerId = kvp.Value;

                    // Ne pas relayer à soi-même
                    if (peerName == client.Name) continue;

                    if (_friendRequestClients.TryGetValue(peerId, out var targetClient))
                    {
                        await targetClient.SendAsync(relayJson);
                        relayedCount++;
                        Console.WriteLine($"✅ [SECURE-TUNNEL] Encrypted message relayed to {peerName}");
                    }
                }

                if (relayedCount > 0)
                {
                    Console.WriteLine($"✅ [SECURE-TUNNEL] Encrypted message relayed to {relayedCount} peers");
                }
                else
                {
                    Console.WriteLine($"⚠️ [SECURE-TUNNEL] No peers available to relay encrypted message");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [SECURE-TUNNEL] Error handling tunneled message: {ex.Message}");
            }
        }

        private async Task HandlePrivateMessage(ClientConnection client, string message)
        {
            // Format: PRIV:fromName:destName:message
            var parts = message.Substring(ProtocolTags.TAG_PRIV.Length).Split(':', 3);
            if (parts.Length < 3) return;

            var fromName = parts[0];
            var destName = parts[1];
            var body = parts[2];

            Console.WriteLine($"Private message: {fromName} → {destName}");

            // Router vers le destinataire
            if (_nameToId.TryGetValue(destName, out var targetClientId) &&
                _clients.TryGetValue(targetClientId, out var targetClient))
            {
                await targetClient.SendAsync(message);
            }

            // Event pour l'extérieur
            PrivateArrived?.Invoke(fromName, destName, body);
        }

        private async Task HandlePublicMessage(ClientConnection client, string message)
        {
            var body = message.Substring(ProtocolTags.TAG_MSG.Length);
            Console.WriteLine($"Public message from {client.Name}: {body}");

            // Broadcast à tous les clients trusted
            await BroadcastToTrustedClients(message, client.Id);
            
            // Event pour l'extérieur
            MessageArrived?.Invoke(client.Name ?? "Unknown", body);
        }

        private async Task HandleIceSignaling(ClientConnection client, string message)
        {
            Console.WriteLine($"🔄 [ICE-LEGACY] Legacy signaling from {client.Name}: {message.Substring(0, Math.Min(50, message.Length))}...");
            
            try 
            {
                // Parse le message ICE: ICE_OFFER:from:to:sdp_data
                string[] parts = message.Split(':', 4);
                if (parts.Length >= 3)
                {
                    string iceType = parts[0];        // ICE_OFFER, ICE_ANSWER, ICE_CAND
                    string fromPeer = parts[1];       // VM1
                    string toPeer = parts[2];         // VM2
                    
                    Console.WriteLine($"🎯 [ICE-LEGACY] Routing {iceType} from {fromPeer} to {toPeer}");
                    
                    // Trouver le client destinataire
                    var targetClient = _clients.Values.FirstOrDefault(c => c.Name == toPeer);
                    if (targetClient != null)
                    {
                        await targetClient.SendAsync(message);
                        Console.WriteLine($"✅ [ICE-LEGACY] {iceType} routé vers {toPeer}");
                    }
                    else
                    {
                        Console.WriteLine($"❌ [ICE-LEGACY] Client {toPeer} non trouvé pour {iceType}");
                    }
                }
                else
                {
                    Console.WriteLine($"⚠️ [ICE-LEGACY] Format message invalide: {message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [ICE-LEGACY] Erreur routing signaling: {ex.Message}");
            }
            
            // Event pour l'extérieur (conservé pour compatibilité)
            IceSignal?.Invoke(message);
        }
        
        /// <summary>
        /// NOUVEAU: Gestion des messages WebRTC signaling structurés
        /// </summary>
        private async Task HandleWebRTCSignaling(ClientConnection client, string message)
        {
            Console.WriteLine($"🚀 [WEBRTC-SIGNALING] Message from {client.Name}: {message.Substring(0, Math.Min(80, message.Length))}...");
            
            try
            {
                if (message.StartsWith(ProtocolTags.TAG_WEBRTC_INITIATE))
                {
                    // Message d'initiation WebRTC: WEBRTC_INITIATE:{json}
                    var jsonData = message.Substring(ProtocolTags.TAG_WEBRTC_INITIATE.Length);
                    Console.WriteLine($"🎯 [WEBRTC-INITIATE] Processing initiation message: {jsonData}");
                    
                    // Le client doit traiter cette demande d'initiation
                    // Pas de routage nécessaire - le client va créer l'offer et l'envoyer via ice_signal API
                    Console.WriteLine($"📋 [WEBRTC-INITIATE] Client {client.Name} received WebRTC initiation request");
                }
                else if (message.StartsWith(ProtocolTags.TAG_WEBRTC_SIGNAL))
                {
                    // Message de signaling WebRTC: WEBRTC_SIGNAL:{json}
                    var jsonData = message.Substring(ProtocolTags.TAG_WEBRTC_SIGNAL.Length);
                    
                    try
                    {
                        // Parser le JSON pour extraire les informations de routage
                        var signalData = JsonSerializer.Deserialize<JsonElement>(jsonData);
                        
                        if (signalData.TryGetProperty("to_peer", out var toPeerElement))
                        {
                            var toPeer = toPeerElement.GetString();
                            var iceType = signalData.TryGetProperty("ice_type", out var iceTypeEl) ? iceTypeEl.GetString() : "UNKNOWN";
                            var fromPeer = signalData.TryGetProperty("from_peer", out var fromPeerEl) ? fromPeerEl.GetString() : "UNKNOWN";
                            
                            Console.WriteLine($"🎯 [WEBRTC-SIGNAL] Routing {iceType}: {fromPeer} → {toPeer}");
                            
                            // Trouver le client destinataire
                            var targetClient = _clients.Values.FirstOrDefault(c => c.Name == toPeer);
                            if (targetClient != null)
                            {
                                await targetClient.SendAsync(message);
                                Console.WriteLine($"✅ [WEBRTC-SIGNAL] {iceType} routé vers {toPeer}");
                                
                                // Log du progrès de signaling
                                LogWebRTCSignalingProgress(iceType, fromPeer, toPeer);
                            }
                            else
                            {
                                Console.WriteLine($"❌ [WEBRTC-SIGNAL] Client {toPeer} non connecté pour {iceType}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"❌ [WEBRTC-SIGNAL] Champ 'to_peer' manquant dans: {jsonData}");
                        }
                    }
                    catch (JsonException ex)
                    {
                        Console.WriteLine($"❌ [WEBRTC-SIGNAL] Erreur parsing JSON: {ex.Message}");
                        Console.WriteLine($"❌ [WEBRTC-SIGNAL] JSON données: {jsonData}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [WEBRTC-SIGNALING] Erreur traitement WebRTC signaling: {ex.Message}");
                Console.WriteLine($"❌ [WEBRTC-SIGNALING] Message: {message}");
            }
        }
        
        /// <summary>
        /// Log du progrès de signaling WebRTC
        /// </summary>
        private void LogWebRTCSignalingProgress(string iceType, string fromPeer, string toPeer)
        {
            switch (iceType?.ToUpper())
            {
                case "ICE_OFFER":
                    Console.WriteLine($"🏁 [WEBRTC-PROGRESS] Step 1/4: Offer relayed {fromPeer} → {toPeer} (waiting for answer...)");
                    break;
                    
                case "ICE_ANSWER":
                    Console.WriteLine($"🏁 [WEBRTC-PROGRESS] Step 2/4: Answer relayed {fromPeer} → {toPeer} (starting ICE candidates...)");
                    break;
                    
                case "ICE_CAND":
                    Console.WriteLine($"🏁 [WEBRTC-PROGRESS] Step 3-4/4: ICE candidate relayed {fromPeer} → {toPeer} (P2P establishing...)");
                    break;
                    
                default:
                    Console.WriteLine($"🏁 [WEBRTC-PROGRESS] Unknown signal type '{iceType}' relayed {fromPeer} → {toPeer}");
                    break;
            }
        }

        private async Task HandleFileTransfer(ClientConnection client, string message)
        {
            Console.WriteLine($"File transfer from {client.Name}: {message.Substring(0, Math.Min(50, message.Length))}...");
            
            // Event pour l'extérieur
            FileSignal?.Invoke(message);
        }

        // ===== UTILITAIRES =====

        private async Task BroadcastPeerList()
        {
            var peerNames = _nameToId.Keys.Where(name => !string.IsNullOrEmpty(name)).ToList();
            var peersMessage = $"{ProtocolTags.TAG_PEERS}{string.Join(",", peerNames)}";
            
            await BroadcastToTrustedClients(peersMessage);
            
            // Event pour l'extérieur
            PeerListUpdated?.Invoke(peerNames);
        }

        private async Task BroadcastToTrustedClients(string message, string? excludeClientId = null)
        {
            var tasks = new List<Task>();
            
            foreach (var client in _clients.Values.Where(c => c.IsTrustedChannel && c.Id != excludeClientId))
            {
                tasks.Add(client.SendAsync(message));
            }
            
            await Task.WhenAll(tasks);
        }

        // Nouvelle méthode : Diffuser un message de chat aux clients connectés
        public async Task BroadcastChatMessage(string fromPeer, string content, string timestamp)
        {
            try
            {
                // Format du message de chat : CHAT:fromPeer:timestamp:content
                var chatMessage = $"CHAT:{fromPeer}:{timestamp}:{content}";
                
                Console.WriteLine($"📡 [RELAY] Broadcasting chat message from {fromPeer} to {_clients.Count} clients");
                
                // Diffuser sur le canal messages (port 8888) à tous les clients connectés
                var tasks = new List<Task>();
                
                foreach (var client in _clients.Values)
                {
                    try
                    {
                        tasks.Add(client.SendAsync(chatMessage));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ [RELAY] Error sending chat message to client {client.Id}: {ex.Message}");
                    }
                }
                
                if (tasks.Count > 0)
                {
                    await Task.WhenAll(tasks);
                    Console.WriteLine($"✅ [RELAY] Chat message broadcasted to {tasks.Count} clients");
                }
                else
                {
                    Console.WriteLine($"⚠️ [RELAY] No clients to broadcast chat message to");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [RELAY] Error broadcasting chat message: {ex.Message}");
            }
        }

        // ===== TUNNEL SÉCURISÉ - CLÉ PERSISTANTE =====
        private static byte[]? _serverTunnelPrivateKey;
        private static byte[]? _serverTunnelPublicKey;

        private static async Task InitializeServerTunnelKeys()
        {
            if (_serverTunnelPrivateKey == null || _serverTunnelPublicKey == null)
            {
                var keyPair = await CryptoService.GenerateKeyPair();
                _serverTunnelPrivateKey = keyPair.PrivateKey;
                _serverTunnelPublicKey = keyPair.PublicKey;
                Console.WriteLine($"🔑 [SECURE-TUNNEL] Server tunnel keys initialized");
            }
        }

        // NOUVEAU: Diffuser un message de synchronisation de statut aux clients connectés
        public async Task BroadcastStatusSync(string fromPeer, string statusType, bool enabled, string timestamp)
        {
            try
            {
                // Format du message de statut : STATUS_SYNC:fromPeer:statusType:enabled:timestamp
                var statusMessage = $"STATUS_SYNC:{fromPeer}:{statusType}:{enabled}:{timestamp}";
                
                Console.WriteLine($"📡 [STATUS-SYNC] Broadcasting status sync from {fromPeer} to {_clients.Count} clients");
                
                // Diffuser sur le canal messages (port 8888) à tous les clients connectés
                var tasks = new List<Task>();
                
                foreach (var client in _clients.Values)
                {
                    try
                    {
                        tasks.Add(client.SendAsync(statusMessage));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ [STATUS-SYNC] Error sending status sync to client {client.Id}: {ex.Message}");
                    }
                }
                
                if (tasks.Count > 0)
                {
                    await Task.WhenAll(tasks);
                    Console.WriteLine($"✅ [STATUS-SYNC] Status sync broadcasted to {tasks.Count} clients");
                }
                else
                {
                    Console.WriteLine($"⚠️ [STATUS-SYNC] No clients to broadcast status sync to");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [STATUS-SYNC] Error broadcasting status sync: {ex.Message}");
            }
        }

        // ✅ NOUVEAU: TRAITEMENT MESSAGES FICHIERS
        private async Task ProcessFileMessage(ClientConnection client, string message)
        {
            // ✅ OPTIMISÉ: Seuls messages fichiers autorisés sur ce canal
            if (message.StartsWith(ProtocolTags.TAG_NAME))
            {
                await HandleFileNameRegistration(client, message);
            }
            else if (message.StartsWith("PRIV:") && message.Contains("FILE_METADATA_RELAY"))
            {
                // Relay metadata directement aux autres clients fichiers
                await RelayFileMessage(client, message);
            }
            else if (message.StartsWith("PRIV:") && message.Contains("FILE_CHUNK_RELAY"))
            {
                // ✅ HAUTE PERFORMANCE: Relay chunks sans logs pour éviter spam
                await RelayFileChunk(client, message);
            }
            else
            {
                // Message non autorisé sur canal fichiers
                Console.WriteLine($"Unauthorized message on file channel from {client.Name}: {message.Substring(0, Math.Min(50, message.Length))}");
            }
        }

        // ✅ NOUVEAU: DÉCONNEXION CLIENT FICHIERS
        private void DisconnectFileClient(ClientConnection client)
        {
            try
            {
                if (!string.IsNullOrEmpty(client.Name))
                {
                    _fileNameToId.TryRemove(client.Name, out _);
                }

                _fileClients.TryRemove(client.Id, out _);
                client.Dispose();

                Console.WriteLine($"File client disconnected: {client.Name ?? client.Id}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disconnecting file client: {ex.Message}");
            }
        }

        private void DisconnectClient(ClientConnection client)
        {
            try
            {
                if (!string.IsNullOrEmpty(client.Name))
                {
                    if (client.IsTrustedChannel)
                    {
                        _nameToId.TryRemove(client.Name, out _);
                    }
                    else
                    {
                        _friendRequestNameToId.TryRemove(client.Name, out _);
                    }
                }
                
                if (client.IsTrustedChannel)
                {
                    _clients.TryRemove(client.Id, out _);
                }
                else
                {
                    _friendRequestClients.TryRemove(client.Id, out _);
                }
                client.Dispose();
                
                Console.WriteLine($"Client disconnected: {client.Name ?? client.Id}");
                
                // Update peer list if was on trusted channel
                if (client.IsTrustedChannel)
                {
                    _ = Task.Run(async () => await BroadcastPeerList());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disconnecting client: {ex.Message}");
            }
        }

        // ===== API PUBLIQUES =====
        
        public List<string> GetConnectedPeers()
        {
            return _nameToId.Keys.Where(name => !string.IsNullOrEmpty(name)).ToList();
        }

        public async Task<bool> SendToClient(string clientName, string message)
        {
            if (_nameToId.TryGetValue(clientName, out var clientId) &&
                _clients.TryGetValue(clientId, out var client))
            {
                // ✅ FIX: Debug log message routing to clients
                if (message.StartsWith("WEBRTC_INITIATE:"))
                {
                    Console.WriteLine($"📤 [RELAY-SEND] WEBRTC_INITIATE → {clientName} (via trusted channel 8888)");
                    Console.WriteLine($"📤 [RELAY-SEND] Message: {message.Substring(0, Math.Min(100, message.Length))}...");
                }

                await client.SendAsync(message);
                return true;
            }

            // ✅ FIX: Log when client is not found
            if (message.StartsWith("WEBRTC_INITIATE:"))
            {
                Console.WriteLine($"❌ [RELAY-SEND] Client {clientName} not found for WEBRTC_INITIATE");
                Console.WriteLine($"📋 [RELAY-SEND] Available clients: {string.Join(", ", _nameToId.Keys)}");
            }
            return false;
        }

        // NOUVEAU: Envoi spécifique sur le canal Friend Requests
        public async Task<bool> SendFriendRequestToClient(string clientName, string message)
        {
            Console.WriteLine($"[DEBUG] SendFriendRequestToClient: Looking for client '{clientName}'");
            Console.WriteLine($"[DEBUG] _friendRequestNameToId contains {_friendRequestNameToId.Count} entries:");
            foreach (var kvp in _friendRequestNameToId)
            {
                Console.WriteLine($"[DEBUG]   - Name: '{kvp.Key}' -> ID: {kvp.Value}");
            }
            
            if (_friendRequestNameToId.TryGetValue(clientName, out var clientId))
            {
                Console.WriteLine($"[DEBUG] Found clientId: {clientId} for {clientName}");
                
                if (_friendRequestClients.TryGetValue(clientId, out var client))
                {
                    Console.WriteLine($"[DEBUG] Found client connection for {clientName}, sending friend request message");
                    await client.SendAsync(message);
                    Console.WriteLine($"[DEBUG] Friend request sent successfully to {clientName}");
                    return true;
                }
                else
                {
                    Console.WriteLine($"[DEBUG] ClientId {clientId} not found in _friendRequestClients dictionary");
                }
            }
            else
            {
                Console.WriteLine($"[DEBUG] Client name '{clientName}' not found in _friendRequestNameToId dictionary");
            }
            
            Console.WriteLine($"[DEBUG] Failed to send friend request to {clientName}");
            return false;
        }

        public bool IsClientConnected(string clientName)
        {
            return _nameToId.ContainsKey(clientName);
        }

        // ✅ NOUVEAU: MÉTHODES CANAL FICHIERS

        private async Task HandleFileNameRegistration(ClientConnection client, string message)
        {
            try
            {
                var name = message.Substring(ProtocolTags.TAG_NAME.Length);
                client.Name = name;
                _fileNameToId[name] = client.Id;
                Console.WriteLine($"File client registered: {name}");
                await client.Writer.WriteLineAsync("NAME_OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in file name registration: {ex.Message}");
            }
        }

        private async Task RelayFileMessage(ClientConnection sender, string message)
        {
            try
            {
                // Format: PRIV:fromName:destName:message
                var parts = message.Substring("PRIV:".Length).Split(':', 3);
                if (parts.Length >= 3)
                {
                    var fromName = parts[0];
                    var destName = parts[1];
                    var content = parts[2];

                    if (_fileNameToId.TryGetValue(destName, out var destId) &&
                        _fileClients.TryGetValue(destId, out var destClient))
                    {
                        await destClient.Writer.WriteLineAsync(message);
                        // Pas de logs pour éviter spam
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error relaying file message: {ex.Message}");
            }
        }

        private async Task RelayFileChunk(ClientConnection sender, string message)
        {
            try
            {
                // ✅ HAUTE PERFORMANCE: Relay chunks sans logs pour éviter spam de GB
                var parts = message.Substring("PRIV:".Length).Split(':', 3);
                if (parts.Length >= 3)
                {
                    var fromName = parts[0];
                    var destName = parts[1];
                    var content = parts[2];

                    if (_fileNameToId.TryGetValue(destName, out var destId) &&
                        _fileClients.TryGetValue(destId, out var destClient))
                    {
                        await destClient.Writer.WriteLineAsync(message);
                        // ✅ AUCUN LOG: Évite 5GB de logs pour 1.68GB de fichier
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error relaying file chunk: {ex.Message}");
            }
        }
    }
}