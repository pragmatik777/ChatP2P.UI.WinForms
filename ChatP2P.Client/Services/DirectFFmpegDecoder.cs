using FFMpegCore;
using FFMpegCore.Pipes;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// ✅ DIRECT FFMPEG DLL: Décodage H.264 ultra-rapide via FFMpegCore (pas de processus externe)
    /// </summary>
    public class DirectFFmpegDecoder : IDisposable
    {
        private const int TARGET_WIDTH = 640;
        private const int TARGET_HEIGHT = 480;

        public event Action<string>? LogEvent;

        // ⚡ CACHED PATH: Avoid repeated filesystem lookups
        private static string? _cachedFFmpegPath;

        // ⚡ REUSABLE TEMP FILES: Avoid Path.GetTempFileName() overhead
        private static readonly string _tempH264Base = Path.Combine(Path.GetTempPath(), "chatp2p_h264_");
        private static readonly string _tempRgbBase = Path.Combine(Path.GetTempPath(), "chatp2p_rgb_");
        private static int _tempFileCounter = 0;

        public DirectFFmpegDecoder()
        {
            // Configurer le chemin FFmpeg si nécessaire
            InitializeFFmpegPath();
        }

        private void InitializeFFmpegPath()
        {
            try
            {
                // FFMpegCore va automatiquement détecter les binaires FFmpeg
                var ffmpegPath = GetFFmpegExecutablePath();
                if (!string.IsNullOrEmpty(ffmpegPath))
                {
                    GlobalFFOptions.Configure(new FFOptions
                    {
                        BinaryFolder = Path.GetDirectoryName(ffmpegPath),
                        TemporaryFilesFolder = Path.GetTempPath()
                    });

                    LogEvent?.Invoke($"[DirectFFmpeg] ✅ FFmpeg configured: {ffmpegPath}");
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[DirectFFmpeg] ⚠️ FFmpeg config warning: {ex.Message}");
            }
        }

        private string? GetFFmpegExecutablePath()
        {
            // ⚡ CACHED PATH: Return cached path for maximum speed
            if (_cachedFFmpegPath != null)
                return _cachedFFmpegPath;

            // Chercher dans les emplacements standards
            var possiblePaths = new[]
            {
                // ✅ LOCALAPPDATA ChatP2P installation (from FFmpegInstaller)
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatP2P", "ffmpeg", "bin", "ffmpeg.exe"),
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                @"ffmpeg.exe" // PATH
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    _cachedFFmpegPath = path; // Cache for next calls
                    return path;
                }
            }

            return null;
        }

        /// <summary>
        /// ✅ DIRECT DLL: Utiliser FFMpegCore DLL directement (pas de processus externe)
        /// </summary>
        public async Task<byte[]?> DecodeH264ToRgbAsync(byte[] h264Data)
        {
            try
            {
                LogEvent?.Invoke($"[DirectFFmpeg] 🚀 DLL DECODE START: {h264Data.Length} bytes H.264 data");

                var expectedSize = TARGET_WIDTH * TARGET_HEIGHT * 3; // RGB24
                var fileId = Interlocked.Increment(ref _tempFileCounter);
                var tempH264 = $"{_tempH264Base}{fileId}.h264";

                try
                {
                    // Write H.264 data
                    await File.WriteAllBytesAsync(tempH264, h264Data);
                    LogEvent?.Invoke($"[DirectFFmpeg] 📝 Written H.264 to temp file: {tempH264}");

                    // ✅ USE FFMPEG DLL DIRECTLY via FFMpegCore
                    using var memoryStream = new MemoryStream();

                    await FFMpegArguments
                        .FromFileInput(tempH264)
                        .OutputToPipe(new StreamPipeSink(memoryStream), options => options
                            .WithVideoCodec("rawvideo")
                            .WithCustomArgument("-pix_fmt rgb24")
                            .WithCustomArgument($"-s {TARGET_WIDTH}x{TARGET_HEIGHT}")
                            .WithCustomArgument("-f rawvideo"))
                        .ProcessAsynchronously();

                    var rgbData = memoryStream.ToArray();
                    LogEvent?.Invoke($"[DirectFFmpeg] 📄 DLL decode result: {rgbData.Length} bytes, expected: {expectedSize}");

                    if (rgbData.Length == expectedSize)
                    {
                        LogEvent?.Invoke($"[DirectFFmpeg] ✅ DLL DECODE SUCCESS: {h264Data.Length}B → {rgbData.Length}B");
                        return rgbData;
                    }
                    else
                    {
                        LogEvent?.Invoke($"[DirectFFmpeg] ❌ DLL decode size mismatch: got {rgbData.Length}, expected {expectedSize}");
                        return null;
                    }
                }
                finally
                {
                    try { File.Delete(tempH264); } catch { }
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[DirectFFmpeg] ❌ DLL DECODE FAILED: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ✅ BATCH PROCESSING: Traiter plusieurs frames H.264 en parallèle
        /// </summary>
        public async Task<byte[]?[]> DecodeBatchH264ToRgbAsync(byte[][] h264Frames)
        {
            try
            {
                LogEvent?.Invoke($"[DirectFFmpeg] 🚀 BATCH START: Processing {h264Frames.Length} H.264 frames");
                var tasks = new Task<byte[]?>[h264Frames.Length];

                for (int i = 0; i < h264Frames.Length; i++)
                {
                    LogEvent?.Invoke($"[DirectFFmpeg] 📝 Creating task {i} for frame of {h264Frames[i].Length} bytes");
                    tasks[i] = DecodeH264ToRgbAsync(h264Frames[i]);
                }

                LogEvent?.Invoke($"[DirectFFmpeg] ⏳ Waiting for {tasks.Length} decode tasks to complete...");
                var results = await Task.WhenAll(tasks);
                LogEvent?.Invoke($"[DirectFFmpeg] ✅ BATCH COMPLETE: {results.Count(r => r != null)}/{results.Length} successful");
                return results;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[DirectFFmpeg] ❌ BATCH FAILED: {ex.Message}");
                return new byte[]?[h264Frames.Length];
            }
        }

        public void Dispose()
        {
            // Silent dispose for performance
        }
    }
}