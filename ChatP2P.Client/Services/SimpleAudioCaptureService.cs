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
                // Simulation de détection - en production, utiliser WaveIn.DeviceCount ou équivalent
                HasMicrophone = true; // Pour l'instant, on suppose qu'il y en a un
                LogEvent?.Invoke("[AudioCapture] Microphone detection completed");
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
        /// Simuler la lecture de fichier audio (envoi de données fictives)
        /// </summary>
        private async Task SimulateAudioFilePlayback()
        {
            try
            {
                while (_isPlayingFile && !string.IsNullOrEmpty(_currentAudioFile))
                {
                    // Simuler des échantillons audio (44.1kHz, 16-bit, mono)
                    var sampleData = new byte[4410]; // 100ms d'audio à 44.1kHz
                    new Random().NextBytes(sampleData); // Données aléatoires pour test

                    AudioSampleReady?.Invoke(new AudioFormat(), sampleData);

                    await Task.Delay(100); // 100ms par chunk
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AudioCapture] ❌ Error during file playback simulation: {ex.Message}");
            }
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
                return new[] { "Default Microphone (Simulated)" };
            }
            catch (Exception)
            {
                return Array.Empty<string>();
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