using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// 🎥 Installation automatique de FFmpeg pour l'encodage vidéo H.264/VP8
    /// Télécharge et installe FFmpeg si absent du système
    /// </summary>
    public static class FFmpegInstaller
    {
        private static readonly string FFMPEG_DOWNLOAD_URL = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        private static readonly string FFMPEG_DIR = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatP2P", "ffmpeg");
        private static readonly string FFMPEG_EXE = Path.Combine(FFMPEG_DIR, "bin", "ffmpeg.exe");
        private static readonly string FFPROBE_EXE = Path.Combine(FFMPEG_DIR, "bin", "ffprobe.exe");

        public static event Action<string>? LogEvent;

        /// <summary>
        /// Vérifier si FFmpeg est disponible (dans PATH ou installation locale)
        /// </summary>
        public static bool IsFFmpegAvailable()
        {
            try
            {
                // 1. Vérifier installation locale ChatP2P
                if (File.Exists(FFMPEG_EXE) && File.Exists(FFPROBE_EXE))
                {
                    LogEvent?.Invoke($"[FFmpeg-Installer] ✅ FFmpeg found in local installation: {FFMPEG_DIR}");
                    return true;
                }

                // 2. Vérifier dans PATH système
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.WaitForExit(5000); // 5 secondes timeout

                if (process.ExitCode == 0)
                {
                    LogEvent?.Invoke($"[FFmpeg-Installer] ✅ FFmpeg found in system PATH");
                    return true;
                }
            }
            catch
            {
                // FFmpeg pas trouvé
            }

            LogEvent?.Invoke($"[FFmpeg-Installer] ❌ FFmpeg not found on system");
            return false;
        }

        /// <summary>
        /// Installation automatique de FFmpeg
        /// </summary>
        public static async Task<bool> InstallFFmpegAsync()
        {
            try
            {
                LogEvent?.Invoke($"[FFmpeg-Installer] 🚀 Starting automatic FFmpeg installation...");

                // Créer dossier de destination
                Directory.CreateDirectory(FFMPEG_DIR);
                var tempZipPath = Path.Combine(Path.GetTempPath(), "ffmpeg-github-shared.zip");

                LogEvent?.Invoke($"[FFmpeg-Installer] 📁 Created directory: {FFMPEG_DIR}");

                // Télécharger FFmpeg
                LogEvent?.Invoke($"[FFmpeg-Installer] 📥 Downloading FFmpeg from: {FFMPEG_DOWNLOAD_URL}");
                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(15); // 15 minutes pour le téléchargement
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "ChatP2P-FFmpegInstaller/1.0");

                    try
                    {
                        LogEvent?.Invoke($"[FFmpeg-Installer] 🔗 Initiating HTTP request...");
                        var response = await httpClient.GetAsync(FFMPEG_DOWNLOAD_URL);
                        LogEvent?.Invoke($"[FFmpeg-Installer] 📡 HTTP Response: {response.StatusCode} ({(int)response.StatusCode})");

                        response.EnsureSuccessStatusCode();

                        LogEvent?.Invoke($"[FFmpeg-Installer] 📊 Content-Length: {response.Content.Headers.ContentLength?.ToString() ?? "Unknown"} bytes");
                        var content = await response.Content.ReadAsByteArrayAsync();
                        LogEvent?.Invoke($"[FFmpeg-Installer] 💾 Downloaded content size: {content.Length} bytes ({content.Length / 1024 / 1024}MB)");

                        await File.WriteAllBytesAsync(tempZipPath, content);
                        LogEvent?.Invoke($"[FFmpeg-Installer] ✅ Downloaded {content.Length / 1024 / 1024}MB to {tempZipPath}");
                    }
                    catch (HttpRequestException ex)
                    {
                        LogEvent?.Invoke($"[FFmpeg-Installer] ❌ HTTP Request failed: {ex.Message}");
                        throw;
                    }
                    catch (TaskCanceledException ex)
                    {
                        LogEvent?.Invoke($"[FFmpeg-Installer] ⏰ Download timeout: {ex.Message}");
                        throw new TimeoutException("FFmpeg download timed out", ex);
                    }
                }

                // Valider le fichier ZIP
                LogEvent?.Invoke($"[FFmpeg-Installer] 🔍 Validating ZIP file...");
                var zipInfo = new FileInfo(tempZipPath);
                LogEvent?.Invoke($"[FFmpeg-Installer] 📁 ZIP file size: {zipInfo.Length} bytes ({zipInfo.Length / 1024 / 1024}MB)");

                if (zipInfo.Length < 1024 * 1024) // Moins de 1MB = probablement pas un vrai FFmpeg
                {
                    LogEvent?.Invoke($"[FFmpeg-Installer] ❌ ZIP file too small, likely not a valid FFmpeg archive");
                    throw new InvalidDataException("Downloaded ZIP file is too small to be a valid FFmpeg archive");
                }

                // Extraire l'archive
                LogEvent?.Invoke($"[FFmpeg-Installer] 📦 Extracting FFmpeg archive...");
                using (var archive = ZipFile.OpenRead(tempZipPath))
                {
                    LogEvent?.Invoke($"[FFmpeg-Installer] 📋 ZIP contains {archive.Entries.Count} entries");

                    var extractedCount = 0;
                    foreach (var entry in archive.Entries)
                    {
                        // Extraire tous les fichiers bin (exe + DLLs natives pour FFmpeg.AutoGen)
                        if (entry.FullName.Contains("/bin/") && !string.IsNullOrEmpty(entry.Name))
                        {
                            var destinationPath = Path.Combine(FFMPEG_DIR, "bin", entry.Name);
                            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                            try
                            {
                                entry.ExtractToFile(destinationPath, overwrite: true);
                                LogEvent?.Invoke($"[FFmpeg-Installer] ✅ Extracted: {entry.Name} ({entry.Length} bytes)");
                                extractedCount++;
                            }
                            catch (Exception ex)
                            {
                                LogEvent?.Invoke($"[FFmpeg-Installer] ⚠️ Failed to extract {entry.Name}: {ex.Message}");
                            }
                        }
                    }

                    LogEvent?.Invoke($"[FFmpeg-Installer] 📊 Extracted {extractedCount} files from archive");
                }

                // Nettoyer fichier temporaire
                File.Delete(tempZipPath);
                LogEvent?.Invoke($"[FFmpeg-Installer] 🗑️ Cleaned up temporary files");

                // Vérifier installation
                if (File.Exists(FFMPEG_EXE) && File.Exists(FFPROBE_EXE))
                {
                    LogEvent?.Invoke($"[FFmpeg-Installer] 🎉 FFmpeg installation completed successfully!");
                    LogEvent?.Invoke($"[FFmpeg-Installer] 📍 Installation path: {FFMPEG_DIR}");

                    // Ajouter au PATH de l'application
                    var binPath = Path.Combine(FFMPEG_DIR, "bin");
                    var currentPath = Environment.GetEnvironmentVariable("PATH");
                    if (!currentPath!.Contains(binPath))
                    {
                        Environment.SetEnvironmentVariable("PATH", $"{currentPath};{binPath}");
                        LogEvent?.Invoke($"[FFmpeg-Installer] 🛣️ Added to application PATH: {binPath}");
                    }

                    return true;
                }
                else
                {
                    LogEvent?.Invoke($"[FFmpeg-Installer] ❌ Installation verification failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpeg-Installer] ❌ Installation failed: {ex.Message}");
                LogEvent?.Invoke($"[FFmpeg-Installer] 🔍 Exception details: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Obtenir la version de FFmpeg installée
        /// </summary>
        public static async Task<string?> GetFFmpegVersionAsync()
        {
            try
            {
                var ffmpegPath = File.Exists(FFMPEG_EXE) ? FFMPEG_EXE : "ffmpeg";

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = "-version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                process.WaitForExit(5000);

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    // Extraire version depuis première ligne (ex: "ffmpeg version 4.4.2")
                    var firstLine = output.Split('\n')[0];
                    LogEvent?.Invoke($"[FFmpeg-Installer] 📋 Version info: {firstLine}");
                    return firstLine;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpeg-Installer] ⚠️ Could not get version: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Installation automatique avec retry en cas d'échec
        /// </summary>
        public static async Task<bool> EnsureFFmpegInstalledAsync()
        {
            if (IsFFmpegAvailable())
            {
                var version = await GetFFmpegVersionAsync();
                LogEvent?.Invoke($"[FFmpeg-Installer] ✅ FFmpeg already available: {version ?? "Unknown version"}");
                return true;
            }

            LogEvent?.Invoke($"[FFmpeg-Installer] 🔧 FFmpeg not found, attempting automatic installation...");

            // Tentative d'installation avec retry
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                LogEvent?.Invoke($"[FFmpeg-Installer] 🔄 Installation attempt {attempt}/3");

                var success = await InstallFFmpegAsync();
                if (success)
                {
                    var version = await GetFFmpegVersionAsync();
                    LogEvent?.Invoke($"[FFmpeg-Installer] 🎉 Installation successful on attempt {attempt}: {version ?? "Unknown version"}");
                    return true;
                }

                if (attempt < 3)
                {
                    LogEvent?.Invoke($"[FFmpeg-Installer] ⏳ Attempt {attempt} failed, retrying in 2 seconds...");
                    await Task.Delay(2000);
                }
            }

            LogEvent?.Invoke($"[FFmpeg-Installer] ❌ All installation attempts failed");
            LogEvent?.Invoke($"[FFmpeg-Installer] 💡 Manual installation required: https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2022-08-04-12-44/ffmpeg-n5.1-2-g915ef932a3-win64-gpl-shared-5.1.zip");
            return false;
        }

        /// <summary>
        /// Nettoyer installation locale de FFmpeg
        /// </summary>
        public static bool UninstallFFmpeg()
        {
            try
            {
                if (Directory.Exists(FFMPEG_DIR))
                {
                    Directory.Delete(FFMPEG_DIR, recursive: true);
                    LogEvent?.Invoke($"[FFmpeg-Installer] 🗑️ Local FFmpeg installation removed: {FFMPEG_DIR}");
                    return true;
                }

                LogEvent?.Invoke($"[FFmpeg-Installer] ℹ️ No local installation found to remove");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[FFmpeg-Installer] ❌ Failed to uninstall: {ex.Message}");
                return false;
            }
        }
    }
}