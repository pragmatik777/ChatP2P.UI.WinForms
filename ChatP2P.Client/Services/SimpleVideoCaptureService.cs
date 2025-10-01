using System;
using System.IO;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using ChatP2P.Client.Services;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// 📹 Service de capture vidéo simplifié pour appels vidéo P2P
    /// Version compatible SipSorcery 6.0.11 sans dépendances complexes
    /// </summary>
    public class SimpleVideoCaptureService : IDisposable
    {
        private bool _isCapturing = false;
        private bool _isPlayingFile = false;
        private readonly object _lock = new object();
        private string? _currentVideoFile;
        private SimpleVirtualCameraService? _simpleVirtualCamera;
        private FFmpegVideoDecoderService? _ffmpegDecoder;

        // Events pour notifier de la disponibilité des frames vidéo
        public event Action<VideoFrame>? VideoFrameReady;
        public event Action<string>? LogEvent;
        public event Action<bool>? CaptureStateChanged;

        // Configuration vidéo
        private const int VIDEO_WIDTH = 640;
        private const int VIDEO_HEIGHT = 480;
        private const int VIDEO_FPS = 15;

        public bool IsCapturing => _isCapturing;
        public bool IsPlayingFile => _isPlayingFile;
        public VideoFormat? VideoFormat { get; private set; }
        public bool HasCamera { get; private set; } = false;

        public SimpleVideoCaptureService()
        {
            // 📹 NOUVEAU: Détecter la disponibilité de la caméra
            DetectCameraAvailability();
            LogEvent?.Invoke($"[VideoCapture] Service initialized - Camera: {(HasCamera ? "Available" : "Not detected")}");
        }

        /// <summary>
        /// Détecter si une caméra est disponible
        /// </summary>
        private void DetectCameraAvailability()
        {
            try
            {
                // Simulation de détection - en production, énumérer les périphériques DirectShow
                HasCamera = false; // Par défaut pas de caméra (plus réaliste pour VMs)
                LogEvent?.Invoke("[VideoCapture] Camera detection completed");
            }
            catch (Exception ex)
            {
                HasCamera = false;
                LogEvent?.Invoke($"[VideoCapture] No camera detected: {ex.Message}");
            }
        }

        /// <summary>
        /// Démarrer la capture vidéo (caméra ou fichier)
        /// </summary>
        public async Task<bool> StartCaptureAsync()
        {
            try
            {
                lock (_lock)
                {
                    if (_isCapturing || _isPlayingFile)
                    {
                        LogEvent?.Invoke("[VideoCapture] Already capturing/playing, ignoring start request");
                        return true;
                    }
                    _isCapturing = true;
                }

                if (HasCamera)
                {
                    LogEvent?.Invoke("[VideoCapture] ✅ Video capture started (camera)");
                }
                else
                {
                    LogEvent?.Invoke("[VideoCapture] ✅ Video capture started (simulation mode - no camera)");

                    // ✅ FIX: Démarrer simulation vidéo pour VMs sans caméra
                    _isPlayingFile = true;
                    _currentVideoFile = "simulation"; // Fake file pour activer la simulation
                    _ = Task.Run(async () => await SimulateVideoFilePlayback());
                }

                CaptureStateChanged?.Invoke(true);
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoCapture] ❌ Failed to start capture: {ex.Message}");
                CaptureStateChanged?.Invoke(false);
                return false;
            }
        }

        /// <summary>
        /// 🎬 AMÉLIORÉ: Démarrer la lecture d'un fichier vidéo réel avec FFMediaToolkit
        /// </summary>
        public async Task<bool> StartVideoFilePlaybackAsync(string videoFilePath)
        {
            try
            {
                if (!File.Exists(videoFilePath))
                {
                    LogEvent?.Invoke($"[VideoCapture] ❌ Video file not found: {videoFilePath}");
                    return false;
                }

                // Vérifier si c'est un format supporté
                if (!SimpleVirtualCameraService.IsSupportedVideoFile(videoFilePath))
                {
                    LogEvent?.Invoke($"[VideoCapture] ❌ Unsupported video format: {Path.GetExtension(videoFilePath)}");
                    return false;
                }

                // Arrêter la lecture précédente
                await StopCaptureAsync();

                lock (_lock)
                {
                    _isPlayingFile = true;
                    _currentVideoFile = videoFilePath;
                }

                // ✅ FIX CRITIQUE: S'assurer que FFmpeg est installé AVANT de charger le fichier
                LogEvent?.Invoke($"[VideoCapture] 🔧 Ensuring FFmpeg is available for video file decoding...");
                var ffmpegAvailable = await FFmpegInstaller.EnsureFFmpegInstalledAsync();

                if (!ffmpegAvailable)
                {
                    LogEvent?.Invoke($"[VideoCapture] ❌ FFmpeg not available, cannot load video file");
                    LogEvent?.Invoke($"[VideoCapture] 💡 Please install FFmpeg using the 'Install FFmpeg' button in VOIP Testing");
                    return false;
                }

                // Initialiser le décodeur FFmpeg réel
                _ffmpegDecoder = new FFmpegVideoDecoderService();
                _ffmpegDecoder.LogEvent += (msg) => LogEvent?.Invoke($"[FFmpegDecoder] {msg}");

                // Charger le fichier vidéo avec FFmpeg
                var loaded = await _ffmpegDecoder.LoadVideoFileAsync(videoFilePath);
                if (!loaded)
                {
                    LogEvent?.Invoke($"[VideoCapture] ❌ Failed to load video file with FFmpeg: {Path.GetFileName(videoFilePath)}");

                    // Fallback vers la caméra virtuelle simulée
                    LogEvent?.Invoke($"[VideoCapture] 🔄 Falling back to simulation mode...");
                    _simpleVirtualCamera = new SimpleVirtualCameraService();
                    _simpleVirtualCamera.VideoFrameReady += OnVirtualCameraFrameReady;
                    _simpleVirtualCamera.LogEvent += (msg) => LogEvent?.Invoke($"[VirtualCamera] {msg}");
                    _simpleVirtualCamera.PlaybackStateChanged += (playing) => CaptureStateChanged?.Invoke(playing);

                    var simLoaded = await _simpleVirtualCamera.LoadVideoFileAsync(videoFilePath);
                    if (!simLoaded) return false;

                    var simStarted = await _simpleVirtualCamera.StartPlaybackAsync();
                    if (!simStarted) return false;

                    LogEvent?.Invoke($"[VideoCapture] ✅ Fallback to simulation mode successful");
                    return true;
                }

                // Démarrer la capture de frames FFmpeg
                await StartFFmpegCaptureAsync();

                LogEvent?.Invoke($"[VideoCapture] ✅ Real video file playback started with FFmpeg: {Path.GetFileName(videoFilePath)}");
                LogEvent?.Invoke($"[VideoCapture] 📊 Video Info: {_ffmpegDecoder.GetVideoInfo()}");

                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoCapture] ❌ Failed to start video file playback: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Callback pour frames de la caméra virtuelle
        /// </summary>
        private void OnVirtualCameraFrameReady(VideoFrame frame)
        {
            VideoFrameReady?.Invoke(frame);
        }

        /// <summary>
        /// Démarrer la capture de frames FFmpeg avec lecture en boucle
        /// </summary>
        private async Task StartFFmpegCaptureAsync()
        {
            try
            {
                if (_ffmpegDecoder == null || !_ffmpegDecoder.IsInitialized)
                {
                    LogEvent?.Invoke("[VideoCapture] ❌ FFmpeg decoder not initialized");
                    return;
                }

                LogEvent?.Invoke("[VideoCapture] 🎬 Starting FFmpeg frame capture loop");

                // Démarrer la boucle de lecture FFmpeg en arrière-plan
                _ = Task.Run(async () => await FFmpegCaptureLoopAsync());
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoCapture] ❌ Error starting FFmpeg capture: {ex.Message}");
            }
        }

        /// <summary>
        /// Boucle de lecture de frames FFmpeg
        /// </summary>
        private async Task FFmpegCaptureLoopAsync()
        {
            try
            {
                var frameCount = 0;
                var frameInterval = TimeSpan.FromMilliseconds(1000.0 / VIDEO_FPS);

                LogEvent?.Invoke($"[VideoCapture] 🎬 FFmpeg capture loop started at {VIDEO_FPS} FPS");

                while (_isPlayingFile && _ffmpegDecoder?.IsInitialized == true)
                {
                    try
                    {
                        // Lire frame depuis FFmpeg
                        var frameData = await _ffmpegDecoder.ReadFrameAsync(frameCount);

                        if (frameData != null && frameData.Length > 0)
                        {
                            var videoFrame = new VideoFrame
                            {
                                Width = VIDEO_WIDTH,
                                Height = VIDEO_HEIGHT,
                                Data = frameData,
                                PixelFormat = VideoPixelFormatsEnum.Rgb,
                                Timestamp = DateTime.UtcNow.Ticks
                            };

                            VideoFrameReady?.Invoke(videoFrame);
                        }
                        else
                        {
                            // Si pas de frame (fin de vidéo), recommencer depuis le début
                            frameCount = 0;
                            LogEvent?.Invoke("[VideoCapture] 🔄 Video reached end, restarting loop");
                            continue;
                        }

                        frameCount++;

                        // Log périodique
                        if (frameCount % (VIDEO_FPS * 10) == 0) // Toutes les 10 secondes
                        {
                            var position = TimeSpan.FromSeconds(frameCount / (double)VIDEO_FPS);
                            LogEvent?.Invoke($"[VideoCapture] 📹 FFmpeg frame {frameCount}, Position: {position:mm\\:ss}");
                        }

                        await Task.Delay(frameInterval);
                    }
                    catch (Exception ex)
                    {
                        LogEvent?.Invoke($"[VideoCapture] ⚠️ Error reading FFmpeg frame {frameCount}: {ex.Message}");
                        frameCount++;
                        await Task.Delay(frameInterval);
                    }
                }

                LogEvent?.Invoke("[VideoCapture] 🛑 FFmpeg capture loop ended");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoCapture] ❌ FFmpeg capture loop error: {ex.Message}");
            }
        }

        /// <summary>
        /// Simuler la lecture de fichier vidéo (envoi de frames fictives ou réelles)
        /// </summary>
        private async Task SimulateVideoFilePlayback()
        {
            try
            {
                LogEvent?.Invoke($"[VideoCapture] 📹 Starting video simulation - File: {_currentVideoFile ?? "Built-in sample"}");

                int frameCount = 0;
                while (_isPlayingFile && !string.IsNullOrEmpty(_currentVideoFile))
                {
                    if (_currentVideoFile == "simulation")
                    {
                        // Générer frame de test avec pattern animé
                        var frameData = GenerateTestVideoFrame(frameCount);
                        var videoFrame = new VideoFrame
                        {
                            Width = VIDEO_WIDTH,
                            Height = VIDEO_HEIGHT,
                            Data = frameData,
                            PixelFormat = VideoPixelFormatsEnum.Rgb,
                            Timestamp = DateTime.Now.Ticks
                        };

                        VideoFrameReady?.Invoke(videoFrame);
                    }
                    else if (File.Exists(_currentVideoFile))
                    {
                        // TODO: Lire vraies frames du fichier vidéo
                        var frameData = await ReadVideoFileSample(_currentVideoFile, frameCount);
                        if (frameData != null && frameData.Length > 0)
                        {
                            var videoFrame = new VideoFrame
                            {
                                Width = VIDEO_WIDTH,
                                Height = VIDEO_HEIGHT,
                                Data = frameData,
                                PixelFormat = VideoPixelFormatsEnum.Rgb,
                                Timestamp = DateTime.Now.Ticks
                            };

                            VideoFrameReady?.Invoke(videoFrame);
                        }
                    }
                    else
                    {
                        // Fallback vers test pattern
                        var frameData = GenerateTestVideoFrame(frameCount);
                        var videoFrame = new VideoFrame
                        {
                            Width = VIDEO_WIDTH,
                            Height = VIDEO_HEIGHT,
                            Data = frameData,
                            PixelFormat = VideoPixelFormatsEnum.Rgb,
                            Timestamp = DateTime.Now.Ticks
                        };

                        VideoFrameReady?.Invoke(videoFrame);
                    }

                    frameCount++;
                    await Task.Delay(1000 / VIDEO_FPS); // Respecter le framerate (15 FPS)
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoCapture] ❌ Error during video simulation: {ex.Message}");
            }
        }

        /// <summary>
        /// Lire frame d'un fichier vidéo (placeholder pour vraie implémentation)
        /// </summary>
        private async Task<byte[]?> ReadVideoFileSample(string filePath, int frameNumber)
        {
            try
            {
                // TODO: Implémenter vraie lecture vidéo avec FFMpeg.NET ou équivalent
                // Pour l'instant, retourner pattern différent pour indiquer qu'on "lit" un fichier
                LogEvent?.Invoke($"[VideoCapture] 📁 Reading video file frame {frameNumber}: {Path.GetFileName(filePath)}");

                // Générer pattern différent pour files vs simulation
                return GenerateFileVideoFrame(frameNumber);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoCapture] ❌ Error reading video file: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Générer frame spéciale pour simulation de lecture fichier
        /// </summary>
        private byte[] GenerateFileVideoFrame(int frameNumber)
        {
            var frameSize = VIDEO_WIDTH * VIDEO_HEIGHT * 3; // RGB24
            var frameData = new byte[frameSize];

            // Pattern différent pour les "files" - grille avec mouvement
            for (int y = 0; y < VIDEO_HEIGHT; y++)
            {
                for (int x = 0; x < VIDEO_WIDTH; x++)
                {
                    int index = (y * VIDEO_WIDTH + x) * 3;

                    // Créer pattern grille avec animation
                    bool isGrid = ((x + frameNumber / 5) % 40 < 5) || ((y + frameNumber / 5) % 30 < 3);

                    if (isGrid)
                    {
                        frameData[index] = 255;     // R (blanc pour grille)
                        frameData[index + 1] = 255; // G
                        frameData[index + 2] = 255; // B
                    }
                    else
                    {
                        frameData[index] = (byte)(64 + frameNumber % 128);     // R (bleu animé)
                        frameData[index + 1] = (byte)(32);                     // G
                        frameData[index + 2] = (byte)(128 + frameNumber % 127); // B
                    }
                }
            }

            return frameData;
        }

        /// <summary>
        /// Générer une frame de test avec couleur qui change
        /// </summary>
        private byte[] GenerateTestVideoFrame(int frameNumber)
        {
            // Générer une frame simple avec couleur qui change
            var frameSize = VIDEO_WIDTH * VIDEO_HEIGHT * 3; // RGB24
            var frameData = new byte[frameSize];

            var color = (byte)(frameNumber % 255); // Couleur qui cycle
            for (int i = 0; i < frameSize; i += 3)
            {
                frameData[i] = color;     // R
                frameData[i + 1] = (byte)(255 - color); // G
                frameData[i + 2] = (byte)(frameNumber / 2 % 255); // B
            }

            return frameData;
        }

        /// <summary>
        /// Arrêter la capture vidéo et/ou lecture de fichier
        /// </summary>
        public async Task StopCaptureAsync()
        {
            try
            {
                lock (_lock)
                {
                    if (!_isCapturing && !_isPlayingFile)
                    {
                        LogEvent?.Invoke("[VideoCapture] Not capturing/playing, ignoring stop request");
                        return;
                    }
                    _isCapturing = false;
                    _isPlayingFile = false;
                    _currentVideoFile = null;
                }

                // Arrêter la caméra virtuelle si active
                if (_simpleVirtualCamera != null)
                {
                    await _simpleVirtualCamera.StopPlaybackAsync();
                    _simpleVirtualCamera.Dispose();
                    _simpleVirtualCamera = null;
                }

                // Arrêter le décodeur FFmpeg si actif
                if (_ffmpegDecoder != null)
                {
                    await _ffmpegDecoder.CloseVideoAsync();
                    _ffmpegDecoder.Dispose();
                    _ffmpegDecoder = null;
                }

                LogEvent?.Invoke("[VideoCapture] ✅ Video capture/playback stopped");
                CaptureStateChanged?.Invoke(false);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoCapture] ❌ Error stopping capture: {ex.Message}");
            }
        }

        /// <summary>
        /// Obtenir la liste des périphériques vidéo disponibles
        /// </summary>
        public static async Task<string[]> GetAvailableVideoDevicesAsync()
        {
            try
            {
                // En VM, lister les périphériques simulés pour debugging
                var devices = new List<string>();

                // TODO: En production, énumérer DirectShow devices ou MediaFoundation
                devices.Add("📹 Default Camera (VM Simulation)");
                devices.Add("🖥️ Screen Capture (VM Desktop)");
                devices.Add("📁 Video File Playback (Test Mode)");
                devices.Add("🎨 Test Pattern Generator");

                return devices.ToArray();
            }
            catch (Exception ex)
            {
                // En cas d'erreur, retourner au moins le mode simulation
                return new[] { $"⚠️ Error detecting video: {ex.Message}", "📹 Simulation Mode Available" };
            }
        }

        /// <summary>
        /// Changer la résolution vidéo
        /// </summary>
        public async Task<bool> ChangeResolutionAsync(int width, int height, int fps)
        {
            try
            {
                LogEvent?.Invoke($"[VideoCapture] Resolution changed to: {width}x{height}@{fps}fps (simulated)");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoCapture] ❌ Error changing resolution: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                StopCaptureAsync().Wait(1000);
                _simpleVirtualCamera?.Dispose();
                _ffmpegDecoder?.Dispose();
                LogEvent?.Invoke("[VideoCapture] Service disposed");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoCapture] ❌ Error during dispose: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Structure pour représenter une frame vidéo
    /// </summary>
    public class VideoFrame
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public VideoPixelFormatsEnum PixelFormat { get; set; }
        public long Timestamp { get; set; }
    }
}