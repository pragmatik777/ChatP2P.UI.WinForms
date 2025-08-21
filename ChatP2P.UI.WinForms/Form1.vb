' ChatP2P.UI.WinForms/Form1.vb
Option Strict On
Imports System.Net
Imports System.IO
Imports System.Text
Imports System.Threading
Imports ChatP2P.Core                    ' P2PManager, PeerDescriptor, IdentityBundle, INetworkStream, DirectPath
Imports ChatP2P.App                     ' RelayHub
Imports ChatP2P.App.Protocol            ' Tags module namespace
Imports Proto = ChatP2P.App.Protocol.Tags

Public Class Form1

    ' --------- Etat global ----------
    Private _cts As CancellationTokenSource
    Private _path As DirectPath
    Private _isHost As Boolean = False

    ' Côté client (chat principal vers host)
    Private _stream As INetworkStream

    ' Côté host (plusieurs clients - historique direct path)
    Private ReadOnly _clients As New Dictionary(Of String, INetworkStream)()
    Private ReadOnly _revIndex As New Dictionary(Of INetworkStream, String)()

    ' Peers & nom local
    Private _displayName As String = "Me"

    ' Paramètres utilisateur
    Private _recvFolder As String = ""

    ' Hub (relai + signaling ICE)
    Private _hub As RelayHub

    ' -- Chat privé --
    Private ReadOnly _privateChats As New Dictionary(Of String, PrivateChatForm)()

    ' Réception fichiers (tous rôles) : état par transfert
    Private Class FileRecvState
        Public File As FileStream
        Public FileName As String
        Public Expected As Long
        Public Received As Long
    End Class
    Private ReadOnly _fileRecv As New Dictionary(Of String, FileRecvState)()
    Private ReadOnly _fileRelays As New Dictionary(Of String, INetworkStream)()

    ' =========================================
    ' =========== Chargement / Settings =========
    ' =========================================
    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Try
            If Not String.IsNullOrWhiteSpace(My.Settings.DisplayName) Then txtName.Text = My.Settings.DisplayName
            If Not String.IsNullOrWhiteSpace(My.Settings.LocalPort) Then txtLocalPort.Text = My.Settings.LocalPort
            If Not String.IsNullOrWhiteSpace(My.Settings.RemoteIp) Then txtRemoteIp.Text = My.Settings.RemoteIp
            If Not String.IsNullOrWhiteSpace(My.Settings.RemotePort) Then txtRemotePort.Text = My.Settings.RemotePort
            If Not String.IsNullOrWhiteSpace(My.Settings.RecvFolder) AndAlso Directory.Exists(My.Settings.RecvFolder) Then
                _recvFolder = My.Settings.RecvFolder
            End If
        Catch
        End Try
        _displayName = If(String.IsNullOrWhiteSpace(txtName.Text), "Me", txtName.Text.Trim())

        ' Double‑click sur un peer => ouvre chat privé
        AddHandler lstPeers.DoubleClick, AddressOf lstPeers_DoubleClick

        ' ==== Init P2P Manager (Core) : routing de signaling ICE ====
        ' Cette init peut être relancée après saisie du nom (si tu veux), mais une fois suffit.
        P2PManager.Init(
            sendSignal:=Function(dest As String, line As String)
                            ' On a un "line" (texte) à envoyer au pair "dest".
                            ' C’est du signaling ICE (ICE_OFFER/ICE_ANSWER/ICE_CAND).
                            Dim bytes = Encoding.UTF8.GetBytes(line)
                            If _isHost Then
                                If _hub IsNot Nothing Then
                                    Return _hub.SendToAsync(dest, bytes)
                                End If
                                Return Task.CompletedTask
                            Else
                                If _stream IsNot Nothing Then
                                    Return _stream.SendAsync(bytes, CancellationToken.None)
                                End If
                                Return Task.CompletedTask
                            End If
                        End Function,
            localDisplayName:=_displayName
        )
    End Sub

    Private Sub Form1_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        SaveSettings()
    End Sub

    Private Sub SaveSettings()
        Try
            My.Settings.DisplayName = txtName.Text.Trim()
            My.Settings.LocalPort = txtLocalPort.Text.Trim()
            My.Settings.RemoteIp = txtRemoteIp.Text.Trim()
            My.Settings.RemotePort = txtRemotePort.Text.Trim()
            My.Settings.RecvFolder = _recvFolder
            My.Settings.Save()
        Catch
        End Try
    End Sub

    ' =========================================
    ' ================ UI helpers ==============
    ' =========================================
    Private Sub Log(s As String, Optional verbose As Boolean = False)
        If verbose AndAlso Not chkVerbose.Checked Then Return
        If txtLog.InvokeRequired Then
            txtLog.Invoke(Sub() txtLog.AppendText(s & Environment.NewLine))
        Else
            txtLog.AppendText(s & Environment.NewLine)
        End If
    End Sub

    Private Sub UpdatePeers(peers As List(Of String))
        If lstPeers.InvokeRequired Then
            lstPeers.Invoke(Sub() UpdatePeers(peers))
        Else
            lstPeers.Items.Clear()
            For Each p In peers
                If Not String.IsNullOrWhiteSpace(p) Then lstPeers.Items.Add(p)
            Next
        End If
    End Sub

    Private Sub lstPeers_DoubleClick(sender As Object, e As EventArgs)
        Dim sel = TryCast(lstPeers.SelectedItem, String)
        If String.IsNullOrWhiteSpace(sel) Then Return
        If String.Equals(sel, _displayName, StringComparison.OrdinalIgnoreCase) Then Return
        OpenPrivateChat(sel)
    End Sub
    ' =========================================
    ' ================ HOST ====================
    ' =========================================
    Private Async Sub btnStartHost_Click(sender As Object, e As EventArgs) Handles btnStartHost.Click
        Dim port As Integer
        If Not Integer.TryParse(txtLocalPort.Text, port) Then Log("Port invalide.") : Return

        _displayName = If(String.IsNullOrWhiteSpace(txtName.Text), "Host", txtName.Text.Trim())
        _isHost = True
        SaveSettings()

        ' --- DirectPath écoute sur le port et accepte les clients entrants ---
        _path = New DirectPath(port)

        Dim localPeer As New PeerDescriptor With {
        .Identity = New IdentityBundle With {.DisplayName = _displayName},
        .Endpoints = New List(Of IPEndPoint) From {New IPEndPoint(IPAddress.Any, port)}
    }

        Dim res = Await _path.ProbeAsync(localPeer, Nothing, CancellationToken.None)
        Log($"Hosting on port {port}: {res.Notes}")
        If Not res.Success Then Return

        _cts = New CancellationTokenSource()

        ' --- Hub (sans écoute TCP intégrée) : brancher les événements vers l’UI ---
        _hub = New RelayHub() With {.HostDisplayName = _displayName}

        AddHandler _hub.PeerListUpdated, Sub(names As List(Of String)) UpdatePeers(names)
        AddHandler _hub.LogLine, Sub(t As String) Log(t)
        AddHandler _hub.MessageArrived, Sub(senderName As String, text As String)
                                            Log($"{senderName}: {text}")
                                        End Sub
        AddHandler _hub.PrivateArrived,
        Sub(senderName As String, dest As String, text As String)
            If String.Equals(dest, _displayName, StringComparison.OrdinalIgnoreCase) Then
                AppendToPrivate(senderName, senderName, text)
            End If
        End Sub
        AddHandler _hub.FileSignal, New RelayHub.FileSignalEventHandler(AddressOf OnHubFileSignal)
        AddHandler _hub.IceSignal, New RelayHub.IceSignalEventHandler(AddressOf OnHubIceSignal)

        Log($"Hub initialisé (host='{_displayName}'). En attente de connexions…")

        ' --- Accepte plusieurs clients en boucle via DirectPath, puis ajoute au hub ---
        Task.Run(Async Sub()
                     While Not _cts.IsCancellationRequested
                         Try
                             Dim s = Await _path.ConnectAsync(Nothing, _cts.Token) ' accepte un client entrant
                             Dim clientName As String
                             SyncLock _clients
                                 clientName = $"Client{_clients.Count + 1}"
                                 _clients(clientName) = s
                                 _revIndex(s) = clientName
                             End SyncLock
                             Log($"{clientName} connecté.")
                             ' Important : brancher le stream côté hub
                             _hub.AddClient(clientName, s)
                         Catch ex As Exception
                             If Not _cts.IsCancellationRequested Then
                                 Log("Accept failed: " & ex.Message)
                             End If
                         End Try
                     End While
                 End Sub)
    End Sub


    ' =========================================
    ' ================ CLIENT ==================
    ' =========================================
    Private Async Sub btnConnect_Click(sender As Object, e As EventArgs) Handles btnConnect.Click
        ' Mode client (legacy direct vers host) : on garde _stream pour envoyer au host.
        ' Ici, on se connecte au hub via TCP ? Ton binaire client actuel utilisait DirectPath.
        ' Pour rester compatible sans tout casser, on garde DirectPath côté client.
        ' => PRÉREQUIS : côté host, lance un composant équivalent qui parle la même "framing".
        ' Si tu utilises désormais uniquement le Hub TCP, tu peux remplacer ce bloc
        ' par une connexion TcpClient et affecter _stream via un wrapper.

        Dim ip As IPAddress
        If Not IPAddress.TryParse(txtRemoteIp.Text, ip) Then Log("IP invalide.") : Return

        Dim port As Integer
        If Not Integer.TryParse(txtRemotePort.Text, port) Then Log("Port invalide.") : Return

        _displayName = If(String.IsNullOrWhiteSpace(txtName.Text), "Client", txtName.Text.Trim())
        _isHost = False
        SaveSettings()

        If _path Is Nothing Then _path = New DirectPath(0)

        Dim remotePeer As New PeerDescriptor With {
            .Identity = New IdentityBundle With {.DisplayName = _displayName},
            .Endpoints = New List(Of IPEndPoint) From {New IPEndPoint(ip, port)}
        }

        Try
            _cts = New CancellationTokenSource()
            _stream = Await _path.ConnectAsync(remotePeer, _cts.Token)
            Log($"Connected to {ip}:{port}")
            Await _stream.SendAsync(Encoding.UTF8.GetBytes(Proto.TAG_NAME & _displayName), CancellationToken.None)

            ' Démarre la boucle d’écoute côté client
            ListenIncomingClient(_stream, _cts.Token)
        Catch ex As Exception
            Log($"Connect failed: {ex.Message}")
        End Try
    End Sub

    ' =========================================
    ' =============== ENVOI CHAT ===============
    ' =========================================
    Private Async Sub btnSend_Click(sender As Object, e As EventArgs) Handles btnSend.Click
        Dim msg = txtMessage.Text.Trim()
        If msg = "" Then Return

        Dim payload = $"{Proto.TAG_MSG}{_displayName}:{msg}"
        Dim data = Encoding.UTF8.GetBytes(payload)

        If _isHost Then
            If _hub Is Nothing Then Log("Hub non initialisé.") : Return
            Await _hub.BroadcastFromHostAsync(data)
        Else
            If _stream Is Nothing Then Log("Not connected.") : Return
            Await _stream.SendAsync(data, CancellationToken.None)
        End If

        Log($"{_displayName}: {msg}")
        txtMessage.Clear()
    End Sub

    ' =========================================
    ' =========== ENVOI FICHIERS (RELAY) ======
    ' =========================================
    Private Async Sub btnSendFile_Click(sender As Object, e As EventArgs) Handles btnSendFile.Click
        If lstPeers.SelectedItem Is Nothing Then
            Log("Sélectionnez un destinataire.")
            Return
        End If
        Dim dest = lstPeers.SelectedItem.ToString()

        Using ofd As New OpenFileDialog()
            If ofd.ShowDialog() <> DialogResult.OK Then Return
            Dim fi As New FileInfo(ofd.FileName)
            Dim transferId As String = Guid.NewGuid().ToString("N")

            ' Progression reset
            pbSend.Value = 0
            lblSendProgress.Text = "0%"

            ' META
            Dim meta = $"{Proto.TAG_FILEMETA}{transferId}:{_displayName}:{dest}:{fi.Name}:{fi.Length}"
            Dim metaBytes = Encoding.UTF8.GetBytes(meta)

            If _isHost Then
                If _hub Is Nothing Then Log("Hub non initialisé.") : Return
                Await _hub.SendToAsync(dest, metaBytes)

                Using fs = fi.OpenRead()
                    Dim buffer(32768 - 1) As Byte
                    Dim totalSent As Long = 0
                    Dim read As Integer
                    Do
                        read = fs.Read(buffer, 0, buffer.Length)
                        If read > 0 Then
                            Dim chunk = Convert.ToBase64String(buffer, 0, read)
                            Dim chunkMsg = $"{Proto.TAG_FILECHUNK}{transferId}:{chunk}"
                            Await _hub.SendToAsync(dest, Encoding.UTF8.GetBytes(chunkMsg))
                            totalSent += read

                            ' Progression
                            Dim percent = CInt((totalSent * 100L) \ fi.Length)
                            pbSend.Value = percent
                            lblSendProgress.Text = percent & "%"
                            Log($"[Send] {totalSent}/{fi.Length} bytes", verbose:=True)
                        End If
                    Loop While read > 0
                End Using

                Dim endMsg = $"{Proto.TAG_FILEEND}{transferId}"
                Await _hub.SendToAsync(dest, Encoding.UTF8.GetBytes(endMsg))
            Else
                If _stream Is Nothing Then Log("Not connected.") : Return

                Await _stream.SendAsync(metaBytes, CancellationToken.None)

                Using fs = fi.OpenRead()
                    Dim buffer(32768 - 1) As Byte
                    Dim totalSent As Long = 0
                    Dim read As Integer
                    Do
                        read = fs.Read(buffer, 0, buffer.Length)
                        If read > 0 Then
                            Dim chunk = Convert.ToBase64String(buffer, 0, read)
                            Dim chunkMsg = $"{Proto.TAG_FILECHUNK}{transferId}:{chunk}"
                            Await _stream.SendAsync(Encoding.UTF8.GetBytes(chunkMsg), CancellationToken.None)
                            totalSent += read

                            Dim percent = CInt((totalSent * 100L) \ fi.Length)
                            pbSend.Value = percent
                            lblSendProgress.Text = percent & "%"
                            Log($"[Send] {totalSent}/{fi.Length} bytes", verbose:=True)
                        End If
                    Loop While read > 0
                End Using

                Dim endMsg = $"{Proto.TAG_FILEEND}{transferId}"
                Await _stream.SendAsync(Encoding.UTF8.GetBytes(endMsg), CancellationToken.None)
            End If

            Log($"Fichier {fi.Name} envoyé à {dest}")
        End Using
    End Sub

    ' =========================================
    ' ======= Handlers FICHIERS (communs) ======
    ' =========================================
    Private Sub HandleFileMeta(msg As String)
        ' FILEMETA:tid:from:dest:filename:size
        Dim parts = msg.Split(":"c, 6)
        If parts.Length < 6 Then Return
        Dim tid = parts(1)
        Dim fromName = parts(2)
        Dim dest = parts(3)
        Dim fname = parts(4)
        Dim fsize = CLng(parts(5))

        Dim iAmDest As Boolean
        If _isHost Then
            iAmDest = String.Equals(dest, _displayName, StringComparison.OrdinalIgnoreCase)
        Else
            iAmDest = True ' côté client, le host ne forwarde que si je suis la cible
        End If
        If Not iAmDest Then Return

        Dim savePath As String = ""
        If Not String.IsNullOrEmpty(_recvFolder) AndAlso Directory.Exists(_recvFolder) Then
            savePath = MakeUniquePath(Path.Combine(_recvFolder, fname))
        Else
            Using sfd As New SaveFileDialog()
                sfd.FileName = fname
                If sfd.ShowDialog() <> DialogResult.OK Then Return
                savePath = sfd.FileName
            End Using
        End If

        Dim fs As New FileStream(savePath, FileMode.Create, FileAccess.Write)
        Dim st As New FileRecvState With {
            .File = fs,
            .FileName = savePath,
            .Expected = fsize,
            .Received = 0
        }
        SyncLock _fileRecv
            _fileRecv(tid) = st
        End SyncLock

        pbRecv.Value = 0
        lblRecvProgress.Text = "0%"
        Log($"Réception {fname} ({fsize} bytes) de {fromName}")
    End Sub

    Private Sub HandleFileChunk(msg As String)
        ' FILECHUNK:tid:base64
        Dim parts = msg.Split(":"c, 3)
        If parts.Length < 3 Then Return
        Dim tid = parts(1)
        Dim chunkB64 = parts(2)
        Dim st As FileRecvState = Nothing
        SyncLock _fileRecv
            If _fileRecv.ContainsKey(tid) Then st = _fileRecv(tid)
        End SyncLock
        If st Is Nothing Then Return

        Dim bytes = Convert.FromBase64String(chunkB64)
        st.File.Write(bytes, 0, bytes.Length)
        st.Received += bytes.Length

        Dim percent = CInt((st.Received * 100L) \ st.Expected)
        If percent < 0 Then percent = 0
        If percent > 100 Then percent = 100
        pbRecv.Value = percent
        lblRecvProgress.Text = percent & "%"
        Log($"[Recv] {st.Received}/{st.Expected} bytes", verbose:=True)
    End Sub

    Private Sub HandleFileEnd(msg As String)
        ' FILEEND:tid
        Dim parts = msg.Split(":"c, 2)
        If parts.Length < 2 Then Return
        Dim tid = parts(1)
        Dim st As FileRecvState = Nothing
        SyncLock _fileRecv
            If _fileRecv.ContainsKey(tid) Then
                st = _fileRecv(tid)
                _fileRecv.Remove(tid)
            End If
        End SyncLock

        If st IsNot Nothing Then
            Try
                st.File.Flush()
                st.File.Close()
            Catch
            End Try
            pbRecv.Value = 100
            lblRecvProgress.Text = "100%"
            Log($"Fichier reçu avec succès → {st.FileName}")
        End If
    End Sub

    Private Function MakeUniquePath(path As String) As String
        Dim dir As String = System.IO.Path.GetDirectoryName(path)
        Dim name As String = System.IO.Path.GetFileNameWithoutExtension(path)
        Dim ext As String = System.IO.Path.GetExtension(path)
        Dim i As Integer = 1
        Dim candidate As String = path
        While File.Exists(candidate)
            candidate = System.IO.Path.Combine(dir, $"{name} ({i}){ext}")
            i += 1
        End While
        Return candidate
    End Function

    ' =========================================
    ' ========== Bouton choix dossier ==========
    ' =========================================
    Private Sub btnChooseRecvFolder_Click(sender As Object, e As EventArgs) Handles btnChooseRecvFolder.Click
        Using fbd As New FolderBrowserDialog()
            If Not String.IsNullOrWhiteSpace(_recvFolder) AndAlso Directory.Exists(_recvFolder) Then
                fbd.SelectedPath = _recvFolder
            End If
            If fbd.ShowDialog() = DialogResult.OK Then
                _recvFolder = fbd.SelectedPath
                SaveSettings()
                Log($"Dossier réception → {_recvFolder}")
            End If
        End Using
    End Sub

    ' =========================================
    ' ========== Boucle réception HOST =========
    ' =========================================
    ' (Utilisée seulement si tu réactives un accept DirectPath côté host sur un autre port.)
    Private Async Sub ListenIncomingHost(s As INetworkStream, clientName As String, ct As CancellationToken)
        Try
            While Not ct.IsCancellationRequested
                Dim data = Await s.ReceiveAsync(ct)
                Dim msg = Encoding.UTF8.GetString(data)

                If msg.StartsWith(Proto.TAG_NAME) Then
                    Dim newName = msg.Substring(Proto.TAG_NAME.Length).Trim()
                    If newName <> "" Then
                        SyncLock _clients
                            If _clients.ContainsKey(clientName) AndAlso _clients(clientName) Is s Then
                                _clients.Remove(clientName)
                                Dim finalName = newName
                                If _clients.ContainsKey(finalName) Then
                                    finalName = finalName & "_" & DateTime.UtcNow.Ticks.ToString()
                                End If
                                _clients(finalName) = s
                                _revIndex(s) = finalName
                                clientName = finalName
                            End If
                        End SyncLock
                        Log($"Client renommé → {clientName}")
                        BroadcastPeersHostLegacy()
                    End If
                    Continue While
                End If

                If msg.StartsWith(Proto.TAG_PEERS) Then Continue While

                If msg.StartsWith(Proto.TAG_MSG) Then
                    ' Rebroadcast legacy
                    Dim targets As New List(Of INetworkStream)
                    SyncLock _clients
                        For Each kvp In _clients
                            If kvp.Value IsNot s Then targets.Add(kvp.Value)
                        Next
                    End SyncLock
                    For Each t In targets
                        Await t.SendAsync(data, CancellationToken.None)
                    Next

                    Dim parts = msg.Split(":"c, 3)
                    If parts.Length >= 3 Then Log($"{parts(1)}: {parts(2)}")

                ElseIf msg.StartsWith(Proto.TAG_PRIV) Then
                    Dim parts = msg.Split(":"c, 4)
                    If parts.Length >= 4 Then
                        Dim senderName = parts(1)
                        Dim dest = parts(2)
                        Dim body = parts(3)

                        If String.Equals(dest, _displayName, StringComparison.OrdinalIgnoreCase) Then
                            AppendToPrivate(senderName, senderName, body)
                        Else
                            Dim target As INetworkStream = Nothing
                            SyncLock _clients
                                If _clients.ContainsKey(dest) Then target = _clients(dest)
                            End SyncLock
                            If target IsNot Nothing Then
                                Await target.SendAsync(data, CancellationToken.None)
                            End If
                        End If
                    End If

                ElseIf msg.StartsWith(Proto.TAG_FILEMETA) Then
                    Dim parts = msg.Split(":"c, 6)
                    If parts.Length >= 6 Then
                        Dim tid = parts(1)
                        Dim dest = parts(3)
                        If String.Equals(dest, _displayName, StringComparison.OrdinalIgnoreCase) Then
                            HandleFileMeta(msg)
                        Else
                            Dim target As INetworkStream = Nothing
                            SyncLock _clients
                                If _clients.ContainsKey(dest) Then target = _clients(dest)
                            End SyncLock
                            If target IsNot Nothing Then
                                SyncLock _fileRelays
                                    _fileRelays(tid) = target
                                End SyncLock
                                Await target.SendAsync(data, CancellationToken.None)
                            End If
                        End If
                    End If

                ElseIf msg.StartsWith(Proto.TAG_FILECHUNK) Then
                    Dim parts = msg.Split(":"c, 3)
                    If parts.Length < 3 Then Continue While
                    Dim tid = parts(1)
                    Dim target As INetworkStream = Nothing
                    SyncLock _fileRelays
                        If _fileRelays.ContainsKey(tid) Then target = _fileRelays(tid)
                    End SyncLock
                    If target Is Nothing Then
                        HandleFileChunk(msg)
                    Else
                        Await target.SendAsync(data, CancellationToken.None)
                    End If

                ElseIf msg.StartsWith(Proto.TAG_FILEEND) Then
                    Dim parts = msg.Split(":"c, 2)
                    If parts.Length < 2 Then Continue While
                    Dim tid = parts(1)
                    Dim target As INetworkStream = Nothing
                    SyncLock _fileRelays
                        If _fileRelays.ContainsKey(tid) Then target = _fileRelays(tid)
                    End SyncLock
                    If target Is Nothing Then
                        HandleFileEnd(msg)
                    Else
                        Await target.SendAsync(data, CancellationToken.None)
                        SyncLock _fileRelays
                            If _fileRelays.ContainsKey(tid) Then _fileRelays.Remove(tid)
                        End SyncLock
                    End If
                End If
            End While
        Catch ex As Exception
            Log($"{clientName} déconnecté: {ex.Message}")
            SyncLock _clients
                If _clients.ContainsKey(clientName) Then _clients.Remove(clientName)
            End SyncLock
            BroadcastPeersHostLegacy()
        End Try
    End Sub

    Private Sub BroadcastPeersHostLegacy()
        ' Legacy: quand on utilisait _clients (DirectPath) côté host.
        Dim names As New List(Of String)
        names.Add(_displayName)
        SyncLock _clients
            names.AddRange(_clients.Keys)
        End SyncLock
        UpdatePeers(names)
    End Sub

    ' =========================================
    ' ========= Boucle réception CLIENT ========
    ' =========================================
    Private Async Sub ListenIncomingClient(s As INetworkStream, ct As CancellationToken)
        Try
            While Not ct.IsCancellationRequested
                Dim data = Await s.ReceiveAsync(ct)
                Dim msg = Encoding.UTF8.GetString(data)

                If msg.StartsWith(Proto.TAG_PEERS) Then
                    Dim peers = msg.Substring(Proto.TAG_PEERS.Length).Split(";"c).ToList()
                    UpdatePeers(peers)

                ElseIf msg.StartsWith(Proto.TAG_MSG) Then
                    Dim parts = msg.Split(":"c, 3)
                    If parts.Length >= 3 Then Log($"{parts(1)}: {parts(2)}")

                ElseIf msg.StartsWith(Proto.TAG_PRIV) Then
                    Dim parts = msg.Split(":"c, 4)
                    If parts.Length >= 4 Then
                        Dim senderName = parts(1)
                        Dim dest = parts(2)
                        Dim body = parts(3)
                        If String.Equals(dest, _displayName, StringComparison.OrdinalIgnoreCase) Then
                            AppendToPrivate(senderName, senderName, body)
                        End If
                    End If

                ElseIf msg.StartsWith(Proto.TAG_FILEMETA) Then
                    HandleFileMeta(msg)

                ElseIf msg.StartsWith(Proto.TAG_FILECHUNK) Then
                    HandleFileChunk(msg)

                ElseIf msg.StartsWith(Proto.TAG_FILEEND) Then
                    HandleFileEnd(msg)
                End If

            End While
        Catch ex As Exception
            Log($"Déconnecté: {ex.Message}")
        End Try
    End Sub

    ' =========================================
    ' ============== ICE SIGNALS ===============
    ' =========================================
    Private Sub OnHubFileSignal(raw As String)
        If raw.StartsWith(Proto.TAG_FILEMETA) Then HandleFileMeta(raw)
        If raw.StartsWith(Proto.TAG_FILECHUNK) Then HandleFileChunk(raw)
        If raw.StartsWith(Proto.TAG_FILEEND) Then HandleFileEnd(raw)
    End Sub

    Private Sub OnHubIceSignal(raw As String)
        Try
            If raw.StartsWith(Proto.TAG_ICE_OFFER) Then
                Dim p = raw.Substring(Proto.TAG_ICE_OFFER.Length).Split(":"c, 3)
                Dim fromPeer = p(0)
                Dim sdp = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                P2PManager.HandleOffer(fromPeer, sdp, New String() {"stun:stun.l.google.com:19302"})

            ElseIf raw.StartsWith(Proto.TAG_ICE_ANSWER) Then
                Dim p = raw.Substring(Proto.TAG_ICE_ANSWER.Length).Split(":"c, 3)
                Dim fromPeer = p(0)
                Dim sdp = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                P2PManager.HandleAnswer(fromPeer, sdp)

            ElseIf raw.StartsWith(Proto.TAG_ICE_CAND) Then
                Dim p = raw.Substring(Proto.TAG_ICE_CAND.Length).Split(":"c, 3)
                Dim fromPeer = p(0)
                Dim cand = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                P2PManager.HandleCandidate(fromPeer, cand)
            End If
        Catch ex As Exception
            Log("[ICE] parse error: " & ex.Message, verbose:=True)
        End Try
    End Sub

    ' =========================================
    ' ========= Fenêtres chat privé ============
    ' =========================================
    Private Sub OpenPrivateChat(peer As String)
        Dim frm As PrivateChatForm = Nothing
        If _privateChats.TryGetValue(peer, frm) Then
            Try
                If frm IsNot Nothing AndAlso Not frm.IsDisposed Then
                    frm.BringToFront()
                    frm.Focus()
                    Exit Sub
                End If
            Catch
            End Try
            _privateChats.Remove(peer)
        End If

        frm = New PrivateChatForm(_displayName, peer,
            Sub(text) SendPrivateMessage(peer, text)
        )
        _privateChats(peer) = frm
        frm.Show(Me)
    End Sub

    Private Sub EnsurePrivateChat(peer As String)
        If String.IsNullOrWhiteSpace(peer) Then Return
        If Not _privateChats.ContainsKey(peer) Then OpenPrivateChat(peer)
    End Sub

    Private Sub AppendToPrivate(peer As String, senderName As String, message As String)
        EnsurePrivateChat(peer)
        Dim frm As PrivateChatForm = Nothing
        If _privateChats.TryGetValue(peer, frm) AndAlso frm IsNot Nothing AndAlso Not frm.IsDisposed Then
            frm.AppendMessage(senderName, message)
        End If
    End Sub

    Private Async Sub SendPrivateMessage(dest As String, text As String)
        If String.IsNullOrWhiteSpace(dest) OrElse String.IsNullOrWhiteSpace(text) Then Return

        ' 1) Si une session P2P existe déjà, on envoie via DataChannel
        Try
            If ChatP2P.Core.P2PManager.TrySendP2P(dest, text) Then
                ' envoyé en direct, on s'arrête là
                Exit Sub
            End If
        Catch
            ' on ignore et on passe au fallback
        End Try

        ' 2) Fallback immédiat via hub/host (pas d’attente de négo P2P)
        Dim payload = $"{Proto.TAG_PRIV}{_displayName}:{dest}:{text}"
        Dim data = System.Text.Encoding.UTF8.GetBytes(payload)

        If _isHost Then
            If _hub Is Nothing Then
                Log("[Privé] Hub non initialisé.")
                Return
            End If
            Await _hub.SendToAsync(dest, data)
        Else
            If _stream Is Nothing Then
                Log("[Privé] Non connecté au host.")
                Return
            End If
            Await _stream.SendAsync(data, Threading.CancellationToken.None)
        End If
    End Sub


    ' =========================================
    ' ============ Entrées clavier =============
    ' =========================================
    Private Sub txtMessage_KeyDown(sender As Object, e As KeyEventArgs) Handles txtMessage.KeyDown
        If e.KeyCode = Keys.Enter Then
            e.SuppressKeyPress = True : e.Handled = True
            btnSend.PerformClick()
        End If
    End Sub

    Private Sub txtName_KeyDown(sender As Object, e As KeyEventArgs) Handles txtName.KeyDown
        If e.KeyCode = Keys.Enter Then
            e.SuppressKeyPress = True : e.Handled = True
            Dim newName = txtName.Text.Trim()
            If String.IsNullOrWhiteSpace(newName) Then Return
            _displayName = newName
            SaveSettings()

            ' Mets aussi à jour le P2PManager (identité locale)
            P2PManager.Init(
                sendSignal:=Function(dest As String, line As String)
                                Dim bytes = Encoding.UTF8.GetBytes(line)
                                If _isHost Then
                                    If _hub IsNot Nothing Then
                                        Return _hub.SendToAsync(dest, bytes)
                                    End If
                                    Return Task.CompletedTask
                                Else
                                    If _stream IsNot Nothing Then
                                        Return _stream.SendAsync(bytes, CancellationToken.None)
                                    End If
                                    Return Task.CompletedTask
                                End If
                            End Function,
                localDisplayName:=_displayName
            )

            If _isHost Then
                Log($"[Info] Host name → {newName}")
                ' Le hub mettra à jour la liste des peers côté clients au prochain broadcast
            ElseIf _stream IsNot Nothing Then
                Dim data = Encoding.UTF8.GetBytes(Proto.TAG_NAME & newName)
                _stream.SendAsync(data, CancellationToken.None)
                Log($"[Info] Client name → {newName} (notif au host)")
            End If
        End If
    End Sub

    Private Sub txtLocalPort_KeyDown(sender As Object, e As KeyEventArgs) Handles txtLocalPort.KeyDown
        If e.KeyCode = Keys.Enter Then e.SuppressKeyPress = True : e.Handled = True : SaveSettings()
    End Sub
    Private Sub txtRemoteIp_KeyDown(sender As Object, e As KeyEventArgs) Handles txtRemoteIp.KeyDown
        If e.KeyCode = Keys.Enter Then e.SuppressKeyPress = True : e.Handled = True : SaveSettings()
    End Sub
    Private Sub txtRemotePort_KeyDown(sender As Object, e As KeyEventArgs) Handles txtRemotePort.KeyDown
        If e.KeyCode = Keys.Enter Then e.SuppressKeyPress = True : e.Handled = True : SaveSettings()
    End Sub

End Class
