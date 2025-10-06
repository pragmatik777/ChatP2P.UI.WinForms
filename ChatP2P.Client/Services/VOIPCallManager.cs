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
        private readonly SimpleVirtualCameraService _virtualCamera; // ✅ NOUVEAU: Caméra virtuelle avec H.264
        private readonly OpusAudioStreamingService _opusStreaming; // ✅ OPUS: Professional streaming service
        private readonly VideoEncodingService _videoEncoder; // ✅ NOUVEAU: Encodage H.264/VP8 professionnel
        private volatile bool _videoEncoderInitialized = false; // ✅ FIX: Track encoder initialization state
        private EmguVideoDecoderService? _emguDecoder;

        // ✅ BUFFERING: Système de buffer intelligent pour réception vidéo
        private readonly Queue<byte[]> _videoReceiveBuffer = new();
        private readonly object _bufferLock = new object();
        private bool _isBuffering = true;
        private const int MIN_BUFFER_SIZE = 2; // ⚡ 30 FPS: Start avec seulement 2 frames pour latence minimale
        private const int MAX_BUFFER_SIZE = 15; // ⚡ 30 FPS: Buffer réduit (500ms à 30fps) pour temps réel
        private DateTime _lastBufferLog = DateTime.MinValue;
        private CancellationTokenSource? _renderingCancellation;

        // ✅ ADAPTIVE RENDERING: FPS adaptatif selon l'émetteur
        private double _receiverAdaptiveFPS = 30.0; // ⚡ 30 FPS: Start optimistic pour performance maximale
        private DateTime _lastFPSDetection = DateTime.MinValue;
        private readonly Queue<DateTime> _frameTimestamps = new(); // Pour calculer le FPS réel
        private readonly WebRTCDirectClient _webRtcClient;
        private VOIPRelayClient? _voipRelay;
        private PureAudioRelayClient? _pureAudioRelay; // ✅ ANCIEN: Canal audio pur TCP
        private UDPAudioRelayClient? _udpAudioRelay; // ✅ NOUVEAU: Canal audio UDP temps réel
        private UDPVideoRelayClient? _udpVideoRelay; // ✅ NOUVEAU: Canal vidéo UDP (port 8894)

        // ✅ NOUVEAU: Mode caméra (physique ou virtuelle)
        public bool UseVirtualCamera { get; set; } = false; // ✅ DEFAULT: Use real video capture with FFmpeg first

        // ✅ DEBUG: Expose video capture service for diagnostics
        public SimpleVideoCaptureService VideoCapture => _videoCapture;

        // Events pour l'interface utilisateur
        public event Action<string, CallState>? CallStateChanged;
        public event Action<string, VideoFrame>? RemoteVideoReceived;
        public event Action<string, byte[]>? RemoteAudioReceived;
        public event Action<string>? LogEvent;

        // Log utility method with intelligent routing
        private async void LogAudio(string message, bool forceLog = false)
        {
            LogEvent?.Invoke(message); // For backwards compatibility

            // Intelligent routing: Video keywords → video log, Audio keywords → audio log
            if (message.Contains("VIDEO") || message.Contains("H264") || message.Contains("H.264") ||
                message.Contains("encoded video") || message.Contains("VideoEncoder") ||
                message.Contains("video frame") || message.Contains("UDP-VIDEO") ||
                message.Contains("VirtCam") || message.Contains("encoding") ||
                message.Contains("FFmpeg") || message.Contains("Frame sent") ||
                message.Contains("active video calls") || message.Contains("libx264") ||
                message.Contains("VOIP-Encoder") || message.Contains("VirtCam-Encoder") ||
                message.Contains("Video encoding") || message.Contains("Virtual camera") ||
                message.Contains("video relay") || message.Contains("fragment"))
            {
                await ServiceLogHelper.LogToVideoAsync(message, forceLog);
            }
            else
            {
                await ServiceLogHelper.LogToAudioAsync(message, forceLog);
            }
        }
        public event Action<string, string>? IncomingCallReceived; // fromPeer, callType

        // ✅ NOUVEAU: Events pour signaling VOIP via MainWindow
        public event Func<string, string, string, string, Task>? SendVOIPSignal; // signalType, fromPeer, toPeer, data

        private string? _serverIP; // ✅ NOUVEAU: Store server IP
        private bool _audioCaptureEventWired = false; // ✅ FIX: Track event wiring to prevent duplicates

        // ⚡ VIDEO THROTTLING: Éviter surcharge processing
        private DateTime _lastVideoFrameProcessed = DateTime.MinValue;

        // ✅ NOUVEAU: Exposer OpusStreamingService pour l'interface utilisateur
        public OpusAudioStreamingService OpusStreamingService => _opusStreaming;

        public VOIPCallManager(string clientId, WebRTCDirectClient webRtcClient, SimpleVideoCaptureService? sharedVideoCapture = null)
        {
            _clientId = clientId;
            _webRtcClient = webRtcClient;
            // ❌ REMOVED: _audioCapture = new SimpleAudioCaptureService() - replaced by OpusAudioStreamingService

            // ✅ FIX: Utiliser le service de capture vidéo partagé depuis MainWindow si disponible
            _videoCapture = sharedVideoCapture ?? new SimpleVideoCaptureService();
            _virtualCamera = new SimpleVirtualCameraService(); // ✅ NOUVEAU: Caméra virtuelle
            _opusStreaming = new OpusAudioStreamingService(); // ✅ OPUS: Initialiser streaming professionnel
            _videoEncoder = new VideoEncodingService(); // ✅ NOUVEAU: Encodeur vidéo professionnel
            _pureAudioRelay = new PureAudioRelayClient(); // ✅ ANCIEN: Canal audio pur TCP
            _udpAudioRelay = new UDPAudioRelayClient(); // ✅ NOUVEAU: Canal audio UDP temps réel
            _udpVideoRelay = new UDPVideoRelayClient(); // ✅ NOUVEAU: Canal vidéo UDP

            // ✅ FIX: Wire les events des services de capture ET VIDÉO
            _videoCapture.LogEvent += (msg) => LogEvent?.Invoke($"[VOIP-Video] {msg}");
            _videoCapture.VideoFrameReady += OnVideoFrameReady; // ✅ FIX CRITIQUE: Connecter les frames vidéo !
            _virtualCamera.LogEvent += (msg) => LogEvent?.Invoke($"[VOIP-VirtualCam] {msg}"); // ✅ NOUVEAU
            _virtualCamera.VideoFrameReady += OnVideoFrameReady; // ✅ NOUVEAU: Caméra virtuelle vers pipeline
            _virtualCamera.EncodedVideoReady += OnEncodedVideoReady; // ✅ NOUVEAU: H.264 direct depuis caméra virtuelle
            _opusStreaming.LogEvent += (msg) => LogEvent?.Invoke($"[VOIP-Opus] {msg}"); // ✅ OPUS
            _videoEncoder.LogEvent += (msg) => LogEvent?.Invoke($"[VOIP-Encoder] {msg}"); // ✅ NOUVEAU
            _videoEncoder.EncodedVideoReady += OnEncodedVideoReady; // ✅ FIX CRITIQUE: Connecter l'encodeur vidéo
            _pureAudioRelay.LogEvent += (msg) => LogEvent?.Invoke($"[PURE-AUDIO-TCP] {msg}"); // ✅ ANCIEN
            _pureAudioRelay.AudioDataReceived += OnPureAudioReceived; // ✅ ANCIEN
            _udpAudioRelay.LogEvent += (msg) => LogEvent?.Invoke($"[UDP-AUDIO] {msg}"); // ✅ NOUVEAU
            _udpAudioRelay.AudioDataReceived += OnUDPAudioReceived; // ✅ NOUVEAU
            _udpVideoRelay.LogEvent += (msg) => LogEvent?.Invoke($"[UDP-VIDEO] {msg}"); // ✅ NOUVEAU
            _udpVideoRelay.VideoDataReceived += OnUDPVideoReceived; // ✅ NOUVEAU

            // ✅ OPUS: Initialize streaming service asynchronously
            _ = Task.Run(async () => await InitializeOpusStreamingAsync());

            // ✅ VIDEO: Initialize video encoding service asynchronously
            _ = Task.Run(async () => await InitializeVideoEncodingAsync());

            // ✅ VIRTUAL CAMERA: Initialize virtual camera with test content asynchronously
            _ = Task.Run(async () => await InitializeVirtualCameraAsync());

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
        /// Get the current server IP
        /// </summary>
        private string GetServerIP()
        {
            return _serverIP ?? "192.168.1.145"; // Fallback to default if not set
        }

        /// <summary>
        /// ✅ NOUVEAU: Contrôler la caméra virtuelle
        /// </summary>
        public async Task<bool> LoadVirtualVideoFileAsync(string filePath)
        {
            if (_virtualCamera == null) return false;
            return await _virtualCamera.LoadVideoFileAsync(filePath);
        }

        public async Task<bool> StartVirtualCameraAsync()
        {
            if (_virtualCamera == null) return false;
            UseVirtualCamera = true;
            LogEvent?.Invoke($"[VOIP-Manager] 📹 Switched to virtual camera mode");
            return await _virtualCamera.StartPlaybackAsync();
        }

        public async Task StopVirtualCameraAsync()
        {
            if (_virtualCamera == null) return;
            await _virtualCamera.StopPlaybackAsync();
            UseVirtualCamera = false;
            LogEvent?.Invoke($"[VOIP-Manager] 🎥 Switched to physical camera mode");
        }

        public async Task<bool> ChangeVirtualCameraCodecAsync(VideoCodecsEnum codec)
        {
            if (_virtualCamera == null) return false;
            return await _virtualCamera.ChangeCodecAsync(codec);
        }

        public string? GetVirtualCameraStats()
        {
            return _virtualCamera?.GetCameraStats();
        }

        public VideoCodecsEnum[] GetSupportedVideoCodecs()
        {
            return SimpleVirtualCameraService.GetSupportedCodecs();
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
        /// ✅ VIDEO: Initialize professional H.264/VP8 video encoding service
        /// </summary>
        private async Task InitializeVideoEncodingAsync()
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🎥 Initializing professional video encoding service...");

                // Utiliser H.264 par défaut pour qualité optimale
                var initialized = await _videoEncoder.InitializeAsync(SIPSorceryMedia.Abstractions.VideoCodecsEnum.H264);
                if (initialized)
                {
                    _videoEncoderInitialized = true; // ✅ FIX: Mark video encoder as ready
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ H.264 video encoder initialized successfully");
                    LogEvent?.Invoke($"[VOIP-Manager] 📊 {_videoEncoder.GetEncodingStats()}");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to initialize H.264 video encoder, trying VP8 fallback...");

                    // Fallback vers VP8 si H.264 échoue
                    var vp8Initialized = await _videoEncoder.InitializeAsync(SIPSorceryMedia.Abstractions.VideoCodecsEnum.VP8);
                    if (vp8Initialized)
                    {
                        _videoEncoderInitialized = true; // ✅ FIX: Mark video encoder as ready (VP8 fallback)
                        LogEvent?.Invoke($"[VOIP-Manager] ✅ VP8 video encoder initialized successfully (fallback)");
                        LogEvent?.Invoke($"[VOIP-Manager] 📊 {_videoEncoder.GetEncodingStats()}");
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to initialize video encoder (both H.264 and VP8)");
                    }
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error initializing video encoder: {ex.Message}");
                LogEvent?.Invoke($"[VOIP-Manager] 🔄 Video will fallback to raw RGB transmission");
            }
        }

        /// <summary>
        /// ✅ VIRTUAL CAMERA: Initialize virtual camera with test content
        /// </summary>
        private async Task InitializeVirtualCameraAsync()
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 📹 Initializing virtual camera...");

                // ✅ FIX: Tentative H.264 avec fallback automatique vers raw frames
                try
                {
                    var encoderInitialized = await _virtualCamera.InitializeEncoderAsync(SIPSorceryMedia.Abstractions.VideoCodecsEnum.H264);
                    if (encoderInitialized)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ✅ Virtual camera H.264 encoder initialized");
                    }
                    else
                    {
                        throw new Exception("H.264 encoder initialization failed");
                    }
                }
                catch (Exception encoderEx)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ⚠️ H.264 encoder failed ({encoderEx.Message}), disabling encoding");
                    _virtualCamera.IsEncodingEnabled = false; // Désactiver l'encodage
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Virtual camera configured for raw RGB frames");
                }

                LogEvent?.Invoke($"[VOIP-Manager] 🎬 Virtual camera ready for procedural content generation");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error initializing virtual camera: {ex.Message}");
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

                // ✅ FIX: Démarrer capture audio et vidéo (PLAYBOOK + CAPTURE)
                var audioStreamStarted = await _opusStreaming.StartStreamingAsync(); // ✅ OPUS PLAYBACK
                var audioCaptureStarted = await _opusStreaming.StartCaptureAsync(); // ✅ OPUS CAPTURE

                // ✅ NOUVEAU: Support caméra virtuelle OU physique
                bool videoStarted;
                if (UseVirtualCamera)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] 📹 Using virtual camera for video call");
                    videoStarted = await _virtualCamera.StartPlaybackAsync();
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] 🎥 Using physical camera for video call");
                    videoStarted = await _videoCapture.StartCaptureAsync();
                }

                // ✅ FIX RECEIVE-ONLY: Audio OK si playback marche, même sans capture (VMs sans micro)
                var audioStarted = audioStreamStarted; // Capture optionnelle pour receive-only

                // Diagnostic du mode audio
                if (audioStreamStarted && audioCaptureStarted)
                    LogEvent?.Invoke($"[VOIP-Manager] 🎤 Audio bidirectional (capture + playback)");
                else if (audioStreamStarted)
                    LogEvent?.Invoke($"[VOIP-Manager] 👂 Audio receive-only mode (no microphone)");

                if (!audioStarted || !videoStarted)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to start media capture (audio: {audioStarted}, video: {videoStarted})");
                    await EndCallAsync(targetPeer);
                    return false;
                }

                // ✅ FIX: Pour video calls, utiliser relay TCP directement (pas WebRTC P2P)
                LogEvent?.Invoke($"[VOIP-Manager] 📹 Using pure relay TCP for video call (no WebRTC needed)");

                // Se connecter au relay VOIP pour l'audio
                var relayConnected = await EnsureRelayConnectionForIncomingCallAsync();
                if (!relayConnected)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to connect to VOIP relay for video call");
                    await EndCallAsync(targetPeer);
                    return false;
                }

                // ✅ NOUVEAU: Se connecter au relay vidéo pur (port 8894)
                var videoRelayConnected = await EnsureUDPVideoRelayConnectionAsync();
                if (!videoRelayConnected)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to connect to pure video relay");
                    await EndCallAsync(targetPeer);
                    return false;
                }

                // ✅ FIX CRITIQUE: Démarrer la session vidéo UDP sur le serveur
                var sessionStarted = await _udpVideoRelay.StartSessionAsync(targetPeer);
                if (!sessionStarted)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to start UDP video session with {targetPeer}");
                    await EndCallAsync(targetPeer);
                    return false;
                }
                LogEvent?.Invoke($"[VOIP-Manager] ✅ UDP video session started with {targetPeer}");

                // Envoyer invitation d'appel vidéo via relay (pas d'offer WebRTC)
                await SendCallInviteAsync(targetPeer, "video", "relay");

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

                // ✅ FIX: Démarrer les captures nécessaires (PLAYBOOK + CAPTURE)
                var audioStreamStarted = await _opusStreaming.StartStreamingAsync(); // ✅ OPUS PLAYBACK
                var audioCaptureStarted = await _opusStreaming.StartCaptureAsync(); // ✅ OPUS CAPTURE

                // ✅ NOUVEAU: Support caméra virtuelle OU physique pour vidéo
                bool videoStarted = true;
                if (isVideo)
                {
                    if (UseVirtualCamera)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] 📹 Starting virtual camera for incoming video call");
                        videoStarted = await _virtualCamera.StartPlaybackAsync();
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] 🎥 Starting physical camera for incoming video call");
                        videoStarted = await _videoCapture.StartCaptureAsync();
                    }
                }

                // ✅ FIX RECEIVE-ONLY: Audio OK si playback marche, même sans capture (VMs sans micro)
                var audioStarted = audioStreamStarted; // Capture optionnelle pour receive-only

                if (!audioStarted || (isVideo && !videoStarted))
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to start media for incoming call (audio: {audioStarted}, video: {videoStarted})");
                    await EndCallAsync(fromPeer);
                    return false;
                }

                // ✅ FIX: Pour vidéo calls, utiliser relay pur directement (pas de WebRTC)
                if (isVideo)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] 📹 Video call detected - using pure relay mode (no WebRTC)");

                    // Se connecter aux relays audio ET vidéo
                    await EnsureRelayConnectionAsync();
                    var videoRelayConnected = await EnsureUDPVideoRelayConnectionAsync();

                    if (_voipRelay?.IsConnected == true && videoRelayConnected)
                    {
                        // ✅ FIX CRITIQUE: Démarrer la session vidéo UDP sur le serveur (côté acceptation)
                        var sessionStarted = await _udpVideoRelay.StartSessionAsync(fromPeer);
                        if (!sessionStarted)
                        {
                            LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to start UDP video session when accepting call from {fromPeer}");
                        }
                        else
                        {
                            LogEvent?.Invoke($"[VOIP-Manager] ✅ UDP video session started when accepting call from {fromPeer}");
                        }

                        // Accepter via le relay
                        var relaySuccess = await _voipRelay.AcceptCallAsync(fromPeer);
                        if (relaySuccess)
                        {
                            // Envoyer une réponse d'acceptation relay
                            await SendCallAcceptAsync(fromPeer, callType, "relay_accepted");

                            call.State = CallState.Connected;
                            CallStateChanged?.Invoke(fromPeer, CallState.Connected);

                            // ✅ FIX CRITIQUE: Setup audio relay pour VM2 (celui qui accepte)
                            await SetupAudioRelayForPeer(fromPeer);

                            LogEvent?.Invoke($"[VOIP-Manager] ✅ Video call accepted from {fromPeer} via pure relay");
                            return true;
                        }
                        else
                        {
                            LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to accept video call via relay");
                        }
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ❌ Cannot connect to video/audio relays for accepting call");
                    }

                    await EndCallAsync(fromPeer);
                    return false;
                }
                else
                {
                    // Audio call - utiliser WebRTC P2P d'abord, puis fallback relay
                    LogEvent?.Invoke($"[VOIP-Manager] 🎵 Audio call detected - trying P2P WebRTC first");

                    var answer = await ProcessMediaOfferAsync(fromPeer, offer, true, false);
                    if (answer != null)
                    {
                        // P2P WebRTC réussi
                        LogEvent?.Invoke($"[VOIP-Manager] ✅ P2P WebRTC answer created successfully");

                        await SendCallAcceptAsync(fromPeer, callType, answer);

                        call.State = CallState.Connected;
                        CallStateChanged?.Invoke(fromPeer, CallState.Connected);

                        LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio call accepted from {fromPeer} via P2P WebRTC");
                        return true;
                    }
                    else
                    {
                        // P2P WebRTC échoué - Fallback vers VOIP relay
                        LogEvent?.Invoke($"[VOIP-Manager] ⚠️ P2P WebRTC failed for audio, falling back to relay");

                        await EnsureRelayConnectionAsync();

                        if (_voipRelay?.IsConnected == true)
                        {
                            var relaySuccess = await _voipRelay.AcceptCallAsync(fromPeer);
                            if (relaySuccess)
                            {
                                await SendCallAcceptAsync(fromPeer, callType, "relay_accepted");

                                call.State = CallState.Connected;
                                CallStateChanged?.Invoke(fromPeer, CallState.Connected);

                                await SetupAudioRelayForPeer(fromPeer);

                                LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio call accepted from {fromPeer} via VOIP relay");
                                return true;
                            }
                            else
                            {
                                LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to accept audio call via VOIP relay");
                            }
                        }
                        else
                        {
                            LogEvent?.Invoke($"[VOIP-Manager] ❌ Cannot connect to VOIP relay for accepting audio call");
                        }

                        await EndCallAsync(fromPeer);
                        return false;
                    }
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

                    // ✅ BUFFERING: Nettoyer le buffer vidéo et arrêter le rendering
                    lock (_bufferLock)
                    {
                        _videoReceiveBuffer.Clear();
                        _isBuffering = true;
                        LogEvent?.Invoke($"[VOIP-Manager] 🧹 Video buffer cleared - no active calls");
                    }

                    // ✅ FPS DETECTION: Reset FPS detection
                    _frameTimestamps.Clear();
                    _receiverAdaptiveFPS = 30.0; // ⚡ Reset au 30 FPS optimal
                    _lastFPSDetection = DateTime.MinValue;

                    if (_renderingCancellation != null)
                    {
                        _renderingCancellation.Cancel();
                        _renderingCancellation.Dispose();
                        _renderingCancellation = null;
                        LogEvent?.Invoke($"[VOIP-Manager] 🛑 Video rendering thread stopped, FPS detection reset");
                    }
                }

                // ✅ NOUVEAU: Terminer session UDP audio
                if (_udpAudioRelay?.IsConnected == true)
                {
                    await _udpAudioRelay.EndSessionAsync(peer);
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ UDP audio session ended with {peer}");
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
        /// ✅ NOUVEAU: Assurer connexion au relay vidéo pur (port 8894)
        /// </summary>
        private async Task<bool> EnsureUDPVideoRelayConnectionAsync()
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 📹 Ensuring pure video relay connection (port 8894)...");

                // Vérifier la connexion existante
                if (_udpVideoRelay?.IsConnected == true)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Pure video relay already connected");
                    return true;
                }

                // Obtenir l'IP du serveur
                if (string.IsNullOrWhiteSpace(_serverIP))
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Server IP required for video relay connection");
                    return false;
                }

                // Connecter au relay vidéo avec le clientId (display name)
                var connected = await _udpVideoRelay.ConnectAsync(_serverIP, _clientId);
                if (connected)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Connected to pure video relay as {_clientId}");
                    return true;
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to connect to pure video relay");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error connecting to pure video relay: {ex.Message}");
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

                // ✅ NOUVEAU: Se connecter aux canaux audio ET vidéo purs (performance maximale)
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

                // ✅ NOUVEAU: Se connecter au canal UDP audio pour performance maximale
                LogEvent?.Invoke($"[VOIP-Manager] 🚀 Attempting UDP audio relay connection: serverIP={serverIP}, clientId={_clientId}");
                if (_udpAudioRelay != null && !_udpAudioRelay.IsConnected)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] 🚀 Trying to connect to UDP audio relay {serverIP}:8895...");
                    var udpConnected = await _udpAudioRelay.ConnectAsync(serverIP, _clientId);
                    if (udpConnected)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ✅ Connected to UDP audio relay channel (port 8895)");
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Failed to connect to UDP audio relay, using TCP fallback");
                    }
                }
                else if (_udpAudioRelay == null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ _udpAudioRelay is null!");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ℹ️ UDP audio relay already connected");
                }

                // ✅ NOUVEAU: Se connecter au canal vidéo pur si appel vidéo
                if (includeVideo && _udpVideoRelay != null && !_udpVideoRelay.IsConnected)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] 📹 Trying to connect to SIPSorcery video relay {serverIP}:8894...");
                    var videoConnected = await _udpVideoRelay.ConnectAsync(serverIP, _clientId);
                    if (videoConnected)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ✅ Connected to pure video relay channel (port 8894)");
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Failed to connect to pure video relay, using JSON fallback");
                    }
                }
                else if (includeVideo && _udpVideoRelay == null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ _udpVideoRelay is null for video call!");
                }
                else if (includeVideo)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ℹ️ Pure video relay already connected");
                }

                // Démarrer l'appel via relay
                var callStarted = await _voipRelay.StartCallAsync(targetPeer, includeVideo);
                if (callStarted)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ VOIP relay call started to {targetPeer}");

                    // ✅ NOUVEAU: Démarrer session UDP audio
                    if (_udpAudioRelay?.IsConnected == true)
                    {
                        await _udpAudioRelay.StartSessionAsync(targetPeer);
                        LogEvent?.Invoke($"[VOIP-Manager] ✅ UDP audio session started with {targetPeer}");
                    }

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

                // ✅ FIX: Se connecter aux canaux audio ET vidéo purs pour appels entrants
                LogEvent?.Invoke($"[VOIP-Manager] 🔧 Attempting pure relay connections for incoming call: serverIP={serverIP}, clientId={_clientId}");
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

                // ✅ NOUVEAU: Se connecter au canal UDP audio pour performance maximale
                if (_udpAudioRelay != null && !_udpAudioRelay.IsConnected)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] 🚀 Trying to connect to UDP audio relay {serverIP}:8895 for incoming call...");
                    var udpConnected = await _udpAudioRelay.ConnectAsync(serverIP, _clientId);
                    if (udpConnected)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ✅ Connected to UDP audio relay channel for incoming call (port 8895)");
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Failed to connect to UDP audio relay for incoming call, using TCP fallback");
                    }
                }
                else if (_udpAudioRelay == null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ _udpAudioRelay is null for incoming call!");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ℹ️ UDP audio relay already connected for incoming call");
                }

                // ✅ NOUVEAU: Se connecter au canal vidéo pur pour appels entrants aussi
                if (_udpVideoRelay != null && !_udpVideoRelay.IsConnected)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] 📹 Trying to connect to SIPSorcery video relay {serverIP}:8894 for incoming call...");
                    var videoConnected = await _udpVideoRelay.ConnectAsync(serverIP, _clientId);
                    if (videoConnected)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ✅ Connected to pure video relay channel for incoming call (port 8894)");
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Failed to connect to pure video relay for incoming call, using JSON fallback");
                    }
                }
                else if (_udpVideoRelay == null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ _udpVideoRelay is null for incoming call!");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ℹ️ Pure video relay already connected for incoming call");
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
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🔧 Setting up audio relay for peer: {targetPeer}");

                // ✅ CRITICAL FIX: S'assurer que l'audio relay écoute pour l'audio entrant
                if (_pureAudioRelay != null && _pureAudioRelay.IsConnected)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Pure audio relay already connected and listening");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] 🔧 Starting pure audio relay listening for incoming audio...");

                    // S'assurer que le pure audio relay est connecté et écoute
                    var serverIP = GetServerIP();
                    if (!string.IsNullOrEmpty(serverIP))
                    {
                        var connected = await _pureAudioRelay.ConnectAsync(_clientId, serverIP, 8893);
                        if (connected)
                        {
                            LogEvent?.Invoke($"[VOIP-Manager] ✅ Pure audio relay connected for receiving audio from {targetPeer}");
                        }
                        else
                        {
                            LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to connect pure audio relay for receiving");
                        }
                    }
                }

                // ✅ CRITICAL FIX: S'assurer que l'opus streaming est prêt pour la playback
                if (!_opusStreaming.IsStreaming)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] 🔧 Starting opus streaming for audio playback...");
                    var streamingStarted = await _opusStreaming.StartStreamingAsync();
                    if (streamingStarted)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ✅ Opus streaming started for audio playback");
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ❌ Failed to start opus streaming for playback");
                    }
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Opus streaming already active for playback");
                }

                LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio relay setup completed for {targetPeer}");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error setting up audio relay: {ex.Message}");
            }

            // ❌ REMOVED OLD CODE: _audioCapture.AudioSampleReady - OpusAudioStreamingService doesn't capture, only plays
            /*

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

                    // ✅ FIX CRITIQUE: Connecter l'event AudioCaptured une seule fois !
                    if (!_audioCaptureEventWired)
                    {
                        _opusStreaming.AudioCaptured += async (audioData) =>
                        {
                            // ✅ FIX: Trouver le peer actif dynamiquement au lieu d'utiliser closure
                            var activePeer = GetActiveCallPeer();
                            if (!string.IsNullOrEmpty(activePeer))
                            {
                                await HandleCapturedAudioData(activePeer, audioData);
                            }
                        };
                        _audioCaptureEventWired = true;
                        LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio capture event connected to relay transmission");
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ℹ️ Audio capture event already wired, skipping duplicate");
                    }
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
        /// ✅ FIX: Obtenir le peer actuellement en appel actif
        /// </summary>
        private string GetActiveCallPeer()
        {
            try
            {
                // Chercher un call en état Connected
                foreach (var kvp in _activeCalls)
                {
                    if (kvp.Value.State == CallState.Connected)
                    {
                        return kvp.Key;
                    }
                }

                // Si aucun call Connected, chercher Ringing/Calling
                foreach (var kvp in _activeCalls)
                {
                    if (kvp.Value.State == CallState.Ringing || kvp.Value.State == CallState.Calling)
                    {
                        return kvp.Key;
                    }
                }

                return "";
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error getting active call peer: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// ✅ BUFFERING: Thread de processing des frames vidéo bufferisées pour rendering fluide
        /// </summary>
        private async Task ProcessBufferedVideoFramesAsync(CancellationToken cancellationToken)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🎬 Starting buffered video frame processing thread");

                // ✅ H.264 DECODER: Initialiser le décodeur une seule fois
                if (_emguDecoder == null)
                {
                    _emguDecoder = new EmguVideoDecoderService();
                    _emguDecoder.LogEvent += (msg) => LogEvent?.Invoke($"[VOIP-Decoder] {msg}");
                }

                var frameCount = 0;
                var lastFPSUpdate = _receiverAdaptiveFPS;

                LogEvent?.Invoke($"[VOIP-Manager] 🎬 Starting adaptive rendering at {_receiverAdaptiveFPS:F1} FPS");

                while (!cancellationToken.IsCancellationRequested)
                {
                    // ✅ DYNAMIC FPS: Recalculer l'intervalle si le FPS a changé
                    var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _receiverAdaptiveFPS);

                    if (Math.Abs(_receiverAdaptiveFPS - lastFPSUpdate) > 0.5)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] 🔄 Rendering FPS updated: {lastFPSUpdate:F1} → {_receiverAdaptiveFPS:F1} FPS");
                        lastFPSUpdate = _receiverAdaptiveFPS;
                    }
                    byte[]? h264Data = null;

                    // ✅ BUFFER: Extraire une frame du buffer
                    lock (_bufferLock)
                    {
                        if (_videoReceiveBuffer.Count > 0)
                        {
                            h264Data = _videoReceiveBuffer.Dequeue();

                            // ✅ STARVATION PROTECTION: Redémarrer le buffering si on n'a plus assez de frames
                            if (_videoReceiveBuffer.Count < 3 && !_isBuffering)
                            {
                                _isBuffering = true;
                                LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Buffer starvation detected, resuming buffering...");
                            }
                        }
                    }

                    if (h264Data != null)
                    {
                        try
                        {
                            // ✅ H.264 BATCH DECODING: Utiliser le nouveau système de batch processing
                            byte[]? rgbData = null;
                            var decodeTime = TimeSpan.Zero;

                            // ⚡ ULTRA-FAST H.264 decode: no logging for performance
                            try
                            {
                                // EmguVideoDecoderService doesn't support direct H.264 decoding
                                // This would need a different approach for H.264 frames
                                LogEvent?.Invoke($"[VOIP-Manager] ⚠️ H.264 decoding not supported by EmguCV decoder");
                                continue;
                            }
                            catch
                            {
                                continue; // Skip cette frame et continuer
                            }

                            if (rgbData != null && rgbData.Length > 0)
                            {
                                var fromPeer = GetActiveVideoPeerName();

                                // ✅ RENDER: Créer VideoFrame et envoyer à l'UI
                                var videoFrame = new VideoFrame
                                {
                                    Width = 640,
                                    Height = 480,
                                    Data = rgbData,
                                    PixelFormat = VideoPixelFormatsEnum.Rgb,
                                    Timestamp = DateTime.UtcNow.Ticks
                                };

                                RemoteVideoReceived?.Invoke(fromPeer, videoFrame);

                                frameCount++;

                                // ✅ PERFORMANCE: Log avec timing de décodage
                                if (frameCount % 15 == 0)
                                {
                                    lock (_bufferLock)
                                    {
                                        LogEvent?.Invoke($"[VOIP-Manager] 🎬 Frame {frameCount} rendered (decode: {decodeTime.TotalMilliseconds:F1}ms, buffer: {_videoReceiveBuffer.Count})");
                                    }
                                }
                            }
                            else
                            {
                                LogEvent?.Invoke($"[VOIP-Manager] ⚠️ H.264 decode returned empty data (took {decodeTime.TotalMilliseconds:F1}ms)");
                            }
                        }
                        catch (Exception decodeEx)
                        {
                            LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Frame processing error: {decodeEx.Message}");
                        }
                    }
                    else if (!_isBuffering)
                    {
                        // Pas de frame dans le buffer mais on n'est pas en mode buffering
                        // Attendre un peu pour éviter de spammer le CPU
                        await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
                    }

                    // ✅ PERFORMANCE: Délai minimal si on a plein de frames à traiter
                    if (_videoReceiveBuffer.Count > 5)
                    {
                        // Buffer plein - traiter rapidement
                        await Task.Delay(frameInterval / 2, cancellationToken);
                    }
                    else
                    {
                        // Buffer normal - timing normal
                        await Task.Delay(frameInterval, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🛑 Buffered video processing stopped");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error in buffered video processing: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ FPS DETECTION: Détecter le FPS réel de l'émetteur via les timestamps d'arrivée
        /// </summary>
        private void DetectSenderFPS(DateTime frameArrivalTime)
        {
            try
            {
                // Ajouter le timestamp d'arrivée
                _frameTimestamps.Enqueue(frameArrivalTime);

                // Garder seulement les 30 derniers timestamps (1-2 secondes d'historique)
                while (_frameTimestamps.Count > 30)
                {
                    _frameTimestamps.Dequeue();
                }

                // Calculer le FPS seulement si on a assez d'échantillons et pas trop récemment
                if (_frameTimestamps.Count >= 10 &&
                    frameArrivalTime - _lastFPSDetection > TimeSpan.FromSeconds(3))
                {
                    var timestamps = _frameTimestamps.ToArray();
                    var timeSpan = timestamps[timestamps.Length - 1] - timestamps[0];
                    var frameCount = timestamps.Length - 1;

                    if (timeSpan.TotalSeconds > 0.5) // Au moins 500ms d'historique
                    {
                        var detectedFPS = frameCount / timeSpan.TotalSeconds;

                        // Filtrer les valeurs aberrantes (entre 5 et 60 FPS)
                        if (detectedFPS >= 5.0 && detectedFPS <= 60.0)
                        {
                            // Lissage : moyenne avec l'ancienne valeur pour éviter les variations brusques
                            var newFPS = (_receiverAdaptiveFPS * 0.7) + (detectedFPS * 0.3);

                            if (Math.Abs(newFPS - _receiverAdaptiveFPS) > 0.5) // Changement significatif
                            {
                                _receiverAdaptiveFPS = newFPS;
                                _lastFPSDetection = frameArrivalTime;

                                LogEvent?.Invoke($"[VOIP-Manager] 🎯 FPS adaptatif mis à jour: {_receiverAdaptiveFPS:F1} FPS (détecté: {detectedFPS:F1})");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Error in FPS detection: {ex.Message}");
            }
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

                LogEvent?.Invoke($"[VOIP-Manager] 🚀 Sending audio to relay: {audioData?.Length ?? 0} bytes");

                // ✅ PRIORITÉ 1: Utiliser canal UDP pour latence minimale !
                if (_udpAudioRelay != null && _udpAudioRelay.IsConnected)
                {
                    await _udpAudioRelay.SendAudioDataAsync(targetPeer, audioData);
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio sent via UDP relay channel ({audioData?.Length ?? 0} bytes) - ULTRA LOW LATENCY!");
                }
                // ✅ PRIORITÉ 2: Fallback vers canal TCP pur (sans JSON)
                else if (_pureAudioRelay != null && _pureAudioRelay.IsConnected)
                {
                    await _pureAudioRelay.SendAudioDataAsync(audioData);
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Audio sent via TCP PURE relay channel ({audioData?.Length ?? 0} bytes)");
                }
                // ✅ PRIORITÉ 3: Dernier fallback vers JSON relay
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ⚠️ UDP and TCP pure audio relays not connected, falling back to JSON relay");
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

        /// <summary>
        /// ✅ NOUVEAU: Traiter audio reçu du canal UDP (port 8895) - Latence minimale !
        /// </summary>
        private async void OnUDPAudioReceived(byte[] audioData)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🚀 UDP Audio received: {audioData.Length} bytes (ultra low latency!)");

                // ✅ OPUS: Professional real-time streaming via UDP - Performance optimale !
                if (_opusStreaming.IsStreaming)
                {
                    _opusStreaming.StreamAudioData(audioData);
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ UDP audio streamed to Opus buffer ({audioData.Length} bytes, buffer: {_opusStreaming.BufferLevel})");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Opus streaming not active for UDP audio, starting...");
                    await _opusStreaming.StartStreamingAsync();
                    _opusStreaming.StreamAudioData(audioData);
                }

                // Notifier l'UI (UDP audio en temps réel)
                RemoteAudioReceived?.Invoke("UDP-Audio-Relay", audioData);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error processing UDP audio: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ H.264 PURE: Traiter vidéo H.264 reçue et décoder avec FFmpeg streaming
        /// </summary>
        private async void OnUDPVideoReceived(byte[] h264Data)
        {
            try
            {
                // ✅ FPS DETECTION: Analyser les timestamps d'arrivée pour détecter le FPS émetteur
                var now = DateTime.UtcNow;
                DetectSenderFPS(now);

                // ✅ BUFFERING: Ajouter au buffer au lieu de décoder immédiatement
                lock (_bufferLock)
                {
                    // Ajouter au buffer avec protection overflow
                    if (_videoReceiveBuffer.Count >= MAX_BUFFER_SIZE)
                    {
                        // Enlever les frames les plus anciennes si buffer trop plein
                        _videoReceiveBuffer.Dequeue();
                    }

                    _videoReceiveBuffer.Enqueue(h264Data);

                    // Log progress périodiquement
                    if (now - _lastBufferLog > TimeSpan.FromSeconds(2))
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] 📦 Video buffer: {_videoReceiveBuffer.Count}/{MAX_BUFFER_SIZE} frames (FPS: {_receiverAdaptiveFPS:F1})");
                        _lastBufferLog = now;
                    }

                    // ✅ BUFFER THRESHOLD: Démarrer le rendering quand on a suffisamment de frames
                    if (_isBuffering && _videoReceiveBuffer.Count >= MIN_BUFFER_SIZE)
                    {
                        _isBuffering = false;
                        LogEvent?.Invoke($"[VOIP-Manager] 🚀 Buffer ready ({MIN_BUFFER_SIZE} frames), starting adaptive rendering at {_receiverAdaptiveFPS:F1} FPS...");

                        // Démarrer le thread de rendering adaptatif
                        _renderingCancellation = new CancellationTokenSource();
                        _ = Task.Run(() => ProcessBufferedVideoFramesAsync(_renderingCancellation.Token));
                    }
                }

                // Ne pas décoder ici - le faire dans le thread de rendering
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error buffering UDP video frame: {ex.Message}");
            }
        }



        /// <summary>
        /// ✅ NOUVEAU: Extraire le nom du peer depuis la session d'appel vidéo active
        /// </summary>
        private string GetActiveVideoPeerName()
        {
            try
            {
                // Chercher parmi les appels actifs pour trouver l'appel vidéo (tout état actif)
                foreach (var kvp in _activeCalls)
                {
                    var call = kvp.Value;
                    if (call.CallType == CallType.VideoCall &&
                        (call.State == CallState.Connected || call.State == CallState.Calling || call.State == CallState.Connecting))
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] 🎯 Found active video call with peer: {call.PeerName} (state: {call.State})");
                        return call.PeerName;
                    }
                }

                // Fallback si pas d'appel vidéo actif trouvé - diagnostiquer le problème
                LogEvent?.Invoke($"[VOIP-Manager] ⚠️ No active video call found, using fallback peer name");
                LogEvent?.Invoke($"[VOIP-Manager] 🔍 DIAGNOSTIC: Current active calls:");
                foreach (var kvp in _activeCalls)
                {
                    var call = kvp.Value;
                    LogEvent?.Invoke($"[VOIP-Manager] 🔍   - {call.PeerName}: Type={call.CallType}, State={call.State}");
                }
                return "Unknown-Video-Peer";
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error finding active video peer: {ex.Message}");
                return "Error-Video-Peer";
            }
        }

        /// <summary>
        /// ✅ FIX CRITIQUE: Traiter les frames vidéo générées, les encoder et les transmettre aux peers
        /// </summary>
        private async void OnVideoFrameReady(VideoFrame frame)
        {
            try
            {
                // ✅ FIX: Vérifier si frame null (plus de contenu procédural)
                if (frame == null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ⏭️ Skipping null video frame (no real video content)");
                    return;
                }

                // ⚡ PERFORMANCE: Skip excessive logging

                // ✅ H.264 PURE: Encoder OBLIGATOIREMENT en H.264, pas de fallback
                if (!_videoEncoderInitialized || _videoEncoder == null)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ Video encoder not initialized, skipping frame (H.264 REQUIRED)");
                    return;
                }

                // ⚠️ TESTING: DISABLE ENCODING BUT PASS RGB TO UI FOR DISPLAY (THROTTLED)

                // ⚡ THROTTLE: Limiter à 10 FPS max pour éviter surcharge
                var now = DateTime.UtcNow;
                if (now - _lastVideoFrameProcessed < TimeSpan.FromMilliseconds(100)) // 10 FPS max
                {
                    return; // Skip frame pour éviter overload
                }
                _lastVideoFrameProcessed = now;

                // Pass RGB frame directly to UI for local display (throttled)
                try
                {
                    RemoteVideoReceived?.Invoke("LOCAL_DISPLAY", frame);
                }
                catch (Exception uiEx)
                {
                    // Silent catch pour performance
                }
                return;

                byte[]? h264Data;
                try
                {
                    LogEvent?.Invoke($"[VOIP-Manager] 🎯 Encoding RGB frame to H.264...");
                    h264Data = await _videoEncoder.EncodeFrameAsync(frame);

                    if (h264Data == null || h264Data.Length == 0)
                    {
                        LogEvent?.Invoke($"[VOIP-Manager] ❌ H.264 encoding failed, skipping frame (NO FALLBACK)");
                        return;
                    }

                    LogEvent?.Invoke($"[VOIP-Manager] ✅ H.264 encoding success: {frame.Data.Length}B → {h264Data.Length}B");
                }
                catch (Exception encodingEx)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ H.264 encoding error: {encodingEx.Message}, skipping frame");
                    return;
                }

                // Transmettre le H.264 à tous les peers en appel vidéo actif
                var videoCallsFound = 0;
                foreach (var call in _activeCalls.Values)
                {
                    if (call.CallType == CallType.VideoCall && (call.State == CallState.Connected || call.State == CallState.Calling))
                    {
                        videoCallsFound++;
                        await SendVideoFrameToPeerAsync(call.PeerName, h264Data, true, _videoEncoder.SelectedCodec);
                        LogEvent?.Invoke($"[VOIP-Manager] 📹 H.264 frame sent to {call.PeerName} (state: {call.State})");
                    }
                }

                if (videoCallsFound == 0)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ⚠️ No active video calls found to send frame to (have {_activeCalls.Count} total calls)");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ Frame sent to {videoCallsFound} active video call(s)");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error processing video frame: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Gestionnaire pour vidéo déjà encodée de la caméra virtuelle
        /// </summary>
        private async void OnEncodedVideoReady(byte[] encodedData)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🎯🎯🎯 OnEncodedVideoReady CALLED: {encodedData?.Length ?? -1} bytes 🎯🎯🎯");
                await ServiceLogHelper.LogToVideoAsync($"[VOIP-Manager] 🎯🎯🎯 OnEncodedVideoReady CALLED: {encodedData?.Length ?? -1} bytes 🎯🎯🎯", true); // Force log
                LogEvent?.Invoke($"[VOIP-Manager] 🔍 DEBUG: Active calls count: {_activeCalls.Count}");

                // ✅ DEBUG: Log all active calls
                foreach (var kvp in _activeCalls)
                {
                    var call = kvp.Value;
                    LogEvent?.Invoke($"[VOIP-Manager] 🔍 DEBUG: Call {call.PeerName} - Type: {call.CallType}, State: {call.State}");
                }

                // Transmettre directement la vidéo encodée (pas besoin de ré-encoder)
                var videoCallsFound = 0;
                foreach (var call in _activeCalls.Values)
                {
                    if (call.CallType == CallType.VideoCall && (call.State == CallState.Connected || call.State == CallState.Calling))
                    {
                        videoCallsFound++;
                        LogEvent?.Invoke($"[VOIP-Manager] ✅ FOUND active video call: {call.PeerName} (state: {call.State})");
                        await SendVideoFrameToPeerAsync(call.PeerName, encodedData, true, _virtualCamera.SelectedCodec);
                        LogEvent?.Invoke($"[VOIP-Manager] 📹 Encoded H.264/VP8 frame sent to {call.PeerName} (state: {call.State})");
                    }
                }

                if (videoCallsFound == 0)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ⚠️ No active video calls found for encoded frame transmission");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌❌❌ CRITICAL ERROR in OnEncodedVideoReady: {ex.Message} ❌❌❌");
                await ServiceLogHelper.LogToVideoAsync($"[VOIP-Manager] ❌❌❌ CRITICAL ERROR in OnEncodedVideoReady: {ex.Message} ❌❌❌", true); // Force log
                await ServiceLogHelper.LogToVideoAsync($"[VOIP-Manager] ❌❌❌ Stack trace: {ex.StackTrace} ❌❌❌", true); // Force log
                await ServiceLogHelper.LogToVideoAsync($"[VOIP-Manager] ❌❌❌ Exception type: {ex.GetType().Name} ❌❌❌", true); // Force log
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Transmettre une frame vidéo (encodée ou raw) à un peer spécifique
        /// </summary>
        private async Task SendVideoFrameToPeerAsync(string peerName, byte[] videoData, bool isEncoded, SIPSorceryMedia.Abstractions.VideoCodecsEnum codec)
        {
            try
            {
                var formatInfo = isEncoded ? $"{codec} encoded" : "RGB raw";
                LogEvent?.Invoke($"[VOIP-Manager] 📤 Sending {formatInfo} video data to {peerName}: {videoData.Length} bytes");

                // ✅ NOUVEAU: Debug des connexions relais
                LogEvent?.Invoke($"[VOIP-Manager] 🔍 DIAGNOSTIC: SIPSorceryVideoRelay connected: {_udpVideoRelay?.IsConnected ?? false}");
                LogEvent?.Invoke($"[VOIP-Manager] 🔍 DIAGNOSTIC: VoipRelay connected: {_voipRelay?.IsConnected ?? false}");
                LogEvent?.Invoke($"[VOIP-Manager] 🔍 DIAGNOSTIC: WebRTC client available: {_webRtcClient != null}");

                // ✅ FIX: Essayer P2P WebRTC d'abord, puis fallback vers pure video relay
                if (_webRtcClient != null)
                {
                    // TODO: Ajouter transmission vidéo via WebRTC DataChannels avec header codec
                    LogEvent?.Invoke($"[VOIP-Manager] 🚧 TODO: WebRTC video transmission to {peerName} ({videoData.Length} bytes, {formatInfo})");
                }

                // ✅ UDP VIDÉO SEUL: Pure Video Relay (port 8894) - H.264 direct uniquement !
                if (_udpVideoRelay != null && _udpVideoRelay.IsConnected)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] 🚀 Sending via UDP Video Relay (port 8894)...");
                    var sendResult = await _udpVideoRelay.SendVideoDataAsync(peerName, videoData);
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ {formatInfo} H.264 sent via UDP relay to {peerName} ({videoData.Length} bytes) - Result: {sendResult}");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ❌ UDP Video Relay NOT CONNECTED - cannot send video to {peerName}");
                    LogEvent?.Invoke($"[VOIP-Manager] 🔍 UDP Video Relay status: {(_udpVideoRelay == null ? "NULL" : $"Connected={_udpVideoRelay.IsConnected}")}");
                    LogEvent?.Invoke($"[VOIP-Manager] ⚠️ Video transmission REQUIRES UDP connection on port 8894");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error sending video frame to {peerName}: {ex.Message}");
            }
        }


        private void OnVoipRelayVideoReceived(string fromPeer, byte[] videoData)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 📹 Video data received from {fromPeer}: {videoData.Length} bytes");

                // ✅ NOUVEAU: Convertir les données vidéo reçues en VideoFrame et notifier l'UI
                var videoFrame = new VideoFrame
                {
                    Width = 640, // Taille standard pour l'instant
                    Height = 480,
                    Data = videoData,
                    PixelFormat = VideoPixelFormatsEnum.Rgb,
                    Timestamp = DateTime.UtcNow.Ticks
                };

                // Notifier l'UI de la frame vidéo reçue
                RemoteVideoReceived?.Invoke(fromPeer, videoFrame);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error processing received video: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Gestionnaire d'échantillons vidéo encodés H.264/VP8 depuis VideoEncodingService
        /// </summary>
        private async void OnFFmpegEncodedSample(uint durationRtpUnits, byte[] sample, int width, int height)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🎥 Video encoded sample ready: {sample.Length} bytes, {width}x{height}, codec: {_videoEncoder.SelectedCodec}");

                // Transmettre l'échantillon encodé à tous les peers connectés en appel vidéo
                var videoCallsFound = 0;
                foreach (var call in _activeCalls.Values)
                {
                    if (call.CallType == CallType.VideoCall && call.State == CallState.Connected)
                    {
                        videoCallsFound++;
                        // Envoyer directement les données encodées H.264/VP8
                        await SendVideoFrameToPeerAsync(call.PeerName, sample, true, _videoEncoder.SelectedCodec);
                    }
                }

                if (videoCallsFound == 0)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ⚠️ No active video calls found for FFmpeg encoded sample (have {_activeCalls.Count} total calls)");
                }
                else
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ✅ FFmpeg encoded sample sent to {videoCallsFound} active video call(s)");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error processing FFmpeg encoded sample: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Gestionnaire d'échantillons vidéo raw depuis VideoEncodingService (fallback)
        /// </summary>
        private async void OnFFmpegRawSample(byte[] sample, uint width, uint height, VideoPixelFormatsEnum pixelFormat)
        {
            try
            {
                LogEvent?.Invoke($"[VOIP-Manager] 🎞️ FFmpeg raw sample ready: {sample.Length} bytes, {width}x{height}, format: {pixelFormat}");

                // Transmettre l'échantillon raw à tous les peers connectés en appel vidéo (fallback si encodage échoue)
                var videoCallsFound = 0;
                foreach (var call in _activeCalls.Values)
                {
                    if (call.CallType == CallType.VideoCall && call.State == CallState.Connected)
                    {
                        videoCallsFound++;
                        // Envoyer les données raw comme fallback
                        await SendVideoFrameToPeerAsync(call.PeerName, sample, false, VideoCodecsEnum.H264); // Raw video data
                    }
                }

                if (videoCallsFound > 0)
                {
                    LogEvent?.Invoke($"[VOIP-Manager] ⚠️ FFmpeg raw sample sent to {videoCallsFound} call(s) (fallback mode)");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VOIP-Manager] ❌ Error processing FFmpeg raw sample: {ex.Message}");
            }
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
                _virtualCamera?.Dispose(); // ✅ NOUVEAU: Nettoyer caméra virtuelle
                _videoEncoder?.Dispose(); // ✅ NOUVEAU: Nettoyer video encoder
                // ❌ DUPLICATE REMOVED: _opusStreaming?.Dispose() already called above
                _pureAudioRelay?.Dispose(); // ✅ ANCIEN: Nettoyer canal audio pur TCP
                _udpAudioRelay?.Dispose(); // ✅ NOUVEAU: Nettoyer canal audio UDP
                _udpVideoRelay?.Dispose(); // ✅ NOUVEAU: Nettoyer canal vidéo pur
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