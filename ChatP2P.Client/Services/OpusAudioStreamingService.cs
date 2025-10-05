using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Media;
using System.IO;
using System.Linq;
using NAudio.Wave;
using Concentus.Structs;
using Concentus.Enums;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// 🎵 Service de streaming audio professionnel en temps réel avec CAPTURE et PLAYBACK
    /// Remplace le système WAV archaïque par un streaming bidirectionnel complet
    /// Version compatible - évite les complexités SDL2 mais garde la performance
    /// </summary>
    public class OpusAudioStreamingService : IDisposable
    {
        private readonly object _lock = new object();
        private bool _isStreaming = false;
        private bool _isPlaying = false;
        private bool _isCapturing = false; // ✅ NOUVEAU: État de capture

        // Buffer circulaire pour streaming temps réel (PLAYBACK)
        private readonly ConcurrentQueue<byte[]> _audioBuffer = new();
        private readonly int _maxBufferSize = 10; // 🎯 OPUS-ALIGNED: 10 frames (200ms) optimal pour Opus 20ms frames
        private readonly int _minBufferSize = 2; // 🎯 OPUS-SYNC: 2 frames (40ms) minimum pour éviter underrun
        private readonly Timer? _playbackTimer;
        private CancellationTokenSource? _streamingCts;

        // ✅ NOUVEAU: Buffer et timer pour CAPTURE audio
        private readonly ConcurrentQueue<byte[]> _captureBuffer = new();
        private readonly Timer? _captureTimer;
        private readonly int _maxCaptureBufferSize = 10;

        // Audio player optimisé
        private SoundPlayer? _currentPlayer;
        private WaveOutEvent? _waveOut; // 🔧 TEST: Direct PCM playback
        private BufferedWaveProvider? _bufferedProvider; // 🔧 TEST: PCM buffer streaming

        // ✅ NOUVEAU: Codecs Opus pour traitement audio bidirectionnel
        private OpusDecoder? _opusDecoder;
        private OpusEncoder? _opusEncoder;
        private const int OPUS_SAMPLE_RATE = 48000;
        private const int OPUS_CHANNELS = 1;
        private const int OPUS_FRAME_SIZE = 960; // 20ms à 48kHz mono

        // Events pour monitoring
        public event Action<string>? LogEvent;
        public event Action<bool>? StreamingStateChanged;
        public event Action<int>? BufferLevelChanged; // Buffer size for monitoring
        public event Action<byte[]>? AudioCaptured; // ✅ NOUVEAU: Event pour données capturées

        public bool IsStreaming => _isStreaming;
        public bool IsPlaying => _isPlaying;
        public bool IsCapturing => _isCapturing; // ✅ NOUVEAU: Property de capture
        public int BufferLevel => _audioBuffer.Count;
        public int CaptureBufferLevel => _captureBuffer.Count; // ✅ NOUVEAU: Niveau buffer capture

        // ✅ NOUVEAU: Device selection support
        private string? _selectedSpeakerDevice;
        private string? _selectedMicrophoneDevice;

        // ✅ VOLUME CONTROL: Support volume microphone et speaker
        private float _microphoneVolume = 1.0f; // 0.0 à 2.0 (0% à 200%)
        private float _speakerVolume = 1.0f; // 0.0 à 2.0 (0% à 200%)

        // 🔍 DIAGNOSTIC COUNTERS: Pour tracking des problèmes audio
        private int _frameCounter = 0;
        private int _receptionCounter = 0;
        private int _qualityCounter = 0;

        // ✅ REAL AUDIO CAPTURE: NAudio components
        private WaveInEvent? _waveIn;
        private double _currentAudioLevel = 0.0;
        private readonly object _levelLock = new object();
        private DateTime _lastCaptureAttempt = DateTime.MinValue;

        /// <summary>
        /// Configurer le device speaker pour le playback audio
        /// </summary>
        public void SetSpeakerDevice(string deviceName)
        {
            _selectedSpeakerDevice = deviceName;
            LogEvent?.Invoke($"[OpusStreaming] 🔊 Speaker device set to: {deviceName}");
            // TODO: Appliquer le device speaker au système audio Windows
        }

        /// <summary>
        /// ✅ NOUVEAU: Configurer le device microphone pour la capture audio
        /// </summary>
        public void SetMicrophoneDevice(string deviceName)
        {
            _selectedMicrophoneDevice = deviceName;
            LogEvent?.Invoke($"[OpusStreaming] 🎤 Microphone device set to: {deviceName}");
            // TODO: Appliquer le device microphone au système audio Windows
        }

        public OpusAudioStreamingService()
        {
            LogEvent?.Invoke("[OpusStreaming] 🎵 Professional audio streaming service initialized");

            // Timer pour playback continu (40ms intervals pour fluidité optimale)
            _playbackTimer = new Timer(ProcessAudioBuffer, null, Timeout.Infinite, Timeout.Infinite);

            // ✅ NOUVEAU: Initialiser les codecs Opus pour audio bidirectionnel
            try
            {
                _opusDecoder = new OpusDecoder(OPUS_SAMPLE_RATE, OPUS_CHANNELS);
                LogEvent?.Invoke("[OpusStreaming] ✅ Opus decoder initialized (48kHz, mono)");

                _opusEncoder = new OpusEncoder(OPUS_SAMPLE_RATE, OPUS_CHANNELS, (Concentus.Enums.OpusApplication)2049); // VOIP application

                // ✅ FIX CRACKLING: Configuration VOIP conservative et stable
                _opusEncoder.Bitrate = 24000; // 24 kbps optimal pour VOIP mono
                _opusEncoder.Complexity = 5; // Complexité modérée pour stabilité
                // Pas de ForceMode, laisser Opus choisir automatiquement

                LogEvent?.Invoke("[OpusStreaming] ✅ Opus encoder initialized (48kHz, mono, VOIP, 24kbps, stable)");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Failed to initialize Opus codecs: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialiser le service audio professionnel
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                LogEvent?.Invoke("[OpusStreaming] 🎵 Initializing professional audio streaming service...");

                // Audio system initialized - no debug beep needed

                LogEvent?.Invoke("[OpusStreaming] ✅ Professional audio streaming service initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error initializing audio streaming: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Démarrer le streaming audio temps réel
        /// </summary>
        public async Task<bool> StartStreamingAsync()
        {
            try
            {
                lock (_lock)
                {
                    if (_isStreaming)
                    {
                        LogEvent?.Invoke("[OpusStreaming] ⚠️ Streaming already active");
                        return true;
                    }
                    _isStreaming = true;
                }

                LogEvent?.Invoke("[OpusStreaming] 🚀 Starting professional audio streaming...");

                _streamingCts = new CancellationTokenSource();

                // Démarrer le timer de playback (20ms pour sync parfaite avec Opus)
                _playbackTimer?.Change(0, 20); // 20ms intervals - sync parfaite avec frames Opus

                StreamingStateChanged?.Invoke(true);
                LogEvent?.Invoke("[OpusStreaming] ✅ Professional audio streaming started");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error starting streaming: {ex.Message}");
                lock (_lock)
                {
                    _isStreaming = false;
                }
                StreamingStateChanged?.Invoke(false);
                return false;
            }
        }

        /// <summary>
        /// 🔧 DEBUG: Méthode de diagnostic audio améliorée avec capture
        /// </summary>
        public void DiagnoseAudioState()
        {
            LogEvent?.Invoke($"[OpusStreaming] 🔍 AUDIO DIAGNOSTIC:");
            LogEvent?.Invoke($"  - IsStreaming: {_isStreaming}");
            LogEvent?.Invoke($"  - IsPlaying: {_isPlaying}");
            LogEvent?.Invoke($"  - IsCapturing: {_isCapturing}"); // ✅ NOUVEAU
            LogEvent?.Invoke($"  - Playback Buffer Level: {_audioBuffer.Count}/{_maxBufferSize}");
            LogEvent?.Invoke($"  - Capture Buffer Level: {_captureBuffer.Count}/{_maxCaptureBufferSize}"); // ✅ NOUVEAU
            LogEvent?.Invoke($"  - Selected Speaker: {_selectedSpeakerDevice ?? "None"}");
            LogEvent?.Invoke($"  - Selected Microphone: {_selectedMicrophoneDevice ?? "None"}"); // ✅ NOUVEAU
            LogEvent?.Invoke($"  - Playback Timer: {(_playbackTimer != null ? "Active" : "Inactive")}");
            LogEvent?.Invoke($"  - Capture Timer: {(_captureTimer != null ? "Active" : "Inactive")}"); // ✅ NOUVEAU
        }

        /// <summary>
        /// Envoyer données audio au buffer de streaming
        /// </summary>
        public void StreamAudioData(byte[] audioData)
        {
            try
            {
                if (!_isStreaming || audioData == null || audioData.Length == 0)
                {
                    LogEvent?.Invoke($"[OpusStreaming] 🚨 RECEPTION BLOCKED: streaming={_isStreaming}, data={(audioData?.Length ?? 0)} bytes");
                    return;
                }

                // 🔍 DIAGNOSTIC: Log toute réception pour tracker les coupures
                _receptionCounter++;

                // Ajouter au buffer circulaire
                _audioBuffer.Enqueue(audioData);

                // Gérer overflow du buffer (drop old frames)
                int droppedFrames = 0;
                while (_audioBuffer.Count > _maxBufferSize)
                {
                    _audioBuffer.TryDequeue(out _);
                    droppedFrames++;
                }

                // Log overflow intelligemment (pas de spam)
                if (droppedFrames > 0)
                {
                    LogEvent?.Invoke($"[OpusStreaming] 🔧 Buffer optimization: dropped {droppedFrames} old frames (maintaining low latency)");
                }

                BufferLevelChanged?.Invoke(_audioBuffer.Count);

                // 🔍 DIAGNOSTIC: Log réception moins verbeux pour éviter lag
                if (_receptionCounter % 500 == 0) // Toutes les 10 secondes
                {
                    LogEvent?.Invoke($"[OpusStreaming] 📨 RECEPTION #{_receptionCounter}: {audioData.Length} bytes (buffer: {_audioBuffer.Count}/{_maxBufferSize})");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error streaming audio data: {ex.Message}");
            }
        }

        /// <summary>
        /// Processeur continu du buffer audio (appelé par timer)
        /// </summary>
        private void ProcessAudioBuffer(object? state)
        {
            try
            {
                if (!_isStreaming || _audioBuffer.IsEmpty)
                {
                    lock (_lock)
                    {
                        _isPlaying = false;
                    }
                    return;
                }

                // 🎯 OPUS-SYNC: Protection buffer alignée sur frames Opus 20ms
                if (_audioBuffer.Count < _minBufferSize)
                {
                    // Attendre minimum 2 frames Opus (40ms) pour stabilité
                    return;
                }

                // 🎯 OPUS-ALIGNED: Traiter 1 frame à la fois (20ms sync parfait)
                if (_audioBuffer.TryDequeue(out var audioFrame))
                {
                    ProcessSingleAudioFrame(audioFrame);
                }


                BufferLevelChanged?.Invoke(_audioBuffer.Count);

                // 🎯 DIAGNOSTIC: Log moins verbeux pour éviter lag
                _frameCounter++;

                if (_frameCounter % 250 == 0) // Toutes les 5 secondes (250 frames * 20ms = 5000ms)
                {
                    LogEvent?.Invoke($"[OpusStreaming] 🔍 DIAGNOSTIC: Frame #{_frameCounter} processed (buffer: {_audioBuffer.Count}/10, timer active: {_playbackTimer != null}, streaming: {_isStreaming})");
                }

                // 🚨 ALERTE CRITIQUE: Détecter quand le buffer devient vide
                if (_audioBuffer.Count == 0)
                {
                    LogEvent?.Invoke($"[OpusStreaming] 🚨 BUFFER EMPTY at frame #{_frameCounter} - potential underrun detected!");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error processing audio buffer: {ex.Message}");
            }
        }

        /// <summary>
        /// Traiter une seule frame audio (nouvelle méthode pour multi-processing)
        /// </summary>
        private void ProcessSingleAudioFrame(byte[] audioData)
        {
            lock (_lock)
            {
                _isPlaying = true;
            }

            // Convertir et jouer audio via système optimisé avec volume
            PlayAudioFrameOptimized(audioData);
        }

        /// <summary>
        /// Jouer frame audio de manière optimisée avec décodage Opus + Volume Control
        /// </summary>
        private void PlayAudioFrameOptimized(byte[] audioData)
        {
            try
            {
                byte[] pcmData;

                // 🎯 DIRECT OPUS DECODING: Pas de détection, on sait que c'est de l'Opus !

                if (_opusDecoder != null)
                {
                    // Décoder Opus → PCM directement
                    short[] decodedSamples = new short[960]; // 20ms à 48kHz mono
                    int samplesDecoded = _opusDecoder.Decode(audioData, 0, audioData.Length, decodedSamples, 0, decodedSamples.Length, false);

                    if (samplesDecoded > 0)
                    {
                        // ✅ VOLUME CONTROL: Appliquer volume speaker avant conversion
                        for (int i = 0; i < samplesDecoded; i++)
                        {
                            decodedSamples[i] = (short)(decodedSamples[i] * _speakerVolume);
                        }

                        // ✅ FIX AUDIO TRUNCATION: Utiliser TOUS les samples décodés sans les tronquer
                        // Convertir short[] → byte[] PCM avec la taille complète
                        pcmData = new byte[samplesDecoded * 2];
                        for (int i = 0; i < samplesDecoded; i++)
                        {
                            byte[] sampleBytes = BitConverter.GetBytes(decodedSamples[i]);
                            pcmData[i * 2] = sampleBytes[0];
                            pcmData[i * 2 + 1] = sampleBytes[1];
                        }
                        // ✅ Opus décodé silencieusement pour performance
                    }
                    else
                    {
                        LogEvent?.Invoke($"[OpusStreaming] ❌ Failed to decode Opus data - decoder error");
                        return; // Skip frame si décodage échoue
                    }
                }
                else
                {
                    LogEvent?.Invoke($"[OpusStreaming] ❌ Opus decoder not initialized");
                    return; // Skip frame si pas de décodeur
                }

                // ✅ OFFICIAL SIPSORCERY PATTERN: NAudio implementation basée sur WindowsAudioEndPoint
                try
                {
                    // 🎯 SIPSorcery standard: 48kHz, 16-bit, mono
                    var waveFormat = new WaveFormat(48000, 16, 1);

                    lock (_lock) // Thread safety selon pattern SIPSorcery
                    {
                        if (_waveOut == null || _bufferedProvider == null)
                        {
                            // 🎯 OPUS-ALIGNED: Configuration buffer synchronisée avec Opus 20ms frames
                            _bufferedProvider = new BufferedWaveProvider(waveFormat);
                            _bufferedProvider.DiscardOnBufferOverflow = true; // Drop old data pour low latency
                            _bufferedProvider.BufferDuration = TimeSpan.FromMilliseconds(200); // 🎯 OPUS: 200ms = 10 frames Opus

                            // 🎯 OPUS-SYNC: Configuration latency alignée sur frames Opus
                            _waveOut = new WaveOutEvent();
                            _waveOut.DesiredLatency = 60; // 🎯 OPUS: 60ms = 3 frames Opus (optimal latency)
                            _waveOut.NumberOfBuffers = 3; // 🎯 OPUS: 3 buffers pour sync Opus
                            _waveOut.Init(_bufferedProvider);
                            _waveOut.Play(); // Start real-time playback

                            LogEvent?.Invoke($"[OpusStreaming] ✅ SIPSorcery pattern audio endpoint initialized (SAFE config)");
                        }

                        // 🔧 VALIDATION: Vérifier taille données PCM AVANT AddSamples
                        if (pcmData.Length == 0 || pcmData.Length % 2 != 0)
                        {
                            LogEvent?.Invoke($"[OpusStreaming] ⚠️ Invalid PCM data size: {pcmData.Length} bytes - skipping frame");
                            return;
                        }

                        // 🔍 QUALITY DIAGNOSTIC: Vérifier l'état du BufferedWaveProvider
                        var bufferBytes = _bufferedProvider.BufferedBytes;
                        var bufferDuration = _bufferedProvider.BufferedDuration;

                        // Pattern SIPSorcery SAFE: Direct AddSamples sans contrôle agressif
                        _bufferedProvider.AddSamples(pcmData, 0, pcmData.Length);

                        // 🔍 QUALITY LOG: Diagnostic complet du playback
                        _qualityCounter++;

                        if (_qualityCounter % 500 == 0) // Toutes les 10 secondes
                        {
                            LogEvent?.Invoke($"[OpusStreaming] 🎵 QUALITY #{_qualityCounter}: Added {pcmData.Length} bytes (samples: {pcmData.Length/2}) | Buffer: {bufferBytes} bytes ({bufferDuration.TotalMilliseconds:F0}ms) | Playing: {_waveOut?.PlaybackState}");
                        }
                    }
                }
                catch (Exception directEx)
                {
                    LogEvent?.Invoke($"[OpusStreaming] ❌ Direct PCM failed: {directEx.GetType().Name}: {directEx.Message}");
                    LogEvent?.Invoke($"[OpusStreaming] 🚫 RADICAL FIX: Disabling SoundPlayer fallback that causes 'tac tac tac' - NAudio streaming ONLY!");

                    // 🔧 RADICAL FIX: Ne PAS utiliser SoundPlayer qui cause les "tac tac tac" !
                    // SoundPlayer crée un nouveau player pour chaque frame → "tac tac tac"
                    // On FORCE l'utilisation de NAudio BufferedWaveProvider UNIQUEMENT
                    LogEvent?.Invoke($"[OpusStreaming] 🔧 Skipping problematic SoundPlayer fallback - audio frame discarded to prevent crackling");
                    return; // Skip complètement le SoundPlayer
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error playing audio frame: {ex.Message}");
            }
        }

        /// <summary>
        /// Détecter si les données audio ressemblent à du PCM 16-bit
        /// </summary>
        private bool IsLikelyPcmData(byte[] audioData)
        {
            if (audioData.Length < 16) return false;

            // Analyser les premiers 16 bytes pour des patterns PCM typiques
            int zeroBytes = 0;
            int ffBytes = 0;
            int lowValueBytes = 0;

            for (int i = 0; i < Math.Min(16, audioData.Length); i++)
            {
                if (audioData[i] == 0x00) zeroBytes++;
                else if (audioData[i] == 0xFF) ffBytes++;
                else if (audioData[i] <= 0x0F) lowValueBytes++;
            }

            // PCM tend à avoir beaucoup de 00, FF ou valeurs basses
            // Opus a des headers plus variés et structurés
            bool hasTypicalPcmPattern = (zeroBytes + ffBytes + lowValueBytes) >= 10;

            return hasTypicalPcmPattern;
        }

        /// <summary>
        /// Convertir données audio raw en format WAV optimisé
        /// </summary>
        private byte[] ConvertToWavFormatOptimized(byte[] rawAudioData)
        {
            try
            {
                // ✅ FIX FREQUENCY MISMATCH: Aligner sur Opus 48kHz pour éviter distorsion
                const int sampleRate = 48000; // Était 44100, maintenant aligné sur OPUS_SAMPLE_RATE
                const short channels = 1; // Mono
                const short bitsPerSample = 16;

                var dataSize = rawAudioData.Length;
                var fileSize = 36 + dataSize;

                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    // Header WAV optimisé
                    writer.Write("RIFF".ToCharArray());
                    writer.Write(fileSize);
                    writer.Write("WAVE".ToCharArray());

                    // Format chunk
                    writer.Write("fmt ".ToCharArray());
                    writer.Write(16); // Chunk size
                    writer.Write((short)1); // Audio format (PCM)
                    writer.Write(channels);
                    writer.Write(sampleRate);
                    writer.Write(sampleRate * channels * bitsPerSample / 8); // Byte rate
                    writer.Write((short)(channels * bitsPerSample / 8)); // Block align
                    writer.Write(bitsPerSample);

                    // Data chunk
                    writer.Write("data".ToCharArray());
                    writer.Write(dataSize);
                    writer.Write(rawAudioData);

                    return stream.ToArray();
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error converting to WAV: {ex.Message}");
                return rawAudioData; // Fallback vers données brutes
            }
        }

        /// <summary>
        /// Arrêter le streaming audio
        /// </summary>
        public async Task StopStreamingAsync()
        {
            try
            {
                LogEvent?.Invoke("[OpusStreaming] 🛑 Stopping audio streaming...");

                lock (_lock)
                {
                    _isStreaming = false;
                    _isPlaying = false;
                }

                // Arrêter timer
                _playbackTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                // Arrêter player en cours
                _currentPlayer?.Stop();
                _currentPlayer?.Dispose();
                _currentPlayer = null;

                // Canceller streaming
                _streamingCts?.Cancel();

                // Vider buffer
                while (_audioBuffer.TryDequeue(out _)) { }

                StreamingStateChanged?.Invoke(false);
                BufferLevelChanged?.Invoke(0);
                LogEvent?.Invoke("[OpusStreaming] ✅ Audio streaming stopped");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error stopping streaming: {ex.Message}");
            }
        }

        /// <summary>
        /// Obtenir statistiques du streaming
        /// </summary>
        public (bool IsStreaming, bool IsPlaying, int BufferLevel, string Status) GetStreamingStats()
        {
            var status = _isStreaming ? (_isPlaying ? "Playing" : "Buffering") : "Stopped";
            return (_isStreaming, _isPlaying, _audioBuffer.Count, status);
        }

        /// <summary>
        /// Test le streaming avec un ton de test
        /// </summary>
        public async Task<bool> PlayTestToneAsync(double frequency = 440.0, int durationMs = 1000)
        {
            try
            {
                LogEvent?.Invoke($"[OpusStreaming] 🎵 Playing test tone {frequency}Hz for {durationMs}ms via streaming");

                // Démarrer le streaming si pas actif
                if (!_isStreaming)
                {
                    await StartStreamingAsync();
                }

                // Générer ton de test par chunks
                var chunkDurationMs = 100; // 100ms chunks
                var chunksNeeded = durationMs / chunkDurationMs;

                for (int i = 0; i < chunksNeeded; i++)
                {
                    var testChunk = GenerateTestToneChunk(frequency, chunkDurationMs);
                    StreamAudioData(testChunk);
                    await Task.Delay(chunkDurationMs);
                }

                LogEvent?.Invoke($"[OpusStreaming] ✅ Test tone completed via professional streaming");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error playing test tone: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Générer un chunk de ton de test
        /// </summary>
        private byte[] GenerateTestToneChunk(double frequency, int durationMs)
        {
            const int sampleRate = 44100;
            var samples = (int)(sampleRate * durationMs / 1000.0);
            var audioData = new byte[samples * 2]; // 16-bit = 2 bytes per sample

            for (int i = 0; i < samples; i++)
            {
                var sample = (short)(Math.Sin(2.0 * Math.PI * frequency * i / sampleRate) * 16000);

                // Little-endian encoding
                audioData[i * 2] = (byte)(sample & 0xFF);
                audioData[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            return audioData;
        }

        #region ✅ NOUVEAU: Méthodes de capture audio

        /// <summary>
        /// ✅ REAL MICROPHONE CAPTURE: Démarrer la capture audio depuis le microphone
        /// </summary>
        public async Task<bool> StartCaptureAsync()
        {
            try
            {
                // ✅ PROTECTION: Éviter boucle infinie de tentatives
                var now = DateTime.Now;
                if (now - _lastCaptureAttempt < TimeSpan.FromSeconds(2))
                {
                    LogEvent?.Invoke("[OpusStreaming] ⚠️ Too many capture attempts, throttling...");
                    return false;
                }
                _lastCaptureAttempt = now;

                lock (_lock)
                {
                    if (_isCapturing)
                    {
                        LogEvent?.Invoke("[OpusStreaming] ⚠️ Audio capture already active");
                        return true;
                    }
                    _isCapturing = true;
                }

                LogEvent?.Invoke("[OpusStreaming] 🎤 Starting REAL audio capture with NAudio...");

                // ✅ REAL CAPTURE: Vérifier devices disponibles d'abord
                int deviceCount = WaveInEvent.DeviceCount;
                LogEvent?.Invoke($"[OpusStreaming] 🎤 Found {deviceCount} audio input devices");

                if (deviceCount == 0)
                {
                    LogEvent?.Invoke("[OpusStreaming] ⚠️ No audio input devices found - audio receive-only mode");
                    lock (_lock)
                    {
                        _isCapturing = true; // Permet receive-only (pas de capture mais traitement actif)
                    }
                    LogEvent?.Invoke("[OpusStreaming] ✅ Audio capture started (receive-only mode - no microphone)");
                    return true; // Succès pour permettre VOIP même sans micro
                }

                // ✅ REAL CAPTURE: Initialiser NAudio WaveInEvent avec device par défaut
                _waveIn = new WaveInEvent();
                _waveIn.DeviceNumber = 0; // Device par défaut (évite BadDeviceId)
                _waveIn.WaveFormat = new WaveFormat(48000, 1); // ✅ FIX MISMATCH: 48kHz pour correspondre au playback
                _waveIn.BufferMilliseconds = 20; // 20ms buffers (like our simulation)

                // Event handler pour données audio réelles
                _waveIn.DataAvailable += OnWaveInDataAvailable;

                // Démarrer la capture réelle
                _waveIn.StartRecording();

                LogEvent?.Invoke("[OpusStreaming] ✅ REAL audio capture started with NAudio");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error starting REAL capture: {ex.Message}");
                lock (_lock)
                {
                    _isCapturing = false;
                }
                return false;
            }
        }

        /// <summary>
        /// ✅ REAL CAPTURE: Event handler pour données audio réelles du microphone
        /// </summary>
        private void OnWaveInDataAvailable(object? sender, WaveInEventArgs e)
        {
            try
            {
                if (!_isCapturing || e.BytesRecorded == 0)
                    return;

                // Créer copie des données audio
                var audioData = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, audioData, 0, e.BytesRecorded);

                // Calculer niveau audio réel pour spectrum analyzer
                CalculateRealAudioLevel(audioData);

                // Ajouter au buffer de capture
                _captureBuffer.Enqueue(audioData);

                // Gérer overflow du buffer
                while (_captureBuffer.Count > _maxCaptureBufferSize)
                {
                    _captureBuffer.TryDequeue(out _);
                }

                // ✅ VOLUME CONTROL + OPUS ENCODING: Appliquer volume micro AVANT encodage
                var volumeAdjustedData = ApplyMicrophoneVolume(audioData);
                var opusData = EncodeToOpus(volumeAdjustedData);
                if (opusData != null)
                {
                    // Déclencher l'event pour notifier les abonnés (VOIP) avec données Opus
                    AudioCaptured?.Invoke(opusData);
                    LogEvent?.Invoke($"[OpusStreaming] ✅ REAL MIC+VOL: PCM (vol:{_microphoneVolume:F1}x) encoded to Opus: {audioData.Length} bytes → {opusData.Length} bytes");
                }
                else
                {
                    // Fallback: Envoyer PCM brut avec volume si l'encodage échoue
                    AudioCaptured?.Invoke(volumeAdjustedData);
                    LogEvent?.Invoke($"[OpusStreaming] ⚠️ REAL MIC+VOL: Opus encoding failed, sending raw PCM with volume: {volumeAdjustedData.Length} bytes");
                }

                // Log périodique (toutes les 100 captures = ~2 secondes)
                if (_captureBuffer.Count % 100 == 0)
                {
                    LogEvent?.Invoke($"[OpusStreaming] 🎤 REAL capture: {audioData.Length} bytes (level: {_currentAudioLevel:F3})");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error processing real audio data: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ REAL AUDIO LEVEL: Calculer le niveau audio réel depuis les données PCM
        /// </summary>
        private void CalculateRealAudioLevel(byte[] audioData)
        {
            try
            {
                if (audioData.Length < 2) return;

                double sum = 0;
                int sampleCount = audioData.Length / 2; // 16-bit = 2 bytes per sample

                for (int i = 0; i < audioData.Length; i += 2)
                {
                    // Convertir bytes en sample 16-bit (little-endian)
                    short sample = (short)(audioData[i] | (audioData[i + 1] << 8));
                    sum += Math.Abs(sample);
                }

                // Normaliser le niveau (0.0 à 1.0)
                double avgLevel = sum / sampleCount / 32768.0; // 32768 = max value for 16-bit

                lock (_levelLock)
                {
                    _currentAudioLevel = avgLevel;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error calculating audio level: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ REAL CAPTURE: Arrêter la capture audio
        /// </summary>
        public async Task StopCaptureAsync()
        {
            try
            {
                LogEvent?.Invoke("[OpusStreaming] 🛑 Stopping REAL audio capture...");

                lock (_lock)
                {
                    _isCapturing = false;
                }

                // Arrêter NAudio capture
                if (_waveIn != null)
                {
                    _waveIn.StopRecording();
                    _waveIn.DataAvailable -= OnWaveInDataAvailable;
                    _waveIn.Dispose();
                    _waveIn = null;
                }

                // Reset audio level
                lock (_levelLock)
                {
                    _currentAudioLevel = 0.0;
                }

                // Vider le buffer de capture
                while (_captureBuffer.TryDequeue(out _)) { }

                LogEvent?.Invoke("[OpusStreaming] ✅ REAL audio capture stopped");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error stopping REAL capture: {ex.Message}");
            }
        }



        /// <summary>
        /// ✅ REAL AUDIO LEVEL: Obtenir le niveau audio actuel de capture (pour spectromètre)
        /// </summary>
        public double GetCurrentCaptureLevel()
        {
            try
            {
                if (!_isCapturing)
                    return 0.0;

                // ✅ RETOURNER LE VRAI NIVEAU AUDIO CALCULÉ DEPUIS LE MICROPHONE
                lock (_levelLock)
                {
                    return _currentAudioLevel;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error getting REAL capture level: {ex.Message}");
                return 0.0;
            }
        }

        /// <summary>
        /// ✅ OPUS ENCODING: Encoder données PCM 16-bit en Opus
        /// </summary>
        private byte[]? EncodeToOpus(byte[] pcmData)
        {
            try
            {
                if (_opusEncoder == null)
                {
                    LogEvent?.Invoke($"[OpusStreaming] ❌ Opus encoder not initialized");
                    return null;
                }

                if (pcmData.Length == 0 || pcmData.Length % 2 != 0)
                {
                    LogEvent?.Invoke($"[OpusStreaming] ❌ Invalid PCM data length: {pcmData.Length} bytes");
                    return null;
                }

                // Convertir byte[] PCM en short[] samples
                int sampleCount = pcmData.Length / 2;
                short[] samples = new short[sampleCount];

                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = BitConverter.ToInt16(pcmData, i * 2);
                }

                // Opus nécessite des frames de taille fixe (960 samples pour 20ms à 48kHz)
                // Si nous n'avons pas assez de données, pad avec zeros
                if (sampleCount < OPUS_FRAME_SIZE)
                {
                    var paddedSamples = new short[OPUS_FRAME_SIZE];
                    Array.Copy(samples, paddedSamples, sampleCount);
                    samples = paddedSamples;
                    sampleCount = OPUS_FRAME_SIZE;
                }
                else if (sampleCount > OPUS_FRAME_SIZE)
                {
                    // Prendre seulement les premiers OPUS_FRAME_SIZE samples
                    var truncatedSamples = new short[OPUS_FRAME_SIZE];
                    Array.Copy(samples, truncatedSamples, OPUS_FRAME_SIZE);
                    samples = truncatedSamples;
                    sampleCount = OPUS_FRAME_SIZE;
                }

                // Encoder avec Opus (output buffer max ~4000 bytes)
                byte[] opusData = new byte[4000];
                int encodedLength = _opusEncoder.Encode(samples, 0, sampleCount, opusData, 0, opusData.Length);

                if (encodedLength > 0)
                {
                    // Retourner seulement les bytes encodés
                    byte[] result = new byte[encodedLength];
                    Array.Copy(opusData, result, encodedLength);
                    return result;
                }
                else
                {
                    LogEvent?.Invoke($"[OpusStreaming] ❌ Opus encoding failed: {encodedLength}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error encoding to Opus: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ✅ VOLUME CONTROL: Appliquer volume au signal microphone
        /// </summary>
        private byte[] ApplyMicrophoneVolume(byte[] audioData)
        {
            try
            {
                if (_microphoneVolume == 1.0f || audioData.Length < 2)
                    return audioData;

                var adjustedData = new byte[audioData.Length];
                for (int i = 0; i < audioData.Length; i += 2)
                {
                    // Convertir bytes en sample 16-bit
                    short sample = (short)(audioData[i] | (audioData[i + 1] << 8));

                    // Appliquer volume avec clipping protection
                    float adjustedSample = sample * _microphoneVolume;
                    adjustedSample = Math.Max(-32768, Math.Min(32767, adjustedSample));

                    // Reconvertir en bytes
                    short finalSample = (short)adjustedSample;
                    adjustedData[i] = (byte)(finalSample & 0xFF);
                    adjustedData[i + 1] = (byte)((finalSample >> 8) & 0xFF);
                }

                return adjustedData;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error applying microphone volume: {ex.Message}");
                return audioData; // Fallback vers données originales
            }
        }

        /// <summary>
        /// ✅ VOLUME CONTROL: Configurer le volume du microphone (0.0 à 2.0)
        /// </summary>
        public void SetMicrophoneVolume(float volume)
        {
            _microphoneVolume = Math.Max(0.0f, Math.Min(2.0f, volume));
            LogEvent?.Invoke($"[OpusStreaming] 🎤 Microphone volume set to: {_microphoneVolume:F1}x ({_microphoneVolume * 100:F0}%)");
        }

        /// <summary>
        /// ✅ VOLUME CONTROL: Configurer le volume du speaker (0.0 à 2.0)
        /// </summary>
        public void SetSpeakerVolume(float volume)
        {
            _speakerVolume = Math.Max(0.0f, Math.Min(2.0f, volume));
            LogEvent?.Invoke($"[OpusStreaming] 🔊 Speaker volume set to: {_speakerVolume:F1}x ({_speakerVolume * 100:F0}%)");
        }

        /// <summary>
        /// ✅ VOLUME CONTROL: Obtenir les volumes actuels
        /// </summary>
        public (float MicVolume, float SpeakerVolume) GetVolumeLevels()
        {
            return (_microphoneVolume, _speakerVolume);
        }

        #endregion

        public void Dispose()
        {
            try
            {
                StopStreamingAsync().Wait(1000);
                StopCaptureAsync().Wait(1000); // ✅ REAL CAPTURE: Arrêter capture NAudio
                _playbackTimer?.Dispose();
                _captureTimer?.Dispose(); // ✅ NOUVEAU: Dispose capture timer
                _streamingCts?.Dispose();
                _currentPlayer?.Dispose();
                _waveIn?.Dispose(); // ✅ REAL CAPTURE: Dispose NAudio
                _opusDecoder?.Dispose(); // ✅ NOUVEAU: Dispose Opus decoder
                _opusEncoder?.Dispose(); // ✅ NOUVEAU: Dispose Opus encoder

                // 🔧 TEST: Dispose BufferedWaveProvider and WaveOutEvent
                lock (_lock)
                {
                    _waveOut?.Stop();
                    _waveOut?.Dispose();
                    _waveOut = null;
                    _bufferedProvider = null; // BufferedWaveProvider is disposed when WaveOut is disposed
                }
                LogEvent?.Invoke("[OpusStreaming] Service disposed (playback + REAL capture)");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error during dispose: {ex.Message}");
            }
        }
    }
}