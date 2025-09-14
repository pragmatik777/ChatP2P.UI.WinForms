using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChatP2P.Server
{
    public class FileChunk
    {
        public int Index { get; set; }
        public string Hash { get; set; }
        public byte[] Data { get; set; }
        public bool Confirmed { get; set; } = false;
        public int RetryCount { get; set; } = 0;
        public DateTime LastSentUtc { get; set; } = DateTime.MinValue;

        public FileChunk(int index, byte[] data)
        {
            Index = index;
            Data = data;
            Hash = ComputeHash(data);
        }

        private static string ComputeHash(byte[] data)
        {
            using var sha = SHA256.Create();
            var hashBytes = sha.ComputeHash(data);
            return Convert.ToBase64String(hashBytes);
        }

        public bool VerifyHash()
        {
            return Hash == ComputeHash(Data);
        }
    }

    public class FileMetadata
    {
        public string TransferId { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public int ChunkSize { get; set; }
        public int TotalChunks { get; set; }
        public string FileHash { get; set; }
        public string FromPeer { get; set; }
        public string ToPeer { get; set; }

        public FileMetadata(string transferId, string fileName, long fileSize, int chunkSize, string fromPeer, string toPeer)
        {
            TransferId = transferId;
            FileName = fileName;
            FileSize = fileSize;
            ChunkSize = chunkSize;
            TotalChunks = (int)Math.Ceiling(fileSize / (double)chunkSize);
            FromPeer = fromPeer;
            ToPeer = toPeer;
        }
    }

    public class TransferState
    {
        public FileMetadata Metadata { get; set; }
        public ConcurrentDictionary<int, FileChunk> Chunks { get; set; }
        public HashSet<int> ReceivedChunks { get; set; }
        public Queue<int> PendingChunks { get; set; }
        public HashSet<int> FailedChunks { get; set; }
        public FileStream? OutputFile { get; set; }
        public string OutputPath { get; set; }
        public DateTime StartTimeUtc { get; set; }
        public DateTime LastActivityUtc { get; set; }
        public bool IsCompleted { get; set; } = false;
        public bool IsPaused { get; set; } = false;

        public TransferState(FileMetadata metadata, string outputPath)
        {
            Metadata = metadata;
            OutputPath = outputPath;
            Chunks = new ConcurrentDictionary<int, FileChunk>();
            ReceivedChunks = new HashSet<int>();
            PendingChunks = new Queue<int>();
            FailedChunks = new HashSet<int>();
            StartTimeUtc = DateTime.UtcNow;
            LastActivityUtc = DateTime.UtcNow;

            // Initialiser la queue avec tous les chunks à recevoir
            for (int i = 0; i < metadata.TotalChunks; i++)
            {
                PendingChunks.Enqueue(i);
            }
        }

        public double Progress => Metadata.TotalChunks == 0 ? 0 : (ReceivedChunks.Count / (double)Metadata.TotalChunks) * 100;

        public long ReceivedBytes => ReceivedChunks.Count * Metadata.ChunkSize;
    }

    public class FileTransferService
    {
        private readonly ConcurrentDictionary<string, TransferState> _activeTransfers = new();
        private readonly object _lockTransfers = new();

        // Configuration adaptative avec optimisations WebRTC
        public int MaxConcurrentChunks { get; set; } = 3;
        public int ChunkTimeoutMs { get; set; } = 10000;
        public int MaxRetries { get; set; } = 5;
        public int ChunkSize { get; set; } = 2048; // Base: 2KB pour éviter limite JSON 4096 bytes
        
        // NOUVEAU: Adaptation bande passante dynamique
        private int _adaptiveChunkSize = 2048;
        private int _adaptiveDelay = 20; // ms entre chunks
        private readonly List<TransferMetrics> _recentTransfers = new();
        private DateTime _lastBandwidthCheck = DateTime.UtcNow;
        
        // Métriques de performance
        private double _currentThroughputKBps = 0;
        private NetworkCondition _networkCondition = NetworkCondition.Unknown;

        // Événements
        public event Action<string, double, long, long>? OnTransferProgress; // transferId, progress, receivedBytes, totalBytes
        public event Action<string, bool, string>? OnTransferCompleted; // transferId, success, outputPath
        public event Action<string, int, string>? OnChunkReceived; // transferId, chunkIndex, hash
        public event Action<string>? OnLog;
        public event Action<double, int, NetworkCondition>? OnBandwidthAdapted; // throughputKBps, chunkSize, condition

        private static FileTransferService? _instance;
        public static FileTransferService Instance => _instance ??= new FileTransferService();

        /// <summary>
        /// Démarre un nouveau transfert de réception
        /// </summary>
        public async Task<bool> StartReceiveTransferAsync(FileMetadata metadata, string outputPath)
        {
            try
            {
                lock (_lockTransfers)
                {
                    if (_activeTransfers.ContainsKey(metadata.TransferId))
                    {
                        OnLog?.Invoke($"[FILE-TRANSFER] Transfert {metadata.TransferId} déjà en cours");
                        return false;
                    }

                    var transfer = new TransferState(metadata, outputPath);

                    // Créer le répertoire si nécessaire
                    var directory = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Créer le fichier de sortie
                    transfer.OutputFile = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                    transfer.OutputFile.SetLength(metadata.FileSize);

                    _activeTransfers[metadata.TransferId] = transfer;

                    OnLog?.Invoke($"[FILE-TRANSFER] Nouveau transfert: {metadata.FileName} ({metadata.FileSize} bytes, {metadata.TotalChunks} chunks)");

                    return true;
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[FILE-TRANSFER] Erreur démarrage transfert: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Traite la réception d'un chunk
        /// </summary>
        public async Task<bool> ProcessReceivedChunkAsync(string transferId, int chunkIndex, string chunkHash, byte[] chunkData)
        {
            try
            {
                lock (_lockTransfers)
                {
                    if (!_activeTransfers.TryGetValue(transferId, out var transfer))
                    {
                        OnLog?.Invoke($"[FILE-TRANSFER] Transfert {transferId} introuvable");
                        return false;
                    }

                    // Vérifier si on a déjà ce chunk
                    if (transfer.ReceivedChunks.Contains(chunkIndex))
                    {
                        OnLog?.Invoke($"[FILE-TRANSFER] Chunk {chunkIndex} déjà reçu (ignoré)");
                        return true;
                    }

                    // Créer le chunk et vérifier son hash
                    var chunk = new FileChunk(chunkIndex, chunkData);
                    if (chunk.Hash != chunkHash)
                    {
                        OnLog?.Invoke($"[FILE-TRANSFER] Hash invalide pour chunk {chunkIndex}");
                        transfer.FailedChunks.Add(chunkIndex);
                        return false;
                    }

                    // Écrire le chunk dans le fichier à la bonne position
                    long position = (long)chunkIndex * transfer.Metadata.ChunkSize;
                    transfer.OutputFile?.Seek(position, SeekOrigin.Begin);
                    transfer.OutputFile?.Write(chunkData, 0, chunkData.Length);
                    transfer.OutputFile?.Flush();

                    // Marquer comme reçu
                    transfer.Chunks[chunkIndex] = chunk;
                    transfer.ReceivedChunks.Add(chunkIndex);
                    transfer.LastActivityUtc = DateTime.UtcNow;

                    OnChunkReceived?.Invoke(transferId, chunkIndex, chunkHash);
                    OnTransferProgress?.Invoke(transferId, transfer.Progress, transfer.ReceivedBytes, transfer.Metadata.FileSize);

                    // Vérifier si le transfert est terminé
                    if (transfer.ReceivedChunks.Count == transfer.Metadata.TotalChunks)
                    {
                        _ = Task.Run(async () => await CompleteTransferAsync(transferId, transfer));
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[FILE-TRANSFER] Erreur traitement chunk: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Finalise un transfert terminé
        /// </summary>
        private async Task CompleteTransferAsync(string transferId, TransferState transfer)
        {
            try
            {
                transfer.OutputFile?.Close();
                transfer.OutputFile?.Dispose();
                transfer.IsCompleted = true;

                OnLog?.Invoke($"[FILE-TRANSFER] Transfert {transferId} terminé: {transfer.Metadata.FileName}");
                OnTransferCompleted?.Invoke(transferId, true, transfer.OutputPath);

                _activeTransfers.TryRemove(transferId, out _);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[FILE-TRANSFER] Erreur finalisation: {ex.Message}");
                OnTransferCompleted?.Invoke(transferId, false, transfer.OutputPath);
            }
        }

        /// <summary>
        /// Obtient la liste des chunks manquants pour demander retransmission
        /// </summary>
        public List<int> GetMissingChunks(string transferId, int maxCount)
        {
            var missing = new List<int>();

            lock (_lockTransfers)
            {
                if (!_activeTransfers.TryGetValue(transferId, out var transfer))
                    return missing;

                int count = 0;

                // Prioriser les chunks qui ont échoué mais avec retry
                foreach (var failedChunk in transfer.FailedChunks)
                {
                    if (count >= maxCount) break;
                    missing.Add(failedChunk);
                    count++;
                }

                // Puis prendre des chunks en attente
                while (transfer.PendingChunks.Count > 0 && count < maxCount)
                {
                    missing.Add(transfer.PendingChunks.Dequeue());
                    count++;
                }
            }

            return missing;
        }

        /// <summary>
        /// Obtient un transfert actif par son ID
        /// </summary>
        public TransferState? GetActiveTransfer(string transferId)
        {
            lock (_lockTransfers)
            {
                return _activeTransfers.TryGetValue(transferId, out var transfer) ? transfer : null;
            }
        }

        /// <summary>
        /// Envoie un fichier via P2P DataChannel
        /// </summary>
        public async Task<bool> SendFileP2PAsync(string fromPeer, string toPeer, string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists)
                {
                    OnLog?.Invoke($"[FILE-TRANSFER] Fichier introuvable: {filePath}");
                    return false;
                }

                var transferId = Guid.NewGuid().ToString();
                
                // Optimisation automatique selon taille fichier
                OptimizeForFileSize(fileInfo.Length);
                
                var metadata = new FileMetadata(transferId, fileInfo.Name, fileInfo.Length, _adaptiveChunkSize, fromPeer, toPeer);

                // Calculer le hash du fichier complet
                metadata.FileHash = await ComputeFileHashAsync(filePath);

                // Envoyer les métadonnées d'abord
                var metadataJson = JsonSerializer.Serialize(new
                {
                    type = "FILE_METADATA",
                    transferId = metadata.TransferId,
                    fileName = metadata.FileName,
                    fileSize = metadata.FileSize,
                    chunkSize = metadata.ChunkSize,
                    totalChunks = metadata.TotalChunks,
                    fileHash = metadata.FileHash,
                    fromPeer = metadata.FromPeer,
                    toPeer = metadata.ToPeer
                });

                if (!await P2PService.SendMessage(toPeer, metadataJson))
                {
                    OnLog?.Invoke($"[FILE-TRANSFER] Échec envoi métadonnées à {toPeer}");
                    return false;
                }

                OnLog?.Invoke($"[FILE-TRANSFER] Début envoi P2P: {fileInfo.Name} ({fileInfo.Length} bytes, {metadata.TotalChunks} chunks)");

                // Envoyer les chunks
                return await SendFileChunksP2PAsync(metadata, filePath, toPeer);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[FILE-TRANSFER] Erreur envoi P2P: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Envoie les chunks d'un fichier via P2P
        /// </summary>
        private async Task<bool> SendFileChunksP2PAsync(FileMetadata metadata, string filePath, string toPeer)
        {
            try
            {
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                var buffer = new byte[metadata.ChunkSize];
                int sentChunks = 0;

                for (int chunkIndex = 0; chunkIndex < metadata.TotalChunks; chunkIndex++)
                {
                    var bytesRead = await fileStream.ReadAsync(buffer, 0, metadata.ChunkSize);
                    if (bytesRead == 0) break;

                    // Créer le chunk avec les données actuelles
                    var chunkData = new byte[bytesRead];
                    Array.Copy(buffer, chunkData, bytesRead);

                    var chunk = new FileChunk(chunkIndex, chunkData);

                    // Créer le message de chunk
                    var chunkMessage = JsonSerializer.Serialize(new
                    {
                        type = "FILE_CHUNK",
                        transferId = metadata.TransferId,
                        chunkIndex = chunk.Index,
                        chunkHash = chunk.Hash,
                        chunkData = Convert.ToBase64String(chunk.Data)
                    });

                    if (await P2PService.SendMessage(toPeer, chunkMessage))
                    {
                        sentChunks++;
                        var progress = (sentChunks / (double)metadata.TotalChunks) * 100;
                        OnTransferProgress?.Invoke(metadata.TransferId, progress, sentChunks * metadata.ChunkSize, metadata.FileSize);

                        // Délai adaptatif selon conditions réseau
                        await Task.Delay(_adaptiveDelay);
                        
                        // Mesurer performance et adapter si nécessaire
                        if (chunkIndex % 10 == 0) // Check every 10 chunks
                        {
                            await AdaptTransferParameters(sentChunks, metadata.ChunkSize, DateTime.UtcNow.Subtract(DateTime.UtcNow.AddSeconds(-10)));
                        }
                    }
                    else
                    {
                        OnLog?.Invoke($"[FILE-TRANSFER] Échec envoi chunk {chunkIndex}");
                    }
                }

                OnLog?.Invoke($"[FILE-TRANSFER] Envoi P2P terminé: {sentChunks}/{metadata.TotalChunks} chunks envoyés");
                return sentChunks == metadata.TotalChunks;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[FILE-TRANSFER] Erreur envoi chunks P2P: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Calcule le hash SHA256 d'un fichier
        /// </summary>
        private async Task<string> ComputeFileHashAsync(string filePath)
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var sha = SHA256.Create();
            var hashBytes = await Task.Run(() => sha.ComputeHash(fileStream));
            return Convert.ToBase64String(hashBytes);
        }

        /// <summary>
        /// Nettoie les transferts inactifs ou en timeout
        /// </summary>
        public void CleanupTransfers()
        {
            var toRemove = new List<string>();
            var now = DateTime.UtcNow;

            lock (_lockTransfers)
            {
                foreach (var kvp in _activeTransfers)
                {
                    var transfer = kvp.Value;
                    var inactiveTime = now.Subtract(transfer.LastActivityUtc);

                    // Timeout après 5 minutes d'inactivité
                    if (inactiveTime.TotalMinutes > 5)
                    {
                        toRemove.Add(kvp.Key);
                        OnLog?.Invoke($"[FILE-TRANSFER] Timeout transfert {kvp.Key}");
                    }
                }

                foreach (var transferId in toRemove)
                {
                    if (_activeTransfers.TryRemove(transferId, out var transfer))
                    {
                        transfer.OutputFile?.Close();
                        transfer.OutputFile?.Dispose();
                        OnTransferCompleted?.Invoke(transferId, false, transfer.OutputPath);
                    }
                }
            }
        }

        /// <summary>
        /// Obtient les statistiques d'un transfert
        /// </summary>
        public string GetTransferStats(string transferId)
        {
            lock (_lockTransfers)
            {
                if (!_activeTransfers.TryGetValue(transferId, out var transfer))
                    return "Transfert introuvable";

                var elapsed = DateTime.UtcNow.Subtract(transfer.StartTimeUtc);
                var speedKBps = elapsed.TotalSeconds > 0 ? transfer.ReceivedBytes / elapsed.TotalSeconds / 1024 : 0;

                return $"Progress: {transfer.Progress:F1}% ({transfer.ReceivedChunks.Count}/{transfer.Metadata.TotalChunks}), " +
                       $"Speed: {speedKBps:F1} KB/s, Failed: {transfer.FailedChunks.Count}";
            }
        }

        /// <summary>
        /// Traite la réception d'un chunk binaire P2P (reconstruction automatique)
        /// </summary>
        public async Task ProcessReceivedBinaryChunk(string fromPeer, byte[] binaryData)
        {
            try
            {
                OnLog?.Invoke($"[P2P-FILE] Processing binary chunk from {fromPeer}: {binaryData.Length} bytes");

                // Chercher un transfert actif pour ce peer
                TransferState? activeTransfer = null;
                lock (_lockTransfers)
                {
                    activeTransfer = _activeTransfers.Values
                        .FirstOrDefault(t => t.Metadata.FromPeer == fromPeer && !t.IsCompleted);
                }

                if (activeTransfer == null)
                {
                    // Nouveau transfert - créer automatiquement
                    string fileName = $"received_file_{fromPeer}_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
                    string transferId = Guid.NewGuid().ToString();
                    
                    // Estimer la taille totale (provisoire - sera ajustée)
                    var estimatedMetadata = new FileMetadata(transferId, fileName, binaryData.Length * 100, _adaptiveChunkSize, fromPeer, "LOCAL");
                    
                    string desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ChatP2P_Recv");
                    if (!Directory.Exists(desktopPath))
                    {
                        Directory.CreateDirectory(desktopPath);
                    }
                    string outputPath = Path.Combine(desktopPath, fileName);
                    
                    bool started = await StartReceiveTransferAsync(estimatedMetadata, outputPath);
                    if (!started)
                    {
                        OnLog?.Invoke($"[P2P-FILE] Failed to start auto-transfer for {fromPeer}");
                        return;
                    }
                    
                    activeTransfer = GetActiveTransfer(transferId);
                    OnLog?.Invoke($"[P2P-FILE] Auto-created transfer {transferId} for {fileName}");
                }

                if (activeTransfer != null)
                {
                    // Déterminer l'index du chunk (séquentiel pour l'instant)
                    int chunkIndex = activeTransfer.ReceivedChunks.Count;
                    
                    // Traiter le chunk reçu
                    string chunkHash = ComputeChunkHash(binaryData);
                    bool processed = await ProcessReceivedChunkAsync(activeTransfer.Metadata.TransferId, chunkIndex, chunkHash, binaryData);
                    
                    if (processed)
                    {
                        OnLog?.Invoke($"[P2P-FILE] Chunk {chunkIndex} processed: {activeTransfer.Progress:F1}% complete");
                    }
                    else
                    {
                        OnLog?.Invoke($"[P2P-FILE] Failed to process chunk {chunkIndex}");
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[P2P-FILE] Error processing binary chunk: {ex.Message}");
            }
        }

        /// <summary>
        /// Calcule le hash d'un chunk pour vérification
        /// </summary>
        private string ComputeChunkHash(byte[] data)
        {
            using var sha = SHA256.Create();
            var hashBytes = sha.ComputeHash(data);
            return Convert.ToBase64String(hashBytes);
        }

        /// <summary>
        /// Obtient tous les transferts actifs avec leurs informations détaillées
        /// </summary>
        public List<object> GetActiveTransfers()
        {
            var activeTransfers = new List<object>();
            
            lock (_lockTransfers)
            {
                foreach (var kvp in _activeTransfers)
                {
                    var transferId = kvp.Key;
                    var transfer = kvp.Value;
                    
                    activeTransfers.Add(new
                    {
                        transferId = transferId,
                        fileName = transfer.Metadata.FileName,
                        fromPeer = transfer.Metadata.FromPeer,
                        toPeer = transfer.Metadata.ToPeer,
                        progress = transfer.Progress,
                        receivedBytes = transfer.ReceivedBytes,
                        totalBytes = transfer.Metadata.FileSize,
                        receivedChunks = transfer.ReceivedChunks.Count,
                        totalChunks = transfer.Metadata.TotalChunks,
                        failedChunks = transfer.FailedChunks.Count,
                        isCompleted = transfer.IsCompleted,
                        isPaused = transfer.IsPaused,
                        startTime = transfer.StartTimeUtc,
                        lastActivity = transfer.LastActivityUtc,
                        outputPath = transfer.OutputPath
                    });
                }
            }
            
            return activeTransfers;
        }
        
        /// <summary>
        /// Adaptation dynamique des paramètres de transfert selon performance réseau
        /// NOUVEAU: Bandwidth adaptation avec optimisation automatique
        /// </summary>
        private async Task AdaptTransferParameters(int chunksSent, int chunkSize, TimeSpan elapsed)
        {
            try
            {
                // Calculer le débit actuel
                double bytesTransferred = chunksSent * chunkSize;
                double throughputKBps = elapsed.TotalSeconds > 0 ? (bytesTransferred / elapsed.TotalSeconds) / 1024 : 0;
                
                _currentThroughputKBps = throughputKBps;
                var previousCondition = _networkCondition;
                _networkCondition = ClassifyNetworkCondition(throughputKBps);
                
                // Adapter les paramètres selon les conditions
                var oldChunkSize = _adaptiveChunkSize;
                var oldDelay = _adaptiveDelay;
                
                switch (_networkCondition)
                {
                    case NetworkCondition.Excellent: // > 1000 KB/s
                        _adaptiveChunkSize = Math.Min(4096, _adaptiveChunkSize + 512); // Augmenter taille chunk
                        _adaptiveDelay = Math.Max(5, _adaptiveDelay - 2);           // Réduire délai
                        break;
                        
                    case NetworkCondition.Good:      // 500-1000 KB/s
                        _adaptiveChunkSize = 3072;  // 3KB optimal
                        _adaptiveDelay = 15;        // Délai modéré
                        break;
                        
                    case NetworkCondition.Fair:      // 100-500 KB/s
                        _adaptiveChunkSize = 2048;  // 2KB standard
                        _adaptiveDelay = 20;        // Délai standard
                        break;
                        
                    case NetworkCondition.Poor:      // 50-100 KB/s
                        _adaptiveChunkSize = Math.Max(1024, _adaptiveChunkSize - 256); // Réduire taille
                        _adaptiveDelay = Math.Min(50, _adaptiveDelay + 5);            // Augmenter délai
                        break;
                        
                    case NetworkCondition.Critical:  // < 50 KB/s
                        _adaptiveChunkSize = 512;   // Chunks minimaux
                        _adaptiveDelay = 100;       // Délai maximal
                        break;
                }
                
                // Log des changements significatifs
                if (Math.Abs(oldChunkSize - _adaptiveChunkSize) >= 256 || Math.Abs(oldDelay - _adaptiveDelay) >= 5 || previousCondition != _networkCondition)
                {
                    OnLog?.Invoke($"🎯 [BANDWIDTH] Adapted: {throughputKBps:F1} KB/s → chunkSize={_adaptiveChunkSize}, delay={_adaptiveDelay}ms, condition={_networkCondition}");
                    OnBandwidthAdapted?.Invoke(throughputKBps, _adaptiveChunkSize, _networkCondition);
                }
                
                // Sauvegarder métriques pour analyse future
                _recentTransfers.Add(new TransferMetrics
                {
                    Timestamp = DateTime.UtcNow,
                    ThroughputKBps = throughputKBps,
                    ChunkSize = chunkSize,
                    Delay = _adaptiveDelay,
                    Condition = _networkCondition
                });
                
                // Garder seulement les 100 dernières mesures
                if (_recentTransfers.Count > 100)
                {
                    _recentTransfers.RemoveRange(0, _recentTransfers.Count - 100);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [BANDWIDTH] Adaptation error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Classifie les conditions réseau selon le débit mesuré
        /// </summary>
        private NetworkCondition ClassifyNetworkCondition(double throughputKBps)
        {
            return throughputKBps switch
            {
                > 1000 => NetworkCondition.Excellent, // > 1 MB/s
                > 500  => NetworkCondition.Good,       // 500 KB/s - 1 MB/s
                > 100  => NetworkCondition.Fair,       // 100-500 KB/s
                > 50   => NetworkCondition.Poor,       // 50-100 KB/s
                _      => NetworkCondition.Critical    // < 50 KB/s
            };
        }
        
        /// <summary>
        /// Obtient les métriques de performance actuelles
        /// </summary>
        public object GetPerformanceMetrics()
        {
            var recentMetrics = _recentTransfers.TakeLast(10).ToList();
            var avgThroughput = recentMetrics.Any() ? recentMetrics.Average(m => m.ThroughputKBps) : 0;
            
            return new
            {
                currentThroughputKBps = _currentThroughputKBps,
                averageThroughputKBps = avgThroughput,
                adaptiveChunkSize = _adaptiveChunkSize,
                adaptiveDelay = _adaptiveDelay,
                networkCondition = _networkCondition.ToString(),
                totalMeasurements = _recentTransfers.Count,
                recentMeasurements = recentMetrics.Count
            };
        }
        
        /// <summary>
        /// Force une réévaluation des conditions réseau
        /// </summary>
        public async Task<NetworkCondition> TestNetworkConditions()
        {
            OnLog?.Invoke($"🧪 [NETWORK-TEST] Testing current network conditions...");
            
            // Simuler un petit transfert test pour mesurer la performance
            var testData = new byte[_adaptiveChunkSize];
            var startTime = DateTime.UtcNow;
            
            // Simulated network test (in real implementation, this would send actual data)
            await Task.Delay(_adaptiveDelay * 3); // Simulate transfer time
            
            var elapsed = DateTime.UtcNow.Subtract(startTime);
            var simulatedThroughput = (testData.Length / elapsed.TotalSeconds) / 1024;
            
            _networkCondition = ClassifyNetworkCondition(simulatedThroughput);
            OnLog?.Invoke($"📊 [NETWORK-TEST] Result: {simulatedThroughput:F1} KB/s → {_networkCondition}");
            
            return _networkCondition;
        }
        
        /// <summary>
        /// Optimise automatiquement les paramètres selon la taille du fichier
        /// </summary>
        private void OptimizeForFileSize(long fileSize)
        {
            if (fileSize < 1024 * 1024) // < 1MB
            {
                OptimizeForTransferType(TransferType.SmallFile);
            }
            else if (fileSize > 10 * 1024 * 1024) // > 10MB
            {
                OptimizeForTransferType(TransferType.LargeFile);
            }
            else
            {
                // Taille moyenne - garder configuration adaptative standard
                OnLog?.Invoke($"📏 [FILE-SIZE] Medium file ({fileSize / 1024}KB) - using adaptive configuration");
            }
        }
        
        /// <summary>
        /// Optimise les paramètres pour un type de transfert spécifique
        /// </summary>
        public void OptimizeForTransferType(TransferType transferType)
        {
            switch (transferType)
            {
                case TransferType.SmallFile: // < 1MB
                    _adaptiveChunkSize = 1024;  // Petits chunks pour réactivité
                    _adaptiveDelay = 10;        // Délai réduit
                    MaxConcurrentChunks = 2;    // Moins de concurrence
                    OnLog?.Invoke($"⚡ [OPTI] Optimized for SMALL files: chunk=1KB, delay=10ms");
                    break;
                    
                case TransferType.LargeFile: // > 10MB
                    _adaptiveChunkSize = 4096;  // Gros chunks pour efficacité
                    _adaptiveDelay = 5;         // Délai minimal
                    MaxConcurrentChunks = 5;    // Plus de concurrence
                    OnLog?.Invoke($"🚀 [OPTI] Optimized for LARGE files: chunk=4KB, delay=5ms");
                    break;
                    
                case TransferType.RealTime: // Chat, messages
                    _adaptiveChunkSize = 512;   // Chunks très petits
                    _adaptiveDelay = 1;         // Délai minimal
                    MaxConcurrentChunks = 1;    // Un seul à la fois
                    OnLog?.Invoke($"⚡ [OPTI] Optimized for REAL-TIME: chunk=512B, delay=1ms");
                    break;
                    
                default:
                    // Configuration standard adaptative
                    break;
            }
        }
    }
    
    /// <summary>
    /// Métriques de transfert pour analyse de performance
    /// </summary>
    public class TransferMetrics
    {
        public DateTime Timestamp { get; set; }
        public double ThroughputKBps { get; set; }
        public int ChunkSize { get; set; }
        public int Delay { get; set; }
        public NetworkCondition Condition { get; set; }
    }
    
    /// <summary>
    /// Conditions réseau détectées
    /// </summary>
    public enum NetworkCondition
    {
        Unknown,
        Critical,   // < 50 KB/s
        Poor,       // 50-100 KB/s
        Fair,       // 100-500 KB/s
        Good,       // 500KB-1MB/s
        Excellent   // > 1 MB/s
    }
    
    /// <summary>
    /// Types de transfert pour optimisation spécifique
    /// </summary>
    public enum TransferType
    {
        SmallFile,  // < 1MB
        LargeFile,  // > 10MB
        RealTime    // Messages temps réel
    }
}