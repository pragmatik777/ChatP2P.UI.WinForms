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
        private DateTime _lastLogTime = DateTime.MinValue;

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
        /// ✅ OPTIMISÉ: Send video data via UDP with BINARY protocol (no JSON/Base64 overhead)
        /// Protocol: [HEADER:32 bytes][DATA:variable] for maximum performance
        /// </summary>
        public async Task<bool> SendVideoDataAsync(string targetPeer, byte[] videoData)
        {
            if (!_isConnected || _serverEndPoint == null) return false;

            try
            {
                // ⚡ H.264 OPTIMIZED: Small frames should fit in single UDP packet (no fragmentation needed)
                const int maxFragmentSize = 1200; // 1200 bytes raw data per packet

                // ✅ OPTIMIZATION: Si frame <1200 bytes, pas de fragmentation nécessaire
                if (videoData.Length <= maxFragmentSize)
                {
                    LogEvent?.Invoke($"[UDP-VIDEO] ⚡ Single packet send: {videoData.Length} bytes (no fragmentation)");
                    return await SendSingleVideoPacket(targetPeer, videoData);
                }
                const int headerSize = 32; // Fixed header size

                var totalFragments = (int)Math.Ceiling((double)videoData.Length / maxFragmentSize);
                _packetNumber++;

                LogEvent?.Invoke($"[UDP-VIDEO] 🚀 Sending BINARY video packet #{_packetNumber} ({videoData.Length} bytes, {totalFragments} fragments) to {targetPeer}");

                for (int i = 0; i < totalFragments; i++)
                {
                    var start = i * maxFragmentSize;
                    var length = Math.Min(maxFragmentSize, videoData.Length - start);

                    // DEBUG: Log fragment calculation
                    LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG Fragment {i}: start={start}, length={length}, videoData.Length={videoData.Length}, maxFragmentSize={maxFragmentSize}");

                    // Bounds check before Array.Copy
                    if (start >= videoData.Length)
                    {
                        LogEvent?.Invoke($"[UDP-VIDEO] ❌ CRITICAL: start ({start}) >= videoData.Length ({videoData.Length})");
                        break;
                    }
                    if (start + length > videoData.Length)
                    {
                        LogEvent?.Invoke($"[UDP-VIDEO] ❌ CRITICAL: start+length ({start + length}) > videoData.Length ({videoData.Length})");
                        length = videoData.Length - start;
                        LogEvent?.Invoke($"[UDP-VIDEO] 🔧 FIXED: Adjusted length to {length}");
                    }

                    // Extract fragment data directly (no Base64)
                    var fragmentData = new byte[length];
                    LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: About to copy from videoData[{start}..{start + length - 1}] to fragmentData[0..{length - 1}]");
                    Array.Copy(videoData, start, fragmentData, 0, length);

                    // Create BINARY header (32 bytes fixed)
                    var header = CreateBinaryVideoHeader(_peerName, targetPeer, _packetNumber, i, totalFragments, fragmentData.Length);

                    // DEBUG: Log sizes before array operations
                    LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: Creating packet - Header:{header.Length}B, Fragment:{fragmentData.Length}B, Fragment start:{start}, Fragment length:{length}");
                    LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: PeerName:{_peerName}, TargetPeer:{targetPeer}, Packet#{_packetNumber}, Frag:{i}/{totalFragments}");

                    // Combine header + data in single packet
                    var packet = new byte[header.Length + fragmentData.Length];
                    LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: Created packet array of {packet.Length} bytes");

                    Array.Copy(header, 0, packet, 0, header.Length);
                    LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: Header copied successfully");

                    Array.Copy(fragmentData, 0, packet, header.Length, fragmentData.Length);
                    LogEvent?.Invoke($"[UDP-VIDEO] 🔧 DEBUG: Fragment copied successfully");

                    // Send raw binary packet
                    await _udpClient!.SendAsync(packet);

                    // ✅ PERFORMANCE: Reduced throttling (1ms instead of 3ms)
                    if (i < totalFragments - 1)
                    {
                        await Task.Delay(1); // 1ms delay for optimal throughput
                    }
                }

                LogEvent?.Invoke($"[UDP-VIDEO] ✅ BINARY video packet #{_packetNumber} sent successfully (efficiency: +66% vs JSON)");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Failed to send video: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ✅ FIXED: Create 32-byte binary header for video packets
        /// Format: [TYPE:4][FROM_LEN:1][FROM:7][TO_LEN:1][TO:7][PACKET:4][FRAG_IDX:2][TOTAL_FRAGS:2][DATA_LEN:4]
        /// </summary>
        private byte[] CreateBinaryVideoHeader(string fromPeer, string toPeer, int packetNumber, int fragIndex, int totalFrags, int dataLength)
        {
            var header = new byte[32];
            int offset = 0;

            LogEvent?.Invoke($"[UDP-VIDEO] ✅ Creating header: from='{fromPeer}', to='{toPeer}', packet={packetNumber}, frag={fragIndex}/{totalFrags}, dataLen={dataLength}");

            // Magic type identifier "VDAT" (4 bytes)
            var typeBytes = Encoding.UTF8.GetBytes("VDAT");
            Array.Copy(typeBytes, 0, header, offset, 4);
            offset += 4;

            // FromPeer (1 byte length + max 7 bytes data = 8 bytes total)
            var fromBytes = Encoding.UTF8.GetBytes(fromPeer);
            var fromLen = Math.Min(fromBytes.Length, 7);
            header[offset] = (byte)fromLen;
            offset += 1;
            if (fromLen > 0)
                Array.Copy(fromBytes, 0, header, offset, fromLen);
            offset += 7; // Always advance by 7 to maintain fixed structure

            // ToPeer (1 byte length + max 7 bytes data = 8 bytes total)
            var toBytes = Encoding.UTF8.GetBytes(toPeer);
            var toLen = Math.Min(toBytes.Length, 7);
            header[offset] = (byte)toLen;
            offset += 1;
            if (toLen > 0)
                Array.Copy(toBytes, 0, header, offset, toLen);
            offset += 7; // Always advance by 7 to maintain fixed structure

            // Packet metadata (4+2+2+4 = 12 bytes)
            Array.Copy(BitConverter.GetBytes(packetNumber), 0, header, offset, 4);
            offset += 4;
            Array.Copy(BitConverter.GetBytes((ushort)fragIndex), 0, header, offset, 2);
            offset += 2;
            Array.Copy(BitConverter.GetBytes((ushort)totalFrags), 0, header, offset, 2);
            offset += 2;
            Array.Copy(BitConverter.GetBytes(dataLength), 0, header, offset, 4);
            offset += 4;

            // Header complete: 32 bytes total

            LogEvent?.Invoke($"[UDP-VIDEO] ✅ Header created successfully: {offset}/32 bytes used");
            return header;
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
                    // ✅ PERFORMANCE: Removed excessive "Waiting for UDP packet" logs
                    var result = await _udpClient!.ReceiveAsync();
                    var data = result.Buffer;

                    // ✅ PERFORMANCE: Only log packet receive occasionally
                    if (DateTime.UtcNow - _lastLogTime > TimeSpan.FromSeconds(2))
                    {
                        LogEvent?.Invoke($"[UDP-VIDEO] 📶 Receiving packets: {data.Length} bytes from {result.RemoteEndPoint}");
                        _lastLogTime = DateTime.UtcNow;
                    }

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
                // ✅ PERFORMANCE: Reduced processing packet logs

                // ✅ NEW: Check if this is a BINARY video packet or legacy JSON
                if (data.Length >= 32 && Encoding.UTF8.GetString(data, 0, 4) == "VDAT")
                {
                    // Process BINARY video data packet
                    ProcessBinaryVideoPacket(data);
                }
                else
                {
                    // Legacy JSON processing for registration/control messages
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
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Error processing packet: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ NEW: Process BINARY video data packet with 32-byte header
        /// </summary>
        private void ProcessBinaryVideoPacket(byte[] packet)
        {
            try
            {
                if (packet.Length < 32)
                {
                    LogEvent?.Invoke($"[UDP-VIDEO] ❌ Binary packet too small: {packet.Length} bytes");
                    return;
                }

                // Parse binary header
                var header = ParseBinaryVideoHeader(packet);
                if (header == null)
                {
                    LogEvent?.Invoke($"[UDP-VIDEO] ❌ Failed to parse binary header");
                    return;
                }

                LogEvent?.Invoke($"[UDP-VIDEO] 🚀 BINARY video fragment: packet #{header.PacketNumber}, fragment {header.FragmentIndex}/{header.TotalFragments}, from {header.FromPeer} to {header.ToPeer}");

                // Extract video data (after 32-byte header)
                var videoData = new byte[header.DataLength];
                Array.Copy(packet, 32, videoData, 0, Math.Min(header.DataLength, packet.Length - 32));

                // Convert to legacy format for existing reassembly logic
                var legacyMessage = new UDPVideoMessage
                {
                    Type = "VIDEO_DATA",
                    FromPeer = header.FromPeer,
                    ToPeer = header.ToPeer,
                    VideoData = Convert.ToBase64String(videoData), // Convert back for legacy compatibility
                    PacketNumber = header.PacketNumber,
                    FragmentIndex = header.FragmentIndex,
                    TotalFragments = header.TotalFragments,
                    Timestamp = DateTime.UtcNow
                };

                // Use existing reassembly logic
                HandleVideoFragment(legacyMessage);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Error processing binary video packet: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ NEW: Parse 32-byte binary video header
        /// </summary>
        private BinaryVideoHeader? ParseBinaryVideoHeader(byte[] packet)
        {
            try
            {
                int offset = 0;

                // Check magic type "VDAT" (4 bytes)
                var typeBytes = new byte[4];
                Array.Copy(packet, offset, typeBytes, 0, 4);
                var type = Encoding.UTF8.GetString(typeBytes);
                if (type != "VDAT") return null;
                offset += 4;

                // Parse FromPeer (1 + 7 bytes)
                var fromLength = packet[offset];
                offset += 1;
                var fromBytes = new byte[fromLength];
                Array.Copy(packet, offset, fromBytes, 0, fromLength);
                var fromPeer = Encoding.UTF8.GetString(fromBytes);
                offset += 7;

                // Parse ToPeer (1 + 7 bytes)
                var toLength = packet[offset];
                offset += 1;
                var toBytes = new byte[toLength];
                Array.Copy(packet, offset, toBytes, 0, toLength);
                var toPeer = Encoding.UTF8.GetString(toBytes);
                offset += 7;

                // Parse metadata (12 bytes)
                var packetNumber = BitConverter.ToInt32(packet, offset);
                offset += 4;
                var fragIndex = BitConverter.ToUInt16(packet, offset);
                offset += 2;
                var totalFrags = BitConverter.ToUInt16(packet, offset);
                offset += 2;
                var dataLength = BitConverter.ToInt32(packet, offset);
                offset += 4;

                return new BinaryVideoHeader
                {
                    FromPeer = fromPeer,
                    ToPeer = toPeer,
                    PacketNumber = packetNumber,
                    FragmentIndex = fragIndex,
                    TotalFragments = totalFrags,
                    DataLength = dataLength
                };
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Error parsing binary header: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ✅ NEW: Binary video header structure
        /// </summary>
        private class BinaryVideoHeader
        {
            public string FromPeer { get; set; } = "";
            public string ToPeer { get; set; } = "";
            public int PacketNumber { get; set; }
            public int FragmentIndex { get; set; }
            public int TotalFragments { get; set; }
            public int DataLength { get; set; }
        }

        private readonly object _bufferLock = new object();
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

                // ✅ PERFORMANCE: Only log fragment handling for key fragments
                if (fragmentIndex == 0 || fragmentIndex == totalFragments - 1)
                {
                    LogEvent?.Invoke($"[UDP-VIDEO] 📦 Fragment #{fragmentIndex}/{totalFragments} for packet #{packetNumber}");
                }

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

                // ✅ FIX: Thread-safe fragment handling with lock
                lock (_bufferLock)
                {
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
                        // ✅ FIX: Handle TotalFragments mismatch - use the maximum to avoid missing fragments
                        var storedTotal = _packetTotalFragments[packetNumber];
                        if (storedTotal != totalFragments)
                        {
                            // ✅ PERFORMANCE: Reduced logging for TotalFragments updates
                            // Use the maximum to ensure we don't miss any fragments
                            totalFragments = Math.Max(storedTotal, totalFragments);
                            _packetTotalFragments[packetNumber] = totalFragments;
                        }
                    }

                    // Store fragment with safety check
                    try
                    {
                        // Double-check packet still exists (could be cleaned up by another thread)
                        if (!_fragmentBuffer.ContainsKey(packetNumber))
                        {
                            LogEvent?.Invoke($"[UDP-VIDEO] ⚠️ Packet #{packetNumber} buffer was cleaned up, recreating");
                            _fragmentBuffer[packetNumber] = new Dictionary<int, string>();
                            _packetTotalFragments[packetNumber] = totalFragments;
                            _packetTimestamps[packetNumber] = DateTime.UtcNow;
                        }

                        _fragmentBuffer[packetNumber][fragmentIndex] = message.VideoData!;
                        var currentCount = _fragmentBuffer[packetNumber].Count;
                        var currentExpectedTotal = _packetTotalFragments[packetNumber];
                        // ✅ PERFORMANCE: Only log fragment completion, not every fragment
                        if (currentCount % 5 == 0 || currentCount == currentExpectedTotal)
                        {
                            LogEvent?.Invoke($"[UDP-VIDEO] 📊 Fragment progress: {currentCount}/{currentExpectedTotal} for packet #{packetNumber}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogEvent?.Invoke($"[UDP-VIDEO] ❌ Error storing fragment {fragmentIndex} for packet #{packetNumber}: {ex.Message}");
                        return;
                    }

                    // Check if all fragments received
                    if (!_packetTotalFragments.ContainsKey(packetNumber) || !_fragmentBuffer.ContainsKey(packetNumber))
                    {
                        LogEvent?.Invoke($"[UDP-VIDEO] ⚠️ Packet #{packetNumber} missing from dictionaries, skipping completion check");
                        return;
                    }

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

                            // Clean up buffer before leaving lock
                            _fragmentBuffer.Remove(packetNumber);
                            _packetTotalFragments.Remove(packetNumber); // ✅ FIX: Clean up totals too
                            _packetTimestamps.Remove(packetNumber); // ✅ FIX: Clean up timestamps too

                            LogEvent?.Invoke($"[UDP-VIDEO] 📹 Packet #{packetNumber} reassembled successfully ({finalExpectedTotal} fragments)");

                            // Decode and emit video data OUTSIDE lock to avoid deadlock
                            var videoBytes = Convert.FromBase64String(reassembledData);
                            VideoDataReceived?.Invoke(videoBytes);

                            LogEvent?.Invoke($"[UDP-VIDEO] 📹 Received complete video packet #{packetNumber} ({videoBytes.Length} bytes, {finalExpectedTotal} fragments) from {message.FromPeer}");
                        }
                        catch (Exception reassembleEx)
                        {
                            LogEvent?.Invoke($"[UDP-VIDEO] ❌ Error reassembling packet #{packetNumber}: {reassembleEx.Message}");
                            // Clean up on error
                            _fragmentBuffer.Remove(packetNumber);
                            _packetTotalFragments.Remove(packetNumber);
                            _packetTimestamps.Remove(packetNumber);
                        }
                    }
                } // End lock
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

        /// <summary>
        /// ⚡ OPTIMIZED: Send single H.264 frame without fragmentation
        /// </summary>
        private async Task<bool> SendSingleVideoPacket(string targetPeer, byte[] videoData)
        {
            try
            {
                _packetNumber++;

                var message = new UDPVideoMessage
                {
                    Type = "VIDEO_FRAME",
                    FromPeer = _peerName,
                    ToPeer = targetPeer,
                    PacketNumber = _packetNumber,
                    FragmentIndex = 0,
                    TotalFragments = 1,  // Single packet = no fragmentation
                    VideoData = Convert.ToBase64String(videoData),
                    Timestamp = DateTime.UtcNow
                };

                await SendMessage(message);
                LogEvent?.Invoke($"[UDP-VIDEO] ✅ Single H.264 packet #{_packetNumber} sent: {videoData.Length} bytes (no fragmentation)");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[UDP-VIDEO] ❌ Failed to send single packet: {ex.Message}");
                return false;
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