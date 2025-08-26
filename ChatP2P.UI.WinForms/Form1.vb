' ChatP2P.UI.WinForms/Form1.vb
Option Strict On
Imports System
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports System.Globalization
Imports System.Reflection
Imports ChatP2P.Core                 ' P2PManager + LocalDb
Imports ChatP2P.App                  ' RelayHub
Imports ChatP2P.App.Protocol
Imports Proto = ChatP2P.App.Protocol.Tags

Public Class Form1

    Private Const MSG_TERM As String = vbLf

    Private Class FileRecvState
        Public File As FileStream
        Public FileName As String = ""
        Public Expected As Long
        Public Received As Long
        Public LastTickUtc As DateTime
    End Class

    Private _cts As CancellationTokenSource
    Private _isHost As Boolean = False

    Private _stream As INetworkStream
    Private _displayName As String = "Me"
    Private _recvFolder As String = ""
    Private _hub As RelayHub

    Private ReadOnly _idVerified As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _peerFp As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _p2pConn As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _cryptoActive As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)

    Private ReadOnly _privateChats As New Dictionary(Of String, PrivateChatForm)(StringComparer.OrdinalIgnoreCase)

    ' Pagination historisation
    Private ReadOnly _histCount As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)

    Private ReadOnly _fileRecv As New Dictionary(Of String, FileRecvState)(StringComparer.Ordinal)
    Private _fileWatchdog As System.Windows.Forms.Timer

    Private ReadOnly _cliBuf As New StringBuilder()
    Private ReadOnly _recentP2P As New Dictionary(Of String, List(Of (DateTime, String)))(StringComparer.OrdinalIgnoreCase)

    ' ---------- Utils ----------
    Private Function Canon(ByVal s As String) As String
        If s Is Nothing Then Return String.Empty
        Dim r As String = s.Replace(vbCr, "")
        Do While r.EndsWith(vbLf, StringComparison.Ordinal) : r = r.Substring(0, r.Length - 1) : Loop
        Return r.TrimEnd()
    End Function

    Private Function SeenRecently(peer As String, text As String) As Boolean
        Dim nowUtc = DateTime.UtcNow
        Dim list As List(Of (DateTime, String)) = Nothing
        If Not _recentP2P.TryGetValue(peer, list) OrElse list Is Nothing Then
            list = New List(Of (DateTime, String))()
            _recentP2P(peer) = list
        End If
        list.RemoveAll(Function(it) (nowUtc - it.Item1).TotalSeconds > 2.0)

        Dim token = If(text, String.Empty)
        If token.Length > 256 Then token = token.Substring(0, 256) & "#" & text.Length.ToString()

        If list.Any(Function(it) it.Item2 = token) Then Return True
        list.Add((nowUtc, token))
        If list.Count > 20 Then list.RemoveAt(0)
        Return False
    End Function

    ' ---------- Strict / Trust helpers ----------
    Private Function IsStrictEnabled() As Boolean
        Try
            If chkStrictTrust IsNot Nothing Then Return chkStrictTrust.Checked
        Catch
        End Try
        ' fallback My.Settings.StrictTrust si présent
        Try
            Dim pi = GetType(My.MySettings).GetProperty("StrictTrust", BindingFlags.Instance Or BindingFlags.Public)
            If pi IsNot Nothing Then
                Dim v = pi.GetValue(My.Settings, Nothing)
                If v IsNot Nothing Then Return CBool(v)
            End If
        Catch
        End Try
        Return False
    End Function

    Private Function IsPeerTrusted(peer As String) As Boolean
        Try
            Dim obj = LocalDb.ExecScalar(Of Object)(
                "SELECT Trusted FROM Peers WHERE Name=@n;",
                LocalDb.P("@n", peer)
            )
            If obj Is Nothing OrElse obj Is DBNull.Value Then Return False
            Return Convert.ToInt32(obj, CultureInfo.InvariantCulture) <> 0
        Catch
            Return False
        End Try
    End Function

    Private Sub PersistStrictToSettingsIfPossible()
        Try
            If chkStrictTrust Is Nothing Then Return
            Dim pi = GetType(My.MySettings).GetProperty("StrictTrust", BindingFlags.Instance Or BindingFlags.Public)
            If pi IsNot Nothing Then
                pi.SetValue(My.Settings, chkStrictTrust.Checked, Nothing)
                My.Settings.Save()
            End If
        Catch
        End Try
    End Sub

    ' ---------- Form ----------
    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' DB
        Try : ChatP2P.Core.LocalDb.Init() : Catch ex As Exception : Log("[DB] init failed: " & ex.Message) : End Try

        ' (SUPPR) Aucune lecture d'identity.bin / Ed25519 via fichier

        ' (Optionnel) Génération X25519 si nécessaire par le core
        Try : Dim kp = ChatP2P.Crypto.KexX25519.GenerateKeyPair() : Catch : End Try

        ' Prefs
        Try
            If Not String.IsNullOrWhiteSpace(My.Settings.DisplayName) Then txtName.Text = My.Settings.DisplayName
            If Not String.IsNullOrWhiteSpace(My.Settings.LocalPort) Then txtLocalPort.Text = My.Settings.LocalPort
            If Not String.IsNullOrWhiteSpace(My.Settings.RemoteIp) Then txtRemoteIp.Text = My.Settings.RemoteIp
            If Not String.IsNullOrWhiteSpace(My.Settings.RemotePort) Then txtRemotePort.Text = My.Settings.RemotePort
            If Not String.IsNullOrWhiteSpace(My.Settings.RecvFolder) AndAlso Directory.Exists(My.Settings.RecvFolder) Then
                _recvFolder = My.Settings.RecvFolder
            End If
            ' charger StrictTrust si dispo
            Try
                Dim pi = GetType(My.MySettings).GetProperty("StrictTrust", BindingFlags.Instance Or BindingFlags.Public)
                If pi IsNot Nothing AndAlso chkStrictTrust IsNot Nothing Then
                    Dim v = pi.GetValue(My.Settings, Nothing)
                    If v IsNot Nothing Then chkStrictTrust.Checked = CBool(v)
                End If
            Catch
            End Try
        Catch
        End Try
        _displayName = If(String.IsNullOrWhiteSpace(txtName.Text), "Me", txtName.Text.Trim())

        AddHandler lstPeers.DoubleClick, AddressOf lstPeers_DoubleClick
        AddHandler lstPeers.SelectedIndexChanged, Sub() UpdateSelectedPeerStatuses()
        If chkStrictTrust IsNot Nothing Then
            AddHandler chkStrictTrust.CheckedChanged, Sub() PersistStrictToSettingsIfPossible()
        End If

        InitP2PManager()

        AddHandler P2PManager.OnP2PState,
            Sub(peer As String, connected As Boolean)
                _p2pConn(peer) = connected
                If Not connected Then _cryptoActive(peer) = False

                Dim frm As PrivateChatForm = Nothing
                If _privateChats.TryGetValue(peer, frm) AndAlso frm IsNot Nothing AndAlso Not frm.IsDisposed Then
                    frm.SetP2PState(connected)
                    frm.SetCryptoState(_cryptoActive.ContainsKey(peer) AndAlso _cryptoActive(peer))
                    frm.SetAuthState(_idVerified.ContainsKey(peer) AndAlso _idVerified(peer))
                End If
                UpdateSelectedPeerStatuses()
            End Sub

        AddHandler P2PManager.OnP2PText, AddressOf OnP2PText_FromP2P

        _fileWatchdog = New System.Windows.Forms.Timer() With {.Interval = 1500}
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
            ' StrictTrust si dispo
            PersistStrictToSettingsIfPossible()
            My.Settings.Save()
        Catch
        End Try
    End Sub

    ' ---------- Log ----------
    Private Sub Log(s As String, Optional verbose As Boolean = False)
        If verbose AndAlso Not (chkVerbose IsNot Nothing AndAlso chkVerbose.Checked) Then Return
        If txtLog IsNot Nothing AndAlso txtLog.IsHandleCreated AndAlso txtLog.InvokeRequired Then
            txtLog.BeginInvoke(Sub() txtLog.AppendText(s & Environment.NewLine))
        ElseIf txtLog IsNot Nothing Then
            txtLog.AppendText(s & Environment.NewLine)
        End If
    End Sub

    ' ---------- DB helpers ----------
    Private Sub EnsurePeerRow(peer As String)
        Try
            Dim nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            LocalDb.ExecNonQuery(
                "INSERT OR IGNORE INTO Peers(Name, LastSeenUtc) VALUES(@n,@ts);",
                LocalDb.P("@n", peer), LocalDb.P("@ts", nowIso)
            )
            LocalDb.ExecNonQuery(
                "UPDATE Peers SET LastSeenUtc=@ts WHERE Name=@n;",
                LocalDb.P("@ts", nowIso), LocalDb.P("@n", peer)
            )
        Catch ex As Exception
            Log("[DB] ensure peer failed: " & ex.Message, verbose:=True)
        End Try
    End Sub

    Private Sub StoreMsg(peer As String, outgoing As Boolean, body As String, viaP2P As Boolean)
        Try
            EnsurePeerRow(peer)
            Dim createdIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            Dim senderVal As String = If(outgoing, "me", peer)
            Dim dirVal As String = If(outgoing, "send", "recv")
            Dim isp2p As Integer = If(viaP2P, 1, 0)

            LocalDb.ExecNonQuery(
                "INSERT INTO Messages(PeerName, Sender, Body, IsP2P, Direction, CreatedUtc)
                 VALUES(@p,@s,@b,@i,@d,@c);",
                LocalDb.P("@p", peer),
                LocalDb.P("@s", senderVal),
                LocalDb.P("@b", body),
                LocalDb.P("@i", isp2p),
                LocalDb.P("@d", dirVal),
                LocalDb.P("@c", createdIso)
            )
        Catch ex As Exception
            Log("[DB] store failed: " & ex.Message, verbose:=True)
        End Try
    End Sub

    Private Sub LoadHistoryIntoPrivate(peer As String, frm As PrivateChatForm, take As Integer)
        Try
            Dim dt = LocalDb.Query(
                "SELECT Sender, Body, IsP2P, Direction, CreatedUtc
                   FROM Messages
                  WHERE PeerName=@p
               ORDER BY CreatedUtc DESC
                  LIMIT @lim;",
                LocalDb.P("@p", peer), LocalDb.P("@lim", take)
            )

            If frm.IsHandleCreated AndAlso frm.InvokeRequired Then
                frm.BeginInvoke(Sub() LoadHistoryApply(frm, peer, dt))
            Else
                LoadHistoryApply(frm, peer, dt)
            End If
        Catch ex As Exception
            Log("[DB] history load error: " & ex.Message, verbose:=True)
        End Try
    End Sub

    Private Sub LoadHistoryApply(frm As PrivateChatForm, peer As String, dt As Data.DataTable)
        If dt Is Nothing Then
            frm.ClearMessages()
            Return
        End If
        Dim rows = New List(Of Data.DataRow)
        For Each r As Data.DataRow In dt.Rows : rows.Add(r) : Next
        rows.Reverse() ' ASC

        frm.ClearMessages()
        For Each r In rows
            Dim dir As String = TryCast(r!Direction, String)
            Dim body As String = TryCast(r!Body, String)
            Dim isp2p As Boolean = False
            If Not IsDBNull(r!IsP2P) Then
                isp2p = (Convert.ToInt32(r!IsP2P, CultureInfo.InvariantCulture) <> 0)
            End If
            Dim sender As String =
                If(String.Equals(dir, "recv", StringComparison.OrdinalIgnoreCase),
                   peer, _displayName)
            Dim show = If(isp2p, "[P2P] ", "") & body
            frm.AppendMessage(sender, show)
        Next
    End Sub

    Private Sub PurgeHistory(peer As String, frm As PrivateChatForm)
        Try
            LocalDb.ExecNonQuery("DELETE FROM Messages WHERE PeerName=@p;", LocalDb.P("@p", peer))
            frm.ClearMessages()
            _histCount(peer) = 0
        Catch ex As Exception
            Log("[DB] purge error: " & ex.Message)
        End Try
    End Sub

    ' ---------- Status ----------
    Private Sub UpdateSelectedPeerStatuses()
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(New Action(AddressOf UpdateSelectedPeerStatuses))
            Return
        End If
        If lstPeers Is Nothing OrElse lstPeers.SelectedItem Is Nothing Then Return

        Dim peer As String = CStr(lstPeers.SelectedItem)
        Dim frm As PrivateChatForm = Nothing
        If _privateChats.TryGetValue(peer, frm) AndAlso frm IsNot Nothing AndAlso Not frm.IsDisposed Then
            Dim authOk As Boolean = _idVerified.ContainsKey(peer) AndAlso _idVerified(peer)
            Dim cryptoActive As Boolean = _cryptoActive.ContainsKey(peer) AndAlso _cryptoActive(peer)
            Dim connected As Boolean = _p2pConn.ContainsKey(peer) AndAlso _p2pConn(peer)

            frm.SetAuthState(authOk)
            frm.SetCryptoState(cryptoActive)
            frm.SetP2PState(connected)
        End If
    End Sub

    Private Sub UpdatePeers(peers As List(Of String))
        If lstPeers Is Nothing Then Return
        If lstPeers.IsHandleCreated AndAlso lstPeers.InvokeRequired Then
            lstPeers.BeginInvoke(Sub() UpdatePeers(peers))
            Return
        End If
        Dim cleaned = peers.
            Where(Function(p) Not String.IsNullOrWhiteSpace(p)).
            Select(Function(p) p.Trim()).
            Distinct(StringComparer.OrdinalIgnoreCase).
            ToList()
        lstPeers.Items.Clear()
        For Each peerName As String In cleaned
            lstPeers.Items.Add(peerName)
        Next
    End Sub

    Private Sub lstPeers_DoubleClick(sender As Object, e As EventArgs)
        Dim sel = TryCast(lstPeers.SelectedItem, String)
        If String.IsNullOrWhiteSpace(sel) Then Return
        If String.Equals(sel, _displayName, StringComparison.OrdinalIgnoreCase) Then Return
        OpenPrivateChat(sel)
    End Sub

    ' ---------- Host ----------
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
                    ' Strict: IN
                    If IsStrictEnabled() AndAlso Not IsPeerTrusted(senderName) Then
                        Log($"[SECURITY] Message RELAY IN bloqué (pair non de confiance): {senderName}")
                        Exit Sub
                    End If
                    EnsurePrivateChat(senderName)
                    AppendToPrivate(senderName, senderName, text)
                    StoreMsg(senderName, outgoing:=False, body:=Canon(text), viaP2P:=False)
                End If
            End Sub
        AddHandler _hub.FileSignal, New RelayHub.FileSignalEventHandler(AddressOf OnHubFileSignal)

        _hub.Start()
        Log($"Hub en écoute sur {port} (host='{_displayName}').")
    End Sub

    ' ---------- Client ----------
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

    ' ---------- Chat général (relay) ----------
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

    ' ---------- Fichiers (relay) ----------
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

    ' ---------- Fichiers RX ----------
    Private Sub HandleFileMeta(msg As String)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() HandleFileMeta(msg)) : Return
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

        Dim iAmDest As Boolean = If(_isHost, String.Equals(dest, _displayName, StringComparison.OrdinalIgnoreCase), True)
        If Not iAmDest Then Return

        ' Strict: on ne reçoit pas de fichiers de pairs non trusted
        If IsStrictEnabled() AndAlso Not IsPeerTrusted(fromName) Then
            Log($"[SECURITY] Fichier RELAY IN bloqué (pair non de confiance): {fromName}")
            Return
        End If

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
        Dim st As New FileRecvState With {.File = fs, .FileName = savePath, .Expected = fsize, .Received = 0, .LastTickUtc = DateTime.UtcNow}
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
        Try : bytes = Convert.FromBase64String(chunkB64) : Catch : Return : End Try

        Try
            st.File.Write(bytes, 0, bytes.Length)
            st.Received += bytes.Length
            st.LastTickUtc = DateTime.UtcNow
        Catch ex As Exception
            Log("[FICHIER] Erreur écriture: " & ex.Message) : Return
        End Try

        Dim percent = CInt((st.Received * 100L) \ Math.Max(1L, st.Expected))
        If pbRecv IsNot Nothing Then pbRecv.Value = Math.Max(0, Math.Min(100, percent))
        If lblRecvProgress IsNot Nothing Then lblRecvProgress.Text = pbRecv.Value & "%"
    End Sub

    Private Sub HandleFileEnd(msg As String)
        Dim parts = msg.Split(":"c, 2)
        If parts.Length < 2 Then Return
        Dim tid = parts(1).Trim()

        Dim st As FileRecvState = Nothing
        SyncLock _fileRecv
            If _fileRecv.ContainsKey(tid) Then st = _fileRecv(tid) : _fileRecv.Remove(tid)
        End SyncLock

        If st IsNot Nothing Then
            Try : st.File.Flush() : st.File.Close() : Catch : End Try
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
                    If stalled AndAlso st.Received = 0 Then toClose.Add(kv.Key)
                End If
            Next
            For Each tid As String In toClose
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

    ' ---------- Client listener ----------
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
                        If parts.Length = 3 Then Log(parts(1) & ": " & parts(2))

                    ElseIf msg.StartsWith(Proto.TAG_PRIV, StringComparison.Ordinal) Then
                        Dim rest = msg.Substring(Proto.TAG_PRIV.Length)
                        Dim parts = rest.Split(":"c, 3)
                        If parts.Length = 3 Then
                            Dim fromPeer = parts(0)
                            Dim toPeer = parts(1)
                            Dim body = parts(2)
                            If String.Equals(toPeer, _displayName, StringComparison.OrdinalIgnoreCase) Then
                                ' Strict: IN
                                If IsStrictEnabled() AndAlso Not IsPeerTrusted(fromPeer) Then
                                    Log($"[SECURITY] Message RELAY IN bloqué (pair non de confiance): {fromPeer}")
                                Else
                                    EnsurePrivateChat(fromPeer)
                                    AppendToPrivate(fromPeer, fromPeer, body)
                                    StoreMsg(fromPeer, outgoing:=False, body:=Canon(body), viaP2P:=False)
                                End If
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

    ' ---------- ICE dispatch ----------
    Private Sub OnHubFileSignal(raw As String)
        If raw.StartsWith(Proto.TAG_FILEMETA, StringComparison.Ordinal) Then HandleFileMeta(raw)
        If raw.StartsWith(Proto.TAG_FILECHUNK, StringComparison.Ordinal) Then HandleFileChunk(raw)
        If raw.StartsWith(Proto.TAG_FILEEND, StringComparison.Ordinal) Then HandleFileEnd(raw)
    End Sub

    Private Sub OnHubIceSignal(raw As String)
        If _isHost Then Exit Sub ' le host ne fait pas de P2P ici
        Try
            If raw.StartsWith(Proto.TAG_ICE_OFFER, StringComparison.Ordinal) Then
                Dim p = raw.Substring(Proto.TAG_ICE_OFFER.Length).Split(":"c, 3)
                If p.Length = 3 Then
                    Dim fromPeer = p(0)
                    If String.Equals(fromPeer, "Server", StringComparison.OrdinalIgnoreCase) _
                       OrElse String.Equals(fromPeer, _displayName, StringComparison.OrdinalIgnoreCase) Then Exit Sub

                    ' Strict: ignorer offres d'un non-trusted
                    If IsStrictEnabled() AndAlso Not IsPeerTrusted(fromPeer) Then
                        Log($"[SECURITY] Offre ICE rejetée (pair non de confiance): {fromPeer}")
                        Exit Sub
                    End If

                    Dim sdp = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                    P2PManager.HandleOffer(fromPeer, sdp, New String() {"stun:stun.l.google.com:19302"})
                End If

            ElseIf raw.StartsWith(Proto.TAG_ICE_ANSWER, StringComparison.Ordinal) Then
                Dim p = raw.Substring(Proto.TAG_ICE_ANSWER.Length).Split(":"c, 3)
                If p.Length = 3 Then
                    Dim fromPeer = p(0)
                    If String.Equals(fromPeer, "Server", StringComparison.OrdinalIgnoreCase) _
                       OrElse String.Equals(fromPeer, _displayName, StringComparison.OrdinalIgnoreCase) Then Exit Sub

                    If IsStrictEnabled() AndAlso Not IsPeerTrusted(fromPeer) Then
                        Log($"[SECURITY] Réponse ICE ignorée (pair non de confiance): {fromPeer}")
                        Exit Sub
                    End If

                    Dim sdp = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                    P2PManager.HandleAnswer(fromPeer, sdp)
                End If

            ElseIf raw.StartsWith(Proto.TAG_ICE_CAND, StringComparison.Ordinal) Then
                Dim p = raw.Substring(Proto.TAG_ICE_CAND.Length).Split(":"c, 3)
                If p.Length = 3 Then
                    Dim fromPeer = p(0)
                    If String.Equals(fromPeer, "Server", StringComparison.OrdinalIgnoreCase) _
                       OrElse String.Equals(fromPeer, _displayName, StringComparison.OrdinalIgnoreCase) Then Exit Sub

                    If IsStrictEnabled() AndAlso Not IsPeerTrusted(fromPeer) Then
                        Log($"[SECURITY] Candidate ICE ignoré (pair non de confiance): {fromPeer}")
                        Exit Sub
                    End If

                    Dim cand = Encoding.UTF8.GetString(Convert.FromBase64String(p(2)))
                    P2PManager.HandleCandidate(fromPeer, cand)
                End If
            End If
        Catch ex As Exception
            Log("[ICE] parse error: " & ex.Message, verbose:=True)
        End Try
    End Sub

    ' ---------- P2P texte ----------
    Private Sub OnP2PText_FromP2P(peer As String, text As String)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() OnP2PText_FromP2P(peer, text)) : Return
        End If
        ' Strict: IN P2P
        If IsStrictEnabled() AndAlso Not IsPeerTrusted(peer) Then
            Log($"[SECURITY] Message P2P IN bloqué (pair non de confiance): {peer}")
            Return
        End If

        Dim norm As String = Canon(text)
        If SeenRecently(peer, norm) Then Return

        StoreMsg(peer, outgoing:=False, body:=norm, viaP2P:=True)
        EnsurePrivateChat(peer)
        AppendToPrivate(peer, peer, "[P2P] " & norm)
    End Sub

    ' ---------- Fenêtres privées ----------
    Private Sub OpenPrivateChat(peer As String)
        If String.IsNullOrWhiteSpace(peer) Then Return

        Dim frm As PrivateChatForm = Nothing
        If _privateChats.TryGetValue(peer, frm) Then
            If frm IsNot Nothing AndAlso Not frm.IsDisposed Then
                If Not frm.Visible Then frm.Show(Me)
                frm.Activate()
                frm.BringToFront()
                Dim take = GetAndEnsureHistCount(peer)
                LoadHistoryIntoPrivate(peer, frm, take)
                Exit Sub
            Else
                _privateChats.Remove(peer)
            End If
        End If

        Dim sendCb As Action(Of String) = Sub(text As String) SendPrivateMessage(peer, text)
        frm = New PrivateChatForm(_displayName, peer, sendCb)

        ' événements PrivateChatForm
        AddHandler frm.ScrollTopReached,
            Sub()
                Dim stepSize As Integer = 50
                Dim take = GetAndEnsureHistCount(peer) + stepSize
                _histCount(peer) = take
                LoadHistoryIntoPrivate(peer, frm, take)
            End Sub

        AddHandler frm.PurgeRequested, Sub() PurgeHistory(peer, frm)

        AddHandler frm.StartP2PRequested,
            Sub()
                StartP2P(peer)
            End Sub

        _privateChats(peer) = frm

        ' Pousse l’état
        Dim authOk As Boolean = _idVerified.ContainsKey(peer) AndAlso _idVerified(peer)
        Dim cryptoActive As Boolean = _cryptoActive.ContainsKey(peer) AndAlso _cryptoActive(peer)
        Dim connected As Boolean = _p2pConn.ContainsKey(peer) AndAlso _p2pConn(peer)
        frm.SetAuthState(authOk)
        frm.SetCryptoState(cryptoActive)
        frm.SetP2PState(connected)

        ' Historique initial
        Dim initialTake = GetAndEnsureHistCount(peer)
        LoadHistoryIntoPrivate(peer, frm, initialTake)

        frm.Show(Me)
        frm.Activate()
        frm.BringToFront()
    End Sub

    Private Function GetAndEnsureHistCount(peer As String) As Integer
        Dim take As Integer = 50
        If _histCount.ContainsKey(peer) Then
            take = Math.Max(10, _histCount(peer))
        Else
            _histCount(peer) = take
        End If
        Return take
    End Function

    Private Sub EnsurePrivateChat(peer As String)
        If String.IsNullOrWhiteSpace(peer) Then Return
        Dim frm As PrivateChatForm = Nothing
        If Not _privateChats.TryGetValue(peer, frm) OrElse frm Is Nothing OrElse frm.IsDisposed Then
            OpenPrivateChat(peer)
        End If
    End Sub

    Private Sub AppendToPrivate(peer As String, senderName As String, message As String)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() AppendToPrivate(peer, senderName, message)) : Return
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

        ' Strict: OUT
        If IsStrictEnabled() AndAlso Not IsPeerTrusted(dest) Then
            Log($"[SECURITY] Envoi bloqué (pair non de confiance): {dest}")
            Return
        End If

        Try
            If ChatP2P.Core.P2PManager.TrySendP2P(dest, canonText) Then
                StoreMsg(dest, outgoing:=True, body:=canonText, viaP2P:=True)
                Log($"[P2P] moi → {dest}: {canonText}")
                AppendToPrivate(dest, _displayName, "[P2P] " & canonText)
                Exit Sub
            End If
        Catch
        End Try

        Dim payload = $"{Proto.TAG_PRIV}{_displayName}:{dest}:{canonText}{MSG_TERM}"
        Dim data = Encoding.UTF8.GetBytes(payload)

        If _isHost Then
            If _hub Is Nothing Then Log("[Privé] Hub non initialisé.") : Return
            Await _hub.SendToAsync(dest, data)
        Else
            If _stream Is Nothing Then Log("[Privé] Non connecté au host.") : Return
            Await _stream.SendAsync(data, CancellationToken.None)
        End If

        StoreMsg(dest, outgoing:=True, body:=canonText, viaP2P:=False)
        AppendToPrivate(dest, _displayName, canonText)
    End Sub

    ' ---------- Démarrage P2P ----------
    Private Sub StartP2P(peer As String)
        ' Strict: blocage démarrage si non-trusted
        If IsStrictEnabled() AndAlso Not IsPeerTrusted(peer) Then
            Log($"[SECURITY] Démarrage P2P bloqué (pair non de confiance): {peer}")
            Return
        End If

        Try
            Log($"[P2P] Négociation démarrée vers {peer}")
            If Not TryStartNegotiation(peer, New String() {"stun:stun.l.google.com:19302"}) Then
                Log("[P2P] Impossible de démarrer: méthode Core introuvable (StartNegotiation/BeginNegotiation/Negotiate/Start/StartP2P/Initiate).")
            End If
        Catch ex As Exception
            Log($"[P2P] StartNegotiation error: {ex.Message}")
        End Try
    End Sub

    ' Essaie plusieurs noms/signatures possibles dans le Core
    Private Function TryStartNegotiation(peer As String, stuns As String()) As Boolean
        Try
            Dim tp = GetType(ChatP2P.Core.P2PManager)
            Dim candidates = New String() {"StartNegotiation", "BeginNegotiation", "Negotiate", "Start", "StartP2P", "Initiate"}
            For Each mName As String In candidates
                Dim mi = tp.GetMethod(mName, BindingFlags.Public Or BindingFlags.Static)
                If mi Is Nothing Then Continue For
                Dim ps = mi.GetParameters()
                Try
                    Select Case ps.Length
                        Case 2
                            If ps(0).ParameterType Is GetType(String) AndAlso
                               GetType(IEnumerable(Of String)).IsAssignableFrom(ps(1).ParameterType) Then
                                mi.Invoke(Nothing, New Object() {peer, stuns})
                                Return True
                            End If
                        Case 1
                            If ps(0).ParameterType Is GetType(String) Then
                                mi.Invoke(Nothing, New Object() {peer})
                                Return True
                            End If
                        Case 0
                            mi.Invoke(Nothing, Nothing)
                            Return True
                    End Select
                Catch
                    ' on tente le suivant
                End Try
            Next
        Catch
        End Try
        Return False
    End Function

    ' ---------- P2P init ----------
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

    ' ---------- Inputs ----------
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

    ' ---------- Security Center ----------
    Private Sub btnSecurity_Click(sender As Object, e As EventArgs) Handles btnSecurity.Click
        Try
            Dim f As New SecurityCenterForm("") With { ' plus de chemin identité fichier
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
