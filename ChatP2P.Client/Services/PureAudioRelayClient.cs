using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ChatP2P.Client.Services
{
    /// <summary>
    /// 🎤 Client TCP pur pour relay audio binaire (port 8893)
    /// Performance maximale : pas de JSON/Base64, données directes
    /// </summary>
    public class PureAudioRelayClient : IDisposable
    {
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private bool _isConnected = false;
        private bool _isListening = false;
        private string _peerName = "";

        public event Action<string>? LogEvent;
        public event Action<byte[]>? AudioDataReceived;
        public event Action<bool>? ConnectionStateChanged;

        public bool IsConnected => _isConnected;
        public string PeerName => _peerName;

        /// <summary>
        /// Se connecter au canal audio pur du serveur
        /// </summary>
        public async Task<bool> ConnectAsync(string peerName, string serverHost = "localhost", int serverPort = 8893)
        {
            try
            {
                if (_isConnected)
                {
                    LogEvent?.Invoke($"[PURE-AUDIO] Already connected as {_peerName}");
                    return true;
                }

                _peerName = peerName;
                _tcpClient = new TcpClient();

                LogEvent?.Invoke($"[PURE-AUDIO] 🎤 Connecting to pure audio relay {serverHost}:{serverPort}...");
                await _tcpClient.ConnectAsync(serverHost, serverPort);

                _stream = _tcpClient.GetStream();
                _isConnected = true;

                // Envoyer identification (format: "PEER:PeerName")
                var identityBytes = Encoding.UTF8.GetBytes($"PEER:{peerName}");
                await _stream.WriteAsync(identityBytes, 0, identityBytes.Length);
                await _stream.FlushAsync();

                LogEvent?.Invoke($"[PURE-AUDIO] ✅ Connected as {peerName}, waiting for confirmation...");

                // Attendre confirmation serveur
                var buffer = new byte[256];
                var bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);
                var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                if (response == "AUDIO_READY")
                {
                    LogEvent?.Invoke($"[PURE-AUDIO] ✅ Pure audio relay ready for {peerName}");
                    ConnectionStateChanged?.Invoke(true);

                    // Commencer à écouter les données audio entrantes
                    _isListening = true;
                    _ = Task.Run(ListenForAudioDataAsync);

                    return true;
                }
                else
                {
                    LogEvent?.Invoke($"[PURE-AUDIO] ❌ Unexpected server response: {response}");
                    await DisconnectAsync();
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[PURE-AUDIO] ❌ Connection failed: {ex.Message}");
                await DisconnectAsync();
                return false;
            }
        }

        /// <summary>
        /// Envoyer données audio binaires pures (pas de JSON/Base64)
        /// </summary>
        public async Task<bool> SendAudioDataAsync(byte[] audioData)
        {
            try
            {
                if (!_isConnected || _stream == null)
                {
                    LogEvent?.Invoke($"[PURE-AUDIO] ⚠️ Not connected, cannot send {audioData.Length} bytes");
                    return false;
                }

                // 🚀 ENVOI BINAIRE PUR - Performance maximale !
                await _stream.WriteAsync(audioData, 0, audioData.Length);
                await _stream.FlushAsync();

                LogEvent?.Invoke($"[PURE-AUDIO] 🎵 Sent pure audio: {audioData.Length} bytes");
                return true;
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[PURE-AUDIO] ❌ Error sending audio: {ex.Message}");
                await DisconnectAsync();
                return false;
            }
        }

        /// <summary>
        /// Écouter les données audio binaires entrantes
        /// </summary>
        private async Task ListenForAudioDataAsync()
        {
            var buffer = new byte[65536]; // 64KB buffer pour audio

            try
            {
                LogEvent?.Invoke($"[PURE-AUDIO] 👂 Listening for incoming audio data...");

                while (_isListening && _isConnected && _stream != null)
                {
                    var bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);

                    if (bytesRead > 0)
                    {
                        // 🎵 RÉCEPTION BINAIRE PURE - Pas de parsing JSON !
                        var audioData = new byte[bytesRead];
                        Array.Copy(buffer, 0, audioData, 0, bytesRead);

                        LogEvent?.Invoke($"[PURE-AUDIO] 🔊 Received pure audio: {bytesRead} bytes");
                        AudioDataReceived?.Invoke(audioData);
                    }
                    else
                    {
                        LogEvent?.Invoke($"[PURE-AUDIO] 📴 Server closed audio connection");
                        break;
                    }
                }
            }
            catch (Exception ex) when (_isListening)
            {
                LogEvent?.Invoke($"[PURE-AUDIO] ❌ Error listening for audio: {ex.Message}");
            }
            finally
            {
                LogEvent?.Invoke($"[PURE-AUDIO] 🛑 Audio listening stopped");
                await DisconnectAsync();
            }
        }

        /// <summary>
        /// Se déconnecter du canal audio pur
        /// </summary>
        public async Task DisconnectAsync()
        {
            try
            {
                _isListening = false;

                if (_isConnected)
                {
                    LogEvent?.Invoke($"[PURE-AUDIO] 📴 Disconnecting {_peerName} from pure audio relay");
                    _isConnected = false;
                    ConnectionStateChanged?.Invoke(false);
                }

                _stream?.Close();
                _tcpClient?.Close();

                _stream = null;
                _tcpClient = null;
                _peerName = "";

                LogEvent?.Invoke($"[PURE-AUDIO] ✅ Pure audio relay disconnected");
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[PURE-AUDIO] ❌ Error during disconnect: {ex.Message}");
            }
        }

        /// <summary>
        /// Obtenir statistiques de connexion
        /// </summary>
        public string GetConnectionStats()
        {
            return $"Pure Audio Relay - Connected: {_isConnected}, Peer: {_peerName}, Listening: {_isListening}";
        }

        public void Dispose()
        {
            try
            {
                DisconnectAsync().Wait(1000);
            }
            catch (Exception ex)
            {
                LogEvent?.Invoke($"[PURE-AUDIO] ❌ Error during dispose: {ex.Message}");
            }
        }
    }
}