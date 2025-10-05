using System;
using System.IO;
using System.Threading.Tasks;

namespace ChatP2P.Server
{
    /// <summary>
    /// Server-side logging helper for diagnostic logs
    /// </summary>
    public static class ServerLogHelper
    {
        private static readonly string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ChatP2P_Logs");
        private static readonly object _lockObject = new object();

        static ServerLogHelper()
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
            }
            catch { /* Ignore directory creation errors */ }
        }

        public static async Task LogToServerAsync(string message)
        {
            await WriteLogAsync("server.log", message);
        }

        public static async Task LogToUDPVideoAsync(string message)
        {
            await WriteLogAsync("server_udp_video.log", message);
        }

        public static async Task LogToUDPAudioAsync(string message)
        {
            await WriteLogAsync("server_udp_audio.log", message);
        }

        public static void LogToConsole(string message)
        {
            Console.WriteLine(message);
        }

        private static async Task WriteLogAsync(string fileName, string message)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logEntry = $"[{timestamp}] {message}";
                var filePath = Path.Combine(LogDirectory, fileName);

                lock (_lockObject)
                {
                    File.AppendAllText(filePath, logEntry + Environment.NewLine);
                }
            }
            catch
            {
                // Fail silently to avoid breaking server operation
            }
        }
    }
}