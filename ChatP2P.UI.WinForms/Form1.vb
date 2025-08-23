' ChatP2P.UI.WinForms/Form1.vb
Option Strict On
Imports System.Net
Imports System.Net.Sockets
Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Security.Cryptography
Imports ChatP2P.Core                    ' P2PManager, INetworkStream
Imports ChatP2P.App                     ' RelayHub
Imports ChatP2P.App.Protocol
Imports Proto = ChatP2P.App.Protocol.Tags

Public Class Form1
    ' === Réception fichiers (tous rôles) ===
    Private Class FileRecvState
        Public File As FileStream
        Public FileName As String
        Public Expected As Long
        Public Received As Long
    End Class

    ' === Conteneur simple pour l'identité Ed25519 (évite les tuples) ===
    Private Class Ed25519Identity
        Public Pub As Byte()
        Public Priv As Byte()
    End Class

    Private ReadOnly _fileRecv As New Dictionary(Of String, FileRecvState)()
    Private _cts As CancellationTokenSource
    Private _isHost As Boolean = False

    Private _stream As INetworkStream
    Private _displayName As String = "Me"
    Private _recvFolder As String = ""
    Private _hub As RelayHub

    ' TabControl privé (si pas dans le Designer)
    Private tabPrivates As TabControl

    ' Si tu utilises des fenêtres privées
    Private ReadOnly _privateChats As New Dictionary(Of String, PrivateChatForm)()

    ' ===== Load / Settings =====
    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' Crée un TabControl si besoin
        If tabPrivates Is Nothing Then
            tabPrivates = New TabControl() With {
                .Name = "tabPrivates",
                .Dock = DockStyle.Bottom,
                .Height = 200
            }
            Controls.Add(tabPrivates)
        End If

        ' === Identité: générer/persister Ed25519 si besoin (AppData\ChatP2P\id_ed25519.bin) ===
        Try
            Dim id As Ed25519Identity = LoadOrCreateEd25519Identity()
            ChatP2P.Core.P2PManager.SetIdentity(id.Pub, id.Priv)
        Catch ex As Exception
            Log("[ID] init failed: " & ex.Message)
        End Try

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

        ' Init P2PManager (routing signaling ICE)
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

        ' P2P events
        AddHandler P2PManager.OnP2PText, AddressOf OnP2PText_FromP2P
        AddHandler P2PManager.OnP2PState,
            Sub(peer As String, connected As Boolean)
                Log($"[P2P] {peer}: " & If(connected, "connecté", "déconnecté"), verbose:=True)
            End Sub

        ' Log identité vérifiée / échec
        AddHandler P2PManager.OnPeerIdentityVerified,
            Sub(peer As String, idpub As Byte(), ok As Boolean)
                Try
                    Using sha As SHA256 = SHA256.Create()
                        Dim fp = BitConverter.ToString(sha.ComputeHash(idpub)).Replace("-", "").ToLowerInvariant()
                        Log($"[ID] {peer}: " & If(ok, "OK", "FAIL") & " pub=" & Convert.ToBase64String(idpub) & " fp256=" & fp)
                    End Using
                Catch
                    Log($"[ID] {peer}: " & If(ok, "OK", "FAIL"))
                End Try
            End Sub
    End Sub

    ' Reçoit un message DataChannel depuis P2PManager
    Private Sub OnP2PText_FromP2P(peer As String, text As String)
        If Me.IsDisposed Then Return
        If Me.InvokeRequired Then
            Me.BeginInvoke(Sub() OnP2PText_FromP2P(peer, text))
            Return
        End If

        If _privateChats.Count > 0 Then
            EnsurePrivateChat(peer)
            AppendToPrivate(peer, peer, text)
            Dim frm As PrivateChatForm = Nothing
            If _privateChats.TryGetValue(peer, frm) AndAlso frm IsNot Nothing AndAlso Not frm.IsDisposed Then
                Try
                    If Not frm.Visible Then frm.Show(Me)
                    frm.Activate()
                    frm.BringToFront()
                Catch
                End Try
            End If
            Return
        End If

        EnsurePrivateTab(peer)
        AppendToPrivateTab(peer, peer, text)
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

    ' ===== UI helpers =====
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

        If _privateChats IsNot Nothing Then
            OpenPrivateChat(sel)
        Else
            EnsurePrivateTab(sel)
            tabPrivates.SelectedTab = tabPrivates.TabPages(sel)
        End If
    End Sub

    ' ===== Host =====
    Private Sub btnStartHost_Click(sender As Object, e As EventArgs) Handles btnStartHost.Click
        Dim port As Integer
        If Not Integer.TryParse(txtLocalPort.Text, port) Then Log("Port invalide.") : Return

        _displayName = If(String.IsNullOrWhiteSpace(txtName.Text), "Host", txtName.Text.Trim())
        _isHost = True
        SaveSettings()

        _hub = New RelayHub(port) With {.HostDisplayName = _displayName}
        AddHandler _hub.PeerListUpdated, Sub(names) UpdatePeers(names)
        AddHandler _hub.LogLine, Sub(t) Log(t)
        AddHandler _hub.MessageArrived, Sub(senderName, text) Log($"{senderName}: {text}")
        AddHandler _hub.PrivateArrived,
            Sub(senderName, dest, text)
                If String.Equals(dest, _displayName, StringComparison.OrdinalIgnoreCase) Then
                    If _privateChats.Count > 0 Then
                        EnsurePrivateChat(senderName)
                        AppendToPrivate(senderName, senderName, text)
                    Else
                        EnsurePrivateTab(senderName)
                        AppendToPrivateTab(senderName, senderName, text)
                    End If
                End If
            End Sub
        AddHandler _hub.FileSignal, New RelayHub.FileSignalEventHandler(AddressOf OnHubFileSignal)
        AddHandler _hub.IceSignal, New RelayHub.IceSignalEventHandler(AddressOf OnHubIceSignal)

        _hub.Start()
        Log($"Hub en écoute sur {port} (host='{_displayName}').")
    End Sub

    ' ===== Client =====
    Private Async Sub btnConnect_Click(sender As Object, e As EventArgs) Handles btnConnect.Click
        Dim ip As IPAddress
        If Not IPAddress.TryParse(txtRemoteIp.Text, ip) Then Log("IP invalide.") : Return

        Dim port As Integer
        If Not Integer.TryParse(txtRemotePort.Text, port) Then Log("Port invalide.") : Return

        _displayName = If(String.IsNullOrWhiteSpace(txtName.Text), "Client", txtName.Text.Trim())
        _isHost = False
        SaveSettings()

        Try
            _cts = New CancellationTokenSource()

            Dim cli As New TcpClient()
            Await cli.ConnectAsync(ip, port)
            _stream = New TcpNetworkStreamAdapter(cli)

            Log($"Connecté au hub {ip}:{port}")
            Await _stream.SendAsync(Encoding.UTF8.GetBytes(Proto.TAG_NAME & _displayName), CancellationToken.None)

            ListenIncomingClient(_stream, _cts.Token)
        Catch ex As Exception
            Log($"Connect failed: {ex.Message}")
        End Try
    End Sub

    ' ===== Envoi chat général =====
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

    ' ===== Fichiers (relay) =====
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

            pbSend.Value = 0
            lblSendProgress.Text = "0%"

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
                            Dim percent = CInt((totalSent * 100L) \ fi.Length)
                            pbSend.Value = percent : lblSendProgress.Text = percent & "%"
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
                            pbSend.Value = percent : lblSendProgress.Text = percent & "%"
                        End If
                    Loop While read > 0
                End Using

                Dim endMsg = $"{Proto.TAG_FILEEND}{transferId}"
                Await _stream.SendAsync(Encoding.UTF8.GetBytes(endMsg), CancellationToken.None)
            End If

            Log($"Fichier {fi.Name} envoyé à {dest}")
        End Using
    End Sub

    ' ===== Handlers fichiers =====
    Private Sub HandleFileMeta(msg As String)
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
            iAmDest = True
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
        Dim st As New FileRecvState With {.File = fs, .FileName = savePath, .Expected = fsize, .Received = 0}
        SyncLock _fileRecv : _fileRecv(tid) = st : End SyncLock

        pbRecv.Value = 0 : lblRecvProgress.Text = "0%"
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
        pbRecv.Value = percent : lblRecvProgress.Text = percent & "%"
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
            Try : st.File.Flush() : st.File.Close() : Catch : End Try
            pbRecv.Value = 100 : lblRecvProgress.Text = "100%"
            Log($"Fichier reçu → {st.FileName}")
        End If
    End Sub

    Private Function MakeUniquePath(path As String) As String
        Dim dir = IO.Path.GetDirectoryName(path)
        Dim name = IO.Path.GetFileNameWithoutExtension(path)
        Dim ext = IO.Path.GetExtension(path)
        Dim i = 1
        Dim candidate = path
        While File.Exists(candidate)
            candidate = IO.Path.Combine(dir, $"{name} ({i}){ext}")
            i += 1
        End While
        Return candidate
    End Function

    ' ===== Listen côté client =====
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
                    If parts.Length = 3 Then Log($"{parts(1)}: {parts(2)}")

                ElseIf msg.StartsWith(Proto.TAG_PRIV) Then
                    Dim rest = msg.Substring(Proto.TAG_PRIV.Length)
                    Dim parts = rest.Split(":"c, 3)
                    If parts.Length = 3 Then
                        Dim fromPeer = parts(0)
                        Dim toPeer = parts(1)
                        Dim body = parts(2)
                        If String.Equals(toPeer, _displayName, StringComparison.OrdinalIgnoreCase) Then
                            If _privateChats.Count > 0 Then
                                EnsurePrivateChat(fromPeer)
                                AppendToPrivate(fromPeer, fromPeer, body)
                            Else
                                EnsurePrivateTab(fromPeer)
                                AppendToPrivateTab(fromPeer, fromPeer, body)
                            End If
                        End If
                    End If

                ElseIf msg.StartsWith(Proto.TAG_FILEMETA) Then
                    HandleFileMeta(msg)
                ElseIf msg.StartsWith(Proto.TAG_FILECHUNK) Then
                    HandleFileChunk(msg)
                ElseIf msg.StartsWith(Proto.TAG_FILEEND) Then
                    HandleFileEnd(msg)

                ElseIf msg.StartsWith(Proto.TAG_ICE_OFFER) _
                    OrElse msg.StartsWith(Proto.TAG_ICE_ANSWER) _
                    OrElse msg.StartsWith(Proto.TAG_ICE_CAND) Then

                    Log("[ICE] signal reçu: " & msg.Substring(0, Math.Min(80, msg.Length)), verbose:=True)
                    OnHubIceSignal(msg)
                End If
            End While
        Catch ex As Exception
            Log($"Déconnecté: {ex.Message}")
        End Try
    End Sub

    ' ===== ICE signals =====
    Private Sub OnHubFileSignal(raw As String)
        If raw.StartsWith(Proto.TAG_FILEMETA) Then HandleFileMeta(raw)
        If raw.StartsWith(Proto.TAG_FILECHUNK) Then HandleFileChunk(raw)
        If raw.StartsWith(Proto.TAG_FILEEND) Then HandleFileEnd(raw)
    End Sub

    Private Sub OnHubIceSignal(raw As String)
        Try
            If raw.StartsWith(Proto.TAG_ICE_OFFER) Then
                Dim p = raw.Substring(Proto.TAG_ICE_OFFER.Length).Split(":"c, 3)
                If p.Length = 3 Then
                    Dim fromPeer = p(0)
                    Dim sdp = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                    P2PManager.HandleOffer(fromPeer, sdp, New String() {"stun:stun.l.google.com:19302"})
                End If
            ElseIf raw.StartsWith(Proto.TAG_ICE_ANSWER) Then
                Dim p = raw.Substring(Proto.TAG_ICE_ANSWER.Length).Split(":"c, 3)
                If p.Length = 3 Then
                    Dim fromPeer = p(0)
                    Dim sdp = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                    P2PManager.HandleAnswer(fromPeer, sdp)
                End If
            ElseIf raw.StartsWith(Proto.TAG_ICE_CAND) Then
                Dim p = raw.Substring(Proto.TAG_ICE_CAND.Length).Split(":"c, 3)
                If p.Length = 3 Then
                    Dim fromPeer = p(0)
                    Dim cand = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                    P2PManager.HandleCandidate(fromPeer, cand)
                End If
            End If
        Catch ex As Exception
            Log("[ICE] parse error: " & ex.Message, verbose:=True)
        End Try
    End Sub

    ' ===== Fenêtres privées (option) =====
    Private Sub OpenPrivateChat(peer As String)
        Dim frm As PrivateChatForm = Nothing
        If _privateChats.TryGetValue(peer, frm) Then
            If frm IsNot Nothing AndAlso Not frm.IsDisposed Then
                frm.BringToFront() : frm.Focus() : Exit Sub
            Else
                _privateChats.Remove(peer)
            End If
        End If
        frm = New PrivateChatForm(_displayName, peer, Sub(text) SendPrivateMessage(peer, text))
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
            Try
                If Not frm.Visible Then frm.Show(Me)
                frm.Activate()
                frm.BringToFront()
            Catch
            End Try
        End If
    End Sub

    ' ===== Onglets privés via TabControl (si pas de PrivateChatForm) =====
    Private Sub EnsurePrivateTab(peer As String)
        If tabPrivates Is Nothing Then Return
        If Not tabPrivates.TabPages.ContainsKey(peer) Then
            Dim tp As New TabPage(peer) With {.Name = peer}
            Dim tb As New TextBox() With {
                .Multiline = True,
                .Dock = DockStyle.Fill,
                .ScrollBars = ScrollBars.Vertical,
                .ReadOnly = True
            }
            tp.Controls.Add(tb)
            tabPrivates.TabPages.Add(tp)
        End If
    End Sub

    Private Sub AppendToPrivateTab(peer As String, fromPeer As String, body As String)
        If tabPrivates Is Nothing Then Return
        If Not tabPrivates.TabPages.ContainsKey(peer) Then Return
        Dim tb = TryCast(tabPrivates.TabPages(peer).Controls(0), TextBox)
        If tb IsNot Nothing Then tb.AppendText($"{fromPeer}: {body}{Environment.NewLine}")
    End Sub

    Private Async Sub SendPrivateMessage(dest As String, text As String)
        If String.IsNullOrWhiteSpace(dest) OrElse String.IsNullOrWhiteSpace(text) Then Return
        Try
            If ChatP2P.Core.P2PManager.TrySendP2P(dest, text) Then Exit Sub
        Catch
        End Try

        Dim payload = $"{Proto.TAG_PRIV}{_displayName}:{dest}:{text}"
        Dim data = Encoding.UTF8.GetBytes(payload)

        If _isHost Then
            If _hub Is Nothing Then Log("[Privé] Hub non initialisé.") : Return
            Await _hub.SendToAsync(dest, data)
        Else
            If _stream Is Nothing Then Log("[Privé] Non connecté au host.") : Return
            Await _stream.SendAsync(data, CancellationToken.None)
        End If
    End Sub

    ' ===== Entrées clavier =====
    Private Sub txtMessage_KeyDown(sender As Object, e As KeyEventArgs) Handles txtMessage.KeyDown
        If e.KeyCode = Keys.Enter Then e.SuppressKeyPress = True : e.Handled = True : btnSend.PerformClick()
    End Sub

    Private Sub txtName_KeyDown(sender As Object, e As KeyEventArgs) Handles txtName.KeyDown
        If e.KeyCode = Keys.Enter Then
            e.SuppressKeyPress = True : e.Handled = True
            Dim newName = txtName.Text.Trim()
            If String.IsNullOrWhiteSpace(newName) Then Return
            _displayName = newName
            SaveSettings()

            P2PManager.Init(
                sendSignal:=Function(dest As String, line As String)
                                Dim bytes = Encoding.UTF8.GetBytes(line)
                                If _isHost Then
                                    If _hub IsNot Nothing Then Return _hub.SendToAsync(dest, bytes)
                                    Return Task.CompletedTask
                                Else
                                    If _stream IsNot Nothing Then Return _stream.SendAsync(bytes, CancellationToken.None)
                                    Return Task.CompletedTask
                                End If
                            End Function,
                localDisplayName:=_displayName
            )

            If (Not _isHost) AndAlso (_stream IsNot Nothing) Then
                Dim data = Encoding.UTF8.GetBytes(Proto.TAG_NAME & newName)
                _stream.SendAsync(data, CancellationToken.None)
                Log($"[Info] Client name → {newName} (notif au hub)")
            ElseIf _isHost Then
                Log($"[Info] Host name → {newName}")
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

    ' === Persistence identité (AppData) ===
    Private Function LoadOrCreateEd25519Identity() As Ed25519Identity
        Dim dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChatP2P")
        Dim pathFile = Path.Combine(dir, "id_ed25519.bin") ' format: [32 pub][64 priv] (96 bytes)

        If System.IO.File.Exists(pathFile) Then
            Dim all = System.IO.File.ReadAllBytes(pathFile)
            If all IsNot Nothing AndAlso all.Length = 96 Then
                Dim pub(31) As Byte, priv(63) As Byte
                Buffer.BlockCopy(all, 0, pub, 0, 32)
                Buffer.BlockCopy(all, 32, priv, 0, 64)
                Return New Ed25519Identity With {.Pub = pub, .Priv = priv}
            End If
        End If

        Directory.CreateDirectory(dir)
        Dim kp = Sodium.PublicKeyAuth.GenerateKeyPair() ' pub=32, priv=64
        Dim blob(95) As Byte
        Buffer.BlockCopy(kp.PublicKey, 0, blob, 0, 32)
        Buffer.BlockCopy(kp.PrivateKey, 0, blob, 32, 64)
        System.IO.File.WriteAllBytes(pathFile, blob)
        Return New Ed25519Identity With {.Pub = kp.PublicKey, .Priv = kp.PrivateKey}
    End Function
End Class
