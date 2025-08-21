Option Strict On
Imports System.Net
Imports System.Net.Sockets
Imports System.IO
Imports System.Text
Imports System.Threading
Imports ChatP2P.Core                    ' P2PManager, INetworkStream
Imports ChatP2P.App                     ' RelayHub, TcpNetworkStreamAdapter
Imports ChatP2P.App.Protocol            ' Tags
Imports Proto = ChatP2P.App.Protocol.Tags

Public Class Form1

    ' --------- Etat global ----------
    Private _cts As CancellationTokenSource
    Private _isHost As Boolean = False

    ' Côté client (chat principal vers host)
    Private _stream As INetworkStream

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
        P2PManager.Init(
            sendSignal:=Function(dest As String, line As String)
                            Dim bytes = ToLineBytes(line) ' ajoute \n
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
        ' Quand un texte P2P arrive → ouvrir/activer la fenêtre correspondante
        AddHandler P2PManager.OnP2PText,
            Sub(peer As String, text As String)
                If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
                    Me.BeginInvoke(Sub() EnsurePrivateChat(peer))
                Else
                    EnsurePrivateChat(peer)
                End If
            End Sub
        AddHandler P2PManager.OnP2PState,
    Sub(peer As String, connected As Boolean)
        Log($"[P2P] {peer}: " & If(connected, "connecté", "déconnecté"), verbose:=True)
    End Sub


    End Sub

    Private Sub Form1_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        SaveSettings()
        Try
            RemoveHandler P2PManager.OnP2PText, Nothing ' pas obligatoire si process se termine
            RemoveHandler P2PManager.OnP2PState, Nothing
        Catch
        End Try
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

    ' --- Helpers lignes ---
    Private Function ToLineBytes(s As String) As Byte()
        Return Encoding.UTF8.GetBytes(s & vbLf)
    End Function

    Private Function ExtractLines(ByRef sb As StringBuilder, chunk As String) As List(Of String)
        sb.Append(chunk)
        Dim res As New List(Of String)
        While True
            Dim txt = sb.ToString()
            Dim idx = txt.IndexOf(vbLf, StringComparison.Ordinal)
            If idx < 0 Then Exit While
            Dim line = txt.Substring(0, idx)
            res.Add(line)
            sb.Remove(0, idx + 1)
        End While
        Return res
    End Function

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
    Private Sub btnStartHost_Click(sender As Object, e As EventArgs) Handles btnStartHost.Click
        Dim port As Integer
        If Not Integer.TryParse(txtLocalPort.Text, port) Then Log("Port invalide.") : Return

        _displayName = If(String.IsNullOrWhiteSpace(txtName.Text), "Host", txtName.Text.Trim())
        _isHost = True
        SaveSettings()

        _hub = New RelayHub(port) With {.HostDisplayName = _displayName}
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

        _hub.Start()
        Log($"Hub en écoute sur {port} (host='{_displayName}').")
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

        ' RE-init P2PManager AVEC le bon nom local côté client
        P2PManager.Init(
            sendSignal:=Function(dest As String, line As String)
                            Dim bytes = ToLineBytes(line)
                            If _stream IsNot Nothing Then
                                Return _stream.SendAsync(bytes, CancellationToken.None)
                            End If
                            Return Task.CompletedTask
                        End Function,
            localDisplayName:=_displayName
        )

        Try
            _cts = New CancellationTokenSource()

            ' Connexion TCP au hub
            Dim cli As New TcpClient()
            Await cli.ConnectAsync(ip, port)
            _stream = New TcpNetworkStreamAdapter(cli)

            Log($"Connecté au hub {ip}:{port}")
            Await _stream.SendAsync(ToLineBytes(Proto.TAG_NAME & _displayName), CancellationToken.None)

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

        If _isHost Then
            If _hub Is Nothing Then Log("Hub non initialisé.") : Return
            Await _hub.BroadcastFromHostAsync(ToLineBytes(payload))
        Else
            If _stream Is Nothing Then Log("Not connected.") : Return
            Await _stream.SendAsync(ToLineBytes(payload), CancellationToken.None)
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

            If _isHost Then
                If _hub Is Nothing Then Log("Hub non initialisé.") : Return
                Await _hub.SendToAsync(dest, ToLineBytes(meta))

                Using fs = fi.OpenRead()
                    Dim buffer(32768 - 1) As Byte
                    Dim totalSent As Long = 0
                    Dim read As Integer
                    Do
                        read = fs.Read(buffer, 0, buffer.Length)
                        If read > 0 Then
                            Dim chunk = Convert.ToBase64String(buffer, 0, read)
                            Dim chunkMsg = $"{Proto.TAG_FILECHUNK}{transferId}:{chunk}"
                            Await _hub.SendToAsync(dest, ToLineBytes(chunkMsg))
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
                Await _hub.SendToAsync(dest, ToLineBytes(endMsg))
            Else
                If _stream Is Nothing Then Log("Not connected.") : Return

                Await _stream.SendAsync(ToLineBytes(meta), CancellationToken.None)

                Using fs = fi.OpenRead()
                    Dim buffer(32768 - 1) As Byte
                    Dim totalSent As Long = 0
                    Dim read As Integer
                    Do
                        read = fs.Read(buffer, 0, buffer.Length)
                        If read > 0 Then
                            Dim chunk = Convert.ToBase64String(buffer, 0, read)
                            Dim chunkMsg = $"{Proto.TAG_FILECHUNK}{transferId}:{chunk}"
                            Await _stream.SendAsync(ToLineBytes(chunkMsg), CancellationToken.None)
                            totalSent += read

                            Dim percent = CInt((totalSent * 100L) \ fi.Length)
                            pbSend.Value = percent
                            lblSendProgress.Text = percent & "%"

                            Log($"[Send] {totalSent}/{fi.Length} bytes", verbose:=True)
                        End If
                    Loop While read > 0
                End Using

                Dim endMsg = $"{Proto.TAG_FILEEND}{transferId}"
                Await _stream.SendAsync(ToLineBytes(endMsg), CancellationToken.None)
            End If

            Log($"Fichier {fi.Name} envoyé à {dest}")
        End Using
    End Sub

    ' =========================================
    ' ======= Handlers FICHIERS (communs) =====
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
    ' ========= Boucle réception CLIENT ========
    ' =========================================
    Private Async Sub ListenIncomingClient(s As INetworkStream, ct As CancellationToken)
        Dim sb As New StringBuilder()
        Try
            While Not ct.IsCancellationRequested
                Dim data = Await s.ReceiveAsync(ct)
                Dim chunk = Encoding.UTF8.GetString(data)
                Dim lines = ExtractLines(sb, chunk)
                For Each msg In lines
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

                    ElseIf msg.StartsWith(Proto.TAG_ICE_OFFER) Then
                        Dim p = msg.Substring(Proto.TAG_ICE_OFFER.Length).Split(":"c, 3)
                        If p.Length = 3 Then
                            Dim fromPeer = p(0)
                            Dim sdp = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                            P2PManager.HandleOffer(fromPeer, sdp, New String() {"stun:stun.l.google.com:19302"})
                        End If

                    ElseIf msg.StartsWith(Proto.TAG_ICE_ANSWER) Then
                        Dim p = msg.Substring(Proto.TAG_ICE_ANSWER.Length).Split(":"c, 3)
                        If p.Length = 3 Then
                            Dim fromPeer = p(0)
                            Dim sdp = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                            P2PManager.HandleAnswer(fromPeer, sdp)
                        End If

                    ElseIf msg.StartsWith(Proto.TAG_ICE_CAND) Then
                        Dim p = msg.Substring(Proto.TAG_ICE_CAND.Length).Split(":"c, 3)
                        If p.Length = 3 Then
                            Dim fromPeer = p(0)
                            Dim cand = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                            P2PManager.HandleCandidate(fromPeer, cand)
                        End If
                    End If
                Next
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

        ' 1) P2P direct si session dispo
        Try
            If ChatP2P.Core.P2PManager.TrySendP2P(dest, text) Then
                Exit Sub
            End If
        Catch
        End Try

        ' 2) Fallback immédiat via hub/host
        Dim data = ToLineBytes($"{Proto.TAG_PRIV}{_displayName}:{dest}:{text}")

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
                                Dim bytes = ToLineBytes(line)
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
            ElseIf _stream IsNot Nothing Then
                _stream.SendAsync(ToLineBytes(Proto.TAG_NAME & newName), CancellationToken.None)
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
        If e.KeyCode = Keys.Enter Then
            e.SuppressKeyPress = True
            e.Handled = True
            SaveSettings()
        End If
    End Sub

End Class
