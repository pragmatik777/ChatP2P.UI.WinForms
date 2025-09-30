using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// 📹 Client TCP pur pour relay vidéo binaire (port 8894)
    /// Performance maximale : frames RGB directes sans codec/compression
    /// </summary>
    public class PureVideoRelayClient : IDisposable
    {
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private bool _isConnected = false;
        private bool _isListening = false;
        private string _peerName = "";

        public event Action<string>? LogEvent;
        public event Action<byte[]>? VideoDataReceived;
        public event Action<bool>? ConnectionStateChanged;

        public bool IsConnected => _isConnected;
        public string PeerName => _peerName;

        /// <summary>
        /// Se connecter au canal vidéo pur du serveur
        /// </summary>
        public async Task<bool> ConnectAsync(string peerName, string serverHost = "localhost", int serverPort = 8894)
        {
            try
            {
                if (_isConnected)
                {
                    LogEvent?.Invoke($"[PURE-VIDEO] Already connected as {_peerName}");
                    return true;
                }

                _peerName = peerName;
                _tcpClient = new TcpClient();

                LogEvent?.Invoke($"[PURE-VIDEO] 📹 Connecting to pure video relay {serverHost}:{serverPort}...");
                await _tcpClient.ConnectAsync(serverHost, serverPort);

                _stream = _tcpClient.GetStream();
                _isConnected = true;

                // Envoyer identification (format: "PEER:PeerName")
                var identityBytes = Encoding.UTF8.GetBytes($"PEER:{peerName}");
                await _stream.WriteAsync(identityBytes, 0, identityBytes.Length);
                await _stream.FlushAsync();

                LogEvent?.Invoke($"[PURE-VIDEO] ✅ Connected as {peerName}, waiting for confirmation...");

                // Attendre confirmation serveur
                var buffer = new byte[256];
                var bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);
                var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                if (response.StartsWith("CONNECTED:"))
                {
                    LogEvent?.Invoke($"[PURE-VIDEO] ✅ Server confirmed connection: {response}");

                    // Démarrer écoute des données vidéo entrantes
                    _ = Task.Run(ListenForVideoDataAsync);

                    ConnectionStateChanged?.Invoke(true);
                    return true;
                }
                else
                {
                    LogEvent?.Invoke($"[PURE-VIDEO] ❌ Server rejected connection: {response}");
                    await DisconnectAsync();
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[PURE-VIDEO] ❌ Connection failed: {ex.Message}");
                await DisconnectAsync();
                return false;
            }
        }

        /// <summary>
        /// Envoyer données vidéo binaires (frame RGB directe)
        /// </summary>
        public async Task<bool> SendVideoDataAsync(byte[] videoData)
        {
            try
            {
                if (!_isConnected || _stream == null)
                {
                    LogEvent?.Invoke($"[PURE-VIDEO] ❌ Not connected, cannot send video data"); 
                    return false;
                }

                if (videoData == null || videoData.Length == 0)
                {
                    LogEvent?.Invoke($"[PURE-VIDEO] ⚠️ Empty video data, skipping");
                    return false;
                }

                // Format: [LENGTH:4 bytes][DATA:variable] - Simple et efficace
                var lengthBytes = BitConverter.GetBytes(videoData.Length);

                await _stream.WriteAsync(lengthBytes, 0, 4);
                await _stream.WriteAsync(videoData, 0, videoData.Length);
                await _stream.FlushAsync();

                LogEvent?.Invoke($"[PURE-VIDEO] 📹 Video frame sent: {videoData.Length} bytes");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[PURE-VIDEO] ❌ Failed to send video data: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Écouter les données vidéo entrantes en continu
        /// </summary>
        private async Task ListenForVideoDataAsync()
        {
            try
            {
                _isListening = true;
                LogEvent?.Invoke($"[PURE-VIDEO] 👁️ Started listening for incoming video data...");

                var lengthBuffer = new byte[4];

                while (_isConnected && _stream != null && _isListening)
                {
                    try
                    {
                        // Lire la taille de la frame
                        var totalBytesRead = 0;
                        while (totalBytesRead < 4)
                        {
                            var bytesRead = await _stream.ReadAsync(lengthBuffer, totalBytesRead, 4 - totalBytesRead);
                            if (bytesRead == 0)
                            {
                                LogEvent?.Invoke($"[PURE-VIDEO] ⚠️ Connection closed by server");
                                break;
                            }
                            totalBytesRead += bytesRead;
                        }

                        if (totalBytesRead < 4) break;

                        var dataLength = BitConverter.ToInt32(lengthBuffer, 0);

                        // Validation taille raisonnable (max 5MB par frame)
                        if (dataLength <= 0 || dataLength > 5 * 1024 * 1024)
                        {
                            LogEvent?.Invoke($"[PURE-VIDEO] ❌ Invalid frame size: {dataLength} bytes");
                            continue;
                        }

                        // Lire les données vidéo
                        var videoBuffer = new byte[dataLength];
                        totalBytesRead = 0;

                        while (totalBytesRead < dataLength)
                        {
                            var bytesRead = await _stream.ReadAsync(videoBuffer, totalBytesRead, dataLength - totalBytesRead);
                            if (bytesRead == 0)
                            {
                                LogEvent?.Invoke($"[PURE-VIDEO] ⚠️ Incomplete video frame received");
                                break;
                            }
                            totalBytesRead += bytesRead;
                        }

                        if (totalBytesRead == dataLength)
                        {
                            LogEvent?.Invoke($"[PURE-VIDEO] 📹 Video frame received: {dataLength} bytes");
                            VideoDataReceived?.Invoke(videoBuffer);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogEvent?.Invoke($"[PURE-VIDEO] ❌ Error reading video data: {ex.Message}");
                        await Task.Delay(100); // Éviter spam en cas d'erreur continue
                    }
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[PURE-VIDEO] ❌ Video listening loop error: {ex.Message}");
            }
            finally
            {
                _isListening = false;
                LogEvent?.Invoke($"[PURE-VIDEO] 👁️ Stopped listening for video data");
            }
        }

        /// <summary>
        /// Déconnecter du serveur relay vidéo
        /// </summary>
        public async Task DisconnectAsync()
        {
            try
            {
                _isListening = false;

                if (_isConnected)
                {
                    LogEvent?.Invoke($"[PURE-VIDEO] 🔌 Disconnecting from video relay server...");

                    _isConnected = false;
                    ConnectionStateChanged?.Invoke(false);
                }

                _stream?.Close();
                _tcpClient?.Close();

                _stream = null;
                _tcpClient = null;

                LogEvent?.Invoke($"[PURE-VIDEO] ✅ Disconnected from video relay server");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[PURE-VIDEO] ❌ Error during disconnect: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                DisconnectAsync().Wait(2000);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[PURE-VIDEO] ❌ Error during dispose: {ex.Message}");
            }
        }
    }
}