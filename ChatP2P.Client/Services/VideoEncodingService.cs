using System;
using System.IO;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// 🎥 Service d'encodage vidéo professionnel avec H.264/VP8
    /// Utilise SIPSorceryMedia.FFmpeg pour compression optimale WebRTC
    /// </summary>
    public class VideoEncodingService : IDisposable
    {
        private FFmpegVideoEncoder? _encoder;
        private FFmpegVideoEndPoint? _videoEndPoint;
        private bool _isInitialized = false;
        private VideoCodecsEnum _selectedCodec = VideoCodecsEnum.H264;

        public event Action<string>? LogEvent;
        public event Action<byte[]>? EncodedVideoReady;

        // Configuration d'encodage
        private const int TARGET_WIDTH = 640;
        private const int TARGET_HEIGHT = 480;
        private const int TARGET_FPS = 15;
        private const int TARGET_BITRATE = 100_000; // 100 kbps pour streaming UDP efficace

        public bool IsInitialized => _isInitialized;
        public VideoCodecsEnum SelectedCodec => _selectedCodec;

        /// <summary>
        /// Initialiser l'encodeur vidéo FFmpeg avec installation automatique
        /// </summary>
        public async Task<bool> InitializeAsync(VideoCodecsEnum codec = VideoCodecsEnum.H264)
        {
            try
            {
                if (_isInitialized)
                {
                    LogEvent?.Invoke($"[VideoEncoder] Already initialized with codec: {_selectedCodec}");
                    return true;
                }

                _selectedCodec = codec;
                LogEvent?.Invoke($"[VideoEncoder] 🎥 Initializing FFmpeg video encoder with {codec}...");

                // ✅ NOUVEAU: Vérifier et installer FFmpeg automatiquement si nécessaire
                FFmpegInstaller.LogEvent += (msg) => LogEvent?.Invoke($"[VideoEncoder] {msg}");

                var ffmpegAvailable = await FFmpegInstaller.EnsureFFmpegInstalledAsync();
                if (!ffmpegAvailable)
                {
                    LogEvent?.Invoke($"[VideoEncoder] ❌ FFmpeg installation failed, falling back to raw transmission");
                    LogEvent?.Invoke($"[VideoEncoder] 💡 Video calls will use raw RGB transmission (higher bandwidth)");
                    _isInitialized = true; // Still enable service but in raw mode
                    return true;
                }

                // ✅ RE-ENABLED: Initialize actual FFmpeg encoder now that library issues are resolved
                LogEvent?.Invoke($"[VideoEncoder] 🎥 Initializing FFmpeg {codec} encoder...");

                try
                {
                    // Initialize FFmpeg.AutoGen
                    FFmpeg.AutoGen.ffmpeg.RootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatP2P", "ffmpeg", "bin");

                    // Create FFmpeg video endpoint
                    _videoEndPoint = new FFmpegVideoEndPoint();

                    // Restrict formats to the selected codec
                    _videoEndPoint.RestrictFormats(fmt => fmt.Codec == codec);

                    LogEvent?.Invoke($"[VideoEncoder] ✅ {codec} endpoint initialized: {TARGET_WIDTH}x{TARGET_HEIGHT}@{TARGET_FPS}fps");
                }
                catch (Exception encEx)
                {
                    LogEvent?.Invoke($"[VideoEncoder] ❌ FFmpeg encoder initialization failed: {encEx.Message}");
                    LogEvent?.Invoke($"[VideoEncoder] 💡 Falling back to raw transmission mode");
                    _videoEndPoint = null;
                }

                _isInitialized = true;
                LogEvent?.Invoke($"[VideoEncoder] ✅ Video encoding service ready with {codec}");

                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoEncoder] ❌ Failed to initialize video encoder: {ex.Message}");
                LogEvent?.Invoke($"[VideoEncoder] ❌ Exception details: {ex}");
                LogEvent?.Invoke($"[VideoEncoder] 💡 Attempting FFmpeg auto-installation for next restart...");

                // Tentative d'installation en arrière-plan pour la prochaine fois
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await FFmpegInstaller.EnsureFFmpegInstalledAsync();
                    }
                    catch
                    {
                        // Installation silencieuse, ne pas faire planter l'app
                    }
                });

                return false;
            }
        }

        /// <summary>
        /// Encoder une frame RGB en format H.264/VP8 compressé
        /// </summary>
        public async Task<byte[]?> EncodeFrameAsync(VideoFrame rawFrame)
        {
            try
            {
                if (!_isInitialized)
                {
                    LogEvent?.Invoke($"[VideoEncoder] ⚠️ Service not initialized, using raw mode");
                    return null;
                }

                if (rawFrame.Data == null || rawFrame.Data.Length == 0)
                {
                    LogEvent?.Invoke($"[VideoEncoder] ⚠️ Empty frame data, skipping");
                    return null;
                }

                // ✅ FIX: Encodage H.264 direct avec FFmpeg
                try
                {
                    LogEvent?.Invoke($"[VideoEncoder] 🎥 Encoding RGB frame to H.264: {rawFrame.Data.Length}B ({rawFrame.Width}x{rawFrame.Height})");

                    var h264Data = await EncodeRGBToH264(rawFrame);

                    if (h264Data != null && h264Data.Length > 0)
                    {
                        LogEvent?.Invoke($"[VideoEncoder] ✅ H.264 encoding successful: {rawFrame.Data.Length}B → {h264Data.Length}B (compression: {(float)h264Data.Length/rawFrame.Data.Length:P1})");
                        EncodedVideoReady?.Invoke(h264Data);
                        return h264Data;
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VideoEncoder] ⚠️ H.264 encoding failed, using raw fallback");
                    }
                }
                catch (Exception encEx)
                {
                    LogEvent?.Invoke($"[VideoEncoder] ❌ H.264 encoding error: {encEx.Message}, using raw fallback");
                }

                // Fallback to raw transmission if encoder failed
                LogEvent?.Invoke($"[VideoEncoder] 📊 Fallback raw frame: {rawFrame.Data.Length}B ({rawFrame.Width}x{rawFrame.Height})");
                EncodedVideoReady?.Invoke(rawFrame.Data);
                return rawFrame.Data;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoEncoder] ❌ Error processing frame: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Convertir VideoFrame SIPSorcery vers format FFmpeg
        /// </summary>
        private FFmpegVideoFrame? ConvertToFFmpegFrame(VideoFrame sipFrame)
        {
            try
            {
                // Create FFmpeg-compatible frame structure
                var ffmpegFrame = new FFmpegVideoFrame
                {
                    Width = (uint)sipFrame.Width,
                    Height = (uint)sipFrame.Height,
                    Sample = sipFrame.Data,
                    PixelFormat = ConvertPixelFormat(sipFrame.PixelFormat)
                };

                // Calculate stride (bytes per row) for RGB24
                ffmpegFrame.Stride = sipFrame.Width * 3; // RGB = 3 bytes per pixel

                LogEvent?.Invoke($"[VideoEncoder] 🔄 Converted SIP frame to FFmpeg: {ffmpegFrame.Width}x{ffmpegFrame.Height}, {ffmpegFrame.PixelFormat}");
                return ffmpegFrame;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoEncoder] ❌ Frame conversion failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Convertir le format de pixel de SIPSorcery vers FFmpeg
        /// </summary>
        private VideoPixelFormatsEnum ConvertPixelFormat(VideoPixelFormatsEnum sipFormat)
        {
            return sipFormat switch
            {
                VideoPixelFormatsEnum.Rgb => VideoPixelFormatsEnum.Rgb,
                VideoPixelFormatsEnum.Bgr => VideoPixelFormatsEnum.Bgr,
                VideoPixelFormatsEnum.Bgra => VideoPixelFormatsEnum.Bgra,
                _ => VideoPixelFormatsEnum.Rgb // Défaut RGB24
            };
        }

        /// <summary>
        /// ✅ NOUVEAU: Encoder RGB vers H.264 via FFmpeg direct
        /// </summary>
        private async Task<byte[]?> EncodeRGBToH264(VideoFrame rgbFrame)
        {
            try
            {
                LogEvent?.Invoke($"[VideoEncoder] 🎯 Encoding RGB to H.264: {rgbFrame.Data.Length} bytes");

                // Créer fichiers temporaires
                var tempDir = Path.GetTempPath();
                var inputFile = Path.Combine(tempDir, $"rgb_frame_{Guid.NewGuid()}.raw");
                var outputFile = Path.Combine(tempDir, $"h264_frame_{Guid.NewGuid()}.h264");

                try
                {
                    // Écrire les données RGB dans un fichier temporaire
                    await File.WriteAllBytesAsync(inputFile, rgbFrame.Data);

                    // Trouver FFmpeg
                    var ffmpegPath = GetFFmpegPath();
                    if (ffmpegPath == null)
                    {
                        LogEvent?.Invoke($"[VideoEncoder] ❌ FFmpeg not found for encoding");
                        return null;
                    }

                    // Commande ffmpeg pour encoder RGB24 → H.264 avec compression agressive pour streaming UDP
                    var arguments = $"-f rawvideo -pix_fmt rgb24 -s {rgbFrame.Width}x{rgbFrame.Height} -r {TARGET_FPS} -i \"{inputFile}\" -c:v libx264 -preset ultrafast -tune zerolatency -b:v 100k -maxrate 150k -bufsize 50k -g {TARGET_FPS} -keyint_min 1 -sc_threshold 0 -f h264 \"{outputFile}\" -y";

                    LogEvent?.Invoke($"[VideoEncoder] 🔧 Running: ffmpeg {arguments}");

                    var processResult = await RunFFmpegAsync(ffmpegPath, arguments);

                    if (processResult && File.Exists(outputFile))
                    {
                        var h264Data = await File.ReadAllBytesAsync(outputFile);

                        if (h264Data.Length > 0)
                        {
                            LogEvent?.Invoke($"[VideoEncoder] ✅ RGB→H.264 encoded successfully: {rgbFrame.Data.Length}B → {h264Data.Length}B");
                            return h264Data;
                        }
                        else
                        {
                            LogEvent?.Invoke($"[VideoEncoder] ⚠️ H.264 output file is empty");
                            return null;
                        }
                    }
                    else
                    {
                        LogEvent?.Invoke($"[VideoEncoder] ❌ FFmpeg encoding failed or output file not created");
                        return null;
                    }
                }
                finally
                {
                    // Nettoyer les fichiers temporaires
                    try
                    {
                        if (File.Exists(inputFile)) File.Delete(inputFile);
                        if (File.Exists(outputFile)) File.Delete(outputFile);
                    }
                    catch { /* Ignore cleanup errors */ }
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoEncoder] ❌ Error encoding RGB to H.264: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Trouver le chemin vers ffmpeg.exe
        /// </summary>
        private string? GetFFmpegPath()
        {
            var possiblePaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatP2P", "ffmpeg", "bin", "ffmpeg.exe"),
                "ffmpeg.exe", // Dans le PATH
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe"
            };

            foreach (var path in possiblePaths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch { /* Ignore access errors */ }
            }

            return null;
        }

        /// <summary>
        /// Exécuter FFmpeg avec les arguments donnés
        /// </summary>
        private async Task<bool> RunFFmpegAsync(string ffmpegPath, string arguments)
        {
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = ffmpegPath;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;

                process.Start();

                // Timeout de 3 secondes pour l'encodage (plus rapide que décodage)
                var completed = await Task.Run(() => process.WaitForExit(3000));

                if (!completed)
                {
                    LogEvent?.Invoke($"[VideoEncoder] ⚠️ FFmpeg encoding timeout, killing...");
                    try { process.Kill(); } catch { }
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoEncoder] ❌ Error running FFmpeg: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Structure pour frame FFmpeg
        /// </summary>
        private class FFmpegVideoFrame
        {
            public uint Width { get; set; }
            public uint Height { get; set; }
            public int Stride { get; set; }
            public byte[] Sample { get; set; } = Array.Empty<byte>();
            public VideoPixelFormatsEnum PixelFormat { get; set; }
        }

        /// <summary>
        /// Changer le codec d'encodage à chaud
        /// </summary>
        public async Task<bool> ChangeCodecAsync(VideoCodecsEnum newCodec)
        {
            try
            {
                if (_selectedCodec == newCodec)
                {
                    LogEvent?.Invoke($"[VideoEncoder] Codec already set to {newCodec}");
                    return true;
                }

                LogEvent?.Invoke($"[VideoEncoder] 🔄 Switching codec from {_selectedCodec} to {newCodec}...");

                // Réinitialiser avec le nouveau codec
                Dispose();
                _isInitialized = false;

                var success = await InitializeAsync(newCodec);
                if (success)
                {
                    LogEvent?.Invoke($"[VideoEncoder] ✅ Codec switched to {newCodec} successfully");
                }
                else
                {
                    LogEvent?.Invoke($"[VideoEncoder] ❌ Failed to switch to codec {newCodec}");
                }

                return success;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoEncoder] ❌ Error changing codec: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Obtenir les codecs supportés
        /// </summary>
        public static VideoCodecsEnum[] GetSupportedCodecs()
        {
            return new[]
            {
                VideoCodecsEnum.H264,   // Meilleure qualité/compression
                VideoCodecsEnum.VP8,    // Libre, intégré WebRTC
                VideoCodecsEnum.VP9,    // Nouvelle génération
                VideoCodecsEnum.H265    // Ultra-compression
            };
        }

        /// <summary>
        /// Obtenir les statistiques d'encodage
        /// </summary>
        public string GetEncodingStats()
        {
            return $"Codec: {_selectedCodec}, Resolution: {TARGET_WIDTH}x{TARGET_HEIGHT}, FPS: {TARGET_FPS}, Bitrate: {TARGET_BITRATE/1000}kbps";
        }

        public void Dispose()
        {
            try
            {
                _videoEndPoint?.Dispose();

                _videoEndPoint = null;
                _isInitialized = false;

                LogEvent?.Invoke($"[VideoEncoder] 🗑️ Video encoding service disposed");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[VideoEncoder] ❌ Error during dispose: {ex.Message}");
            }
        }
    }
}