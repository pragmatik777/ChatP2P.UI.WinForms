using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ChatP2P.Server
{
    /// <summary>
    /// 📹 Service de relay vidéo TCP pur (port 8894)
    /// Données binaires directes sans JSON/Base64 overhead
    /// </summary>
    public class VOIPVideoRelayService
    {
        private TcpListener? _tcpListener;
        private readonly ConcurrentDictionary<string, VideoConnection> _videoClients = new();
        private readonly ConcurrentDictionary<string, VideoSession> _videoSessions = new();
        private bool _isRunning = false;

        public event Action<string>? LogEvent;

        private class VideoConnection
        {
            public TcpClient TcpClient { get; set; } = null!;
            public NetworkStream Stream { get; set; } = null!;
            public string PeerName { get; set; } = "";
            public DateTime ConnectedAt { get; set; }
        }

        private class VideoSession
        {
            public string Caller { get; set; } = "";
            public string Callee { get; set; } = "";
            public DateTime StartedAt { get; set; }
            public long BytesRelayed { get; set; } = 0;
        }

        public async Task StartAsync()
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, 8894);
                _tcpListener.Start();
                _isRunning = true;

                LogEvent?.Invoke("[VIDEO-RELAY] 📹 Pure Video Relay started on port 8894");
                LogEvent?.Invoke("[VIDEO-RELAY] 🚀 Ready for binary video streaming (no JSON overhead)");

                // Accepter connexions en parallèle
                _ = Task.Run(AcceptClientsAsync);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VIDEO-RELAY] ❌ Error starting video relay: {ex.Message}");
            }
        }

        private async Task AcceptClientsAsync()
        {
            while (_isRunning)
            {
                try
                {
                    var tcpClient = await _tcpListener!.AcceptTcpClientAsync();
                    LogEvent?.Invoke($"[VIDEO-RELAY] 📹 New video client connected from {tcpClient.Client.RemoteEndPoint}");

                    // Traiter chaque client en parallèle
                    _ = Task.Run(() => HandleVideoClientAsync(tcpClient));
                }
                catch (Exception ex) when (_isRunning)
                {
                    LogEvent?.Invoke($"[VIDEO-RELAY] ❌ Error accepting video client: {ex.Message}");
                }
            }
        }

        private async Task HandleVideoClientAsync(TcpClient tcpClient)
        {
            string peerName = "";
            VideoConnection? connection = null;

            try
            {
                var stream = tcpClient.GetStream();
                var buffer = new byte[1048576]; // 1MB buffer pour vidéo (frames plus grosses)

                // Lire premier message d'identification (format: "PEER:PeerName")
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    var identityMessage = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    if (identityMessage.StartsWith("PEER:"))
                    {
                        peerName = identityMessage.Substring(5);
                        connection = new VideoConnection
                        {
                            TcpClient = tcpClient,
                            Stream = stream,
                            PeerName = peerName,
                            ConnectedAt = DateTime.UtcNow
                        };

                        _videoClients.TryAdd(peerName, connection);
                        LogEvent?.Invoke($"[VIDEO-RELAY] ✅ Video client {peerName} registered");

                        // Confirmer connexion avec format attendu par le client
                        var confirmBytes = Encoding.UTF8.GetBytes($"CONNECTED:{peerName}");
                        await stream.WriteAsync(confirmBytes, 0, confirmBytes.Length);
                    }
                }

                // Boucle de traitement des données vidéo binaires pures
                while (_isRunning && tcpClient.Connected)
                {
                    bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        // Relayer directement les données vidéo binaires
                        await RelayPureVideoData(peerName, buffer, bytesRead);
                    }
                    else
                    {
                        break; // Connexion fermée
                    }
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VIDEO-RELAY] ❌ Error handling video client {peerName}: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(peerName))
                {
                    _videoClients.TryRemove(peerName, out _);
                    LogEvent?.Invoke($"[VIDEO-RELAY] 📴 Video client {peerName} disconnected");
                }
                tcpClient?.Close();
            }
        }

        private async Task RelayPureVideoData(string fromPeer, byte[] videoData, int length)
        {
            try
            {
                // Trouver destinataire via session active
                string targetPeer = FindVideoTarget(fromPeer);

                if (!string.IsNullOrEmpty(targetPeer) && _videoClients.TryGetValue(targetPeer, out var targetConnection))
                {
                    // 🚀 RELAY BINAIRE PUR - Pas de JSON/Base64 !
                    await targetConnection.Stream.WriteAsync(videoData, 0, length);
                    await targetConnection.Stream.FlushAsync();

                    // Mettre à jour statistiques
                    UpdateVideoSessionStats(fromPeer, targetPeer, length);

                    LogEvent?.Invoke($"[VIDEO-RELAY] 📹 Pure video: {fromPeer} → {targetPeer} ({length} bytes)");
                }
                else
                {
                    LogEvent?.Invoke($"[VIDEO-RELAY] ⚠️ No video target for {fromPeer}, dropping {length} bytes");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VIDEO-RELAY] ❌ Error relaying video from {fromPeer}: {ex.Message}");
            }
        }

        private string FindVideoTarget(string fromPeer)
        {
            // Chercher dans les sessions vidéo actives
            foreach (var session in _videoSessions.Values)
            {
                if (session.Caller == fromPeer)
                    return session.Callee;
                else if (session.Callee == fromPeer)
                    return session.Caller;
            }
            return "";
        }

        private void UpdateVideoSessionStats(string fromPeer, string targetPeer, int bytes)
        {
            var sessionId = $"{fromPeer}-{targetPeer}";
            var reverseSessionId = $"{targetPeer}-{fromPeer}";

            if (_videoSessions.TryGetValue(sessionId, out var session) ||
                _videoSessions.TryGetValue(reverseSessionId, out session))
            {
                session.BytesRelayed += bytes;
            }
        }

        /// <summary>
        /// Démarrer session vidéo entre deux peers
        /// </summary>
        public void StartVideoSession(string caller, string callee)
        {
            try
            {
                var sessionId = $"{caller}-{callee}";
                var session = new VideoSession
                {
                    Caller = caller,
                    Callee = callee,
                    StartedAt = DateTime.UtcNow
                };

                _videoSessions.TryAdd(sessionId, session);
                LogEvent?.Invoke($"[VIDEO-RELAY] 📹 Video session started: {caller} ↔ {callee}");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VIDEO-RELAY] ❌ Error starting video session: {ex.Message}");
            }
        }

        /// <summary>
        /// Arrêter session vidéo
        /// </summary>
        public void StopVideoSession(string caller, string callee)
        {
            try
            {
                var sessionId1 = $"{caller}-{callee}";
                var sessionId2 = $"{callee}-{caller}";

                if (_videoSessions.TryRemove(sessionId1, out var session) ||
                    _videoSessions.TryRemove(sessionId2, out session))
                {
                    LogEvent?.Invoke($"[VIDEO-RELAY] 🛑 Video session ended: {caller} ↔ {callee} ({session.BytesRelayed} bytes relayed)");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VIDEO-RELAY] ❌ Error stopping video session: {ex.Message}");
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _isRunning = false;
                _tcpListener?.Stop();

                // Fermer toutes les connexions
                foreach (var client in _videoClients.Values)
                {
                    client.TcpClient?.Close();
                }
                _videoClients.Clear();
                _videoSessions.Clear();

                LogEvent?.Invoke("[VIDEO-RELAY] 🛑 Pure Video Relay stopped");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VIDEO-RELAY] ❌ Error stopping video relay: {ex.Message}");
            }
        }

        /// <summary>
        /// Statistiques vidéo relay
        /// </summary>
        public string GetVideoStats()
        {
            return $"Video Clients: {_videoClients.Count}, Active Sessions: {_videoSessions.Count}";
        }
    }
}