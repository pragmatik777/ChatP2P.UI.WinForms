using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ChatP2P.Server
{
    /// <summary>
    /// 🎤 Service de relay audio TCP pur (port 8893)
    /// Données binaires directes sans JSON/Base64 overhead
    /// </summary>
    public class VOIPAudioRelayService
    {
        private TcpListener? _tcpListener;
        private readonly ConcurrentDictionary<string, AudioConnection> _audioClients = new();
        private readonly ConcurrentDictionary<string, AudioSession> _audioSessions = new();
        private bool _isRunning = false;

        public event Action<string>? LogEvent;

        private class AudioConnection
        {
            public TcpClient TcpClient { get; set; } = null!;
            public NetworkStream Stream { get; set; } = null!;
            public string PeerName { get; set; } = "";
            public DateTime ConnectedAt { get; set; }
        }

        private class AudioSession
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
                _tcpListener = new TcpListener(IPAddress.Any, 8893);
                _tcpListener.Start();
                _isRunning = true;

                LogEvent?.Invoke("[AUDIO-RELAY] 🎤 Pure Audio Relay started on port 8893");
                LogEvent?.Invoke("[AUDIO-RELAY] 🚀 Ready for binary audio streaming (no JSON overhead)");

                // Accepter connexions en parallèle
                _ = Task.Run(AcceptClientsAsync);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AUDIO-RELAY] ❌ Error starting audio relay: {ex.Message}");
            }
        }

        private async Task AcceptClientsAsync()
        {
            while (_isRunning)
            {
                try
                {
                    var tcpClient = await _tcpListener!.AcceptTcpClientAsync();
                    LogEvent?.Invoke($"[AUDIO-RELAY] 📞 New audio client connected from {tcpClient.Client.RemoteEndPoint}");

                    // Traiter chaque client en parallèle
                    _ = Task.Run(() => HandleAudioClientAsync(tcpClient));
                }
                catch (Exception ex) when (_isRunning)
                {
                    LogEvent?.Invoke($"[AUDIO-RELAY] ❌ Error accepting audio client: {ex.Message}");
                }
            }
        }

        private async Task HandleAudioClientAsync(TcpClient tcpClient)
        {
            string peerName = "";
            AudioConnection? connection = null;

            try
            {
                var stream = tcpClient.GetStream();
                var buffer = new byte[65536]; // 64KB buffer pour audio

                // Lire premier message d'identification (format: "PEER:PeerName")
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    var identityMessage = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    if (identityMessage.StartsWith("PEER:"))
                    {
                        peerName = identityMessage.Substring(5);
                        connection = new AudioConnection
                        {
                            TcpClient = tcpClient,
                            Stream = stream,
                            PeerName = peerName,
                            ConnectedAt = DateTime.UtcNow
                        };

                        _audioClients.TryAdd(peerName, connection);
                        LogEvent?.Invoke($"[AUDIO-RELAY] ✅ Audio client {peerName} registered");

                        // Confirmer connexion
                        var confirmBytes = Encoding.UTF8.GetBytes("AUDIO_READY");
                        await stream.WriteAsync(confirmBytes, 0, confirmBytes.Length);
                    }
                }

                // Boucle de traitement des données audio binaires pures
                while (_isRunning && tcpClient.Connected)
                {
                    bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        // Relayer directement les données audio binaires
                        await RelayPureAudioData(peerName, buffer, bytesRead);
                    }
                    else
                    {
                        break; // Connexion fermée
                    }
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AUDIO-RELAY] ❌ Error handling audio client {peerName}: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(peerName))
                {
                    _audioClients.TryRemove(peerName, out _);
                    LogEvent?.Invoke($"[AUDIO-RELAY] 📴 Audio client {peerName} disconnected");
                }
                tcpClient?.Close();
            }
        }

        private async Task RelayPureAudioData(string fromPeer, byte[] audioData, int length)
        {
            try
            {
                // Trouver destinataire via session active
                string targetPeer = FindAudioTarget(fromPeer);

                if (!string.IsNullOrEmpty(targetPeer) && _audioClients.TryGetValue(targetPeer, out var targetConnection))
                {
                    // 🚀 RELAY BINAIRE PUR - Pas de JSON/Base64 !
                    await targetConnection.Stream.WriteAsync(audioData, 0, length);
                    await targetConnection.Stream.FlushAsync();

                    // Mettre à jour statistiques
                    UpdateAudioSessionStats(fromPeer, targetPeer, length);

                    LogEvent?.Invoke($"[AUDIO-RELAY] 🎵 Pure audio: {fromPeer} → {targetPeer} ({length} bytes)");
                }
                else
                {
                    LogEvent?.Invoke($"[AUDIO-RELAY] ⚠️ No audio target for {fromPeer}, dropping {length} bytes");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AUDIO-RELAY] ❌ Error relaying audio from {fromPeer}: {ex.Message}");
            }
        }

        private string FindAudioTarget(string fromPeer)
        {
            // Chercher dans les sessions audio actives
            foreach (var session in _audioSessions.Values)
            {
                if (session.Caller == fromPeer)
                    return session.Callee;
                else if (session.Callee == fromPeer)
                    return session.Caller;
            }
            return "";
        }

        private void UpdateAudioSessionStats(string fromPeer, string targetPeer, int bytes)
        {
            var sessionId = $"{fromPeer}-{targetPeer}";
            var reverseSessionId = $"{targetPeer}-{fromPeer}";

            if (_audioSessions.TryGetValue(sessionId, out var session) ||
                _audioSessions.TryGetValue(reverseSessionId, out session))
            {
                session.BytesRelayed += bytes;
            }
        }

        /// <summary>
        /// Démarrer session audio entre deux peers
        /// </summary>
        public void StartAudioSession(string caller, string callee)
        {
            try
            {
                var sessionId = $"{caller}-{callee}";
                var session = new AudioSession
                {
                    Caller = caller,
                    Callee = callee,
                    StartedAt = DateTime.UtcNow
                };

                _audioSessions.TryAdd(sessionId, session);
                LogEvent?.Invoke($"[AUDIO-RELAY] 🎤 Audio session started: {caller} ↔ {callee}");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AUDIO-RELAY] ❌ Error starting audio session: {ex.Message}");
            }
        }

        /// <summary>
        /// Arrêter session audio
        /// </summary>
        public void StopAudioSession(string caller, string callee)
        {
            try
            {
                var sessionId1 = $"{caller}-{callee}";
                var sessionId2 = $"{callee}-{caller}";

                if (_audioSessions.TryRemove(sessionId1, out var session) ||
                    _audioSessions.TryRemove(sessionId2, out session))
                {
                    LogEvent?.Invoke($"[AUDIO-RELAY] 🛑 Audio session ended: {caller} ↔ {callee} ({session.BytesRelayed} bytes relayed)");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AUDIO-RELAY] ❌ Error stopping audio session: {ex.Message}");
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _isRunning = false;
                _tcpListener?.Stop();

                // Fermer toutes les connexions
                foreach (var client in _audioClients.Values)
                {
                    client.TcpClient?.Close();
                }
                _audioClients.Clear();
                _audioSessions.Clear();

                LogEvent?.Invoke("[AUDIO-RELAY] 🛑 Pure Audio Relay stopped");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AUDIO-RELAY] ❌ Error stopping audio relay: {ex.Message}");
            }
        }

        /// <summary>
        /// Statistiques audio relay
        /// </summary>
        public string GetAudioStats()
        {
            return $"Audio Clients: {_audioClients.Count}, Active Sessions: {_audioSessions.Count}";
        }
    }
}