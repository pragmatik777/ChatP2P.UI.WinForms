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

        // Configuration
        private const int TARGET_WIDTH = 640;
        private const int TARGET_HEIGHT = 480;
        private const int TARGET_FPS = 15;

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
        public bool IsEncodingEnabled { get; set; } = true;

        public SimpleVirtualCameraService()
        {
            LogEvent?.Invoke("[SimpleVirtualCamera] 📹 Virtual camera with H.264/VP8 encoding initialized");

            // Initialiser l'encodeur vidéo
            _videoEncoder = new VideoEncodingService();
            _videoEncoder.LogEvent += (msg) => LogEvent?.Invoke($"[VirtCam-Encoder] {msg}");
            _videoEncoder.EncodedVideoReady += (data) => EncodedVideoReady?.Invoke(data);
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

                lock (_lock)
                {
                    _currentVideoFile = filePath;
                }

                // Analyser le fichier pour obtenir la durée approximative
                var fileInfo = new FileInfo(filePath);
                var estimatedDuration = TimeSpan.FromSeconds(Math.Max(30, fileInfo.Length / 1_000_000)); // Estimation basique

                Duration = estimatedDuration;

                LogEvent?.Invoke($"[SimpleVirtualCamera] ✅ Video loaded (simulation): {Path.GetFileName(filePath)}");
                LogEvent?.Invoke($"[SimpleVirtualCamera] 📊 Estimated duration: {Duration:mm\\:ss}, Size: {fileInfo.Length / 1024 / 1024}MB");

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
                    LogEvent?.Invoke("[SimpleVirtualCamera] 📹 No video file loaded, using procedural content generation");
                    _currentVideoFile = "PROCEDURAL_CONTENT"; // Marquer comme contenu procédural
                    Duration = TimeSpan.FromMinutes(60); // 60 minutes de contenu procédural
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
        /// Boucle de génération de frames simulées
        /// </summary>
        private async Task SimulatedPlaybackLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                var frameInterval = TimeSpan.FromMilliseconds(1000.0 / TARGET_FPS);
                var frameCount = 0;

                LogEvent?.Invoke($"[SimpleVirtualCamera] 🎬 Starting simulated video playback at {TARGET_FPS} FPS");

                while (!cancellationToken.IsCancellationRequested && _isPlaying)
                {
                    try
                    {
                        // Calculer la position temporelle
                        var targetTime = TimeSpan.FromSeconds(frameCount / (double)TARGET_FPS);

                        // Si on dépasse la durée, recommencer (loop)
                        if (targetTime >= Duration)
                        {
                            frameCount = 0;
                            targetTime = TimeSpan.Zero;
                            LogEvent?.Invoke("[SimpleVirtualCamera] 🔄 Video loop restarted");
                        }

                        CurrentPosition = targetTime;

                        // Générer frame simulée basée sur le fichier et le temps
                        var videoFrame = GenerateSimulatedFrame(frameCount, targetTime);

                        // Émettre frame RGB brute
                        VideoFrameReady?.Invoke(videoFrame);

                        // Encoder en H.264/VP8 si activé
                        if (IsEncodingEnabled && _videoEncoder?.IsInitialized == true)
                        {
                            try
                            {
                                var encodedData = await _videoEncoder.EncodeFrameAsync(videoFrame);
                                if (encodedData != null && encodedData.Length > 0)
                                {
                                    // Les données encodées sont automatiquement émises via EncodedVideoReady
                                    // dans le VideoEncodingService
                                }
                            }
                            catch (Exception encEx)
                            {
                                LogEvent?.Invoke($"[SimpleVirtualCamera] ⚠️ Encoding error frame {frameCount}: {encEx.Message}");
                            }
                        }

                        frameCount++;

                        // Log périodique
                        if (frameCount % (TARGET_FPS * 5) == 0) // Toutes les 5 secondes
                        {
                            LogEvent?.Invoke($"[SimpleVirtualCamera] 📹 Frame {frameCount}, Position: {targetTime:mm\\:ss}/{Duration:mm\\:ss}");
                        }

                        await Task.Delay(frameInterval, cancellationToken);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        LogEvent?.Invoke($"[SimpleVirtualCamera] ⚠️ Error generating frame {frameCount}: {ex.Message}");
                        frameCount++;
                        await Task.Delay(frameInterval, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LogEvent?.Invoke("[SimpleVirtualCamera] 🛑 Simulated playback cancelled");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ Simulated playback loop error: {ex.Message}");
            }
        }

        /// <summary>
        /// Générer frame simulée avec pattern basé sur le fichier et le temps
        /// </summary>
        private VideoFrame GenerateSimulatedFrame(int frameNumber, TimeSpan position)
        {
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
                   $"Playing: {_isPlaying}, {encodingStats}";
        }

        public void Dispose()
        {
            try
            {
                StopPlaybackAsync().Wait(5000);
                _cancellationTokenSource?.Dispose();
                _videoEncoder?.Dispose();
                LogEvent?.Invoke("[SimpleVirtualCamera] 🗑️ Virtual camera with H.264/VP8 encoding disposed");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[SimpleVirtualCamera] ❌ Error during dispose: {ex.Message}");
            }
        }
    }
}