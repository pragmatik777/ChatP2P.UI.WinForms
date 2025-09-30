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
    /// UDP Audio Relay Service - Port 8895
    /// Real-time audio relay using UDP for minimal latency
    /// </summary>
    public class UDPAudioRelayService
    {
        private UdpClient? _udpServer;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly ConcurrentDictionary<string, IPEndPoint> _clients = new();
        private readonly ConcurrentDictionary<string, AudioSession> _activeSessions = new();
        private bool _isRunning = false;
        private const int UDP_AUDIO_PORT = 8895;

        public event Action<string>? LogEvent;

        private class AudioSession
        {
            public string Peer1 { get; set; } = "";
            public string Peer2 { get; set; } = "";
            public IPEndPoint? Peer1EndPoint { get; set; }
            public IPEndPoint? Peer2EndPoint { get; set; }
            public DateTime StartedAt { get; set; }
            public long AudioPacketsRelayed { get; set; } = 0;
        }

        private class UDPAudioMessage
        {
            public string Type { get; set; } = "";
            public string FromPeer { get; set; } = "";
            public string ToPeer { get; set; } = "";
            public string? AudioData { get; set; } // Base64 encoded audio
            public int PacketNumber { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public async Task StartAsync()
        {
            try
            {
                _udpServer = new UdpClient(UDP_AUDIO_PORT);
                _isRunning = true;

                LogEvent?.Invoke($"[UDP-AUDIO] ✅ UDP Audio Relay started on port {UDP_AUDIO_PORT}");

                // Start listening for UDP packets
                _ = Task.Run(ListenForPacketsAsync);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-AUDIO] ❌ Failed to start UDP audio relay: {ex.Message}");
                throw;
            }
        }

        private async Task ListenForPacketsAsync()
        {
            while (_isRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpServer!.ReceiveAsync();
                    var clientEndPoint = result.RemoteEndPoint;
                    var data = result.Buffer;

                    // Process packet in background to avoid blocking
                    _ = Task.Run(() => ProcessUdpPacket(data, clientEndPoint));
                }
                catch (Exception ex) when (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    LogEvent?.Invoke($"[UDP-AUDIO] ❌ Error receiving UDP packet: {ex.Message}");
                }
            }
        }

        private async Task ProcessUdpPacket(byte[] data, IPEndPoint clientEndPoint)
        {
            try
            {
                var json = Encoding.UTF8.GetString(data);
                var message = JsonSerializer.Deserialize<UDPAudioMessage>(json);

                if (message == null) return;

                switch (message.Type)
                {
                    case "REGISTER":
                        await HandleClientRegistration(message.FromPeer, clientEndPoint);
                        break;

                    case "AUDIO_DATA":
                        await HandleAudioData(message, clientEndPoint);
                        break;

                    case "START_SESSION":
                        await HandleStartSession(message.FromPeer, message.ToPeer, clientEndPoint);
                        break;

                    case "END_SESSION":
                        await HandleEndSession(message.FromPeer, message.ToPeer);
                        break;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-AUDIO] ❌ Error processing UDP packet: {ex.Message}");
            }
        }

        private async Task HandleClientRegistration(string peerName, IPEndPoint endPoint)
        {
            _clients[peerName] = endPoint;
            LogEvent?.Invoke($"[UDP-AUDIO] ✅ Registered {peerName} at {endPoint}");

            // Send confirmation back
            var response = new UDPAudioMessage
            {
                Type = "REGISTER_CONFIRM",
                FromPeer = "SERVER",
                ToPeer = peerName,
                Timestamp = DateTime.UtcNow
            };

            await SendUdpMessage(response, endPoint);
        }

        private async Task HandleStartSession(string peer1, string peer2, IPEndPoint initiatorEndPoint)
        {
            if (!_clients.TryGetValue(peer2, out var peer2EndPoint))
            {
                LogEvent?.Invoke($"[UDP-AUDIO] ❌ Cannot start session: {peer2} not registered");
                return;
            }

            var sessionKey = $"{peer1}↔{peer2}";
            var session = new AudioSession
            {
                Peer1 = peer1,
                Peer2 = peer2,
                Peer1EndPoint = initiatorEndPoint,
                Peer2EndPoint = peer2EndPoint,
                StartedAt = DateTime.UtcNow
            };

            _activeSessions[sessionKey] = session;
            LogEvent?.Invoke($"[UDP-AUDIO] 🎵 Started audio session: {sessionKey}");

            // Notify both peers
            var notification = new UDPAudioMessage
            {
                Type = "SESSION_STARTED",
                FromPeer = "SERVER",
                Timestamp = DateTime.UtcNow
            };

            var notification1 = new UDPAudioMessage
            {
                Type = "SESSION_STARTED",
                FromPeer = "SERVER",
                ToPeer = peer1,
                Timestamp = DateTime.UtcNow
            };
            var notification2 = new UDPAudioMessage
            {
                Type = "SESSION_STARTED",
                FromPeer = "SERVER",
                ToPeer = peer2,
                Timestamp = DateTime.UtcNow
            };

            await SendUdpMessage(notification1, initiatorEndPoint);
            await SendUdpMessage(notification2, peer2EndPoint);
        }

        private async Task HandleAudioData(UDPAudioMessage message, IPEndPoint senderEndPoint)
        {
            // Find active session
            var sessionKey1 = $"{message.FromPeer}↔{message.ToPeer}";
            var sessionKey2 = $"{message.ToPeer}↔{message.FromPeer}";

            if (!_activeSessions.TryGetValue(sessionKey1, out var session) &&
                !_activeSessions.TryGetValue(sessionKey2, out session))
            {
                return; // No active session
            }

            // Determine target endpoint
            IPEndPoint? targetEndPoint = null;
            if (session.Peer1 == message.FromPeer)
                targetEndPoint = session.Peer2EndPoint;
            else if (session.Peer2 == message.FromPeer)
                targetEndPoint = session.Peer1EndPoint;

            if (targetEndPoint == null) return;

            // Relay audio data directly
            await SendUdpMessage(message, targetEndPoint);

            // Update statistics
            session.AudioPacketsRelayed++;

            LogEvent?.Invoke($"[UDP-AUDIO] 🎵 Relayed audio packet #{message.PacketNumber} from {message.FromPeer} to {message.ToPeer}");
        }

        private async Task HandleEndSession(string peer1, string peer2)
        {
            var sessionKey1 = $"{peer1}↔{peer2}";
            var sessionKey2 = $"{peer2}↔{peer1}";

            if (_activeSessions.TryRemove(sessionKey1, out var session) ||
                _activeSessions.TryRemove(sessionKey2, out session))
            {
                LogEvent?.Invoke($"[UDP-AUDIO] 📴 Ended audio session: {session.Peer1}↔{session.Peer2} (relayed {session.AudioPacketsRelayed} packets)");
            }
        }

        private async Task SendUdpMessage(UDPAudioMessage message, IPEndPoint endPoint)
        {
            try
            {
                var json = JsonSerializer.Serialize(message);
                var data = Encoding.UTF8.GetBytes(json);
                await _udpServer!.SendAsync(data, endPoint);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-AUDIO] ❌ Failed to send UDP message to {endPoint}: {ex.Message}");
            }
        }

        public async Task StopAsync()
        {
            _isRunning = false;
            _cancellationTokenSource.Cancel();

            _udpServer?.Close();
            _udpServer?.Dispose();

            LogEvent?.Invoke("[UDP-AUDIO] 📴 UDP Audio Relay stopped");
        }

        public void Dispose()
        {
            StopAsync().Wait(1000);
            _cancellationTokenSource.Dispose();
        }
    }
}