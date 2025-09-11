' ChatP2P.Core/P2PFileTransfer.vb
Option Strict On
Imports System
Imports System.Collections.Generic
Imports System.Security.Cryptography
Imports System.IO
Imports System.Threading.Tasks

Namespace ChatP2P.Core
    ''' <summary>
    ''' Système de transfert P2P BitTorrent-like avec hash et reprise automatique.
    ''' Conçu pour contourner le crash 400 chunks de WebRTC.
    ''' </summary>
    Public Class P2PFileTransfer
        
        ''' <summary>
        ''' Chunk de fichier avec hash pour vérification d'intégrité
        ''' </summary>
        Public Class FileChunk
            Public Property Index As Integer
            Public Property Hash As String
            Public Property Data As Byte()
            Public Property Confirmed As Boolean = False
            Public Property RetryCount As Integer = 0
            Public Property LastSentUtc As DateTime = DateTime.MinValue
            
            Public Sub New(index As Integer, data As Byte())
                Me.Index = index
                Me.Data = data
                Me.Hash = ComputeHash(data)
            End Sub
            
            Private Shared Function ComputeHash(data As Byte()) As String
                Using sha = SHA256.Create()
                    Dim hashBytes = sha.ComputeHash(data)
                    Return Convert.ToBase64String(hashBytes)
                End Using
            End Function
            
            Public Function VerifyHash() As Boolean
                Return Hash = ComputeHash(Data)
            End Function
        End Class
        
        ''' <summary>
        ''' Métadonnées du fichier à transférer
        ''' </summary>
        Public Class FileMetadata
            Public Property TransferId As String
            Public Property FileName As String
            Public Property FileSize As Long
            Public Property ChunkSize As Integer
            Public Property TotalChunks As Integer
            Public Property FileHash As String
            
            Public Sub New(transferId As String, fileName As String, fileSize As Long, chunkSize As Integer)
                Me.TransferId = transferId
                Me.FileName = fileName
                Me.FileSize = fileSize
                Me.ChunkSize = chunkSize
                Me.TotalChunks = CInt(Math.Ceiling(fileSize / CDbl(chunkSize)))
            End Sub
        End Class
        
        ''' <summary>
        ''' État d'un transfert en cours
        ''' </summary>
        Public Class TransferState
            Public Property Metadata As FileMetadata
            Public Property Chunks As Dictionary(Of Integer, FileChunk)
            Public Property ReceivedChunks As HashSet(Of Integer)
            Public Property PendingChunks As Queue(Of Integer)
            Public Property FailedChunks As HashSet(Of Integer)
            Public Property OutputFile As FileStream
            Public Property OutputPath As String
            Public Property StartTimeUtc As DateTime
            Public Property LastActivityUtc As DateTime
            Public Property IsCompleted As Boolean = False
            Public Property IsPaused As Boolean = False
            
            Public Sub New(metadata As FileMetadata, outputPath As String)
                Me.Metadata = metadata
                Me.OutputPath = outputPath
                Me.Chunks = New Dictionary(Of Integer, FileChunk)()
                Me.ReceivedChunks = New HashSet(Of Integer)()
                Me.PendingChunks = New Queue(Of Integer)()
                Me.FailedChunks = New HashSet(Of Integer)()
                Me.StartTimeUtc = DateTime.UtcNow
                Me.LastActivityUtc = DateTime.UtcNow
                
                ' Initialiser la queue avec tous les chunks à recevoir
                For i = 0 To metadata.TotalChunks - 1
                    PendingChunks.Enqueue(i)
                Next
            End Sub
            
            Public ReadOnly Property Progress As Double
                Get
                    If Metadata.TotalChunks = 0 Then Return 0
                    Return (ReceivedChunks.Count / CDbl(Metadata.TotalChunks)) * 100
                End Get
            End Property
            
            Public ReadOnly Property ReceivedBytes As Long
                Get
                    Return ReceivedChunks.Count * Metadata.ChunkSize
                End Get
            End Property
        End Class
        
        ''' <summary>
        ''' Gestionnaire principal des transferts P2P
        ''' </summary>
        Public Class P2PTransferManager
            Private ReadOnly _activeTransfers As New Dictionary(Of String, TransferState)
            Private ReadOnly _lockTransfers As New Object()
            
            ' Configuration
            Public Property MaxConcurrentChunks As Integer = 3
            Public Property ChunkTimeoutMs As Integer = 10000
            Public Property MaxRetries As Integer = 5
            Public Property ChunkSize As Integer = 4096  ' Plus petit pour éviter le crash
            
            ' Événements
            Public Event OnTransferProgress(transferId As String, progress As Double, receivedBytes As Long, totalBytes As Long)
            Public Event OnTransferCompleted(transferId As String, success As Boolean, outputPath As String)
            Public Event OnChunkReceived(transferId As String, chunkIndex As Integer, hash As String)
            Public Event OnLog(message As String)
            
            ''' <summary>
            ''' Démarre un nouveau transfert de réception
            ''' </summary>
            Public Function StartReceiveTransfer(metadata As FileMetadata, outputPath As String) As Boolean
                Try
                    SyncLock _lockTransfers
                        If _activeTransfers.ContainsKey(metadata.TransferId) Then
                            RaiseEvent OnLog($"[P2P TRANSFER] Transfert {metadata.TransferId} déjà en cours")
                            Return False
                        End If
                        
                        Dim transfer As New TransferState(metadata, outputPath)
                        
                        ' Créer le fichier de sortie
                        transfer.OutputFile = New FileStream(outputPath, FileMode.Create, FileAccess.Write)
                        transfer.OutputFile.SetLength(metadata.FileSize)
                        
                        _activeTransfers(metadata.TransferId) = transfer
                        
                        RaiseEvent OnLog($"[P2P TRANSFER] Nouveau transfert: {metadata.FileName} ({metadata.FileSize} bytes, {metadata.TotalChunks} chunks)")
                        
                        Return True
                    End SyncLock
                    
                Catch ex As Exception
                    RaiseEvent OnLog($"[P2P TRANSFER] Erreur démarrage transfert: {ex.Message}")
                    Return False
                End Try
            End Function
            
            ''' <summary>
            ''' Traite la réception d'un chunk
            ''' </summary>
            Public Function ProcessReceivedChunk(transferId As String, chunkIndex As Integer, chunkHash As String, chunkData As Byte()) As Boolean
                Try
                    SyncLock _lockTransfers
                        If Not _activeTransfers.ContainsKey(transferId) Then
                            RaiseEvent OnLog($"[P2P TRANSFER] Transfert {transferId} introuvable")
                            Return False
                        End If
                        
                        Dim transfer = _activeTransfers(transferId)
                        
                        ' Vérifier si on a déjà ce chunk
                        If transfer.ReceivedChunks.Contains(chunkIndex) Then
                            RaiseEvent OnLog($"[P2P TRANSFER] Chunk {chunkIndex} déjà reçu (ignoré)")
                            Return True
                        End If
                        
                        ' Créer le chunk et vérifier son hash
                        Dim chunk As New FileChunk(chunkIndex, chunkData)
                        If chunk.Hash <> chunkHash Then
                            RaiseEvent OnLog($"[P2P TRANSFER] Hash invalide pour chunk {chunkIndex}")
                            Return False
                        End If
                        
                        ' Écrire le chunk dans le fichier à la bonne position
                        Dim position As Long = chunkIndex * transfer.Metadata.ChunkSize
                        transfer.OutputFile.Seek(position, SeekOrigin.Begin)
                        transfer.OutputFile.Write(chunkData, 0, chunkData.Length)
                        transfer.OutputFile.Flush()
                        
                        ' Marquer comme reçu
                        transfer.Chunks(chunkIndex) = chunk
                        transfer.ReceivedChunks.Add(chunkIndex)
                        transfer.LastActivityUtc = DateTime.UtcNow
                        
                        RaiseEvent OnChunkReceived(transferId, chunkIndex, chunkHash)
                        RaiseEvent OnTransferProgress(transferId, transfer.Progress, transfer.ReceivedBytes, transfer.Metadata.FileSize)
                        
                        ' Vérifier si le transfert est terminé
                        If transfer.ReceivedChunks.Count = transfer.Metadata.TotalChunks Then
                            CompleteTransfer(transferId, transfer)
                        End If
                        
                        Return True
                    End SyncLock
                    
                Catch ex As Exception
                    RaiseEvent OnLog($"[P2P TRANSFER] Erreur traitement chunk: {ex.Message}")
                    Return False
                End Try
            End Function
            
            ''' <summary>
            ''' Finalise un transfert terminé
            ''' </summary>
            Private Sub CompleteTransfer(transferId As String, transfer As TransferState)
                Try
                    transfer.OutputFile?.Close()
                    transfer.IsCompleted = True
                    
                    RaiseEvent OnLog($"[P2P TRANSFER] Transfert {transferId} terminé: {transfer.Metadata.FileName}")
                    RaiseEvent OnTransferCompleted(transferId, True, transfer.OutputPath)
                    
                    _activeTransfers.Remove(transferId)
                    
                Catch ex As Exception
                    RaiseEvent OnLog($"[P2P TRANSFER] Erreur finalisation: {ex.Message}")
                    RaiseEvent OnTransferCompleted(transferId, False, transfer.OutputPath)
                End Try
            End Sub
            
            ''' <summary>
            ''' Obtient la liste des chunks manquants pour demander retransmission
            ''' </summary>
            Public Function GetMissingChunks(transferId As String, maxCount As Integer) As List(Of Integer)
                Dim missing As New List(Of Integer)()
                
                SyncLock _lockTransfers
                    If Not _activeTransfers.ContainsKey(transferId) Then Return missing
                    
                    Dim transfer = _activeTransfers(transferId)
                    Dim count = 0
                    
                    ' Prioriser les chunks qui ont échoué mais avec retry
                    For Each failedChunk In transfer.FailedChunks
                        If count >= maxCount Then Exit For
                        missing.Add(failedChunk)
                        count += 1
                    Next
                    
                    ' Puis prendre des chunks en attente
                    While transfer.PendingChunks.Count > 0 AndAlso count < maxCount
                        missing.Add(transfer.PendingChunks.Dequeue())
                        count += 1
                    End While
                End SyncLock
                
                Return missing
            End Function
            
            ''' <summary>
            ''' Obtient un transfert actif par son ID
            ''' </summary>
            Public Function GetActiveTransfer(transferId As String) As TransferState
                SyncLock _lockTransfers
                    If _activeTransfers.ContainsKey(transferId) Then
                        Return _activeTransfers(transferId)
                    End If
                    Return Nothing
                End SyncLock
            End Function
            
            ''' <summary>
            ''' Nettoie les transferts inactifs ou en timeout
            ''' </summary>
            Public Sub CleanupTransfers()
                Dim toRemove As New List(Of String)()
                Dim now = DateTime.UtcNow
                
                SyncLock _lockTransfers
                    For Each kvp In _activeTransfers
                        Dim transfer = kvp.Value
                        Dim inactiveTime = now.Subtract(transfer.LastActivityUtc)
                        
                        ' Timeout après 5 minutes d'inactivité
                        If inactiveTime.TotalMinutes > 5 Then
                            toRemove.Add(kvp.Key)
                            RaiseEvent OnLog($"[P2P TRANSFER] Timeout transfert {kvp.Key}")
                        End If
                    Next
                    
                    For Each transferId In toRemove
                        Dim transfer = _activeTransfers(transferId)
                        transfer.OutputFile?.Close()
                        RaiseEvent OnTransferCompleted(transferId, False, transfer.OutputPath)
                        _activeTransfers.Remove(transferId)
                    Next
                End SyncLock
            End Sub
            
            ''' <summary>
            ''' Obtient les statistiques d'un transfert
            ''' </summary>
            Public Function GetTransferStats(transferId As String) As String
                SyncLock _lockTransfers
                    If Not _activeTransfers.ContainsKey(transferId) Then Return "Transfert introuvable"
                    
                    Dim transfer = _activeTransfers(transferId)
                    Dim elapsed = DateTime.UtcNow.Subtract(transfer.StartTimeUtc)
                    Dim speedKBps = If(elapsed.TotalSeconds > 0, transfer.ReceivedBytes / elapsed.TotalSeconds / 1024, 0)
                    
                    Return $"Progress: {transfer.Progress:F1}% ({transfer.ReceivedChunks.Count}/{transfer.Metadata.TotalChunks}), " &
                           $"Speed: {speedKBps:F1} KB/s, Failed: {transfer.FailedChunks.Count}"
                End SyncLock
            End Function
            
        End Class
        
    End Class
End Namespace