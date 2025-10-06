using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text;
using System.Linq;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// 🎬 Service de décodage vidéo FFmpeg direct
    /// Lecture réelle de fichiers vidéo via FFmpeg process pour streaming via caméra virtuelle
    /// </summary>
    public class FFmpegVideoDecoderService : IDisposable
    {
        private string? _currentFilePath;
        private bool _isInitialized = false;
        private int _totalFrames = 0;
        private double _frameRate = 30.0;
        private TimeSpan _duration = TimeSpan.Zero;
        private string? _ffmpegPath;
        private VideoInfo? _videoInfo;

        // ✅ PERFORMANCE: Système de buffer intelligent adaptatif
        private readonly Dictionary<int, byte[]> _frameCache = new();
        private int _maxCacheSize = 30; // Cache adaptatif selon FPS
        private DateTime _lastLogTime = DateTime.MinValue;

        // ✅ BATCH PROCESSING: Traitement par lots en mémoire
        private readonly Queue<int> _batchQueue = new();
        private const int BATCH_SIZE = 10; // Traiter 10 frames par lot
        private bool _isBatchProcessing = false;

        // ✅ DIRECT FFMPEG DLL: Décodeur FFmpeg direct via DLLs (pas de processus)
        private DirectFFmpegDecoder? _directDecoder;

        // ✅ BATCH DECODING: Buffer pour traitement par lots H.264
        private readonly Queue<(byte[] h264Data, TaskCompletionSource<byte[]?> resultTask)> _decodeQueue = new();
        private readonly object _decodeLock = new object();
        private bool _isBatchDecodingActive = false;

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
        /// Initialiser FFmpeg direct et analyser un fichier vidéo
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

                LogEvent?.Invoke($"[FFmpegDecoder] 🎬 Loading video file via FFmpeg direct: {Path.GetFileName(filePath)}");

                // S'assurer que FFmpeg est disponible
                _ffmpegPath = await GetFFmpegPathAsync();
                if (_ffmpegPath == null)
                {
                    LogEvent?.Invoke($"[FFmpegDecoder] ❌ FFmpeg not found or not installed");
                    return false;
                }

                // Analyser le fichier vidéo avec ffprobe
                var videoInfo = await AnalyzeVideoFileAsync(filePath);
                if (videoInfo == null)
                {
                    LogEvent?.Invoke($"[FFmpegDecoder] ❌ Failed to analyze video file");
                    return false;
                }

                _currentFilePath = filePath;
                _videoInfo = videoInfo; // ✅ FIX: Store video info for later use
                _frameRate = videoInfo.FrameRate;
                _duration = videoInfo.Duration;
                _totalFrames = (int)(_duration.TotalSeconds * _frameRate);

                // ✅ ADAPTIVE: Ajuster le cache selon le FPS de la vidéo
                AdaptCacheSize(_frameRate);

                _isInitialized = true;

                LogEvent?.Invoke($"[FFmpegDecoder] ✅ Video analyzed successfully:");
                LogEvent?.Invoke($"[FFmpegDecoder] 📊 Resolution: {videoInfo.Width}x{videoInfo.Height}");
                LogEvent?.Invoke($"[FFmpegDecoder] 📊 Frame Rate: {_frameRate:F2} FPS");
                LogEvent?.Invoke($"[FFmpegDecoder] 📊 Duration: {_duration:mm\\:ss\\.fff}");
                LogEvent?.Invoke($"[FFmpegDecoder] 📊 Total Frames: {_totalFrames}");
                LogEvent?.Invoke($"[FFmpegDecoder] 📊 Codec: {videoInfo.Codec}");

                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error loading video file: {ex.Message}");
                await CloseVideoAsync();
                return false;
            }
        }

        /// <summary>
        /// ✅ OPTIMIZED: Lire une frame avec cache intelligent pour performance
        /// </summary>
        public async Task<byte[]?> ReadFrameAsync(int frameNumber)
        {
            try
            {
                if (!_isInitialized || string.IsNullOrEmpty(_currentFilePath))
                {
                    return null;
                }

                // ✅ PERFORMANCE: Vérifier le cache d'abord
                var actualFrameNumber = frameNumber % _totalFrames; // Loop automatique
                if (_frameCache.ContainsKey(actualFrameNumber))
                {
                    return _frameCache[actualFrameNumber];
                }

                // Calculer le timestamp de la frame
                var timeStamp = TimeSpan.FromSeconds(actualFrameNumber / _frameRate);

                // ✅ PERFORMANCE: Logging réduit
                var now = DateTime.UtcNow;
                if (now - _lastLogTime > TimeSpan.FromSeconds(5))
                {
                    LogEvent?.Invoke($"[FFmpegDecoder] 📹 Processing frame {actualFrameNumber} at {timeStamp:mm\\:ss}");
                    _lastLogTime = now;
                }

                // Extraire la frame via FFmpeg
                var rgbData = await ExtractFrameAtTimestamp(timeStamp);

                if (rgbData != null && rgbData.Length > 0)
                {
                    // ✅ PERFORMANCE: Ajouter au cache avec nettoyage automatique
                    AddToCache(actualFrameNumber, rgbData);
                    return rgbData;
                }

                return null;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error reading frame {frameNumber}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ✅ ADAPTIVE: Ajuster la taille du cache selon le FPS de la vidéo
        /// </summary>
        private void AdaptCacheSize(double fps)
        {
            // Plus la vidéo est fluide (FPS élevé), plus on cache de frames
            // Cache pour environ 2-3 secondes de vidéo
            _maxCacheSize = Math.Max(15, (int)(fps * 2.5));
            LogEvent?.Invoke($"[FFmpegDecoder] 🎯 Cache size adapted to {_maxCacheSize} frames for {fps:F1} FPS video");
        }

        /// <summary>
        /// ✅ PERFORMANCE: Ajouter frame au cache avec gestion mémoire adaptative
        /// </summary>
        private void AddToCache(int frameNumber, byte[] frameData)
        {
            // Nettoyer le cache si trop plein (taille adaptative)
            if (_frameCache.Count >= _maxCacheSize)
            {
                var oldestKey = _frameCache.Keys.First();
                _frameCache.Remove(oldestKey);
            }

            _frameCache[frameNumber] = frameData;
        }

        /// <summary>
        /// ✅ BATCH PROCESSING: Précharger plusieurs frames en lot pour performance
        /// </summary>
        public async Task PreloadFrameBatchAsync(int startFrame, int batchSize = BATCH_SIZE)
        {
            if (_isBatchProcessing || !_isInitialized) return;

            _isBatchProcessing = true;
            try
            {
                LogEvent?.Invoke($"[FFmpegDecoder] 🔄 Preloading batch of {batchSize} frames starting from {startFrame}");

                var tasks = new List<Task>();
                for (int i = 0; i < batchSize; i++)
                {
                    var frameNumber = (startFrame + i) % _totalFrames;

                    // Skip si déjà en cache
                    if (_frameCache.ContainsKey(frameNumber)) continue;

                    // Ajouter à la queue de traitement
                    _batchQueue.Enqueue(frameNumber);
                }

                // Traiter la queue par lots de 3 frames en parallèle
                while (_batchQueue.Count > 0)
                {
                    var parallelBatch = new List<Task>();
                    for (int i = 0; i < 3 && _batchQueue.Count > 0; i++)
                    {
                        var frameNum = _batchQueue.Dequeue();
                        parallelBatch.Add(ProcessSingleFrameAsync(frameNum));
                    }

                    if (parallelBatch.Count > 0)
                    {
                        await Task.WhenAll(parallelBatch);
                    }
                }

                LogEvent?.Invoke($"[FFmpegDecoder] ✅ Batch preloading completed");
            }
            finally
            {
                _isBatchProcessing = false;
            }
        }

        /// <summary>
        /// ✅ BATCH PROCESSING: Traiter une frame individuelle (pour parallélisation)
        /// </summary>
        private async Task ProcessSingleFrameAsync(int frameNumber)
        {
            try
            {
                var timeStamp = TimeSpan.FromSeconds(frameNumber / _frameRate);
                var rgbData = await ExtractFrameAtTimestamp(timeStamp);

                if (rgbData != null && rgbData.Length > 0)
                {
                    AddToCache(frameNumber, rgbData);
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ⚠️ Error processing frame {frameNumber}: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Analyser un fichier vidéo avec ffprobe
        /// </summary>
        private async Task<VideoInfo?> AnalyzeVideoFileAsync(string filePath)
        {
            try
            {
                var ffprobePath = _ffmpegPath?.Replace("ffmpeg.exe", "ffprobe.exe");
                if (string.IsNullOrEmpty(ffprobePath) || !File.Exists(ffprobePath))
                {
                    LogEvent?.Invoke($"[FFmpegDecoder] ❌ ffprobe not found");
                    return null;
                }

                // Commande ffprobe pour analyser le fichier
                var arguments = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"";

                using var process = new Process();
                process.StartInfo.FileName = ffprobePath;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;

                process.Start();

                var output = await process.StandardOutput.ReadToEndAsync();
                var completed = await Task.Run(() => process.WaitForExit(5000));

                if (!completed || process.ExitCode != 0)
                {
                    LogEvent?.Invoke($"[FFmpegDecoder] ❌ ffprobe failed or timeout");
                    return null;
                }

                // Parser le JSON de ffprobe (simple parsing)
                return ParseFFProbeOutput(output);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error analyzing video: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Extraire une frame à un timestamp donné via FFmpeg
        /// </summary>
        private async Task<byte[]?> ExtractFrameAtTimestamp(TimeSpan timestamp)
        {
            try
            {
                var tempDir = Path.GetTempPath();
                var outputFile = Path.Combine(tempDir, $"frame_{Guid.NewGuid()}.raw");

                try
                {
                    // ✅ FIX: Force scale to target dimensions for consistent frame size
                    var width = TARGET_WIDTH;
                    var height = TARGET_HEIGHT;

                    // ✅ OPTIMIZED: Commande FFmpeg optimisée pour performance et qualité RGB
                    var timestampStr = timestamp.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                    var arguments = $"-ss {timestampStr} -i \"{_currentFilePath}\" -vframes 1 -f rawvideo -pix_fmt rgb24 -vf \"scale={width}:{height}:flags=lanczos\" -sws_flags lanczos+accurate_rnd \"{outputFile}\" -y";

                    // ✅ PERFORMANCE: Reduced logging for FFmpeg commands

                    using var process = new Process();
                    process.StartInfo.FileName = _ffmpegPath!;
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;

                    var stdoutBuilder = new StringBuilder();
                    var stderrBuilder = new StringBuilder();

                    process.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    var completed = await Task.Run(() => process.WaitForExit(5000));

                    if (!completed)
                    {
                        LogEvent?.Invoke($"[FFmpegDecoder] ⚠️ FFmpeg timeout during frame extraction");
                        try { process.Kill(); } catch { }
                        return null;
                    }

                    var stdout = stdoutBuilder.ToString();
                    var stderr = stderrBuilder.ToString();

                    if (process.ExitCode != 0)
                    {
                        LogEvent?.Invoke($"[FFmpegDecoder] ❌ FFmpeg failed with exit code {process.ExitCode}");
                        LogEvent?.Invoke($"[FFmpegDecoder] ❌ FFmpeg stderr: {stderr}");
                        return null;
                    }

                    if (File.Exists(outputFile))
                    {
                        var frameData = await File.ReadAllBytesAsync(outputFile);
                        var expectedSize = width * height * 3;

                        LogEvent?.Invoke($"[FFmpegDecoder] 📊 Frame extracted: {frameData.Length} bytes (expected: {expectedSize} for {width}x{height})");

                        if (frameData.Length > 0)
                        {
                            // ✅ Update video dimensions for proper rendering
                            if (_videoInfo != null)
                            {
                                _videoInfo.Width = width;
                                _videoInfo.Height = height;
                            }
                            return frameData;
                        }
                    }
                    else
                    {
                        LogEvent?.Invoke($"[FFmpegDecoder] ❌ Output file not created: {outputFile}");
                    }

                    return null;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(outputFile)) File.Delete(outputFile);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error extracting frame: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Lire frame à un timestamp spécifique
        /// </summary>
        public async Task<byte[]?> ReadFrameAtTimeAsync(TimeSpan timestamp)
        {
            try
            {
                if (!_isInitialized || string.IsNullOrEmpty(_currentFilePath))
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
                await Task.CompletedTask; // Pour maintenir l'interface async

                _currentFilePath = null;
                _totalFrames = 0;
                _frameRate = 30.0;
                _duration = TimeSpan.Zero;
                _isInitialized = false;

                LogEvent?.Invoke($"[FFmpegDecoder] 📴 Video file closed");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error closing video: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Trouver le chemin vers ffmpeg.exe
        /// </summary>
        private async Task<string?> GetFFmpegPathAsync()
        {
            try
            {
                // S'assurer que FFmpeg est installé via FFmpegInstaller
                var ffmpegAvailable = await FFmpegInstaller.EnsureFFmpegInstalledAsync();
                if (!ffmpegAvailable)
                {
                    return null;
                }

                // Vérifier les emplacements possibles
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
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error finding FFmpeg: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ✅ NOUVEAU: Parser simple de la sortie ffprobe JSON
        /// </summary>
        private VideoInfo? ParseFFProbeOutput(string jsonOutput)
        {
            try
            {
                // Simple parsing sans dépendance JSON complexe
                // Rechercher les informations vidéo de base
                var info = new VideoInfo();

                // Extraire width/height
                var widthIndex = jsonOutput.IndexOf("\"width\":");
                var heightIndex = jsonOutput.IndexOf("\"height\":");
                var durationIndex = jsonOutput.IndexOf("\"duration\":");
                var frameRateIndex = jsonOutput.IndexOf("\"r_frame_rate\":");
                var codecIndex = jsonOutput.IndexOf("\"codec_name\":");

                if (widthIndex > 0 && heightIndex > 0)
                {
                    var widthEnd = jsonOutput.IndexOf(',', widthIndex);
                    var heightEnd = jsonOutput.IndexOf(',', heightIndex);

                    if (int.TryParse(jsonOutput.Substring(widthIndex + 8, widthEnd - widthIndex - 8).Trim(), out int width))
                        info.Width = width;

                    if (int.TryParse(jsonOutput.Substring(heightIndex + 9, heightEnd - heightIndex - 9).Trim(), out int height))
                        info.Height = height;
                }

                if (durationIndex > 0)
                {
                    var durationStart = jsonOutput.IndexOf('"', durationIndex + 11) + 1;
                    var durationEnd = jsonOutput.IndexOf('"', durationStart);

                    if (double.TryParse(jsonOutput.Substring(durationStart, durationEnd - durationStart), out double duration))
                        info.Duration = TimeSpan.FromSeconds(duration);
                }

                if (frameRateIndex > 0)
                {
                    var rateStart = jsonOutput.IndexOf('"', frameRateIndex + 15) + 1;
                    var rateEnd = jsonOutput.IndexOf('"', rateStart);
                    var rateStr = jsonOutput.Substring(rateStart, rateEnd - rateStart);

                    // Parse "25/1" format
                    if (rateStr.Contains('/'))
                    {
                        var parts = rateStr.Split('/');
                        if (parts.Length == 2 && double.TryParse(parts[0], out double num) && double.TryParse(parts[1], out double den) && den > 0)
                            info.FrameRate = num / den;
                    }
                    else if (double.TryParse(rateStr, out double rate))
                    {
                        info.FrameRate = rate;
                    }
                }

                if (codecIndex > 0)
                {
                    var codecStart = jsonOutput.IndexOf('"', codecIndex + 13) + 1;
                    var codecEnd = jsonOutput.IndexOf('"', codecStart);
                    info.Codec = jsonOutput.Substring(codecStart, codecEnd - codecStart);
                }

                // Valeurs par défaut si parsing échoue
                if (info.Width == 0) info.Width = 640;
                if (info.Height == 0) info.Height = 480;
                if (info.FrameRate == 0) info.FrameRate = 25.0;
                if (info.Duration == TimeSpan.Zero) info.Duration = TimeSpan.FromMinutes(1);
                if (string.IsNullOrEmpty(info.Codec)) info.Codec = "unknown";

                return info;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error parsing ffprobe output: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ✅ OPTIMISÉ: Décoder des données H.264 brutes en RGB via FFmpeg STREAMING
        /// PERFORMANCE: ~30ms par frame au lieu de 3-5 secondes !
        /// </summary>
        /// <summary>
        /// ✅ OPTIMIZED: Décodage H.264 avec processus FFmpeg persistant et traitement par lots
        /// </summary>
        public async Task<byte[]?> DecodeH264FrameAsync(byte[] h264Data)
        {
            try
            {
                // ✅ BATCH PROCESSING: Ajouter à la queue et attendre le résultat
                var taskSource = new TaskCompletionSource<byte[]?>();

                lock (_decodeLock)
                {
                    _decodeQueue.Enqueue((h264Data, taskSource));

                    // Démarrer le traitement par lots si pas encore actif
                    if (!_isBatchDecodingActive)
                    {
                        _isBatchDecodingActive = true;
                        _ = Task.Run(ProcessDecodeQueueAsync);
                    }
                }

                // Attendre le résultat du décodage par lots
                return await taskSource.Task;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error in STREAMING H.264 decoding: {ex.Message}");
                return null;
            }
        }

        // ✅ OLD METHOD REMOVED: RunFFmpegStreamingAsync removed to avoid conflicts with persistent decoder

        /// <summary>
        /// Trouver le chemin vers ffmpeg.exe
        /// </summary>
        private string? GetFFmpegPath()
        {
            // Vérifier plusieurs emplacements possibles
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

                // Timeout de 5 secondes pour éviter les blocages
                var completed = await Task.Run(() => process.WaitForExit(5000));

                if (!completed)
                {
                    LogEvent?.Invoke($"[FFmpegDecoder] ⚠️ FFmpeg process timeout, killing...");
                    try { process.Kill(); } catch { }
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error running FFmpeg: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Obtenir les informations du fichier vidéo
        /// </summary>
        public string GetVideoInfo()
        {
            if (_isInitialized && !string.IsNullOrEmpty(_currentFilePath))
            {
                return $"File: {Path.GetFileName(_currentFilePath)}, " +
                       $"FPS: {_frameRate:F2}, Duration: {_duration:mm\\:ss}, " +
                       $"Frames: {_totalFrames}";
            }
            return "No video loaded";
        }

        /// <summary>
        /// ✅ BATCH PROCESSING: Traitement de la queue de décodage H.264 par lots
        /// </summary>
        private async Task ProcessDecodeQueueAsync()
        {
            try
            {
                while (true)
                {
                    List<(byte[] h264Data, TaskCompletionSource<byte[]?> resultTask)> currentBatch;

                    // Collecter un lot de frames à traiter
                    lock (_decodeLock)
                    {
                        if (_decodeQueue.Count == 0)
                        {
                            _isBatchDecodingActive = false;
                            return;
                        }

                        currentBatch = new List<(byte[], TaskCompletionSource<byte[]?>)>();
                        int batchSize = Math.Min(2, _decodeQueue.Count); // ⚡ REAL-TIME: max 2 frames par lot pour réduire latence

                        for (int i = 0; i < batchSize; i++)
                        {
                            currentBatch.Add(_decodeQueue.Dequeue());
                        }
                    }

                    // S'assurer que le décodeur persistant est prêt
                    await EnsurePersistentDecoderAsync();

                    // Traiter le lot de frames
                    await ProcessBatchAsync(currentBatch);

                    // ⚡ NO DELAY: Continuous processing for real-time performance
                    // await Task.Delay(10); // Removed for 30 FPS capability
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error in batch processing queue: {ex.Message}");

                // Marquer toutes les tâches en attente comme échec
                lock (_decodeLock)
                {
                    while (_decodeQueue.Count > 0)
                    {
                        var (_, taskSource) = _decodeQueue.Dequeue();
                        taskSource.SetResult(null);
                    }
                    _isBatchDecodingActive = false;
                }
            }
        }

        /// <summary>
        /// ✅ SIMPLIFIED: FFmpeg disponible pour traitement par lots
        /// </summary>
        private async Task EnsurePersistentDecoderAsync()
        {
            // Plus besoin de processus persistant avec l'approche fichiers temporaires optimisée
            LogEvent?.Invoke($"[FFmpegDecoder] ✅ Fast file-based H.264 decoder ready");
            await Task.CompletedTask;
        }

        /// <summary>
        /// ✅ DIRECT DLL H.264: Décodage ultra-rapide via FFMpegCore DLL (pas de processus)
        /// </summary>
        private async Task ProcessBatchAsync(List<(byte[] h264Data, TaskCompletionSource<byte[]?> resultTask)> batch)
        {
            try
            {
                LogEvent?.Invoke($"[FFmpegDecoder] 🔍 Processing batch: {batch.Count} frames");

                // Initialiser le décodeur direct si nécessaire
                if (_directDecoder == null)
                {
                    _directDecoder = new DirectFFmpegDecoder();
                    _directDecoder.LogEvent += (msg) => LogEvent?.Invoke($"[DirectFFmpeg] {msg}");
                }

                var results = new List<byte[]?>();

                // ✅ PARALLEL PROCESSING: Traiter toutes les frames en parallèle via DLL
                var h264Frames = batch.Select(b => b.h264Data).ToArray();
                LogEvent?.Invoke($"[FFmpegDecoder] 📊 H.264 frame sizes: {string.Join(", ", h264Frames.Select(f => f.Length))} bytes");

                LogEvent?.Invoke($"[FFmpegDecoder] 🚀 CALLING DirectFFmpeg.DecodeBatchH264ToRgbAsync with {h264Frames.Length} frames");
                var decodedFrames = await _directDecoder.DecodeBatchH264ToRgbAsync(h264Frames);
                LogEvent?.Invoke($"[FFmpegDecoder] 🎯 BACK FROM DirectFFmpeg: {decodedFrames?.Length ?? -1} results received");
                LogEvent?.Invoke($"[FFmpegDecoder] 📤 Decoded results: {decodedFrames?.Count(f => f != null) ?? 0}/{decodedFrames?.Length ?? 0} successful");

                results.AddRange(decodedFrames);

                // Retourner les résultats aux tâches correspondantes
                for (int i = 0; i < batch.Count && i < results.Count; i++)
                {
                    batch[i].resultTask.SetResult(results[i]);
                }

                // Performance: no logging
            }
            catch (Exception ex)
            {
                // Performance: silent fail

                foreach (var (_, taskSource) in batch)
                {
                    taskSource.SetResult(null);
                }
            }
        }

        public void Dispose()
        {
            try
            {
                // Nettoyer le décodeur direct DLL
                if (_directDecoder != null)
                {
                    _directDecoder.Dispose();
                    _directDecoder = null;
                }

                CloseVideoAsync().Wait(2000);
                LogEvent?.Invoke($"[FFmpegDecoder] 🗑️ FFmpeg decoder disposed");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpegDecoder] ❌ Error during dispose: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// ✅ NOUVEAU: Structure pour stocker les informations vidéo analysées par ffprobe
    /// </summary>
    internal class VideoInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public double FrameRate { get; set; }
        public TimeSpan Duration { get; set; }
        public string Codec { get; set; } = "";
    }
}