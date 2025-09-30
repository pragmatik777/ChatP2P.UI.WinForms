using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// UDP Audio Relay Client - Real-time audio transmission
    /// Connects to UDP port 8895 for minimal latency audio streaming
    /// </summary>
    public class UDPAudioRelayClient : IDisposable
    {
        private UdpClient? _udpClient;
        private IPEndPoint? _serverEndPoint;
        private string _peerName = "";
        private bool _isConnected = false;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private int _packetNumber = 0;

        public event Action<string>? LogEvent;
        public event Action<byte[]>? AudioDataReceived;

        public bool IsConnected => _isConnected;

        /// <summary>
        /// Connect to UDP audio relay server
        /// </summary>
        public async Task<bool> ConnectAsync(string serverIP, string peerName)
        {
            try
            {
                _peerName = peerName;
                _serverEndPoint = new IPEndPoint(IPAddress.Parse(serverIP), 8895);
                _udpClient = new UdpClient();

                // Register with server
                var registerMessage = new UDPAudioMessage
                {
                    Type = "REGISTER",
                    FromPeer = peerName,
                    ToPeer = "",
                    Timestamp = DateTime.UtcNow
                };

                await SendMessage(registerMessage);

                // Start listening for incoming packets
                _ = Task.Run(ListenForPacketsAsync);

                _isConnected = true;
                LogEvent?.Invoke($"[UDP-AUDIO] ✅ Connected {peerName} to UDP audio relay {serverIP}:8895");

                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-AUDIO] ❌ Connection failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Start audio session with another peer
        /// </summary>
        public async Task<bool> StartSessionAsync(string targetPeer)
        {
            if (!_isConnected || _serverEndPoint == null) return false;

            try
            {
                var startMessage = new UDPAudioMessage
                {
                    Type = "START_SESSION",
                    FromPeer = _peerName,
                    ToPeer = targetPeer,
                    Timestamp = DateTime.UtcNow
                };

                await SendMessage(startMessage);
                LogEvent?.Invoke($"[UDP-AUDIO] 🎵 Starting audio session with {targetPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-AUDIO] ❌ Failed to start session: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send audio data via UDP (minimal latency)
        /// </summary>
        public async Task<bool> SendAudioDataAsync(string targetPeer, byte[] audioData)
        {
            if (!_isConnected || _serverEndPoint == null) return false;

            try
            {
                var audioMessage = new UDPAudioMessage
                {
                    Type = "AUDIO_DATA",
                    FromPeer = _peerName,
                    ToPeer = targetPeer,
                    AudioData = Convert.ToBase64String(audioData),
                    PacketNumber = ++_packetNumber,
                    Timestamp = DateTime.UtcNow
                };

                await SendMessage(audioMessage);

                LogEvent?.Invoke($"[UDP-AUDIO] 🎵 Sent audio packet #{_packetNumber} ({audioData.Length} bytes) to {targetPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-AUDIO] ❌ Failed to send audio: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// End audio session
        /// </summary>
        public async Task EndSessionAsync(string targetPeer)
        {
            if (!_isConnected || _serverEndPoint == null) return;

            try
            {
                var endMessage = new UDPAudioMessage
                {
                    Type = "END_SESSION",
                    FromPeer = _peerName,
                    ToPeer = targetPeer,
                    Timestamp = DateTime.UtcNow
                };

                await SendMessage(endMessage);
                LogEvent?.Invoke($"[UDP-AUDIO] 📴 Ended audio session with {targetPeer}");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-AUDIO] ❌ Failed to end session: {ex.Message}");
            }
        }

        private async Task SendMessage(UDPAudioMessage message)
        {
            if (_udpClient == null || _serverEndPoint == null) return;

            var json = JsonSerializer.Serialize(message);
            var data = Encoding.UTF8.GetBytes(json);
            await _udpClient.SendAsync(data, _serverEndPoint);
        }

        private async Task ListenForPacketsAsync()
        {
            while (_isConnected && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient!.ReceiveAsync();
                    var data = result.Buffer;

                    // Process packet in background
                    _ = Task.Run(() => ProcessReceivedPacket(data));
                }
                catch (Exception ex) when (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    LogEvent?.Invoke($"[UDP-AUDIO] ❌ Error receiving packet: {ex.Message}");
                }
            }
        }

        private void ProcessReceivedPacket(byte[] data)
        {
            try
            {
                var json = Encoding.UTF8.GetString(data);
                var message = JsonSerializer.Deserialize<UDPAudioMessage>(json);

                if (message == null) return;

                switch (message.Type)
                {
                    case "REGISTER_CONFIRM":
                        LogEvent?.Invoke($"[UDP-AUDIO] ✅ Registration confirmed by server");
                        break;

                    case "SESSION_STARTED":
                        LogEvent?.Invoke($"[UDP-AUDIO] 🎵 Audio session started");
                        break;

                    case "AUDIO_DATA":
                        if (!string.IsNullOrEmpty(message.AudioData))
                        {
                            var audioBytes = Convert.FromBase64String(message.AudioData);
                            AudioDataReceived?.Invoke(audioBytes);
                            LogEvent?.Invoke($"[UDP-AUDIO] 🎵 Received audio packet #{message.PacketNumber} ({audioBytes.Length} bytes) from {message.FromPeer}");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-AUDIO] ❌ Error processing packet: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            _isConnected = false;
            _cancellationTokenSource.Cancel();

            _udpClient?.Close();
            _udpClient?.Dispose();
            _udpClient = null;

            LogEvent?.Invoke($"[UDP-AUDIO] 📴 Disconnected {_peerName} from UDP audio relay");
        }

        public void Dispose()
        {
            Disconnect();
            _cancellationTokenSource.Dispose();
        }

        private class UDPAudioMessage
        {
            public string Type { get; set; } = "";
            public string FromPeer { get; set; } = "";
            public string ToPeer { get; set; } = "";
            public string? AudioData { get; set; }
            public int PacketNumber { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }
}