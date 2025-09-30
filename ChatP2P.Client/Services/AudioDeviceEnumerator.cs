using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// Service pour énumérer les périphériques audio réels (microphones et haut-parleurs)
    /// Utilise l'API Windows Core Audio pour récupérer les vrais devices installés
    /// </summary>
    public static class AudioDeviceEnumerator
    {
        [DllImport("winmm.dll")]
        private static extern int waveInGetNumDevs();

        [DllImport("winmm.dll")]
        private static extern int waveInGetDevCaps(UIntPtr uDeviceID, ref WaveInCaps pwic, int cbwic);

        [DllImport("winmm.dll")]
        private static extern int waveOutGetNumDevs();

        [DllImport("winmm.dll")]
        private static extern int waveOutGetDevCaps(UIntPtr uDeviceID, ref WaveOutCaps pwoc, int cbwoc);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct WaveInCaps
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct WaveOutCaps
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
            public uint dwSupport;
        }

        /// <summary>
        /// Récupère la liste des microphones installés
        /// </summary>
        public static async Task<List<AudioDevice>> GetMicrophonesAsync()
        {
            return await Task.Run(() =>
            {
                var microphones = new List<AudioDevice>();

                try
                {
                    int deviceCount = waveInGetNumDevs();

                    // Ajouter le device par défaut
                    microphones.Add(new AudioDevice
                    {
                        Id = -1,
                        Name = "🎤 Default Microphone",
                        IsDefault = true,
                        DeviceType = AudioDeviceType.Microphone
                    });

                    // Énumérer tous les microphones
                    for (int i = 0; i < deviceCount; i++)
                    {
                        var caps = new WaveInCaps();
                        if (waveInGetDevCaps((UIntPtr)i, ref caps, Marshal.SizeOf(caps)) == 0)
                        {
                            var cleanName = CleanDeviceName(caps.szPname);
                            microphones.Add(new AudioDevice
                            {
                                Id = i,
                                Name = $"🎤 {cleanName}",
                                IsDefault = false,
                                DeviceType = AudioDeviceType.Microphone
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    // En cas d'erreur, ajouter au moins un device par défaut
                    microphones.Clear();
                    microphones.Add(new AudioDevice
                    {
                        Id = -1,
                        Name = $"🎤 Default Microphone (Error: {ex.Message})",
                        IsDefault = true,
                        DeviceType = AudioDeviceType.Microphone
                    });
                }

                return microphones;
            });
        }

        /// <summary>
        /// Récupère la liste des haut-parleurs/cartes son installés
        /// </summary>
        public static async Task<List<AudioDevice>> GetSpeakersAsync()
        {
            return await Task.Run(() =>
            {
                var speakers = new List<AudioDevice>();

                try
                {
                    int deviceCount = waveOutGetNumDevs();

                    // Ajouter le device par défaut
                    speakers.Add(new AudioDevice
                    {
                        Id = -1,
                        Name = "🔊 Default Speaker",
                        IsDefault = true,
                        DeviceType = AudioDeviceType.Speaker
                    });

                    // Énumérer tous les haut-parleurs
                    for (int i = 0; i < deviceCount; i++)
                    {
                        var caps = new WaveOutCaps();
                        if (waveOutGetDevCaps((UIntPtr)i, ref caps, Marshal.SizeOf(caps)) == 0)
                        {
                            var cleanName = CleanDeviceName(caps.szPname);
                            speakers.Add(new AudioDevice
                            {
                                Id = i,
                                Name = $"🔊 {cleanName}",
                                IsDefault = false,
                                DeviceType = AudioDeviceType.Speaker
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    // En cas d'erreur, ajouter au moins un device par défaut
                    speakers.Clear();
                    speakers.Add(new AudioDevice
                    {
                        Id = -1,
                        Name = $"🔊 Default Speaker (Error: {ex.Message})",
                        IsDefault = true,
                        DeviceType = AudioDeviceType.Speaker
                    });
                }

                return speakers;
            });
        }

        /// <summary>
        /// Récupère la liste des caméras (pour l'instant simulées)
        /// </summary>
        public static async Task<List<AudioDevice>> GetCamerasAsync()
        {
            return await Task.Run(() =>
            {
                var cameras = new List<AudioDevice>();

                // Pour l'instant, on garde les simulacres pour les caméras
                cameras.Add(new AudioDevice
                {
                    Id = 0,
                    Name = "📹 Default Camera",
                    IsDefault = true,
                    DeviceType = AudioDeviceType.Camera
                });

                cameras.Add(new AudioDevice
                {
                    Id = 1,
                    Name = "📹 USB Camera (Simulation)",
                    IsDefault = false,
                    DeviceType = AudioDeviceType.Camera
                });

                // Ajouter notre caméra virtuelle
                cameras.Add(new AudioDevice
                {
                    Id = 2,
                    Name = "🎬 Virtual Camera (File Simulation)",
                    IsDefault = false,
                    DeviceType = AudioDeviceType.Camera
                });

                return cameras;
            });
        }

        /// <summary>
        /// Nettoie le nom d'un périphérique pour éviter les caractères garbagés/chinois
        /// </summary>
        private static string CleanDeviceName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName))
                return "Unknown Device";

            // Supprimer les caractères null et de contrôle
            var cleanName = rawName.Trim('\0', ' ', '\t', '\r', '\n');

            // Supprimer les caractères de contrôle mais garder les caractères étendus (accents, etc.)
            cleanName = new string(cleanName.Where(c =>
                !char.IsControl(c) && c != '\0').ToArray()).Trim();

            // Si le nom est vide après nettoyage, utiliser un nom générique
            if (string.IsNullOrEmpty(cleanName))
                return "Audio Device";

            // Limiter la longueur
            if (cleanName.Length > 30)
                cleanName = cleanName.Substring(0, 30) + "...";

            return cleanName;
        }
    }

    /// <summary>
    /// Représente un périphérique audio
    /// </summary>
    public class AudioDevice
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public bool IsDefault { get; set; }
        public AudioDeviceType DeviceType { get; set; }
    }

    /// <summary>
    /// Types de périphériques audio
    /// </summary>
    public enum AudioDeviceType
    {
        Microphone,
        Speaker,
        Camera
    }
}