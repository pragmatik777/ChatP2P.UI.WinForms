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
    /// 📹 UDP Video Relay Service - Port 8894
    /// Real-time video relay using UDP for minimal latency
    /// </summary>
    public class VOIPVideoRelayService
    {
        private UdpClient? _udpServer;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly ConcurrentDictionary<string, IPEndPoint> _clients = new();
        private readonly ConcurrentDictionary<string, VideoSession> _activeSessions = new();
        private readonly ConcurrentDictionary<string, DateTime> _clientLastSeen = new();
        private bool _isRunning = false;
        private const int UDP_VIDEO_PORT = 8894;

        public event Action<string>? LogEvent;

        private class VideoSession
        {
            public string Peer1 { get; set; } = "";
            public string Peer2 { get; set; } = "";
            public IPEndPoint? Peer1EndPoint { get; set; }
            public IPEndPoint? Peer2EndPoint { get; set; }
            public DateTime StartedAt { get; set; }
            public long VideoPacketsRelayed { get; set; } = 0;
        }

        private class UDPVideoMessage
        {
            public string Type { get; set; } = "";
            public string FromPeer { get; set; } = "";
            public string ToPeer { get; set; } = "";
            public string? VideoData { get; set; } // Base64 encoded video
            public int PacketNumber { get; set; }
            public int FragmentIndex { get; set; } = 0;
            public int TotalFragments { get; set; } = 1;
            public DateTime Timestamp { get; set; }
        }

        public async Task StartAsync()
        {
            try
            {
                _udpServer = new UdpClient(UDP_VIDEO_PORT);
                _isRunning = true;

                LogEvent?.Invoke($"[UDP-VIDEO] ✅ UDP Video Relay started on port {UDP_VIDEO_PORT}");

                // Start listening for UDP packets
                _ = Task.Run(ListenForPacketsAsync);

                // Start periodic cleanup task
                _ = Task.Run(PeriodicCleanupAsync);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Failed to start UDP video relay: {ex.Message}");
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
                    LogEvent?.Invoke($"[UDP-VIDEO] ❌ Error receiving UDP packet: {ex.Message}");
                }
            }
        }

        private async Task ProcessUdpPacket(byte[] data, IPEndPoint clientEndPoint)
        {
            try
            {
                Console.WriteLine($"[UDP-VIDEO-SERVER] 📨 Received UDP packet from {clientEndPoint} ({data.Length} bytes)");
                await ServerLogHelper.LogToUDPVideoAsync($"📨 Received UDP packet from {clientEndPoint} ({data.Length} bytes)");

                var json = Encoding.UTF8.GetString(data);
                var message = JsonSerializer.Deserialize<UDPVideoMessage>(json);

                if (message == null)
                {
                    Console.WriteLine($"[UDP-VIDEO-SERVER] ❌ Failed to deserialize message from {clientEndPoint}");
                    await ServerLogHelper.LogToUDPVideoAsync($"❌ Failed to deserialize message from {clientEndPoint}");
                    return;
                }

                Console.WriteLine($"[UDP-VIDEO-SERVER] 📋 Message type: {message.Type} from {message.FromPeer}");
                await ServerLogHelper.LogToUDPVideoAsync($"📋 Message type: {message.Type} from {message.FromPeer}");

                switch (message.Type)
                {
                    case "REGISTER":
                        await HandleClientRegistration(message.FromPeer, clientEndPoint);
                        break;

                    case "VIDEO_DATA":
                        await HandleVideoData(message, clientEndPoint);
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
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Error processing UDP packet: {ex.Message}");
            }
        }

        private async Task HandleClientRegistration(string peerName, IPEndPoint endPoint)
        {
            _clients[peerName] = endPoint;
            _clientLastSeen[peerName] = DateTime.UtcNow;
            LogEvent?.Invoke($"[UDP-VIDEO] ✅ Registered {peerName} at {endPoint}");

            // Send confirmation back
            var response = new UDPVideoMessage
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
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Cannot start session: {peer2} not registered");
                return;
            }

            var sessionKey = $"{peer1}↔{peer2}";
            var session = new VideoSession
            {
                Peer1 = peer1,
                Peer2 = peer2,
                Peer1EndPoint = initiatorEndPoint,
                Peer2EndPoint = peer2EndPoint,
                StartedAt = DateTime.UtcNow
            };

            _activeSessions[sessionKey] = session;
            LogEvent?.Invoke($"[UDP-VIDEO] 📹 Started video session: {sessionKey}");

            // Notify both peers
            var notification1 = new UDPVideoMessage
            {
                Type = "SESSION_STARTED",
                FromPeer = "SERVER",
                ToPeer = peer1,
                Timestamp = DateTime.UtcNow
            };
            var notification2 = new UDPVideoMessage
            {
                Type = "SESSION_STARTED",
                FromPeer = "SERVER",
                ToPeer = peer2,
                Timestamp = DateTime.UtcNow
            };

            await SendUdpMessage(notification1, initiatorEndPoint);
            await SendUdpMessage(notification2, peer2EndPoint);
        }

        private async Task HandleVideoData(UDPVideoMessage message, IPEndPoint senderEndPoint)
        {
            // Update last seen time for sender
            _clientLastSeen[message.FromPeer] = DateTime.UtcNow;

            // Find active session
            var sessionKey1 = $"{message.FromPeer}↔{message.ToPeer}";
            var sessionKey2 = $"{message.ToPeer}↔{message.FromPeer}";

            Console.WriteLine($"[UDP-VIDEO-SERVER] 📦 Received video fragment from {message.FromPeer} to {message.ToPeer} (packet #{message.PacketNumber}, fragment {message.FragmentIndex}/{message.TotalFragments})");
            await ServerLogHelper.LogToUDPVideoAsync($"📦 Received video fragment from {message.FromPeer} to {message.ToPeer} (packet #{message.PacketNumber}, fragment {message.FragmentIndex}/{message.TotalFragments})");

            if (!_activeSessions.TryGetValue(sessionKey1, out var session) &&
                !_activeSessions.TryGetValue(sessionKey2, out session))
            {
                Console.WriteLine($"[UDP-VIDEO-SERVER] ❌ No active session found for {sessionKey1} or {sessionKey2}");
                Console.WriteLine($"[UDP-VIDEO-SERVER] 📊 Active sessions: {string.Join(", ", _activeSessions.Keys)}");
                await ServerLogHelper.LogToUDPVideoAsync($"❌ No active session found for {sessionKey1} or {sessionKey2}");
                await ServerLogHelper.LogToUDPVideoAsync($"📊 Active sessions: {string.Join(", ", _activeSessions.Keys)}");
                return; // No active session
            }

            // Determine target endpoint
            IPEndPoint? targetEndPoint = null;
            if (session.Peer1 == message.FromPeer)
                targetEndPoint = session.Peer2EndPoint;
            else if (session.Peer2 == message.FromPeer)
                targetEndPoint = session.Peer1EndPoint;

            if (targetEndPoint == null) return;

            // Relay video data directly
            Console.WriteLine($"[UDP-VIDEO-SERVER] 🔄 Relaying fragment {message.FragmentIndex}/{message.TotalFragments} to {targetEndPoint}");
            await ServerLogHelper.LogToUDPVideoAsync($"🔄 Relaying fragment {message.FragmentIndex}/{message.TotalFragments} to {targetEndPoint}");
            await SendUdpMessage(message, targetEndPoint);

            // Update statistics
            session.VideoPacketsRelayed++;

            if (message.FragmentIndex == 0) // Log only first fragment to avoid spam
            {
                LogEvent?.Invoke($"[UDP-VIDEO] 📹 Relayed video packet #{message.PacketNumber} from {message.FromPeer} to {message.ToPeer} ({message.TotalFragments} fragments)");
                Console.WriteLine($"[UDP-VIDEO-SERVER] ✅ Started relaying packet #{message.PacketNumber} ({message.TotalFragments} fragments)");
                await ServerLogHelper.LogToUDPVideoAsync($"✅ Started relaying packet #{message.PacketNumber} ({message.TotalFragments} fragments)");
            }
        }

        private async Task HandleEndSession(string peer1, string peer2)
        {
            var sessionKey1 = $"{peer1}↔{peer2}";
            var sessionKey2 = $"{peer2}↔{peer1}";

            if (_activeSessions.TryRemove(sessionKey1, out var session) ||
                _activeSessions.TryRemove(sessionKey2, out session))
            {
                LogEvent?.Invoke($"[UDP-VIDEO] 📴 Ended video session: {session.Peer1}↔{session.Peer2} (relayed {session.VideoPacketsRelayed} packets)");
            }
        }

        private async Task SendUdpMessage(UDPVideoMessage message, IPEndPoint endPoint)
        {
            try
            {
                var json = JsonSerializer.Serialize(message);
                var data = Encoding.UTF8.GetBytes(json);
                await _udpServer!.SendAsync(data, endPoint);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Failed to send UDP message to {endPoint}: {ex.Message}");
            }
        }

        public async Task StartVideoSession(string peer1, string peer2)
        {
            if (!_clients.TryGetValue(peer1, out var peer1EndPoint) ||
                !_clients.TryGetValue(peer2, out var peer2EndPoint))
            {
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Cannot start session: peers not registered");
                return;
            }

            await HandleStartSession(peer1, peer2, peer1EndPoint);
        }

        public async Task StopVideoSession(string peer1, string peer2)
        {
            await HandleEndSession(peer1, peer2);
        }

        private async Task PeriodicCleanupAsync()
        {
            while (_isRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(10000, _cancellationTokenSource.Token); // Check every 10 seconds

                    var cutoff = DateTime.UtcNow.AddSeconds(-30); // Consider clients inactive after 30 seconds
                    var inactiveClients = new List<string>();

                    // Find inactive clients
                    foreach (var kvp in _clientLastSeen)
                    {
                        if (kvp.Value < cutoff)
                        {
                            inactiveClients.Add(kvp.Key);
                        }
                    }

                    // Clean up inactive clients
                    foreach (var client in inactiveClients)
                    {
                        _clients.TryRemove(client, out _);
                        _clientLastSeen.TryRemove(client, out _);

                        // End any sessions with this client
                        var sessionsToEnd = _activeSessions.Keys
                            .Where(k => k.Contains(client))
                            .ToList();

                        foreach (var sessionKey in sessionsToEnd)
                        {
                            if (_activeSessions.TryRemove(sessionKey, out var session))
                            {
                                LogEvent?.Invoke($"[UDP-VIDEO] 🧹 Cleaned up inactive session: {sessionKey} (client {client} inactive)");
                                await ServerLogHelper.LogToUDPVideoAsync($"🧹 Cleaned up inactive session: {sessionKey} (client {client} inactive)");
                            }
                        }

                        LogEvent?.Invoke($"[UDP-VIDEO] 🧹 Cleaned up inactive client: {client}");
                        await ServerLogHelper.LogToUDPVideoAsync($"🧹 Cleaned up inactive client: {client}");
                    }
                }
                catch (OperationCanceledException)
                {
                    break; // Normal shutdown
                }
                catch (Exception ex)
                {
                    LogEvent?.Invoke($"[UDP-VIDEO] ❌ Error in cleanup task: {ex.Message}");
                }
            }
        }

        public async Task StopAsync()
        {
            _isRunning = false;
            _cancellationTokenSource.Cancel();

            _udpServer?.Close();
            _udpServer?.Dispose();

            LogEvent?.Invoke("[UDP-VIDEO] 📴 UDP Video Relay stopped");
        }

        public void Dispose()
        {
            StopAsync().Wait(1000);
            _cancellationTokenSource.Dispose();
        }
    }
}