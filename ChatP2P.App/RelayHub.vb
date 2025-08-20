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
    ''' Hub de relay mixte :
    ''' - Mode P2P : on ajoute des INetworkStream existants (DirectPath, ICE, etc.) via AddClient.
    ''' - Mode TCP facultatif : new RelayHub(port) + Start() ouvre un TcpListener interne (debug / clients bruts).
    ''' 
    ''' Gère :
    '''  - TAG_NAME (rename), TAG_MSG, TAG_PRIV,
    '''  - TAG_FILEMETA/CHUNK/END (forward + event FileSignal pour l’UI si besoin),
    '''  - TAG_ICE_OFFER/ANSWER/CAND (broadcast + event IceSignal(kind, payload)).
    '''  - TAG_PEERS construit et envoyé à tous (et PeerListUpdated pour l’UI).
    ''' </summary>
    Public Class RelayHub

        ' === Events côté UI ===
        Public Event PeerListUpdated(names As List(Of String))
        Public Event LogLine(line As String)
        Public Event MessageArrived(sender As String, text As String)
        Public Event PrivateArrived(sender As String, dest As String, text As String)
        Public Event FileSignal(raw As String) ' brut pour l’UI si destinataire = host
        Public Event IceSignal(kind As String, payload As String)

        ' Le nom d’affichage de l’host (pour décider si certains messages me sont destinés)
        Public Property HostDisplayName As String = "Host"

        ' === Stocks de clients P2P (INetworkStream) ===
        Private ReadOnly _clientsP2p As New Dictionary(Of String, INetworkStream)
        Private ReadOnly _revP2p As New Dictionary(Of INetworkStream, String)

        ' === Facultatif: clients TCP (NetworkStream) + listener interne ===
        Private ReadOnly _clientsTcp As New Dictionary(Of String, NetworkStream)
        Private ReadOnly _revTcp As New Dictionary(Of NetworkStream, String)
        Private _listener As TcpListener
        Private _cts As CancellationTokenSource

        ' --- Constructeurs ---
        Public Sub New()
            ' Mode P2P uniquement (pas de listener interne)
        End Sub

        Public Sub New(port As Integer)
            _listener = New TcpListener(IPAddress.Any, port)
        End Sub

        ' --- Démarrage/Arrêt listener TCP ---
        Public Sub Start()
            If _listener Is Nothing Then
                RaiseEvent LogLine("[Hub] Aucun listener TCP (mode P2P).")
                Return
            End If
            _cts = New CancellationTokenSource()
            _listener.Start()
            RaiseEvent LogLine("[Hub] TcpListener démarré.")
            Task.Run(Function() AcceptLoop(_cts.Token))
        End Sub

        Public Sub [Stop]()
            Try
                _cts?.Cancel()
                _listener?.Stop()
            Catch
            End Try
        End Sub

        ' --- API P2P ---
        Public Sub AddClient(defaultName As String, s As INetworkStream)
            If s Is Nothing Then Return
            Dim name As String
            SyncLock _clientsP2p
                name = defaultName
                Dim i = 1
                While _clientsP2p.ContainsKey(name)
                    i += 1
                    name = defaultName & i.ToString()
                End While
                _clientsP2p(name) = s
                _revP2p(s) = name
            End SyncLock
            RaiseEvent LogLine($"[Hub] P2P client ajouté: {name}")
            ' Écoute asynchrone fire-and-forget
            Task.Run(Function() ListenClientLoopP2pAsync(s, name, CancellationToken.None))
            BroadcastPeers()
        End Sub

        ' --- Envoi depuis l’host à tous (relay) ---
        Public Async Function BroadcastFromHostAsync(payload As String) As Task
            Dim data = Encoding.UTF8.GetBytes(payload)
            Await BroadcastFromHostAsync(data)
        End Function

        Public Async Function BroadcastFromHostAsync(data As Byte()) As Task
            ' Envoie à TOUS les clients (P2P+TCP)
            Dim targetsP As List(Of INetworkStream)
            Dim targetsT As List(Of NetworkStream)

            SyncLock _clientsP2p
                targetsP = New List(Of INetworkStream)(_clientsP2p.Values)
            End SyncLock
            SyncLock _clientsTcp
                targetsT = New List(Of NetworkStream)(_clientsTcp.Values)
            End SyncLock

            For Each s In targetsP
                Try
                    Await s.SendAsync(data, CancellationToken.None)
                Catch
                End Try
            Next
            For Each ns In targetsT
                Try
                    Await ns.WriteAsync(data, 0, data.Length)
                Catch
                End Try
            Next
        End Function

        ' --- Envoi ciblé ---
        Public Async Function SendToAsync(dest As String, payload As String) As Task
            Dim data = Encoding.UTF8.GetBytes(payload)
            Await SendToAsync(dest, data)
        End Function

        Public Async Function SendToAsync(dest As String, data As Byte()) As Task
            If String.IsNullOrWhiteSpace(dest) Then Return

            Dim p2p As INetworkStream = Nothing
            Dim tcp As NetworkStream = Nothing

            SyncLock _clientsP2p
                If _clientsP2p.ContainsKey(dest) Then p2p = _clientsP2p(dest)
            End SyncLock
            If p2p IsNot Nothing Then
                Try
                    Await p2p.SendAsync(data, CancellationToken.None)
                Catch
                End Try
                Return
            End If

            SyncLock _clientsTcp
                If _clientsTcp.ContainsKey(dest) Then tcp = _clientsTcp(dest)
            End SyncLock
            If tcp IsNot Nothing Then
                Try
                    Await tcp.WriteAsync(data, 0, data.Length)
                Catch
                End Try
            End If
        End Function

        ' ====================== LISTENERS ======================

        ' Accept loop TCP
        Private Async Function AcceptLoop(ct As CancellationToken) As Task
            While Not ct.IsCancellationRequested
                Try
                    Dim client = Await _listener.AcceptTcpClientAsync()
                    Dim s = client.GetStream()
                    Dim clientName = "peer" & Guid.NewGuid().ToString("N")

                    SyncLock _clientsTcp
                        _clientsTcp(clientName) = s
                        _revTcp(s) = clientName
                    End SyncLock

                    RaiseEvent LogLine($"[Hub] Nouveau client TCP : {clientName}")
                    Task.Run(Function() ListenClientLoopTcpAsync(s, clientName, ct))
                    BroadcastPeers()
                Catch ex As Exception
                    If Not ct.IsCancellationRequested Then
                        RaiseEvent LogLine($"[Hub] Erreur AcceptLoop: {ex.Message}")
                    End If
                End Try
            End While
        End Function

        ' Boucle P2P
        Private Async Function ListenClientLoopP2pAsync(s As INetworkStream, clientName As String, ct As CancellationToken) As Task
            Try
                While Not ct.IsCancellationRequested
                    Dim data = Await s.ReceiveAsync(ct)
                    If data Is Nothing OrElse data.Length = 0 Then Exit While
                    Dim msg = Encoding.UTF8.GetString(data)
                    Await HandleInboundAsync(msg, clientName, isTcp:=False)
                End While
            Catch ex As Exception
                RaiseEvent LogLine($"[Hub] {clientName} (P2P) déconnecté: {ex.Message}")
            Finally
                SyncLock _clientsP2p
                    If _clientsP2p.ContainsKey(clientName) Then _clientsP2p.Remove(clientName)
                    If _revP2p.ContainsKey(s) Then _revP2p.Remove(s)
                End SyncLock
                BroadcastPeers()
            End Try
        End Function

        ' Boucle TCP
        Private Async Function ListenClientLoopTcpAsync(ns As NetworkStream, clientName As String, ct As CancellationToken) As Task
            Dim buffer(4096) As Byte
            Try
                While Not ct.IsCancellationRequested
                    Dim read = Await ns.ReadAsync(buffer, 0, buffer.Length, ct)
                    If read <= 0 Then Exit While
                    Dim msg = Encoding.UTF8.GetString(buffer, 0, read)
                    Await HandleInboundAsync(msg, clientName, isTcp:=True)
                End While
            Catch ex As Exception
                RaiseEvent LogLine($"[Hub] {clientName} (TCP) déconnecté: {ex.Message}")
            Finally
                SyncLock _clientsTcp
                    If _clientsTcp.ContainsKey(clientName) Then _clientsTcp.Remove(clientName)
                    If _revTcp.ContainsKey(ns) Then _revTcp.Remove(ns)
                End SyncLock
                BroadcastPeers()
            End Try
        End Function

        ' ====================== ROUTAGE ======================

        Private Async Function HandleInboundAsync(msg As String, senderName As String, isTcp As Boolean) As Task
            Try
                ' --- RENOMMAGE ---
                If msg.StartsWith(Proto.TAG_NAME) Then
                    Dim newName = msg.Substring(Proto.TAG_NAME.Length).Trim()
                    If newName <> "" Then
                        If isTcp Then
                            SyncLock _clientsTcp
                                If _clientsTcp.ContainsKey(senderName) Then
                                    Dim s = _clientsTcp(senderName)
                                    _clientsTcp.Remove(senderName)
                                    Dim finalName = GetUniqueName(newName)
                                    _clientsTcp(finalName) = s
                                    _revTcp(s) = finalName
                                    senderName = finalName
                                End If
                            End SyncLock
                        Else
                            SyncLock _clientsP2p
                                If _clientsP2p.ContainsKey(senderName) Then
                                    Dim s = _clientsP2p(senderName)
                                    _clientsP2p.Remove(senderName)
                                    Dim finalName = GetUniqueName(newName)
                                    _clientsP2p(finalName) = s
                                    _revP2p(s) = finalName
                                    senderName = finalName
                                End If
                            End SyncLock
                        End If
                        RaiseEvent LogLine($"[Hub] Rename → {senderName}")
                        BroadcastPeers()
                    End If
                    Return
                End If

                ' --- PEERS (ignoré si venant d’un client) ---
                If msg.StartsWith(Proto.TAG_PEERS) Then
                    Return
                End If

                ' --- MESSAGES PUBLICS ---
                If msg.StartsWith(Proto.TAG_MSG) Then
                    Dim text = msg.Substring(Proto.TAG_MSG.Length)
                    RaiseEvent MessageArrived(senderName, text)
                    Await BroadcastExceptAsync(msg, senderName)
                    Return
                End If

                ' --- MESSAGES PRIVÉS ---
                If msg.StartsWith(Proto.TAG_PRIV) Then
                    ' PRIV:sender:dest:message (dans Form1 on fabrique PRIV:local:dest:body)
                    Dim parts = msg.Split(":"c, 4)
                    If parts.Length >= 4 Then
                        Dim fromName = parts(1)
                        Dim dest = parts(2)
                        Dim body = parts(3)
                        If String.Equals(dest, HostDisplayName, StringComparison.OrdinalIgnoreCase) Then
                            ' l’host est la cible → on remonte à l’UI
                            RaiseEvent PrivateArrived(fromName, dest, body)
                        Else
                            ' forward
                            Await SendToAsync(dest, msg)
                        End If
                    End If
                    Return
                End If

                ' --- FICHIERS ---
                If msg.StartsWith(Proto.TAG_FILEMETA) _
                    OrElse msg.StartsWith(Proto.TAG_FILECHUNK) _
                    OrElse msg.StartsWith(Proto.TAG_FILEEND) Then

                    ' Si le host est destinataire (dans META), on remonte; sinon on redistribue
                    If msg.StartsWith(Proto.TAG_FILEMETA) Then
                        Dim parts = msg.Split(":"c, 6)
                        If parts.Length >= 6 Then
                            Dim dest = parts(3)
                            If String.Equals(dest, HostDisplayName, StringComparison.OrdinalIgnoreCase) Then
                                RaiseEvent FileSignal(msg)
                                Return
                            End If
                        End If
                    End If
                    ' forward au(x) destinataire(s) (ici simplifié : broadcast sauf sender; côté client on filtre)
                    Await BroadcastExceptAsync(msg, senderName)
                    Return
                End If

                ' --- ICE (signaling) ---
                If msg.StartsWith(Proto.TAG_ICE_OFFER) Then
                    RaiseEvent IceSignal("ICE_OFFER", msg)
                    Await BroadcastExceptAsync(msg, senderName)
                    Return
                End If
                If msg.StartsWith(Proto.TAG_ICE_ANSWER) Then
                    RaiseEvent IceSignal("ICE_ANSWER", msg)
                    Await BroadcastExceptAsync(msg, senderName)
                    Return
                End If
                If msg.StartsWith(Proto.TAG_ICE_CAND) Then
                    RaiseEvent IceSignal("ICE_CAND", msg)
                    Await BroadcastExceptAsync(msg, senderName)
                    Return
                End If

            Catch ex As Exception
                RaiseEvent LogLine($"[Hub] HandleInbound error: {ex.Message}")
            End Try
        End Function

        Private Async Function BroadcastExceptAsync(msg As String, excludeName As String) As Task
            Dim data = Encoding.UTF8.GetBytes(msg)

            Dim targetsP As New List(Of INetworkStream)
            Dim targetsT As New List(Of NetworkStream)

            SyncLock _clientsP2p
                For Each kvp In _clientsP2p
                    If kvp.Key <> excludeName Then targetsP.Add(kvp.Value)
                Next
            End SyncLock
            SyncLock _clientsTcp
                For Each kvp In _clientsTcp
                    If kvp.Key <> excludeName Then targetsT.Add(kvp.Value)
                Next
            End SyncLock

            For Each s In targetsP
                Try : Await s.SendAsync(data, CancellationToken.None) : Catch : End Try
            Next
            For Each ns In targetsT
                Try : Await ns.WriteAsync(data, 0, data.Length) : Catch : End Try
            Next
        End Function

        Private Sub BroadcastPeers()
            Dim all As New List(Of String)
            SyncLock _clientsP2p
                all.AddRange(_clientsP2p.Keys)
            End SyncLock
            SyncLock _clientsTcp
                all.AddRange(_clientsTcp.Keys)
            End SyncLock

            RaiseEvent PeerListUpdated(New List(Of String)(all))

            Dim payload = Proto.TAG_PEERS & String.Join(";", all)
            ' diffuse à tous
            _ = BroadcastFromHostAsync(payload)
        End Sub

        Private Function GetUniqueName(baseName As String) As String
            Dim n = baseName
            Dim i = 1
            Do While _clientsP2p.ContainsKey(n) OrElse _clientsTcp.ContainsKey(n)
                i += 1
                n = baseName & "_" & i.ToString()
            Loop
            Return n
        End Function

    End Class

End Namespace
