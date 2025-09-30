using System;
using System.IO;
using System.Threading.Tasks;
using FFMediaToolkit;
using FFMediaToolkit.Decoding;
using FFMediaToolkit.Graphics;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// 🎬 Service de décodage vidéo FFmpeg avec FFMediaToolkit
    /// Lecture réelle de fichiers vidéo pour streaming via caméra virtuelle
    /// </summary>
    public class FFmpegVideoDecoderService : IDisposable
    {
        private MediaFile? _mediaFile;
        private string? _currentFilePath;
        private bool _isInitialized = false;
        private int _totalFrames = 0;
        private double _frameRate = 30.0;
        private TimeSpan _duration = TimeSpan.Zero;

        public event Action<string>? LogEvent;

        // Propriétés publiques
        public bool IsInitialized => _isInitialized;
        public int TotalFrames => _totalFrames;
        public double FrameRate => _frameRate;
        public TimeSpan Duration => _duration;
        public string? CurrentFilePath => _currentFilePath;

        // Configuration cible
        private const int TARGET_WIDTH = 640;
        private const int TARGET_HEIGHT = 480;

        /// <summary>
        /// Initialiser FFmpeg et charger un fichier vidéo
        /// </summary>
        public async Task<bool> LoadVideoFileAsync(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    LogEvent?.Invoke($"[FFmpegDecoder] ❌ File not found: {filePath}");
                    return false;
                }

                // Fermer le fichier précédent si ouvert
                await CloseVideoAsync();

                LogEvent?.Invoke($"[FFmpegDecoder] 🎬 Loading video file: {Path.GetFileName(filePath)}");

                // Initialiser FFMediaToolkit si pas encore fait
                if (!_isInitialized)
                {
                    // FFMediaToolkit utilise automatiquement FFmpeg
                    LogEvent?.Invoke($"[FFmpegDecoder] 🔧 Initializing FFMediaToolkit...");
                    _isInitialized = true;
                }

                // Ouvrir le fichier vidéo
                _mediaFile = await Task.Run(() => MediaFile.Open(filePath));
                _currentFilePath = filePath;

                // Récupérer les informations du fichier
                if (_mediaFile.HasVideo)
                {
                    var videoStream = _mediaFile.Video;
                    _frameRate = videoStream.Info.AvgFrameRate;
                    _duration = videoStream.Info.Duration;
                    _totalFrames = (int)(_duration.TotalSeconds * _frameRate);

                    LogEvent?.Invoke($"[FFmpegDecoder] ✅ Video loaded successfully:");
                    LogEvent?.Invoke($"[FFmpegDecoder] 📊 Resolution: {videoStream.Info.FrameSize.Width}x{videoStream.Info.FrameSize.Height}");
                    LogEvent?.Invoke($"[FFmpegDecoder] 📊 Frame Rate: {_frameRate:F2} FPS");
                    LogEvent?.Invoke($"[FFmpegDecoder] 📊 Duration: {_duration:mm\\:ss\\.fff}");
                    LogEvent?.Invoke($"[FFmpegDecoder] 📊 Total Frames: {_totalFrames}");
                    LogEvent?.Invoke($"[FFmpegDecoder] 📊 Codec: {videoStream.Info.CodecName}");

                    return true;
                }
                else
                {
                    LogEvent?.Invoke($"[FFmpegDecoder] ❌ No video stream found in file");
                    await CloseVideoAsync();
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error loading video file: {ex.Message}");
                LogEvent?.Invoke($"[FFmpegDecoder] 💡 Make sure FFmpeg is properly installed and the file format is supported");
                await CloseVideoAsync();
                return false;
            }
        }

        /// <summary>
        /// Lire une frame spécifique du fichier vidéo
        /// </summary>
        public async Task<byte[]?> ReadFrameAsync(int frameNumber)
        {
            try
            {
                if (_mediaFile?.HasVideo != true)
                {
                    LogEvent?.Invoke($"[FFmpegDecoder] ⚠️ No video file loaded");
                    return null;
                }

                // Calculer le timestamp de la frame
                var timeStamp = TimeSpan.FromSeconds(frameNumber / _frameRate);

                // S'assurer qu'on ne dépasse pas la durée
                if (timeStamp >= _duration)
                {
                    // Revenir au début (loop)
                    timeStamp = TimeSpan.FromSeconds((frameNumber % _totalFrames) / _frameRate);
                }

                // Lire la frame à ce timestamp de manière synchrone
                var frameResult = await Task.Run(() =>
                {
                    try
                    {
                        if (_mediaFile.Video.TryGetFrame(timeStamp, out var videoFrame))
                        {
                            // Convertir immédiatement dans le thread de lecture
                            return ConvertFrameToRGB24(videoFrame, TARGET_WIDTH, TARGET_HEIGHT);
                        }
                        return null;
                    }
                    catch (Exception frameEx)
                    {
                        LogEvent?.Invoke($"[FFmpegDecoder] ⚠️ Frame read error for frame {frameNumber}: {frameEx.Message}");
                        return null;
                    }
                });

                if (frameResult != null)
                {
                    return frameResult;
                }
                else
                {
                    LogEvent?.Invoke($"[FFmpegDecoder] ⚠️ Could not read frame {frameNumber} at {timeStamp:mm\\:ss\\.fff}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error reading frame {frameNumber}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Convertir une frame FFMediaToolkit en RGB24 raw bytes
        /// </summary>
        private byte[] ConvertFrameToRGB24(ImageData frame, int targetWidth, int targetHeight)
        {
            try
            {
                // Extraire les données de l'ImageData FFMediaToolkit
                var frameData = frame.Data;
                var frameWidth = frame.ImageSize.Width;
                var frameHeight = frame.ImageSize.Height;

                // Créer le buffer de sortie RGB24
                var rgbBytes = new byte[targetWidth * targetHeight * 3];

                // Calculer les ratios de redimensionnement
                double scaleX = (double)frameWidth / targetWidth;
                double scaleY = (double)frameHeight / targetHeight;

                // Remplir le buffer RGB24 en redimensionnant
                for (int y = 0; y < targetHeight; y++)
                {
                    for (int x = 0; x < targetWidth; x++)
                    {
                        // Calculer les coordonnées source
                        int srcX = Math.Min((int)(x * scaleX), frameWidth - 1);
                        int srcY = Math.Min((int)(y * scaleY), frameHeight - 1);

                        int dstIndex = (y * targetWidth + x) * 3;
                        int srcIndex = (srcY * frameWidth + srcX) * 3;

                        // Les données FFMediaToolkit sont en RGB, copier directement
                        if (srcIndex + 2 < frameData.Length && dstIndex + 2 < rgbBytes.Length)
                        {
                            rgbBytes[dstIndex] = frameData[srcIndex];       // R
                            rgbBytes[dstIndex + 1] = frameData[srcIndex + 1]; // G
                            rgbBytes[dstIndex + 2] = frameData[srcIndex + 2]; // B
                        }
                    }
                }

                return rgbBytes;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error converting frame to RGB24: {ex.Message}");
                return new byte[targetWidth * targetHeight * 3]; // Retourner frame noire en cas d'erreur
            }
        }

        /// <summary>
        /// Lire frame à un timestamp spécifique
        /// </summary>
        public async Task<byte[]?> ReadFrameAtTimeAsync(TimeSpan timestamp)
        {
            try
            {
                if (_mediaFile?.HasVideo != true)
                {
                    return null;
                }

                var frameNumber = (int)(timestamp.TotalSeconds * _frameRate);
                return await ReadFrameAsync(frameNumber);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error reading frame at {timestamp:mm\\:ss\\.fff}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fermer le fichier vidéo actuel
        /// </summary>
        public async Task CloseVideoAsync()
        {
            try
            {
                if (_mediaFile != null)
                {
                    await Task.Run(() => _mediaFile.Dispose());
                    _mediaFile = null;
                    LogEvent?.Invoke($"[FFmpegDecoder] 📴 Video file closed");
                }

                _currentFilePath = null;
                _totalFrames = 0;
                _frameRate = 30.0;
                _duration = TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error closing video: {ex.Message}");
            }
        }

        /// <summary>
        /// Obtenir les informations du fichier vidéo
        /// </summary>
        public string GetVideoInfo()
        {
            if (_mediaFile?.HasVideo == true)
            {
                var video = _mediaFile.Video.Info;
                return $"Resolution: {video.FrameSize.Width}x{video.FrameSize.Height}, " +
                       $"FPS: {_frameRate:F2}, Duration: {_duration:mm\\:ss}, " +
                       $"Codec: {video.CodecName}, Frames: {_totalFrames}";
            }
            return "No video loaded";
        }

        public void Dispose()
        {
            try
            {
                CloseVideoAsync().Wait(2000);
                LogEvent?.Invoke($"[FFmpegDecoder] 🗑️ FFmpeg decoder disposed");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error during dispose: {ex.Message}");
            }
        }
    }
}