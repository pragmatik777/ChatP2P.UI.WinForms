using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChatP2P.Server
{
    /// <summary>
    /// VOIP Relay Service - Port 8892
    /// Fallback relay pour audio/vidéo quand P2P WebRTC échoue
    /// </summary>
    public class VOIPRelayService
    {
        private TcpListener? _tcpListener;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
        private readonly ConcurrentDictionary<string, VOIPSession> _activeSessions = new();
        private bool _isRunning = false;

        public event Action<string>? LogEvent;

        private class ClientConnection
        {
            public string PeerName { get; set; } = "";
            public TcpClient TcpClient { get; set; }
            public NetworkStream Stream { get; set; }
            public DateTime ConnectedAt { get; set; }

            public ClientConnection(TcpClient client, NetworkStream stream)
            {
                TcpClient = client;
                Stream = stream;
                ConnectedAt = DateTime.Now;
            }
        }

        private class VOIPSession
        {
            public string Caller { get; set; } = "";
            public string Callee { get; set; } = "";
            public DateTime StartedAt { get; set; }
            public long AudioBytesRelayed { get; set; } = 0;
            public long VideoBytesRelayed { get; set; } = 0;
        }

        private class VOIPMessage
        {
            public string Type { get; set; } = "";
            public string From { get; set; } = "";
            public string To { get; set; } = "";
            public string Data { get; set; } = "";
            public DateTime Timestamp { get; set; }
        }

        public async Task StartAsync()
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, 8892);
                _tcpListener.Start();
                _isRunning = true;

                LogEvent?.Invoke("[VOIP-RELAY] 🎙️ VOIP Relay Server started on port 8892");
                LogEvent?.Invoke("[VOIP-RELAY] 📡 Ready for audio/video fallback connections");

                // Écouter les connexions entrantes
                _ = Task.Run(async () =>
                {
                    while (_isRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var tcpClient = await _tcpListener.AcceptTcpClientAsync();
                            _ = Task.Run(() => HandleClientAsync(tcpClient), _cancellationTokenSource.Token);
                        }
                        catch (ObjectDisposedException)
                        {
                            break; // Server stopped
                        }
                        catch (Exception ex)
                        {
                            LogEvent?.Invoke($"[VOIP-RELAY] ❌ Error accepting client: {ex.Message}");
                        }
                    }
                });

                // Stats périodiques
                _ = Task.Run(async () =>
                {
                    while (_isRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        await Task.Delay(30000, _cancellationTokenSource.Token); // 30 secondes
                        LogEvent?.Invoke($"[VOIP-RELAY] 📊 Connected clients: {_clients.Count}, Active sessions: {_activeSessions.Count}");
                    }
                });
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY] ❌ Failed to start VOIP relay server: {ex.Message}");
                throw;
            }
        }

        private async Task HandleClientAsync(TcpClient tcpClient)
        {
            string clientId = $"{tcpClient.Client.RemoteEndPoint}";
            string peerName = "";

            try
            {
                var stream = tcpClient.GetStream();
                var buffer = new byte[8192];

                LogEvent?.Invoke($"[VOIP-RELAY] 🔗 New VOIP client connected: {clientId}");

                while (tcpClient.Connected && _isRunning)
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _cancellationTokenSource.Token);
                    if (bytesRead == 0) break;

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    // Parse message VOIP
                    try
                    {
                        var voipMsg = JsonSerializer.Deserialize<VOIPMessage>(message);
                        if (voipMsg != null)
                        {
                            await ProcessVOIPMessage(voipMsg, stream, clientId);

                            // Mettre à jour le nom du peer si c'est la première fois
                            if (string.IsNullOrEmpty(peerName) && !string.IsNullOrEmpty(voipMsg.From))
                            {
                                peerName = voipMsg.From;
                                var connection = new ClientConnection(tcpClient, stream);
                                connection.PeerName = peerName;
                                _clients.TryAdd(peerName, connection);
                                LogEvent?.Invoke($"[VOIP-RELAY] ✅ Client {peerName} registered for VOIP relay");
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Probablement des données binaires audio/vidéo, les relayer directement
                        await RelayBinaryData(peerName, buffer, bytesRead);
                    }
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY] ❌ Error handling client {clientId}: {ex.Message}");
            }
            finally
            {
                tcpClient?.Close();
                if (!string.IsNullOrEmpty(peerName))
                {
                    _clients.TryRemove(peerName, out _);

                    // ✅ FIX: Nettoyer les sessions actives pour ce peer
                    CleanupSessionsForPeer(peerName);

                    LogEvent?.Invoke($"[VOIP-RELAY] 📴 Client {peerName} disconnected from VOIP relay");
                }
            }
        }

        private async Task ProcessVOIPMessage(VOIPMessage message, NetworkStream senderStream, string senderId)
        {
            try
            {
                switch (message.Type.ToLower())
                {
                    case "client_identity":
                        await HandleClientIdentity(message, senderStream, senderId);
                        break;

                    case "call_start":
                        await HandleCallStart(message);
                        break;

                    case "call_accept":
                        await HandleCallAccept(message);
                        break;

                    case "call_end":
                        await HandleCallEnd(message);
                        break;

                    case "audio_data":
                        await RelayAudioData(message);
                        break;

                    case "video_data":
                        await RelayVideoData(message);
                        break;

                    default:
                        LogEvent?.Invoke($"[VOIP-RELAY] ⚠️ Unknown VOIP message type: {message.Type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY] ❌ Error processing VOIP message: {ex.Message}");
            }
        }

        private async Task HandleClientIdentity(VOIPMessage message, NetworkStream senderStream, string clientId)
        {
            try
            {
                string peerName = message.From;
                if (!string.IsNullOrEmpty(peerName))
                {
                    // Créer la connection et l'enregistrer
                    var tcpClient = new TcpClient(); // Dummy pour créer ClientConnection
                    var connection = new ClientConnection(tcpClient, senderStream);
                    connection.PeerName = peerName;

                    _clients.TryAdd(peerName, connection);
                    LogEvent?.Invoke($"[VOIP-RELAY] ✅ Client {peerName} registered for VOIP relay via identity message");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-RELAY] ⚠️ Received client_identity with empty peerName");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY] ❌ Error handling client identity: {ex.Message}");
            }
        }

        private async Task HandleCallStart(VOIPMessage message)
        {
            var sessionId = $"{message.From}_{message.To}";
            var session = new VOIPSession
            {
                Caller = message.From,
                Callee = message.To,
                StartedAt = DateTime.Now
            };

            _activeSessions.TryAdd(sessionId, session);
            LogEvent?.Invoke($"[VOIP-RELAY] 📞 Call started: {message.From} → {message.To}");

            // ✅ NOUVEAU: Synchroniser avec le canal audio pur
            try
            {
                var audioRelay = ChatP2P.Server.Program.GetAudioRelay();
                audioRelay?.StartAudioSession(message.From, message.To);
                LogEvent?.Invoke($"[VOIP-RELAY] 🎵 Pure audio session synchronized: {message.From} ↔ {message.To}");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY] ⚠️ Failed to sync audio session: {ex.Message}");
            }

            // ✅ FIX CRITIQUE: Vérifier si le destinataire est connecté au relay
            if (_clients.ContainsKey(message.To))
            {
                // Destinataire connecté - relayer normalement
                await RelayMessageToPeer(message.To, message);
                LogEvent?.Invoke($"[VOIP-RELAY] ✅ Call invite relayed to {message.To} via VOIP relay");
            }
            else
            {
                // Destinataire pas connecté - envoyer notification d'appel entrant
                LogEvent?.Invoke($"[VOIP-RELAY] ⚠️ Peer {message.To} not connected to VOIP relay");
                LogEvent?.Invoke($"[VOIP-RELAY] 🔔 Sending incoming call notification to {message.To}");

                // Créer un message de notification d'appel entrant
                var callNotification = new VOIPMessage
                {
                    Type = "incoming_call_notification",
                    From = message.From,
                    To = message.To,
                    Data = message.Data,
                    Timestamp = DateTime.UtcNow
                };

                // Essayer de notifier via d'autres canaux si disponible
                // Pour l'instant, on loggue juste - VM2 devrait se connecter automatiquement au démarrage
                LogEvent?.Invoke($"[VOIP-RELAY] 📡 Waiting for {message.To} to connect to receive call from {message.From}");
            }
        }

        private async Task HandleCallAccept(VOIPMessage message)
        {
            LogEvent?.Invoke($"[VOIP-RELAY] ✅ Call accepted: {message.From} ← {message.To}");
            await RelayMessageToPeer(message.To, message);
        }

        private async Task HandleCallEnd(VOIPMessage message)
        {
            var sessionId1 = $"{message.From}_{message.To}";
            var sessionId2 = $"{message.To}_{message.From}";

            if (_activeSessions.TryRemove(sessionId1, out var session) ||
                _activeSessions.TryRemove(sessionId2, out session))
            {
                var duration = DateTime.Now - session.StartedAt;
                LogEvent?.Invoke($"[VOIP-RELAY] 📴 Call ended: {session.Caller} ↔ {session.Callee} " +
                               $"(Duration: {duration:mm\\:ss}, Audio: {session.AudioBytesRelayed / 1024}KB, " +
                               $"Video: {session.VideoBytesRelayed / 1024}KB)");
            }

            // Relayer vers l'autre peer
            await RelayMessageToPeer(message.To, message);
        }

        private async Task RelayAudioData(VOIPMessage message)
        {
            await RelayMessageToPeer(message.To, message);

            // Statistiques
            var sessionId1 = $"{message.From}_{message.To}";
            var sessionId2 = $"{message.To}_{message.From}";

            if (_activeSessions.TryGetValue(sessionId1, out var session) ||
                _activeSessions.TryGetValue(sessionId2, out session))
            {
                session.AudioBytesRelayed += message.Data.Length;
            }
        }

        private async Task RelayVideoData(VOIPMessage message)
        {
            await RelayMessageToPeer(message.To, message);

            // Statistiques
            var sessionId1 = $"{message.From}_{message.To}";
            var sessionId2 = $"{message.To}_{message.From}";

            if (_activeSessions.TryGetValue(sessionId1, out var session) ||
                _activeSessions.TryGetValue(sessionId2, out session))
            {
                session.VideoBytesRelayed += message.Data.Length;
            }
        }

        private async Task RelayBinaryData(string fromPeer, byte[] data, int length)
        {
            try
            {
                // ✅ FIX CRITIQUE: Relayer réellement les données audio au destinataire
                // Trouver la session active pour ce peer
                VOIPSession? targetSession = null;
                string targetPeer = "";

                foreach (var session in _activeSessions.Values)
                {
                    if (session.Caller == fromPeer)
                    {
                        targetSession = session;
                        targetPeer = session.Callee;
                        break;
                    }
                    else if (session.Callee == fromPeer)
                    {
                        targetSession = session;
                        targetPeer = session.Caller;
                        break;
                    }
                }

                if (targetSession != null && !string.IsNullOrEmpty(targetPeer))
                {
                    // Créer message JSON audio_data pour le client destinataire
                    var audioMessage = new VOIPMessage
                    {
                        Type = "audio_data",
                        From = fromPeer,
                        To = targetPeer,
                        Data = Convert.ToBase64String(data, 0, length),
                        Timestamp = DateTime.UtcNow
                    };

                    // Relayer vers le destinataire
                    await RelayMessageToPeer(targetPeer, audioMessage);

                    // Mettre à jour les statistiques de session
                    targetSession.AudioBytesRelayed += length;

                    LogEvent?.Invoke($"[VOIP-RELAY] ✅ Audio data relayed: {fromPeer} → {targetPeer} ({length} bytes)");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-RELAY] ⚠️ No active session found for {fromPeer}, dropping {length} bytes");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY] ❌ Error relaying binary data from {fromPeer}: {ex.Message}");
            }
        }

        private async Task RelayMessageToPeer(string toPeer, VOIPMessage message)
        {
            if (_clients.TryGetValue(toPeer, out var connection))
            {
                try
                {
                    var json = JsonSerializer.Serialize(message);
                    var data = Encoding.UTF8.GetBytes(json);
                    await connection.Stream.WriteAsync(data, 0, data.Length);
                    await connection.Stream.FlushAsync();
                }
                catch (Exception ex)
                {
                    LogEvent?.Invoke($"[VOIP-RELAY] ❌ Failed to relay to {toPeer}: {ex.Message}");
                    // Remove dead connection
                    _clients.TryRemove(toPeer, out _);
                }
            }
            else
            {
                LogEvent?.Invoke($"[VOIP-RELAY] ⚠️ Peer {toPeer} not connected to VOIP relay");
            }
        }

        /// <summary>
        /// ✅ FIX: Nettoie les sessions actives pour un peer déconnecté
        /// </summary>
        private void CleanupSessionsForPeer(string peerName)
        {
            var sessionsToRemove = new List<string>();

            // Trouver toutes les sessions impliquant ce peer
            foreach (var kvp in _activeSessions)
            {
                var session = kvp.Value;
                if (session.Caller == peerName || session.Callee == peerName)
                {
                    sessionsToRemove.Add(kvp.Key);
                }
            }

            // Supprimer les sessions trouvées
            foreach (var sessionId in sessionsToRemove)
            {
                if (_activeSessions.TryRemove(sessionId, out var session))
                {
                    var duration = DateTime.Now - session.StartedAt;
                    LogEvent?.Invoke($"[VOIP-RELAY] 🧹 Session cleanup: {session.Caller} ↔ {session.Callee} " +
                                   $"(Duration: {duration:mm\\:ss}, Audio: {session.AudioBytesRelayed / 1024}KB, " +
                                   $"Video: {session.VideoBytesRelayed / 1024}KB)");
                }
            }

            if (sessionsToRemove.Count > 0)
            {
                LogEvent?.Invoke($"[VOIP-RELAY] ✅ Cleaned up {sessionsToRemove.Count} sessions for disconnected peer: {peerName}");
            }
        }

        public void Stop()
        {
            try
            {
                _isRunning = false;
                _cancellationTokenSource.Cancel();
                _tcpListener?.Stop();

                // Fermer toutes les connexions clients
                foreach (var client in _clients.Values)
                {
                    client.TcpClient?.Close();
                }
                _clients.Clear();
                _activeSessions.Clear();

                LogEvent?.Invoke("[VOIP-RELAY] 🛑 VOIP Relay Server stopped");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY] ❌ Error stopping VOIP relay: {ex.Message}");
            }
        }
    }
}