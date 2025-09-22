using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SIPSorcery.Net;
using System.Text.Json;

namespace ChatP2P.Client
{
    /// <summary>
    /// 🎥 Version simplifiée du WebRTCMediaClient pour VOIP/Vidéo basique
    /// Compatible avec SIPSorcery 6.0.11 sans dépendances media complexes
    /// </summary>
    public class SimpleWebRTCMediaClient : IDisposable
    {
        private readonly string _clientId;
        private readonly RTCConfiguration _rtcConfig;
        private readonly Dictionary<string, RTCPeerConnection> _mediaConnections = new();

        // Events pour signaling
        public event Action<string, string, string>? ICECandidateGenerated; // fromPeer, toPeer, candidate
        public event Action<string>? LogEvent;
        public event Action<string, bool>? MediaConnectionChanged; // peer, connected
        public event Action<string, byte[]>? RemoteAudioReceived; // peer, audioData
        public event Action<string, byte[]>? RemoteVideoReceived; // peer, videoData

        public SimpleWebRTCMediaClient(string clientId)
        {
            _clientId = clientId;

            // Configuration ICE servers (même que WebRTCDirectClient)
            _rtcConfig = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                    new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" },
                    new RTCIceServer
                    {
                        urls = "turn:openrelay.metered.ca:80",
                        username = "openrelayproject",
                        credential = "openrelayproject"
                    }
                }
            };

            LogEvent?.Invoke($"[WebRTC-Media] Simple client initialized for: {_clientId}");
        }

        /// <summary>
        /// Initialiser les endpoints audio/vidéo (simulé)
        /// </summary>
        public async Task<bool> InitializeMediaAsync(bool enableAudio, bool enableVideo)
        {
            try
            {
                LogEvent?.Invoke($"[WebRTC-Media] Initializing media (audio: {enableAudio}, video: {enableVideo}) - Simulated");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-Media] ❌ Error initializing media: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Créer une offer avec médias audio/vidéo (structure basique)
        /// </summary>
        public async Task<string?> CreateMediaOfferAsync(string targetPeer, bool includeAudio, bool includeVideo)
        {
            try
            {
                LogEvent?.Invoke($"[WebRTC-Media] Creating media offer for {targetPeer} (audio: {includeAudio}, video: {includeVideo})");

                // Créer PeerConnection basique
                var pc = new RTCPeerConnection(_rtcConfig);
                _mediaConnections[targetPeer] = pc;

                // Setup ICE candidate events
                pc.onicecandidate += (candidate) =>
                {
                    if (candidate != null)
                    {
                        LogEvent?.Invoke($"[WebRTC-Media] ICE candidate for {targetPeer}: {candidate.candidate}");
                        ICECandidateGenerated?.Invoke(_clientId, targetPeer, candidate.candidate ?? "");
                    }
                };

                // Setup connection monitoring
                SetupMediaEvents(pc, targetPeer);

                // Créer offer basique (sans tracks pour l'instant)
                var offer = pc.createOffer();
                var setResult = pc.setLocalDescription(offer);

                LogEvent?.Invoke($"[WebRTC-Media] Media offer created for {targetPeer}");

                var offerJson = new
                {
                    type = "offer",
                    sdp = offer.sdp,
                    hasAudio = includeAudio,
                    hasVideo = includeVideo
                };

                return JsonSerializer.Serialize(offerJson);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-Media] ❌ Error creating media offer: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Traiter une offer média et créer answer
        /// </summary>
        public async Task<string?> ProcessMediaOfferAsync(string fromPeer, string offerJson)
        {
            try
            {
                LogEvent?.Invoke($"[WebRTC-Media] Processing media offer from {fromPeer}");

                var offerData = JsonSerializer.Deserialize<JsonElement>(offerJson);
                var sdp = offerData.GetProperty("sdp").GetString();
                var hasAudio = offerData.TryGetProperty("hasAudio", out var audioProp) && audioProp.GetBoolean();
                var hasVideo = offerData.TryGetProperty("hasVideo", out var videoProp) && videoProp.GetBoolean();

                // Créer PeerConnection
                var pc = new RTCPeerConnection(_rtcConfig);
                _mediaConnections[fromPeer] = pc;

                // Setup ICE candidate events
                pc.onicecandidate += (candidate) =>
                {
                    if (candidate != null)
                    {
                        LogEvent?.Invoke($"[WebRTC-Media] ICE candidate for {fromPeer}: {candidate.candidate}");
                        ICECandidateGenerated?.Invoke(_clientId, fromPeer, candidate.candidate ?? "");
                    }
                };

                // Setup media events
                SetupMediaEvents(pc, fromPeer);

                // Set remote description
                var offer = new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.offer,
                    sdp = sdp
                };
                pc.setRemoteDescription(offer);

                // Créer answer
                var answer = pc.createAnswer();
                pc.setLocalDescription(answer);

                LogEvent?.Invoke($"[WebRTC-Media] Media answer created for {fromPeer}");

                var answerJson = new
                {
                    type = "answer",
                    sdp = answer.sdp,
                    hasAudio = hasAudio,
                    hasVideo = hasVideo
                };

                return JsonSerializer.Serialize(answerJson);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-Media] ❌ Error processing media offer: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Traiter une answer média reçue
        /// </summary>
        public async Task<bool> ProcessMediaAnswerAsync(string fromPeer, string answerJson)
        {
            try
            {
                LogEvent?.Invoke($"[WebRTC-Media] Processing media answer from {fromPeer}");

                if (!_mediaConnections.TryGetValue(fromPeer, out var pc))
                {
                    LogEvent?.Invoke($"[WebRTC-Media] ❌ No media connection for {fromPeer}");
                    return false;
                }

                var answerData = JsonSerializer.Deserialize<JsonElement>(answerJson);
                var sdp = answerData.GetProperty("sdp").GetString();

                var answer = new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.answer,
                    sdp = sdp
                };

                pc.setRemoteDescription(answer);

                LogEvent?.Invoke($"[WebRTC-Media] ✅ Media answer processed from {fromPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-Media] ❌ Error processing media answer: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Ajouter un candidat ICE pour connexion média
        /// </summary>
        public async Task<bool> AddMediaCandidateAsync(string fromPeer, string candidateData)
        {
            try
            {
                if (!_mediaConnections.TryGetValue(fromPeer, out var pc))
                {
                    LogEvent?.Invoke($"[WebRTC-Media] ❌ No media connection for {fromPeer}");
                    return false;
                }

                var candidateInit = new RTCIceCandidateInit
                {
                    candidate = candidateData
                };
                pc.addIceCandidate(candidateInit);

                LogEvent?.Invoke($"[WebRTC-Media] ✅ ICE candidate added for {fromPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-Media] ❌ Error adding media candidate: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Démarrer la capture et transmission média (simulé)
        /// </summary>
        public async Task<bool> StartMediaAsync(string peer, bool startAudio, bool startVideo)
        {
            try
            {
                LogEvent?.Invoke($"[WebRTC-Media] Starting media for {peer} (audio: {startAudio}, video: {startVideo}) - Simulated");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-Media] ❌ Error starting media: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Arrêter la transmission média
        /// </summary>
        public async Task StopMediaAsync(string peer)
        {
            try
            {
                LogEvent?.Invoke($"[WebRTC-Media] Stopping media for {peer}");

                if (_mediaConnections.TryGetValue(peer, out var pc))
                {
                    pc.close();
                    pc.Dispose();
                    _mediaConnections.Remove(peer);
                }

                LogEvent?.Invoke($"[WebRTC-Media] ✅ Media stopped for {peer}");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-Media] ❌ Error stopping media: {ex.Message}");
            }
        }

        /// <summary>
        /// Configurer les événements média pour une PeerConnection
        /// </summary>
        private void SetupMediaEvents(RTCPeerConnection pc, string peer)
        {
            pc.onconnectionstatechange += (state) =>
            {
                LogEvent?.Invoke($"[WebRTC-Media] Connection state changed for {peer}: {state}");
                MediaConnectionChanged?.Invoke(peer, state == RTCPeerConnectionState.connected);
            };

            // Note: Les events pour les tracks audio/vidéo seraient ajoutés ici
            // quand les MediaStreamTrack seront implémentés
        }

        /// <summary>
        /// Vérifier si une connexion média est active
        /// </summary>
        public bool IsMediaConnected(string peer)
        {
            return _mediaConnections.TryGetValue(peer, out var pc) &&
                   pc.connectionState == RTCPeerConnectionState.connected;
        }

        public void Dispose()
        {
            try
            {
                // Arrêter toutes les connexions
                var peers = new List<string>(_mediaConnections.Keys);
                foreach (var peer in peers)
                {
                    StopMediaAsync(peer).Wait(1000);
                }

                LogEvent?.Invoke("[WebRTC-Media] Service disposed");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[WebRTC-Media] ❌ Error during dispose: {ex.Message}");
            }
        }
    }
}