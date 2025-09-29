using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Linq;

namespace ChatP2P.Client
{
    /// <summary>
    /// ✅ NOUVEAU: Client WebRTC décentralisé pour connexions P2P directes
    /// Remplace la logique VB.NET supprimée - établit des DataChannels directs entre peers
    /// Le serveur ne fait QUE du signaling relay
    /// </summary>
    public class WebRTCDirectClient
    {
        private readonly string _clientId;
        private readonly Dictionary<string, RTCPeerConnection> _peerConnections = new();
        private readonly Dictionary<string, RTCDataChannel> _messageChannels = new(); // Messages texte
        private readonly Dictionary<string, RTCDataChannel> _dataChannels = new();    // Transfert fichiers

        // 🔧 MESSAGE FRAGMENTATION SYSTEM - Fix for WebRTC size limits
        private readonly Dictionary<string, Dictionary<string, List<MessageFragment>>> _fragmentBuffers = new();
        private readonly object _fragmentLock = new object();
        private const int MAX_MESSAGE_SIZE = 16384; // 16KB max per WebRTC chunk

        // Structure pour gérer les fragments de messages
        private class MessageFragment
        {
            public string MessageId { get; set; } = "";
            public int ChunkIndex { get; set; }
            public int TotalChunks { get; set; }
            public string Data { get; set; } = "";
            public DateTime Timestamp { get; set; }
        }

        // Events pour communication avec MainWindow
        public event Action<string, string>? MessageReceived; // peer, message
        public event Action<string, bool>? ConnectionStatusChanged; // peer, connected
        public event Action<string>? LogEvent;
        public event Action<string, string, string>? ICECandidateGenerated; // fromPeer, toPeer, candidate
        public event Action<string, byte[]>? FileDataReceived; // peer, binaryData
        public event Action<string, double, string>? FileTransferProgress; // peer, progressPercent, fileName

        // ✅ OPTIMISATIONS 2025: Flow control constants pour haute performance (>10Mbps)
        private const ulong BUFFER_THRESHOLD = 1048576UL; // 1MB buffer limit (4x plus agressif)
        private const ulong LOW_BUFFER_THRESHOLD = 262144UL; // 256KB low threshold (8x plus agressif)
        private const int MAX_CHUNK_SIZE = 65536; // 64KB max chunk size (4x plus gros chunks)
        private const int FLOW_CONTROL_DELAY = 1; // 1ms base delay (10x plus rapide)
        private const int BURST_SIZE = 5; // Send 5 chunks then micro-pause pour flow control

        // File reconstruction for chunked transfers
        private readonly Dictionary<string, FileReconstructionState> _fileReconstructions = new();

        private readonly RTCConfiguration _rtcConfig;

        public WebRTCDirectClient(string clientId)
        {
            _clientId = clientId;

            // 🔧 SIMPLIFIED ICE CONFIG - Fix for VM environment SCTP issues
            _rtcConfig = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    // Local network fallback for VMs
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                },
                // 🔧 VM-FRIENDLY: Disable problematic features for VM environments
                iceTransportPolicy = RTCIceTransportPolicy.all,
                bundlePolicy = RTCBundlePolicy.balanced
            };

            LogEvent?.Invoke($"[WebRTC-DIRECT] Initialized for client: {_clientId}");

            // Start cleanup timer for old fragments
            _ = Task.Run(CleanupOldFragments);
        }

        /// <summary>
        /// 🧹 CLEANUP - Remove old incomplete fragment buffers
        /// </summary>
        private async Task CleanupOldFragments()
        {
            while (true)
            {
                try
                {
                    await Task.Delay(60000); // Check every minute

                    lock (_fragmentLock)
                    {
                        var cutoffTime = DateTime.Now.AddMinutes(-5); // 5 minute timeout
                        var toRemove = new List<(string peer, string messageId)>();

                        foreach (var peerBuffer in _fragmentBuffers)
                        {
                            foreach (var messageBuffer in peerBuffer.Value)
                            {
                                var oldestFragment = messageBuffer.Value.MinBy(f => f.Timestamp);
                                if (oldestFragment?.Timestamp < cutoffTime)
                                {
                                    toRemove.Add((peerBuffer.Key, messageBuffer.Key));
                                }
                            }
                        }

                        foreach (var (peer, messageId) in toRemove)
                        {
                            _fragmentBuffers[peer].Remove(messageId);
                            LogEvent?.Invoke($"[WebRTC-FRAG] 🧹 Cleaned up old incomplete message {messageId} from {peer}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogEvent?.Invoke($"[WebRTC-FRAG] ❌ Error during fragment cleanup: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Créer une offer WebRTC pour établir une connexion directe avec un peer
        /// </summary>
        public async Task<string?> CreateOfferAsync(string targetPeer)
        {
            try
            {
                LogEvent?.Invoke($"[WebRTC-DIRECT] Creating offer for: {targetPeer}");

                // 🔧 VM-SAFE: Try full WebRTC sequence with fallback for SCTP issues
                RTCPeerConnection pc;
                bool usedFallback = false;

                try
                {
                    pc = new RTCPeerConnection(_rtcConfig);
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ✅ PeerConnection created successfully with standard config");
                }
                catch (Exception sctpEx)
                {
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ⚠️ Standard config failed (SCTP issue): {sctpEx.Message}");
                    LogEvent?.Invoke($"[WebRTC-DIRECT] 🔧 Trying minimal fallback config for VM environment");

                    // Fallback: Minimal config for VMs
                    var fallbackConfig = new RTCConfiguration
                    {
                        iceServers = new List<RTCIceServer>(),  // No STUN for local testing
                        iceTransportPolicy = RTCIceTransportPolicy.all
                    };
                    pc = new RTCPeerConnection(fallbackConfig);
                    usedFallback = true;
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ✅ PeerConnection created with fallback config");
                }

                _peerConnections[targetPeer] = pc;

                // 🔧 VM-SAFE: Try DataChannel creation with SCTP fallback
                try
                {

                // Setup ICE candidate events
                pc.onicecandidate += (candidate) =>
                {
                    if (candidate != null)
                    {
                        LogEvent?.Invoke($"[WebRTC-DIRECT] ICE candidate for {targetPeer}: {candidate.candidate}");
                        // ✅ FIX: Envoyer le candidate au peer via MainWindow
                        ICECandidateGenerated?.Invoke(_clientId, targetPeer, candidate.candidate ?? "");
                    }
                };

                // ✅ FIX: Créer DUAL DataChannels (SIPSorcery best practice)

                // Canal 1: Messages texte (ordered, reliable)
                var msgConfig = new RTCDataChannelInit
                {
                    ordered = true,
                    maxRetransmits = null // Reliable delivery
                };
                var messageChannel = pc.createDataChannel("messages", msgConfig).Result;

                // ✅ OPTIMISATION 2025: Configurer bufferedAmountLowThreshold pour messages aussi
                messageChannel.bufferedAmountLowThreshold = LOW_BUFFER_THRESHOLD;

                _messageChannels[targetPeer] = messageChannel;
                LogEvent?.Invoke($"[WebRTC-DIRECT] ✅ Message DataChannel created with optimized buffer thresholds (state: {messageChannel.readyState})");

                // Canal 2: Données fichiers (unordered, performance)
                var dataConfig = new RTCDataChannelInit
                {
                    ordered = false,
                    maxRetransmits = 3 // Limited retries for performance
                };
                var dataChannel = pc.createDataChannel("data", dataConfig).Result;

                // ✅ OPTIMISATION 2025: Configurer bufferedAmountLowThreshold pour haute performance
                dataChannel.bufferedAmountLowThreshold = LOW_BUFFER_THRESHOLD;

                _dataChannels[targetPeer] = dataChannel;
                LogEvent?.Invoke($"[WebRTC-DIRECT] ✅ Data DataChannel created with optimized buffer thresholds (state: {dataChannel.readyState})");

                // ✅ FIX: Event handlers pour MESSAGE channel
                WireMessageChannelEvents(messageChannel, targetPeer);

                // ✅ FIX: Event handlers pour DATA channel (fichiers)
                WireDataChannelEvents(dataChannel, targetPeer);

                // ✅ FIX AGRESSIF: Polling pour les DEUX canaux
                MonitorChannelsAsync(targetPeer, messageChannel, dataChannel);

                // ✅ FIX CRITIQUE: Attendre que ICE gathering soit prêt avant de créer l'offer
                pc.onicegatheringstatechange += (state) =>
                {
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ICE gathering state changed for {targetPeer}: {state}");
                };

                // Créer offer
                var offer = pc.createOffer();
                var setResult = pc.setLocalDescription(offer);
                LogEvent?.Invoke($"[WebRTC-DIRECT] setLocalDescription result for {targetPeer}: {setResult}");

                // Note: SIPSorcery setLocalDescription est synchrone

                var offerJson = new
                {
                    type = "offer",
                    sdp = offer.sdp
                };

                    var jsonString = JsonSerializer.Serialize(offerJson);
                    LogEvent?.Invoke($"[WebRTC-DIRECT] Offer created for {targetPeer} (SDP length: {offer.sdp?.Length ?? 0})");
                    LogEvent?.Invoke($"[WebRTC-DIRECT] Offer SDP preview: {offer.sdp?.Substring(0, Math.Min(200, offer.sdp?.Length ?? 0))}...");
                    return jsonString;
                }
                catch (Exception sctpDataEx)
                {
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ⚠️ SCTP/DataChannel error for {targetPeer}: {sctpDataEx.Message}");

                    if (usedFallback)
                    {
                        LogEvent?.Invoke($"[WebRTC-DIRECT] ❌ Even fallback config failed - VM environment incompatible");
                        return null;
                    }

                    LogEvent?.Invoke($"[WebRTC-DIRECT] 🔧 Trying simplified approach without DataChannels");

                    // Cleanup failed connection
                    if (_peerConnections.TryGetValue(targetPeer, out var failedPc))
                    {
                        failedPc.close();
                        _peerConnections.Remove(targetPeer);
                    }

                    // For VOIP, we might not need DataChannels - just return null to trigger TCP relay fallback
                    LogEvent?.Invoke($"[WebRTC-DIRECT] 💡 Suggesting TCP relay fallback for {targetPeer}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-DIRECT] ❌ Error creating offer for {targetPeer}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Traiter une offer reçue et créer une answer
        /// </summary>
        public async Task<string?> ProcessOfferAsync(string fromPeer, string offerSdp)
        {
            try
            {
                LogEvent?.Invoke($"[WebRTC-DIRECT] Processing offer from: {fromPeer}");

                // Créer nouvelle PeerConnection
                var pc = new RTCPeerConnection(_rtcConfig);
                _peerConnections[fromPeer] = pc;

                // Setup ICE candidate events
                pc.onicecandidate += (candidate) =>
                {
                    if (candidate != null)
                    {
                        LogEvent?.Invoke($"[WebRTC-DIRECT] ICE candidate for {fromPeer}: {candidate.candidate}");
                        // ✅ FIX: Envoyer le candidate au peer via MainWindow (receiver side)
                        ICECandidateGenerated?.Invoke(_clientId, fromPeer, candidate.candidate ?? "");
                    }
                };

                // ✅ FIX: Event handler pour DataChannels entrants (DUAL)
                pc.ondatachannel += (dc) =>
                {
                    LogEvent?.Invoke($"[WebRTC-DIRECT] DataChannel received from {fromPeer}: {dc.label}");

                    if (dc.label == "messages")
                    {
                        _messageChannels[fromPeer] = dc;
                        WireMessageChannelEvents(dc, fromPeer);
                        LogEvent?.Invoke($"[WebRTC-DIRECT] Message channel wired for {fromPeer}");
                    }
                    else if (dc.label == "data")
                    {
                        _dataChannels[fromPeer] = dc;
                        WireDataChannelEvents(dc, fromPeer);
                        LogEvent?.Invoke($"[WebRTC-DIRECT] Data channel wired for {fromPeer}");
                    }

                    // Monitor channel opening
                    MonitorSingleChannelAsync(fromPeer, dc);
                };

                // Set remote description (offer)
                var offer = new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.offer,
                    sdp = offerSdp
                };
                var setRemoteResult = pc.setRemoteDescription(offer);
                // Note: SIPSorcery setRemoteDescription est synchrone

                // ✅ FIX CRITIQUE: Setup ICE monitoring pour receiver
                pc.onicegatheringstatechange += (state) =>
                {
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ICE gathering state changed for {fromPeer}: {state}");
                };

                pc.onconnectionstatechange += (state) =>
                {
                    LogEvent?.Invoke($"[WebRTC-DIRECT] Connection state changed for {fromPeer}: {state}");
                };

                // Créer answer
                var answer = pc.createAnswer();
                var setLocalResult = pc.setLocalDescription(answer);
                LogEvent?.Invoke($"[WebRTC-DIRECT] setLocalDescription result for {fromPeer}: {setLocalResult}");

                // Note: SIPSorcery setLocalDescription est synchrone

                var answerJson = new
                {
                    type = "answer",
                    sdp = answer.sdp
                };

                var jsonString = JsonSerializer.Serialize(answerJson);
                LogEvent?.Invoke($"[WebRTC-DIRECT] Answer created for {fromPeer} (SDP length: {answer.sdp?.Length ?? 0})");
                LogEvent?.Invoke($"[WebRTC-DIRECT] Answer SDP preview: {answer.sdp?.Substring(0, Math.Min(200, answer.sdp?.Length ?? 0))}...");
                return jsonString;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-DIRECT] ❌ Error processing offer from {fromPeer}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Traiter une answer reçue
        /// </summary>
        public async Task<bool> ProcessAnswerAsync(string fromPeer, string answerSdp)
        {
            try
            {
                LogEvent?.Invoke($"[WebRTC-DIRECT] Processing answer from: {fromPeer}");

                if (!_peerConnections.TryGetValue(fromPeer, out var pc))
                {
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ❌ No peer connection for {fromPeer}");
                    return false;
                }

                var answer = new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.answer,
                    sdp = answerSdp
                };
                var setRemoteResult = pc.setRemoteDescription(answer);
                LogEvent?.Invoke($"[WebRTC-DIRECT] setRemoteDescription answer result for {fromPeer}: {setRemoteResult}");

                // ✅ FIX CRITIQUE: Setup connection monitoring après answer
                pc.onconnectionstatechange += (state) =>
                {
                    LogEvent?.Invoke($"[WebRTC-DIRECT] Connection state changed for {fromPeer}: {state}");
                    if (state == RTCPeerConnectionState.connected)
                    {
                        LogEvent?.Invoke($"[WebRTC-DIRECT] 🎉 PeerConnection CONNECTED with {fromPeer}!");
                    }
                };

                // Note: SIPSorcery setRemoteDescription est synchrone

                LogEvent?.Invoke($"[WebRTC-DIRECT] ✅ Answer processed from {fromPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-DIRECT] ❌ Error processing answer from {fromPeer}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Traiter un candidat ICE reçu
        /// </summary>
        public async Task<bool> ProcessCandidateAsync(string fromPeer, string candidateData)
        {
            try
            {
                if (!_peerConnections.TryGetValue(fromPeer, out var pc))
                {
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ❌ No peer connection for {fromPeer}");
                    return false;
                }

                // ✅ FIX: Parse JSON-wrapped candidate data or handle raw candidate string
                string actualCandidateString;
                try
                {
                    // Try to parse as JSON first (new format)
                    var candidateJson = JsonSerializer.Deserialize<JsonElement>(candidateData);
                    if (candidateJson.TryGetProperty("candidate", out var candidateProp))
                    {
                        actualCandidateString = candidateProp.GetString() ?? candidateData;
                    }
                    else
                    {
                        actualCandidateString = candidateData; // Fallback to raw string
                    }
                }
                catch
                {
                    // If JSON parsing fails, assume it's a raw candidate string (old format)
                    actualCandidateString = candidateData;
                }

                var candidateInit = new RTCIceCandidateInit
                {
                    candidate = actualCandidateString
                };
                pc.addIceCandidate(candidateInit);
                // Note: SIPSorcery addIceCandidate est synchrone

                LogEvent?.Invoke($"[WebRTC-DIRECT] ✅ ICE candidate added for {fromPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-DIRECT] ❌ Error adding ICE candidate from {fromPeer}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Envoyer un message texte via Message DataChannel (pas Data channel)
        /// </summary>
        public async Task<bool> SendMessageAsync(string targetPeer, string message)
        {
            try
            {
                if (!_messageChannels.TryGetValue(targetPeer, out var msgChannel))
                {
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ❌ No message channel for {targetPeer}");
                    return false;
                }

                if (msgChannel.readyState != RTCDataChannelState.open)
                {
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ❌ Message channel not open for {targetPeer} (state: {msgChannel.readyState})");
                    return false;
                }

                // ✅ FIX: Buffer monitoring avant envoi (best practice SIPSorcery)
                if (msgChannel.bufferedAmount > BUFFER_THRESHOLD)
                {
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ⚠️ Message buffer high: {msgChannel.bufferedAmount} bytes - waiting");
                    await WaitForBufferLow(msgChannel, targetPeer);
                }

                // 🔧 MESSAGE FRAGMENTATION - Handle large messages (VOIP SDP, etc.)
                var messageBytes = Encoding.UTF8.GetBytes(message);
                LogEvent?.Invoke($"[WebRTC-DIRECT] 🚀 Sending {messageBytes.Length} bytes to {targetPeer} via Message Channel");

                if (messageBytes.Length <= MAX_MESSAGE_SIZE)
                {
                    // Single message - send directly
                    msgChannel.send(messageBytes);
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ✅ Single message sent to {targetPeer}: {message.Substring(0, Math.Min(50, message.Length))}...");
                }
                else
                {
                    // Large message - fragment and send
                    await SendFragmentedMessageAsync(msgChannel, targetPeer, message);
                }

                LogEvent?.Invoke($"[WebRTC-DIRECT] 📊 Buffer after send: {msgChannel.bufferedAmount} bytes");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-DIRECT] ❌ Error sending message to {targetPeer}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 🔧 FRAGMENTATION - Envoyer un message volumineux en fragments
        /// </summary>
        private async Task SendFragmentedMessageAsync(RTCDataChannel channel, string targetPeer, string message)
        {
            var messageId = Guid.NewGuid().ToString("N")[..8];
            var messageBytes = Encoding.UTF8.GetBytes(message);
            var chunks = new List<string>();

            // Split message into chunks
            for (int i = 0; i < messageBytes.Length; i += MAX_MESSAGE_SIZE - 200) // Reserve space for headers
            {
                var chunkSize = Math.Min(MAX_MESSAGE_SIZE - 200, messageBytes.Length - i);
                var chunkData = Convert.ToBase64String(messageBytes, i, chunkSize);
                chunks.Add(chunkData);
            }

            LogEvent?.Invoke($"[WebRTC-FRAG] 📦 Fragmenting large message ({messageBytes.Length} bytes) into {chunks.Count} chunks for {targetPeer}");

            // Send each fragment
            for (int i = 0; i < chunks.Count; i++)
            {
                var fragmentJson = JsonSerializer.Serialize(new
                {
                    type = "fragment",
                    messageId = messageId,
                    chunkIndex = i,
                    totalChunks = chunks.Count,
                    data = chunks[i]
                });

                var fragmentBytes = Encoding.UTF8.GetBytes(fragmentJson);

                // Wait for buffer if needed
                if (channel.bufferedAmount > BUFFER_THRESHOLD)
                {
                    await WaitForBufferLow(channel, targetPeer);
                }

                channel.send(fragmentBytes);
                LogEvent?.Invoke($"[WebRTC-FRAG] 📮 Sent fragment {i + 1}/{chunks.Count} to {targetPeer}");

                // Small delay between fragments
                await Task.Delay(10);
            }
        }

        /// <summary>
        /// 🔧 REASSEMBLY - Traiter un fragment reçu et assembler le message complet
        /// </summary>
        private bool ProcessMessageFragment(string fromPeer, string fragmentData)
        {
            try
            {
                var fragment = JsonSerializer.Deserialize<Dictionary<string, object>>(fragmentData);

                if (!fragment.ContainsKey("type") || fragment["type"].ToString() != "fragment")
                    return false;

                var messageId = fragment["messageId"].ToString();
                var chunkIndex = int.Parse(fragment["chunkIndex"].ToString());
                var totalChunks = int.Parse(fragment["totalChunks"].ToString());
                var data = fragment["data"].ToString();

                lock (_fragmentLock)
                {
                    // Initialize peer buffer if needed
                    if (!_fragmentBuffers.ContainsKey(fromPeer))
                        _fragmentBuffers[fromPeer] = new Dictionary<string, List<MessageFragment>>();

                    // Initialize message buffer if needed
                    if (!_fragmentBuffers[fromPeer].ContainsKey(messageId))
                        _fragmentBuffers[fromPeer][messageId] = new List<MessageFragment>();

                    // Add fragment
                    _fragmentBuffers[fromPeer][messageId].Add(new MessageFragment
                    {
                        MessageId = messageId,
                        ChunkIndex = chunkIndex,
                        TotalChunks = totalChunks,
                        Data = data,
                        Timestamp = DateTime.Now
                    });

                    LogEvent?.Invoke($"[WebRTC-FRAG] 📥 Received fragment {chunkIndex + 1}/{totalChunks} from {fromPeer} (messageId: {messageId})");

                    // Check if message is complete
                    var fragments = _fragmentBuffers[fromPeer][messageId];
                    if (fragments.Count == totalChunks)
                    {
                        // Reassemble message
                        fragments.Sort((a, b) => a.ChunkIndex.CompareTo(b.ChunkIndex));

                        var allData = new List<byte>();
                        foreach (var frag in fragments)
                        {
                            var chunkBytes = Convert.FromBase64String(frag.Data);
                            allData.AddRange(chunkBytes);
                        }

                        var completeMessage = Encoding.UTF8.GetString(allData.ToArray());
                        LogEvent?.Invoke($"[WebRTC-FRAG] ✅ Message reassembled from {fromPeer}: {completeMessage.Substring(0, Math.Min(50, completeMessage.Length))}...");

                        // Clean up fragments
                        _fragmentBuffers[fromPeer].Remove(messageId);

                        // Process the complete message
                        MessageReceived?.Invoke(fromPeer, completeMessage);
                        return true;
                    }
                }

                return true; // Fragment processed but message not complete yet
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-FRAG] ❌ Error processing fragment from {fromPeer}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Envoyer fichier via Data DataChannel avec flow control SIPSorcery
        /// </summary>
        public async Task<bool> SendFileAsync(string targetPeer, byte[] fileData, string fileName)
        {
            try
            {
                RTCDataChannel dataChannel = null;

                // ✅ FIX CRITIQUE: Essayer d'abord le data channel, puis le message channel en fallback
                if (_dataChannels.TryGetValue(targetPeer, out var dc) && dc.readyState == RTCDataChannelState.open)
                {
                    dataChannel = dc;
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ✅ Using DATA channel for file transfer to {targetPeer}");
                }
                else if (_messageChannels.TryGetValue(targetPeer, out var mc) && mc.readyState == RTCDataChannelState.open)
                {
                    dataChannel = mc;
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ⚠️ Using MESSAGE channel fallback for file transfer to {targetPeer}");
                }

                if (dataChannel == null)
                {
                    LogEvent?.Invoke($"[WebRTC-DIRECT] ❌ No usable channel for {targetPeer}");
                    LogEvent?.Invoke($"[WebRTC-DIRECT] 🔍 Data channel state: {(_dataChannels.TryGetValue(targetPeer, out var debugDc) ? debugDc.readyState.ToString() : "NOT_FOUND")}");
                    LogEvent?.Invoke($"[WebRTC-DIRECT] 🔍 Message channel state: {(_messageChannels.TryGetValue(targetPeer, out var debugMc) ? debugMc.readyState.ToString() : "NOT_FOUND")}");
                    return false;
                }

                LogEvent?.Invoke($"[WebRTC-FILE] 🚀 Starting file transfer: {fileName} ({fileData.Length} bytes) via Data Channel");

                // ✅ NOUVEAU: Pour les fichiers <= 32KB, envoyer en un seul bloc avec nom
                if (fileData.Length <= MAX_CHUNK_SIZE * 2)
                {
                    await WaitForBufferLow(dataChannel, targetPeer);

                    // ✅ FIX CRITIQUE: Utiliser même format que chunked pour compatibilité
                    var smallFileTransferId = Guid.NewGuid().ToString("N")[..8];
                    var fileHeader = Encoding.UTF8.GetBytes($"FILESTART:{smallFileTransferId}|FILENAME:{fileName}|SIZE:{fileData.Length}|CHUNKS:1|END|");
                    var completeData = new byte[fileHeader.Length + fileData.Length];
                    Array.Copy(fileHeader, 0, completeData, 0, fileHeader.Length);
                    Array.Copy(fileData, 0, completeData, fileHeader.Length, fileData.Length);

                    dataChannel.send(completeData);
                    LogEvent?.Invoke($"[WebRTC-FILE] ✅ Small file sent as single block: {fileName} (transfer: {smallFileTransferId})");
                    return true;
                }

                // ✅ FIX: Pour gros fichiers, utiliser chunking avec transfer ID
                var totalChunks = (int)Math.Ceiling(fileData.Length / (double)MAX_CHUNK_SIZE);
                var sentChunks = 0;
                var transferId = Guid.NewGuid().ToString("N")[..8]; // 8 char unique ID

                // Envoyer d'abord les métadonnées du fichier avec transfer ID
                var metadata = Encoding.UTF8.GetBytes($"FILESTART:{transferId}|FILENAME:{fileName}|SIZE:{fileData.Length}|CHUNKS:{totalChunks}|END|");
                await WaitForBufferLow(dataChannel, targetPeer);
                dataChannel.send(metadata);
                LogEvent?.Invoke($"[WebRTC-FILE] Metadata sent: {fileName}, {totalChunks} chunks, ID: {transferId}");

                for (int i = 0; i < totalChunks; i++)
                {
                    var offset = i * MAX_CHUNK_SIZE;
                    var remainingBytes = fileData.Length - offset;
                    var currentChunkSize = Math.Min(MAX_CHUNK_SIZE, remainingBytes);

                    var chunkData = new byte[currentChunkSize];
                    Array.Copy(fileData, offset, chunkData, 0, currentChunkSize);

                    // ✅ NEW: Envoyer chunk avec header ID
                    var chunkHeader = Encoding.UTF8.GetBytes($"CHUNK:{transferId}|{i}|{totalChunks}|");
                    var completeChunk = new byte[chunkHeader.Length + currentChunkSize];
                    Array.Copy(chunkHeader, 0, completeChunk, 0, chunkHeader.Length);
                    Array.Copy(chunkData, 0, completeChunk, chunkHeader.Length, currentChunkSize);

                    // ✅ FIX: Buffer monitoring AVANT envoi (critical!)
                    await WaitForBufferLow(dataChannel, targetPeer);

                    try
                    {
                        dataChannel.send(completeChunk);
                        sentChunks++;

                        var progress = (double)sentChunks / totalChunks * 100;
                        LogEvent?.Invoke($"[WebRTC-FILE] Progress: {progress:F1}% ({sentChunks}/{totalChunks}), Buffer: {dataChannel.bufferedAmount} bytes");

                        // ✅ FIXED: Déclencher événement progress pour UI avec filename
                        FileTransferProgress?.Invoke(targetPeer, progress, fileName);

                        // ✅ OPTIMISATION 2025: Burst control + micro-pauses pour haute performance
                        if (sentChunks % BURST_SIZE == 0)
                        {
                            // Pause micro après chaque burst de chunks
                            var bufferRatio = (double)dataChannel.bufferedAmount / BUFFER_THRESHOLD;
                            var delay = (int)(FLOW_CONTROL_DELAY * Math.Max(1, bufferRatio));
                            await Task.Delay(delay);
                        }
                        // Pas de délai entre chunks du même burst = performance maximale
                    }
                    catch (Exception chunkEx)
                    {
                        LogEvent?.Invoke($"[WebRTC-FILE] ❌ Failed to send chunk {i}: {chunkEx.Message}");
                        return false;
                    }
                }

                LogEvent?.Invoke($"[WebRTC-FILE] ✅ File transfer completed: {fileName} ({sentChunks}/{totalChunks} chunks)");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-FILE] ❌ Error sending file to {targetPeer}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Vérifier si une connexion directe existe avec un peer (DUAL channels)
        /// </summary>
        public bool IsConnected(string peer)
        {
            var msgConnected = _messageChannels.TryGetValue(peer, out var msgChan) &&
                              msgChan.readyState == RTCDataChannelState.open;
            var dataConnected = _dataChannels.TryGetValue(peer, out var dataChan) &&
                               dataChan.readyState == RTCDataChannelState.open;

            return msgConnected && dataConnected; // Les DEUX canaux doivent être ouverts
        }

        /// <summary>
        /// Obtenir l'état de connexion avec un peer
        /// </summary>
        public string GetConnectionState(string peer)
        {
            if (_peerConnections.TryGetValue(peer, out var pc))
            {
                return pc.connectionState.ToString();
            }
            return "none";
        }

        /// <summary>
        /// Nettoyer les ressources
        /// </summary>
        public void Dispose()
        {
            try
            {
                foreach (var msgChan in _messageChannels.Values)
                {
                    msgChan?.close();
                }

                foreach (var dataChan in _dataChannels.Values)
                {
                    dataChan?.close();
                }

                foreach (var pc in _peerConnections.Values)
                {
                    pc?.close();
                    pc?.Dispose();
                }

                _peerConnections.Clear();
                _messageChannels.Clear();
                _dataChannels.Clear();

                LogEvent?.Invoke($"[WebRTC-DIRECT] Disposed for client: {_clientId}");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-DIRECT] ❌ Error during dispose: {ex.Message}");
            }
        }

        // ===== ✅ NOUVELLES MÉTHODES HELPER FLOW CONTROL =====

        /// <summary>
        /// Wire event handlers pour Message DataChannel
        /// </summary>
        private void WireMessageChannelEvents(RTCDataChannel msgChannel, string peer)
        {
            msgChannel.onmessage += (dc, type, data) =>
            {
                if (type == DataChannelPayloadProtocols.WebRTC_String)
                {
                    var message = Encoding.UTF8.GetString(data);
                    LogEvent?.Invoke($"[WebRTC-MSG] 📩 Text from {peer}: {message.Substring(0, Math.Min(50, message.Length))}...");

                    // 🔧 FRAGMENTATION - Check if this is a fragment or complete message
                    if (!ProcessMessageFragment(peer, message))
                    {
                        // Not a fragment, process as normal message
                        MessageReceived?.Invoke(peer, message);
                    }
                }
                else if (type == DataChannelPayloadProtocols.WebRTC_Binary)
                {
                    // ✅ FIX: SIPSorcery envoie les strings comme Binary
                    var message = Encoding.UTF8.GetString(data);
                    LogEvent?.Invoke($"[WebRTC-MSG] 📦 Binary-as-text from {peer}: {message.Substring(0, Math.Min(50, message.Length))}...");

                    // 🔧 FRAGMENTATION - Check if this is a fragment or complete message
                    if (!ProcessMessageFragment(peer, message))
                    {
                        // Not a fragment, process as normal message
                        MessageReceived?.Invoke(peer, message);
                    }
                }
            };

            msgChannel.onopen += () =>
            {
                LogEvent?.Invoke($"[WebRTC-MSG] ✅ Message channel opened with {peer}");
                CheckBothChannelsReady(peer);
            };

            msgChannel.onclose += () =>
            {
                LogEvent?.Invoke($"[WebRTC-MSG] ❌ Message channel closed with {peer}");
                ConnectionStatusChanged?.Invoke(peer, false);
            };

            msgChannel.onerror += (error) =>
            {
                LogEvent?.Invoke($"[WebRTC-MSG] Channel error with {peer}: {error}");
            };

            // ✅ FIX: Setup bufferedAmountLowThreshold (SIPSorcery best practice)
            msgChannel.bufferedAmountLowThreshold = LOW_BUFFER_THRESHOLD;
        }

        /// <summary>
        /// Wire event handlers pour Data DataChannel (fichiers)
        /// </summary>
        private void WireDataChannelEvents(RTCDataChannel dataChannel, string peer)
        {
            dataChannel.onmessage += (dc, type, data) =>
            {
                LogEvent?.Invoke($"[WebRTC-DATA] 📦 File data from {peer}: {data?.Length ?? 0} bytes ({type})");

                // ✅ FIX: Traitement intelligent des données reçues
                if (data != null)
                {
                    ProcessReceivedFileData(peer, data);
                }
            };

            dataChannel.onopen += () =>
            {
                LogEvent?.Invoke($"[WebRTC-DATA] ✅ Data channel opened with {peer}");
                CheckBothChannelsReady(peer);
            };

            dataChannel.onclose += () =>
            {
                LogEvent?.Invoke($"[WebRTC-DATA] ❌ Data channel closed with {peer}");
                ConnectionStatusChanged?.Invoke(peer, false);
            };

            dataChannel.onerror += (error) =>
            {
                LogEvent?.Invoke($"[WebRTC-DATA] Channel error with {peer}: {error}");
            };

            // ✅ FIX: Setup bufferedAmountLowThreshold pour fichiers
            dataChannel.bufferedAmountLowThreshold = LOW_BUFFER_THRESHOLD;
        }

        /// <summary>
        /// Vérifier si les DEUX canaux sont ouverts avant de signaler connexion prête
        /// </summary>
        private void CheckBothChannelsReady(string peer)
        {
            var msgReady = _messageChannels.TryGetValue(peer, out var msgChan) &&
                          msgChan.readyState == RTCDataChannelState.open;
            var dataReady = _dataChannels.TryGetValue(peer, out var dataChan) &&
                           dataChan.readyState == RTCDataChannelState.open;

            if (msgReady && dataReady)
            {
                LogEvent?.Invoke($"[WebRTC-DUAL] 🎉 BOTH channels ready for {peer}!");
                ConnectionStatusChanged?.Invoke(peer, true);
            }
            else
            {
                LogEvent?.Invoke($"[WebRTC-DUAL] Waiting for channels: msg={msgReady}, data={dataReady}");
            }
        }

        /// <summary>
        /// Monitor dual channels opening avec polling
        /// </summary>
        private void MonitorChannelsAsync(string peer, RTCDataChannel msgChannel, RTCDataChannel dataChannel)
        {
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(1000);

                    var msgOpen = msgChannel.readyState == RTCDataChannelState.open;
                    var dataOpen = dataChannel.readyState == RTCDataChannelState.open;

                    if (msgOpen && dataOpen)
                    {
                        LogEvent?.Invoke($"[WebRTC-MONITOR] ✅ Both channels opened with {peer} after {i + 1}s");
                        ConnectionStatusChanged?.Invoke(peer, true);
                        break;
                    }
                    else if (i % 5 == 0)
                    {
                        LogEvent?.Invoke($"[WebRTC-MONITOR] Status {peer}: msg={msgChannel.readyState}, data={dataChannel.readyState} (attempt {i + 1})");
                    }
                }
            });
        }

        /// <summary>
        /// Monitor single channel opening
        /// </summary>
        private void MonitorSingleChannelAsync(string peer, RTCDataChannel channel)
        {
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(1000);

                    if (channel.readyState == RTCDataChannelState.open)
                    {
                        LogEvent?.Invoke($"[WebRTC-MONITOR] ✅ Channel '{channel.label}' opened with {peer} after {i + 1}s");
                        CheckBothChannelsReady(peer); // Check if both are ready
                        break;
                    }
                }
            });
        }

        /// <summary>
        /// ✅ OPTIMISÉ 2025: Wait for buffer low avec polling agressif pour haute performance
        /// </summary>
        private async Task WaitForBufferLow(RTCDataChannel channel, string peer)
        {
            int waitCount = 0;
            const int maxWaits = 500; // 5 secondes max (was 10s, now more aggressive)

            while (channel.bufferedAmount > BUFFER_THRESHOLD && waitCount < maxWaits)
            {
                if (waitCount == 0)
                {
                    LogEvent?.Invoke($"[FLOW-CONTROL] High buffer for {peer}: {channel.bufferedAmount/1024}KB - polling...");
                }

                // ✅ OPTIMISATION: 10ms polling au lieu de 100ms = 10x plus rapide
                await Task.Delay(10);
                waitCount++;

                // Log toutes les 100 polls (1 seconde) au lieu de toutes les 100ms
                if (waitCount % 100 == 0)
                {
                    LogEvent?.Invoke($"[FLOW-CONTROL] Polling {peer}: {channel.bufferedAmount/1024}KB ({waitCount * 10}ms)");
                }
            }

            if (waitCount >= maxWaits)
            {
                LogEvent?.Invoke($"[FLOW-CONTROL] ⚠️ Buffer timeout on {peer} after 5s");
            }
            else if (waitCount > 5) // Log seulement si attente significative
            {
                LogEvent?.Invoke($"[FLOW-CONTROL] ✅ Buffer ready {peer}: {channel.bufferedAmount/1024}KB (waited {waitCount * 10}ms)");
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Traitement intelligent des données de fichier reçues
        /// </summary>
        private void ProcessReceivedFileData(string peer, byte[] data)
        {
            try
            {
                var headerText = Encoding.UTF8.GetString(data, 0, Math.Min(200, data.Length));
                LogEvent?.Invoke($"[FILE-PROCESS] Data received from {peer}: {data.Length} bytes, header: {headerText.Substring(0, Math.Min(100, headerText.Length))}...");

                // Vérifier si c'est un fichier avec header FILENAME
                if (headerText.StartsWith("FILENAME:"))
                {
                    ProcessFileWithHeader(peer, data, headerText);
                }
                else if (headerText.StartsWith("FILESTART:"))
                {
                    ProcessFileMetadata(peer, data, headerText);
                }
                else if (headerText.StartsWith("CHUNK:"))
                {
                    ProcessFileChunk(peer, data, headerText);
                }
                else
                {
                    // Données brutes - traiter comme fichier complet ou chunk
                    LogEvent?.Invoke($"[FILE-PROCESS] Raw data received from {peer}: {data.Length} bytes");
                    FileDataReceived?.Invoke(peer, data);
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FILE-PROCESS] ❌ Error processing file data from {peer}: {ex.Message}");
            }
        }

        private void ProcessFileWithHeader(string peer, byte[] data, string headerText)
        {
            try
            {
                // Parser: FILENAME:nom.txt|SIZE:1234|
                var parts = headerText.Split('|');
                var fileName = "";
                var fileSize = 0L;

                foreach (var part in parts)
                {
                    if (part.StartsWith("FILENAME:"))
                        fileName = part.Substring(9);
                    else if (part.StartsWith("SIZE:"))
                        long.TryParse(part.Substring(5), out fileSize);
                }

                // Trouver la fin du header (premier |fileSize| pattern)
                var headerEnd = headerText.IndexOf($"|SIZE:{fileSize}|") + $"|SIZE:{fileSize}|".Length;
                var headerBytes = Encoding.UTF8.GetBytes(headerText.Substring(0, headerEnd));

                // Extraire les données du fichier après le header
                var fileData = new byte[data.Length - headerBytes.Length];
                Array.Copy(data, headerBytes.Length, fileData, 0, fileData.Length);

                LogEvent?.Invoke($"[FILE-PROCESS] ✅ Complete file received: {fileName} ({fileData.Length} bytes)");

                // Déclencher l'événement avec le nom de fichier dans un format spécial
                var fileWithName = Encoding.UTF8.GetBytes($"FILENAME:{fileName}|").Concat(fileData).ToArray();
                FileDataReceived?.Invoke(peer, fileWithName);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FILE-PROCESS] ❌ Error processing file header: {ex.Message}");
            }
        }

        private void ProcessFileMetadata(string peer, byte[] data, string headerText)
        {
            try
            {
                LogEvent?.Invoke($"[FILE-PROCESS] File metadata received from {peer}: {headerText}");

                // Parser: FILESTART:12345678|FILENAME:test.pdf|SIZE:1234|CHUNKS:5|END|
                var parts = headerText.Split('|');
                string transferId = "";
                string fileName = "";
                long fileSize = 0;
                int totalChunks = 0;

                foreach (var part in parts)
                {
                    if (part.StartsWith("FILESTART:"))
                        transferId = part.Substring(10);
                    else if (part.StartsWith("FILENAME:"))
                        fileName = part.Substring(9);
                    else if (part.StartsWith("SIZE:"))
                        long.TryParse(part.Substring(5), out fileSize);
                    else if (part.StartsWith("CHUNKS:"))
                        int.TryParse(part.Substring(7), out totalChunks);
                }

                if (!string.IsNullOrEmpty(transferId) && totalChunks > 0)
                {
                    // Initialiser la reconstruction du fichier
                    _fileReconstructions[transferId] = new FileReconstructionState
                    {
                        FileName = fileName,
                        TotalSize = fileSize,
                        TotalChunks = totalChunks
                    };

                    LogEvent?.Invoke($"[FILE-RECONSTRUCT] Started reconstruction: {fileName} ({totalChunks} chunks, {fileSize} bytes), ID: {transferId}");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FILE-PROCESS] ❌ Error processing metadata: {ex.Message}");
            }
        }

        private void ProcessFileChunk(string peer, byte[] data, string headerText)
        {
            try
            {
                // Parser: CHUNK:12345678|5|159|
                var parts = headerText.Split('|');
                if (parts.Length < 3) return;

                var transferId = parts[0].Substring(6); // Remove "CHUNK:"
                int.TryParse(parts[1], out var chunkIndex);
                int.TryParse(parts[2], out var totalChunks);

                LogEvent?.Invoke($"[FILE-CHUNK] Received chunk {chunkIndex}/{totalChunks} for transfer {transferId}");

                if (!_fileReconstructions.TryGetValue(transferId, out var reconstruction))
                {
                    LogEvent?.Invoke($"[FILE-CHUNK] ⚠️ Unknown transfer ID: {transferId} - ignoring chunk");
                    return;
                }

                // Trouver la fin du header
                var headerEnd = headerText.IndexOf($"|{totalChunks}|") + $"|{totalChunks}|".Length;
                var headerBytes = Encoding.UTF8.GetBytes(headerText.Substring(0, headerEnd));

                // Extraire les données du chunk après le header
                var chunkData = new byte[data.Length - headerBytes.Length];
                Array.Copy(data, headerBytes.Length, chunkData, 0, chunkData.Length);

                // Stocker le chunk
                reconstruction.ReceivedChunks[chunkIndex] = chunkData;
                reconstruction.LastActivity = DateTime.Now;

                LogEvent?.Invoke($"[FILE-CHUNK] Stored chunk {chunkIndex}, progress: {reconstruction.Progress:F1}% ({reconstruction.ReceivedChunks.Count}/{reconstruction.TotalChunks})");

                // ✅ FIXED: Déclencher événement progress pour receiver UI avec filename
                FileTransferProgress?.Invoke(peer, reconstruction.Progress, reconstruction.FileName);

                // Vérifier si le fichier est complet
                if (reconstruction.IsComplete)
                {
                    LogEvent?.Invoke($"[FILE-RECONSTRUCT] ✅ File reconstruction complete: {reconstruction.FileName}");

                    // Reconstruire le fichier
                    var completeFile = reconstruction.ReconstructFile();

                    // ✅ FIX REVERTED: Revenir au format simple FILENAME: qui fonctionnait
                    var fileWithName = Encoding.UTF8.GetBytes($"FILENAME:{reconstruction.FileName}|").Concat(completeFile).ToArray();

                    // Nettoyer
                    _fileReconstructions.Remove(transferId);

                    // Envoyer le fichier complet avec nouveau format header
                    FileDataReceived?.Invoke(peer, fileWithName);
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FILE-CHUNK] ❌ Error processing chunk: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// State for reconstructing chunked file transfers
    /// </summary>
    public class FileReconstructionState
    {
        public string FileName { get; set; } = "";
        public long TotalSize { get; set; }
        public int TotalChunks { get; set; }
        public Dictionary<int, byte[]> ReceivedChunks { get; set; } = new();
        public DateTime StartTime { get; set; } = DateTime.Now;
        public DateTime LastActivity { get; set; } = DateTime.Now;

        public bool IsComplete => ReceivedChunks.Count == TotalChunks;
        public double Progress => TotalChunks == 0 ? 0 : (ReceivedChunks.Count / (double)TotalChunks) * 100;

        public byte[] ReconstructFile()
        {
            if (!IsComplete) throw new InvalidOperationException("File reconstruction incomplete");

            // ✅ FIX: Calculer la taille réelle basée sur les chunks reçus
            var actualTotalSize = ReceivedChunks.Values.Sum(chunk => chunk.Length);
            var result = new byte[actualTotalSize];
            int offset = 0;

            for (int i = 0; i < TotalChunks; i++)
            {
                if (ReceivedChunks.TryGetValue(i, out var chunk))
                {
                    if (offset + chunk.Length <= result.Length)
                    {
                        Array.Copy(chunk, 0, result, offset, chunk.Length);
                        offset += chunk.Length;
                    }
                    else
                    {
                        throw new InvalidOperationException($"Chunk {i} size {chunk.Length} would exceed buffer at offset {offset}");
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Missing chunk {i}");
                }
            }

            return result;
        }
    }
}