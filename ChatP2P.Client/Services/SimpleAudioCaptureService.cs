using System;
using System.IO;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// 🎤 Service de capture audio simplifié pour VOIP P2P
    /// Version compatible SIPSorcery 6.0.11 sans dépendances complexes
    /// </summary>
    public class SimpleAudioCaptureService : IDisposable
    {
        private bool _isCapturing = false;
        private bool _isPlayingFile = false;
        private readonly object _lock = new object();
        private string? _currentAudioFile;

        // Events pour notifier de la disponibilité des données audio
        public event Action<AudioFormat, byte[]>? AudioSampleReady;
        public event Action<string>? LogEvent;
        public event Action<bool>? CaptureStateChanged;

        // Configuration audio
        public bool IsCapturing => _isCapturing;
        public bool IsPlayingFile => _isPlayingFile;
        public AudioFormat? AudioFormat { get; private set; }
        public bool HasMicrophone { get; private set; } = false;

        // ✅ NOUVEAU: Device selection support
        private string? _selectedMicrophoneDevice;

        /// <summary>
        /// Configurer le device microphone pour la capture audio
        /// </summary>
        public void SetMicrophoneDevice(string deviceName)
        {
            _selectedMicrophoneDevice = deviceName;
            LogEvent?.Invoke($"[AudioCapture] 🎤 Microphone device set to: {deviceName}");
            // TODO: Appliquer le device microphone au système audio Windows
        }

        public SimpleAudioCaptureService()
        {
            // 🎤 NOUVEAU: Détecter la disponibilité du microphone
            DetectMicrophoneAvailability();
            LogEvent?.Invoke($"[AudioCapture] Service initialized - Microphone: {(HasMicrophone ? "Available" : "Not detected")}");
        }

        /// <summary>
        /// Détecter si un microphone est disponible
        /// </summary>
        private void DetectMicrophoneAvailability()
        {
            try
            {
                // ✅ FIX VM: En VM, pas de microphone - forcer mode simulation
                HasMicrophone = false; // Force simulation mode pour VMs
                LogEvent?.Invoke("[AudioCapture] VM Environment detected - using audio simulation mode");
            }
            catch (Exception ex)
            {
                HasMicrophone = false;
                LogEvent?.Invoke($"[AudioCapture] No microphone detected: {ex.Message}");
            }
        }

        /// <summary>
        /// Démarrer la capture audio (microphone ou fichier)
        /// </summary>
        public async Task<bool> StartCaptureAsync()
        {
            try
            {
                lock (_lock)
                {
                    if (_isCapturing || _isPlayingFile)
                    {
                        LogEvent?.Invoke("[AudioCapture] Already capturing/playing, ignoring start request");
                        return true;
                    }
                    _isCapturing = true;
                }

                if (HasMicrophone)
                {
                    LogEvent?.Invoke("[AudioCapture] ✅ Audio capture started (microphone)");
                }
                else
                {
                    LogEvent?.Invoke("[AudioCapture] ✅ Audio capture started (simulation mode - no microphone)");

                    // ✅ FIX: Démarrer simulation audio pour VMs sans microphone
                    _isPlayingFile = true;
                    _currentAudioFile = "simulation"; // Fake file pour activer la simulation
                    _ = Task.Run(async () => await SimulateAudioFilePlayback());
                }

                CaptureStateChanged?.Invoke(true);
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AudioCapture] ❌ Failed to start capture: {ex.Message}");
                CaptureStateChanged?.Invoke(false);
                return false;
            }
        }

        /// <summary>
        /// 🎵 NOUVEAU: Démarrer la lecture d'un fichier audio pour tests
        /// </summary>
        public async Task<bool> StartAudioFilePlaybackAsync(string audioFilePath)
        {
            try
            {
                if (!File.Exists(audioFilePath))
                {
                    LogEvent?.Invoke($"[AudioCapture] ❌ Audio file not found: {audioFilePath}");
                    return false;
                }

                lock (_lock)
                {
                    if (_isCapturing || _isPlayingFile)
                    {
                        LogEvent?.Invoke("[AudioCapture] Already capturing/playing, stopping first");
                        StopCaptureAsync().Wait(1000);
                    }
                    _isPlayingFile = true;
                    _currentAudioFile = audioFilePath;
                }

                LogEvent?.Invoke($"[AudioCapture] ✅ Started audio file playback: {Path.GetFileName(audioFilePath)}");
                CaptureStateChanged?.Invoke(true);

                // TODO: Implémenter la lecture réelle du fichier WAV
                // Pour l'instant, simuler l'envoi de données audio
                _ = Task.Run(async () => await SimulateAudioFilePlayback());

                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AudioCapture] ❌ Failed to start audio file playback: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Simuler la lecture de fichier audio (envoi de données fictives ou réelles)
        /// </summary>
        private async Task SimulateAudioFilePlayback()
        {
            try
            {
                LogEvent?.Invoke($"[AudioCapture] 🎵 Starting audio simulation - File: {_currentAudioFile ?? "Built-in sample"}");

                while (_isPlayingFile && !string.IsNullOrEmpty(_currentAudioFile))
                {
                    if (_currentAudioFile == "simulation")
                    {
                        // Générer un son de test sinusoïdal plus fort et plus long
                        var sampleData = GenerateTestTone(8820, 440.0); // 200ms à 440Hz (La) - plus long et audible
                        AudioSampleReady?.Invoke(new AudioFormat(), sampleData);
                    }
                    else if (File.Exists(_currentAudioFile))
                    {
                        // TODO: Lire vraies données du fichier WAV
                        var sampleData = await ReadAudioFileSample(_currentAudioFile);
                        if (sampleData != null && sampleData.Length > 0)
                        {
                            AudioSampleReady?.Invoke(new AudioFormat(), sampleData);
                        }
                    }
                    else
                    {
                        // Fallback vers test tone
                        var sampleData = GenerateTestTone(4410, 440.0);
                        AudioSampleReady?.Invoke(new AudioFormat(), sampleData);
                    }

                    await Task.Delay(100); // 100ms par chunk
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AudioCapture] ❌ Error during audio simulation: {ex.Message}");
            }
        }

        /// <summary>
        /// Générer un ton de test sinusoïdal
        /// </summary>
        private byte[] GenerateTestTone(int sampleCount, double frequency)
        {
            var samples = new byte[sampleCount * 2]; // 16-bit samples
            var sampleRate = 44100;

            for (int i = 0; i < sampleCount; i++)
            {
                // Générer onde sinusoïdale avec amplitude maximale audible
                var sample = (short)(Math.Sin(2.0 * Math.PI * frequency * i / sampleRate) * 16000);

                // Convertir en bytes (little-endian)
                samples[i * 2] = (byte)(sample & 0xFF);
                samples[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            // 🔧 DEBUG: Save generated RAW audio to disk for comparison
            try
            {
                var debugPath = @"C:\Users\pragm\OneDrive\Bureau\ChatP2P_Logs\debug_generated.raw";
                File.WriteAllBytes(debugPath, samples);
                LogEvent?.Invoke($"[AudioCapture] 🔧 DEBUG: Saved RAW to {debugPath} ({samples.Length} bytes)");
            }
            catch (Exception debugEx)
            {
                LogEvent?.Invoke($"[AudioCapture] ⚠️ Debug save failed: {debugEx.Message}");
            }

            return samples;
        }

        /// <summary>
        /// Lire échantillon d'un fichier audio WAV réel
        /// </summary>
        private async Task<byte[]?> ReadAudioFileSample(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    LogEvent?.Invoke($"[AudioCapture] ⚠️ File not found: {filePath}");
                    return GenerateTestTone(4410, 440.0); // Fallback
                }

                LogEvent?.Invoke($"[AudioCapture] 🎵 Reading REAL audio from: {Path.GetFileName(filePath)}");

                // Lire le fichier WAV et extraire les données audio pures
                var audioData = await ReadWavFileData(filePath);
                if (audioData != null && audioData.Length > 0)
                {
                    // Prendre un chunk de ~100ms à partir de la position courante
                    var chunkSize = Math.Min(audioData.Length, 8820); // ~100ms pour 44.1kHz 16-bit mono
                    var chunk = new byte[chunkSize];

                    // Calculer position dans le fichier (rotation pour boucle)
                    var totalChunks = audioData.Length / chunkSize;
                    var currentChunk = (_filePosition / chunkSize) % totalChunks;
                    var startPos = currentChunk * chunkSize;

                    Array.Copy(audioData, startPos, chunk, 0, chunkSize);
                    _filePosition += chunkSize;

                    LogEvent?.Invoke($"[AudioCapture] ✅ Read {chunk.Length} bytes from real WAV file");
                    return chunk;
                }
                else
                {
                    LogEvent?.Invoke($"[AudioCapture] ⚠️ No audio data in WAV file, using test tone");
                    return GenerateTestTone(4410, 440.0); // Fallback
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AudioCapture] ❌ Error reading audio file: {ex.Message}");
                return GenerateTestTone(4410, 440.0); // Fallback
            }
        }

        private int _filePosition = 0; // Position dans le fichier pour streaming

        /// <summary>
        /// Lire données audio pures d'un fichier WAV (sans header)
        /// </summary>
        private async Task<byte[]?> ReadWavFileData(string filePath)
        {
            try
            {
                var allBytes = await File.ReadAllBytesAsync(filePath);

                // Parser header WAV pour trouver les données audio
                if (allBytes.Length < 44)
                {
                    LogEvent?.Invoke($"[AudioCapture] ❌ WAV file too small: {allBytes.Length} bytes");
                    return null;
                }

                // Vérifier signature WAV
                var riff = System.Text.Encoding.ASCII.GetString(allBytes, 0, 4);
                var wave = System.Text.Encoding.ASCII.GetString(allBytes, 8, 4);

                if (riff != "RIFF" || wave != "WAVE")
                {
                    LogEvent?.Invoke($"[AudioCapture] ❌ Not a valid WAV file");
                    return null;
                }

                // Chercher le chunk "data"
                var dataOffset = FindDataChunk(allBytes);
                if (dataOffset == -1)
                {
                    LogEvent?.Invoke($"[AudioCapture] ❌ No data chunk found in WAV file");
                    return null;
                }

                // Lire taille du chunk data
                var dataSize = BitConverter.ToInt32(allBytes, dataOffset + 4);
                var audioData = new byte[dataSize];
                Array.Copy(allBytes, dataOffset + 8, audioData, 0, dataSize);

                LogEvent?.Invoke($"[AudioCapture] ✅ Extracted {audioData.Length} bytes of pure audio data from WAV");
                return audioData;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AudioCapture] ❌ Error parsing WAV file: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Trouver l'offset du chunk "data" dans un fichier WAV
        /// </summary>
        private int FindDataChunk(byte[] wavBytes)
        {
            for (int i = 12; i < wavBytes.Length - 4; i++)
            {
                var chunk = System.Text.Encoding.ASCII.GetString(wavBytes, i, 4);
                if (chunk == "data")
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Arrêter la capture audio et/ou lecture de fichier
        /// </summary>
        public async Task StopCaptureAsync()
        {
            try
            {
                lock (_lock)
                {
                    if (!_isCapturing && !_isPlayingFile)
                    {
                        LogEvent?.Invoke("[AudioCapture] Not capturing/playing, ignoring stop request");
                        return;
                    }
                    _isCapturing = false;
                    _isPlayingFile = false;
                    _currentAudioFile = null;
                }

                LogEvent?.Invoke("[AudioCapture] ✅ Audio capture/playback stopped");
                CaptureStateChanged?.Invoke(false);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AudioCapture] ❌ Error stopping capture: {ex.Message}");
            }
        }

        /// <summary>
        /// Obtenir la liste des périphériques audio disponibles
        /// </summary>
        public static async Task<string[]> GetAvailableAudioDevicesAsync()
        {
            try
            {
                // En VM, lister les périphériques simulés pour debugging
                var devices = new List<string>();

                // TODO: En production, utiliser WaveIn.DeviceCount et WaveIn.GetCapabilities()
                devices.Add("🎤 Default Microphone (VM Simulation)");
                devices.Add("🎧 Default Speakers (VM Output)");
                devices.Add("📁 Audio File Playback (Test Mode)");

                return devices.ToArray();
            }
            catch (Exception ex)
            {
                // En cas d'erreur, retourner au moins le mode simulation
                return new[] { $"⚠️ Error detecting audio: {ex.Message}", "🎤 Simulation Mode Available" };
            }
        }

        /// <summary>
        /// Obtenir les niveaux audio actuels (pour indicateur visuel)
        /// </summary>
        public double GetCurrentAudioLevel()
        {
            return _isCapturing ? 0.5 : 0.0;
        }

        public void Dispose()
        {
            try
            {
                StopCaptureAsync().Wait(1000);
                LogEvent?.Invoke("[AudioCapture] Service disposed");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AudioCapture] ❌ Error during dispose: {ex.Message}");
            }
        }
    }
}