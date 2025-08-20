' ChatP2P.App/RelayHub.vb
Option Strict On
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports Proto = ChatP2P.App.Protocol.Tags

Namespace ChatP2P.App

    ''' <summary>
    ''' Hub TCP (côté host) pour relayer messages publics/privés, fichiers et signaux ICE.
    ''' API "compat" pour Form1 : HostDisplayName, SendToAsync, BroadcastFromHostAsync,
    ''' événements PeerListUpdated, LogLine, MessageArrived, PrivateArrived, FileSignal, IceSignal, etc.
    ''' </summary>
    Public Class RelayHub

        ' ==== API exposée à l'UI ====
        Public Property HostDisplayName As String = "Host"

        Public Event PeerListUpdated(peers As List(Of String))
        Public Event LogLine(line As String)
        Public Event MessageArrived(sender As String, text As String)
        Public Event PrivateArrived(sender As String, dest As String, text As String)
        ' kind: "FILEMETA" | "FILECHUNK" | "FILEEND"
        Public Event FileSignal(kind As String, payload As String)
        ' kind: "ICE_OFFER" | "ICE_ANSWER" | "ICE_CAND"
        Public Event IceSignal(kind As String, payload As String)

        ' ==== Impl interne ====
        Private ReadOnly _clients As New Dictionary(Of String, NetworkStream)()
        Private ReadOnly _listener As TcpListener
        Private _cts As CancellationTokenSource

        Public Sub New(port As Integer)
            _listener = New TcpListener(IPAddress.Any, port)
        End Sub

        Public Sub Start()
            _cts = New CancellationTokenSource()
            _listener.Start()
            RaiseEvent LogLine($"[RelayHub] Écoute sur port {CType(_listener.LocalEndpoint, IPEndPoint).Port}")
            Task.Run(Function() AcceptLoop(_cts.Token))
        End Sub

        Public Sub [Stop]()
            Try
                _cts?.Cancel()
                _listener.Stop()
            Catch
            End Try
        End Sub

        ''' <summary>
        ''' Ajoute manuellement un client existant (si tu as déjà un TcpClient ailleurs).
        ''' Optionnel : Form1 peut ne jamais l’appeler si on laisse le hub accepter lui-même.
        ''' </summary>
        Public Sub AddClient(name As String, stream As NetworkStream)
            If String.IsNullOrWhiteSpace(name) OrElse stream Is Nothing Then Return
            SyncLock _clients
                _clients(name) = stream
            End SyncLock
            RaiseEvent LogLine($"[RelayHub] Client ajouté manuellement: {name}")
            BroadcastPeers()
        End Sub

        Private Async Function AcceptLoop(ct As CancellationToken) As Task
            While Not ct.IsCancellationRequested
                Try
                    Dim client = Await _listener.AcceptTcpClientAsync()
                    Dim s = client.GetStream()
                    Dim clientName = "peer" & Guid.NewGuid().ToString("N")

                    SyncLock _clients
                        _clients(clientName) = s
                    End SyncLock

                    RaiseEvent LogLine($"[RelayHub] Nouveau client connecté : {clientName}")

                    ' Fire & forget robuste
                    Task.Run(Function() ListenClientLoopAsync(s, clientName, ct))
                Catch ex As Exception
                    If Not ct.IsCancellationRequested Then
                        RaiseEvent LogLine($"[RelayHub] Erreur AcceptLoop: {ex.Message}")
                    End If
                End Try
            End While
        End Function

        Private Async Function ListenClientLoopAsync(s As NetworkStream, clientName As String, ct As CancellationToken) As Task
            Dim buffer(8192) As Byte
            Try
                While Not ct.IsCancellationRequested
                    Dim read = Await s.ReadAsync(buffer, 0, buffer.Length, ct)
                    If read <= 0 Then Exit While

                    Dim msg = Encoding.UTF8.GetString(buffer, 0, read)
                    RaiseEvent LogLine($"[RelayHub] {clientName} → {Preview(msg)}")

                    ' Gestion rename
                    If msg.StartsWith(Proto.TAG_NAME) Then
                        Dim newName = msg.Substring(Proto.TAG_NAME.Length).Trim()
                        If Not String.IsNullOrEmpty(newName) Then
                            SyncLock _clients
                                _clients.Remove(clientName)
                                ' collision ?
                                Dim finalName = newName
                                If _clients.ContainsKey(finalName) Then
                                    finalName = $"{finalName}_{DateTime.UtcNow.Ticks}"
                                End If
                                _clients(finalName) = s
                                clientName = finalName
                            End SyncLock
                            RaiseEvent LogLine($"[RelayHub] Client renommé en {clientName}")
                            BroadcastPeers()
                        End If
                        Continue While
                    End If

                    ' Ignorer PEERS clients (l'annonce officielle vient du hub)
                    If msg.StartsWith(Proto.TAG_PEERS) Then Continue While

                    ' Messages publics
                    If msg.StartsWith(Proto.TAG_MSG) Then
                        ' MSG:sender:text
                        Dim parts = msg.Split(":"c, 3)
                        If parts.Length >= 3 Then
                            Dim sender = parts(1)
                            Dim text = parts(2)
                            RaiseEvent MessageArrived(sender, text)
                        End If
                        ' re-broadcast aux autres
                        Await BroadcastAsync(msg, clientName)
                        Continue While
                    End If

                    ' Messages privés
                    If msg.StartsWith(Proto.TAG_PRIV) Then
                        ' PRIV:sender:dest:text
                        Dim parts = msg.Split(":"c, 4)
                        If parts.Length >= 4 Then
                            Dim sender = parts(1)
                            Dim dest = parts(2)
                            Dim text = parts(3)
                            RaiseEvent PrivateArrived(sender, dest, text)

                            ' Envoi ciblé si possible, sinon broadcast fallback
                            If Not Await TrySendToAsync(dest, msg) Then
                                Await BroadcastAsync(msg, clientName)
                            End If
                        End If
                        Continue While
                    End If

                    ' Fichiers
                    If msg.StartsWith(Proto.TAG_FILEMETA) Then
                        RaiseEvent FileSignal("FILEMETA", msg)
                        Await RouteFileLikeAsync(msg, clientName, isMeta:=True)
                        Continue While
                    End If
                    If msg.StartsWith(Proto.TAG_FILECHUNK) Then
                        RaiseEvent FileSignal("FILECHUNK", msg)
                        Await RouteFileLikeAsync(msg, clientName, isMeta:=False)
                        Continue While
                    End If
                    If msg.StartsWith(Proto.TAG_FILEEND) Then
                        RaiseEvent FileSignal("FILEEND", msg)
                        Await RouteFileLikeAsync(msg, clientName, isMeta:=False, endSig:=True)
                        Continue While
                    End If

                    ' ICE
                    If msg.StartsWith(Proto.TAG_ICE_OFFER) Then
                        RaiseEvent IceSignal("ICE_OFFER", msg)
                        Await BroadcastAsync(msg, clientName)
                        Continue While
                    End If
                    If msg.StartsWith(Proto.TAG_ICE_ANSWER) Then
                        RaiseEvent IceSignal("ICE_ANSWER", msg)
                        Await BroadcastAsync(msg, clientName)
                        Continue While
                    End If
                    If msg.StartsWith(Proto.TAG_ICE_CAND) Then
                        RaiseEvent IceSignal("ICE_CAND", msg)
                        Await BroadcastAsync(msg, clientName)
                        Continue While
                    End If

                    ' par défaut, rebroadcast
                    Await BroadcastAsync(msg, clientName)
                End While
            Catch ex As Exception
                RaiseEvent LogLine($"[RelayHub] {clientName} déconnecté: {ex.Message}")
            Finally
                SyncLock _clients
                    If _clients.ContainsKey(clientName) Then _clients.Remove(clientName)
                End SyncLock
                BroadcastPeers()
            End Try
        End Function

        ' Envoi ciblé (utilisé par PRIV et fichiers) — retourne False si destinataire introuvable
        Private Async Function TrySendToAsync(dest As String, payload As String) As Task(Of Boolean)
            Dim target As NetworkStream = Nothing
            SyncLock _clients
                If _clients.ContainsKey(dest) Then target = _clients(dest)
            End SyncLock
            If target Is Nothing Then Return False
            Dim data = Encoding.UTF8.GetBytes(payload)
            Try
                Await target.WriteAsync(data, 0, data.Length)
                Return True
            Catch
                Return False
            End Try
        End Function

        ' Routing des fichiers: FILEMETA contient le dest, CHUNK/END utilisent une table logique (ici on route en broadcast si on ne peut pas déduire)
        Private Async Function RouteFileLikeAsync(msg As String, sender As String, isMeta As Boolean, Optional endSig As Boolean = False) As Task
            If isMeta Then
                ' FILEMETA:tid:from:dest:filename:size
                Dim parts = msg.Split(":"c, 6)
                If parts.Length >= 6 Then
                    Dim dest = parts(3)
                    If Await TrySendToAsync(dest, msg) Then Return
                End If
            End If
            ' fallback : broadcast aux autres
            Await BroadcastAsync(msg, sender)
        End Function

        Private Async Function BroadcastAsync(msg As String, sender As String) As Task
            Dim data = Encoding.UTF8.GetBytes(msg)
            Dim targets As New List(Of NetworkStream)
            SyncLock _clients
                For Each kvp In _clients
                    If kvp.Key <> sender Then targets.Add(kvp.Value)
                Next
            End SyncLock

            For Each t In targets
                Try
                    Await t.WriteAsync(data, 0, data.Length)
                Catch
                End Try
            Next
        End Function

        Private Sub BroadcastPeers()
            Dim peers As New List(Of String)
            SyncLock _clients
                peers.Add(HostDisplayName)
                peers.AddRange(_clients.Keys)
            End SyncLock

            Dim peersStr = String.Join(";", peers)
            Dim msg = Proto.TAG_PEERS & peersStr

            RaiseEvent LogLine($"[RelayHub] Peers → {peersStr}")
            RaiseEvent PeerListUpdated(New List(Of String)(peers))

            Try
                ' on envoie aux clients (host n’a pas besoin de s’envoyer à lui-même)
                BroadcastAsync(msg, "").Wait()
            Catch
            End Try
        End Sub

        ' ======== API attendue par Form1 ========

        ''' <summary>Envoi ciblé à un destinataire par nom.</summary>
        Public Async Function SendToAsync(dest As String, payload As String) As Task
            If Not Await TrySendToAsync(dest, payload) Then
                RaiseEvent LogLine($"[RelayHub] Destinataire '{dest}' introuvable (SendToAsync).")
            End If
        End Function

        ''' <summary>Broadcast depuis le host à tous les clients.</summary>
        Public Async Function BroadcastFromHostAsync(payload As String) As Task
            Await BroadcastAsync(payload, sender:="")
        End Function

        ' ======== Outils ========
        Private Shared Function Preview(s As String) As String
            If String.IsNullOrEmpty(s) Then Return ""
            If s.Length <= 120 Then Return s
            Return s.Substring(0, 120) & "..."
        End Function

    End Class
End Namespace
