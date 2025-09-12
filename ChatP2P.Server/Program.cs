using System;
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
        private static bool _isRunning = false;
        private static bool _p2pInitialized = false;
        private static readonly Dictionary<string, DateTime> _connectedClients = new();
        
        public static async Task Main(string[] args)
        {
            Console.WriteLine("=== ChatP2P.Server Console v1.0 ===");
            Console.WriteLine("Démarrage du serveur local...");
            
            try
            {
                // Initialisation du P2P Manager
                InitializeP2P();
                
                // Démarrage du serveur TCP sur localhost
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
                
                Console.WriteLine($"API: {apiRequest.Command} - {apiRequest.Action}");
                
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
            return request.Action?.ToLower() switch
            {
                "start" => await StartP2PNetwork(request.Data),
                "stop" => await StopP2PNetwork(),
                "peers" => await GetConnectedPeers(),
                "connect" => await StartP2PConnection(request.Data),
                "send_message" => await SendP2PMessage(request.Data),
                "send_file" => await SendP2PFile(request.Data),
                "handle_offer" => await HandleP2POffer(request.Data),
                "handle_answer" => await HandleP2PAnswer(request.Data),
                "handle_candidate" => await HandleP2PCandidate(request.Data),
                "connection_status" => await GetP2PConnectionStatus(request.Data),
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
        private static void InitializeP2P()
        {
            try
            {
                // Initialize P2P Service with signaling function
                P2PService.Initialize("ChatP2P.Server", async (peer, signal) =>
                {
                    Console.WriteLine($"P2P Signal to {peer}: {signal}");
                    // TODO: Route signaling through relay/hub mechanism
                    await Task.Delay(1);
                });
                
                _p2pInitialized = true;
                Console.WriteLine("P2P Service initialized");
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
            if (data == null) return CreateErrorResponse("Message data missing");
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var messageData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (!messageData!.TryGetValue("peer", out var peer) || 
                    !messageData.TryGetValue("message", out var message))
                {
                    return CreateErrorResponse("Peer and message fields required");
                }
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                var success = P2PService.SendTextMessage(peer, message);
                if (success)
                {
                    Console.WriteLine($"Message sent to {peer}: {message}");
                    return CreateSuccessResponse("Message sent successfully");
                }
                else
                {
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
                    !fileData.TryGetValue("data", out var dataObj))
                {
                    return CreateErrorResponse("Peer and data fields required");
                }
                
                var peer = peerObj.ToString()!;
                byte[] binaryData;
                
                if (dataObj is string base64Data)
                {
                    binaryData = Convert.FromBase64String(base64Data);
                }
                else
                {
                    return CreateErrorResponse("Data must be base64 encoded");
                }
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                var success = P2PService.SendBinaryData(peer, binaryData);
                if (success)
                {
                    Console.WriteLine($"Binary data sent to {peer}: {binaryData.Length} bytes");
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
                
                if (!_p2pInitialized)
                {
                    return CreateErrorResponse("P2P service not initialized");
                }
                
                var success = await P2PService.StartP2PConnection(peer);
                if (success)
                {
                    Console.WriteLine($"P2P connection initiated with {peer}");
                    return CreateSuccessResponse($"P2P connection started with {peer}");
                }
                else
                {
                    return CreateErrorResponse($"Failed to start P2P connection with {peer}");
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
        
        // Implémentations des méthodes Contacts
        private static async Task<string> GetContactList()
        {
            await Task.Delay(1);
            try
            {
                var trustedPeerNames = DatabaseService.Instance.GetTrustedContacts();
                var contacts = new List<object>();
                
                foreach (var peerName in trustedPeerNames)
                {
                    var contact = new
                    {
                        peer_name = peerName,
                        status = _connectedClients.ContainsKey(peerName) ? "Online" : "Offline",
                        verified = true, // They're trusted so considered verified
                        added_date = DateTime.Now.ToString("o")
                    };
                    contacts.Add(contact);
                }
                
                Console.WriteLine($"Returning {contacts.Count} trusted contacts");
                return CreateSuccessResponse(contacts);
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Error getting contacts: {ex.Message}");
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
                
                // Get pending friend requests for this peer
                var requests = ContactManager.GetPendingRequests(peerName);
                
                Console.WriteLine($"Found {requests.Count} pending friend requests for {peerName}");
                
                return CreateSuccessResponse(requests);
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Erreur récupération demandes d'ami: {ex.Message}");
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
            
            await Task.Delay(100); // Laisser le temps aux tâches de se terminer
            Console.WriteLine("Serveur arrêté.");
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
