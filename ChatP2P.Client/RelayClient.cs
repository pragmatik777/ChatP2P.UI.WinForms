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
        
        // Logging helper
        private async Task LogToFile(string message)
        {
            try
            {
                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var logDir = Path.Combine(desktopPath, "ChatP2P_Logs");
                Directory.CreateDirectory(logDir);
                var logFile = Path.Combine(logDir, "relay_client.log");
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                await File.AppendAllTextAsync(logFile, logEntry);
            }
            catch { /* Ignore log errors */ }
        }

        // ICE Event logging helper
        private async Task LogIceEvent(string iceType, string fromPeer, string toPeer, string status, string? iceData = null)
        {
            try
            {
                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var logDir = Path.Combine(desktopPath, "ChatP2P_Logs");
                Directory.CreateDirectory(logDir);
                var logFile = Path.Combine(logDir, "relay_client_ice.log");
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                
                var logEntry = $"[{timestamp}] 🧊 [ICE-{iceType.ToUpper()}] {fromPeer} → {toPeer} | {status}";
                if (!string.IsNullOrEmpty(iceData))
                    logEntry += $" | Data: {iceData}";
                logEntry += Environment.NewLine;
                
                await File.AppendAllTextAsync(logFile, logEntry);
            }
            catch { /* Ignore log errors */ }
        }
        public event Action<string, string, string?>? FriendRequestAccepted; // from, to, pqcPublicKey
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

                _isConnected = true;

                // Démarrer l'écoute sur les trois canaux
                _ = Task.Run(async () => await ListenFriendRequestChannel());
                _ = Task.Run(async () => await ListenMessagesChannel());
                _ = Task.Run(async () => await ListenFilesChannel());
                
                Console.WriteLine($"RelayClient connected as {displayName}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error connecting to RelayHub: {ex.Message}");
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
            
            Console.WriteLine("RelayClient disconnected");
        }
        
        // ===== FRIEND REQUESTS =====
        
        public async Task<bool> SendFriendRequestAsync(string fromPeer, string toPeer, string publicKey, string message = "")
        {
            if (!_isConnected || _friendRequestWriter == null) return false;
            
            try
            {
                var friendRequest = $"FRIEND_REQ:{fromPeer}:{toPeer}:{publicKey}:{message}";
                await _friendRequestWriter.WriteLineAsync(friendRequest);
                Console.WriteLine($"Friend request sent: {fromPeer} → {toPeer}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending friend request: {ex.Message}");
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
                Console.WriteLine($"Friend request accepted: {fromPeer} ← {toPeer}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error accepting friend request: {ex.Message}");
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
                Console.WriteLine($"Friend request rejected: {fromPeer} ← {toPeer}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error rejecting friend request: {ex.Message}");
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
                Console.WriteLine($"Private message sent: {fromPeer} → {toPeer}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending private message: {ex.Message}");
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
                Console.WriteLine($"📁 [FILES-CHANNEL-8891] Metadata sent: {fileName} ({fileSize} bytes) {fromPeer} → {toPeer}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending file metadata: {ex.Message}");
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
                    var activePqKey = peerKeys.FirstOrDefault(k => !k.Revoked && k.Public != null);

                    if (activePqKey?.Public != null)
                    {
                        finalChunkData = await CryptoService.EncryptMessage(chunkData, activePqKey.Public);
                        await CryptoService.LogCrypto($"🔒 [FILE-ENCRYPT] Chunk {chunkIndex}/{totalChunks} encrypted for {toPeer} ({chunkData.Length} → {finalChunkData.Length} bytes)");
                    }
                    else
                    {
                        await CryptoService.LogCrypto($"⚠️ [FILE-ENCRYPT] No PQC key for {toPeer}, sending chunk {chunkIndex} unencrypted");
                        Console.WriteLine($"⚠️ [FILE-ENCRYPT] No PQC key for {toPeer}, sending chunk unencrypted");
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
                    Console.WriteLine($"{encStatus} [FILES-CHANNEL-8891] Chunk {chunkIndex}/{totalChunks} sent ({finalChunkData.Length} bytes)");
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending file chunk: {ex.Message}");
                await CryptoService.LogCrypto($"❌ [FILE-ENCRYPT] Error sending chunk {chunkIndex}: {ex.Message}");
                return false;
            }
        }

        // ===== LISTENING =====
        
        private async Task ListenFriendRequestChannel()
        {
            await LogToFile("📡 [RelayClient] ListenFriendRequestChannel started");
            Console.WriteLine($"📡 [RelayClient] ListenFriendRequestChannel started");
            if (_friendRequestReader == null) 
            {
                await LogToFile("❌ [RelayClient] _friendRequestReader is null!");
                Console.WriteLine($"❌ [RelayClient] _friendRequestReader is null!");
                return;
            }
            
            try
            {
                while (_isConnected && !_cancellationToken!.Token.IsCancellationRequested)
                {
                    await LogToFile("[DEBUG] Waiting for message on friend request channel...");
                    Console.WriteLine($"[DEBUG] Waiting for message on friend request channel...");
                    var message = await _friendRequestReader.ReadLineAsync();
                    
                    if (message == null) 
                    {
                        await LogToFile("⚠️  [RelayClient] Received null message, connection closed");
                        Console.WriteLine($"⚠️  [RelayClient] Received null message, connection closed");
                        break;
                    }
                    
                    await LogToFile($"📥 [RelayClient] Raw message received: {message}");
                    Console.WriteLine($"📥 [RelayClient] Raw message received: {message}");
                    await ProcessFriendRequestMessage(message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [RelayClient] Error listening friend request channel: {ex.Message}");
            }
            Console.WriteLine($"📡 [RelayClient] ListenFriendRequestChannel ended");
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
                    Console.WriteLine($"📨 [MSG-CHANNEL-8888] Received: {message.Substring(0, Math.Min(100, message.Length))}...");
                    await LogToFile($"[MSG-CHANNEL-8888] Received: {message}");

                    await ProcessMessageChannelMessage(message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error listening message channel: {ex.Message}");
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
                Console.WriteLine($"Error listening files channel: {ex.Message}");
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

                                Console.WriteLine($"📁 [FILES-CHANNEL-8891] Metadata: {fileName} ({fileSize} bytes) from {fromPeer}");
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
                                            Console.WriteLine($"❌ [FILE-DECRYPT] No PQC private key to decrypt chunk {chunkIndex} from {fromPeer}");
                                            return; // Skip ce chunk si pas de clé
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        await CryptoService.LogCrypto($"❌ [FILE-DECRYPT] Failed to decrypt chunk {chunkIndex}: {ex.Message}");
                                        Console.WriteLine($"❌ [FILE-DECRYPT] Failed to decrypt chunk {chunkIndex}: {ex.Message}");
                                        return; // Skip ce chunk si décryption échoue
                                    }
                                }

                                // ✅ OPTIMISÉ: Log seulement tous les 100 chunks pour éviter spam
                                if (chunkIndex % 100 == 0)
                                {
                                    var encStatus = encryptionFlag == "ENC" ? "🔓 decrypted" : "📦 clear";
                                    Console.WriteLine($"{encStatus} [FILES-CHANNEL-8891] Chunk {chunkIndex}/{totalChunks} ({chunkData.Length} bytes)");
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
                                    Console.WriteLine($"📦 [FILES-CHANNEL-8891-LEGACY] Chunk {chunkIndex}/{totalChunks} ({chunkData.Length} bytes)");
                                }

                                FileChunkRelayReceived?.Invoke(transferId, chunkIndex, totalChunks, chunkData);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing files channel message: {ex.Message}");
            }
        }

        private async Task ProcessFriendRequestMessage(string message)
        {
            await LogToFile($"🔄 [RelayClient] ProcessFriendRequestMessage: {message}");
            Console.WriteLine($"🔄 [RelayClient] ProcessFriendRequestMessage: {message}");
            try
            {
                if (message.StartsWith("FRIEND_REQ:"))
                {
                    await LogToFile("🎯 [RelayClient] Friend request message détectée!");
                    Console.WriteLine($"🎯 [RelayClient] Friend request message détectée!");
                    // Format: FRIEND_REQ:fromPeer:toPeer:publicKey:message
                    var parts = message.Substring("FRIEND_REQ:".Length).Split(':', 4);
                    await LogToFile($"[DEBUG] Parts count: {parts.Length}");
                    Console.WriteLine($"[DEBUG] Parts count: {parts.Length}");
                    
                    if (parts.Length >= 4)
                    {
                        var fromPeer = parts[0];
                        var toPeer = parts[1];
                        var publicKey = parts[2];
                        var requestMessage = parts[3];
                        
                        await LogToFile($"[DEBUG] From: {fromPeer}, To: {toPeer}, Key: {publicKey}, Msg: {requestMessage}");
                        await LogToFile($"✅ Friend request received: {fromPeer} → {toPeer}");
                        Console.WriteLine($"[DEBUG] From: {fromPeer}, To: {toPeer}, Key: {publicKey}, Msg: {requestMessage}");
                        Console.WriteLine($"✅ Friend request received: {fromPeer} → {toPeer}");
                        FriendRequestReceived?.Invoke(fromPeer, toPeer, publicKey, requestMessage);
                        await LogToFile("✅ FriendRequestReceived event invoked");
                        Console.WriteLine($"✅ FriendRequestReceived event invoked");
                    }
                    else
                    {
                        await LogToFile($"❌ [RelayClient] Invalid friend request format: {parts.Length} parts");
                        Console.WriteLine($"❌ [RelayClient] Invalid friend request format: {parts.Length} parts");
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

                        Console.WriteLine($"Friend request accepted: {fromPeer} ← {toPeer} (PQC: {!string.IsNullOrEmpty(pqcPublicKey)})");
                        FriendRequestAccepted?.Invoke(fromPeer, toPeer, pqcPublicKey);
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
                        
                        Console.WriteLine($"Friend request rejected: {fromPeer} ← {toPeer}");
                        FriendRequestRejected?.Invoke(fromPeer, toPeer);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing friend request message: {ex.Message}");
            }
        }
        
        private async Task ProcessMessageChannelMessage(string message)
        {
            try
            {
                // ✅ DEBUG: Log tous les messages reçus pour diagnostiquer WEBRTC_INITIATE
                // ✅ FIX CRITIQUE: Plus de log debug pour éviter les 5GB de logs avec gros fichiers
                // Console.WriteLine($"📥 [RELAY-DEBUG] Received message: {message.Substring(0, Math.Min(100, message.Length))}...");
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
                                
                                Console.WriteLine($"📁 [RELAY-FILE] Metadata received: {fileName} ({fileSize} bytes) from {fromPeer}");
                                FileMetadataRelayReceived?.Invoke(transferId, fileName, fileSize, fromPeer);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"❌ [RELAY-FILE] Error parsing metadata: {ex.Message}");
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
                                Console.WriteLine($"❌ [RELAY-FILE] Error parsing chunk: {ex.Message}");
                            }
                        }
                        else
                        {
                            // Message privé normal
                            Console.WriteLine($"Private message received: {fromPeer} → {toPeer}");
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
                            
                            Console.WriteLine($"💬 Chat message received from {fromPeer}: {content}");
                            ChatMessageReceived?.Invoke(fromPeer, timestamp, content);
                        }
                    }
                }
                else if (message.StartsWith("ICE_OFFER:") || 
                         message.StartsWith("ICE_ANSWER:") ||
                         message.StartsWith("ICE_CAND:"))
                {
                    Console.WriteLine($"🧊 [ICE-LEGACY] Legacy ICE signal received: {message.Substring(0, Math.Min(50, message.Length))}...");
                    IceSignalReceived?.Invoke(message);
                }
                else if (message.StartsWith("WEBRTC_INITIATE:"))
                {
                    // ✅ FIX: Explicitly log WebRTC initiation detection
                    Console.WriteLine($"🎯 [MSG-PROCESS] WEBRTC_INITIATE detected, calling ProcessWebRTCInitiate");
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
                        
                        Console.WriteLine($"📡 [STATUS-SYNC] Received from {fromPeer}: {statusType} = {enabled}");
                        StatusSyncReceived?.Invoke(fromPeer, statusType, enabled, timestamp);
                    }
                }
                else if (message.StartsWith("PEERS:"))
                {
                    // Format: PEERS:peer1,peer2,peer3
                    var peerList = message.Substring("PEERS:".Length);
                    var peers = string.IsNullOrEmpty(peerList) ? new List<string>() : peerList.Split(',').ToList();
                    
                    Console.WriteLine($"Peer list updated: {peers.Count} peers");
                    PeerListUpdated?.Invoke(peers);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [MESSAGE-CHANNEL] Error processing message: {ex.Message}");
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
                
                Console.WriteLine($"🚀 [WEBRTC-INITIATE] Processing initiation message: {jsonData}");
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
                        Console.WriteLine($"🎯 [WEBRTC-INITIATE] Server requests ICE offer creation: {initiatorPeer} → {targetPeer}");
                        await LogToFile($"[WEBRTC-INITIATE] Creating offer: {initiatorPeer} → {targetPeer}");
                        await LogIceEvent("INITIATE", initiatorPeer, targetPeer, "RelayClient received WebRTC offer initiation from server", jsonData.Substring(0, Math.Min(100, jsonData.Length)));
                        
                        // Déclencher l'événement pour que MainWindow traite la création d'offer
                        WebRTCInitiateReceived?.Invoke(targetPeer, initiatorPeer);
                    }
                    else if (action == "incoming_connection")
                    {
                        Console.WriteLine($"🔔 [WEBRTC-INITIATE] Incoming connection notification: {initiatorPeer} → {targetPeer}");
                        await LogToFile($"[WEBRTC-INITIATE] Incoming connection from: {initiatorPeer}");
                        await LogIceEvent("INCOMING", initiatorPeer, targetPeer, "RelayClient received WebRTC incoming connection notification", jsonData.Substring(0, Math.Min(100, jsonData.Length)));
                        
                        // For incoming connections, we don't create an offer, just log the notification
                        // The initiator will create the offer and it will be relayed to us via WebRTCSignalReceived
                    }
                    else
                    {
                        Console.WriteLine($"❓ [WEBRTC-INITIATE] Unknown action: {action}");
                        await LogToFile($"[WEBRTC-INITIATE] Unknown action: {action}");
                    }
                }
                else
                {
                    Console.WriteLine($"❌ [WEBRTC-INITIATE] Invalid initiation message format");
                    await LogToFile($"[WEBRTC-INITIATE] ERROR: Invalid format - {jsonData}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [WEBRTC-INITIATE] Error processing initiation: {ex.Message}");
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
                
                Console.WriteLine($"📡 [WEBRTC-SIGNAL] Processing signal: {jsonData.Substring(0, Math.Min(80, jsonData.Length))}...");
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
                    
                    Console.WriteLine($"🔄 [WEBRTC-SIGNAL] Routing {iceType}: {fromPeer} → {toPeer}");
                    await LogToFile($"[WEBRTC-SIGNAL] {iceType} from {fromPeer} to {toPeer}");
                    await LogIceEvent("SIGNAL", fromPeer, toPeer, $"RelayClient received {iceType} from server", iceData.Substring(0, Math.Min(100, iceData.Length)));
                    
                    // Déclencher l'événement pour que MainWindow traite le signal ICE
                    WebRTCSignalReceived?.Invoke(iceType, fromPeer, toPeer, iceData);
                }
                else
                {
                    Console.WriteLine($"❌ [WEBRTC-SIGNAL] Invalid signal message format");
                    await LogToFile($"[WEBRTC-SIGNAL] ERROR: Invalid format - {jsonData}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [WEBRTC-SIGNAL] Error processing signal: {ex.Message}");
                await LogToFile($"[WEBRTC-SIGNAL] ERROR: {ex.Message}");
            }
        }
        
        public bool IsConnected => _isConnected;
    }
}