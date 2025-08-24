' ChatP2P.UI.WinForms/Form1.vb
Option Strict On
Imports System
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports System.Globalization
Imports System.Security.Cryptography
Imports ChatP2P.Core                 ' P2PManager
Imports ChatP2P.App                  ' RelayHub
Imports ChatP2P.App.Protocol
Imports Proto = ChatP2P.App.Protocol.Tags

Public Class Form1

    ' ========== Const ==========
    Private Const MSG_TERM As String = vbLf   ' délimiteur de messages TCP

    ' ======== Types internes ========
    Private Class FileRecvState
        Public File As FileStream
        Public FileName As String = ""
        Public Expected As Long
        Public Received As Long
        Public LastTickUtc As DateTime
    End Class

    ' ======== Champs UI/état ========
    Private _cts As CancellationTokenSource
    Private _isHost As Boolean = False

    Private _stream As INetworkStream ' côté client (TcpNetworkStreamAdapter)
    Private _displayName As String = "Me"
    Private _recvFolder As String = ""
    Private _hub As RelayHub

    ' P2P/crypto/identité (affichage simple)
    Private ReadOnly _idVerified As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _peerFp As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _p2pConn As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _cryptoActive As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)

    ' Chats privés par fenêtre
    Private ReadOnly _privateChats As New Dictionary(Of String, PrivateChatForm)(StringComparer.OrdinalIgnoreCase)

    ' Réception fichiers en cours
    Private ReadOnly _fileRecv As New Dictionary(Of String, FileRecvState)(StringComparer.Ordinal)
    Private _fileWatchdog As System.Windows.Forms.Timer

    ' Fichier identité Ed25519 (persistant)
    Private ReadOnly _idFilePath As String =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "ChatP2P", "identity.bin")

    ' Buffer accumulateur pour découpe des messages réseau (client)
    Private ReadOnly _cliBuf As New StringBuilder()

    ' Anti‑doublons P2P: fenêtre 2s des derniers messages normalisés par pair
    Private ReadOnly _recentP2P As New Dictionary(Of String, List(Of (DateTime, String)))(StringComparer.OrdinalIgnoreCase)

    ' ========== Utils anti‑doublons / normalisation ==========
    Private Function Canon(ByVal s As String) As String
        If s Is Nothing Then Return String.Empty
        Dim r As String = s.Replace(vbCr, "")
        Do While r.EndsWith(vbLf, StringComparison.Ordinal)
            r = r.Substring(0, r.Length - 1)
        Loop
        Return r.TrimEnd()
    End Function

    Private Function SeenRecently(peer As String, text As String) As Boolean
        Dim nowUtc = DateTime.UtcNow
        Dim list As List(Of (DateTime, String)) = Nothing
        If Not _recentP2P.TryGetValue(peer, list) OrElse list Is Nothing Then
            list = New List(Of (DateTime, String))()
            _recentP2P(peer) = list
        End If

        ' purge > 2s
        list.RemoveAll(Function(it) (nowUtc - it.Item1).TotalSeconds > 2.0)

        Dim token = If(text, String.Empty)
        If token.Length > 256 Then token = token.Substring(0, 256) & "#" & text.Length.ToString()

        If list.Any(Function(it) it.Item2 = token) Then Return True
        list.Add((nowUtc, token))
        If list.Count > 20 Then list.RemoveAt(0)
        Return False
    End Function

    ' ======== Form Load / Settings ========
    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' Identité locale Ed25519 persistée (pub=32, priv=64)
        Try
            Dim id = LoadOrCreateEd25519Identity()
            ' si tu as encore ces API côté core, décommente:
            ' P2PManager.SetIdentity(id.Pub, id.Priv)
            ' P2PManager.SetIdentityInfo(id.Pub)
        Catch ex As Exception
            Log("[ID] init failed: " & ex.Message)
        End Try

        ' Clé X25519 statique (facultatif selon ton core)
        Try
            Dim kp = ChatP2P.Crypto.KexX25519.GenerateKeyPair()
            ' P2PManager.InitializeCrypto(kp.priv, kp.pub)
        Catch
        End Try

        ' Charger préférences UI
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
        AddHandler lstPeers.SelectedIndexChanged, Sub() UpdateSelectedPeerStatuses()

        ' Init P2PManager (routing signaling ICE)
        InitP2PManager()

        ' Suivi des évènements Core pour MAJ état
        AddHandler P2PManager.OnP2PState,
            Sub(peer As String, connected As Boolean)
                _p2pConn(peer) = connected
                If Not connected Then _cryptoActive(peer) = False
                UpdateSelectedPeerStatuses()
            End Sub

        AddHandler P2PManager.OnP2PText, AddressOf OnP2PText_FromP2P

        ' --- Watchdog réceptions fichiers ---
        _fileWatchdog = New System.Windows.Forms.Timer()
        _fileWatchdog.Interval = 1500
        AddHandler _fileWatchdog.Tick, AddressOf FileWatchdog_Tick
        _fileWatchdog.Start()
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

    ' ======== UI helpers ========
    Private Sub Log(s As String, Optional verbose As Boolean = False)
        If verbose AndAlso Not chkVerbose.Checked Then Return
        If txtLog IsNot Nothing AndAlso txtLog.IsHandleCreated AndAlso txtLog.InvokeRequired Then
            txtLog.BeginInvoke(Sub() txtLog.AppendText(s & Environment.NewLine))
        ElseIf txtLog IsNot Nothing Then
            txtLog.AppendText(s & Environment.NewLine)
        End If
    End Sub

    ' Met à jour les étiquettes d’état (auth/crypto) selon le peer sélectionné.
    Private Sub UpdateSelectedPeerStatuses()
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(New MethodInvoker(AddressOf UpdateSelectedPeerStatuses))
            Return
        End If

        If lstPeers Is Nothing OrElse lstPeers.SelectedItem Is Nothing Then
            If lblAuthStatus IsNot Nothing Then lblAuthStatus.Text = "Auth: —"
            If lblCryptoStatus IsNot Nothing Then lblCryptoStatus.Text = "Crypto: —"
            Return
        End If

        Dim peer As String = CStr(lstPeers.SelectedItem)

        Dim p2p As Boolean = False
        If _p2pConn.ContainsKey(peer) Then p2p = _p2pConn(peer)

        Dim cry As Boolean = False
        If _cryptoActive.ContainsKey(peer) Then cry = _cryptoActive(peer)

        If lblAuthStatus IsNot Nothing Then
            lblAuthStatus.Text = "Auth: —"
        End If

        If lblCryptoStatus IsNot Nothing Then
            Dim cryptoText As String = If(cry AndAlso p2p, "ON", If(p2p, "P2P", "OFF"))
            lblCryptoStatus.Text = "Crypto: " & cryptoText
        End If
    End Sub

    Private Sub UpdatePeers(peers As List(Of String))
        If lstPeers Is Nothing Then Return
        If lstPeers.IsHandleCreated AndAlso lstPeers.InvokeRequired Then
            lstPeers.BeginInvoke(Sub() UpdatePeers(peers))
            Return
        End If
        ' nettoie & déduplique
        Dim cleaned = peers.
            Where(Function(p) Not String.IsNullOrWhiteSpace(p)).
            Select(Function(p) p.Trim()).
            Distinct(StringComparer.OrdinalIgnoreCase).
            ToList()
        lstPeers.Items.Clear()
        For Each p In cleaned
            lstPeers.Items.Add(p)
        Next
    End Sub

    Private Sub lstPeers_DoubleClick(sender As Object, e As EventArgs)
        Dim sel = TryCast(lstPeers.SelectedItem, String)
        If String.IsNullOrWhiteSpace(sel) Then Return
        If String.Equals(sel, _displayName, StringComparison.OrdinalIgnoreCase) Then Return
        OpenPrivateChat(sel)
    End Sub

    ' ======== Host ========
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
                    EnsurePrivateChat(senderName)
                    AppendToPrivate(senderName, senderName, text)
                End If
            End Sub
        AddHandler _hub.FileSignal, New RelayHub.FileSignalEventHandler(AddressOf OnHubFileSignal)

        _hub.Start()

        Log($"Hub en écoute sur {port} (host='{_displayName}').")
    End Sub

    ' ======== Client ========
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
            Await _stream.SendAsync(Encoding.UTF8.GetBytes(Proto.TAG_NAME & _displayName & MSG_TERM), CancellationToken.None)

            ListenIncomingClient(_stream, _cts.Token)
        Catch ex As Exception
            Log($"Connect failed: {ex.Message}")
        End Try
    End Sub

    ' ======== Envoi chat général (relay) ========
    Private Async Sub btnSend_Click(sender As Object, e As EventArgs) Handles btnSend.Click
        Dim msg = txtMessage.Text.Trim()
        If msg = "" Then Return

        Dim payload = $"{Proto.TAG_MSG}{_displayName}:{msg}{MSG_TERM}"
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

    ' ======== Fichiers (relay) ========
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

            If pbSend IsNot Nothing Then pbSend.Value = 0
            If lblSendProgress IsNot Nothing Then lblSendProgress.Text = "0%"

            Dim meta = $"{Proto.TAG_FILEMETA}{transferId}:{_displayName}:{dest}:{fi.Name}:{fi.Length}{MSG_TERM}"
            Dim metaBytes = Encoding.UTF8.GetBytes(meta)

            If _isHost Then
                If _hub Is Nothing Then Log("Hub non initialisé.") : Return
                Await _hub.SendToAsync(dest, metaBytes)

                Using fs = fi.OpenRead()
                    Dim buffer(32768 - 1) As Byte
                    Dim totalSent As Long = 0
                    While True
                        Dim read = fs.Read(buffer, 0, buffer.Length)
                        If read <= 0 Then Exit While
                        Dim chunk = Convert.ToBase64String(buffer, 0, read)
                        Dim chunkMsg = $"{Proto.TAG_FILECHUNK}{transferId}:{chunk}{MSG_TERM}"
                        Await _hub.SendToAsync(dest, Encoding.UTF8.GetBytes(chunkMsg))
                        totalSent += read
                        Dim percent = CInt((totalSent * 100L) \ Math.Max(1L, fi.Length))
                        If pbSend IsNot Nothing Then pbSend.Value = Math.Max(0, Math.Min(100, percent))
                        If lblSendProgress IsNot Nothing Then lblSendProgress.Text = pbSend.Value & "%"
                    End While
                End Using

                Dim endMsg = $"{Proto.TAG_FILEEND}{transferId}{MSG_TERM}"
                Await _hub.SendToAsync(dest, Encoding.UTF8.GetBytes(endMsg))
            Else
                If _stream Is Nothing Then Log("Not connected.") : Return

                Await _stream.SendAsync(metaBytes, CancellationToken.None)

                Using fs = fi.OpenRead()
                    Dim buffer(32768 - 1) As Byte
                    Dim totalSent As Long = 0
                    While True
                        Dim read = fs.Read(buffer, 0, buffer.Length)
                        If read <= 0 Then Exit While
                        Dim chunk = Convert.ToBase64String(buffer, 0, read)
                        Dim chunkMsg = $"{Proto.TAG_FILECHUNK}{transferId}:{chunk}{MSG_TERM}"
                        Await _stream.SendAsync(Encoding.UTF8.GetBytes(chunkMsg), CancellationToken.None)
                        totalSent += read
                        Dim percent = CInt((totalSent * 100L) \ Math.Max(1L, fi.Length))
                        If pbSend IsNot Nothing Then pbSend.Value = Math.Max(0, Math.Min(100, percent))
                        If lblSendProgress IsNot Nothing Then lblSendProgress.Text = pbSend.Value & "%"
                    End While
                End Using

                Dim endMsg = $"{Proto.TAG_FILEEND}{transferId}{MSG_TERM}"
                Await _stream.SendAsync(Encoding.UTF8.GetBytes(endMsg), CancellationToken.None)
            End If

            Log($"[P2P] Fichier {fi.Name} envoyé à {dest}")
        End Using
    End Sub

    ' ======== Réception fichiers ========
    Private Sub HandleFileMeta(msg As String)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() HandleFileMeta(msg))
            Return
        End If

        Dim parts = msg.Split(":"c, 6)
        If parts.Length < 6 Then Return
        Dim tid = parts(1).Trim()
        Dim fromName = parts(2).Trim()
        Dim dest = parts(3).Trim()
        Dim fname = parts(4).Trim()
        Dim sizeStr = parts(5).Trim()

        Dim fsize As Long = 0
        Long.TryParse(sizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, fsize)

        Dim iAmDest As Boolean = If(_isHost,
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

        Dim fs As New FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.Read, 32768, FileOptions.SequentialScan)
        Dim st As New FileRecvState With {
            .File = fs, .FileName = savePath, .Expected = fsize, .Received = 0, .LastTickUtc = DateTime.UtcNow
        }
        SyncLock _fileRecv : _fileRecv(tid) = st : End SyncLock

        If pbRecv IsNot Nothing Then pbRecv.Value = 0
        If lblRecvProgress IsNot Nothing Then lblRecvProgress.Text = "0%"
        Log($"[FICHIER] META reçu de {fromName} → {fname} ({fsize} bytes)")
    End Sub

    Private Sub HandleFileChunk(msg As String)
        Dim parts = msg.Split(":"c, 3)
        If parts.Length < 3 Then Return
        Dim tid = parts(1).Trim()
        Dim chunkB64 = parts(2).Trim()

        Dim st As FileRecvState = Nothing
        SyncLock _fileRecv
            If _fileRecv.ContainsKey(tid) Then st = _fileRecv(tid)
        End SyncLock
        If st Is Nothing Then Return

        Dim bytes As Byte()
        Try
            bytes = Convert.FromBase64String(chunkB64)
        Catch
            Return
        End Try

        Try
            st.File.Write(bytes, 0, bytes.Length)
            st.Received += bytes.Length
            st.LastTickUtc = DateTime.UtcNow
        Catch ex As Exception
            Log("[FICHIER] Erreur écriture: " & ex.Message)
            Return
        End Try

        Dim percent = CInt((st.Received * 100L) \ Math.Max(1L, st.Expected))
        If percent < 0 Then percent = 0
        If percent > 100 Then percent = 100
        If pbRecv IsNot Nothing Then pbRecv.Value = percent
        If lblRecvProgress IsNot Nothing Then lblRecvProgress.Text = percent & "%"
    End Sub

    Private Sub HandleFileEnd(msg As String)
        Dim parts = msg.Split(":"c, 2)
        If parts.Length < 2 Then Return
        Dim tid = parts(1).Trim()

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

            If pbRecv IsNot Nothing Then pbRecv.Value = 100
            If lblRecvProgress IsNot Nothing Then lblRecvProgress.Text = "100%"
            Log($"[FICHIER] Fichier reçu → {st.FileName}")
        End If
    End Sub

    Private Sub FileWatchdog_Tick(sender As Object, e As EventArgs)
        Dim toClose As New List(Of String)()
        SyncLock _fileRecv
            For Each kv In _fileRecv
                Dim st = kv.Value
                If st IsNot Nothing Then
                    Dim stalled As Boolean = (DateTime.UtcNow - st.LastTickUtc).TotalSeconds > 30.0
                    If stalled AndAlso st.Received = 0 Then
                        toClose.Add(kv.Key)
                    End If
                End If
            Next
            For Each tid In toClose
                Try
                    Dim st = _fileRecv(tid)
                    Try : st.File.Flush() : st.File.Close() : Catch : End Try
                    _fileRecv.Remove(tid)
                    Log($"[FICHIER] Transfert {tid} abandonné: timeout d’inactivité")
                Catch
                End Try
            Next
        End SyncLock
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

    ' ======== Client listener (relay) ========
    Private Async Sub ListenIncomingClient(s As INetworkStream, ct As CancellationToken)
        Try
            While Not ct.IsCancellationRequested
                Dim data = Await s.ReceiveAsync(ct)
                Dim chunk = Encoding.UTF8.GetString(data)

                _cliBuf.Append(chunk)

                While True
                    Dim full = _cliBuf.ToString()
                    Dim idx = full.IndexOf(MSG_TERM, StringComparison.Ordinal)
                    If idx < 0 Then Exit While

                    Dim line = full.Substring(0, idx)
                    _cliBuf.Remove(0, idx + MSG_TERM.Length)

                    Dim msg = line.Trim()
                    If msg.Length = 0 Then Continue While

                    If msg.StartsWith(Proto.TAG_PEERS, StringComparison.Ordinal) Then
                        Dim peers = msg.Substring(Proto.TAG_PEERS.Length).Split(";"c).ToList()
                        UpdatePeers(peers)

                    ElseIf msg.StartsWith(Proto.TAG_MSG, StringComparison.Ordinal) Then
                        Dim parts = msg.Split(":"c, 3)
                        If parts.Length = 3 Then
                            Log(parts(1) & ": " & parts(2))
                        End If

                    ElseIf msg.StartsWith(Proto.TAG_PRIV, StringComparison.Ordinal) Then
                        Dim rest = msg.Substring(Proto.TAG_PRIV.Length)
                        Dim parts = rest.Split(":"c, 3) ' sender:dest:body
                        If parts.Length = 3 Then
                            Dim fromPeer = parts(0)
                            Dim toPeer = parts(1)
                            Dim body = parts(2)
                            If String.Equals(toPeer, _displayName, StringComparison.OrdinalIgnoreCase) Then
                                EnsurePrivateChat(fromPeer)
                                AppendToPrivate(fromPeer, fromPeer, body)
                            End If
                        End If

                    ElseIf msg.StartsWith(Proto.TAG_FILEMETA, StringComparison.Ordinal) Then
                        HandleFileMeta(msg)
                    ElseIf msg.StartsWith(Proto.TAG_FILECHUNK, StringComparison.Ordinal) Then
                        HandleFileChunk(msg)
                    ElseIf msg.StartsWith(Proto.TAG_FILEEND, StringComparison.Ordinal) Then
                        HandleFileEnd(msg)

                    ElseIf msg.StartsWith(Proto.TAG_ICE_OFFER, StringComparison.Ordinal) _
                        OrElse msg.StartsWith(Proto.TAG_ICE_ANSWER, StringComparison.Ordinal) _
                        OrElse msg.StartsWith(Proto.TAG_ICE_CAND, StringComparison.Ordinal) Then

                        Log("[ICE] signal reçu: " & msg.Substring(0, Math.Min(80, msg.Length)), verbose:=True)
                        OnHubIceSignal(msg)
                    End If
                End While
            End While
        Catch ex As Exception
            Log($"Déconnecté: {ex.Message}")
        End Try
    End Sub

    ' ======== ICE signals dispatch ========
    Private Sub OnHubFileSignal(raw As String)
        If raw.StartsWith(Proto.TAG_FILEMETA, StringComparison.Ordinal) Then HandleFileMeta(raw)
        If raw.StartsWith(Proto.TAG_FILECHUNK, StringComparison.Ordinal) Then HandleFileChunk(raw)
        If raw.StartsWith(Proto.TAG_FILEEND, StringComparison.Ordinal) Then HandleFileEnd(raw)
    End Sub

    Private Sub OnHubIceSignal(raw As String)
        ' ✅ Empêche toute tentative de P2P côté host
        If _isHost Then Exit Sub

        Try
            If raw.StartsWith(Proto.TAG_ICE_OFFER, StringComparison.Ordinal) Then
                Dim p = raw.Substring(Proto.TAG_ICE_OFFER.Length).Split(":"c, 3)
                If p.Length = 3 Then
                    Dim fromPeer = p(0)

                    ' ✅ Ignore les messages qui viendraient du host lui-même ou de soi-même
                    If String.Equals(fromPeer, "Server", StringComparison.OrdinalIgnoreCase) _
                   OrElse String.Equals(fromPeer, _displayName, StringComparison.OrdinalIgnoreCase) Then Exit Sub

                    Dim sdp = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                    P2PManager.HandleOffer(fromPeer, sdp, New String() {"stun:stun.l.google.com:19302"})
                End If

            ElseIf raw.StartsWith(Proto.TAG_ICE_ANSWER, StringComparison.Ordinal) Then
                Dim p = raw.Substring(Proto.TAG_ICE_ANSWER.Length).Split(":"c, 3)
                If p.Length = 3 Then
                    Dim fromPeer = p(0)
                    If String.Equals(fromPeer, "Server", StringComparison.OrdinalIgnoreCase) _
                   OrElse String.Equals(fromPeer, _displayName, StringComparison.OrdinalIgnoreCase) Then Exit Sub

                    Dim sdp = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                    P2PManager.HandleAnswer(fromPeer, sdp)
                End If

            ElseIf raw.StartsWith(Proto.TAG_ICE_CAND, StringComparison.Ordinal) Then
                Dim p = raw.Substring(Proto.TAG_ICE_CAND.Length).Split(":"c, 3)
                If p.Length = 3 Then
                    Dim fromPeer = p(0)
                    If String.Equals(fromPeer, "Server", StringComparison.OrdinalIgnoreCase) _
                   OrElse String.Equals(fromPeer, _displayName, StringComparison.OrdinalIgnoreCase) Then Exit Sub

                    Dim cand = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                    P2PManager.HandleCandidate(fromPeer, cand)
                End If
            End If
        Catch ex As Exception
            Log("[ICE] parse error: " & ex.Message, verbose:=True)
        End Try
    End Sub

    ' ======== Texte P2P reçu ========
    Private Sub OnP2PText_FromP2P(peer As String, text As String)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() OnP2PText_FromP2P(peer, text))
            Return
        End If

        Dim norm As String = Canon(text)
        If SeenRecently(peer, norm) Then Return

        EnsurePrivateChat(peer)
        AppendToPrivate(peer, peer, "[P2P] " & norm) ' <= tag ici
    End Sub


    ' ======== Privé : helpers fenêtres ========
    Private Sub OpenPrivateChat(peer As String)
        If String.IsNullOrWhiteSpace(peer) Then Return

        Dim frm As PrivateChatForm = Nothing
        If _privateChats.TryGetValue(peer, frm) Then
            If frm IsNot Nothing AndAlso Not frm.IsDisposed Then
                If Not frm.Visible Then frm.Show(Me)
                frm.Activate()
                frm.BringToFront()
                Exit Sub
            Else
                _privateChats.Remove(peer)
            End If
        End If

        Dim sendCb As Action(Of String) =
            Sub(text As String)
                SendPrivateMessage(peer, text)
            End Sub

        frm = New PrivateChatForm(_displayName, peer, sendCb)
        _privateChats(peer) = frm
        frm.Show(Me)
        frm.Activate()
        frm.BringToFront()
    End Sub

    Private Sub EnsurePrivateChat(peer As String)
        If String.IsNullOrWhiteSpace(peer) Then Return
        Dim frm As PrivateChatForm = Nothing
        If Not _privateChats.TryGetValue(peer, frm) OrElse frm Is Nothing OrElse frm.IsDisposed Then
            OpenPrivateChat(peer)
        End If
    End Sub

    Private Sub AppendToPrivate(peer As String, senderName As String, message As String)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() AppendToPrivate(peer, senderName, message))
            Return
        End If

        Dim frm As PrivateChatForm = Nothing
        If _privateChats.TryGetValue(peer, frm) AndAlso frm IsNot Nothing AndAlso Not frm.IsDisposed Then
            frm.AppendMessage(senderName, message)
            If Not frm.Visible Then frm.Show(Me)
            frm.Activate()
            frm.BringToFront()
        Else
            OpenPrivateChat(peer)
            If _privateChats.TryGetValue(peer, frm) AndAlso frm IsNot Nothing AndAlso Not frm.IsDisposed Then
                frm.AppendMessage(senderName, message)
            End If
        End If
    End Sub

    Private Async Sub SendPrivateMessage(dest As String, text As String)
        If String.IsNullOrWhiteSpace(dest) OrElse String.IsNullOrWhiteSpace(text) Then Return

        Dim canonText As String = Canon(text)

        Try
            ' Tentative P2P directe
            If ChatP2P.Core.P2PManager.TrySendP2P(dest, canonText) Then
                ' Affichage UNE SEULE FOIS côté émetteur, avec le tag [P2P]
                Log($"[P2P] moi → {dest}: {canonText}")
                AppendToPrivate(dest, _displayName, "[P2P] " & canonText)
                Exit Sub
            End If
        Catch
            ' ignore et on retombe en relay
        End Try

        ' --- Chemin relay ---
        Dim payload = $"{Proto.TAG_PRIV}{_displayName}:{dest}:{canonText}{MSG_TERM}"
        Dim data = Encoding.UTF8.GetBytes(payload)

        If _isHost Then
            If _hub Is Nothing Then Log("[Privé] Hub non initialisé.") : Return
            Await _hub.SendToAsync(dest, data)
        Else
            If _stream Is Nothing Then Log("[Privé] Non connecté au host.") : Return
            Await _stream.SendAsync(data, CancellationToken.None)
        End If

        ' Affichage UNE SEULE FOIS côté émetteur, sans tag (puisque c'est relay)
        AppendToPrivate(dest, _displayName, canonText)
    End Sub




    ' ======== Signaling vers Core (Init) ========
    Private Sub InitP2PManager()
        P2PManager.Init(
            sendSignal:=Function(dest As String, line As String)
                            Dim bytes = Encoding.UTF8.GetBytes(line & MSG_TERM)
                            If _isHost Then
                                If _hub IsNot Nothing Then
                                    Return _hub.SendToAsync(dest, bytes)
                                End If
                                Return Threading.Tasks.Task.CompletedTask
                            Else
                                If _stream IsNot Nothing Then
                                    Return _stream.SendAsync(bytes, CancellationToken.None)
                                End If
                                Return Threading.Tasks.Task.CompletedTask
                            End If
                        End Function,
            localDisplayName:=_displayName
        )

        AddHandler P2PManager.OnLog, Sub(peer, l) Log("[P2P] " & l, verbose:=True)
    End Sub

    ' ======== Saisie/Prefs ========
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

            InitP2PManager()

            If (Not _isHost) AndAlso (_stream IsNot Nothing) Then
                Dim data = Encoding.UTF8.GetBytes(Proto.TAG_NAME & newName & MSG_TERM)
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

    ' ======= Utilitaires identité (Ed25519) & KEX (X25519) =======
    Private Function LoadOrCreateEd25519Identity() As (Pub As Byte(), Priv As Byte())
        Dim dir = Path.GetDirectoryName(_idFilePath)
        If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
        If File.Exists(_idFilePath) Then
            Dim all = File.ReadAllBytes(_idFilePath)
            If all IsNot Nothing AndAlso all.Length = (32 + 64) Then
                Dim pubKey(31) As Byte, privKey(63) As Byte
                Buffer.BlockCopy(all, 0, pubKey, 0, 32)
                Buffer.BlockCopy(all, 32, privKey, 0, 64)
                Return (pubKey, privKey)
            End If
        End If
        ' Génération par réflexion (utilise ChatP2P.Crypto.Ed25519 si présent)
        Dim edType As Type = Nothing
        For Each asm In AppDomain.CurrentDomain.GetAssemblies()
            edType = asm.GetType("ChatP2P.Crypto.Ed25519", False)
            If edType IsNot Nothing Then Exit For
        Next
        If edType Is Nothing Then Throw New MissingMethodException("ChatP2P.Crypto.Ed25519 type not found.")
        Dim m = edType.GetMethod("GenerateKeyPair", Reflection.BindingFlags.Public Or Reflection.BindingFlags.Static)
        If m Is Nothing Then Throw New MissingMethodException("Ed25519.GenerateKeyPair not found.")
        Dim kv = m.Invoke(Nothing, Nothing)
        Dim pubKey2 As Byte() = TryGetFieldOrProp(Of Byte())(kv, {"pub", "Pub", "PublicKey"})
        Dim privKey2 As Byte() = TryGetFieldOrProp(Of Byte())(kv, {"priv", "Priv", "PrivateKey", "SecretKey"})
        If pubKey2 Is Nothing OrElse privKey2 Is Nothing Then Throw New Exception("Invalid Ed25519 pair.")
        Dim all2(32 + 64 - 1) As Byte
        Buffer.BlockCopy(pubKey2, 0, all2, 0, 32)
        Buffer.BlockCopy(privKey2, 0, all2, 32, 64)
        File.WriteAllBytes(_idFilePath, all2)
        Return (pubKey2, privKey2)
    End Function

    Private Function GenerateX25519KeyPairReflect() As (priv As Byte(), pub As Byte())
        Dim kexType As Type = Nothing

        kexType = Type.GetType("ChatP2P.Crypto.KexX25519, ChatP2P.Crypto", throwOnError:=False)
        If kexType Is Nothing Then
            Try
                Dim asm = System.Reflection.Assembly.Load("ChatP2P.Crypto")
                kexType = asm.GetType("ChatP2P.Crypto.KexX25519", throwOnError:=False)
            Catch
            End Try
        End If
        If kexType Is Nothing Then
            For Each asm In AppDomain.CurrentDomain.GetAssemblies()
                Dim t = asm.GetType("ChatP2P.Crypto.KexX25519", False)
                If t IsNot Nothing Then kexType = t : Exit For
            Next
        End If
        If kexType Is Nothing Then Throw New MissingMethodException("ChatP2P.Crypto.KexX25519 type not found.")

        Dim m = kexType.GetMethod("GenerateKeyPair", Reflection.BindingFlags.Public Or Reflection.BindingFlags.Static)
        If m Is Nothing Then Throw New MissingMethodException("KexX25519.GenerateKeyPair not found.")
        Dim kv = m.Invoke(Nothing, Nothing)

        Dim pub As Byte() = TryGetFieldOrProp(Of Byte())(kv, New String() {"pub", "Pub", "PublicKey"})
        Dim priv As Byte() = TryGetFieldOrProp(Of Byte())(kv, New String() {"priv", "Priv", "PrivateKey"})
        If priv Is Nothing OrElse pub Is Nothing Then Throw New Exception("Invalid X25519 keypair object returned.")
        Return (priv, pub)
    End Function

    Private Function TryGetFieldOrProp(Of TRes)(obj As Object, names As IEnumerable(Of String)) As TRes
        If obj Is Nothing Then Return Nothing
        Dim tp = obj.GetType()
        For Each nm In names
            Dim pi = tp.GetProperty(nm, Reflection.BindingFlags.Public Or Reflection.BindingFlags.Instance)
            If pi IsNot Nothing Then
                Dim v = pi.GetValue(obj, Nothing)
                If TypeOf v Is TRes Then Return CType(v, TRes)
            End If
            Dim fi = tp.GetField(nm, Reflection.BindingFlags.Public Or Reflection.BindingFlags.Instance)
            If fi IsNot Nothing Then
                Dim v = fi.GetValue(obj)
                If TypeOf v Is TRes Then Return CType(v, TRes)
            End If
        Next
        Return Nothing
    End Function

    ' ======== Security Center (bouton) ========
    Private Sub btnSecurity_Click(sender As Object, e As EventArgs) Handles btnSecurity.Click
        Try
            Dim f As New SecurityCenterForm(_idFilePath) With {
                .PeersProvider = Function() _peerFp.Keys.ToList(),
                .PeerStatusProvider = Function(peer As String)
                                          Dim st = (Verified:=False, Fp:="", P2PConnected:=False, CryptoActive:=False)
                                          If _idVerified.ContainsKey(peer) Then st.Verified = _idVerified(peer)
                                          If _peerFp.ContainsKey(peer) Then st.Fp = _peerFp(peer)
                                          If _p2pConn.ContainsKey(peer) Then st.P2PConnected = _p2pConn(peer)
                                          If _cryptoActive.ContainsKey(peer) Then st.CryptoActive = _cryptoActive(peer)
                                          Return st
                                      End Function
            }
            f.ShowDialog(Me)
        Catch ex As Exception
            MessageBox.Show("Erreur à l'ouverture du Security Center: " & ex.Message,
                            "Security Center", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

End Class
