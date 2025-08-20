' ChatP2P.App/RelayHub.vb
Option Strict On
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports ChatP2P.Core
Imports Proto = ChatP2P.App.Protocol.Tags

Namespace ChatP2P.App

    ''' <summary>
    ''' Hub de relais.
    ''' - Peut écouter lui-même en TCP (NetworkStream) via un TcpListener interne.
    ''' - Peut aussi recevoir des connexions déjà établies via INetworkStream (DirectPath / ICE).
    ''' - Conserve l’API attendue par Form1 (events + SendToAsync/BroadcastFromHostAsync/AddClient).
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
        ' Deux “pools” : TCP natif et INetworkStream
        Private ReadOnly _clientsTcp As New Dictionary(Of String, NetworkStream)()
        Private ReadOnly _clientsP2p As New Dictionary(Of String, INetworkStream)()

        Private ReadOnly _listener As TcpListener
        Private ReadOnly _hasListener As Boolean
        Private _cts As CancellationTokenSource

        Public Sub New(port As Integer)
            _listener = New TcpListener(IPAddress.Any, port)
            _hasListener = True
        End Sub

        ''' <summary>
        ''' Constructeur “sans listener” si tu veux seulement AddClient(INetworkStream).
        ''' </summary>
        Public Sub New()
            _hasListener = False
        End Sub

        Public Sub Start()
            _cts = New CancellationTokenSource()
            If _hasListener Then
                _listener.Start()
                RaiseEvent LogLine($"[RelayHub] Écoute TCP sur port {CType(_listener.LocalEndpoint, IPEndPoint).Port}")
                Task.Run(Function() AcceptLoop(_cts.Token))
            Else
                RaiseEvent LogLine("[RelayHub] Démarré (mode sans listener TCP)")
            End If
        End Sub

        Public Sub [Stop]()
            Try
                _cts?.Cancel()
                If _hasListener Then _listener.Stop()
            Catch
            End Try
        End Sub

        ''' <summary>
        ''' Ajoute un client en TCP natif (si tu as déjà un NetworkStream).
        ''' </summary>
        Public Sub AddClient(name As String, stream As NetworkStream)
            If String.IsNullOrWhiteSpace(name) OrElse stream Is Nothing Then Return
            SyncLock _clientsTcp
                _clientsTcp(name) = stream
            End SyncLock
            RaiseEvent LogLine($"[RelayHub] Client TCP ajouté: {name}")
            BroadcastPeers()
            ' boucle de réception
            Dim token = If(_cts IsNot Nothing, _cts.Token, CancellationToken.None)
            Task.Run(Function() ListenClientLoopTcpAsync(stream, name, token))
        End Sub

        ''' <summary>
        ''' Ajoute un client via un INetworkStream (DirectPath/ICE).
        ''' </summary>
        Public Sub AddClient(name As String, stream As INetworkStream)
            If String.IsNullOrWhiteSpace(name) OrElse stream Is Nothing Then Return
            SyncLock _clientsP2p
                _clientsP2p(name) = stream
            End SyncLock
            RaiseEvent LogLine($"[RelayHub] Client P2P ajouté: {name}")
            BroadcastPeers()
            Dim token = If(_cts IsNot Nothing, _cts.Token, CancellationToken.None)
            Task.Run(Function() ListenClientLoopP2pAsync(stream, name, token))
        End Sub

        Private Async Function AcceptLoop(ct As CancellationToken) As Task
            While Not ct.IsCancellationRequested
                Try
                    Dim tcp = Await _listener.AcceptTcpClientAsync()
                    Dim s = tcp.GetStream()
                    Dim clientName = "peer" & Guid.NewGuid().ToString("N")

                    SyncLock _clientsTcp
                        _clientsTcp(clientName) = s
                    End SyncLock

                    RaiseEvent LogLine($"[RelayHub] Nouveau client TCP : {clientName}")
                    Task.Run(Function() ListenClientLoopTcpAsync(s, clientName, ct))
                Catch ex As Exception
                    If Not ct.IsCancellationRequested Then
                        RaiseEvent LogLine($"[RelayHub] Erreur AcceptLoop: {ex.Message}")
                    End If
                End Try
            End While
        End Function

        ' =========================
        ' == Boucle TCP native  ===
        ' =========================
        Private Async Function ListenClientLoopTcpAsync(s As NetworkStream, clientName As String, ct As CancellationToken) As Task
            Dim buffer(8192) As Byte
            Try
                While Not ct.IsCancellationRequested
                    Dim read = Await s.ReadAsync(buffer, 0, buffer.Length, ct)
                    If read <= 0 Then Exit While

                    Dim msg = Encoding.UTF8.GetString(buffer, 0, read)
                    Await HandleInboundAsync(msg, clientName)
                End While
            Catch ex As Exception
                RaiseEvent LogLine($"[RelayHub] {clientName} (TCP) déconnecté: {ex.Message}")
            Finally
                SyncLock _clientsTcp
                    If _clientsTcp.ContainsKey(clientName) Then _clientsTcp.Remove(clientName)
                End SyncLock
                BroadcastPeers()
            End Try
        End Function

        ' =========================
        ' == Boucle INetworkStream
        ' =========================
        Private Async Function ListenClientLoopP2pAsync(s As INetworkStream, clientName As String, ct As CancellationToken) As Task
            Try
                While Not ct.IsCancellationRequested
                    Dim data = Await s.ReceiveAsync(ct)
                    If data Is Nothing OrElse data.Length = 0 Then Exit While

                    Dim msg = Encoding.UTF8.GetString(data)
                    Await HandleInboundAsync(msg, clientName)
                End While
            Catch ex As Exception
                RaiseEvent LogLine($"[RelayHub] {clientName} (P2P) déconnecté: {ex.Message}")
            Finally
                SyncLock _clientsP2p
                    If _clientsP2p.ContainsKey(clientName) Then _clientsP2p.Remove(clientName)
                End SyncLock
                BroadcastPeers()
            End Try
        End Function

        ' =========================
        ' == Traitement générique ==
        ' =========================
        Private Async Function HandleInboundAsync(msg As String, clientName As String) As Task
            RaiseEvent LogLine($"[RelayHub] {clientName} → {Preview(msg)}")

            ' NAME: rename
            If msg.StartsWith(Proto.TAG_NAME) Then
                Dim newName = msg.Substring(Proto.TAG_NAME.Length).Trim()
                If newName <> "" Then
                    ' collision-safe
                    Dim finalName = newName
                    SyncLock _clientsTcp
                        SyncLock _clientsP2p
                            If _clientsTcp.ContainsKey(finalName) OrElse _clientsP2p.ContainsKey(finalName) Then
                                finalName = $"{finalName}_{DateTime.UtcNow.Ticks}"
                            End If
                            ' déplace selon le pool
                            If _clientsTcp.ContainsKey(clientName) Then
                                Dim s = _clientsTcp(clientName)
                                _clientsTcp.Remove(clientName)
                                _clientsTcp(finalName) = s
                            End If
                            If _clientsP2p.ContainsKey(clientName) Then
                                Dim p = _clientsP2p(clientName)
                                _clientsP2p.Remove(clientName)
                                _clientsP2p(finalName) = p
                            End If
                        End SyncLock
                    End SyncLock
                    RaiseEvent LogLine($"[RelayHub] Client renommé → {finalName}")
                    BroadcastPeers()
                    Return
                End If
            End If

            ' PEERS côté client ignoré
            If msg.StartsWith(Proto.TAG_PEERS) Then Return

            ' MSG
            If msg.StartsWith(Proto.TAG_MSG) Then
                Dim parts = msg.Split(":"c, 3)
                If parts.Length >= 3 Then
                    RaiseEvent MessageArrived(parts(1), parts(2))
                End If
                Await BroadcastAsync(msg, clientName)
                Return
            End If

            ' PRIV
            If msg.StartsWith(Proto.TAG_PRIV) Then
                ' PRIV:sender:dest:text
                Dim parts = msg.Split(":"c, 4)
                If parts.Length >= 4 Then
                    Dim sender = parts(1)
                    Dim dest = parts(2)
                    Dim text = parts(3)
                    RaiseEvent PrivateArrived(sender, dest, text)
                    If Not Await TrySendToAsync(dest, msg) Then
                        Await BroadcastAsync(msg, clientName)
                    End If
                End If
                Return
            End If

            ' FICHIERS
            If msg.StartsWith(Proto.TAG_FILEMETA) Then
                RaiseEvent FileSignal("FILEMETA", msg)
                Await RouteFileLikeAsync(msg, clientName, isMeta:=True)
                Return
            End If
            If msg.StartsWith(Proto.TAG_FILECHUNK) Then
                RaiseEvent FileSignal("FILECHUNK", msg)
                Await RouteFileLikeAsync(msg, clientName, isMeta:=False)
                Return
            End If
            If msg.StartsWith(Proto.TAG_FILEEND) Then
                RaiseEvent FileSignal("FILEEND", msg)
                Await RouteFileLikeAsync(msg, clientName, isMeta:=False, endSig:=True)
                Return
            End If

            ' ICE
            If msg.StartsWith(Proto.TAG_ICE_OFFER) Then
                RaiseEvent IceSignal("ICE_OFFER", msg)
                Await BroadcastAsync(msg, clientName)
                Return
            End If
            If msg.StartsWith(Proto.TAG_ICE_ANSWER) Then
                RaiseEvent IceSignal("ICE_ANSWER", msg)
                Await BroadcastAsync(msg, clientName)
                Return
            End If
            If msg.StartsWith(Proto.TAG_ICE_CAND) Then
                RaiseEvent IceSignal("ICE_CAND", msg)
                Await BroadcastAsync(msg, clientName)
                Return
            End If

            ' fallback
            Await BroadcastAsync(msg, clientName)
        End Function

        ' ===== Routing ciblé / broadcast pour string =====
        Private Async Function TrySendToAsync(dest As String, payload As String) As Task(Of Boolean)
            Dim data = Encoding.UTF8.GetBytes(payload)

            ' P2P ?
            Dim p2p As INetworkStream = Nothing
            SyncLock _clientsP2p
                If _clientsP2p.ContainsKey(dest) Then p2p = _clientsP2p(dest)
            End SyncLock
            If p2p IsNot Nothing Then
                Try
                    Await p2p.SendAsync(data, CancellationToken.None)
                    Return True
                Catch
                    Return False
                End Try
            End If

            ' TCP ?
            Dim tcp As NetworkStream = Nothing
            SyncLock _clientsTcp
                If _clientsTcp.ContainsKey(dest) Then tcp = _clientsTcp(dest)
            End SyncLock
            If tcp IsNot Nothing Then
                Try
                    Await tcp.WriteAsync(data, 0, data.Length)
                    Return True
                Catch
                    Return False
                End Try
            End If

            Return False
        End Function

        Private Async Function BroadcastAsync(payload As String, sender As String) As Task
            Dim data = Encoding.UTF8.GetBytes(payload)

            Dim targetsP2p As List(Of INetworkStream)
            Dim targetsTcp As List(Of NetworkStream)

            SyncLock _clientsP2p
                targetsP2p = New List(Of INetworkStream)(
                    _clientsP2p.Where(Function(kv) kv.Key <> sender).Select(Function(kv) kv.Value)
                )
            End SyncLock
            SyncLock _clientsTcp
                targetsTcp = New List(Of NetworkStream)(
                    _clientsTcp.Where(Function(kv) kv.Key <> sender).Select(Function(kv) kv.Value)
                )
            End SyncLock

            For Each s In targetsP2p
                Try : Await s.SendAsync(data, CancellationToken.None) : Catch : End Try
            Next
            For Each s In targetsTcp
                Try : Await s.WriteAsync(data, 0, data.Length) : Catch : End Try
            Next
        End Function

        ' Fichiers : route META au destinataire si possible, sinon broadcast; CHUNK/END broadcast fallback
        Private Async Function RouteFileLikeAsync(msg As String, sender As String, isMeta As Boolean, Optional endSig As Boolean = False) As Task
            If isMeta Then
                Dim parts = msg.Split(":"c, 6) ' FILEMETA:tid:from:dest:filename:size
                If parts.Length >= 6 Then
                    Dim dest = parts(3)
                    If Await TrySendToAsync(dest, msg) Then Return
                End If
            End If
            Await BroadcastAsync(msg, sender)
        End Function

        Private Sub BroadcastPeers()
            Dim peers As New List(Of String)
            SyncLock _clientsTcp
                peers.AddRange(_clientsTcp.Keys)
            End SyncLock
            SyncLock _clientsP2p
                peers.AddRange(_clientsP2p.Keys)
            End SyncLock

            ' ajouter le host en tête
            peers.Insert(0, HostDisplayName)

            Dim peersStr = String.Join(";", peers)
            Dim msg = Proto.TAG_PEERS & peersStr

            RaiseEvent LogLine($"[RelayHub] Peers → {peersStr}")
            RaiseEvent PeerListUpdated(New List(Of String)(peers))

            Try
                ' informer les clients
                BroadcastAsync(msg, sender:="").Wait()
            Catch
            End Try
        End Sub

        ' ======== API attendue par Form1 ========

        ''' <summary>Envoi ciblé à un destinataire par nom (payload string).</summary>
        Public Async Function SendToAsync(dest As String, payload As String) As Task
            If Not Await TrySendToAsync(dest, payload) Then
                RaiseEvent LogLine($"[RelayHub] Destinataire '{dest}' introuvable (SendToAsync).")
            End If
        End Function

        ''' <summary>Broadcast depuis le host à tous les clients (payload string).</summary>
        Public Async Function BroadcastFromHostAsync(payload As String) As Task
            Await BroadcastAsync(payload, sender:="")
        End Function

        ' ======== Outils ========
        Private Shared Function Preview(s As String) As String
            If String.IsNullOrEmpty(s) Then Return ""
            If s.Length <= 160 Then Return s
            Return s.Substring(0, 160) & "..."
        End Function

    End Class
End Namespace
