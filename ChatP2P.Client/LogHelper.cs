using System;
using System.IO;
using System.Threading.Tasks;

namespace ChatP2P.Client
{
    public static class LogHelper
    {
        private static string _logDirectory;
        private static bool _verboseLoggingEnabled = false;

        static LogHelper()
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            _logDirectory = Path.Combine(documentsPath, "ChatP2P_Logs");
            Directory.CreateDirectory(_logDirectory);
        }

        public static void SetVerboseLogging(bool enabled)
        {
            _verboseLoggingEnabled = enabled;
        }

        public static async Task LogToGeneralAsync(string message, bool forceLog = false)
        {
            if (!forceLog && !_verboseLoggingEnabled) return;

            try
            {
                var logFile = Path.Combine(_logDirectory, "client.log");
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                await File.AppendAllTextAsync(logFile, logEntry);
            }
            catch { }
        }

        public static async Task LogToAudioAsync(string message, bool forceLog = false)
        {
            if (!forceLog && !_verboseLoggingEnabled) return;

            try
            {
                var logFile = Path.Combine(_logDirectory, "client_audio.log");
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [AUDIO] {message}{Environment.NewLine}";
                await File.AppendAllTextAsync(logFile, logEntry);
            }
            catch { }
        }

        public static async Task LogToIceAsync(string message, bool forceLog = false)
        {
            if (!forceLog && !_verboseLoggingEnabled) return;

            try
            {
                var logFile = Path.Combine(_logDirectory, "client_ice.log");
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [ICE] {message}{Environment.NewLine}";
                await File.AppendAllTextAsync(logFile, logEntry);
            }
            catch { }
        }

        public static async Task LogToCryptoAsync(string message, bool forceLog = false)
        {
            if (!forceLog && !_verboseLoggingEnabled) return;

            try
            {
                var logFile = Path.Combine(_logDirectory, "crypto.log");
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CRYPTO] {message}{Environment.NewLine}";
                await File.AppendAllTextAsync(logFile, logEntry);
            }
            catch { }
        }

        public static async Task LogToRelayAsync(string message, bool forceLog = false)
        {
            if (!forceLog && !_verboseLoggingEnabled) return;

            try
            {
                var logFile = Path.Combine(_logDirectory, "relay_client.log");
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [RELAY] {message}{Environment.NewLine}";
                await File.AppendAllTextAsync(logFile, logEntry);
            }
            catch { }
        }

        public static async Task LogToP2PAsync(string message, bool forceLog = false)
        {
            if (!forceLog && !_verboseLoggingEnabled) return;

            try
            {
                var logFile = Path.Combine(_logDirectory, "p2p_direct.log");
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [P2P] {message}{Environment.NewLine}";
                await File.AppendAllTextAsync(logFile, logEntry);
            }
            catch { }
        }

        public static void LogToConsole(string message, bool forceLog = false)
        {
            if (!forceLog && !_verboseLoggingEnabled) return;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }
    }
}