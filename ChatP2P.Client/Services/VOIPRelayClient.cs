using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// VOIP Relay Client - Fallback quand WebRTC P2P échoue
    /// Se connecte au serveur port 8892 pour relay audio/vidéo
    /// </summary>
    public class VOIPRelayClient
    {
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private readonly string _serverIP;
        private readonly string _peerName;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private bool _isConnected = false;

        public event Action<string>? LogEvent;
        public event Action<string, string>? VoipMessageReceived; // From, Message
        public event Action<string, byte[]>? AudioDataReceived;    // From, AudioData
        public event Action<string, byte[]>? VideoDataReceived;    // From, VideoData

        private class VOIPMessage
        {
            public string Type { get; set; } = "";
            public string From { get; set; } = "";
            public string To { get; set; } = "";
            public string Data { get; set; } = "";
            public DateTime Timestamp { get; set; } = DateTime.Now;
        }

        public VOIPRelayClient(string serverIP, string peerName)
        {
            _serverIP = serverIP;
            _peerName = peerName;
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] 🔗 Connecting to VOIP relay server {_serverIP}:8892");

                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(_serverIP, 8892);
                _stream = _tcpClient.GetStream();
                _isConnected = true;

                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ✅ Connected to VOIP relay server");

                // ✅ FIX CRITIQUE: Envoyer message d'identification immédiatement
                var identityMessage = new VOIPMessage
                {
                    Type = "client_identity",
                    From = _peerName,
                    To = "",
                    Data = "register",
                    Timestamp = DateTime.UtcNow
                };

                await SendMessageAsync(identityMessage);
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] 📝 Sent identity message for peer: {_peerName}");

                // Démarrer la réception des messages
                _ = Task.Run(ReceiveMessagesAsync, _cancellationTokenSource.Token);

                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ❌ Failed to connect to VOIP relay: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> StartCallAsync(string targetPeer, bool includeVideo = false)
        {
            if (!_isConnected || _stream == null)
            {
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ❌ Not connected to VOIP relay server");
                return false;
            }

            try
            {
                var message = new VOIPMessage
                {
                    Type = "call_start",
                    From = _peerName,
                    To = targetPeer,
                    Data = includeVideo ? "audio_video" : "audio_only"
                };

                await SendMessageAsync(message);
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] 📞 Call started via relay: {_peerName} → {targetPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ❌ Failed to start call via relay: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> AcceptCallAsync(string fromPeer)
        {
            if (!_isConnected || _stream == null)
                return false;

            try
            {
                var message = new VOIPMessage
                {
                    Type = "call_accept",
                    From = _peerName,
                    To = fromPeer,
                    Data = "accepted"
                };

                await SendMessageAsync(message);
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ✅ Call accepted via relay: {_peerName} → {fromPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ❌ Failed to accept call via relay: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> EndCallAsync(string targetPeer)
        {
            if (!_isConnected || _stream == null)
                return false;

            try
            {
                var message = new VOIPMessage
                {
                    Type = "call_end",
                    From = _peerName,
                    To = targetPeer,
                    Data = "ended"
                };

                await SendMessageAsync(message);
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] 📴 Call ended via relay: {_peerName} → {targetPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ❌ Failed to end call via relay: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> SendAudioDataAsync(string targetPeer, byte[] audioData)
        {
            if (!_isConnected || _stream == null)
                return false;

            try
            {
                var message = new VOIPMessage
                {
                    Type = "audio_data",
                    From = _peerName,
                    To = targetPeer,
                    Data = Convert.ToBase64String(audioData)
                };

                await SendMessageAsync(message);
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ❌ Failed to send audio data: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> SendVideoDataAsync(string targetPeer, byte[] videoData)
        {
            if (!_isConnected || _stream == null)
                return false;

            try
            {
                var message = new VOIPMessage
                {
                    Type = "video_data",
                    From = _peerName,
                    To = targetPeer,
                    Data = Convert.ToBase64String(videoData)
                };

                await SendMessageAsync(message);
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ❌ Failed to send video data: {ex.Message}");
                return false;
            }
        }

        private async Task SendMessageAsync(VOIPMessage message)
        {
            if (_stream == null) return;

            var json = JsonSerializer.Serialize(message);
            var data = Encoding.UTF8.GetBytes(json);
            await _stream.WriteAsync(data, 0, data.Length);
            await _stream.FlushAsync();
        }

        private async Task ReceiveMessagesAsync()
        {
            var buffer = new byte[8192];

            try
            {
                while (_isConnected && _tcpClient?.Connected == true && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var bytesRead = await _stream!.ReadAsync(buffer, 0, buffer.Length, _cancellationTokenSource.Token);
                    if (bytesRead == 0) break;

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    try
                    {
                        var voipMsg = JsonSerializer.Deserialize<VOIPMessage>(message);
                        if (voipMsg != null)
                        {
                            await ProcessReceivedMessage(voipMsg);
                        }
                    }
                    catch (JsonException)
                    {
                        LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ⚠️ Received non-JSON data, ignoring");
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ❌ Error receiving messages: {ex.Message}");
                }
            }
            finally
            {
                _isConnected = false;
            }
        }

        private async Task ProcessReceivedMessage(VOIPMessage message)
        {
            try
            {
                switch (message.Type.ToLower())
                {
                    case "call_start":
                        LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] 📞 Incoming call from {message.From}");
                        VoipMessageReceived?.Invoke(message.From, "call_start");
                        break;

                    case "call_accept":
                        LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ✅ Call accepted by {message.From}");
                        VoipMessageReceived?.Invoke(message.From, "call_accept");
                        break;

                    case "call_end":
                        LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] 📴 Call ended by {message.From}");
                        VoipMessageReceived?.Invoke(message.From, "call_end");
                        break;

                    case "audio_data":
                        if (!string.IsNullOrEmpty(message.Data))
                        {
                            var audioData = Convert.FromBase64String(message.Data);
                            AudioDataReceived?.Invoke(message.From, audioData);
                        }
                        break;

                    case "video_data":
                        if (!string.IsNullOrEmpty(message.Data))
                        {
                            var videoData = Convert.FromBase64String(message.Data);
                            VideoDataReceived?.Invoke(message.From, videoData);
                        }
                        break;

                    default:
                        LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ⚠️ Unknown message type: {message.Type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ❌ Error processing message: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            try
            {
                _isConnected = false;
                _cancellationTokenSource.Cancel();
                _stream?.Close();
                _tcpClient?.Close();
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] 📴 Disconnected from VOIP relay server");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-RELAY-CLIENT] ❌ Error disconnecting: {ex.Message}");
            }
        }

        public bool IsConnected => _isConnected && _tcpClient?.Connected == true;
    }
}