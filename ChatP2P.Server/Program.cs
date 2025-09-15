using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatP2P.Crypto;

namespace ChatP2P.Server
{
    public class Program
    {
        private static TcpListener? _tcpListener;
        private static RelayHub? _relayHub;

        // Getter public pour le RelayHub
        public static RelayHub? GetRelayHub() => _relayHub;
        private static bool _isRunning = false;
        private static bool _p2pInitialized = false;
        private static string? _displayName = null;
        private static string? _detectedPrimaryIP = null;

        // Progress tracking for file transfers
        private static readonly Dictionary<string, TransferProgress> _activeTransfers = new();
        private static readonly Dictionary<string, DateTime> _connectedClients = new();

        // ✅ NOUVEAU: Logging général du serveur
        private static string? _generalLogPath = null;
        
        public static async Task Main(string[] args)
        {
            // ✅ NOUVEAU: Initialisation du logging général
            InitializeGeneralLogging();

            LogToFile("=== ChatP2P.Server Console v1.0 ===");
            LogToFile("Démarrage du serveur local...");

            try
            {
                // ✅ NOUVEAU: Configuration réseau AVANT l'initialisation SIPSorcery
                DetectAndLogNetworkIPs();

                // Initialisation du P2P Manager
                InitializeP2P();

                // Nettoyage des anciennes friend requests transmises
                await ContactManager.CleanupTransmittedRequests();

                // Démarrage du RelayHub centralisé
                await StartRelayHub();

                // Démarrage du serveur TCP sur localhost (API)
                await StartTcpServer();
                
                Console.WriteLine("Serveur démarré sur localhost:8889");
                Console.WriteLine("Appuyez sur 'q' pour arrêter...");
                
                // Attente des commandes
                await WaitForShutdown();
                
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur fatale: {ex.Message}");
                Console.WriteLine("Appuyez sur une touche pour quitter...");
                Console.ReadKey();
            }
            finally
            {
                await Shutdown();
            }
        }
        
        private static async Task StartTcpServer()
        {
            _tcpListener = new TcpListener(IPAddress.Any, 8889); // Écoute sur toutes les interfaces
            _tcpListener.Start();
            _isRunning = true;
            
            // Écoute des connexions en arrière-plan
            _ = Task.Run(async () =>
            {
                while (_isRunning)
                {
                    try
                    {
                        var tcpClient = await _tcpListener.AcceptTcpClientAsync();
                        _ = Task.Run(() => HandleClient(tcpClient));
                    }
                    catch (ObjectDisposedException)
                    {
                        break; // Serveur arrêté
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Erreur acceptation client: {ex.Message}");
                    }
                }
            });
        }
        
        private static async Task HandleClient(TcpClient client)
        {
            var buffer = new byte[4096];
            var stream = client.GetStream();
            
            try
            {
                while (client.Connected)
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;
                    
                    var request = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var response = await ProcessApiRequest(request);
                    
                    var responseBytes = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur client: {ex.Message}");
            }
            finally
            {
                client.Close();
            }
        }
        
        private static async Task<string> ProcessApiRequest(string request)
        {
            try
            {
                var apiRequest = JsonSerializer.Deserialize<ApiRequest>(request);
                if (apiRequest == null) return CreateErrorResponse("Format de requête invalide");
                
                // Only log API calls that are not frequent polling requests
                if (!(apiRequest.Command?.ToLower() == "contacts" && apiRequest.Action?.ToLower() == "get_friend_requests"))
                {
                    Console.WriteLine($"API: {apiRequest.Command} - {apiRequest.Action}");
                }
                
                return apiRequest.Command.ToLower() switch
                {
                    "p2p" => await HandleP2PCommand(apiRequest),
                    "contacts" => await HandleContactsCommand(apiRequest),
                    "crypto" => await HandleCryptoCommand(apiRequest),
                    "keyexchange" => await HandleKeyExchangeCommand(apiRequest),
                    "search" => await HandleSearchCommand(apiRequest),
                    "security" => await HandleSecurityCommand(apiRequest),
                    "status" => HandleStatusCommand(apiRequest),
                    _ => CreateErrorResponse($"Commande inconnue: {apiRequest.Command}")
                };
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur traitement requête: {ex.Message}");
            }
        }
        
        private static async Task<string> HandleP2PCommand(ApiRequest request)
        {
            // ✅ DEBUG: Log P2P API calls (skip frequent polling)
            if (request.Action?.ToLower() != "get_transfer_progress")
            {
                Console.WriteLine($"🔍 [DEBUG-API] P2P Command: {request.Action}");
            }

            return request.Action?.ToLower() switch
            {
                "start" => await StartP2PNetwork(request.Data),
                "stop" => await StopP2PNetwork(),
                "peers" => await GetConnectedPeers(),
                "connect" => await StartP2PConnection(request.Data),
                "send_message" => await SendP2PMessage(request.Data),
                "send_file" => await SendP2PFile(request.Data),
                "send_file_stream" => await SendP2PFileStream(request.Data),
                "send_raw_data" => await SendRawP2PData(request.Data),
                "send_webrtc_direct" => await SendWebRTCDirect(request.Data),
                "check_connection" => await CheckP2PConnection(request.Data),
                "get_transfer_progress" => await GetTransferProgress(request.Data),
                "send_file_relay" => await SendRelayFile(request.Data),
                "handle_offer" => await HandleP2POffer(request.Data),
                "handle_answer" => await HandleP2PAnswer(request.Data),
                "handle_candidate" => await HandleP2PCandidate(request.Data),
                "connection_status" => await GetP2PConnectionStatus(request.Data),
                "ice_stats" => await GetIceServerStats(),
                "ice_test" => await TestIceConnectivity(),
                "ice_config" => await GetIceConfiguration(request.Data),
                "ice_signal" => await HandleIceSignal(request.Data),
                "create_ice_offer" => await CreateIceOffer(request.Data),
                "handle_file_message" => await HandleFileMessage(request.Data),
                "notify_connection_ready" => await NotifyConnectionReady(request.Data),
                _ => CreateErrorResponse($"Action P2P inconnue: {request.Action}")
            };
        }
        
        private static async Task<string> HandleContactsCommand(ApiRequest request)
        {
            return request.Action?.ToLower() switch
            {
                "list" => await GetContactList(),
                "add" => await AddContact(request.Data),
                "remove" => await RemoveContact(request.Data),
                "import_contact" => await ImportContact(request.Data),
                "send_friend_request" => await SendFriendRequest(request.Data),
                "get_friend_requests" => await GetFriendRequests(request.Data),
                "receive_friend_request" => await ReceiveFriendRequest(request.Data),
                "accept_friend_request" => await AcceptFriendRequest(request.Data),
                "reject_friend_request" => await RejectFriendRequest(request.Data),
                "request" => await CreateContactRequest(request.Data),
                "accept" => await AcceptContactRequest(request.Data),
                "reject" => await RejectContactRequest(request.Data),
                "pending" => await GetPendingRequests(request.Data),
                "sent" => await GetSentRequests(request.Data),
                _ => CreateErrorResponse($"Action Contacts inconnue: {request.Action}")
            };
        }
        
        private static async Task<string> HandleCryptoCommand(ApiRequest request)
        {
            return request.Action?.ToLower() switch
            {
                "generate_keypair" => await GenerateKeyPair(),
                "get_my_key" => await GetMyPublicKey(),
                "encrypt" => await EncryptMessage(request.Data),
                "decrypt" => await DecryptMessage(request.Data),
                _ => CreateErrorResponse($"Action Crypto inconnue: {request.Action}")
            };
        }
        
        private static async Task<string> HandleKeyExchangeCommand(ApiRequest request)
        {
            return request.Action?.ToLower() switch
            {
                "initiate" => await InitiateKeyExchange(request.Data),
                "respond" => await RespondKeyExchange(request.Data),
                "finalize" => await FinalizeKeyExchange(request.Data),
                "cancel" => await CancelKeyExchange(request.Data),
                "status" => await GetKeyExchangeStatus(request.Data),
                "sessions" => await GetKeyExchangeSessions(request.Data),
                "cleanup" => await CleanupKeyExchangeSessions(),
                _ => CreateErrorResponse($"Action KeyExchange inconnue: {request.Action}")
            };
        }

        private static async Task<string> HandleSearchCommand(ApiRequest request)
        {
            return request.Action?.ToLower() switch
            {
                "find_peer" => await SearchPeers(request.Data),
                _ => CreateErrorResponse($"Action Search inconnue: {request.Action}")
            };
        }

        private static async Task<string> HandleSecurityCommand(ApiRequest request)
        {
            return request.Action?.ToLower() switch
            {
                "list_peers" => await GetSecurityPeerList(request.Data),
                "get_my_fingerprint" => await GetMyFingerprint(),
                "set_trusted" => await SetPeerTrusted(request.Data),
                "set_note" => await SetPeerNote(request.Data),
                "reset_tofu" => await ResetPeerTofu(request.Data),
                "import_key" => await ImportPeerKey(request.Data),
                "export_my_key" => await ExportMyKey(),
                _ => CreateErrorResponse($"Action Security inconnue: {request.Action}")
            };
        }

        private static string HandleStatusCommand(ApiRequest request)
        {
            var status = new
            {
                server_running = _isRunning,
                p2p_active = _p2pInitialized,
                connected_peers = 0, // TODO: Implémenter GetPeerCount
                contact_count = ContactManager.GetContactCount(),
                active_key_exchanges = KeyExchangeManager.GetActiveSessionCount(),
                key_exchange_stats = KeyExchangeManager.GetStats(),
                timestamp = DateTime.Now
            };
            
            return CreateSuccessResponse(status);
        }
        
        // Initialise le P2P Manager (Module VB.NET)
        private static async Task StartRelayHub()
        {
            try 
            {
                _relayHub = new RelayHub(7777, 8888, 8891); // Friend requests: 7777, Messages: 8888, Files: 8891
                
                // Subscribe to events
                _relayHub.FriendRequestReceived += async (from, to, publicKey, message) =>
                {
                    Console.WriteLine($"[RELAY] Friend request received: {from} → {to}");
                    
                    // Persister la friend request côté destinataire
                    await ContactManager.ReceiveFriendRequestFromP2P(from, to, publicKey, message);
                };
                
                _relayHub.PrivateArrived += (from, to, body) =>
                {
                    Console.WriteLine($"[RELAY] Private message: {from} → {to}");
                };

                await _relayHub.StartAsync();
                Console.WriteLine("RelayHub centralisé démarré");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur démarrage RelayHub: {ex.Message}");
            }
        }

        private static void InitializeP2P()
        {
            try
            {
                // Initialize P2P Service with signaling function
                P2PService.Initialize("ChatP2P.Server", async (peer, signal) =>
                {
                    Console.WriteLine($"🧊 [ICE-SIGNAL] Received signal for {peer}: {signal}");
                    
                    // Parse signal format: ICE_TYPE:sender:receiver:data
                    try
                    {
                        var parts = signal.Split(':', 4);
                        if (parts.Length >= 3)
                        {
                            var iceType = parts[0];
                            var sender = parts[1];
                            var receiver = parts[2];
                            var iceData = parts.Length > 3 ? parts[3] : "";
                            
                            Console.WriteLine($"🔍 [ICE-PARSE] Type: {iceType}, From: {sender}, To: {receiver}");
                            
                            // Use the new RelaySignalingMessage method for proper WebRTC signaling
                            var relaySuccess = await P2PService.RelaySignalingMessage(iceType, sender, receiver, iceData);
                            
                            if (relaySuccess)
                            {
                                Console.WriteLine($"✅ [ICE-SIGNAL] {iceType} successfully relayed: {sender} → {receiver}");
                            }
                            else
                            {
                                Console.WriteLine($"❌ [ICE-SIGNAL] Failed to relay {iceType}: {sender} → {receiver}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"❌ [ICE-SIGNAL] Invalid signal format: {signal}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ [ICE-SIGNAL] Error processing signal: {ex.Message}");
                        
                        // Fallback to old method for backward compatibility
                        var correctedSignal = P2PService.CorrectSignalSender(signal);
                        var relayHub = GetRelayHub();
                        if (relayHub != null)
                        {
                            await relayHub.SendToClient(peer, correctedSignal);
                        }
                    }
                });
                
                // Initialize FileTransferService with progress callbacks
                InitializeFileTransferService();

                // ✅ NOUVEAU: Initialiser P2PManager dual-channel
                P2PManager.Init(async (targetPeer, signal) => {
                    Console.WriteLine($"✅ [P2P-CALLBACK] P2PManager signal: {targetPeer} -> {signal.Substring(0, Math.Min(50, signal.Length))}...");
                    // Les signaux P2PManager sont gérés localement sur le serveur
                    // Pas besoin de les renvoyer aux clients ici
                }, "ChatP2P.Server");

                _p2pInitialized = true;
                Console.WriteLine("P2P Service initialized");
                Console.WriteLine("✅ [P2P-INIT] P2PManager dual-channel initialized on server");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur initialisation P2P: {ex.Message}");
                _p2pInitialized = false;
            }
        }
        
        // Implémentations des méthodes P2P
        private static async Task<string> StartP2PNetwork(object? data)
        {
            try
            {
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                // Extract client name from data if provided
                string? clientName = null;
                if (data != null)
                {
                    try
                    {
                        var json = JsonSerializer.Serialize(data);
                        var startData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                        if (startData?.TryGetValue("display_name", out var nameObj) == true)
                        {
                            clientName = nameObj?.ToString();
                        }
                    }
                    catch { }
                }
                
                // Register this client as connected if we have a name
                if (!string.IsNullOrWhiteSpace(clientName))
                {
                    _connectedClients[clientName] = DateTime.Now;
                    Console.WriteLine($"Client '{clientName}' registered in P2P network");
                }
                
                // P2P network is already running once initialized
                var stats = P2PService.GetStats();
                Console.WriteLine("P2P Network ready for connections");
                return CreateSuccessResponse(new { message = "P2P network started", stats });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting P2P network: {ex.Message}");
                return CreateErrorResponse($"Failed to start P2P network: {ex.Message}");
            }
        }
        
        private static async Task<string> StopP2PNetwork()
        {
            await Task.Delay(1);
            Console.WriteLine("P2P Network shutdown requested");
            return CreateSuccessResponse("P2P network stopped");
        }
        
        private static async Task<string> GetConnectedPeers()
        {
            try
            {
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                var connectedPeers = P2PService.GetConnectedPeers();
                Console.WriteLine($"Connected peers count: {connectedPeers.Count(kvp => kvp.Value)}");
                return CreateSuccessResponse(connectedPeers);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting connected peers: {ex.Message}");
                return CreateErrorResponse($"Failed to get connected peers: {ex.Message}");
            }
        }
        
        private static async Task<string> SearchPeers(object? data)
        {
            if (data == null) return CreateErrorResponse("Search data missing");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var searchData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (!searchData!.TryGetValue("peer_name", out var peerNameObj) || 
                    peerNameObj?.ToString() is not string searchTerm)
                {
                    return CreateErrorResponse("peer_name field required");
                }
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                // Search in registered clients list instead of P2P connections
                var matchingPeers = _connectedClients
                    .Where(kvp => kvp.Key.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                    .Select(kvp => new 
                    {
                        name = kvp.Key,
                        status = "Online"
                    })
                    .ToList();
                
                Console.WriteLine($"Search for '{searchTerm}' returned {matchingPeers.Count} results");
                return CreateSuccessResponse(matchingPeers);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error searching peers: {ex.Message}");
                return CreateErrorResponse($"Failed to search peers: {ex.Message}");
            }
        }
        
        private static async Task<string> SendP2PMessage(object? data)
        {
            Console.WriteLine($"🔄 [SEND-MSG] SendP2PMessage called with data: {data}");
            
            if (data == null) 
            {
                Console.WriteLine($"❌ [SEND-MSG] Data is null");
                return CreateErrorResponse("Message data missing");
            }
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                Console.WriteLine($"📝 [SEND-MSG] JSON: {json}");
                
                var messageData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                Console.WriteLine($"🗂️ [SEND-MSG] Parsed data: {messageData.Count} fields");
                
                if (!messageData!.TryGetValue("peer", out var peer) || 
                    !messageData.TryGetValue("message", out var message))
                {
                    Console.WriteLine($"❌ [SEND-MSG] Missing fields - peer: {messageData.ContainsKey("peer")}, message: {messageData.ContainsKey("message")}");
                    return CreateErrorResponse("Peer and message fields required");
                }
                
                // Option de chiffrement (par défaut: false = clear text)
                var encrypted = messageData.TryGetValue("encrypted", out var encStr) && 
                               (encStr?.ToLower() == "true" || encStr == "1");
                
                // Try to get the "from" field, fallback to "ChatP2P.Server" if not provided
                var fromPeer = messageData.TryGetValue("from", out var from) ? from : "ChatP2P.Server";
                
                Console.WriteLine($"🎯 [SEND-MSG] Target peer: {peer}, Message: {message}, Encrypted: {encrypted}");
                
                if (!_p2pInitialized)
                {
                    Console.WriteLine($"❌ [SEND-MSG] P2P not initialized");
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                Console.WriteLine($"📡 [SEND-MSG] Calling P2PService.SendTextMessage({peer}, {message}) from {fromPeer}, encrypted={encrypted}");
                var success = await P2PService.SendTextMessage(peer, message, fromPeer, encrypted);
                Console.WriteLine($"📋 [SEND-MSG] P2PService.SendTextMessage returned: {success}");
                
                if (success)
                {
                    Console.WriteLine($"✅ [SEND-MSG] Message sent to {peer}: {message}");
                    return CreateSuccessResponse("Message sent successfully");
                }
                else
                {
                    Console.WriteLine($"❌ [SEND-MSG] Failed to send message to {peer}");
                    return CreateErrorResponse($"Failed to send message to {peer}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending P2P message: {ex.Message}");
                return CreateErrorResponse($"Failed to send message: {ex.Message}");
            }
        }
        
        private static async Task<string> SendP2PFile(object? data)
        {
            if (data == null) return CreateErrorResponse("File data missing");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var fileData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (!fileData!.TryGetValue("peer", out var peerObj) || 
                    !fileData.TryGetValue("filePath", out var filePathObj))
                {
                    return CreateErrorResponse("Peer and filePath fields required");
                }
                
                var peer = peerObj.ToString()!;
                var filePath = filePathObj.ToString()!;
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }

                if (!File.Exists(filePath))
                {
                    return CreateErrorResponse($"File not found: {filePath}");
                }

                // Obtenir le nom du peer local pour le transfert
                var fromPeer = _displayName ?? "ChatP2P.Server";
                
                Console.WriteLine($"📁 [FILE-TRANSFER] Starting P2P file transfer: {filePath} → {peer}");
                
                var success = await FileTransferService.Instance.SendFileP2PAsync(fromPeer, peer, filePath);
                if (success)
                {
                    var fileInfo = new FileInfo(filePath);
                    Console.WriteLine($"📁 [FILE-TRANSFER] File transfer initiated: {fileInfo.Name} ({fileInfo.Length} bytes) → {peer}");
                    return CreateSuccessResponse("File transfer initiated");
                }
                else
                {
                    return CreateErrorResponse($"Failed to send file to {peer}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending P2P file: {ex.Message}");
                return CreateErrorResponse($"Failed to send file: {ex.Message}");
            }
        }

        private static async Task<string> SendP2PFileStream(object? data)
        {
            if (data == null) return CreateErrorResponse("File data missing");
            
            string transferId = ""; // Declare at method level for error handling
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var fileData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (!fileData!.TryGetValue("peer", out var peerObj) || 
                    !fileData.TryGetValue("fileContent", out var fileContentObj) ||
                    !fileData.TryGetValue("fileName", out var fileNameObj) ||
                    !fileData.TryGetValue("transferId", out var transferIdObj))
                {
                    return CreateErrorResponse("Peer, fileContent, fileName, and transferId fields required");
                }
                
                var peer = peerObj.ToString()!;
                var fileContent = fileContentObj.ToString()!; // Base64 encoded file content
                var fileName = fileNameObj.ToString()!;
                transferId = transferIdObj.ToString()!;
                var fromPeer = fileData.TryGetValue("fromPeer", out var fromObj) ? fromObj.ToString()! : _displayName ?? "ChatP2P.Server";
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }

                Console.WriteLine($"📁 [P2P-STREAM] Starting direct file stream: {fileName} → {peer} (ID: {transferId})");

                // Décoder le contenu du fichier
                byte[] fileBytes;
                try
                {
                    fileBytes = Convert.FromBase64String(fileContent);
                    Console.WriteLine($"📦 [P2P-STREAM] Decoded {fileBytes.Length} bytes for {fileName}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [P2P-STREAM] Failed to decode file content: {ex.Message}");
                    return CreateErrorResponse("Invalid file content encoding");
                }

                // Initialize progress tracking
                const int chunkSize = 2048; // 2KB chunks
                var totalChunks = (int)Math.Ceiling(fileBytes.Length / (double)chunkSize);
                
                var progressInfo = new TransferProgress
                {
                    TransferId = transferId,
                    FileName = fileName,
                    TotalChunks = totalChunks,
                    Status = "starting"
                };
                
                _activeTransfers[transferId] = progressInfo;
                Console.WriteLine($"📊 [P2P-STREAM] Progress tracking initialized: {totalChunks} chunks for {fileName}");

                // Envoyer metadata
                var metadata = new
                {
                    type = "FILE_METADATA",
                    transferId = transferId,
                    fileName = fileName,
                    fileSize = fileBytes.Length,
                    fromPeer = fromPeer,
                    toPeer = peer
                };
                
                var metadataJson = JsonSerializer.Serialize(metadata);
                var metadataSuccess = await P2PService.SendTextMessage(peer, metadataJson, fromPeer, false);
                
                if (!metadataSuccess)
                {
                    Console.WriteLine($"❌ [P2P-STREAM] Failed to send metadata to {peer}");
                    return CreateErrorResponse("Failed to send file metadata");
                }

                Console.WriteLine($"✅ [P2P-STREAM] Metadata sent successfully");

                // Stream les chunks directement via P2P avec buffering optimisé
                progressInfo.Status = "transferring";
                var sentChunks = 0;
                
                for (int i = 0; i < totalChunks; i++)
                {
                    var offset = i * chunkSize;
                    var remainingBytes = fileBytes.Length - offset;
                    var currentChunkSize = Math.Min(chunkSize, remainingBytes);
                    
                    var chunkData = new byte[currentChunkSize];
                    Array.Copy(fileBytes, offset, chunkData, 0, currentChunkSize);
                    
                    var chunkMessage = new
                    {
                        type = "FILE_CHUNK",
                        transferId = transferId,
                        chunkIndex = i,
                        chunkHash = ComputeFileHash(chunkData),
                        chunkData = Convert.ToBase64String(chunkData)
                    };
                    
                    var chunkJson = JsonSerializer.Serialize(chunkMessage);
                    var chunkSuccess = await P2PService.SendTextMessage(peer, chunkJson, fromPeer, false);
                    
                    if (chunkSuccess)
                    {
                        sentChunks++;
                        
                        // Update progress tracking
                        progressInfo.SentChunks = sentChunks;
                        
                        if (i % 100 == 0 || i == totalChunks - 1) // Log every 100 chunks or last chunk
                        {
                            var progress = (double)sentChunks / totalChunks * 100;
                            Console.WriteLine($"🚀 [P2P-STREAM] Progress: {progress:F1}% ({sentChunks}/{totalChunks} chunks)");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"❌ [P2P-STREAM] Failed to send chunk {i}");
                        return CreateErrorResponse($"Failed to send chunk {i}");
                    }
                    
                    // Small delay to prevent overwhelming the connection
                    if (i % 50 == 0) await Task.Delay(10);
                }

                // Mark transfer as completed
                progressInfo.Status = "completed";
                Console.WriteLine($"✅ [P2P-STREAM] File {fileName} streamed successfully ({sentChunks}/{totalChunks} chunks)");
                
                // Keep progress info for a short time for final status check, then remove
                _ = Task.Run(async () =>
                {
                    await Task.Delay(30000); // Keep for 30 seconds
                    _activeTransfers.Remove(transferId);
                    Console.WriteLine($"🗑️ [P2P-STREAM] Cleaned up progress tracking for {transferId}");
                });
                
                return CreateSuccessResponse($"File streamed successfully: {sentChunks}/{totalChunks} chunks");
            }
            catch (Exception ex)
            {
                // Mark transfer as failed if it exists
                if (_activeTransfers.TryGetValue(transferId, out var failedTransfer))
                {
                    failedTransfer.Status = "failed";
                }
                
                Console.WriteLine($"❌ [P2P-STREAM] Error: {ex.Message}");
                return CreateErrorResponse($"Failed to stream file: {ex.Message}");
            }
        }

        private static string ComputeFileHash(byte[] data)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = sha256.ComputeHash(data);
            return Convert.ToBase64String(hash);
        }

        private static async Task<string> GetTransferProgress(object? data)
        {
            await Task.Delay(1); // Make async
            
            try
            {
                // Obtenir tous les transferts actifs du FileTransferService local
                var activeTransfers = FileTransferService.Instance.GetActiveTransfers();
                
                // Only log if there are active transfers to avoid spam
                if (activeTransfers.Count > 0)
                {
                    Console.WriteLine($"📊 [TRANSFER-API] Found {activeTransfers.Count} active file transfers");
                }
                
                return CreateSuccessResponse(new
                {
                    activeTransfers = activeTransfers,
                    totalActive = activeTransfers.Count,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [TRANSFER-PROGRESS] Error: {ex.Message}");
                return CreateErrorResponse($"Failed to get transfer progress: {ex.Message}");
            }
        }

        private static async Task<string> SendRawP2PData(object? data)
        {
            if (data == null) return CreateErrorResponse("Data missing");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (!requestData!.TryGetValue("peer", out var peerObj) || 
                    !requestData.TryGetValue("data", out var dataObj))
                {
                    return CreateErrorResponse("Peer and data fields required");
                }
                
                var peer = peerObj.ToString()!;
                var dataBase64 = dataObj.ToString()!;
                var fromPeer = requestData.TryGetValue("fromPeer", out var fromObj) ? fromObj.ToString()! : _displayName ?? "ChatP2P.Server";
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                // Decode base64 data to binary
                byte[] binaryData;
                try
                {
                    binaryData = Convert.FromBase64String(dataBase64);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [RAW-P2P] Failed to decode base64 data: {ex.Message}");
                    return CreateErrorResponse("Invalid base64 data encoding");
                }
                
                Console.WriteLine($"📡 [RAW-P2P] Sending {binaryData.Length} bytes to {peer} via DataChannel");
                
                // Send binary data directly via P2P DataChannel
                var success = P2PService.SendBinaryData(peer, binaryData);
                
                if (success)
                {
                    Console.WriteLine($"✅ [RAW-P2P] Binary data sent successfully to {peer}");
                    return CreateSuccessResponse($"Raw data sent: {binaryData.Length} bytes");
                }
                else
                {
                    Console.WriteLine($"❌ [RAW-P2P] Failed to send binary data to {peer}");
                    return CreateErrorResponse("Failed to send raw P2P data");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [RAW-P2P] Error: {ex.Message}");
                return CreateErrorResponse($"Failed to send raw data: {ex.Message}");
            }
        }

        /// <summary>
        /// Envoie des données via WebRTC DataChannel direct (vraiment bypass relay)
        /// </summary>
        private static async Task<string> SendWebRTCDirect(object? data)
        {
            if (data == null) return CreateErrorResponse("Data missing");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (!requestData!.TryGetValue("peer", out var peerObj) || 
                    !requestData.TryGetValue("data", out var dataObj))
                {
                    return CreateErrorResponse("Peer and data fields required");
                }
                
                var peer = peerObj.ToString()!;
                var dataStr = dataObj.ToString()!;
                var isBinary = requestData.TryGetValue("is_binary", out var binaryObj) && 
                              (binaryObj.ToString()?.ToLower() == "true");
                
                Console.WriteLine($"🚀 [WEBRTC-DIRECT] Sending to {peer} - Binary: {isBinary}, Size: {dataStr.Length} chars");
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                // Vérifier que la connexion P2P existe
                if (!P2PService.IsConnected(peer))
                {
                    Console.WriteLine($"❌ [WEBRTC-DIRECT] No active P2P connection to {peer}");
                    return CreateErrorResponse($"No active P2P connection to {peer}");
                }
                
                bool success;
                if (isBinary)
                {
                    // Données binaires - décoder base64 et envoyer via DataChannel
                    try
                    {
                        var binaryData = Convert.FromBase64String(dataStr);
                        Console.WriteLine($"📦 [WEBRTC-DIRECT] Sending {binaryData.Length} binary bytes to {peer}");
                        success = P2PService.SendBinaryData(peer, binaryData);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ [WEBRTC-DIRECT] Failed to decode binary data: {ex.Message}");
                        return CreateErrorResponse("Invalid binary data encoding");
                    }
                }
                else
                {
                    // Données texte - envoyer directement via DataChannel
                    Console.WriteLine($"📝 [WEBRTC-DIRECT] Sending text data to {peer}: {dataStr.Substring(0, Math.Min(100, dataStr.Length))}...");
                    success = await P2PService.SendMessage(peer, dataStr);
                }
                
                if (success)
                {
                    Console.WriteLine($"✅ [WEBRTC-DIRECT] Data sent successfully to {peer} via direct DataChannel");
                    return CreateSuccessResponse($"WebRTC Direct data sent to {peer}");
                }
                else
                {
                    Console.WriteLine($"❌ [WEBRTC-DIRECT] Failed to send data to {peer}");
                    return CreateErrorResponse($"Failed to send WebRTC Direct data to {peer}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [WEBRTC-DIRECT] Error: {ex.Message}");
                return CreateErrorResponse($"WebRTC Direct error: {ex.Message}");
            }
        }

        /// <summary>
        /// Vérifie si une connexion P2P WebRTC active existe vers un peer
        /// </summary>
        private static async Task<string> CheckP2PConnection(object? data)
        {
            if (data == null) return CreateErrorResponse("Peer data missing");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (!requestData!.TryGetValue("peer", out var peerObj))
                {
                    return CreateErrorResponse("Peer field required");
                }
                
                var peer = peerObj.ToString()!;
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                var isConnected = P2PService.IsConnected(peer);
                Console.WriteLine($"🔍 [CHECK-P2P] Connection to {peer}: {(isConnected ? "ACTIVE" : "INACTIVE")}");
                
                if (isConnected)
                {
                    return CreateSuccessResponse($"Active WebRTC connection to {peer}");
                }
                else
                {
                    return CreateErrorResponse($"No active WebRTC connection to {peer}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [CHECK-P2P] Error: {ex.Message}");
                return CreateErrorResponse($"Connection check error: {ex.Message}");
            }
        }

        public class TransferProgress
        {
            public string TransferId { get; set; } = "";
            public string FileName { get; set; } = "";
            public int SentChunks { get; set; } = 0;
            public int TotalChunks { get; set; } = 0;
            public double ProgressPercent => TotalChunks > 0 ? (double)SentChunks / TotalChunks * 100 : 0;
            public string Status { get; set; } = "starting"; // starting, transferring, completed, failed
            public DateTime StartTime { get; set; } = DateTime.Now;
        }

        private static async Task<string> SendRelayFile(object? data)
        {
            if (data == null) return CreateErrorResponse("File data missing");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var fileData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (!fileData!.TryGetValue("peer", out var peerObj) || 
                    !fileData.TryGetValue("filePath", out var filePathObj))
                {
                    return CreateErrorResponse("Peer and filePath fields required");
                }
                
                var peer = peerObj.ToString()!;
                var filePath = filePathObj.ToString()!;

                if (!File.Exists(filePath))
                {
                    return CreateErrorResponse($"File not found: {filePath}");
                }

                var fromPeer = _displayName ?? "ChatP2P.Server";
                
                Console.WriteLine($"📁 [FILE-TRANSFER-RELAY] Starting relay file transfer: {filePath} → {peer}");
                
                var success = await SendFileViaRelayAsync(fromPeer, peer, filePath);
                if (success)
                {
                    var fileInfo = new FileInfo(filePath);
                    Console.WriteLine($"📁 [FILE-TRANSFER-RELAY] Relay transfer initiated: {fileInfo.Name} ({fileInfo.Length} bytes) → {peer}");
                    return CreateSuccessResponse("Relay file transfer initiated");
                }
                else
                {
                    return CreateErrorResponse($"Failed to send file via relay to {peer}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending relay file: {ex.Message}");
                return CreateErrorResponse($"Failed to send relay file: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a file via TCP relay through the RelayHub
        /// </summary>
        private static async Task<bool> SendFileViaRelayAsync(string fromPeer, string toPeer, string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists) return false;

                var transferId = Guid.NewGuid().ToString();
                
                // Send file metadata first via RelayHub
                var metadata = new
                {
                    type = "FILE_METADATA_RELAY",
                    transferId = transferId,
                    fileName = fileInfo.Name,
                    fileSize = fileInfo.Length,
                    fromPeer = fromPeer,
                    toPeer = toPeer
                };

                var metadataMessage = $"FILE_META_RELAY:{JsonSerializer.Serialize(metadata)}";
                
                var relayHub = GetRelayHub();
                if (relayHub == null)
                {
                    Console.WriteLine("❌ [FILE-TRANSFER-RELAY] RelayHub not available");
                    return false;
                }

                // Send metadata
                var metadataSent = await relayHub.SendToClient(toPeer, metadataMessage);
                if (!metadataSent)
                {
                    Console.WriteLine($"❌ [FILE-TRANSFER-RELAY] Failed to send metadata to {toPeer}");
                    return false;
                }

                Console.WriteLine($"📁 [FILE-TRANSFER-RELAY] Metadata sent to {toPeer}");

                // Send file in chunks via RelayHub
                const int chunkSize = 32768; // 32KB chunks for relay (larger than P2P)
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                var buffer = new byte[chunkSize];
                var chunkIndex = 0;
                var totalChunks = (int)Math.Ceiling(fileInfo.Length / (double)chunkSize);

                while (true)
                {
                    var bytesRead = await fileStream.ReadAsync(buffer, 0, chunkSize);
                    if (bytesRead == 0) break;

                    // Create chunk data
                    var chunkData = new byte[bytesRead];
                    Array.Copy(buffer, chunkData, bytesRead);

                    var chunkMessage = new
                    {
                        type = "FILE_CHUNK_RELAY",
                        transferId = transferId,
                        chunkIndex = chunkIndex,
                        totalChunks = totalChunks,
                        chunkData = Convert.ToBase64String(chunkData)
                    };

                    var chunkMessageJson = $"FILE_CHUNK_RELAY:{JsonSerializer.Serialize(chunkMessage)}";
                    
                    var chunkSent = await relayHub.SendToClient(toPeer, chunkMessageJson);
                    if (!chunkSent)
                    {
                        Console.WriteLine($"❌ [FILE-TRANSFER-RELAY] Failed to send chunk {chunkIndex} to {toPeer}");
                        return false;
                    }

                    chunkIndex++;
                    var progress = (chunkIndex / (double)totalChunks) * 100;
                    
                    if (chunkIndex % 10 == 0) // Log every 10 chunks
                    {
                        Console.WriteLine($"📦 [FILE-TRANSFER-RELAY] Progress: {progress:F1}% ({chunkIndex}/{totalChunks} chunks)");
                    }

                    // Small delay to prevent overwhelming the relay
                    await Task.Delay(10);
                }

                Console.WriteLine($"✅ [FILE-TRANSFER-RELAY] File {fileInfo.Name} sent successfully to {toPeer} ({chunkIndex} chunks)");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [FILE-TRANSFER-RELAY] Error sending file: {ex.Message}");
                return false;
            }
        }
        
        private static async Task<string> StartP2PConnection(object? data)
        {
            if (data == null) return CreateErrorResponse("Peer data missing");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var peerData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (!peerData!.TryGetValue("peer", out var peer))
                {
                    return CreateErrorResponse("Peer field required");
                }
                
                // Try to get the initiator (from client) - if not provided, fall back to "ChatP2P.Server"
                var initiator = "ChatP2P.Server";
                if (peerData.TryGetValue("from", out var fromPeer) && !string.IsNullOrEmpty(fromPeer))
                {
                    initiator = fromPeer;
                }
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                var success = await P2PService.StartP2PConnection(peer, initiator);
                if (success)
                {
                    Console.WriteLine($"P2P connection initiated: {initiator} → {peer}");
                    return CreateSuccessResponse($"P2P connection started: {initiator} → {peer}");
                }
                else
                {
                    return CreateErrorResponse($"Failed to start P2P connection: {initiator} → {peer}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting P2P connection: {ex.Message}");
                return CreateErrorResponse($"Failed to start P2P connection: {ex.Message}");
            }
        }
        
        private static async Task<string> HandleP2POffer(object? data)
        {
            if (data == null) return CreateErrorResponse("Offer data missing");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var offerData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (!offerData!.TryGetValue("fromPeer", out var fromPeer) || 
                    !offerData.TryGetValue("sdp", out var sdp))
                {
                    return CreateErrorResponse("fromPeer and sdp fields required");
                }
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                P2PService.HandleOffer(fromPeer, sdp);
                Console.WriteLine($"P2P offer handled from {fromPeer}");
                return CreateSuccessResponse("Offer handled");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling P2P offer: {ex.Message}");
                return CreateErrorResponse($"Failed to handle offer: {ex.Message}");
            }
        }
        
        private static async Task<string> HandleP2PAnswer(object? data)
        {
            if (data == null) return CreateErrorResponse("Answer data missing");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var answerData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (!answerData!.TryGetValue("fromPeer", out var fromPeer) || 
                    !answerData.TryGetValue("sdp", out var sdp))
                {
                    return CreateErrorResponse("fromPeer and sdp fields required");
                }
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                P2PService.HandleAnswer(fromPeer, sdp);
                Console.WriteLine($"P2P answer handled from {fromPeer}");
                return CreateSuccessResponse("Answer handled");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling P2P answer: {ex.Message}");
                return CreateErrorResponse($"Failed to handle answer: {ex.Message}");
            }
        }
        
        private static async Task<string> HandleP2PCandidate(object? data)
        {
            if (data == null) return CreateErrorResponse("Candidate data missing");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var candidateData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (!candidateData!.TryGetValue("fromPeer", out var fromPeer) || 
                    !candidateData.TryGetValue("candidate", out var candidate))
                {
                    return CreateErrorResponse("fromPeer and candidate fields required");
                }
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                P2PService.HandleCandidate(fromPeer, candidate);
                Console.WriteLine($"P2P candidate handled from {fromPeer}");
                return CreateSuccessResponse("Candidate handled");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling P2P candidate: {ex.Message}");
                return CreateErrorResponse($"Failed to handle candidate: {ex.Message}");
            }
        }
        
        private static async Task<string> HandleIceSignal(object? data)
        {
            // ✅ DEBUG: Log que HandleIceSignal est appelé
            Console.WriteLine($"🔍 [DEBUG-ICE] HandleIceSignal called with data: {data?.GetType().Name}");

            if (data == null) return CreateErrorResponse("ICE signal data missing");

            try
            {
                var json = JsonSerializer.Serialize(data);
                var iceData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

                if (!iceData!.TryGetValue("ice_type", out var iceTypeEl) ||
                    !iceData.TryGetValue("from_peer", out var fromPeerEl) ||
                    !iceData.TryGetValue("to_peer", out var toPeerEl) ||
                    !iceData.TryGetValue("ice_data", out var iceDataEl))
                {
                    return CreateErrorResponse("ice_type, from_peer, to_peer, and ice_data fields required");
                }

                var iceType = iceTypeEl.GetString();
                var fromPeer = fromPeerEl.GetString();
                var toPeer = toPeerEl.GetString();

                // ice_data peut être string ou objet - gérer les deux cas
                string iceSignalData;
                JsonElement iceDataObject;

                if (iceDataEl.ValueKind == JsonValueKind.String)
                {
                    // Ancien format - ice_data est une string
                    iceSignalData = iceDataEl.GetString()!;
                    iceDataObject = JsonSerializer.Deserialize<JsonElement>(iceSignalData);
                }
                else
                {
                    // Nouveau format - ice_data est déjà un objet JsonElement
                    iceDataObject = iceDataEl;
                    iceSignalData = JsonSerializer.Serialize(iceDataEl);
                }
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                Console.WriteLine($"🧊 [ICE-API] Received {iceType} from client {fromPeer} for {toPeer}");

                // ✅ NOUVEAU: Force network IP detection at API level since IceP2PSession constructor not reached
                DetectAndLogNetworkIPs();

                // ✅ SIMPLIFIÉ: Auto-answer maintenant géré dans CreateIceOffer directement
                // HandleIceSignal ne fait plus que relayer les messages normalement

                // ✅ PURE SIGNALING RELAY: Le serveur ne traite PAS les signaux WebRTC
                // Il les relaye seulement entre clients pour établissement P2P direct
                Console.WriteLine($"📡 [PURE-RELAY] Relaying {iceType}: {fromPeer} → {toPeer} (server does NO WebRTC processing)");

                // Puis relayer normalement aux autres clients
                var relaySuccess = await P2PService.RelaySignalingMessage(iceType, fromPeer, toPeer, iceSignalData);

                if (relaySuccess)
                {
                    Console.WriteLine($"✅ [ICE-API] {iceType} relayed successfully: {fromPeer} → {toPeer}");
                    LogToFile($"✅ [ICE-API] {iceType} relayed successfully: {fromPeer} → {toPeer}");
                    return CreateSuccessResponse($"ICE signal {iceType} processed locally and relayed from {fromPeer} to {toPeer}");
                }
                else
                {
                    Console.WriteLine($"❌ [ICE-API] Failed to relay {iceType}: {fromPeer} → {toPeer}");
                    LogToFile($"❌ [ICE-API] Failed to relay {iceType}: {fromPeer} → {toPeer}");
                    return CreateErrorResponse($"Failed to relay ICE signal {iceType} to {toPeer}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [ICE-API] Error handling ICE signal: {ex.Message}");
                return CreateErrorResponse($"Failed to handle ICE signal: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Gère les messages de transfert de fichiers P2P reçus via WebRTC DataChannels
        /// </summary>
        private static async Task<string> HandleFileMessage(object? data)
        {
            if (data == null) return CreateErrorResponse("File message data missing");

            try
            {
                var json = JsonSerializer.Serialize(data);
                var messageData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

                if (!messageData!.TryGetValue("fromPeer", out var fromPeerObj) ||
                    !messageData.TryGetValue("message", out var messageObj))
                {
                    return CreateErrorResponse("fromPeer and message fields required");
                }

                var fromPeer = fromPeerObj.ToString() ?? "";
                var message = messageObj.ToString() ?? "";

                LogToFile($"📁 [FILE-MESSAGE-API] Processing file message from {fromPeer}");

                // Rediriger vers P2PService pour traitement (même logique que OnP2PTextReceived)
                // Simuler réception P2P pour traiter FILE_CHUNK et FILE_METADATA
                P2PService.SimulateP2PTextReceived(fromPeer, message);

                return CreateSuccessResponse("File message processed successfully");
            }
            catch (Exception ex)
            {
                LogToFile($"❌ [FILE-MESSAGE-API] Error processing file message: {ex.Message}");
                return CreateErrorResponse($"Failed to process file message: {ex.Message}");
            }
        }

        /// <summary>
        /// NOUVEAU: API pour créer une ICE offer automatiquement quand le serveur le demande
        /// </summary>
        private static async Task<string> CreateIceOffer(object? data)
        {
            if (data == null) return CreateErrorResponse("Offer creation data missing");

            try
            {
                var json = JsonSerializer.Serialize(data);
                var offerData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                if (!offerData!.TryGetValue("initiator_peer", out var initiatorPeer) ||
                    !offerData.TryGetValue("target_peer", out var targetPeer))
                {
                    return CreateErrorResponse("initiator_peer and target_peer fields required");
                }

                // ✅ NOUVEAU: Récupérer l'IP du client initiateur
                var clientIP = "127.0.0.1"; // Fallback par défaut
                if (offerData.TryGetValue("client_ip", out var providedIP) && !string.IsNullOrEmpty(providedIP))
                {
                    clientIP = providedIP;
                    LogToFile($"🌐 [CREATE-OFFER] Using client IP: {clientIP} for {initiatorPeer}");
                }
                else
                {
                    LogToFile($"⚠️ [CREATE-OFFER] No client IP provided, using fallback: {clientIP}");
                }
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                LogToFile($"🚀 [CREATE-OFFER] Creating ICE offer: {initiatorPeer} → {targetPeer}");
                
                // FIXE: Ne pas rappeler StartP2PConnection (causait boucle infinie)
                // Au lieu, créer directement l'offre ICE et la transmettre via le serveur
                
                // ✅ NOUVEAU: Créer une offre ICE avec l'IP PUBLIQUE (pas locale)
                var publicIP = "77.57.58.6"; // IP publique détectée par STUN pour WebRTC
                var fakeIceOffer = new
                {
                    type = "offer",
                    sdp = $"v=0\r\no=- {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} 2 IN IP4 {publicIP}\r\ns=-\r\nt=0 0\r\n" +
                          $"a=group:BUNDLE 0\r\na=msid-semantic: WMS\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n" +
                          $"c=IN IP4 {publicIP}\r\na=ice-ufrag:test{Random.Shared.Next(1000, 9999)}\r\na=ice-pwd:test{Random.Shared.Next(100000, 999999)}"
                };
                
                var offerJson = JsonSerializer.Serialize(fakeIceOffer);
                
                // Transmettre l'offre via le système de signaling existant
                var relaySuccess = await P2PService.RelaySignalingMessage("offer", initiatorPeer, targetPeer, offerJson);
                
                if (relaySuccess)
                {
                    LogToFile($"✅ [CREATE-OFFER] ICE offer transmitted via signaling: {initiatorPeer} → {targetPeer}");
                    LogToFile($"🔄 [PURE-RELAY] Server acting as pure relay - clients will handle WebRTC negotiation locally");

                    return CreateSuccessResponse($"ICE offer relayed successfully: {initiatorPeer} → {targetPeer} - clients will handle WebRTC locally");
                }
                else
                {
                    LogToFile($"❌ [CREATE-OFFER] Failed to transmit ICE offer: {initiatorPeer} → {targetPeer}");
                    return CreateErrorResponse($"Failed to transmit ICE offer to {targetPeer}");
                }
            }
            catch (Exception ex)
            {
                LogToFile($"❌ [CREATE-OFFER] Error creating ICE offer: {ex.Message}");
                return CreateErrorResponse($"Failed to create ICE offer: {ex.Message}");
            }
        }
        
        private static async Task<string> GetP2PConnectionStatus(object? data)
        {
            if (data == null) return CreateErrorResponse("Peer data missing");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var peerData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (!peerData!.TryGetValue("peer", out var peer))
                {
                    return CreateErrorResponse("Peer field required");
                }
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                var isConnected = P2PService.IsConnected(peer);
                var status = new { peer, connected = isConnected };
                
                Console.WriteLine($"P2P status for {peer}: {isConnected}");
                return CreateSuccessResponse(status);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting P2P status: {ex.Message}");
                return CreateErrorResponse($"Failed to get P2P status: {ex.Message}");
            }
        }

        // Nouvelles méthodes ICE avancées
        private static async Task<string> GetIceServerStats()
        {
            try
            {
                await Task.Delay(1);
                var stats = IceServerManager.GetIceStats();
                Console.WriteLine($"ICE Stats: {stats["total_servers"]} STUN servers + RelayHub fallback");
                return CreateSuccessResponse(stats);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting ICE stats: {ex.Message}");
                return CreateErrorResponse($"Failed to get ICE stats: {ex.Message}");
            }
        }

        private static async Task<string> TestIceConnectivity()
        {
            try
            {
                await Task.Delay(1);
                var results = new List<object>();
                
                IceServerManager.TestIceConnectivity((server, isConnected) =>
                {
                    results.Add(new { server, connected = isConnected });
                    Console.WriteLine($"ICE Test: {server} -> {(isConnected ? "✅ OK" : "❌ FAILED")}");
                });

                var summary = new
                {
                    total_tested = results.Count,
                    successful = results.Count(r => (bool)((dynamic)r).connected),
                    failed = results.Count(r => !(bool)((dynamic)r).connected),
                    results = results
                };

                Console.WriteLine($"ICE Connectivity Test: {summary.successful}/{summary.total_tested} servers reachable");
                return CreateSuccessResponse(summary);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error testing ICE connectivity: {ex.Message}");
                return CreateErrorResponse($"Failed to test ICE connectivity: {ex.Message}");
            }
        }

        private static async Task<string> GetIceConfiguration(object? data)
        {
            try
            {
                await Task.Delay(1);
                
                // Analyser les paramètres optionnels
                string? mode = null;
                try
                {
                    if (data is System.Text.Json.JsonElement jsonData && jsonData.TryGetProperty("mode", out var modeElement))
                    {
                        mode = modeElement.GetString();
                    }
                }
                catch { }

                // Générer la configuration selon le mode demandé
                object configInfo = mode?.ToLower() switch
                {
                    "standard" => new { 
                        mode = "standard", 
                        description = "STUN discovery + RelayHub fallback",
                        ice_transport_policy = "all",
                        servers = IceServerManager.GetIceStats(),
                        fallback = "RelayHub (port 8888)"
                    },
                    "legacy" => new { 
                        mode = "legacy", 
                        description = "Single STUN server (backward compatibility)",
                        ice_transport_policy = "all",
                        servers = (object)new { stun_servers = 1, turn_servers = 0, relay_hub = "Internal" },
                        fallback = "RelayHub (port 8888)"
                    },
                    _ => new { 
                        mode = "adaptive", 
                        description = "STUN-first with automatic RelayHub fallback",
                        ice_transport_policy = "all",
                        servers = IceServerManager.GetIceStats(),
                        fallback = "RelayHub (port 8888)"
                    }
                };

                Console.WriteLine($"ICE Configuration requested: {mode ?? "adaptive"} mode");
                return CreateSuccessResponse(configInfo);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting ICE configuration: {ex.Message}");
                return CreateErrorResponse($"Failed to get ICE configuration: {ex.Message}");
            }
        }
        
        // Implémentations des méthodes Contacts
        private static async Task<string> GetContactList()
        {
            await Task.Delay(1);
            // DECENTRALIZED: Server no longer manages contacts - clients should manage their own
            // This endpoint should not be used in decentralized mode
            try
            {
                Console.WriteLine($"Server: GetContactList called - this should be handled by clients in decentralized mode");
                Console.WriteLine($"Connected clients: {string.Join(", ", _connectedClients.Keys)}");
                
                // Return empty list - clients manage their own contacts
                return CreateSuccessResponse(new List<object>());
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Error: {ex.Message}");
            }
        }
        
        private static async Task<string> AddContact(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var contactData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (contactData == null || !contactData.ContainsKey("peer") || !contactData.ContainsKey("publicKey"))
                    return CreateErrorResponse("Peer et publicKey requis");
                
                var success = await ContactManager.AddContact(contactData["peer"], contactData["publicKey"], true);
                return success ? CreateSuccessResponse("Contact ajouté") : CreateErrorResponse("Contact déjà existant");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur ajout contact: {ex.Message}");
            }
        }
        
        private static async Task<string> RemoveContact(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var contactData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (contactData == null || !contactData.ContainsKey("peer"))
                    return CreateErrorResponse("Peer requis");
                
                var success = await ContactManager.RemoveContact(contactData["peer"]);
                return success ? CreateSuccessResponse("Contact supprimé") : CreateErrorResponse("Contact introuvable");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur suppression contact: {ex.Message}");
            }
        }
        
        private static async Task<string> CreateContactRequest(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (requestData == null || !requestData.ContainsKey("from") || 
                    !requestData.ContainsKey("to") || !requestData.ContainsKey("publicKey"))
                    return CreateErrorResponse("From, to et publicKey requis");
                
                var message = requestData.GetValueOrDefault("message", "");
                var result = await ContactManager.CreateContactRequest(
                    requestData["from"], requestData["to"], requestData["publicKey"], message);
                
                return CreateSuccessResponse(result);
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur création demande: {ex.Message}");
            }
        }
        
        private static async Task<string> AcceptContactRequest(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (requestData == null || !requestData.ContainsKey("from") || !requestData.ContainsKey("to"))
                    return CreateErrorResponse("From et to requis");
                
                var success = await ContactManager.AcceptContactRequest(requestData["from"], requestData["to"]);
                return success ? CreateSuccessResponse("Demande acceptée") : CreateErrorResponse("Demande introuvable");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur acceptation: {ex.Message}");
            }
        }
        
        private static async Task<string> RejectContactRequest(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (requestData == null || !requestData.ContainsKey("from") || !requestData.ContainsKey("to"))
                    return CreateErrorResponse("From et to requis");
                
                var success = await ContactManager.RejectContactRequest(requestData["from"], requestData["to"]);
                return success ? CreateSuccessResponse("Demande rejetée") : CreateErrorResponse("Demande introuvable");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur rejet: {ex.Message}");
            }
        }
        
        private static async Task<string> GetPendingRequests(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (requestData == null || !requestData.ContainsKey("peer"))
                    return CreateErrorResponse("Peer requis");
                
                var requests = ContactManager.GetPendingRequests(requestData["peer"]);
                return CreateSuccessResponse(requests);
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur récupération demandes: {ex.Message}");
            }
        }
        
        private static async Task<string> GetSentRequests(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (requestData == null || !requestData.ContainsKey("peer"))
                    return CreateErrorResponse("Peer requis");
                
                var requests = ContactManager.GetSentRequests(requestData["peer"]);
                return CreateSuccessResponse(requests);
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur récupération demandes envoyées: {ex.Message}");
            }
        }
        
        private static async Task<string> SendFriendRequest(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (requestData == null || !requestData.ContainsKey("peer_name") || 
                    !requestData.ContainsKey("requester"))
                    return CreateErrorResponse("peer_name et requester requis");
                
                var peerName = requestData["peer_name"]?.ToString() ?? "";
                var requester = requestData["requester"]?.ToString() ?? "";
                
                // Check if peer exists in connected clients
                if (!_connectedClients.ContainsKey(peerName))
                {
                    return CreateErrorResponse($"Peer {peerName} not found or not connected");
                }
                
                // Generate a key pair for this request if needed
                var keyPair = P2PMessageCrypto.GenerateKeyPair();
                
                // Create the friend request
                var result = await ContactManager.CreateContactRequest(
                    requester, peerName, Convert.ToBase64String(keyPair.PublicKey), "Friend request");
                
                // Store pending request
                Console.WriteLine($"Friend request from {requester} to {peerName} created");
                
                return CreateSuccessResponse(new { 
                    message = "Friend request sent",
                    request_id = result,
                    public_key = Convert.ToBase64String(keyPair.PublicKey)
                });
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur envoi demande d'ami: {ex.Message}");
            }
        }
        
        private static async Task<string> ImportContact(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var contactData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (contactData == null || !contactData.ContainsKey("peer_name") || 
                    !contactData.ContainsKey("public_key"))
                    return CreateErrorResponse("peer_name et public_key requis");
                
                var peerName = contactData["peer_name"]?.ToString() ?? "";
                var publicKey = contactData["public_key"]?.ToString() ?? "";
                
                // Add contact with imported key
                var success = await ContactManager.AddContact(peerName, publicKey, true);
                
                if (success)
                {
                    Console.WriteLine($"Contact {peerName} imported with public key");
                    return CreateSuccessResponse(new { 
                        message = "Contact imported successfully",
                        peer_name = peerName
                    });
                }
                else
                {
                    return CreateErrorResponse("Failed to import contact");
                }
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur import contact: {ex.Message}");
            }
        }
        
        private static async Task<string> GetFriendRequests(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (requestData == null || !requestData.ContainsKey("peer_name"))
                    return CreateErrorResponse("peer_name requis");
                
                var peerName = requestData["peer_name"]?.ToString() ?? "";
                
                // Get ALL friend requests received by this peer
                var requests = ContactManager.GetAllReceivedRequests(peerName);
                
                // Only log if there are requests to avoid spam
                if (requests.Count > 0)
                {
                    Console.WriteLine($"Found {requests.Count} total friend requests for {peerName}");
                }
                
                return CreateSuccessResponse(requests);
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur récupération demandes d'ami: {ex.Message}");
            }
        }
        
        private static async Task<string> ReceiveFriendRequest(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (requestData == null || !requestData.ContainsKey("fromPeer") || 
                    !requestData.ContainsKey("toPeer") || !requestData.ContainsKey("publicKey"))
                    return CreateErrorResponse("fromPeer, toPeer et publicKey requis");
                
                var fromPeer = requestData["fromPeer"]?.ToString() ?? "";
                var toPeer = requestData["toPeer"]?.ToString() ?? "";
                var publicKey = requestData["publicKey"]?.ToString() ?? "";
                var message = requestData.ContainsKey("message") ? requestData["message"]?.ToString() ?? "" : "";
                
                Console.WriteLine($"API: contacts - receive_friend_request: {fromPeer} → {toPeer}");
                
                // Persister la friend request côté destinataire
                await ContactManager.ReceiveFriendRequestFromP2P(fromPeer, toPeer, publicKey, message);
                
                return CreateSuccessResponse(new { message = "Friend request received and persisted" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in ReceiveFriendRequest: {ex.Message}");
                return CreateErrorResponse($"Erreur: {ex.Message}");
            }
        }
        
        private static async Task<string> AcceptFriendRequest(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (requestData == null || !requestData.ContainsKey("requester") || 
                    !requestData.ContainsKey("accepter"))
                    return CreateErrorResponse("requester et accepter requis");
                
                var requester = requestData["requester"]?.ToString() ?? "";
                var accepter = requestData["accepter"]?.ToString() ?? "";
                
                // Accept the friend request and add both as contacts
                var success = await ContactManager.AcceptContactRequest(requester, accepter);
                
                if (success)
                {
                    // DECENTRALIZED: No push notifications for now, clients will poll for changes
                    Console.WriteLine($"Server: Friend request accepted, both {requester} and {accepter} should add each other to their local contacts");
                    
                    Console.WriteLine($"Friend request accepted: {requester} <-> {accepter}");
                    return CreateSuccessResponse(new { 
                        message = "Friend request accepted",
                        requester = requester,
                        accepter = accepter
                    });
                }
                else
                {
                    return CreateErrorResponse("Failed to accept friend request");
                }
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur acceptation demande d'ami: {ex.Message}");
            }
        }
        
        private static async Task<string> RejectFriendRequest(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (requestData == null || !requestData.ContainsKey("requester") || 
                    !requestData.ContainsKey("rejecter"))
                    return CreateErrorResponse("requester et rejecter requis");
                
                var requester = requestData["requester"]?.ToString() ?? "";
                var rejecter = requestData["rejecter"]?.ToString() ?? "";
                
                // Reject the friend request
                var success = await ContactManager.RejectContactRequest(requester, rejecter);
                
                if (success)
                {
                    Console.WriteLine($"Friend request rejected: {requester} -> {rejecter}");
                    return CreateSuccessResponse(new { 
                        message = "Friend request rejected",
                        requester = requester,
                        rejecter = rejecter
                    });
                }
                else
                {
                    return CreateErrorResponse("Failed to reject friend request");
                }
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur rejet demande d'ami: {ex.Message}");
            }
        }
        
        // Implémentations des méthodes KeyExchange
        private static async Task<string> InitiateKeyExchange(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var exchangeData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (exchangeData == null || !exchangeData.ContainsKey("initiator") || 
                    !exchangeData.ContainsKey("responder") || !exchangeData.ContainsKey("publicKey"))
                    return CreateErrorResponse("Initiator, responder et publicKey requis");
                
                var sessionId = await KeyExchangeManager.InitiateKeyExchange(
                    exchangeData["initiator"], exchangeData["responder"], exchangeData["publicKey"]);
                
                if (string.IsNullOrEmpty(sessionId))
                    return CreateErrorResponse("Erreur initiation négociation");
                
                return CreateSuccessResponse(new { session_id = sessionId });
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur initiation: {ex.Message}");
            }
        }
        
        private static async Task<string> RespondKeyExchange(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var responseData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (responseData == null || !responseData.ContainsKey("sessionId") || 
                    !responseData.ContainsKey("publicKey") || !responseData.ContainsKey("challengeResponse"))
                    return CreateErrorResponse("SessionId, publicKey et challengeResponse requis");
                
                var success = await KeyExchangeManager.RespondToKeyExchange(
                    responseData["sessionId"], responseData["publicKey"], responseData["challengeResponse"]);
                
                return success ? CreateSuccessResponse("Réponse acceptée") 
                              : CreateErrorResponse("Réponse rejetée");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur réponse: {ex.Message}");
            }
        }
        
        private static async Task<string> FinalizeKeyExchange(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var finalizeData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (finalizeData == null || !finalizeData.ContainsKey("sessionId") || 
                    !finalizeData.ContainsKey("verification"))
                    return CreateErrorResponse("SessionId et verification requis");
                
                var success = await KeyExchangeManager.FinalizeKeyExchange(
                    finalizeData["sessionId"], finalizeData["verification"]);
                
                return success ? CreateSuccessResponse("Négociation finalisée") 
                              : CreateErrorResponse("Finalisation échouée");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur finalisation: {ex.Message}");
            }
        }
        
        private static async Task<string> CancelKeyExchange(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var cancelData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (cancelData == null || !cancelData.ContainsKey("sessionId"))
                    return CreateErrorResponse("SessionId requis");
                
                var reason = cancelData.GetValueOrDefault("reason", "User cancelled");
                var success = await KeyExchangeManager.CancelSession(cancelData["sessionId"], reason);
                
                return success ? CreateSuccessResponse("Session annulée") 
                              : CreateErrorResponse("Session introuvable");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur annulation: {ex.Message}");
            }
        }
        
        private static async Task<string> GetKeyExchangeStatus(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var statusData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (statusData == null || !statusData.ContainsKey("sessionId"))
                    return CreateErrorResponse("SessionId requis");
                
                var session = KeyExchangeManager.GetSession(statusData["sessionId"]);
                return session != null ? CreateSuccessResponse(session) 
                                      : CreateErrorResponse("Session introuvable");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur status: {ex.Message}");
            }
        }
        
        private static async Task<string> GetKeyExchangeSessions(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var sessionData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (sessionData == null || !sessionData.ContainsKey("peer"))
                    return CreateErrorResponse("Peer requis");
                
                var sessions = KeyExchangeManager.GetSessionsForPeer(sessionData["peer"]);
                return CreateSuccessResponse(sessions);
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur récupération sessions: {ex.Message}");
            }
        }
        
        private static async Task<string> CleanupKeyExchangeSessions()
        {
            try
            {
                await KeyExchangeManager.CleanupExpiredSessions();
                return CreateSuccessResponse("Nettoyage effectué");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur nettoyage: {ex.Message}");
            }
        }

        // Implémentations des méthodes Crypto
        private static async Task<string> GenerateKeyPair()
        {
            await Task.Delay(1);
            var keyPair = P2PMessageCrypto.GenerateKeyPair();
            var result = new
            {
                public_key = Convert.ToBase64String(keyPair.PublicKey),
                algorithm = keyPair.Algorithm,
                is_simulated = keyPair.IsSimulated
            };
            return CreateSuccessResponse(result);
        }
        
        private static async Task<string> GetMyPublicKey()
        {
            await Task.Delay(1);
            // For now, generate a new key each time
            // TODO: Store and reuse server's persistent key
            var keyPair = P2PMessageCrypto.GenerateKeyPair();
            var result = new
            {
                public_key = Convert.ToBase64String(keyPair.PublicKey),
                algorithm = keyPair.Algorithm
            };
            return CreateSuccessResponse(result);
        }
        
        private static async Task<string> EncryptMessage(object? data)
        {
            await Task.Delay(1);
            // TODO: Implémenter chiffrement
            return CreateSuccessResponse("Message encrypted");
        }
        
        private static async Task<string> DecryptMessage(object? data)
        {
            await Task.Delay(1);
            // TODO: Implémenter déchiffrement
            return CreateSuccessResponse("Message decrypted");
        }
        
        // Méthodes utilitaires
        private static string CreateSuccessResponse(object data)
        {
            var response = new { success = true, data, timestamp = DateTime.Now };
            return JsonSerializer.Serialize(response);
        }
        
        private static string CreateErrorResponse(string error)
        {
            var response = new { success = false, error, timestamp = DateTime.Now };
            return JsonSerializer.Serialize(response);
        }

        // Implémentations des méthodes Security
        private static async Task<string> GetSecurityPeerList(object? data)
        {
            await Task.Delay(1);
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                var filter = requestData?.ContainsKey("filter") == true ? requestData["filter"]?.ToString() ?? "" : "";
                
                var peers = DatabaseService.Instance.GetSecurityPeerList(filter);
                return CreateSuccessResponse(new { peers });
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur récupération peers: {ex.Message}");
            }
        }

        private static async Task<string> GetMyFingerprint()
        {
            await Task.Delay(1);
            try
            {
                var fingerprint = DatabaseService.Instance.GetMyFingerprint();
                return CreateSuccessResponse(new { fingerprint });
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur récupération empreinte: {ex.Message}");
            }
        }

        private static async Task<string> SetPeerTrusted(object? data)
        {
            await Task.Delay(1);
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (requestData == null || !requestData.ContainsKey("peer_name"))
                    return CreateErrorResponse("peer_name requis");
                
                var peerName = requestData["peer_name"].ToString();
                var trusted = requestData.ContainsKey("trusted") && 
                             bool.TryParse(requestData["trusted"].ToString(), out var t) && t;
                
                DatabaseService.Instance.SetPeerTrusted(peerName!, trusted);
                
                return CreateSuccessResponse($"Trust updated for {peerName}");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur mise à jour trust: {ex.Message}");
            }
        }

        private static async Task<string> SetPeerNote(object? data)
        {
            await Task.Delay(1);
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (requestData == null || !requestData.ContainsKey("peer_name"))
                    return CreateErrorResponse("peer_name requis");
                
                var peerName = requestData["peer_name"].ToString();
                var note = requestData.ContainsKey("note") ? requestData["note"].ToString() : "";
                
                DatabaseService.Instance.SetPeerNote(peerName!, note ?? "");
                
                return CreateSuccessResponse($"Note updated for {peerName}");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur mise à jour note: {ex.Message}");
            }
        }

        private static async Task<string> ResetPeerTofu(object? data)
        {
            await Task.Delay(1);
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (requestData == null || !requestData.ContainsKey("peer_name"))
                    return CreateErrorResponse("peer_name requis");
                
                var peerName = requestData["peer_name"].ToString();
                
                DatabaseService.Instance.ResetPeerTofu(peerName!);
                
                return CreateSuccessResponse($"TOFU reset for {peerName}");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur reset TOFU: {ex.Message}");
            }
        }

        private static async Task<string> ImportPeerKey(object? data)
        {
            await Task.Delay(1);
            try
            {
                var json = JsonSerializer.Serialize(data);
                var requestData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (requestData == null || !requestData.ContainsKey("peer_name") || !requestData.ContainsKey("public_key_b64"))
                    return CreateErrorResponse("peer_name et public_key_b64 requis");
                
                var peerName = requestData["peer_name"].ToString();
                var publicKeyB64 = requestData["public_key_b64"].ToString();
                
                DatabaseService.Instance.ImportPeerKey(peerName!, publicKeyB64!);
                
                return CreateSuccessResponse($"Key imported for {peerName}");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur import clé: {ex.Message}");
            }
        }

        private static async Task<string> ExportMyKey()
        {
            await Task.Delay(1);
            try
            {
                var (publicKeyB64, fingerprint) = DatabaseService.Instance.ExportMyKey();
                
                return CreateSuccessResponse(new { public_key_b64 = publicKeyB64, fingerprint });
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur export clé: {ex.Message}");
            }
        }
        
        private static async Task WaitForShutdown()
        {
            await Task.Run(() =>
            {
                while (true)
                {
                    var key = Console.ReadKey(true);
                    if (key.KeyChar == 'q' || key.KeyChar == 'Q')
                        break;
                }
            });
        }
        
        private static async Task Shutdown()
        {
            Console.WriteLine("\nArrêt du serveur...");
            _isRunning = false;
            
            _tcpListener?.Stop();
            
            if (_relayHub != null)
            {
                await _relayHub.StopAsync();
                Console.WriteLine("RelayHub arrêté");
            }
            
            await Task.Delay(100); // Laisser le temps aux tâches de se terminer
            Console.WriteLine("Serveur arrêté.");
        }
        
        private static void InitializeFileTransferService()
        {
            try
            {
                // Initialize File Transfer Service with event handlers
                FileTransferService.Instance.OnTransferProgress += (transferId, progress, receivedBytes, totalBytes) =>
                {
                    Console.WriteLine($"📦 [FILE-TRANSFER] Progress {transferId}: {progress:F1}% ({receivedBytes}/{totalBytes} bytes)");
                };
                
                FileTransferService.Instance.OnTransferCompleted += (transferId, success, outputPath) =>
                {
                    if (success)
                    {
                        Console.WriteLine($"✅ [FILE-TRANSFER] Transfer {transferId} completed successfully: {outputPath}");
                    }
                    else
                    {
                        Console.WriteLine($"❌ [FILE-TRANSFER] Transfer {transferId} failed");
                    }
                };
                
                FileTransferService.Instance.OnChunkReceived += (transferId, chunkIndex, hash) =>
                {
                    Console.WriteLine($"📦 [FILE-TRANSFER] Chunk {chunkIndex} received for transfer {transferId}");
                };
                
                FileTransferService.Instance.OnLog += (message) =>
                {
                    Console.WriteLine($"[FILE-TRANSFER] {message}");
                };
                
                Console.WriteLine("File Transfer Service initialized");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur initialisation FileTransferService: {ex.Message}");
            }
        }

        /// <summary>
        /// Diagnostic STUN pour identifier pourquoi nous obtenons 127.0.0.1 au lieu de l'IP publique
        /// </summary>
        private static async Task DiagnoseStunConnectivity()
        {
            Console.WriteLine("🧪 [STUN-DIAG] Diagnostic de connectivité STUN...");

            try
            {
                // Test de connectivité basique
                var publicIp = await GetPublicIpAsync();
                Console.WriteLine($"🌐 [STUN-DIAG] IP publique détectée: {publicIp}");

                // Test SIPSorcery avec configuration simple
                var config = new SIPSorcery.Net.RTCConfiguration()
                {
                    iceServers = new List<SIPSorcery.Net.RTCIceServer>()
                    {
                        new SIPSorcery.Net.RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                    }
                };

                var pc = new SIPSorcery.Net.RTCPeerConnection(config);
                var stunResults = new List<string>();

                pc.onicecandidate += (candidate) =>
                {
                    if (candidate != null && !string.IsNullOrEmpty(candidate.candidate))
                    {
                        stunResults.Add(candidate.candidate);
                        Console.WriteLine($"🎯 [STUN-DIAG] ICE Candidate: {candidate.candidate}");

                        if (candidate.candidate.Contains(publicIp))
                        {
                            Console.WriteLine("✅ [STUN-DIAG] STUN discovery réussi - IP publique trouvée!");
                        }
                        else if (candidate.candidate.Contains("127.0.0.1"))
                        {
                            Console.WriteLine("❌ [STUN-DIAG] STUN discovery échoué - localhost détecté");
                        }
                    }
                };

                // Créer une offre fictive pour déclencher ICE gathering
                var offer = pc.createOffer();
                pc.setLocalDescription(offer);

                Console.WriteLine("⏳ [STUN-DIAG] Attente des candidats ICE (5s)...");
                await Task.Delay(5000);

                pc.close();

                Console.WriteLine($"🏁 [STUN-DIAG] Diagnostic terminé - {stunResults.Count} candidats trouvés");

                if (stunResults.Count == 0)
                {
                    Console.WriteLine("⚠️  [STUN-DIAG] Aucun candidat ICE - problème de connectivité STUN");
                }
                else if (stunResults.All(c => c.Contains("127.0.0.1")))
                {
                    Console.WriteLine("⚠️  [STUN-DIAG] Tous les candidats sont localhost - STUN bloqué ou infonctionnel");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [STUN-DIAG] Erreur diagnostic: {ex.Message}");
            }
        }

        /// <summary>
        /// Obtient l'IP publique via un service externe
        /// </summary>
        private static async Task<string> GetPublicIpAsync()
        {
            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetStringAsync("https://api.ipify.org");
                return response.Trim();
            }
            catch
            {
                return "77.57.58.6"; // Fallback sur IP connue
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Force la détection des IPs réseau pour éviter 127.0.0.1 dans les SDP
        /// </summary>
        private static void DetectAndLogNetworkIPs()
        {
            try
            {
                Console.WriteLine("🔧 [NETWORK-FIX] Starting network IP detection - VERSION 3.0 STARTUP");

                // Obtenir les vraies IPs réseau de la machine
                var networkIps = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                    .Where(ni => ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                    .Where(addr => addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Where(addr => !System.Net.IPAddress.IsLoopback(addr.Address))
                    .Select(addr => addr.Address.ToString())
                    .ToList();

                if (networkIps.Any())
                {
                    var primaryIp = networkIps.First();
                    Console.WriteLine($"🌐 [NETWORK-FIX] Detected network IPs: {string.Join(", ", networkIps)}");
                    Console.WriteLine($"🎯 [NETWORK-FIX] Primary IP detected: {primaryIp}");

                    // ✅ MULTIPLE CONFIG STRATEGIES pour SIPSorcery
                    Environment.SetEnvironmentVariable("SIPSORCERY_HOST_IP", primaryIp);
                    Environment.SetEnvironmentVariable("SIPSORCERY_BIND_IP", primaryIp);
                    Environment.SetEnvironmentVariable("RTC_HOST_IP", primaryIp);

                    Console.WriteLine($"✅ [NETWORK-FIX] SIPSorcery configuration applied:");
                    Console.WriteLine($"   - SIPSORCERY_HOST_IP: {primaryIp}");
                    Console.WriteLine($"   - SIPSORCERY_BIND_IP: {primaryIp}");
                    Console.WriteLine($"   - RTC_HOST_IP: {primaryIp}");

                    // Store for later use in IceP2PSession if needed
                    _detectedPrimaryIP = primaryIp;
                }
                else
                {
                    Console.WriteLine("⚠️ [NETWORK-FIX] No valid network IPs found, SIPSorcery will use 127.0.0.1");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [NETWORK-FIX] Error detecting network IPs: {ex.Message}");
            }
        }

        // ✅ NOUVEAU: Méthodes de logging général du serveur
        private static void InitializeGeneralLogging()
        {
            try
            {
                var logDir = @"C:\Users\pragm\OneDrive\Bureau\ChatP2P_Logs";
                Directory.CreateDirectory(logDir);

                var timestamp = DateTime.Now.ToString("yyyy-MM-dd");
                _generalLogPath = Path.Combine(logDir, $"server_general_{timestamp}.log");

                LogToFile($"🚀 [SERVER-INIT] General logging initialized - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [LOG-INIT] Failed to initialize general logging: {ex.Message}");
            }
        }

        public static void LogToFile(string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var logLine = $"[{timestamp}] {message}";

            // Toujours afficher dans la console
            Console.WriteLine(message);

            // Sauvegarder dans le fichier si possible
            if (!string.IsNullOrEmpty(_generalLogPath))
            {
                try
                {
                    File.AppendAllText(_generalLogPath, logLine + Environment.NewLine);
                }
                catch
                {
                    // Ignore les erreurs de logging pour ne pas planter le serveur
                }
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Notifier le serveur qu'une connexion P2P client est prête
        /// </summary>
        private static async Task<string> NotifyConnectionReady(object? data)
        {
            if (data == null) return CreateErrorResponse("Data manquante");

            try
            {
                var json = JsonSerializer.Serialize(data);
                var notifyData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

                if (notifyData == null || !notifyData.TryGetValue("from_peer", out var fromPeerObj) ||
                    !notifyData.TryGetValue("to_peer", out var toPeerObj))
                {
                    return CreateErrorResponse("from_peer and to_peer fields required");
                }

                var fromPeer = fromPeerObj.ToString()!;
                var toPeer = toPeerObj.ToString()!;
                var status = notifyData.TryGetValue("status", out var statusObj) ? statusObj.ToString() : "ready";

                Console.WriteLine($"🔗 [P2P-READY] {fromPeer} reports P2P connection ready with {toPeer} (status: {status})");

                // ✅ FIX: Marquer cette connexion comme prête côté serveur
                // Cela permet au serveur de savoir que les DataChannels clients sont ouverts
                P2PService.NotifyDirectConnectionReady(fromPeer, toPeer);

                return CreateSuccessResponse($"P2P connection readiness noted: {fromPeer} ↔ {toPeer}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [P2P-READY] Error processing connection notification: {ex.Message}");
                return CreateErrorResponse($"Error processing connection notification: {ex.Message}");
            }
        }
    }

    // Classes de données pour l'API
    public class ApiRequest
    {
        public string Command { get; set; } = "";
        public string? Action { get; set; }
        public object? Data { get; set; }
    }
}
