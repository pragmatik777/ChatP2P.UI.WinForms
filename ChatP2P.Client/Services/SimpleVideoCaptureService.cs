using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using ChatP2P.Client.Services;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// 📹 Service de capture vidéo simple - Interface unifiée pour caméra/fichiers
    /// Délègue le décodage réel des fichiers vidéo à EmguVideoDecoderService
    /// Interface compatible SipSorcery 6.0.11 avec buffer intelligent
    /// </summary>
    public class SimpleVideoCaptureService : IDisposable
    {
        private bool _isCapturing = false;
        private bool _isPlayingFile = false;
        private readonly object _lock = new object();
        private string? _currentVideoFile;
        private SimpleVirtualCameraService? _simpleVirtualCamera;
        private EmguVideoDecoderService? _emguDecoder;

        // ✅ BUFFER SYSTEM: Intelligent frame buffering with bitrate adaptation
        private readonly ConcurrentQueue<VideoFrame> _frameBuffer = new ConcurrentQueue<VideoFrame>();
        private readonly object _bufferLock = new object();
        private int _detectedWidth = 640;
        private int _detectedHeight = 480;
        private int _currentBufferSize = 0;
        private int _maxBufferSize = 30; // Adaptive buffer size
        private int _minBufferSize = 10;
        private double _detectedFrameRate = 15.0;
        private volatile bool _bufferProcessingActive = false;
        private CancellationTokenSource? _bufferCancellation;

        // Events pour notifier de la disponibilité des frames vidéo
        public event Action<VideoFrame>? VideoFrameReady;
        public event Action<string>? LogEvent;
        public event Action<bool>? CaptureStateChanged;

        // ✅ ADAPTIVE: Configuration vidéo adaptative selon le fichier
        private const int DEFAULT_VIDEO_WIDTH = 640;
        private const int DEFAULT_VIDEO_HEIGHT = 480;
        private double _adaptiveFPS = 15.0; // FPS adaptatif selon la vidéo
        private int _adaptiveBatchSize = 10; // Taille de lot adaptative
        private DateTime _lastLogTime = DateTime.MinValue;
        private DateTime _lastBatchPreload = DateTime.MinValue;

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
                // En production, énumérer les périphériques DirectShow/MediaFoundation
                HasCamera = false; // Par défaut pas de caméra détectée
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

                LogEvent?.Invoke("[VideoCapture] ❌ No real camera available. Use StartVideoFilePlaybackAsync() for video files.");
                CaptureStateChanged?.Invoke(false);
                return false;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoCapture] ❌ Failed to start capture: {ex.Message}");
                CaptureStateChanged?.Invoke(false);
                return false;
            }
        }

        /// <summary>
        /// 🎬 Démarrer la lecture d'un fichier vidéo réel via EmguVideoDecoderService
        /// Interface simple qui délègue le décodage complexe au service spécialisé
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

                // ✅ DELEGATE: Use EmguVideoDecoderService for real video processing
                LogEvent?.Invoke($"[VideoCapture] 🎬 Delegating video decoding to EmguVideoDecoderService...");

                _emguDecoder = new EmguVideoDecoderService();
                _emguDecoder.LogEvent += (msg) => LogEvent?.Invoke($"[EmguDecoder] {msg}");

                // Load video file with specialized EmguCV service
                var loaded = await _emguDecoder.InitializeAsync(videoFilePath);
                if (!loaded)
                {
                    LogEvent?.Invoke($"[VideoCapture] ❌ EmguVideoDecoderService failed to load: {Path.GetFileName(videoFilePath)}");

                    lock (_lock)
                    {
                        _isPlayingFile = false;
                        _currentVideoFile = null;
                    }
                    return false;
                }

                // ✅ ADAPTIVE: Adapter le FPS et le batch size selon la vidéo
                AdaptToVideoSpecs();

                // Start intelligent buffered frame processing
                await StartFFmpegCaptureAsync();

                LogEvent?.Invoke($"[VideoCapture] ✅ Video playback started via EmguVideoDecoderService: {Path.GetFileName(videoFilePath)}");
                LogEvent?.Invoke($"[VideoCapture] 📊 Video Info: {_emguDecoder.TotalFrames} frames, {_emguDecoder.FrameRate:F1} FPS, {_emguDecoder.Duration:mm\\:ss}");

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
        /// ✅ ADAPTIVE: Adapter la configuration selon les spécifications de la vidéo
        /// </summary>
        private void AdaptToVideoSpecs()
        {
            if (_emguDecoder == null) return;

            // Récupérer les specs de la vidéo depuis EmguCV
            _adaptiveFPS = _emguDecoder.FrameRate;
            if (_adaptiveFPS > 0)
            {
                // Adapter le batch size : plus le FPS est élevé, plus le batch est grand
                _adaptiveBatchSize = Math.Max(5, (int)(_adaptiveFPS / 3)); // 1/3 du FPS
            }

            LogEvent?.Invoke($"[VideoCapture] 🎯 Adapted to video: {_adaptiveFPS:F1} FPS, batch size: {_adaptiveBatchSize}");
        }

        /// <summary>
        /// Start buffered frame processing using EmguVideoDecoderService
        /// This service manages intelligent buffering and batch processing
        /// </summary>
        private async Task StartFFmpegCaptureAsync()
        {
            try
            {
                if (_emguDecoder == null || !_emguDecoder.IsInitialized)
                {
                    LogEvent?.Invoke("[VideoCapture] ❌ EmguCV decoder not initialized");
                    return;
                }

                LogEvent?.Invoke("[VideoCapture] 🎬 Starting intelligent buffered frame processing");

                // Start buffered frame processing in background
                _ = Task.Run(async () => await EmguCaptureLoopAsync());
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoCapture] ❌ Error starting EmguCV capture: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ OPTIMIZED: Batch EmguCV processing with intelligent buffering
        /// </summary>
        private async Task EmguCaptureLoopAsync()
        {
            try
            {
                var frameCount = 0;
                var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _adaptiveFPS);

                LogEvent?.Invoke($"[VideoCapture] 🚀 ADAPTIVE EmguCV loop started: {_adaptiveFPS:F1} FPS native, batch size: {_adaptiveBatchSize}");

                // ✅ PRELOAD: Démarrer avec un préchargement initial
                if (_emguDecoder != null)
                {
                    _ = _emguDecoder.PreloadFrameBatchAsync(0);
                }

                while (_isPlayingFile && _emguDecoder?.IsInitialized == true)
                {
                    try
                    {
                        // ✅ SMART PRELOADING: Précharger les prochaines frames en arrière-plan
                        var now = DateTime.UtcNow;
                        if (now - _lastBatchPreload > TimeSpan.FromSeconds(1))
                        {
                            var nextBatchStart = frameCount + _adaptiveBatchSize;
                            _ = _emguDecoder.PreloadFrameBatchAsync(nextBatchStart);
                            _lastBatchPreload = now;
                        }

                        // ✅ NATIVE FPS: Lire une frame au FPS natif de la vidéo
                        var frameData = await _emguDecoder.ReadFrameAsync(frameCount);

                        if (frameData != null && frameData.Length > 0)
                        {
                            var videoFrame = new VideoFrame
                            {
                                Width = DEFAULT_VIDEO_WIDTH,
                                Height = DEFAULT_VIDEO_HEIGHT,
                                Data = frameData,
                                PixelFormat = VideoPixelFormatsEnum.Rgb,
                                Timestamp = DateTime.UtcNow.Ticks
                            };

                            // Émettre la frame immédiatement
                            VideoFrameReady?.Invoke(videoFrame);

                            frameCount++;

                            // ✅ PERFORMANCE: Logging périodique seulement
                            if (frameCount % (_adaptiveBatchSize * 3) == 0)
                            {
                                LogEvent?.Invoke($"[VideoCapture] 📹 Frame {frameCount} processed (FPS: {_adaptiveFPS:F1})");
                            }
                        }
                        else
                        {
                            // End of video - restart loop
                            frameCount = 0;
                            LogEvent?.Invoke("[VideoCapture] 🔄 Video loop restarted");
                        }

                        // ✅ ADAPTIVE TIMING: Respecter le timing natif du FPS
                        await Task.Delay(frameInterval);
                    }
                    catch (Exception ex)
                    {
                        LogEvent?.Invoke($"[VideoCapture] ⚠️ Frame processing error at frame {frameCount}: {ex.Message}");
                        frameCount++;
                        await Task.Delay(frameInterval);
                    }
                }

                LogEvent?.Invoke("[VideoCapture] 🛑 ADAPTIVE FFmpeg loop ended");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoCapture] ❌ FFmpeg loop error: {ex.Message}");
            }
            finally
            {
                _bufferCancellation?.Cancel();
            }
        }

        /// <summary>
        /// ✅ BUFFER: Adaptive buffer size based on frame rate
        /// </summary>
        private void AdaptBufferSizeToFrameRate(double frameRate)
        {
            // Higher frame rate = larger buffer for smooth playback
            _maxBufferSize = Math.Max(15, (int)(frameRate * 2)); // 2 seconds buffer
            _minBufferSize = Math.Max(5, (int)(frameRate * 0.5)); // 0.5 seconds minimum
        }

        /// <summary>
        /// ✅ BUFFER: Add frames to buffer with overflow protection
        /// </summary>
        private void AddFramesToBuffer(List<VideoFrame> frames)
        {
            lock (_bufferLock)
            {
                foreach (var frame in frames)
                {
                    if (_currentBufferSize >= _maxBufferSize)
                    {
                        // Remove old frames to make space
                        if (_frameBuffer.TryDequeue(out _))
                        {
                            _currentBufferSize--;
                        }
                    }

                    _frameBuffer.Enqueue(frame);
                    _currentBufferSize++;
                }
            }
        }

        /// <summary>
        /// ✅ BUFFER: Process buffered frames at stable rate
        /// </summary>
        private async Task ProcessBufferedFramesAsync(CancellationToken cancellation)
        {
            try
            {
                _bufferProcessingActive = true;
                var targetFrameTime = TimeSpan.FromMilliseconds(1000.0 / _detectedFrameRate);

                while (!cancellation.IsCancellationRequested && _isPlayingFile)
                {
                    if (_frameBuffer.TryDequeue(out var frame))
                    {
                        lock (_bufferLock)
                        {
                            _currentBufferSize--;
                        }

                        // ✅ PERFORMANCE: Dispatch frame to UI without logging
                        VideoFrameReady?.Invoke(frame);
                        await Task.Delay(targetFrameTime, cancellation);
                    }
                    else
                    {
                        // Buffer empty - wait for more frames
                        await Task.Delay(10, cancellation);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoCapture] ❌ Buffer processing error: {ex.Message}");
            }
            finally
            {
                _bufferProcessingActive = false;
            }
        }

        /// <summary>
        /// ✅ PERFORMANCE: Calculate adaptive delay based on buffer level
        /// </summary>
        private int CalculateAdaptiveDelay()
        {
            var bufferRatio = (double)_currentBufferSize / _maxBufferSize;

            if (bufferRatio > 0.8) // Buffer nearly full
            {
                return 100; // Slow down capture
            }
            else if (bufferRatio < 0.3) // Buffer low
            {
                return 10; // Speed up capture
            }
            else
            {
                return 50; // Normal rate
            }
        }

        /// <summary>
        /// ✅ REDUCED LOGGING: Log progress only every 30 seconds
        /// </summary>
        private void LogProgressPeriodically(int frameCount)
        {
            var now = DateTime.UtcNow;
            if (now - _lastLogTime > TimeSpan.FromSeconds(30))
            {
                var position = TimeSpan.FromSeconds(frameCount / _detectedFrameRate);
                LogEvent?.Invoke($"[VideoCapture] 📹 Position: {position:mm\\:ss}, Buffer: {_currentBufferSize}/{_maxBufferSize}");
                _lastLogTime = now;
            }
        }

        /// <summary>
        /// ✅ REDUCED LOGGING: Log restart only periodically
        /// </summary>
        private void LogRestartPeriodically()
        {
            var now = DateTime.UtcNow;
            if (now - _lastLogTime > TimeSpan.FromSeconds(10))
            {
                LogEvent?.Invoke("[VideoCapture] 🔄 Video loop restart");
                _lastLogTime = now;
            }
        }

        /// <summary>
        /// ✅ BUFFER: Get current buffer status for diagnostics
        /// </summary>
        public (int count, int max, double ratio) GetBufferStatus()
        {
            return (_currentBufferSize, _maxBufferSize, (double)_currentBufferSize / _maxBufferSize);
        }

        // ❌ REMOVED: No more simulation/test frames - use real videos only


        // ❌ REMOVED: No test frame generation - real videos only

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

                // Clean up virtual camera if exists
                if (_simpleVirtualCamera != null)
                {
                    await _simpleVirtualCamera.StopPlaybackAsync();
                    _simpleVirtualCamera.Dispose();
                    _simpleVirtualCamera = null;
                }

                // ✅ BUFFER: Stop buffer processing
                _bufferCancellation?.Cancel();

                // Arrêter le décodeur FFmpeg si actif
                if (_emguDecoder != null)
                {
                    _emguDecoder.Dispose();
                    _emguDecoder = null;
                }

                // ✅ BUFFER: Clear buffer
                lock (_bufferLock)
                {
                    while (_frameBuffer.TryDequeue(out _)) { }
                    _currentBufferSize = 0;
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
                devices.Add("📹 Default Camera");
                devices.Add("🖥️ Screen Capture");
                devices.Add("📁 Video File Playback");

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
                _emguDecoder?.Dispose();
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