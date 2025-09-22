using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using ChatP2P.Client.Services;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// 📞 Gestionnaire d'appels VOIP/Vidéo P2P
    /// Orchestre les appels audio/vidéo via WebRTC avec les services de capture
    /// </summary>
    public class VOIPCallManager : IDisposable
    {
        private readonly string _clientId;
        private readonly Dictionary<string, VOIPCall> _activeCalls = new();
        private readonly SimpleAudioCaptureService _audioCapture;
        private readonly SimpleVideoCaptureService _videoCapture;
        private readonly WebRTCDirectClient _webRtcClient;
        private VOIPRelayClient? _voipRelay;

        // Events pour l'interface utilisateur
        public event Action<string, CallState>? CallStateChanged;
        public event Action<string, VideoFrame>? RemoteVideoReceived;
        public event Action<string, byte[]>? RemoteAudioReceived;
        public event Action<string>? LogEvent;
        public event Action<string, string>? IncomingCallReceived; // fromPeer, callType

        // ✅ NOUVEAU: Events pour signaling VOIP via MainWindow
        public event Func<string, string, string, string, Task>? SendVOIPSignal; // signalType, fromPeer, toPeer, data

        public VOIPCallManager(string clientId, WebRTCDirectClient webRtcClient)
        {
            _clientId = clientId;
            _webRtcClient = webRtcClient;
            _audioCapture = new SimpleAudioCaptureService();
            _videoCapture = new SimpleVideoCaptureService();

            // Wire les events des services de capture
            _audioCapture.LogEvent += (msg) => LogEvent?.Invoke($"[VOIP-Audio] {msg}");
            _videoCapture.LogEvent += (msg) => LogEvent?.Invoke($"[VOIP-Video] {msg}");

            LogEvent?.Invoke($"[VOIP-Manager] Initialized for client: {_clientId}");
            LogEvent?.Invoke($"[VOIP-Manager] 🔍 DIAGNOSTIC: Using clientId '{_clientId}' for VOIP signaling");
        }

        /// <summary>
        /// Initier un appel audio vers un peer
        /// </summary>
        public async Task<bool> StartAudioCallAsync(string targetPeer)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] Starting audio call to {targetPeer}");

                if (_activeCalls.ContainsKey(targetPeer))
                {
                    LogEvent?.Invoke($"[VOIP-Manager] Call already active with {targetPeer}");
                    return false;
                }

                // Créer nouvelle session d'appel
                var call = new VOIPCall
                {
                    PeerName = targetPeer,
                    CallType = CallType.AudioOnly,
                    State = CallState.Initiating,
                    StartTime = DateTime.Now
                };

                _activeCalls[targetPeer] = call;
                CallStateChanged?.Invoke(targetPeer, CallState.Initiating);

                // Démarrer capture audio
                LogEvent?.Invoke($"[VOIP-Manager] Starting audio capture service...");

                if (_audioCapture == null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Audio capture service is null");
                    await EndCallAsync(targetPeer);
                    return false;
                }

                LogEvent?.Invoke($"[VOIP-Manager] Audio capture service OK, calling StartCaptureAsync...");
                var audioStarted = await _audioCapture.StartCaptureAsync();
                LogEvent?.Invoke($"[VOIP-Manager] Audio capture StartCaptureAsync returned: {audioStarted}");

                if (!audioStarted)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to start audio capture");
                    await EndCallAsync(targetPeer);
                    return false;
                }

                LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio capture started successfully");

                // 🔧 FALLBACK SYSTEM: Try P2P first, then VOIP relay
                LogEvent?.Invoke($"[VOIP-Manager] Creating media offer for {targetPeer} (audio: True, video: False)");

                if (_webRtcClient == null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ WebRTC client is null");
                    await EndCallAsync(targetPeer);
                    return false;
                }

                LogEvent?.Invoke($"[VOIP-Manager] WebRTC client OK, calling CreateOfferAsync for {targetPeer}");

                // Try P2P WebRTC first
                var offer = await _webRtcClient.CreateOfferAsync(targetPeer);

                if (offer != null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ P2P WebRTC offer created successfully");
                    // Envoyer l'invitation d'appel via signaling P2P
                    await SendCallInviteAsync(targetPeer, "audio", offer);
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ⚠️ P2P WebRTC failed, trying VOIP relay fallback");

                    // Fallback to VOIP relay
                    var relaySuccess = await TryVOIPRelayFallback(targetPeer, false);
                    if (!relaySuccess)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ❌ Both P2P and relay failed");
                        await EndCallAsync(targetPeer);
                        return false;
                    }
                }

                call.State = CallState.Calling;
                CallStateChanged?.Invoke(targetPeer, CallState.Calling);

                LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio call initiated to {targetPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error starting audio call: {ex.Message}");
                await EndCallAsync(targetPeer);
                return false;
            }
        }

        /// <summary>
        /// Initier un appel vidéo vers un peer
        /// </summary>
        public async Task<bool> StartVideoCallAsync(string targetPeer)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] Starting video call to {targetPeer}");

                if (_activeCalls.ContainsKey(targetPeer))
                {
                    LogEvent?.Invoke($"[VOIP-Manager] Call already active with {targetPeer}");
                    return false;
                }

                // Créer nouvelle session d'appel vidéo
                var call = new VOIPCall
                {
                    PeerName = targetPeer,
                    CallType = CallType.VideoCall,
                    State = CallState.Initiating,
                    StartTime = DateTime.Now
                };

                _activeCalls[targetPeer] = call;
                CallStateChanged?.Invoke(targetPeer, CallState.Initiating);

                // Démarrer capture audio et vidéo
                var audioStarted = await _audioCapture.StartCaptureAsync();
                var videoStarted = await _videoCapture.StartCaptureAsync();

                if (!audioStarted || !videoStarted)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to start media capture (audio: {audioStarted}, video: {videoStarted})");
                    await EndCallAsync(targetPeer);
                    return false;
                }

                // Créer l'offer WebRTC avec audio et vidéo
                var offer = await CreateMediaOfferAsync(targetPeer, true, true);
                if (offer == null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to create video offer");
                    await EndCallAsync(targetPeer);
                    return false;
                }

                // Envoyer l'invitation d'appel via signaling
                await SendCallInviteAsync(targetPeer, "video", offer);

                call.State = CallState.Calling;
                CallStateChanged?.Invoke(targetPeer, CallState.Calling);

                LogEvent?.Invoke($"[VOIP-Manager] ✅ Video call initiated to {targetPeer}");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error starting video call: {ex.Message}");
                await EndCallAsync(targetPeer);
                return false;
            }
        }

        /// <summary>
        /// Accepter un appel entrant
        /// </summary>
        public async Task<bool> AcceptCallAsync(string fromPeer, string callType, string offer)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] Accepting {callType} call from {fromPeer}");

                var isVideo = callType.ToLower() == "video";

                // Créer session d'appel
                var call = new VOIPCall
                {
                    PeerName = fromPeer,
                    CallType = isVideo ? CallType.VideoCall : CallType.AudioOnly,
                    State = CallState.Connecting,
                    StartTime = DateTime.Now
                };

                _activeCalls[fromPeer] = call;
                CallStateChanged?.Invoke(fromPeer, CallState.Connecting);

                // Démarrer les captures nécessaires
                var audioStarted = await _audioCapture.StartCaptureAsync();
                var videoStarted = isVideo ? await _videoCapture.StartCaptureAsync() : true;

                if (!audioStarted || (isVideo && !videoStarted))
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to start media for incoming call");
                    await EndCallAsync(fromPeer);
                    return false;
                }

                // ✅ FIX: Essayer P2P WebRTC d'abord, puis fallback vers VOIP relay
                var answer = await ProcessMediaOfferAsync(fromPeer, offer, true, isVideo);
                if (answer != null)
                {
                    // P2P WebRTC réussi
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ P2P WebRTC answer created successfully");

                    // Envoyer la réponse d'acceptation P2P
                    await SendCallAcceptAsync(fromPeer, callType, answer);

                    call.State = CallState.Connected;
                    CallStateChanged?.Invoke(fromPeer, CallState.Connected);

                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Call accepted from {fromPeer} via P2P WebRTC");
                    return true;
                }
                else
                {
                    // P2P WebRTC échoué - Fallback vers VOIP relay
                    LogEvent?.Invoke($"[VOIP-Manager] ⚠️ P2P WebRTC failed, falling back to VOIP relay");

                    // ✅ FIX: Se connecter au relay VOIP pour accepter l'appel
                    await EnsureRelayConnectionAsync();

                    if (_voipRelay?.IsConnected == true)
                    {
                        // Accepter via le relay
                        var relaySuccess = await _voipRelay.AcceptCallAsync(fromPeer);
                        if (relaySuccess)
                        {
                            // Envoyer une réponse d'acceptation générique pour signaler l'accord
                            await SendCallAcceptAsync(fromPeer, callType, "relay_accepted");

                            call.State = CallState.Connected;
                            CallStateChanged?.Invoke(fromPeer, CallState.Connected);

                            // ✅ FIX CRITIQUE: Setup audio relay pour VM2 (celui qui accepte)
                            await SetupAudioRelayForPeer(fromPeer);

                            LogEvent?.Invoke($"[VOIP-Manager] ✅ Call accepted from {fromPeer} via VOIP relay");
                            return true;
                        }
                        else
                        {
                            LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to accept call via VOIP relay");
                        }
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ❌ Cannot connect to VOIP relay for accepting call");
                    }

                    await EndCallAsync(fromPeer);
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error accepting call: {ex.Message}");
                await EndCallAsync(fromPeer);
                return false;
            }
        }

        /// <summary>
        /// Terminer un appel
        /// </summary>
        public async Task EndCallAsync(string peer)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] Ending call with {peer}");

                if (_activeCalls.TryGetValue(peer, out var call))
                {
                    call.State = CallState.Ended;
                    call.EndTime = DateTime.Now;
                    CallStateChanged?.Invoke(peer, CallState.Ended);

                    _activeCalls.Remove(peer);
                }

                // Arrêter les captures si plus d'appels actifs
                if (_activeCalls.Count == 0)
                {
                    await _audioCapture.StopCaptureAsync();
                    await _videoCapture.StopCaptureAsync();
                }

                // Envoyer signal de fin d'appel
                await SendCallEndAsync(peer);

                LogEvent?.Invoke($"[VOIP-Manager] ✅ Call ended with {peer}");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error ending call: {ex.Message}");
            }
        }

        /// <summary>
        /// Obtenir l'état d'un appel
        /// </summary>
        public CallState? GetCallState(string peer)
        {
            return _activeCalls.TryGetValue(peer, out var call) ? call.State : null;
        }

        /// <summary>
        /// Force la connexion au VOIP relay (public pour appels entrants)
        /// </summary>
        public async Task<bool> EnsureRelayConnectionForIncomingCallAsync()
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🔄 Ensuring VOIP relay connection for incoming call...");
                await EnsureRelayConnectionAsync();
                return _voipRelay?.IsConnected == true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error ensuring relay connection: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Vérifier si un appel est actif
        /// </summary>
        public bool IsCallActive(string peer)
        {
            return _activeCalls.TryGetValue(peer, out var call) &&
                   call.State == CallState.Connected;
        }

        /// <summary>
        /// Créer une offer WebRTC avec médias
        /// </summary>
        private async Task<string?> CreateMediaOfferAsync(string peer, bool includeAudio, bool includeVideo)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] Creating media offer for {peer} (audio: {includeAudio}, video: {includeVideo})");

                // 🔍 DIAGNOSTIC: Vérifier état WebRTC client
                if (_webRtcClient == null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ WebRTC client is null");
                    return null;
                }

                LogEvent?.Invoke($"[VOIP-Manager] WebRTC client OK, calling CreateOfferAsync for {peer}");

                // ✅ Utiliser l'offer standard pour l'instant - MediaStreamTrack sera ajouté plus tard
                var offer = await _webRtcClient.CreateOfferAsync(peer);

                if (offer != null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Media offer created successfully for {peer}");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ CreateOfferAsync returned null for {peer}");
                }

                return offer;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error creating media offer: {ex.Message}");
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Exception details: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Traiter une offer reçue et créer answer avec médias
        /// </summary>
        private async Task<string?> ProcessMediaOfferAsync(string peer, string offer, bool includeAudio, bool includeVideo)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] Processing media offer from {peer}");

                // ✅ Utiliser la méthode answer standard pour l'instant - MediaStreamTrack sera ajouté plus tard
                return await _webRtcClient.ProcessOfferAsync(peer, offer);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error processing media offer: {ex.Message}");
                return null;
            }
        }

        // ===== MÉTHODES DE SIGNALING =====

        private async Task SendCallInviteAsync(string peer, string callType, string offer)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] Sending call invite to {peer}: {callType}");

                // ✅ NOUVEAU: Préparer les données d'invitation VOIP
                var inviteData = JsonSerializer.Serialize(new
                {
                    callType = callType,
                    offer = offer,
                    timestamp = DateTime.UtcNow.ToString("O")
                });

                // Envoyer via l'event delegate vers MainWindow
                if (SendVOIPSignal != null)
                {
                    await SendVOIPSignal("call_invite", _clientId, peer, inviteData);
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Call invite sent to {peer}");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ No signaling handler available");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error sending call invite: {ex.Message}");
            }
        }

        private async Task SendCallAcceptAsync(string peer, string callType, string answer)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] Sending call accept to {peer}: {callType}");

                // ✅ NOUVEAU: Préparer les données d'acceptation VOIP
                var acceptData = JsonSerializer.Serialize(new
                {
                    callType = callType,
                    answer = answer,
                    timestamp = DateTime.UtcNow.ToString("O")
                });

                // Envoyer via l'event delegate vers MainWindow
                if (SendVOIPSignal != null)
                {
                    await SendVOIPSignal("call_accept", _clientId, peer, acceptData);
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Call accept sent to {peer}");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ No signaling handler available");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error sending call accept: {ex.Message}");
            }
        }

        private async Task SendCallEndAsync(string peer)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] Sending call end to {peer}");

                // ✅ NOUVEAU: Préparer les données de fin d'appel
                var endData = JsonSerializer.Serialize(new
                {
                    reason = "user_ended",
                    timestamp = DateTime.UtcNow.ToString("O")
                });

                // Envoyer via l'event delegate vers MainWindow
                if (SendVOIPSignal != null)
                {
                    await SendVOIPSignal("call_end", _clientId, peer, endData);
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Call end signal sent to {peer}");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ No signaling handler available");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error sending call end: {ex.Message}");
            }
        }

        /// <summary>
        /// Fallback vers VOIP relay quand P2P WebRTC échoue
        /// </summary>
        private async Task<bool> TryVOIPRelayFallback(string targetPeer, bool includeVideo)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🔄 Trying VOIP relay fallback for {targetPeer}");

                // Obtenir l'IP du serveur (normalement passée depuis MainWindow)
                string serverIP = "192.168.1.152"; // TODO: Get from configuration

                // Créer client VOIP relay si nécessaire
                if (_voipRelay == null)
                {
                    _voipRelay = new VOIPRelayClient(serverIP, _clientId);

                    // Wire events
                    _voipRelay.LogEvent += (msg) => LogEvent?.Invoke($"[VOIP-RELAY] {msg}");
                    _voipRelay.VoipMessageReceived += OnVoipRelayMessageReceived;
                    _voipRelay.AudioDataReceived += OnVoipRelayAudioReceived;
                    _voipRelay.VideoDataReceived += OnVoipRelayVideoReceived;
                }

                // Se connecter au relay server
                if (!_voipRelay.IsConnected)
                {
                    var connected = await _voipRelay.ConnectAsync();
                    if (!connected)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to connect to VOIP relay server");
                        return false;
                    }
                }

                // Démarrer l'appel via relay
                var callStarted = await _voipRelay.StartCallAsync(targetPeer, includeVideo);
                if (callStarted)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ VOIP relay call started to {targetPeer}");

                    // Setup audio relay
                    await SetupAudioRelayForPeer(targetPeer);

                    return true;
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to start VOIP relay call");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error in VOIP relay fallback: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ✅ FIX: S'assurer que la connexion relay est établie (pour appels entrants)
        /// </summary>
        private async Task EnsureRelayConnectionAsync()
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🔄 Ensuring VOIP relay connection...");

                // Obtenir l'IP du serveur
                string serverIP = "192.168.1.152"; // TODO: Get from configuration

                // Créer client VOIP relay si nécessaire
                if (_voipRelay == null)
                {
                    _voipRelay = new VOIPRelayClient(serverIP, _clientId);

                    // Wire events
                    _voipRelay.LogEvent += (msg) => LogEvent?.Invoke($"[VOIP-RELAY] {msg}");
                    _voipRelay.VoipMessageReceived += OnVoipRelayMessageReceived;
                    _voipRelay.AudioDataReceived += OnVoipRelayAudioReceived;
                    _voipRelay.VideoDataReceived += OnVoipRelayVideoReceived;
                }

                // Se connecter au relay server si pas encore connecté
                if (!_voipRelay.IsConnected)
                {
                    var connected = await _voipRelay.ConnectAsync();
                    if (connected)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ✅ Connected to VOIP relay for incoming call");
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to connect to VOIP relay for incoming call");
                    }
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ VOIP relay already connected");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error ensuring relay connection: {ex.Message}");
            }
        }

        private async Task SetupAudioRelayForPeer(string targetPeer)
        {
            // Setup audio streaming to relay server
            _audioCapture.AudioSampleReady += async (format, sample) =>
            {
                if (_voipRelay?.IsConnected == true)
                {
                    await _voipRelay.SendAudioDataAsync(targetPeer, sample);
                }
            };

            // ✅ FIX: Démarrer réellement l'audio capture !
            try
            {
                await _audioCapture.StartCaptureAsync();
                LogEvent?.Invoke($"[VOIP-Manager] 🎙️ Audio capture started for relay to {targetPeer}");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to start audio capture: {ex.Message}");
            }

            LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio relay setup completed for {targetPeer}");
        }

        private void OnVoipRelayMessageReceived(string fromPeer, string messageType)
        {
            LogEvent?.Invoke($"[VOIP-Manager] 📨 VOIP relay message from {fromPeer}: {messageType}");

            switch (messageType.ToLower())
            {
                case "call_start":
                    // ✅ FIX: Se connecter automatiquement au relay pour répondre
                    _ = Task.Run(async () => await EnsureRelayConnectionAsync());
                    IncomingCallReceived?.Invoke(fromPeer, "audio_relay");
                    break;
                case "call_accept":
                    if (_activeCalls.TryGetValue(fromPeer, out var call))
                    {
                        call.State = CallState.Connected;
                        CallStateChanged?.Invoke(fromPeer, CallState.Connected);
                    }
                    break;
                case "call_end":
                    _ = EndCallAsync(fromPeer);
                    break;
            }
        }

        private void OnVoipRelayAudioReceived(string fromPeer, byte[] audioData)
        {
            // Play received audio
            RemoteAudioReceived?.Invoke(fromPeer, audioData);
        }

        private void OnVoipRelayVideoReceived(string fromPeer, byte[] videoData)
        {
            // Process received video (convert to VideoFrame if needed)
            LogEvent?.Invoke($"[VOIP-Manager] 📹 Video data received from {fromPeer}: {videoData.Length} bytes");
        }

        public void Dispose()
        {
            try
            {
                // Terminer tous les appels actifs
                var activePeers = new List<string>(_activeCalls.Keys);
                foreach (var peer in activePeers)
                {
                    EndCallAsync(peer).Wait(1000);
                }

                _audioCapture?.Dispose();
                _videoCapture?.Dispose();
                _voipRelay?.Disconnect();

                LogEvent?.Invoke("[VOIP-Manager] Service disposed");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error during dispose: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Représente un appel VOIP actif
    /// </summary>
    public class VOIPCall
    {
        public string PeerName { get; set; } = "";
        public CallType CallType { get; set; }
        public CallState State { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration => EndTime?.Subtract(StartTime) ?? DateTime.Now.Subtract(StartTime);
    }

    /// <summary>
    /// Types d'appel supportés
    /// </summary>
    public enum CallType
    {
        AudioOnly,
        VideoCall
    }

    /// <summary>
    /// États d'un appel
    /// </summary>
    public enum CallState
    {
        Initiating,    // Préparation de l'appel
        Calling,       // Appel en cours (sonnerie)
        Ringing,       // Appel entrant (sonnerie)
        Connecting,    // Établissement de la connexion
        Connected,     // Appel établi
        Ended,         // Appel terminé
        Failed         // Appel échoué
    }
}