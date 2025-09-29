using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChatP2P.Client
{
    /// <summary>
    /// Client P2P direct qui bypasse complètement le relay server
    /// Etablit une connexion TCP directe entre VM1 et VM2 pour les transferts de fichiers
    /// </summary>
    public class P2PDirectClient
    {
        private readonly string _localDisplayName;
        private readonly int _p2pPort = 8890; // Port différent du relay (8889)
        
        public P2PDirectClient(string localDisplayName)
        {
            _localDisplayName = localDisplayName;
        }
        
        /// <summary>
        /// Envoie un fichier directement à un peer sans passer par le relay
        /// </summary>
        public async Task<bool> SendFileDirectAsync(string targetPeerIp, string filePath, string fileName, Action<double> progressCallback)
        {
            try
            {
                await LogToFile($"🚀 [P2P-DIRECT] Connecting directly to {targetPeerIp}:{_p2pPort}");
                
                using var client = new TcpClient();
                await client.ConnectAsync(targetPeerIp, _p2pPort);
                
                using var stream = client.GetStream();
                using var writer = new BinaryWriter(stream);
                using var reader = new BinaryReader(stream);
                
                // 1. Send file metadata
                var fileInfo = new FileInfo(filePath);
                var metadata = new
                {
                    type = "FILE_TRANSFER_DIRECT",
                    fileName = fileName,
                    fileSize = fileInfo.Length,
                    fromPeer = _localDisplayName
                };
                
                var metadataJson = JsonSerializer.Serialize(metadata);
                var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
                
                writer.Write(metadataBytes.Length);
                writer.Write(metadataBytes);
                writer.Flush();
                
                await LogToFile($"📋 [P2P-DIRECT] Metadata sent: {fileName} ({fileInfo.Length} bytes)");
                
                // 2. Wait for acknowledgment
                var ackLength = reader.ReadInt32();
                var ackBytes = reader.ReadBytes(ackLength);
                var ackMessage = Encoding.UTF8.GetString(ackBytes);
                
                if (ackMessage != "READY")
                {
                    await LogToFile($"❌ [P2P-DIRECT] Peer not ready: {ackMessage}");
                    return false;
                }
                
                // 3. Send file in chunks
                const int chunkSize = 8192; // 8KB chunks for optimal TCP performance
                var buffer = new byte[chunkSize];
                var totalBytes = fileInfo.Length;
                var sentBytes = 0L;
                
                using var fileStream = File.OpenRead(filePath);
                
                while (sentBytes < totalBytes)
                {
                    var bytesToRead = (int)Math.Min(chunkSize, totalBytes - sentBytes);
                    var bytesRead = await fileStream.ReadAsync(buffer, 0, bytesToRead);
                    
                    if (bytesRead == 0) break;
                    
                    // Send chunk size then data
                    writer.Write(bytesRead);
                    writer.Write(buffer, 0, bytesRead);
                    writer.Flush();
                    
                    sentBytes += bytesRead;
                    var progress = (double)sentBytes / totalBytes * 100;
                    
                    progressCallback?.Invoke(progress);
                    
                    if (sentBytes % (chunkSize * 50) == 0) // Log every ~400KB
                    {
                        await LogToFile($"📊 [P2P-DIRECT] Progress: {progress:F1}% ({sentBytes}/{totalBytes} bytes)");
                    }
                }
                
                // 4. Send end marker
                writer.Write(0); // 0 bytes = end of transfer
                writer.Flush();
                
                await LogToFile($"✅ [P2P-DIRECT] File transfer completed: {fileName}");
                return true;
            }
            catch (Exception ex)
            {
                await LogToFile($"❌ [P2P-DIRECT] Transfer failed: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Démarre le serveur P2P pour recevoir des fichiers
        /// </summary>
        public async Task StartP2PServerAsync()
        {
            try
            {
                var listener = new TcpListener(System.Net.IPAddress.Any, _p2pPort);
                listener.Start();
                
                await LogToFile($"🎧 [P2P-SERVER] Listening on port {_p2pPort} for direct P2P connections");
                
                // Keep listening in background
                _ = Task.Run(async () =>
                {
                    while (true)
                    {
                        try
                        {
                            var client = await listener.AcceptTcpClientAsync();
                            _ = Task.Run(async () => await HandleIncomingFileTransfer(client));
                        }
                        catch (Exception ex)
                        {
                            await LogToFile($"❌ [P2P-SERVER] Listener error: {ex.Message}");
                            await Task.Delay(5000); // Wait before retry
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                await LogToFile($"❌ [P2P-SERVER] Failed to start server: {ex.Message}");
            }
        }
        
        private async Task HandleIncomingFileTransfer(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new BinaryReader(stream))
                using (var writer = new BinaryWriter(stream))
                {
                    // 1. Read metadata
                    var metadataLength = reader.ReadInt32();
                    var metadataBytes = reader.ReadBytes(metadataLength);
                    var metadataJson = Encoding.UTF8.GetString(metadataBytes);
                    
                    var metadata = JsonSerializer.Deserialize<JsonElement>(metadataJson);
                    var fileName = metadata.GetProperty("fileName").GetString();
                    var fileSize = metadata.GetProperty("fileSize").GetInt64();
                    var fromPeer = metadata.GetProperty("fromPeer").GetString();
                    
                    await LogToFile($"📥 [P2P-SERVER] Receiving {fileName} ({fileSize} bytes) from {fromPeer}");
                    
                    // 2. Send acknowledgment
                    var ackBytes = Encoding.UTF8.GetBytes("READY");
                    writer.Write(ackBytes.Length);
                    writer.Write(ackBytes);
                    writer.Flush();
                    
                    // 3. Create output file
                    var desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ChatP2P_Recv");
                    Directory.CreateDirectory(desktopPath);
                    
                    var outputPath = Path.Combine(desktopPath, fileName);
                    var receivedBytes = 0L;
                    
                    using (var fileStream = File.Create(outputPath))
                    {
                        while (receivedBytes < fileSize)
                        {
                            var chunkSize = reader.ReadInt32();
                            if (chunkSize == 0) break; // End marker
                            
                            var chunkData = reader.ReadBytes(chunkSize);
                            await fileStream.WriteAsync(chunkData, 0, chunkData.Length);
                            
                            receivedBytes += chunkData.Length;
                            var progress = (double)receivedBytes / fileSize * 100;
                            
                            if (receivedBytes % (8192 * 50) == 0) // Log every ~400KB
                            {
                                await LogToFile($"📊 [P2P-SERVER] Received: {progress:F1}% ({receivedBytes}/{fileSize} bytes)");
                            }
                        }
                    }
                    
                    await LogToFile($"✅ [P2P-SERVER] File saved: {outputPath}");
                }
            }
            catch (Exception ex)
            {
                await LogToFile($"❌ [P2P-SERVER] Error handling transfer: {ex.Message}");
            }
        }
        
        private async Task LogToFile(string message)
        {
            await LogHelper.LogToP2PAsync(message);
        }
        
        /// <summary>
        /// Résout l'IP d'un peer via le relay server
        /// </summary>
        public async Task<string?> ResolvePeerIpAsync(string peerName)
        {
            try
            {
                // Pour l'instant, on utilise des IPs fixes pour VM1/VM2
                // TODO: Implémenter découverte dynamique via relay
                return peerName switch
                {
                    "VM1" => "192.168.1.100", // IP fixe VM1 
                    "VM2" => "192.168.1.101", // IP fixe VM2
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }
    }
}