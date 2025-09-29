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
        // ❌ REMOVED: SimpleAudioCaptureService _audioCapture - replaced by OpusAudioStreamingService
        private readonly SimpleVideoCaptureService _videoCapture;
        private readonly OpusAudioStreamingService _opusStreaming; // ✅ OPUS: Professional streaming service
        private readonly WebRTCDirectClient _webRtcClient;
        private VOIPRelayClient? _voipRelay;
        private PureAudioRelayClient? _pureAudioRelay; // ✅ NOUVEAU: Canal audio pur

        // Events pour l'interface utilisateur
        public event Action<string, CallState>? CallStateChanged;
        public event Action<string, VideoFrame>? RemoteVideoReceived;
        public event Action<string, byte[]>? RemoteAudioReceived;
        public event Action<string>? LogEvent;

        // Log utility method
        private async void LogAudio(string message, bool forceLog = false)
        {
            LogEvent?.Invoke(message); // For backwards compatibility
            await ServiceLogHelper.LogToAudioAsync(message, forceLog);
        }
        public event Action<string, string>? IncomingCallReceived; // fromPeer, callType

        // ✅ NOUVEAU: Events pour signaling VOIP via MainWindow
        public event Func<string, string, string, string, Task>? SendVOIPSignal; // signalType, fromPeer, toPeer, data

        private string? _serverIP; // ✅ NOUVEAU: Store server IP

        // ✅ NOUVEAU: Exposer OpusStreamingService pour l'interface utilisateur
        public OpusAudioStreamingService OpusStreamingService => _opusStreaming;

        public VOIPCallManager(string clientId, WebRTCDirectClient webRtcClient)
        {
            _clientId = clientId;
            _webRtcClient = webRtcClient;
            // ❌ REMOVED: _audioCapture = new SimpleAudioCaptureService() - replaced by OpusAudioStreamingService
            _videoCapture = new SimpleVideoCaptureService();
            _opusStreaming = new OpusAudioStreamingService(); // ✅ OPUS: Initialiser streaming professionnel
            _pureAudioRelay = new PureAudioRelayClient(); // ✅ NOUVEAU: Canal audio pur

            // ❌ REMOVED: Wire les events des services de capture
            // ❌ REMOVED: _audioCapture.LogEvent += (msg) => LogEvent?.Invoke($"[VOIP-Audio] {msg}");
            _videoCapture.LogEvent += (msg) => LogEvent?.Invoke($"[VOIP-Video] {msg}");
            _opusStreaming.LogEvent += (msg) => LogEvent?.Invoke($"[VOIP-Opus] {msg}"); // ✅ OPUS
            _pureAudioRelay.LogEvent += (msg) => LogEvent?.Invoke($"[PURE-AUDIO] {msg}"); // ✅ NOUVEAU
            _pureAudioRelay.AudioDataReceived += OnPureAudioReceived; // ✅ NOUVEAU

            // ✅ OPUS: Initialize streaming service asynchronously
            _ = Task.Run(async () => await InitializeOpusStreamingAsync());

            LogEvent?.Invoke($"[VOIP-Manager] Initialized for client: {_clientId}");
            LogEvent?.Invoke($"[VOIP-Manager] 🔍 DIAGNOSTIC: Using clientId '{_clientId}' for VOIP signaling");
        }

        /// <summary>
        /// ✅ NOUVEAU: Set audio devices for VOIP services
        /// </summary>
        public void SetAudioDevices(string microphoneDevice, string speakerDevice)
        {
            LogEvent?.Invoke($"[VOIP-Manager] 🎤🔊 Setting audio devices: Mic={microphoneDevice}, Speaker={speakerDevice}");

            // ✅ FIX: Configure BOTH microphone and speaker devices for OpusAudioStreamingService
            if (_opusStreaming != null)
            {
                _opusStreaming.SetMicrophoneDevice(microphoneDevice); // ✅ NOUVEAU: Microphone
                _opusStreaming.SetSpeakerDevice(speakerDevice);       // Speaker existant
            }

            LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio devices configured successfully");

            // 🔧 DEBUG: Diagnostiquer l'état audio après configuration
            if (_opusStreaming != null)
                _opusStreaming.DiagnoseAudioState();
        }

        /// <summary>
        /// ✅ NOUVEAU: Set server IP from MainWindow textbox
        /// </summary>
        public void SetServerIP(string serverIP)
        {
            _serverIP = serverIP;
            LogEvent?.Invoke($"[VOIP-Manager] 🔧 Server IP set to: {serverIP}");
        }

        /// <summary>
        /// ✅ OPUS: Initialize professional audio streaming service
        /// </summary>
        private async Task InitializeOpusStreamingAsync()
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🎵 Initializing professional Opus streaming service...");

                var initialized = await _opusStreaming.InitializeAsync();
                if (initialized)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Opus streaming service initialized successfully");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to initialize Opus streaming service");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error initializing Opus streaming: {ex.Message}");
            }
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

                // ✅ OPUS: Start professional streaming service instead of old capture
                LogEvent?.Invoke($"[VOIP-Manager] 🎵 Starting Opus streaming service...");
                var audioStarted = await _opusStreaming.StartStreamingAsync();
                LogEvent?.Invoke($"[VOIP-Manager] ✅ Opus streaming StartStreamingAsync returned: {audioStarted}");

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

                // ✅ FIX: Démarrer capture audio et vidéo (PLAYBACK + CAPTURE)
                var audioStreamStarted = await _opusStreaming.StartStreamingAsync(); // ✅ OPUS PLAYBACK
                var audioCaptureStarted = await _opusStreaming.StartCaptureAsync(); // ✅ OPUS CAPTURE
                var videoStarted = await _videoCapture.StartCaptureAsync();

                var audioStarted = audioStreamStarted && audioCaptureStarted;

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

                // ✅ FIX: Démarrer les captures nécessaires (PLAYBACK + CAPTURE)
                var audioStreamStarted = await _opusStreaming.StartStreamingAsync(); // ✅ OPUS PLAYBACK
                var audioCaptureStarted = await _opusStreaming.StartCaptureAsync(); // ✅ OPUS CAPTURE
                var videoStarted = isVideo ? await _videoCapture.StartCaptureAsync() : true;

                var audioStarted = audioStreamStarted && audioCaptureStarted;

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

                // Arrêter les captures et streaming si plus d'appels actifs
                if (_activeCalls.Count == 0)
                {
                    await _opusStreaming.StopStreamingAsync(); // ✅ OPUS
                    await _videoCapture.StopCaptureAsync();

                    // ✅ OPUS: Stop streaming when no active calls
                    if (_opusStreaming.IsStreaming)
                    {
                        await _opusStreaming.StopStreamingAsync();
                        LogEvent?.Invoke($"[VOIP-Manager] 🎵 Opus streaming stopped - no active calls");
                    }
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

                // ✅ FIX: Obtenir l'IP du serveur depuis la textbox MainWindow
                if (string.IsNullOrWhiteSpace(_serverIP))
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Server IP is required but not set from textbox!");
                    return false;
                }
                string serverIP = _serverIP;
                LogEvent?.Invoke($"[VOIP-Manager] 🔧 Using server IP: {serverIP} (from textbox: {_serverIP != null})");;

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

                // Se connecter au relay server (signaling)
                if (!_voipRelay.IsConnected)
                {
                    var connected = await _voipRelay.ConnectAsync();
                    if (!connected)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to connect to VOIP relay server");
                        return false;
                    }
                }

                // ✅ NOUVEAU: Se connecter au canal audio pur (performance maximale)
                LogEvent?.Invoke($"[VOIP-Manager] 🔧 Attempting pure audio relay connection: serverIP={serverIP}, clientId={_clientId}");
                if (_pureAudioRelay != null && !_pureAudioRelay.IsConnected)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] 🎤 Trying to connect to pure audio relay {serverIP}:8893...");
                    var pureConnected = await _pureAudioRelay.ConnectAsync(_clientId, serverIP, 8893);
                    if (pureConnected)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ✅ Connected to pure audio relay channel (port 8893)");
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Failed to connect to pure audio relay, using JSON fallback");
                    }
                }
                else if (_pureAudioRelay == null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ _pureAudioRelay is null!");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ℹ️ Pure audio relay already connected");
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
        private async Task<bool> EnsureRelayConnectionAsync()
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🔄 Ensuring VOIP relay connection...");

                // ✅ FIX: Obtenir l'IP du serveur depuis la textbox MainWindow
                if (string.IsNullOrWhiteSpace(_serverIP))
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Server IP is required but not set from textbox!");
                    return false;
                }
                string serverIP = _serverIP;

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

                // ✅ FIX: Se connecter au canal audio pur pour appels entrants
                LogEvent?.Invoke($"[VOIP-Manager] 🔧 Attempting pure audio relay connection for incoming call: serverIP={serverIP}, clientId={_clientId}");
                if (_pureAudioRelay != null && !_pureAudioRelay.IsConnected)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] 🎤 Trying to connect to pure audio relay {serverIP}:8893 for incoming call...");
                    var pureConnected = await _pureAudioRelay.ConnectAsync(_clientId, serverIP, 8893);
                    if (pureConnected)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ✅ Connected to pure audio relay channel for incoming call (port 8893)");
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Failed to connect to pure audio relay for incoming call, using JSON fallback");
                    }
                }
                else if (_pureAudioRelay == null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ _pureAudioRelay is null for incoming call!");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ℹ️ Pure audio relay already connected for incoming call");
                }

                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error ensuring relay connection: {ex.Message}");
                return false;
            }
        }

        private async Task SetupAudioRelayForPeer(string targetPeer)
        {
            // Setup audio streaming to relay server
            // ❌ REMOVED: _audioCapture.AudioSampleReady - OpusAudioStreamingService doesn't capture, only plays
            // TODO: If audio capture needed, implement proper audio capture for VOIP
            /*
            _audioCapture.AudioSampleReady += async (format, sample) =>
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🎵 Audio sample ready: {sample?.Length ?? 0} bytes");

                if (_voipRelay == null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ VOIP relay is null!");
                    return;
                }

                if (!_voipRelay.IsConnected)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ VOIP relay not connected, state: {_voipRelay.IsConnected}");
                    return;
                }

                LogEvent?.Invoke($"[VOIP-Manager] 🚀 Sending PURE audio: {sample?.Length ?? 0} bytes (no JSON overhead!)");

                // ✅ NOUVEAU: Utiliser canal audio pur au lieu de JSON !
                if (_pureAudioRelay != null && _pureAudioRelay.IsConnected)
                {
                    await _pureAudioRelay.SendAudioDataAsync(sample);
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Pure audio relay not connected, falling back to JSON relay");
                    await _voipRelay.SendAudioDataAsync(targetPeer, sample);
                }
            };
            */

            // ✅ FIX: Démarrer réellement l'audio capture avec OpusAudioStreamingService !
            try
            {
                // ✅ FIX: Démarrer la capture audio avec le nouveau service OpusStreaming
                var captureStarted = await _opusStreaming.StartCaptureAsync();
                if (captureStarted)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio capture started for relay to {targetPeer}");

                    // ✅ FIX CRITIQUE: Connecter l'event AudioCaptured à la transmission relay !
                    _opusStreaming.AudioCaptured += async (audioData) =>
                    {
                        await HandleCapturedAudioData(targetPeer, audioData);
                    };

                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio capture event connected to relay transmission");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to start audio capture for relay to {targetPeer}");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to start audio capture: {ex.Message}");
            }

            LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio relay setup completed for {targetPeer}");
        }

        /// <summary>
        /// ✅ FIX CRITIQUE: Gérer les données audio capturées et les transmettre au relay
        /// </summary>
        private async Task HandleCapturedAudioData(string targetPeer, byte[] audioData)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🎵 Audio captured: {audioData?.Length ?? 0} bytes for {targetPeer}");

                if (_voipRelay == null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ VOIP relay is null!");
                    return;
                }

                if (!_voipRelay.IsConnected)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ VOIP relay not connected, state: {_voipRelay.IsConnected}");
                    return;
                }

                LogEvent?.Invoke($"[VOIP-Manager] 🚀 Sending audio to relay: {audioData?.Length ?? 0} bytes (no JSON overhead!)");

                // ✅ PRIORITÉ: Utiliser canal audio pur pour performance maximale !
                if (_pureAudioRelay != null && _pureAudioRelay.IsConnected)
                {
                    await _pureAudioRelay.SendAudioDataAsync(audioData);
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio sent via PURE relay channel ({audioData?.Length ?? 0} bytes)");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Pure audio relay not connected, falling back to JSON relay");
                    await _voipRelay.SendAudioDataAsync(targetPeer, audioData);
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio sent via JSON relay fallback ({audioData?.Length ?? 0} bytes)");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error handling captured audio: {ex.Message}");
            }
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

        private async void OnVoipRelayAudioReceived(string fromPeer, byte[] audioData)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🔊 Audio received from {fromPeer}: {audioData.Length} bytes");

                // ✅ OPUS: Stream audio data to professional buffer
                if (_opusStreaming.IsStreaming)
                {
                    _opusStreaming.StreamAudioData(audioData);
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio streamed to Opus buffer from {fromPeer} ({audioData.Length} bytes)");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Opus streaming not active, starting...");
                    await _opusStreaming.StartStreamingAsync();
                    _opusStreaming.StreamAudioData(audioData);
                }

                // Notifier aussi l'UI via l'événement
                RemoteAudioReceived?.Invoke(fromPeer, audioData);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error processing audio from {fromPeer}: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Traiter audio reçu du canal pur (port 8893) - Performance maximale !
        /// </summary>
        private async void OnPureAudioReceived(byte[] audioData)
        {
            try
            {
                // Audio reception confirmed - no debug beep needed anymore
                LogEvent?.Invoke($"[VOIP-Manager] 🎵 PURE Audio received: {audioData.Length} bytes (no JSON overhead!)");

                // ✅ OPUS: Professional real-time streaming - Qualité P2P niveau!
                if (_opusStreaming.IsStreaming)
                {
                    _opusStreaming.StreamAudioData(audioData);
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Pure audio streamed to Opus buffer ({audioData.Length} bytes, buffer: {_opusStreaming.BufferLevel})");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Opus streaming not active for pure audio, starting...");
                    await _opusStreaming.StartStreamingAsync();
                    _opusStreaming.StreamAudioData(audioData);
                }

                // Notifier l'UI (fromPeer inconnu en mode pur, utiliser placeholder)
                RemoteAudioReceived?.Invoke("Pure-Audio-Relay", audioData);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error processing pure audio: {ex.Message}");
            }
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

                // ❌ REMOVED: _audioCapture?.Dispose() - replaced with OpusAudioStreamingService
                _opusStreaming?.Dispose(); // ✅ OPUS
                _videoCapture?.Dispose();
                // ❌ DUPLICATE REMOVED: _opusStreaming?.Dispose() already called above
                _pureAudioRelay?.Dispose(); // ✅ NOUVEAU: Nettoyer canal audio pur
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