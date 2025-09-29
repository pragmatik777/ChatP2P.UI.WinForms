using System;
using System.IO;
using System.Threading.Tasks;
using System.Media;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// 🔊 Service de playback audio simple pour VOIP relay
    /// Lecture des données audio reçues via relay ou P2P
    /// </summary>
    public class SimpleAudioPlaybackService : IDisposable
    {
        private bool _isPlaying = false;
        private readonly object _lock = new object();
        private SoundPlayer? _soundPlayer;

        // Events pour monitoring
        public event Action<string>? LogEvent;
        public event Action<bool>? PlaybackStateChanged;

        public bool IsPlaying => _isPlaying;
        public bool HasSpeakers { get; private set; } = true; // Assume speakers available

        public SimpleAudioPlaybackService()
        {
            LogEvent?.Invoke("[AudioPlayback] Service initialized");
        }

        /// <summary>
        /// Jouer des données audio reçues
        /// </summary>
        public async Task<bool> PlayAudioDataAsync(byte[] audioData, string fromPeer)
        {
            try
            {
                LogEvent?.Invoke($"[AudioPlayback] 🔊 Playing audio from {fromPeer}: {audioData.Length} bytes");

                // Convertir les données raw en format jouable
                var wavData = ConvertToWavFormat(audioData);

                // Jouer via SoundPlayer (méthode simple)
                using (var memoryStream = new MemoryStream(wavData))
                {
                    _soundPlayer = new SoundPlayer(memoryStream);

                    // ✅ FIX: Load synchronously to ensure WAV is ready before playing
                    _soundPlayer.Load();

                    lock (_lock)
                    {
                        _isPlaying = true;
                    }

                    PlaybackStateChanged?.Invoke(true);

                    // 🔊 FIX: Test system beep before WAV playback to verify audio subsystem
                    System.Console.Beep(440, 200); // System beep to confirm audio works
                    LogEvent?.Invoke($"[AudioPlayback] 🔊 System beep test played before WAV");

                    // Jouer maintenant que le fichier WAV est chargé
                    _soundPlayer.Play();

                    // Attendre la durée approximative du sample
                    var durationMs = EstimateAudioDuration(audioData);
                    await Task.Delay(durationMs);

                    lock (_lock)
                    {
                        _isPlaying = false;
                    }

                    PlaybackStateChanged?.Invoke(false);
                }

                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AudioPlayback] ❌ Error playing audio: {ex.Message}");

                lock (_lock)
                {
                    _isPlaying = false;
                }
                PlaybackStateChanged?.Invoke(false);
                return false;
            }
        }

        /// <summary>
        /// Convertir données audio raw en format WAV jouable
        /// </summary>
        private byte[] ConvertToWavFormat(byte[] rawAudioData)
        {
            try
            {
                // 🔧 FIX: Toujours traiter comme données RAW pour VOIP streaming
                // Car VM1 envoie des chunks audio purs sans header WAV complet
                LogEvent?.Invoke($"[AudioPlayback] 🔧 Converting raw audio chunk to WAV format ({rawAudioData.Length} bytes)");

                // Paramètres audio standards (correspond à SimpleAudioCaptureService)
                const int sampleRate = 44100;
                const short channels = 1; // Mono
                const short bitsPerSample = 16;

                var dataSize = rawAudioData.Length;
                var fileSize = 36 + dataSize;

                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    // Header WAV
                    writer.Write("RIFF".ToCharArray());
                    writer.Write(fileSize);
                    writer.Write("WAVE".ToCharArray());

                    // Format chunk
                    writer.Write("fmt ".ToCharArray());
                    writer.Write(16); // Chunk size
                    writer.Write((short)1); // Audio format (PCM)
                    writer.Write(channels);
                    writer.Write(sampleRate);
                    writer.Write(sampleRate * channels * bitsPerSample / 8); // Byte rate
                    writer.Write((short)(channels * bitsPerSample / 8)); // Block align
                    writer.Write(bitsPerSample);

                    // Data chunk
                    writer.Write("data".ToCharArray());
                    writer.Write(dataSize);
                    writer.Write(rawAudioData);

                    var wavData = stream.ToArray();

                    // 🔧 DEBUG: Save WAV to disk for analysis
                    try
                    {
                        var debugPath = @"C:\Users\pragm\OneDrive\Bureau\ChatP2P_Logs\debug_received.wav";
                        File.WriteAllBytes(debugPath, wavData);
                        LogEvent?.Invoke($"[AudioPlayback] 🔧 DEBUG: Saved WAV to {debugPath} ({wavData.Length} bytes)");
                    }
                    catch (Exception debugEx)
                    {
                        LogEvent?.Invoke($"[AudioPlayback] ⚠️ Debug save failed: {debugEx.Message}");
                    }

                    return wavData;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AudioPlayback] ❌ Error converting to WAV: {ex.Message}");
                return rawAudioData; // Fallback vers données brutes
            }
        }

        /// <summary>
        /// Vérifier si les données sont déjà au format WAV (avec header RIFF/WAVE)
        /// </summary>
        private bool IsAlreadyWavFormat(byte[] data)
        {
            if (data.Length < 12) return false;

            try
            {
                var riff = System.Text.Encoding.ASCII.GetString(data, 0, 4);
                var wave = System.Text.Encoding.ASCII.GetString(data, 8, 4);
                return riff == "RIFF" && wave == "WAVE";
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Estimer la durée de lecture des données audio
        /// </summary>
        private int EstimateAudioDuration(byte[] audioData)
        {
            try
            {
                // Calcul approximatif basé sur 44.1kHz, 16-bit, mono
                const double sampleRate = 44100.0;
                const int bytesPerSample = 2; // 16-bit = 2 bytes

                var samples = audioData.Length / bytesPerSample;
                var durationSeconds = samples / sampleRate;

                return Math.Max(50, (int)(durationSeconds * 1000)); // Minimum 50ms
            }
            catch
            {
                return 100; // Défaut 100ms
            }
        }

        /// <summary>
        /// Jouer un ton de test pour vérifier le système audio
        /// </summary>
        public async Task<bool> PlayTestToneAsync(double frequency = 440.0, int durationMs = 1000)
        {
            try
            {
                LogEvent?.Invoke($"[AudioPlayback] 🎵 Playing test tone {frequency}Hz for {durationMs}ms");

                // Générer ton de test
                var testTone = GenerateTestTone(frequency, durationMs);
                return await PlayAudioDataAsync(testTone, "System");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AudioPlayback] ❌ Error playing test tone: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Générer un ton de test sinusoïdal
        /// </summary>
        private byte[] GenerateTestTone(double frequency, int durationMs)
        {
            const int sampleRate = 44100;
            var samples = (int)(sampleRate * durationMs / 1000.0);
            var audioData = new byte[samples * 2]; // 16-bit = 2 bytes per sample

            for (int i = 0; i < samples; i++)
            {
                var sample = (short)(Math.Sin(2.0 * Math.PI * frequency * i / sampleRate) * 16000);

                // Little-endian encoding
                audioData[i * 2] = (byte)(sample & 0xFF);
                audioData[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            return audioData;
        }

        /// <summary>
        /// Arrêter tout playback en cours
        /// </summary>
        public void StopPlayback()
        {
            try
            {
                lock (_lock)
                {
                    if (_isPlaying)
                    {
                        _soundPlayer?.Stop();
                        _isPlaying = false;
                        PlaybackStateChanged?.Invoke(false);
                        LogEvent?.Invoke("[AudioPlayback] 🛑 Playback stopped");
                    }
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AudioPlayback] ❌ Error stopping playback: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                StopPlayback();
                _soundPlayer?.Dispose();
                LogEvent?.Invoke("[AudioPlayback] Service disposed");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[AudioPlayback] ❌ Error during dispose: {ex.Message}");
            }
        }
    }
}