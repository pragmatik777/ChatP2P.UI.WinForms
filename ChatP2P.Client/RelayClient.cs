using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatP2P.Client
{
    public class RelayClient
    {
        private readonly string _serverAddress;
        private readonly int _friendRequestPort;
        private readonly int _messagesPort;
        private readonly int _filesPort;

        private TcpClient? _friendRequestClient;
        private NetworkStream? _friendRequestStream;
        private StreamWriter? _friendRequestWriter;
        private StreamReader? _friendRequestReader;

        private TcpClient? _messagesClient;
        private NetworkStream? _messagesStream;
        private StreamWriter? _messagesWriter;
        private StreamReader? _messagesReader;

        private TcpClient? _filesClient;
        private NetworkStream? _filesStream;
        private StreamWriter? _filesWriter;
        private StreamReader? _filesReader;
        
        private bool _isConnected = false;
        private CancellationTokenSource? _cancellationToken;
        
        // Events pour les notifications
        public event Action<string, string, string, string>? FriendRequestReceived; // from, to, publicKey, message

        // ===== TUNNEL SÉCURISÉ PQC =====
        private SecureRelayTunnel? _secureTunnel;
        private bool _useSecureTunnel = true; // PQC tunnel enabled
        
        // Logging helper
        private async Task LogToFile(string message)
        {
            await LogHelper.LogToRelayAsync(message);
        }

        // ICE Event logging helper
        private async Task LogIceEvent(string iceType, string fromPeer, string toPeer, string status, string? iceData = null)
        {
            var logEntry = $"🧊 [ICE-{iceType.ToUpper()}] {fromPeer} → {toPeer} | {status}";
            if (!string.IsNullOrEmpty(iceData))
                logEntry += $" | Data: {iceData}";

            await LogHelper.LogToIceAsync(logEntry);
        }
        public event Action<string, string, string?>? FriendRequestAccepted; // from, to, pqcPublicKey
        public event Action<string, string, string, string>? DualKeyAcceptanceReceived; // fromPeer, toPeer, ed25519Key, pqcKey
        public event Action<string, string>? FriendRequestRejected; // from, to
        public event Action<string, string, string>? PrivateMessageReceived; // from, to, message
        public event Action<List<string>>? PeerListUpdated;
        public event Action<string, string, string>? ChatMessageReceived; // from, timestamp, content
        public event Action<string>? IceSignalReceived; // ICE signal message
        public event Action<string, string, bool, string>? StatusSyncReceived; // from, statusType, enabled, timestamp
        // NOUVEAU: WebRTC Signaling Events
        public event Action<string, string>? WebRTCInitiateReceived; // target_peer, initiator_peer
        public event Action<string, string, string, string>? WebRTCSignalReceived; // ice_type, from_peer, to_peer, ice_data
        public event Action<string, string, long, string>? FileMetadataRelayReceived; // transferId, fileName, fileSize, fromPeer
        public event Action<string, int, int, byte[]>? FileChunkRelayReceived; // transferId, chunkIndex, totalChunks, chunkData
        
        public RelayClient(string serverAddress, int friendRequestPort = 7777, int messagesPort = 8888, int filesPort = 8891)
        {
            _serverAddress = serverAddress;
            _friendRequestPort = friendRequestPort;
            _messagesPort = messagesPort;
            _filesPort = filesPort;
        }
        
        public async Task<bool> ConnectAsync(string displayName)
        {
            try
            {
                _cancellationToken = new CancellationTokenSource();
                
                // Connexion au canal friend requests (port 7777)
                _friendRequestClient = new TcpClient();
                await _friendRequestClient.ConnectAsync(_serverAddress, _friendRequestPort);
                _friendRequestStream = _friendRequestClient.GetStream();
                _friendRequestWriter = new StreamWriter(_friendRequestStream, Encoding.UTF8) { AutoFlush = true };
                _friendRequestReader = new StreamReader(_friendRequestStream, Encoding.UTF8);

                // Connexion au canal messages (port 8888)
                _messagesClient = new TcpClient();
                await _messagesClient.ConnectAsync(_serverAddress, _messagesPort);
                _messagesStream = _messagesClient.GetStream();
                _messagesWriter = new StreamWriter(_messagesStream, Encoding.UTF8) { AutoFlush = true };
                _messagesReader = new StreamReader(_messagesStream, Encoding.UTF8);

                // ✅ NOUVEAU: Connexion au canal fichiers (port 8891)
                _filesClient = new TcpClient();
                await _filesClient.ConnectAsync(_serverAddress, _filesPort);
                _filesStream = _filesClient.GetStream();
                _filesWriter = new StreamWriter(_filesStream, Encoding.UTF8) { AutoFlush = true };
                _filesReader = new StreamReader(_filesStream, Encoding.UTF8);

                // Enregistrer notre nom sur les trois canaux
                await _friendRequestWriter.WriteLineAsync($"NAME:{displayName}");
                await _messagesWriter.WriteLineAsync($"NAME:{displayName}");
                await _filesWriter.WriteLineAsync($"NAME:{displayName}");

                // NOUVEAU: Initialiser le tunnel sécurisé P2P pour ce client
                if (_useSecureTunnel)
                {
                    _secureTunnel = new SecureRelayTunnel(displayName);

                    // Connecter l'événement de friend request sécurisée à notre événement principal
                    _secureTunnel.SecureFriendRequestReceived += async (fromPeer, toPeer, ed25519Key, pqcKey, message) =>
                    {
                        // Stocker directement les deux clés
                        try
                        {
                            if (!string.IsNullOrEmpty(ed25519Key) && ed25519Key != "no_ed25519_key")
                            {
                                var ed25519Bytes = Convert.FromBase64String(ed25519Key);
                                await DatabaseService.Instance.AddPeerKey(fromPeer, "Ed25519", ed25519Bytes, "Secure tunnel Ed25519 key");
                                await LogToFile($"✅ Ed25519 key stored for {fromPeer} from secure tunnel");
                            }

                            if (!string.IsNullOrEmpty(pqcKey) && pqcKey != "no_pqc_key")
                            {
                                var pqcBytes = Convert.FromBase64String(pqcKey);
                                await DatabaseService.Instance.AddPeerKey(fromPeer, "PQ", pqcBytes, "Secure tunnel PQC key");
                                await LogToFile($"✅ PQC key stored for {fromPeer} from secure tunnel");
                            }
                        }
                        catch (Exception ex)
                        {
                            await LogToFile($"❌ Error storing keys for {fromPeer}: {ex.Message}");
                        }

                        // Pour compatibilité avec l'interface existante, on passe la clé PQC comme clé principale
                        FriendRequestReceived?.Invoke(fromPeer, toPeer, pqcKey, message);
                    };

                    // Configurer le callback pour envoyer des messages
                    _secureTunnel.SetSendMessageCallback(async (message) =>
                    {
                        using var writer = new StreamWriter(_friendRequestStream, Encoding.UTF8, leaveOpen: true);
                        await writer.WriteLineAsync(message);
                        await writer.FlushAsync();
                    });

                    await LogToFile($"🔐 [TUNNEL-INIT] P2P tunnel initialized for {displayName}");
                    LogHelper.LogToConsole($"🔐 [TUNNEL-INIT] P2P tunnel initialized for {displayName}");

                    // Générer nos clés P2P (pas de handshake serveur)
                    try
                    {
                        var established = await _secureTunnel.EstablishSecureChannelAsync(_friendRequestStream);
                        if (established)
                        {
                            await LogToFile($"✅ [TUNNEL-INIT] P2P tunnel ready for {displayName}");
                            LogHelper.LogToConsole($"✅ [TUNNEL-INIT] P2P tunnel ready for {displayName}");
                        }
                        else
                        {
                            await LogToFile($"❌ [TUNNEL-INIT] Failed to initialize P2P tunnel for {displayName}");
                            LogHelper.LogToConsole($"❌ [TUNNEL-INIT] Failed to initialize P2P tunnel for {displayName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        await LogToFile($"❌ [TUNNEL-INIT] Error initializing P2P tunnel: {ex.Message}");
                        LogHelper.LogToConsole($"❌ [TUNNEL-INIT] Error initializing P2P tunnel: {ex.Message}");
                    }
                }

                _isConnected = true;

                // Démarrer l'écoute sur les trois canaux
                _ = Task.Run(async () => await ListenFriendRequestChannel());
                _ = Task.Run(async () => await ListenMessagesChannel());
                _ = Task.Run(async () => await ListenFilesChannel());
                
                LogHelper.LogToConsole($"RelayClient connected as {displayName}");
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"Error connecting to RelayHub: {ex.Message}");
                await DisconnectAsync();
                return false;
            }
        }
        
        public async Task DisconnectAsync()
        {
            _isConnected = false;
            _cancellationToken?.Cancel();
            
            try
            {
                _friendRequestWriter?.Dispose();
                _friendRequestReader?.Dispose();
                _friendRequestStream?.Dispose();
                _friendRequestClient?.Dispose();
                
                _messagesWriter?.Dispose();
                _messagesReader?.Dispose();
                _messagesStream?.Dispose();
                _messagesClient?.Dispose();

                // ✅ NOUVEAU: Fermeture canal files
                _filesWriter?.Dispose();
                _filesReader?.Dispose();
                _filesStream?.Dispose();
                _filesClient?.Dispose();
            }
            catch { }
            
            LogHelper.LogToConsole("RelayClient disconnected");
        }
        
        // ===== FRIEND REQUESTS =====
        
        public async Task<bool> SendFriendRequestAsync(string fromPeer, string toPeer, string publicKey, string message = "")
        {
            if (!_isConnected || _friendRequestWriter == null) return false;

            try
            {
                var friendRequest = $"FRIEND_REQ:{fromPeer}:{toPeer}:{publicKey}:{message}";
                await _friendRequestWriter.WriteLineAsync(friendRequest);
                LogHelper.LogToConsole($"Friend request sent: {fromPeer} → {toPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"Error sending friend request: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Nouvelle méthode : Envoie friend request avec clés Ed25519 ET PQC via tunnel sécurisé
        /// </summary>
        public async Task<bool> SendFriendRequestWithBothKeysAsync(string fromPeer, string toPeer, string ed25519Key, string pqcKey, string message = "")
        {
            if (!_isConnected) return false;

            try
            {
                // Utiliser tunnel sécurisé si disponible
                if (_useSecureTunnel && _friendRequestStream != null)
                {
                    // Initialiser tunnel si pas encore fait
                    if (_secureTunnel == null)
                    {
                        await LogToFile("❌ [ERROR] Secure tunnel should be initialized in ConnectAsync, not here!");
                        return false;
                    }

                    // Établir canal sécurisé si nécessaire
                    if (!_secureTunnel.IsSecureChannelEstablished)
                    {
                        var established = await _secureTunnel.EstablishSecureChannelAsync(_friendRequestStream);
                        if (!established)
                        {
                            LogHelper.LogToConsole("❌ Failed to establish secure tunnel, falling back to legacy mode");
                            return await SendLegacyFriendRequest(fromPeer, toPeer, ed25519Key, pqcKey, message);
                        }
                    }

                    // Envoyer via tunnel sécurisé
                    var success = await _secureTunnel.SendSecureFriendRequestAsync(_friendRequestStream, toPeer, ed25519Key, pqcKey, message);
                    if (success)
                    {
                        LogHelper.LogToConsole($"🔐 Secure dual key friend request sent: {fromPeer} → {toPeer}");
                        return true;
                    }
                }

                // Fallback vers ancien protocole si tunnel échoue
                LogHelper.LogToConsole("⚠️ Secure tunnel failed, using legacy friend request");
                return await SendLegacyFriendRequest(fromPeer, toPeer, ed25519Key, pqcKey, message);
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"Error sending secure friend request: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Ancien protocole friend request pour compatibilité
        /// </summary>
        private async Task<bool> SendLegacyFriendRequest(string fromPeer, string toPeer, string ed25519Key, string pqcKey, string message)
        {
            if (_friendRequestWriter == null) return false;

            try
            {
                // Format legacy: FRIEND_REQ_DUAL:fromPeer:toPeer:ed25519Key:pqcKey:message
                var friendRequest = $"FRIEND_REQ_DUAL:{fromPeer}:{toPeer}:{ed25519Key}:{pqcKey}:{message}";
                await _friendRequestWriter.WriteLineAsync(friendRequest);
                LogHelper.LogToConsole($"⚠️ Legacy dual key friend request sent: {fromPeer} → {toPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"Error sending legacy friend request: {ex.Message}");
                return false;
            }
        }
        
        public async Task<bool> AcceptFriendRequestAsync(string fromPeer, string toPeer, string? myPqcPublicKey = null)
        {
            if (!_isConnected || _friendRequestWriter == null) return false;

            try
            {
                // ✅ PQC: Include our PQC public key in the acceptance so the requester can encrypt messages to us
                var response = string.IsNullOrEmpty(myPqcPublicKey)
                    ? $"FRIEND_ACCEPT:{fromPeer}:{toPeer}"
                    : $"FRIEND_ACCEPT:{fromPeer}:{toPeer}:{myPqcPublicKey}";
                await _friendRequestWriter.WriteLineAsync(response);
                LogHelper.LogToConsole($"Friend request accepted: {fromPeer} ← {toPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"Error accepting friend request: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Nouvelle méthode : Accepte friend request en renvoyant clés Ed25519 ET PQC
        /// </summary>
        public async Task<bool> AcceptFriendRequestWithBothKeysAsync(string fromPeer, string toPeer, string myEd25519Key, string myPqcKey)
        {
            if (!_isConnected || _friendRequestWriter == null) return false;

            try
            {
                // Format: FRIEND_ACCEPT_DUAL:fromPeer:toPeer:ed25519Key:pqcKey
                var response = $"FRIEND_ACCEPT_DUAL:{fromPeer}:{toPeer}:{myEd25519Key}:{myPqcKey}";
                await _friendRequestWriter.WriteLineAsync(response);
                LogHelper.LogToConsole($"Dual key friend request accepted: {fromPeer} ← {toPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"Error accepting dual key friend request: {ex.Message}");
                return false;
            }
        }
        
        public async Task<bool> RejectFriendRequestAsync(string fromPeer, string toPeer)
        {
            if (!_isConnected || _friendRequestWriter == null) return false;
            
            try
            {
                var response = $"FRIEND_REJECT:{fromPeer}:{toPeer}";
                await _friendRequestWriter.WriteLineAsync(response);
                LogHelper.LogToConsole($"Friend request rejected: {fromPeer} ← {toPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"Error rejecting friend request: {ex.Message}");
                return false;
            }
        }
        
        // ===== MESSAGES =====
        
        public async Task<bool> SendPrivateMessageAsync(string fromPeer, string toPeer, string message)
        {
            if (!_isConnected || _messagesWriter == null) return false;
            
            try
            {
                var privateMessage = $"PRIV:{fromPeer}:{toPeer}:{message}";
                await _messagesWriter.WriteLineAsync(privateMessage);
                LogHelper.LogToConsole($"Private message sent: {fromPeer} → {toPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"Error sending private message: {ex.Message}");
                return false;
            }
        }

        // ===== FICHIERS =====

        // ✅ NOUVEAU: Envoi métadonnées fichier via canal 8891 avec format PRIV
        public async Task<bool> SendFileMetadataAsync(string transferId, string fileName, long fileSize, string fromPeer, string toPeer)
        {
            if (!_isConnected || _filesWriter == null) return false;

            try
            {
                var metadataContent = $"FILE_METADATA_RELAY:{transferId}:{fileName}:{fileSize}";
                var metadataMessage = $"PRIV:{fromPeer}:{toPeer}:{metadataContent}";
                await _filesWriter.WriteLineAsync(metadataMessage);
                LogHelper.LogToConsole($"📁 [FILES-CHANNEL-8891] Metadata sent: {fileName} ({fileSize} bytes) {fromPeer} → {toPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"Error sending file metadata: {ex.Message}");
                return false;
            }
        }

        // ✅ NOUVEAU: Envoi chunk fichier via canal 8891 avec format PRIV (optimisé sans logs)
        public async Task<bool> SendFileChunkAsync(string transferId, int chunkIndex, int totalChunks, byte[] chunkData, string fromPeer, string toPeer, bool useEncryption = false)
        {
            if (!_isConnected || _filesWriter == null) return false;

            try
            {
                byte[] finalChunkData = chunkData;

                // 🔐 NOUVEAU: Chiffrement PQC des chunks de fichiers (relay seulement, P2P reste clair)
                if (useEncryption)
                {
                    var peerKeys = await DatabaseService.Instance.GetPeerKeys(toPeer, "PQ");
                    var activePqKey = peerKeys.Where(k => !k.Revoked && k.Public != null)
                                              .OrderByDescending(k => k.CreatedUtc)
                                              .FirstOrDefault();

                    if (activePqKey?.Public != null)
                    {
                        finalChunkData = await CryptoService.EncryptMessage(chunkData, activePqKey.Public);
                        await CryptoService.LogCrypto($"🔒 [FILE-ENCRYPT] Chunk {chunkIndex}/{totalChunks} encrypted for {toPeer} ({chunkData.Length} → {finalChunkData.Length} bytes)");
                    }
                    else
                    {
                        await CryptoService.LogCrypto($"⚠️ [FILE-ENCRYPT] No PQC key for {toPeer}, sending chunk {chunkIndex} unencrypted");
                        LogHelper.LogToConsole($"⚠️ [FILE-ENCRYPT] No PQC key for {toPeer}, sending chunk unencrypted");
                    }
                }

                var base64Chunk = Convert.ToBase64String(finalChunkData);
                var encryptionFlag = useEncryption ? "ENC" : "CLR";
                var chunkContent = $"FILE_CHUNK_RELAY:{transferId}:{chunkIndex}:{totalChunks}:{encryptionFlag}:{base64Chunk}";
                var chunkMessage = $"PRIV:{fromPeer}:{toPeer}:{chunkContent}";
                await _filesWriter.WriteLineAsync(chunkMessage);

                // ✅ OPTIMISÉ: Log seulement tous les 100 chunks pour éviter spam logs
                if (chunkIndex % 100 == 0)
                {
                    var encStatus = useEncryption ? "🔒 encrypted" : "📦 clear";
                    LogHelper.LogToConsole($"{encStatus} [FILES-CHANNEL-8891] Chunk {chunkIndex}/{totalChunks} sent ({finalChunkData.Length} bytes)");
                }
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"Error sending file chunk: {ex.Message}");
                await CryptoService.LogCrypto($"❌ [FILE-ENCRYPT] Error sending chunk {chunkIndex}: {ex.Message}");
                return false;
            }
        }

        // ===== LISTENING =====
        
        private async Task ListenFriendRequestChannel()
        {
            await LogToFile("📡 [RelayClient] ListenFriendRequestChannel started");
            LogHelper.LogToConsole($"📡 [RelayClient] ListenFriendRequestChannel started");
            if (_friendRequestReader == null) 
            {
                await LogToFile("❌ [RelayClient] _friendRequestReader is null!");
                LogHelper.LogToConsole($"❌ [RelayClient] _friendRequestReader is null!");
                return;
            }
            
            try
            {
                while (_isConnected && !_cancellationToken!.Token.IsCancellationRequested)
                {
                    await LogToFile("[DEBUG] Waiting for message on friend request channel...");
                    LogHelper.LogToConsole($"[DEBUG] Waiting for message on friend request channel...");
                    var message = await _friendRequestReader.ReadLineAsync();
                    
                    if (message == null) 
                    {
                        await LogToFile("⚠️  [RelayClient] Received null message, connection closed");
                        LogHelper.LogToConsole($"⚠️  [RelayClient] Received null message, connection closed");
                        break;
                    }
                    
                    await LogToFile($"📥 [RelayClient] Raw message received: {message}");
                    LogHelper.LogToConsole($"📥 [RelayClient] Raw message received: {message}");
                    await ProcessFriendRequestMessage(message);
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"❌ [RelayClient] Error listening friend request channel: {ex.Message}");
            }
            LogHelper.LogToConsole($"📡 [RelayClient] ListenFriendRequestChannel ended");
        }
        
        private async Task ListenMessagesChannel()
        {
            if (_messagesReader == null) return;

            try
            {
                while (_isConnected && !_cancellationToken!.Token.IsCancellationRequested)
                {
                    var message = await _messagesReader.ReadLineAsync();
                    if (message == null) break;

                    // ✅ FIX: Debug log every message received on trusted channel (8888)
                    LogHelper.LogToConsole($"📨 [MSG-CHANNEL-8888] Received: {message.Substring(0, Math.Min(100, message.Length))}...");
                    await LogToFile($"[MSG-CHANNEL-8888] Received: {message}");

                    await ProcessMessageChannelMessage(message);
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"Error listening message channel: {ex.Message}");
            }
        }

        // ✅ NOUVEAU: Canal files (port 8891) pour transferts fichiers optimisés
        private async Task ListenFilesChannel()
        {
            if (_filesReader == null) return;

            try
            {
                while (_isConnected && !_cancellationToken!.Token.IsCancellationRequested)
                {
                    var message = await _filesReader.ReadLineAsync();
                    if (message == null) break;

                    // ✅ OPTIMISÉ: Pas de logging détaillé pour les chunks pour éviter les 5GB logs
                    await ProcessFilesChannelMessage(message);
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"Error listening files channel: {ex.Message}");
            }
        }

        private async Task ProcessFilesChannelMessage(string message)
        {
            try
            {
                // ✅ FIX: Le serveur relaie au format PRIV:fromPeer:toPeer:content
                if (message.StartsWith("PRIV:"))
                {
                    // Format: PRIV:fromPeer:toPeer:FILE_METADATA_RELAY:... ou PRIV:fromPeer:toPeer:FILE_CHUNK_RELAY:...
                    var parts = message.Substring("PRIV:".Length).Split(':', 3);
                    if (parts.Length >= 3)
                    {
                        var fromPeer = parts[0];
                        var toPeer = parts[1];
                        var content = parts[2];

                        if (content.StartsWith("FILE_METADATA_RELAY:"))
                        {
                            // Format: FILE_METADATA_RELAY:transferId:fileName:fileSize
                            var metaParts = content.Substring("FILE_METADATA_RELAY:".Length).Split(':', 3);
                            if (metaParts.Length >= 3)
                            {
                                var transferId = metaParts[0];
                                var fileName = metaParts[1];
                                var fileSize = long.Parse(metaParts[2]);

                                LogHelper.LogToConsole($"📁 [FILES-CHANNEL-8891] Metadata: {fileName} ({fileSize} bytes) from {fromPeer}");
                                FileMetadataRelayReceived?.Invoke(transferId, fileName, fileSize, fromPeer);
                            }
                        }
                        else if (content.StartsWith("FILE_CHUNK_RELAY:"))
                        {
                            // Format: FILE_CHUNK_RELAY:transferId:chunkIndex:totalChunks:ENC/CLR:base64ChunkData
                            var chunkParts = content.Substring("FILE_CHUNK_RELAY:".Length).Split(':', 5);
                            if (chunkParts.Length >= 5)
                            {
                                var transferId = chunkParts[0];
                                var chunkIndex = int.Parse(chunkParts[1]);
                                var totalChunks = int.Parse(chunkParts[2]);
                                var encryptionFlag = chunkParts[3]; // "ENC" ou "CLR"
                                var base64ChunkData = chunkParts[4];

                                var chunkData = Convert.FromBase64String(base64ChunkData);

                                // 🔓 NOUVEAU: Déchiffrement PQC des chunks de fichiers si nécessaire
                                if (encryptionFlag == "ENC")
                                {
                                    try
                                    {
                                        // Récupérer notre clé privée PQC
                                        var identity = await DatabaseService.Instance.GetIdentity();
                                        if (identity?.PqPriv != null)
                                        {
                                            chunkData = await CryptoService.DecryptMessageBytes(chunkData, identity.PqPriv);
                                            await CryptoService.LogCrypto($"🔓 [FILE-DECRYPT] Chunk {chunkIndex}/{totalChunks} decrypted from {fromPeer} ({base64ChunkData.Length} → {chunkData.Length} bytes)");
                                        }
                                        else
                                        {
                                            await CryptoService.LogCrypto($"❌ [FILE-DECRYPT] No PQC private key to decrypt chunk {chunkIndex} from {fromPeer}");
                                            LogHelper.LogToConsole($"❌ [FILE-DECRYPT] No PQC private key to decrypt chunk {chunkIndex} from {fromPeer}");
                                            return; // Skip ce chunk si pas de clé
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        await CryptoService.LogCrypto($"❌ [FILE-DECRYPT] Failed to decrypt chunk {chunkIndex}: {ex.Message}");
                                        LogHelper.LogToConsole($"❌ [FILE-DECRYPT] Failed to decrypt chunk {chunkIndex}: {ex.Message}");
                                        return; // Skip ce chunk si décryption échoue
                                    }
                                }

                                // ✅ OPTIMISÉ: Log seulement tous les 100 chunks pour éviter spam
                                if (chunkIndex % 100 == 0)
                                {
                                    var encStatus = encryptionFlag == "ENC" ? "🔓 decrypted" : "📦 clear";
                                    LogHelper.LogToConsole($"{encStatus} [FILES-CHANNEL-8891] Chunk {chunkIndex}/{totalChunks} ({chunkData.Length} bytes)");
                                }

                                FileChunkRelayReceived?.Invoke(transferId, chunkIndex, totalChunks, chunkData);
                            }
                            else if (chunkParts.Length >= 4)
                            {
                                // ✅ RÉTROCOMPATIBILITÉ: Format ancien sans flag encryption
                                var transferId = chunkParts[0];
                                var chunkIndex = int.Parse(chunkParts[1]);
                                var totalChunks = int.Parse(chunkParts[2]);
                                var chunkData = Convert.FromBase64String(chunkParts[3]);

                                if (chunkIndex % 100 == 0)
                                {
                                    LogHelper.LogToConsole($"📦 [FILES-CHANNEL-8891-LEGACY] Chunk {chunkIndex}/{totalChunks} ({chunkData.Length} bytes)");
                                }

                                FileChunkRelayReceived?.Invoke(transferId, chunkIndex, totalChunks, chunkData);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"Error processing files channel message: {ex.Message}");
            }
        }

        private async Task ProcessFriendRequestMessage(string message)
        {
            await LogToFile($"🔄 [RelayClient] ProcessFriendRequestMessage: {message}");
            LogHelper.LogToConsole($"🔄 [RelayClient] ProcessFriendRequestMessage: {message}");
            try
            {
                if (message.StartsWith("FRIEND_REQ:"))
                {
                    await LogToFile("🎯 [RelayClient] Friend request message détectée!");
                    LogHelper.LogToConsole($"🎯 [RelayClient] Friend request message détectée!");
                    // Format: FRIEND_REQ:fromPeer:toPeer:publicKey:message
                    var parts = message.Substring("FRIEND_REQ:".Length).Split(':', 4);
                    await LogToFile($"[DEBUG] Parts count: {parts.Length}");
                    LogHelper.LogToConsole($"[DEBUG] Parts count: {parts.Length}");
                    
                    if (parts.Length >= 4)
                    {
                        var fromPeer = parts[0];
                        var toPeer = parts[1];
                        var publicKey = parts[2];
                        var requestMessage = parts[3];
                        
                        await LogToFile($"[DEBUG] From: {fromPeer}, To: {toPeer}, Key: {publicKey}, Msg: {requestMessage}");
                        await LogToFile($"✅ Friend request received: {fromPeer} → {toPeer}");
                        LogHelper.LogToConsole($"[DEBUG] From: {fromPeer}, To: {toPeer}, Key: {publicKey}, Msg: {requestMessage}");
                        LogHelper.LogToConsole($"✅ Friend request received: {fromPeer} → {toPeer}");
                        FriendRequestReceived?.Invoke(fromPeer, toPeer, publicKey, requestMessage);
                        await LogToFile("✅ FriendRequestReceived event invoked");
                        LogHelper.LogToConsole($"✅ FriendRequestReceived event invoked");
                    }
                    else
                    {
                        await LogToFile($"❌ [RelayClient] Invalid friend request format: {parts.Length} parts");
                        LogHelper.LogToConsole($"❌ [RelayClient] Invalid friend request format: {parts.Length} parts");
                    }
                }
                else if (message.StartsWith("FRIEND_ACCEPT:"))
                {
                    // Format: FRIEND_ACCEPT:fromPeer:toPeer OR FRIEND_ACCEPT:fromPeer:toPeer:pqcPublicKey
                    var parts = message.Substring("FRIEND_ACCEPT:".Length).Split(':', 3);
                    if (parts.Length >= 2)
                    {
                        var fromPeer = parts[0];
                        var toPeer = parts[1];
                        var pqcPublicKey = parts.Length >= 3 ? parts[2] : null; // ✅ PQC key optional

                        LogHelper.LogToConsole($"Friend request accepted: {fromPeer} ← {toPeer} (PQC: {!string.IsNullOrEmpty(pqcPublicKey)})");
                        FriendRequestAccepted?.Invoke(fromPeer, toPeer, pqcPublicKey);
                    }
                }
                else if (message.StartsWith("FRIEND_ACCEPT_DUAL:"))
                {
                    // Format: FRIEND_ACCEPT_DUAL:fromPeer:toPeer:ed25519Key:pqcKey
                    var parts = message.Substring("FRIEND_ACCEPT_DUAL:".Length).Split(':', 4);
                    if (parts.Length >= 4)
                    {
                        var fromPeer = parts[0];
                        var toPeer = parts[1];
                        var ed25519Key = parts[2];
                        var pqcKey = parts[3];

                        LogHelper.LogToConsole($"Dual key friend request accepted: {fromPeer} ← {toPeer}");

                        // Store both keys automatically
                        try
                        {
                            if (!string.IsNullOrEmpty(ed25519Key) && ed25519Key != "no_ed25519_key")
                            {
                                var ed25519Bytes = Convert.FromBase64String(ed25519Key);
                                await DatabaseService.Instance.AddPeerKey(fromPeer, "Ed25519", ed25519Bytes, "Dual key acceptance Ed25519");
                                await LogToFile($"✅ Ed25519 key stored for {fromPeer} from dual acceptance");
                            }

                            if (!string.IsNullOrEmpty(pqcKey) && pqcKey != "no_pqc_key")
                            {
                                var pqcBytes = Convert.FromBase64String(pqcKey);
                                await DatabaseService.Instance.AddPeerKey(fromPeer, "PQ", pqcBytes, "Dual key acceptance PQC");
                                await LogToFile($"✅ PQC key stored for {fromPeer} from dual acceptance");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogHelper.LogToConsole($"❌ Error storing dual keys for {fromPeer}: {ex.Message}");
                        }

                        // ✅ FIX: Use a new event specifically for dual key acceptance responses
                        // This avoids infinite loops while still handling the acceptance properly
                        DualKeyAcceptanceReceived?.Invoke(fromPeer, toPeer, ed25519Key, pqcKey);
                        LogHelper.LogToConsole($"✅ [DUAL-ACCEPT] Keys stored for {fromPeer}, dual acceptance event triggered");
                    }
                }
                else if (message.StartsWith("FRIEND_REJECT:"))
                {
                    // Format: FRIEND_REJECT:fromPeer:toPeer
                    var parts = message.Substring("FRIEND_REJECT:".Length).Split(':');
                    if (parts.Length >= 2)
                    {
                        var fromPeer = parts[0];
                        var toPeer = parts[1];

                        LogHelper.LogToConsole($"Friend request rejected: {fromPeer} ← {toPeer}");
                        FriendRequestRejected?.Invoke(fromPeer, toPeer);
                    }
                }
                else if (message.StartsWith("{") && (message.Contains("\"type\":\"SECURE_TUNNEL_MESSAGE\"") || message.Contains("\"type\":\"TUNNEL_KEY_EXCHANGE\"")))
                {
                    // Nouveau: Messages JSON du tunnel sécurisé en provenance du serveur
                    await HandleRelayedTunnelMessage(message);
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"Error processing friend request message: {ex.Message}");
            }
        }

        /// <summary>
        /// Gestion des messages de tunnel sécurisé relayés par le serveur
        /// </summary>
        private async Task HandleRelayedTunnelMessage(string jsonMessage)
        {
            try
            {
                await LogToFile($"🔐 [TUNNEL-RELAY] Received relayed tunnel message from server");
                LogHelper.LogToConsole($"🔐 [TUNNEL-RELAY] Received relayed tunnel message from server");

                // Transférer le message au SecureRelayTunnel pour déchiffrement
                if (_secureTunnel != null)
                {
                    await _secureTunnel.HandleRelayedTunnelMessage(jsonMessage);
                }
                else
                {
                    LogHelper.LogToConsole($"❌ [TUNNEL-RELAY] No secure tunnel available to handle relayed message");
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"❌ [TUNNEL-RELAY] Error handling relayed tunnel message: {ex.Message}");
            }
        }
        
        private async Task ProcessMessageChannelMessage(string message)
        {
            try
            {
                // ✅ DEBUG: Log tous les messages reçus pour diagnostiquer WEBRTC_INITIATE
                // ✅ FIX CRITIQUE: Plus de log debug pour éviter les 5GB de logs avec gros fichiers
                // LogHelper.LogToConsole($"📥 [RELAY-DEBUG] Received message: {message.Substring(0, Math.Min(100, message.Length))}...");
                // await LogToFile($"[RELAY-DEBUG] Received: {message}"); // SUPPRIMÉ - causait 5GB logs
                if (message.StartsWith("PRIV:"))
                {
                    // Format: PRIV:fromName:destName:message
                    var parts = message.Substring("PRIV:".Length).Split(':', 3);
                    if (parts.Length >= 3)
                    {
                        var fromPeer = parts[0];
                        var toPeer = parts[1];
                        var messageBody = parts[2];
                        
                        // Vérifier si c'est un message de transfert de fichier
                        if (messageBody.StartsWith("FILE_META_RELAY:"))
                        {
                            var jsonData = messageBody.Substring("FILE_META_RELAY:".Length);
                            try
                            {
                                var metadata = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(jsonData);
                                var fileName = metadata.GetProperty("fileName").GetString() ?? "";
                                var fileSize = metadata.GetProperty("fileSize").GetInt64();
                                var transferId = metadata.GetProperty("transferId").GetString() ?? "";
                                
                                LogHelper.LogToConsole($"📁 [RELAY-FILE] Metadata received: {fileName} ({fileSize} bytes) from {fromPeer}");
                                FileMetadataRelayReceived?.Invoke(transferId, fileName, fileSize, fromPeer);
                            }
                            catch (Exception ex)
                            {
                                LogHelper.LogToConsole($"❌ [RELAY-FILE] Error parsing metadata: {ex.Message}");
                            }
                        }
                        else if (messageBody.StartsWith("FILE_CHUNK_RELAY:"))
                        {
                            var jsonData = messageBody.Substring("FILE_CHUNK_RELAY:".Length);
                            try
                            {
                                var chunkData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(jsonData);
                                var transferId = chunkData.GetProperty("transferId").GetString() ?? "";
                                var chunkIndex = chunkData.GetProperty("chunkIndex").GetInt32();
                                var totalChunks = chunkData.GetProperty("totalChunks").GetInt32();
                                var chunkDataBase64 = chunkData.GetProperty("chunkData").GetString() ?? "";
                                
                                var chunkBytes = Convert.FromBase64String(chunkDataBase64);

                                // ✅ NO LOGS: Complètement supprimé pour éviter spam fichiers logs
                                // Progress visible dans l'UI seulement, pas de logs chunks

                                FileChunkRelayReceived?.Invoke(transferId, chunkIndex, totalChunks, chunkBytes);
                            }
                            catch (Exception ex)
                            {
                                LogHelper.LogToConsole($"❌ [RELAY-FILE] Error parsing chunk: {ex.Message}");
                            }
                        }
                        else
                        {
                            // Message privé normal
                            LogHelper.LogToConsole($"Private message received: {fromPeer} → {toPeer}");
                            PrivateMessageReceived?.Invoke(fromPeer, toPeer, messageBody);
                        }
                    }
                }
                else if (message.StartsWith("CHAT:"))
                {
                    // Format: CHAT:fromPeer:timestamp:content
                    // Note: timestamp peut contenir des ':' donc on split en 2 étapes
                    var messageContent = message.Substring("CHAT:".Length);
                    var firstColonIndex = messageContent.IndexOf(':');
                    if (firstColonIndex > 0)
                    {
                        var fromPeer = messageContent.Substring(0, firstColonIndex);
                        var remainingContent = messageContent.Substring(firstColonIndex + 1);
                        
                        // Trouver le dernier ':' pour séparer timestamp et content
                        var lastColonIndex = remainingContent.LastIndexOf(':');
                        if (lastColonIndex > 0)
                        {
                            var timestamp = remainingContent.Substring(0, lastColonIndex);
                            var content = remainingContent.Substring(lastColonIndex + 1);
                            
                            LogHelper.LogToConsole($"💬 Chat message received from {fromPeer}: {content}");
                            ChatMessageReceived?.Invoke(fromPeer, timestamp, content);
                        }
                    }
                }
                else if (message.StartsWith("ICE_OFFER:") || 
                         message.StartsWith("ICE_ANSWER:") ||
                         message.StartsWith("ICE_CAND:"))
                {
                    LogHelper.LogToConsole($"🧊 [ICE-LEGACY] Legacy ICE signal received: {message.Substring(0, Math.Min(50, message.Length))}...");
                    IceSignalReceived?.Invoke(message);
                }
                else if (message.StartsWith("WEBRTC_INITIATE:"))
                {
                    // ✅ FIX: Explicitly log WebRTC initiation detection
                    LogHelper.LogToConsole($"🎯 [MSG-PROCESS] WEBRTC_INITIATE detected, calling ProcessWebRTCInitiate");
                    await LogToFile($"[MSG-PROCESS] WEBRTC_INITIATE detected, calling ProcessWebRTCInitiate");
                    await ProcessWebRTCInitiate(message);
                }
                else if (message.StartsWith("WEBRTC_SIGNAL:"))
                {
                    await ProcessWebRTCSignal(message);
                }
                else if (message.StartsWith("STATUS_SYNC:"))
                {
                    // Format: STATUS_SYNC:fromPeer:statusType:enabled:timestamp
                    var parts = message.Substring("STATUS_SYNC:".Length).Split(':', 4);
                    if (parts.Length >= 4)
                    {
                        var fromPeer = parts[0];
                        var statusType = parts[1];
                        var enabled = bool.Parse(parts[2]);
                        var timestamp = parts[3];
                        
                        LogHelper.LogToConsole($"📡 [STATUS-SYNC] Received from {fromPeer}: {statusType} = {enabled}");
                        StatusSyncReceived?.Invoke(fromPeer, statusType, enabled, timestamp);
                    }
                }
                else if (message.StartsWith("PEERS:"))
                {
                    // Format: PEERS:peer1,peer2,peer3
                    var peerList = message.Substring("PEERS:".Length);
                    var peers = string.IsNullOrEmpty(peerList) ? new List<string>() : peerList.Split(',').ToList();
                    
                    LogHelper.LogToConsole($"Peer list updated: {peers.Count} peers");
                    PeerListUpdated?.Invoke(peers);
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"❌ [MESSAGE-CHANNEL] Error processing message: {ex.Message}");
            }
        }
        
        /// <summary>
        /// NOUVEAU: Traiter les messages d'initiation WebRTC du serveur
        /// </summary>
        private async Task ProcessWebRTCInitiate(string message)
        {
            try
            {
                // Format: WEBRTC_INITIATE:{json}
                var jsonData = message.Substring("WEBRTC_INITIATE:".Length);
                
                LogHelper.LogToConsole($"🚀 [WEBRTC-INITIATE] Processing initiation message: {jsonData}");
                await LogToFile($"[WEBRTC-INITIATE] Received: {jsonData}");
                
                var initData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(jsonData);
                
                if (initData.TryGetProperty("target_peer", out var targetPeerEl) &&
                    initData.TryGetProperty("initiator_peer", out var initiatorPeerEl) &&
                    initData.TryGetProperty("action", out var actionEl))
                {
                    var targetPeer = targetPeerEl.GetString() ?? "";
                    var initiatorPeer = initiatorPeerEl.GetString() ?? "";
                    var action = actionEl.GetString() ?? "";
                    
                    if (action == "initiate_offer")
                    {
                        LogHelper.LogToConsole($"🎯 [WEBRTC-INITIATE] Server requests ICE offer creation: {initiatorPeer} → {targetPeer}");
                        await LogToFile($"[WEBRTC-INITIATE] Creating offer: {initiatorPeer} → {targetPeer}");
                        await LogIceEvent("INITIATE", initiatorPeer, targetPeer, "RelayClient received WebRTC offer initiation from server", jsonData.Substring(0, Math.Min(100, jsonData.Length)));
                        
                        // Déclencher l'événement pour que MainWindow traite la création d'offer
                        WebRTCInitiateReceived?.Invoke(targetPeer, initiatorPeer);
                    }
                    else if (action == "incoming_connection")
                    {
                        LogHelper.LogToConsole($"🔔 [WEBRTC-INITIATE] Incoming connection notification: {initiatorPeer} → {targetPeer}");
                        await LogToFile($"[WEBRTC-INITIATE] Incoming connection from: {initiatorPeer}");
                        await LogIceEvent("INCOMING", initiatorPeer, targetPeer, "RelayClient received WebRTC incoming connection notification", jsonData.Substring(0, Math.Min(100, jsonData.Length)));
                        
                        // For incoming connections, we don't create an offer, just log the notification
                        // The initiator will create the offer and it will be relayed to us via WebRTCSignalReceived
                    }
                    else
                    {
                        LogHelper.LogToConsole($"❓ [WEBRTC-INITIATE] Unknown action: {action}");
                        await LogToFile($"[WEBRTC-INITIATE] Unknown action: {action}");
                    }
                }
                else
                {
                    LogHelper.LogToConsole($"❌ [WEBRTC-INITIATE] Invalid initiation message format");
                    await LogToFile($"[WEBRTC-INITIATE] ERROR: Invalid format - {jsonData}");
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"❌ [WEBRTC-INITIATE] Error processing initiation: {ex.Message}");
                await LogToFile($"[WEBRTC-INITIATE] ERROR: {ex.Message}");
            }
        }
        
        /// <summary>
        /// NOUVEAU: Traiter les messages de signaling WebRTC (offers, answers, candidates)
        /// </summary>
        private async Task ProcessWebRTCSignal(string message)
        {
            try
            {
                // Format: WEBRTC_SIGNAL:{json}
                var jsonData = message.Substring("WEBRTC_SIGNAL:".Length);
                
                LogHelper.LogToConsole($"📡 [WEBRTC-SIGNAL] Processing signal: {jsonData.Substring(0, Math.Min(80, jsonData.Length))}...");
                await LogToFile($"[WEBRTC-SIGNAL] Received: {jsonData}");
                
                var signalData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(jsonData);
                
                if (signalData.TryGetProperty("ice_type", out var iceTypeEl) &&
                    signalData.TryGetProperty("from_peer", out var fromPeerEl) &&
                    signalData.TryGetProperty("to_peer", out var toPeerEl) &&
                    signalData.TryGetProperty("ice_data", out var iceDataEl))
                {
                    var iceType = iceTypeEl.GetString() ?? "";
                    var fromPeer = fromPeerEl.GetString() ?? "";
                    var toPeer = toPeerEl.GetString() ?? "";
                    var iceData = iceDataEl.GetString() ?? "";
                    
                    LogHelper.LogToConsole($"🔄 [WEBRTC-SIGNAL] Routing {iceType}: {fromPeer} → {toPeer}");
                    await LogToFile($"[WEBRTC-SIGNAL] {iceType} from {fromPeer} to {toPeer}");
                    await LogIceEvent("SIGNAL", fromPeer, toPeer, $"RelayClient received {iceType} from server", iceData.Substring(0, Math.Min(100, iceData.Length)));
                    
                    // Déclencher l'événement pour que MainWindow traite le signal ICE
                    WebRTCSignalReceived?.Invoke(iceType, fromPeer, toPeer, iceData);
                }
                else
                {
                    LogHelper.LogToConsole($"❌ [WEBRTC-SIGNAL] Invalid signal message format");
                    await LogToFile($"[WEBRTC-SIGNAL] ERROR: Invalid format - {jsonData}");
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"❌ [WEBRTC-SIGNAL] Error processing signal: {ex.Message}");
                await LogToFile($"[WEBRTC-SIGNAL] ERROR: {ex.Message}");
            }
        }
        
        public bool IsConnected => _isConnected;

        /// <summary>
        /// Active/désactive le tunnel sécurisé PQC
        /// </summary>
        public void SetSecureTunnelEnabled(bool enabled)
        {
            _useSecureTunnel = enabled;
            if (!enabled && _secureTunnel != null)
            {
                _secureTunnel.ResetTunnel();
                LogHelper.LogToConsole("🔓 Secure tunnel disabled, using legacy protocol");
            }
            else if (enabled)
            {
                LogHelper.LogToConsole("🔐 Secure tunnel enabled for friend requests");
            }
        }

        /// <summary>
        /// Vérifie si le tunnel sécurisé est établi
        /// </summary>
        public bool IsSecureTunnelEstablished => _secureTunnel?.IsSecureChannelEstablished ?? false;
    }
}