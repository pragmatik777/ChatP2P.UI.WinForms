Imports System.Net
Imports System.Threading
Imports System.IO
Imports ChatP2P.Core
Imports ChatP2P.App
Imports Proto = ChatP2P.App.Protocol.Tags

Public Class Form1

    ' --------- Etat global ----------
    Private _cts As CancellationTokenSource
    Private _path As DirectPath
    Private _isHost As Boolean = False

    ' Côté client (chat principal vers host)
    Private _stream As INetworkStream

    ' Hub (logique host consolidée)
    Private _hub As RelayHub

    ' Peers & nom local
    Private _displayName As String = "Me"

    ' Paramètres utilisateur
    Private _recvFolder As String = ""

    ' -- Chat privé --
    Private ReadOnly _privateChats As New Dictionary(Of String, PrivateChatForm)()

    ' Réception fichiers (état par transfert)
    Private Class FileRecvState
        Public File As FileStream
        Public FileName As String
        Public Expected As Long
        Public Received As Long
    End Class
    Private ReadOnly _fileRecv As New Dictionary(Of String, FileRecvState)()

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

    ' =========================================
    ' =========== Entrées clavier ==============
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
                ' Hub rebroadcaste la liste lors des changements de clients; ici on peut juste mettre à jour l'UI locale
                ' (Optionnel) : rafraîchir côté clients via un message spécifique si tu veux.
            ElseIf _stream IsNot Nothing Then
                Dim data = System.Text.Encoding.UTF8.GetBytes(MessageProtocol.TAG_NAME & newName)
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
    ' ================ HOST ====================
    ' =========================================
    Private Async Sub btnStartHost_Click(sender As Object, e As EventArgs) Handles btnStartHost.Click
        Dim port As Integer
        If Not Integer.TryParse(txtLocalPort.Text, port) Then Log("Port invalide.") : Return

        _displayName = If(String.IsNullOrWhiteSpace(txtName.Text), "Host", txtName.Text.Trim())
        _isHost = True
        SaveSettings()

        _path = New DirectPath(port)

        Dim localPeer As New PeerDescriptor With {
        .Identity = New IdentityBundle With {.DisplayName = _displayName},
        .Endpoints = New List(Of IPEndPoint) From {New IPEndPoint(IPAddress.Any, port)}
    }

        Dim res = Await _path.ProbeAsync(localPeer, Nothing, CancellationToken.None)
        Log($"Hosting on port {port}: {res.Notes}")
        If Not res.Success Then Return

        _cts = New CancellationTokenSource()

        ' Init du hub et branchements UI
        _hub = New RelayHub(port) With {.HostDisplayName = _displayName}

        AddHandler _hub.PeerListUpdated, Sub(names) UpdatePeers(names)
        AddHandler _hub.LogLine, Sub(t) Log(t)
        AddHandler _hub.MessageArrived, Sub(senderName, text) Log($"{senderName}: {text}")
        AddHandler _hub.PrivateArrived,
        Sub(senderName, dest, text)
            If String.Equals(dest, _displayName, StringComparison.OrdinalIgnoreCase) Then
                AppendToPrivate(senderName, senderName, text)
            End If
        End Sub

        ' ✅ FileSignal a 2 paramètres (kind, payload)
        AddHandler _hub.FileSignal,
        Sub(kind As String, payload As String)
            Select Case kind
                Case "FILEMETA" : HandleFileMeta(payload)
                Case "FILECHUNK" : HandleFileChunk(payload)
                Case "FILEEND" : HandleFileEnd(payload)
            End Select
        End Sub

        ' ✅ IceSignal a 2 paramètres (kind, payload)
        AddHandler _hub.IceSignal,
        Sub(kind As String, payload As String)
            Log("[ICE] " & kind & " " & payload, verbose:=True)
        End Sub

        ' Accepte plusieurs clients en boucle (via DirectPath)
        Task.Run(Async Sub()
                     While Not _cts.IsCancellationRequested
                         Try
                             Dim s = Await _path.ConnectAsync(Nothing, _cts.Token) ' accepte un client
                             _hub.AddClient("Client", s)
                         Catch ex As Exception
                             Log("Accept failed: " & ex.Message)
                         End Try
                     End While
                 End Sub)
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

            ' Notifie le host de mon nom
            Await _stream.SendAsync(System.Text.Encoding.UTF8.GetBytes(MessageProtocol.TAG_NAME & _displayName), CancellationToken.None)

            ' Attend messages du host (peers, chat, privés, fichiers)
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

        Dim payload As String = $"{Proto.TAG_MSG}{_displayName}:{msg}"
        Dim data As Byte() = System.Text.Encoding.UTF8.GetBytes(payload)

        If _isHost Then
            If _hub Is Nothing Then
                Log("Hub non initialisé.") : Return
            End If
            ' ✅ Le hub prend une String
            Await _hub.BroadcastFromHostAsync(payload)
        Else
            If _stream Is Nothing Then
                Log("Not connected.") : Return
            End If
            ' ✅ Le stream prend des bytes
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

            ' META (string)
            Dim meta As String = $"{Proto.TAG_FILEMETA}{transferId}:{_displayName}:{dest}:{fi.Name}:{fi.Length}"

            If _isHost Then
                If _hub Is Nothing Then Log("Hub non initialisé.") : Return

                ' ✅ Le hub prend des String
                Await _hub.SendToAsync(dest, meta)

                Using fs = fi.OpenRead()
                    Dim buffer(32768 - 1) As Byte
                    Dim totalSent As Long = 0
                    Dim read As Integer
                    Do
                        read = fs.Read(buffer, 0, buffer.Length)
                        If read > 0 Then
                            Dim chunk = Convert.ToBase64String(buffer, 0, read)
                            Dim chunkMsg As String = $"{Proto.TAG_FILECHUNK}{transferId}:{chunk}"
                            ' ✅ string vers le hub
                            Await _hub.SendToAsync(dest, chunkMsg)
                            totalSent += read

                            ' Progression
                            Dim percent = CInt((totalSent * 100L) \ fi.Length)
                            pbSend.Value = percent
                            lblSendProgress.Text = percent & "%"
                            Log($"[Send] {totalSent}/{fi.Length} bytes", verbose:=True)
                        End If
                    Loop While read > 0
                End Using

                Dim endMsg As String = $"{Proto.TAG_FILEEND}{transferId}"
                Await _hub.SendToAsync(dest, endMsg)

            Else
                ' Côté client : on envoie au host en bytes
                If _stream Is Nothing Then Log("Not connected.") : Return

                ' META en bytes
                Await _stream.SendAsync(System.Text.Encoding.UTF8.GetBytes(meta), CancellationToken.None)

                Using fs = fi.OpenRead()
                    Dim buffer(32768 - 1) As Byte
                    Dim totalSent As Long = 0
                    Dim read As Integer
                    Do
                        read = fs.Read(buffer, 0, buffer.Length)
                        If read > 0 Then
                            Dim chunk = Convert.ToBase64String(buffer, 0, read)
                            Dim chunkMsg As String = $"{Proto.TAG_FILECHUNK}{transferId}:{chunk}"
                            Await _stream.SendAsync(System.Text.Encoding.UTF8.GetBytes(chunkMsg), CancellationToken.None)
                            totalSent += read

                            Dim percent = CInt((totalSent * 100L) \ fi.Length)
                            pbSend.Value = percent
                            lblSendProgress.Text = percent & "%"
                            Log($"[Send] {totalSent}/{fi.Length} bytes", verbose:=True)
                        End If
                    Loop While read > 0
                End Using

                Dim endMsg As String = $"{Proto.TAG_FILEEND}{transferId}"
                Await _stream.SendAsync(System.Text.Encoding.UTF8.GetBytes(endMsg), CancellationToken.None)
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

        ' Je suis destinataire ?
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
                    Dim parts = msg.Split(":"c, 3)
                    If parts.Length >= 3 Then Log($"{parts(1)}: {parts(2)}")

                ElseIf msg.StartsWith(Proto.TAG_PRIV) Then
                    ' PRIV:sender:dest:message
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
    ' ======== Chat privé (fenêtres) ===========
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

        ' crée la fenêtre avec callback d’envoi
        frm = New PrivateChatForm(_displayName, peer,
            Sub(text)
                SendPrivateMessage(peer, text)
            End Sub)

        _privateChats(peer) = frm
        frm.Show(Me)
    End Sub

    Private Sub EnsurePrivateChat(peer As String)
        If String.IsNullOrWhiteSpace(peer) Then Return
        Dim frm As PrivateChatForm = Nothing
        If Not _privateChats.TryGetValue(peer, frm) OrElse frm Is Nothing OrElse frm.IsDisposed Then
            OpenPrivateChat(peer)
        End If
    End Sub

    Private Sub AppendToPrivate(peer As String, senderName As String, message As String)
        EnsurePrivateChat(peer)
        Dim frm As PrivateChatForm = Nothing
        If _privateChats.TryGetValue(peer, frm) AndAlso frm IsNot Nothing AndAlso Not frm.IsDisposed Then
            frm.AppendMessage(senderName, message)
        End If
    End Sub

    Private Async Sub SendPrivateMessage(dest As String, text As String)
        Dim payload As String = $"{Proto.TAG_PRIV}{_displayName}:{dest}:{text}"
        Dim data As Byte() = System.Text.Encoding.UTF8.GetBytes(payload)

        If _isHost Then
            If _hub Is Nothing Then
                Log("[Privé] Hub non initialisé.")
                Return
            End If
            ' ✅ Le hub attend une string
            Await _hub.SendToAsync(dest, payload)
        Else
            If _stream Is Nothing Then
                Log("[Privé] Non connecté au host.")
                Return
            End If
            ' ✅ Le stream prend des bytes
            Await _stream.SendAsync(data, CancellationToken.None)
        End If
    End Sub


End Class
