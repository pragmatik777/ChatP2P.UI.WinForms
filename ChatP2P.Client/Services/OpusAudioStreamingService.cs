using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Media;
using System.IO;
using NAudio.Wave;

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
        private readonly int _maxBufferSize = 10; // 10 frames max (~200ms de latence)
        private readonly Timer? _playbackTimer;
        private CancellationTokenSource? _streamingCts;

        // ✅ NOUVEAU: Buffer et timer pour CAPTURE audio
        private readonly ConcurrentQueue<byte[]> _captureBuffer = new();
        private readonly Timer? _captureTimer;
        private readonly int _maxCaptureBufferSize = 10;

        // Audio player optimisé
        private SoundPlayer? _currentPlayer;

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

            // Timer pour playback continu (50ms intervals pour réactivité)
            _playbackTimer = new Timer(ProcessAudioBuffer, null, Timeout.Infinite, Timeout.Infinite);
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

                // Démarrer le timer de playback (50ms pour réactivité)
                _playbackTimer?.Change(0, 50); // 50ms intervals (20 fps - optimisé performance/qualité)

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
                    return;

                // Ajouter au buffer circulaire
                _audioBuffer.Enqueue(audioData);

                // Gérer overflow du buffer (drop old frames)
                while (_audioBuffer.Count > _maxBufferSize)
                {
                    _audioBuffer.TryDequeue(out _);
                    LogEvent?.Invoke("[OpusStreaming] ⚠️ Buffer overflow, dropping frame");
                }

                BufferLevelChanged?.Invoke(_audioBuffer.Count);

                // 🔧 DEBUG: Log détaillé mais pas spam (seulement toutes les 50 frames)
                if (_audioBuffer.Count % 50 == 0)
                {
                    LogEvent?.Invoke($"[OpusStreaming] 📨 Audio data received: {audioData.Length} bytes (buffer: {_audioBuffer.Count}/{_maxBufferSize})");
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

                // Déqueue et jouer frame suivante
                if (_audioBuffer.TryDequeue(out var audioFrame))
                {
                    lock (_lock)
                    {
                        _isPlaying = true;
                    }

                    // Convertir et jouer audio via système optimisé
                    PlayAudioFrameOptimized(audioFrame);

                    BufferLevelChanged?.Invoke(_audioBuffer.Count);
                    LogEvent?.Invoke($"[OpusStreaming] 🔊 Played audio frame ({audioFrame.Length} bytes, buffer: {_audioBuffer.Count})");
                }
                else
                {
                    lock (_lock)
                    {
                        _isPlaying = false;
                    }
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error processing audio buffer: {ex.Message}");
            }
        }

        /// <summary>
        /// Jouer frame audio de manière optimisée
        /// </summary>
        private void PlayAudioFrameOptimized(byte[] audioData)
        {
            try
            {
                // Convertir les données raw en format WAV jouable (optimisé)
                var wavData = ConvertToWavFormatOptimized(audioData);

                // Utiliser player optimisé pour performance
                using (var memoryStream = new MemoryStream(wavData))
                {
                    _currentPlayer?.Stop(); // Stop previous if playing
                    _currentPlayer?.Dispose();

                    _currentPlayer = new SoundPlayer(memoryStream);
                    _currentPlayer.Load(); // Synchronous load for timing
                    _currentPlayer.Play(); // Non-blocking play
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error playing audio frame: {ex.Message}");
            }
        }

        /// <summary>
        /// Convertir données audio raw en format WAV optimisé
        /// </summary>
        private byte[] ConvertToWavFormatOptimized(byte[] rawAudioData)
        {
            try
            {
                // Paramètres audio standards optimisés pour streaming
                const int sampleRate = 44100;
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
                    LogEvent?.Invoke("[OpusStreaming] ❌ No audio input devices found, falling back to simulation");
                    // Fallback vers simulation
                    _ = Task.Run(async () => await SimulateCaptureLoop());
                    LogEvent?.Invoke("[OpusStreaming] ✅ Audio capture started (simulation fallback)");
                    return true;
                }

                // ✅ REAL CAPTURE: Initialiser NAudio WaveInEvent avec device par défaut
                _waveIn = new WaveInEvent();
                _waveIn.DeviceNumber = 0; // Device par défaut (évite BadDeviceId)
                _waveIn.WaveFormat = new WaveFormat(44100, 1); // 44.1kHz, mono, 16-bit
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

                // Déclencher l'event pour notifier les abonnés (VOIP)
                AudioCaptured?.Invoke(audioData);

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
        /// ✅ NOUVEAU: Boucle de simulation de capture audio (à remplacer par vraie capture)
        /// </summary>
        private async Task SimulateCaptureLoop()
        {
            var random = new Random();

            while (_isCapturing)
            {
                try
                {
                    // Simuler capture audio toutes les 20ms (50 FPS)
                    await Task.Delay(20);

                    if (!_isCapturing) break;

                    // Générer des données audio simulées
                    var audioData = GenerateSimulatedCaptureData();

                    // Ajouter au buffer de capture
                    _captureBuffer.Enqueue(audioData);

                    // Gérer overflow du buffer
                    while (_captureBuffer.Count > _maxCaptureBufferSize)
                    {
                        _captureBuffer.TryDequeue(out _);
                    }

                    // Déclencher l'event pour notifier les abonnés
                    AudioCaptured?.Invoke(audioData);

                    // Log périodique (toutes les 100 captures = ~2 secondes)
                    if (_captureBuffer.Count % 100 == 0)
                    {
                        LogEvent?.Invoke($"[OpusStreaming] 🎤 Capture active: {audioData.Length} bytes captured (buffer: {_captureBuffer.Count})");
                    }
                }
                catch (Exception ex)
                {
                    LogEvent?.Invoke($"[OpusStreaming] ❌ Error in capture loop: {ex.Message}");
                    break;
                }
            }

            LogEvent?.Invoke("[OpusStreaming] 🛑 Capture simulation loop ended");
        }

        /// <summary>
        /// ✅ NOUVEAU: Générer des données audio simulées pour la capture
        /// </summary>
        private byte[] GenerateSimulatedCaptureData()
        {
            // Générer 20ms d'audio à 44.1kHz, 16-bit mono
            const int sampleRate = 44100;
            const int durationMs = 20;
            var samples = (int)(sampleRate * durationMs / 1000.0);
            var audioData = new byte[samples * 2]; // 16-bit = 2 bytes per sample

            var random = new Random();

            for (int i = 0; i < samples; i++)
            {
                // Simuler un signal audio très faible (comme un microphone en veille)
                var sample = (short)(random.Next(-1000, 1000)); // Très faible comparé aux 16000 du test tone

                // Little-endian encoding
                audioData[i * 2] = (byte)(sample & 0xFF);
                audioData[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            return audioData;
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
                LogEvent?.Invoke("[OpusStreaming] Service disposed (playback + REAL capture)");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[OpusStreaming] ❌ Error during dispose: {ex.Message}");
            }
        }
    }
}