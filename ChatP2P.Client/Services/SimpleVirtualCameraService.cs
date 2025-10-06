using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// 📹 Service de caméra virtuelle avec encodage H.264/VP8 intégré
    /// Génère des patterns animés + encode avec VideoEncodingService
    /// </summary>
    public class SimpleVirtualCameraService : IDisposable
    {
        private bool _isPlaying = false;
        private string? _currentVideoFile;
        private readonly object _lock = new object();
        private Task? _playbackTask;
        private CancellationTokenSource? _cancellationTokenSource;
        private VideoEncodingService? _videoEncoder;
        private EmguVideoDecoderService? _videoDecoder;

        // Cache pour optimiser les performances
        private byte[]? _lastFrameData;
        private TimeSpan _lastFrameTime = TimeSpan.MinValue;

        // Configuration
        private const int TARGET_WIDTH = 640;
        private const int TARGET_HEIGHT = 480;
        private int _currentFPS = 30; // Dynamic FPS based on loaded video file

        // ⚡ FRAME PRELOADING: Cache frames in RAM for ultra-fast access
        private readonly Dictionary<int, byte[]> _frameBuffer = new();
        private const int BUFFER_SIZE = 60; // Preload 2 seconds of frames
        private int _bufferStartIndex = 0;

        // Events
        public event Action<VideoFrame>? VideoFrameReady; // Frame RGB brute
        public event Action<byte[]>? EncodedVideoReady;   // Frame H.264/VP8 encodée
        public event Action<string>? LogEvent;
        public event Action<bool>? PlaybackStateChanged;

        // Properties
        public bool IsPlaying => _isPlaying;
        public string? CurrentVideoFile => _currentVideoFile;
        public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(5); // 5 minutes par défaut
        public TimeSpan CurrentPosition { get; private set; } = TimeSpan.Zero;
        public VideoCodecsEnum SelectedCodec => _videoEncoder?.SelectedCodec ?? VideoCodecsEnum.H264;
        public bool IsEncodingEnabled { get; set; } = false; // DISABLE encoding for performance test
        public int CurrentFPS => _currentFPS;

        public SimpleVirtualCameraService()
        {
            LogEvent?.Invoke("[SimpleVirtualCamera] 📹 Virtual camera with real video + H.264/VP8 encoding initialized");

            // Initialiser l'encodeur vidéo
            _videoEncoder = new VideoEncodingService();
            _videoEncoder.LogEvent += (msg) => LogEvent?.Invoke($"[VirtCam-Encoder] {msg}");
            _videoEncoder.EncodedVideoReady += (data) => EncodedVideoReady?.Invoke(data);

            // Initialiser le décodeur FFmpeg pour les vraies vidéos
            _videoDecoder = new EmguVideoDecoderService();
            _videoDecoder.LogEvent += (msg) => LogEvent?.Invoke($"[VirtCam-Decoder] {msg}");
        }

        /// <summary>
        /// Initialiser l'encodeur avec codec spécifique
        /// </summary>
        public async Task<bool> InitializeEncoderAsync(VideoCodecsEnum codec = VideoCodecsEnum.H264)
        {
            if (_videoEncoder == null) return false;

            var success = await _videoEncoder.InitializeAsync(codec);
            if (success)
            {
                LogEvent?.Invoke($"[SimpleVirtualCamera] ✅ Encoder initialized with {codec}");
            }
            return success;
        }

        /// <summary>
        /// Changer le codec d'encodage
        /// </summary>
        public async Task<bool> ChangeCodecAsync(VideoCodecsEnum newCodec)
        {
            if (_videoEncoder == null) return false;
            return await _videoEncoder.ChangeCodecAsync(newCodec);
        }

        /// <summary>
        /// Charger un fichier vidéo pour simulation
        /// </summary>
        public async Task<bool> LoadVideoFileAsync(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ Video file not found: {filePath}");
                    return false;
                }

                await StopPlaybackAsync();

                // Charger le fichier avec FFmpeg decoder
                if (_videoDecoder != null)
                {
                    var loadSuccess = await _videoDecoder.InitializeAsync(filePath);
                    if (!loadSuccess)
                    {
                        LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ Failed to load video file with FFmpeg");
                        return false;
                    }

                    // Utiliser les vraies informations du fichier
                    // ⚡ PROTECTION: Validate Duration pour éviter TimeSpan overflow
                    var rawDuration = _videoDecoder.Duration;
                    if (rawDuration.TotalSeconds > 0 && rawDuration.TotalSeconds < TimeSpan.MaxValue.TotalSeconds && rawDuration.TotalHours < 24)
                    {
                        Duration = rawDuration;
                    }
                    else
                    {
                        Duration = TimeSpan.FromMinutes(5); // Fallback sécurisé
                        LogEvent?.Invoke($"[SimpleVirtualCamera] ⚠️ Invalid video duration detected ({rawDuration}), using 5min fallback");
                    }
                    // ⚡ ADAPTIVE FPS: Set current FPS from video file
                    var videoFPS = (int)Math.Round(_videoDecoder.FrameRate);
                    if (videoFPS > 0 && videoFPS <= 120) // Reasonable FPS range
                    {
                        _currentFPS = videoFPS;
                        LogEvent?.Invoke($"[SimpleVirtualCamera] 🎯 ADAPTIVE FPS: Set to {_currentFPS} FPS from video file");
                    }
                    else
                    {
                        _currentFPS = 30; // Safe fallback
                        LogEvent?.Invoke($"[SimpleVirtualCamera] ⚠️ Invalid FPS detected ({_videoDecoder.FrameRate:F2}), using 30 FPS fallback");
                    }

                    LogEvent?.Invoke($"[SimpleVirtualCamera] 📊 Video loaded: {_videoDecoder.TotalFrames} frames, {Duration:mm\\:ss\\.fff}, {_videoDecoder.FrameRate:F2} FPS → {_currentFPS} FPS");
                }

                lock (_lock)
                {
                    _currentVideoFile = filePath;
                }

                var fileInfo = new FileInfo(filePath);
                LogEvent?.Invoke($"[SimpleVirtualCamera] ✅ Video loaded: {Path.GetFileName(filePath)}");
                LogEvent?.Invoke($"[SimpleVirtualCamera] 📊 Duration: {Duration:mm\\:ss}, Size: {fileInfo.Length / 1024 / 1024}MB");

                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ Error loading video file: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Démarrer la lecture simulée
        /// </summary>
        public async Task<bool> StartPlaybackAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_currentVideoFile))
                {
                    LogEvent?.Invoke("[SimpleVirtualCamera] ❌ No video file loaded - virtual camera disabled");
                    return false; // Pas de contenu procédural, désactiver la caméra
                }

                lock (_lock)
                {
                    if (_isPlaying)
                    {
                        LogEvent?.Invoke("[SimpleVirtualCamera] ⚠️ Already playing");
                        return true;
                    }
                    _isPlaying = true;
                }

                // Initialiser l'encodeur si pas encore fait et si l'encodage est activé
                if (IsEncodingEnabled && _videoEncoder != null && !_videoEncoder.IsInitialized)
                {
                    await InitializeEncoderAsync();
                }

                _cancellationTokenSource = new CancellationTokenSource();
                _playbackTask = Task.Run(() => SimulatedPlaybackLoopAsync(_cancellationTokenSource.Token));

                PlaybackStateChanged?.Invoke(true);
                LogEvent?.Invoke($"[SimpleVirtualCamera] ▶️ Simulated playback started: {Path.GetFileName(_currentVideoFile)}");
                if (IsEncodingEnabled)
                {
                    LogEvent?.Invoke($"[SimpleVirtualCamera] 🎯 Encoding enabled with {SelectedCodec}");
                }

                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ Error starting playback: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Arrêter la lecture
        /// </summary>
        public async Task StopPlaybackAsync()
        {
            try
            {
                lock (_lock)
                {
                    if (!_isPlaying)
                        return;
                    _isPlaying = false;
                }

                _cancellationTokenSource?.Cancel();

                if (_playbackTask != null)
                {
                    await _playbackTask;
                    _playbackTask = null;
                }

                CurrentPosition = TimeSpan.Zero;
                PlaybackStateChanged?.Invoke(false);
                LogEvent?.Invoke("[SimpleVirtualCamera] ⏹️ Simulated playback stopped");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ Error stopping playback: {ex.Message}");
            }
        }

        /// <summary>
        /// ⚡ SIMPLE VIDEO LOOP: Play video at native FPS with sequential frame reading
        /// </summary>
        private async Task SimulatedPlaybackLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                // ⚡ ADAPTIVE: Use dynamic FPS from loaded video file
                var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _currentFPS);
                var frameIndex = 0;
                var maxFrames = _videoDecoder?.TotalFrames ?? (_currentFPS * 60); // Fallback to 60s at current FPS

                LogEvent?.Invoke($"[SimpleVirtualCamera] 🎬 ADAPTIVE LOOP: Playing {maxFrames} frames at {_currentFPS} FPS ({frameInterval.TotalMilliseconds:F1}ms/frame)");

                while (!cancellationToken.IsCancellationRequested && _isPlaying)
                {
                    try
                    {
                        // ⚡ SIMPLE: Sequential frame reading, no complex time calculations
                        if (frameIndex >= maxFrames)
                        {
                            frameIndex = 0; // Loop video
                        }

                        // Calculate position for display only
                        CurrentPosition = TimeSpan.FromSeconds(frameIndex / (double)_currentFPS);

                        // Read frame directly by index (silent performance mode)
                        var videoFrame = await GetVideoFrameByIndex(frameIndex);

                        if (videoFrame?.Data != null && videoFrame.Data.Length > 0)
                        {
                            // Emit RGB frame (silent)
                            VideoFrameReady?.Invoke(videoFrame);

                            // Encode if enabled (DISABLED FOR TESTING)
                            if (IsEncodingEnabled && _videoEncoder?.IsInitialized == true)
                            {
                                try
                                {
                                    await _videoEncoder.EncodeFrameAsync(videoFrame);
                                }
                                catch
                                {
                                    // Silent catch pour performance
                                }
                            }
                        }
                        else
                        {
                            LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ No frame at index {frameIndex}, stopping");
                            break;
                        }

                        frameIndex++;

                        // ⚡ SIMPLE TIMING: Fixed delay, no compensation
                        await Task.Delay(frameInterval, cancellationToken);

                        // Log every 5 seconds
                        if (frameIndex % (_currentFPS * 5) == 0)
                        {
                            LogEvent?.Invoke($"[SimpleVirtualCamera] 📹 Frame {frameIndex}/{maxFrames} at {CurrentPosition:mm\\:ss} ({_currentFPS} FPS)");
                        }
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        LogEvent?.Invoke($"[SimpleVirtualCamera] ⚠️ Error processing frame {frameIndex}: {ex.Message}");
                        frameIndex++;
                        await Task.Delay(frameInterval, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LogEvent?.Invoke("[SimpleVirtualCamera] 🛑 Simple playback cancelled");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ Simple playback error: {ex.Message}");
            }
        }

        /// <summary>
        /// ⚡ BUFFERED: Get frame from pre-loaded buffer or load batch if needed
        /// </summary>
        private async Task<VideoFrame?> GetVideoFrameByIndex(int frameIndex)
        {
            if (_videoDecoder == null || !_videoDecoder.IsInitialized ||
                string.IsNullOrEmpty(_currentVideoFile) || _currentVideoFile == "PROCEDURAL_CONTENT")
            {
                return null;
            }

            try
            {
                // ⚡ BUFFER HIT: Return cached frame instantly
                if (_frameBuffer.ContainsKey(frameIndex))
                {
                    var cachedData = _frameBuffer[frameIndex];
                    LogEvent?.Invoke($"[SimpleVirtualCamera] ⚡ BUFFER HIT: Frame {frameIndex} from cache (<1ms)");

                    return new VideoFrame
                    {
                        Width = TARGET_WIDTH,
                        Height = TARGET_HEIGHT,
                        Data = cachedData,
                        PixelFormat = SIPSorceryMedia.Abstractions.VideoPixelFormatsEnum.Rgb,
                        Timestamp = DateTime.UtcNow.Ticks
                    };
                }

                // ⚡ BUFFER MISS: Load batch of frames around current index
                LogEvent?.Invoke($"[SimpleVirtualCamera] 📦 BUFFER MISS: Preloading batch around frame {frameIndex}");
                await PreloadFrameBatch(frameIndex);

                // Try again from buffer
                if (_frameBuffer.ContainsKey(frameIndex))
                {
                    var loadedData = _frameBuffer[frameIndex];
                    LogEvent?.Invoke($"[SimpleVirtualCamera] ✅ BATCH LOADED: Frame {frameIndex} now available");

                    return new VideoFrame
                    {
                        Width = TARGET_WIDTH,
                        Height = TARGET_HEIGHT,
                        Data = loadedData,
                        PixelFormat = SIPSorceryMedia.Abstractions.VideoPixelFormatsEnum.Rgb,
                        Timestamp = DateTime.UtcNow.Ticks
                    };
                }

                LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ Failed to load frame {frameIndex} even after batch preload");
                return null;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ Error getting buffered frame {frameIndex}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ⚡ BATCH PRELOAD: Load BUFFER_SIZE frames around target index
        /// </summary>
        private async Task PreloadFrameBatch(int targetIndex)
        {
            try
            {
                var maxFrames = _videoDecoder?.TotalFrames ?? 1800;
                var startIndex = Math.Max(0, targetIndex - 10); // 10 frames before
                var endIndex = Math.Min(maxFrames - 1, startIndex + BUFFER_SIZE - 1); // 60 frames total

                LogEvent?.Invoke($"[SimpleVirtualCamera] 📦 PRELOADING: Frames {startIndex} to {endIndex} ({endIndex - startIndex + 1} frames)");

                // Clear old buffer if we're moving to a new region
                if (Math.Abs(startIndex - _bufferStartIndex) > BUFFER_SIZE / 2)
                {
                    _frameBuffer.Clear();
                    LogEvent?.Invoke($"[SimpleVirtualCamera] 🗑️ Cleared old buffer (moving from {_bufferStartIndex} to {startIndex})");
                }

                _bufferStartIndex = startIndex;

                // Load frames in parallel (but limit to avoid overwhelming FFmpeg)
                var semaphore = new SemaphoreSlim(4); // Max 4 concurrent FFmpeg calls
                var tasks = new List<Task>();

                for (int i = startIndex; i <= endIndex; i++)
                {
                    if (_frameBuffer.ContainsKey(i)) continue; // Skip if already cached

                    var frameIndex = i;
                    tasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            var rgbData = await _videoDecoder.ReadFrameAsync(frameIndex);
                            if (rgbData != null && rgbData.Length > 0)
                            {
                                _frameBuffer[frameIndex] = rgbData;
                                LogEvent?.Invoke($"[SimpleVirtualCamera] ✅ Preloaded frame {frameIndex}");
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }

                await Task.WhenAll(tasks);
                LogEvent?.Invoke($"[SimpleVirtualCamera] 🎯 BATCH COMPLETE: {_frameBuffer.Count} frames in buffer");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ Batch preload error: {ex.Message}");
            }
        }

        /// <summary>
        /// Obtenir frame vidéo - vraie vidéo si chargée, sinon contenu procédural (avec cache optimisé)
        /// </summary>
        private async Task<VideoFrame> GetVideoFrame(int frameNumber, TimeSpan position)
        {
            // Si un fichier vidéo réel est chargé, l'utiliser
            if (_videoDecoder != null && _videoDecoder.IsInitialized &&
                !string.IsNullOrEmpty(_currentVideoFile) && _currentVideoFile != "PROCEDURAL_CONTENT")
            {
                try
                {
                    // ✅ OPTIMISATION: Réutiliser la frame si position identique (avec protection overflow)
                    double timeDiff = 0;
                    try
                    {
                        timeDiff = Math.Abs((position - _lastFrameTime).TotalMilliseconds);
                    }
                    catch (OverflowException)
                    {
                        timeDiff = double.MaxValue; // Force reload
                    }

                    if (_lastFrameData != null && timeDiff < 100) // Cache valide si <100ms différence
                    {
                        return new VideoFrame
                        {
                            Width = TARGET_WIDTH,
                            Height = TARGET_HEIGHT,
                            Data = _lastFrameData,
                            PixelFormat = SIPSorceryMedia.Abstractions.VideoPixelFormatsEnum.Rgb,
                            Timestamp = DateTime.UtcNow.Ticks
                        };
                    }

                    // ⚡ FIX: Use video's native frame rate, not our current FPS
                    var nativeFrameRate = _videoDecoder?.FrameRate ?? _currentFPS;
                    var maxFrames = _videoDecoder?.TotalFrames ?? 1;
                    var frameIndex = Math.Max(0, Math.Min((int)(position.TotalSeconds * nativeFrameRate), maxFrames - 1));
                    var rgbData = await _videoDecoder.ReadFrameAsync(frameIndex);
                    if (rgbData != null && rgbData.Length > 0)
                    {
                        // Mettre en cache pour prochaine fois
                        _lastFrameData = rgbData;
                        _lastFrameTime = position;

                        return new VideoFrame
                        {
                            Width = TARGET_WIDTH,
                            Height = TARGET_HEIGHT,
                            Data = rgbData,
                            PixelFormat = SIPSorceryMedia.Abstractions.VideoPixelFormatsEnum.Rgb,
                            Timestamp = DateTime.UtcNow.Ticks
                        };
                    }
                }
                catch (Exception ex)
                {
                    LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ Error reading video frame: {ex.Message}, using procedural fallback");
                }
            }

            // ⚡ FALLBACK PROCÉDURAL: Générer une frame simulée SEULEMENT si vraiment nécessaire
            return GenerateSimulatedFrame(frameNumber, position);
        }

        /// <summary>
        /// ❌ DISABLED: Frame simulée désactivée - utiliser seulement des fichiers vidéo réels
        /// </summary>
        private VideoFrame? GenerateSimulatedFrame(int frameNumber, TimeSpan position)
        {
            // ❌ PROCEDURAL FRAMES DISABLED: Return null to force video file usage only
            LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ PROCEDURAL FRAME DISABLED - No fallback generated (frame: {frameNumber})");
            return null;

            // ❌ DISABLED: Ancienne génération procédurale qui polluait le flux vidéo
            /*
            var frameData = new byte[TARGET_WIDTH * TARGET_HEIGHT * 3]; // RGB24

            // Créer pattern basé sur le nom du fichier et la position
            var fileHash = (_currentVideoFile?.GetHashCode() ?? 0) & 0xFF;
            var timeOffset = (int)(position.TotalSeconds * 10) % 255;

            for (int y = 0; y < TARGET_HEIGHT; y++)
            {
                for (int x = 0; x < TARGET_WIDTH; x++)
                {
                    var index = (y * TARGET_WIDTH + x) * 3;

                    // Pattern complexe qui simule une vraie vidéo
                    var pattern1 = (x + frameNumber + fileHash) % 255;
                    var pattern2 = (y + timeOffset) % 255;
                    var pattern3 = ((x + y) / 2 + frameNumber / 3) % 255;

                    // Créer zones distinctes pour simuler une scène
                    if (y < TARGET_HEIGHT / 3)
                    {
                        // Zone supérieure - "ciel" bleu animé
                        frameData[index] = (byte)(pattern1 / 3);           // R faible
                        frameData[index + 1] = (byte)(pattern2 / 2);       // G moyen
                        frameData[index + 2] = (byte)(150 + pattern3 / 3); // B élevé
                    }
                    else if (y < (TARGET_HEIGHT * 2) / 3)
                    {
                        // Zone centrale - "objets" colorés
                        frameData[index] = (byte)(100 + pattern1 / 2);     // R
                        frameData[index + 1] = (byte)(50 + pattern2 / 2);  // G
                        frameData[index + 2] = (byte)(pattern3);           // B
                    }
                    else
                    {
                        // Zone inférieure - "sol" vert/brun
                        frameData[index] = (byte)(50 + pattern3 / 4);      // R faible
                        frameData[index + 1] = (byte)(100 + pattern1 / 3); // G moyen
                        frameData[index + 2] = (byte)(pattern2 / 4);       // B faible
                    }
                }
            }

            return new VideoFrame
            {
                Width = TARGET_WIDTH,
                Height = TARGET_HEIGHT,
                Data = frameData,
                PixelFormat = SIPSorceryMedia.Abstractions.VideoPixelFormatsEnum.Rgb,
                Timestamp = DateTime.UtcNow.Ticks
            };
            */
        }

        /// <summary>
        /// Obtenir formats vidéo supportés
        /// </summary>
        public static string[] GetSupportedVideoFormats()
        {
            return new[]
            {
                "*.mp4", "*.avi", "*.mkv", "*.mov", "*.wmv",
                "*.flv", "*.webm", "*.m4v", "*.3gp", "*.mpg"
            };
        }

        /// <summary>
        /// Vérifier si un fichier est supporté
        /// </summary>
        public static bool IsSupportedVideoFile(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var supportedExtensions = new[]
            {
                ".mp4", ".avi", ".mkv", ".mov", ".wmv",
                ".flv", ".webm", ".m4v", ".3gp", ".mpg", ".mpeg"
            };

            return Array.Exists(supportedExtensions, ext => ext == extension);
        }

        /// <summary>
        /// Obtenir codecs supportés
        /// </summary>
        public static VideoCodecsEnum[] GetSupportedCodecs()
        {
            return VideoEncodingService.GetSupportedCodecs();
        }

        /// <summary>
        /// Obtenir statistiques de la caméra virtuelle avec encodage
        /// </summary>
        public string GetCameraStats()
        {
            var encodingStats = IsEncodingEnabled && _videoEncoder?.IsInitialized == true
                ? _videoEncoder.GetEncodingStats()
                : "Encoding disabled";

            var fileDisplay = _currentVideoFile == "PROCEDURAL_CONTENT" ? "Procedural Content" : Path.GetFileName(_currentVideoFile);
            return $"Virtual Camera - File: {fileDisplay}, " +
                   $"Position: {CurrentPosition:mm\\:ss}/{Duration:mm\\:ss}, " +
                   $"FPS: {_currentFPS}, Playing: {_isPlaying}, {encodingStats}";
        }

        public void Dispose()
        {
            try
            {
                StopPlaybackAsync().Wait(5000);
                _cancellationTokenSource?.Dispose();
                _videoEncoder?.Dispose();
                _videoDecoder?.Dispose();
                LogEvent?.Invoke("[SimpleVirtualCamera] 🗑️ Virtual camera with real video + H.264/VP8 encoding disposed");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ Error during dispose: {ex.Message}");
            }
        }
    }
}