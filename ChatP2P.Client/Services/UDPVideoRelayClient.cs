using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// UDP Video Relay Client - Real-time video transmission
    /// Connects to UDP port 8894 for minimal latency video streaming
    /// </summary>
    public class UDPVideoRelayClient : IDisposable
    {
        private UdpClient? _udpClient;
        private IPEndPoint? _serverEndPoint;
        private string _peerName = "";
        private bool _isConnected = false;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private int _packetNumber = 0;

        public event Action<string>? LogEvent;
        public event Action<byte[]>? VideoDataReceived;

        public bool IsConnected => _isConnected;

        /// <summary>
        /// Connect to UDP video relay server
        /// </summary>
        public async Task<bool> ConnectAsync(string serverIP, string peerName)
        {
            try
            {
                _peerName = peerName;
                _serverEndPoint = new IPEndPoint(IPAddress.Parse(serverIP), 8894);

                // ✅ FIX CRITIQUE: Bind to local endpoint so server can send back to us
                var localEndPoint = new IPEndPoint(IPAddress.Any, 0); // Let system choose port
                _udpClient = new UdpClient(localEndPoint);

                LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: Created UdpClient bound to local endpoint: {_udpClient.Client.LocalEndPoint}");

                // Connect to server endpoint for receiving responses
                _udpClient.Connect(_serverEndPoint);

                LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: Connected UdpClient to server endpoint: {_serverEndPoint}");

                // Register with server
                var registerMessage = new UDPVideoMessage
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
                LogEvent?.Invoke($"[UDP-VIDEO] ✅ Connected {peerName} to UDP video relay {serverIP}:8894");

                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Connection failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Start video session with another peer
        /// </summary>
        public async Task<bool> StartSessionAsync(string targetPeer)
        {
            if (!_isConnected || _serverEndPoint == null) return false;

            try
            {
                var startMessage = new UDPVideoMessage
                {
                    Type = "START_SESSION",
                    FromPeer = _peerName,
                    ToPeer = targetPeer,
                    Timestamp = DateTime.UtcNow
                };

                await SendMessage(startMessage);
                LogEvent?.Invoke($"[UDP-VIDEO] 📹 Starting video session with {targetPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Failed to start session: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send video data via UDP with fragmentation support for large frames
        /// </summary>
        public async Task<bool> SendVideoDataAsync(string targetPeer, byte[] videoData)
        {
            if (!_isConnected || _serverEndPoint == null) return false;

            try
            {
                var base64Data = Convert.ToBase64String(videoData);
                const int maxFragmentSize = 500; // ✅ FIX: Reduced to 500B per fragment to minimize UDP packet loss

                var totalFragments = (int)Math.Ceiling((double)base64Data.Length / maxFragmentSize);
                _packetNumber++;

                for (int i = 0; i < totalFragments; i++)
                {
                    var start = i * maxFragmentSize;
                    var length = Math.Min(maxFragmentSize, base64Data.Length - start);
                    var fragmentData = base64Data.Substring(start, length);

                    var videoMessage = new UDPVideoMessage
                    {
                        Type = "VIDEO_DATA",
                        FromPeer = _peerName,
                        ToPeer = targetPeer,
                        VideoData = fragmentData,
                        PacketNumber = _packetNumber,
                        FragmentIndex = i,
                        TotalFragments = totalFragments,
                        Timestamp = DateTime.UtcNow
                    };

                    await SendMessage(videoMessage);

                    // ✅ FIX CRITIQUE: Add throttling to prevent UDP packet loss
                    if (i < totalFragments - 1) // Don't delay after last fragment
                    {
                        await Task.Delay(3); // 3ms delay between fragments to reduce UDP saturation
                    }
                }

                LogEvent?.Invoke($"[UDP-VIDEO] 📹 Sent video packet #{_packetNumber} ({videoData.Length} bytes, {totalFragments} fragments) to {targetPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Failed to send video: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// End video session
        /// </summary>
        public async Task EndSessionAsync(string targetPeer)
        {
            if (!_isConnected || _serverEndPoint == null) return;

            try
            {
                var endMessage = new UDPVideoMessage
                {
                    Type = "END_SESSION",
                    FromPeer = _peerName,
                    ToPeer = targetPeer,
                    Timestamp = DateTime.UtcNow
                };

                await SendMessage(endMessage);
                LogEvent?.Invoke($"[UDP-VIDEO] 📴 Ended video session with {targetPeer}");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Failed to end session: {ex.Message}");
            }
        }

        private async Task SendMessage(UDPVideoMessage message)
        {
            if (_udpClient == null) return;

            var json = JsonSerializer.Serialize(message);
            var data = Encoding.UTF8.GetBytes(json);

            // ✅ FIX: Use connected UDP client (no need to specify endpoint)
            await _udpClient.SendAsync(data);
        }

        private async Task ListenForPacketsAsync()
        {
            LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: Starting UDP listen loop for {_peerName}");

            while (_isConnected && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: Waiting for UDP packet...");
                    var result = await _udpClient!.ReceiveAsync();
                    var data = result.Buffer;

                    LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: Received UDP packet: {data.Length} bytes from {result.RemoteEndPoint}");

                    // Process packet in background
                    _ = Task.Run(() => ProcessReceivedPacket(data));
                }
                catch (Exception ex) when (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    LogEvent?.Invoke($"[UDP-VIDEO] ❌ Error receiving packet: {ex.Message}");
                }
            }

            LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: UDP listen loop ended for {_peerName}");
        }

        private void ProcessReceivedPacket(byte[] data)
        {
            try
            {
                LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: Processing packet of {data.Length} bytes");

                var json = Encoding.UTF8.GetString(data);
                LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: JSON content: {json.Substring(0, Math.Min(100, json.Length))}...");

                var message = JsonSerializer.Deserialize<UDPVideoMessage>(json);

                if (message == null)
                {
                    LogEvent?.Invoke($"[UDP-VIDEO] ❌ Failed to deserialize message");
                    return;
                }

                LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: Message type: {message.Type}, From: {message.FromPeer}, To: {message.ToPeer}");

                switch (message.Type)
                {
                    case "REGISTER_CONFIRM":
                        LogEvent?.Invoke($"[UDP-VIDEO] ✅ Registration confirmed by server");
                        break;

                    case "SESSION_STARTED":
                        LogEvent?.Invoke($"[UDP-VIDEO] 📹 Video session started");
                        break;

                    case "VIDEO_DATA":
                        LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: VIDEO_DATA packet - VideoData null: {string.IsNullOrEmpty(message.VideoData)}");
                        if (!string.IsNullOrEmpty(message.VideoData))
                        {
                            LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: About to handle video fragment packet #{message.PacketNumber}, fragment {message.FragmentIndex}/{message.TotalFragments}");
                            // Handle fragmented video data
                            HandleVideoFragment(message);
                        }
                        else
                        {
                            LogEvent?.Invoke($"[UDP-VIDEO] ⚠️ VIDEO_DATA packet has no VideoData content");
                        }
                        break;

                    default:
                        LogEvent?.Invoke($"[UDP-VIDEO] ⚠️ Unknown message type: {message.Type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Error processing packet: {ex.Message}");
            }
        }

        private readonly Dictionary<int, Dictionary<int, string>> _fragmentBuffer = new();
        private readonly Dictionary<int, int> _packetTotalFragments = new(); // ✅ FIX: Track expected total fragments per packet
        private readonly Dictionary<int, DateTime> _packetTimestamps = new(); // ✅ FIX: Track packet timestamps for cleanup

        private void HandleVideoFragment(UDPVideoMessage message)
        {
            try
            {
                var packetNumber = message.PacketNumber;
                var fragmentIndex = message.FragmentIndex;
                var totalFragments = message.TotalFragments;

                LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: HandleVideoFragment - packet #{packetNumber}, fragment {fragmentIndex}/{totalFragments}");

                // ✅ FIX: Validate fragment data with enhanced checks
                if (fragmentIndex < 0)
                {
                    LogEvent?.Invoke($"[UDP-VIDEO] ❌ Negative fragment index {fragmentIndex}, skipping");
                    return;
                }

                if (fragmentIndex >= totalFragments)
                {
                    LogEvent?.Invoke($"[UDP-VIDEO] ❌ Fragment index {fragmentIndex} >= totalFragments {totalFragments}, skipping");
                    return;
                }

                // Additional safety check for corrupted data
                if (fragmentIndex >= 500) // Reasonable upper limit
                {
                    LogEvent?.Invoke($"[UDP-VIDEO] ❌ Fragment index {fragmentIndex} too large (possible corruption), skipping");
                    return;
                }

                if (string.IsNullOrEmpty(message.VideoData))
                {
                    LogEvent?.Invoke($"[UDP-VIDEO] ❌ Empty fragment data for packet #{packetNumber}, fragment {fragmentIndex}");
                    return;
                }

                // ✅ FIX: Clean up old incomplete packets (older than 5 seconds)
                CleanupOldPackets();

                // Initialize packet buffer if needed
                if (!_fragmentBuffer.ContainsKey(packetNumber))
                {
                    _fragmentBuffer[packetNumber] = new Dictionary<int, string>();
                    _packetTotalFragments[packetNumber] = totalFragments; // ✅ FIX: Store expected total
                    _packetTimestamps[packetNumber] = DateTime.UtcNow;
                    LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: Created new buffer for packet #{packetNumber} expecting {totalFragments} fragments");
                }
                else
                {
                    // ✅ FIX: Be more flexible with TotalFragments - allow small differences due to corruption
                    var storedTotal = _packetTotalFragments[packetNumber];
                    if (Math.Abs(storedTotal - totalFragments) <= 5) // Allow up to 5 fragment difference
                    {
                        if (storedTotal != totalFragments)
                        {
                            LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: Minor TotalFragments variance for packet #{packetNumber}: expected {storedTotal}, got {totalFragments} (within tolerance)");
                        }
                        // Use the smaller of the two to avoid waiting for non-existent fragments
                        totalFragments = Math.Min(storedTotal, totalFragments);
                        _packetTotalFragments[packetNumber] = totalFragments;
                    }
                    else
                    {
                        LogEvent?.Invoke($"[UDP-VIDEO] ⚠️ Major TotalFragments mismatch for packet #{packetNumber}: expected {storedTotal}, got {totalFragments} - using expected");
                        totalFragments = storedTotal;
                    }
                }

                // Store fragment with safety check
                try
                {
                    _fragmentBuffer[packetNumber][fragmentIndex] = message.VideoData!;
                    var currentCount = _fragmentBuffer[packetNumber].Count;
                    var currentExpectedTotal = _packetTotalFragments[packetNumber];
                    LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: Stored fragment {fragmentIndex} for packet #{packetNumber}. Current count: {currentCount}/{currentExpectedTotal}");
                }
                catch (Exception ex)
                {
                    LogEvent?.Invoke($"[UDP-VIDEO] ❌ Error storing fragment {fragmentIndex} for packet #{packetNumber}: {ex.Message}");
                    return;
                }

                // Check if all fragments received
                var finalExpectedTotal = _packetTotalFragments[packetNumber];
                if (_fragmentBuffer[packetNumber].Count == finalExpectedTotal)
                {
                    LogEvent?.Invoke($"[UDP-VIDEO] 🎉 DEBUG: All fragments received for packet #{packetNumber}! Starting reassembly...");
                    // Reassemble video data with safety check
                    var reassembledData = "";
                    try
                    {
                        for (int i = 0; i < finalExpectedTotal; i++)
                        {
                            if (_fragmentBuffer[packetNumber].ContainsKey(i))
                            {
                                reassembledData += _fragmentBuffer[packetNumber][i];
                            }
                            else
                            {
                                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Missing fragment {i} for packet #{packetNumber}, cannot reassemble");
                                _fragmentBuffer.Remove(packetNumber);
                                _packetTotalFragments.Remove(packetNumber);
                                _packetTimestamps.Remove(packetNumber);
                                return;
                            }
                        }

                        // Decode and emit video data
                        var videoBytes = Convert.FromBase64String(reassembledData);
                        VideoDataReceived?.Invoke(videoBytes);

                        LogEvent?.Invoke($"[UDP-VIDEO] 📹 Received complete video packet #{packetNumber} ({videoBytes.Length} bytes, {finalExpectedTotal} fragments) from {message.FromPeer}");
                    }
                    catch (Exception reassembleEx)
                    {
                        LogEvent?.Invoke($"[UDP-VIDEO] ❌ Error reassembling packet #{packetNumber}: {reassembleEx.Message}");
                    }

                    // Clean up buffer
                    _fragmentBuffer.Remove(packetNumber);
                    _packetTotalFragments.Remove(packetNumber); // ✅ FIX: Clean up totals too
                    _packetTimestamps.Remove(packetNumber); // ✅ FIX: Clean up timestamps too
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Error handling video fragment: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            _isConnected = false;
            _cancellationTokenSource.Cancel();

            _udpClient?.Close();
            _udpClient?.Dispose();
            _udpClient = null;

            LogEvent?.Invoke($"[UDP-VIDEO] 📴 Disconnected {_peerName} from UDP video relay");
        }

        public void Dispose()
        {
            Disconnect();
            _cancellationTokenSource.Dispose();
        }

        /// <summary>
        /// ✅ FIX: Clean up old incomplete packets to prevent memory leaks
        /// </summary>
        private void CleanupOldPackets()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-5); // Remove packets older than 5 seconds
            var toRemove = new List<int>();

            foreach (var kvp in _packetTimestamps)
            {
                if (kvp.Value < cutoff)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var packetNumber in toRemove)
            {
                var fragmentCount = _fragmentBuffer.ContainsKey(packetNumber) ? _fragmentBuffer[packetNumber].Count : 0;
                var expectedTotal = _packetTotalFragments.ContainsKey(packetNumber) ? _packetTotalFragments[packetNumber] : 0;

                LogEvent?.Invoke($"[UDP-VIDEO] 🗑️ Cleaned up incomplete packet #{packetNumber} ({fragmentCount}/{expectedTotal} fragments received)");

                _fragmentBuffer.Remove(packetNumber);
                _packetTotalFragments.Remove(packetNumber);
                _packetTimestamps.Remove(packetNumber);
            }
        }

        private class UDPVideoMessage
        {
            public string Type { get; set; } = "";
            public string FromPeer { get; set; } = "";
            public string ToPeer { get; set; } = "";
            public string? VideoData { get; set; }
            public int PacketNumber { get; set; }
            public int FragmentIndex { get; set; } = 0;
            public int TotalFragments { get; set; } = 1;
            public DateTime Timestamp { get; set; }
        }
    }
}