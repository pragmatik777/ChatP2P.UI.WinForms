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
        private double _exactFPS = 30.0; // Exact FPS for precise timing calculations

        // ⚡ FRAME PRELOADING: Cache frames in RAM for ultra-fast access
        // Removed: _frameBuffer - now delegating all buffering to EmguVideoDecoderService
        private const int BUFFER_SIZE = 60; // Preload 2 seconds of frames
        // Removed: _bufferStartIndex - no longer managing local buffer

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
        public double ExactFPS => _exactFPS;

        public SimpleVirtualCameraService()
        {
            LogEvent?.Invoke("[SimpleVirtualCamera] 📹 Virtual camera with real video + H.264/VP8 encoding initialized");

            // Initialiser l'encodeur vidéo
            _videoEncoder = new VideoEncodingService();
            _videoEncoder.LogEvent += (msg) => LogEvent?.Invoke($"[VirtCam-Encoder] {msg}");
            _videoEncoder.EncodedVideoReady += (data) => EncodedVideoReady?.Invoke(data);

            // Initialiser le décodeur EmguCV pour les vraies vidéos
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
                    // ⚡ PRECISION FPS: Use exact framerate from video file (preserve decimals)
                    var exactFPS = _videoDecoder.FrameRate;
                    if (exactFPS > 0 && exactFPS <= 120) // Reasonable FPS range
                    {
                        _currentFPS = (int)Math.Round(exactFPS); // For UI display
                        _exactFPS = exactFPS; // Store exact value for precise timing
                        LogEvent?.Invoke($"[SimpleVirtualCamera] 🎯 PRECISION FPS: {exactFPS:F3} FPS (rounded to {_currentFPS} for display)");
                    }
                    else
                    {
                        _currentFPS = 30; // Safe fallback
                        _exactFPS = 30.0;
                        LogEvent?.Invoke($"[SimpleVirtualCamera] ⚠️ Invalid FPS detected ({exactFPS:F3}), using 30 FPS fallback");
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
                // ⚡ PRECISION: Use exact FPS from video file for perfect timing
                var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _exactFPS);
                var frameIndex = 0;
                var estimatedMaxFrames = _videoDecoder?.TotalFrames ?? (_currentFPS * 60); // Fallback to 60s at current FPS
                var frameStartTime = DateTime.UtcNow;

                LogEvent?.Invoke($"[SimpleVirtualCamera] 🎬 ADAPTIVE LOOP: Playing video at {_currentFPS} FPS ({frameInterval.TotalMilliseconds:F1}ms/frame), estimated {estimatedMaxFrames} frames");

                while (!cancellationToken.IsCancellationRequested && _isPlaying)
                {
                    try
                    {
                        var currentFrameStart = DateTime.UtcNow;

                        // Calculate position for display only (use exact FPS for precision)
                        CurrentPosition = TimeSpan.FromSeconds(frameIndex / _exactFPS);

                        // ⚡ REAL END DETECTION: Try to read frame, let EmguCV tell us when it's really over
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

                            frameIndex++;
                        }
                        else
                        {
                            // ⚡ REAL END: Frame doesn't exist = we've reached the ACTUAL end
                            LogEvent?.Invoke($"[SimpleVirtualCamera] 🏁 REAL END: Frame {frameIndex} doesn't exist, actual video end reached. Looping from start.");
                            frameIndex = 0; // Loop back to beginning

                            // ⚡ MICRO DELAY: Just enough to avoid rapid retry, maintain smoothness
                            await Task.Delay(5, cancellationToken); // 5ms micro-delay only
                            continue; // Try again with frame 0
                        }

                        // ⚡ PRECISION TIMING: Compensate for processing time to maintain accurate FPS
                        var processingTime = DateTime.UtcNow - currentFrameStart;
                        var remainingDelay = frameInterval - processingTime;

                        if (remainingDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(remainingDelay, cancellationToken);
                        }
                        // If processing took longer than frame interval, continue immediately (catch up)

                        // Log every 5 seconds
                        if (frameIndex % (_currentFPS * 5) == 0)
                        {
                            LogEvent?.Invoke($"[SimpleVirtualCamera] 📹 Frame {frameIndex} at {CurrentPosition:mm\\:ss} ({_currentFPS} FPS, est. {estimatedMaxFrames} total)");
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
                // ⚡ DIRECT DELEGATION: Use EmguVideoDecoderService's intelligent buffering directly
                // No need for double buffering - EmguDecoder handles caching optimally
                var rgbData = await _videoDecoder.ReadFrameAsync(frameIndex);

                if (rgbData != null && rgbData.Length > 0)
                {
                    LogEvent?.Invoke($"[SimpleVirtualCamera] ✅ DELEGATED: Frame {frameIndex} loaded via EmguDecoder ({rgbData.Length} bytes)");

                    return new VideoFrame
                    {
                        Width = TARGET_WIDTH,
                        Height = TARGET_HEIGHT,
                        Data = rgbData,
                        PixelFormat = SIPSorceryMedia.Abstractions.VideoPixelFormatsEnum.Rgb,
                        Timestamp = DateTime.UtcNow.Ticks
                    };
                }

                LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ DELEGATED: Frame {frameIndex} not available from EmguDecoder - likely end");
                return null;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ Error getting buffered frame {frameIndex}: {ex.Message}");
                return null;
            }
        }

        // Removed: PreloadFrameBatch method - EmguVideoDecoderService handles all buffering

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