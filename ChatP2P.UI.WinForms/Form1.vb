Imports System.Net
Imports System.Threading
Imports System.IO
Imports ChatP2P.Core
Imports ChatP2P.App
Imports ChatP2P.App.Protocol
Imports Proto = ChatP2P.App.Protocol.Tags

Public Class Form1

    ' --------- Etat global ----------
    Private _cts As CancellationTokenSource
    Private _path As DirectPath
    Private _isHost As Boolean = False

    ' Côté client (chat principal vers host)
    Private _stream As INetworkStream

    ' Hub de relai (TCP)
    Private _hub As RelayHub

    ' Côté host (plusieurs clients via DirectPath historique)
    Private ReadOnly _clients As New Dictionary(Of String, INetworkStream)()
    Private ReadOnly _revIndex As New Dictionary(Of INetworkStream, String)()

    ' Peers & nom local
    Private _displayName As String = "Me"

    ' Paramètres utilisateur
    Private _recvFolder As String = ""

    ' -- Chat privé --
    Private ReadOnly _privateChats As New Dictionary(Of String, PrivateChatForm)()

    ' Réception fichiers
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

        AddHandler lstPeers.DoubleClick, AddressOf lstPeers_DoubleClick
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

    Private Sub BroadcastPeers() ' HOST → tous (via connexions DirectPath historiques)
        If Not _isHost Then Return

        Dim allNames As New List(Of String)
        Dim targets As List(Of INetworkStream)

        SyncLock _clients
            allNames.Add(_displayName) ' host
            allNames.AddRange(_clients.Keys)
            targets = New List(Of INetworkStream)(_clients.Values)
        End SyncLock

        Dim payload = Proto.TAG_PEERS & String.Join(";", allNames)
        Dim data = System.Text.Encoding.UTF8.GetBytes(payload)

        For Each cl In targets
            cl.SendAsync(data, CancellationToken.None)
        Next

        UpdatePeers(allNames)
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

            If _isHost Then
                Log($"[Info] Host name → {newName}")
                BroadcastPeers()
            ElseIf _stream IsNot Nothing Then
                Dim data = System.Text.Encoding.UTF8.GetBytes(Proto.TAG_NAME & newName)
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

    ' =========================================
    ' ============ Double‑click peer ===========
    ' =========================================
    Private Sub lstPeers_DoubleClick(sender As Object, e As EventArgs)
        Dim sel = TryCast(lstPeers.SelectedItem, String)
        If String.IsNullOrWhiteSpace(sel) Then Return
        If String.Equals(sel, _displayName, StringComparison.OrdinalIgnoreCase) Then Return
        OpenPrivateChat(sel)
    End Sub

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
            Sub(text)
                SendPrivateMessage(peer, text)
            End Sub)

        _privateChats(peer) = frm
        frm.Show(Me)
    End Sub

    Private Sub EnsurePrivateChat(peer As String)
        If String.IsNullOrWhiteSpace(peer) Then Return
        If Not _privateChats.ContainsKey(peer) Then
            OpenPrivateChat(peer)
        End If
    End Sub

    ' Append dans le chat privé (ouvre si nécessaire)
    Private Sub AppendToPrivate(peer As String, senderName As String, message As String)
        EnsurePrivateChat(peer)
        Dim frm As PrivateChatForm = Nothing
        If _privateChats.TryGetValue(peer, frm) AndAlso frm IsNot Nothing AndAlso Not frm.IsDisposed Then
            frm.AppendMessage(senderName, message)
        End If
    End Sub

    Private Async Sub SendPrivateMessage(dest As String, text As String)
        Dim payload = $"{Proto.TAG_PRIV}{_displayName}:{dest}:{text}"
        Dim data = System.Text.Encoding.UTF8.GetBytes(payload)

        If _isHost Then
            If _hub Is Nothing Then Log("[Privé] Hub non initialisé.") : Return
            Await _hub.SendToAsync(dest, data)
        Else
            If _stream Is Nothing Then Log("[Privé] Non connecté au host.") : Return
            Await _stream.SendAsync(data, CancellationToken.None)
        End If
    End Sub
    Private Async Sub btnStartHost_Click(sender As Object, e As EventArgs) Handles btnStartHost.Click
        Dim port As Integer
        If Not Integer.TryParse(txtLocalPort.Text, port) Then Log("Port invalide.") : Return

        _displayName = If(String.IsNullOrWhiteSpace(txtName.Text), "Host", txtName.Text.Trim())
        _isHost = True
        SaveSettings()

        ' Ouvre DirectPath (UN SEUL listener: ici)
        _path = New DirectPath(port)

        Dim localPeer As New PeerDescriptor With {
        .Identity = New IdentityBundle With {.DisplayName = _displayName},
        .Endpoints = New List(Of IPEndPoint) From {New IPEndPoint(IPAddress.Any, port)}
    }

        Dim res = Await _path.ProbeAsync(localPeer, Nothing, CancellationToken.None)
        Log($"Hosting on port {port}: {res.Notes}")
        If Not res.Success Then Return

        _cts = New CancellationTokenSource()

        ' Hub "manager" (pas de port, pas de Start)
        _hub = New RelayHub() With {.HostDisplayName = _displayName}
        AddHandler _hub.PeerListUpdated, Sub(names As List(Of String)) UpdatePeers(names)
        AddHandler _hub.LogLine, Sub(t As String) Log(t)
        AddHandler _hub.MessageArrived, Sub(senderName As String, text As String) Log($"{senderName}: {text}")
        AddHandler _hub.PrivateArrived,
        Sub(senderName As String, dest As String, text As String)
            If String.Equals(dest, _displayName, StringComparison.OrdinalIgnoreCase) Then
                AppendToPrivate(senderName, senderName, text)
            End If
        End Sub
        AddHandler _hub.FileSignal, New RelayHub.FileSignalEventHandler(AddressOf OnHubFileSignal)
        AddHandler _hub.IceSignal, New RelayHub.IceSignalEventHandler(AddressOf OnHubIceSignal)

        ' Accepte les clients via DirectPath et les donne au hub
        Task.Run(Async Sub()
                     While Not _cts.IsCancellationRequested
                         Try
                             Dim s = Await _path.ConnectAsync(Nothing, _cts.Token) ' accepte un client
                             Dim name As String
                             SyncLock _clients
                                 name = $"Client{_clients.Count + 1}"
                                 _clients(name) = s
                                 _revIndex(s) = name
                             End SyncLock
                             Log($"{name} connected.")
                             ' Ajoute au hub (qui va lire/écrire sur s)
                             _hub.AddClient(name, s)
                         Catch ex As Exception
                             Log("Accept failed: " & ex.Message)
                         End Try
                     End While
                 End Sub)
    End Sub

    ' Handlers nommés pour le Hub (signatures strictes)
    Private Sub OnHubFileSignal(raw As String)
        If raw.StartsWith(Proto.TAG_FILEMETA) Then
            HandleFileMeta(raw)
        ElseIf raw.StartsWith(Proto.TAG_FILECHUNK) Then
            HandleFileChunk(raw)
        ElseIf raw.StartsWith(Proto.TAG_FILEEND) Then
            HandleFileEnd(raw)
        End If
    End Sub

    Private Sub OnHubIceSignal(raw As String)
        Log("[ICE] " & raw, verbose:=True)
        ' Si tu as une routine OnIceSignal(kind, payload) ailleurs, appelle-la ici :
        ' OnIceSignal(kind, payload)
    End Sub

    ' =========================================
    ' ================ CLIENT ==================
    ' =========================================
    Private Async Sub btnConnect_Click(sender As Object, e As EventArgs) Handles btnConnect.Click
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
            Await _stream.SendAsync(System.Text.Encoding.UTF8.GetBytes(Proto.TAG_NAME & _displayName), CancellationToken.None)
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
        Dim data = System.Text.Encoding.UTF8.GetBytes(payload)

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
            Dim metaBytes = System.Text.Encoding.UTF8.GetBytes(meta)

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
                            Await _hub.SendToAsync(dest, System.Text.Encoding.UTF8.GetBytes(chunkMsg))
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
                Await _hub.SendToAsync(dest, System.Text.Encoding.UTF8.GetBytes(endMsg))
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
                            Await _stream.SendAsync(System.Text.Encoding.UTF8.GetBytes(chunkMsg), CancellationToken.None)
                            totalSent += read

                            Dim percent = CInt((totalSent * 100L) \ fi.Length)
                            pbSend.Value = percent
                            lblSendProgress.Text = percent & "%"
                            Log($"[Send] {totalSent}/{fi.Length} bytes", verbose:=True)
                        End If
                    Loop While read > 0
                End Using

                Dim endMsg = $"{Proto.TAG_FILEEND}{transferId}"
                Await _stream.SendAsync(System.Text.Encoding.UTF8.GetBytes(endMsg), CancellationToken.None)
            End If

            Log($"Fichier {fi.Name} envoyé à {dest}")
        End Using
    End Sub

    ' =========================================
    ' ======= Handlers FICHIERS (communs) ======
    ' =========================================
    Private Sub HandleFileMeta(msg As String)
        Dim parts = msg.Split(":"c, 6)
        If parts.Length < 6 Then Return

        Dim tid = parts(1)
        Dim fromName = parts(2)
        Dim dest = parts(3)         ' <- VB: parenthèses
        Dim fname = parts(4)        ' <- VB: parenthèses
        Dim fsize = CLng(parts(5))  ' <- VB: parenthèses

        Dim iAmDest As Boolean =
        If(_isHost,
           String.Equals(dest, _displayName, StringComparison.OrdinalIgnoreCase),
           True)

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
    Private Async Sub ListenIncomingHost(s As INetworkStream, clientName As String, ct As CancellationToken)
        Try
            While Not ct.IsCancellationRequested
                Dim data = Await s.ReceiveAsync(ct)
                Dim msg = System.Text.Encoding.UTF8.GetString(data)

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

                                ' Mise à jour fenêtre privée si ouverte sous l’ancien nom
                                Dim frmOld As PrivateChatForm = Nothing
                                If _privateChats.TryGetValue(clientName, frmOld) Then
                                    _privateChats.Remove(clientName)
                                    _privateChats(finalName) = frmOld
                                    Try : frmOld.UpdatePeerName(finalName) : Catch : End Try
                                End If

                                clientName = finalName
                            End If
                        End SyncLock
                        Log($"Client renommé → {clientName}")
                        BroadcastPeers()
                    End If
                    Continue While
                End If

                If msg.StartsWith(Proto.TAG_PEERS) Then Continue While

                If msg.StartsWith(Proto.TAG_MSG) Then
                    Dim targets As New List(Of INetworkStream)
                    SyncLock _clients
                        For Each kvp In _clients
                            If kvp.Value IsNot s Then targets.Add(kvp.Value)
                        Next
                    End SyncLock
                    For Each t In targets
                        Await t.SendAsync(data, CancellationToken.None)
                    Next

                    Dim parts = msg.Substring(Proto.TAG_MSG.Length).Split(":"c, 2)
                    If parts.Length = 2 Then Log($"{parts(0)}: {parts(1)}")

                ElseIf msg.StartsWith(Proto.TAG_PRIV) Then
                    Dim rest = msg.Substring(Proto.TAG_PRIV.Length)
                    Dim parts = rest.Split(":"c, 3)
                    If parts.Length = 3 Then
                        Dim senderName = parts(0)
                        Dim dest = parts(1)
                        Dim body = parts(2)

                        If String.Equals(dest, _displayName, StringComparison.OrdinalIgnoreCase) Then
                            AppendToPrivate(senderName, senderName, body)
                        Else
                            Dim target As INetworkStream = Nothing
                            SyncLock _clients
                                If _clients.ContainsKey(dest) Then target = _clients(dest)
                            End SyncLock
                            If target IsNot Nothing Then
                                Await target.SendAsync(System.Text.Encoding.UTF8.GetBytes(Proto.TAG_PRIV & rest), CancellationToken.None)
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
            BroadcastPeers()
        End Try
    End Sub

    ' =========================================
    ' ========= Boucle réception CLIENT ========
    ' =========================================
    Private Async Sub ListenIncomingClient(s As INetworkStream, ct As CancellationToken)
        Try
            While Not ct.IsCancellationRequested
                Dim data = Await s.ReceiveAsync(ct)
                Dim msg = System.Text.Encoding.UTF8.GetString(data)

                If msg.StartsWith(Proto.TAG_PEERS) Then
                    Dim peers = msg.Substring(Proto.TAG_PEERS.Length).Split(";"c).ToList()
                    UpdatePeers(peers)

                ElseIf msg.StartsWith(Proto.TAG_MSG) Then
                    Dim payload = msg.Substring(Proto.TAG_MSG.Length)
                    Dim parts = payload.Split(":"c, 2)
                    If parts.Length = 2 Then Log($"{parts(0)}: {parts(1)}")

                ElseIf msg.StartsWith(Proto.TAG_PRIV) Then
                    Dim rest = msg.Substring(Proto.TAG_PRIV.Length)
                    Dim parts = rest.Split(":"c, 3)
                    If parts.Length = 3 Then
                        Dim senderName = parts(0)
                        Dim dest = parts(1)
                        Dim body = parts(2)
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

End Class
