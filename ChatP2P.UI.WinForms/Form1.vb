Option Strict On
Option Explicit On

Imports System
Imports System.Globalization
Imports System.IO
Imports System.Net.Sockets
Imports System.Reflection
Imports System.Text
Imports System.Threading
Imports System.Linq
Imports System.Security.Cryptography
Imports ChatP2P.App
Imports ChatP2P.Core
Imports ChatP2P.App.Protocol
Imports Proto = ChatP2P.App.Protocol.Tags

' ChatP2P.UI.WinForms/Form1.vb
Public Class Form1

    Private Const MSG_TERM As String = vbLf

    ' ==== TAGs/Marqueurs chiffrement RELAY ====
    Private Const ENC_HELLO As String = "[ENCHELLO]"
    Private Const ENC_ACK As String = "[ENCACK]"
    Private Const ENC_PREFIX As String = "ENC1:"
    Private Const ENC_FILE_MARK As String = "[ENC]"

    ' ==== TAGs/Marqueurs identité TOFU ====
    Private Const ID_HELLO As String = "[IDHELLO]"    ' [IDHELLO]<b64(pub)>:<b64(nonce)>
    Private Const ID_PROOF As String = "[IDPROOF]"    ' [IDPROOF]<b64(nonce)>:<b64(sig)>

    ' ====== État identité (Ed25519 / TOFU) ======
    Private _myEdPk As Byte()
    Private _myEdSk As Byte()
    ' Nonces en attente (challenge) par pair
    Private ReadOnly _idChallenge As New Dictionary(Of String, Byte())(StringComparer.OrdinalIgnoreCase)
    ' Pubkeys TOFU vues/mémorisées (cache)
    Private ReadOnly _peerEdPk As New Dictionary(Of String, Byte())(StringComparer.OrdinalIgnoreCase)

    Private Class FileRecvState
        Public Id As String = ""
        Public FromName As String = ""
        Public File As FileStream
        Public Path As String = ""
        Public Expected As Long
        Public Received As Long
        Public LastTickUtc As DateTime
        Public EncRelay As Boolean = False
    End Class

    ' Remplace les tuples (DateTime, String) par une petite classe
    Private Class RecentItem
        Public TimeUtc As DateTime
        Public Token As String
        Public Sub New(t As DateTime, k As String)
            TimeUtc = t : Token = k
        End Sub
    End Class

    Private _cts As CancellationTokenSource
    Private _isHost As Boolean = False

    Private _stream As INetworkStream
    Private _displayName As String = "Me"
    Private _recvFolder As String = ""
    Private _hub As RelayHub

    Private ReadOnly _idVerified As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _p2pConn As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _cryptoActive As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)

    Private ReadOnly _privateChats As New Dictionary(Of String, PrivateChatForm)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _histCount As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)

    Private ReadOnly _fileRecv As New Dictionary(Of String, FileRecvState)(StringComparer.Ordinal)
    Private _fileWatchdog As System.Windows.Forms.Timer

    Private ReadOnly _cliBuf As New StringBuilder()
    Private ReadOnly _recentP2P As New Dictionary(Of String, List(Of RecentItem))(StringComparer.OrdinalIgnoreCase)

    Private _lastPeers As New List(Of String)()

    ' pour éviter d’accrocher 2× les handlers P2P
    Private _p2pHandlersHooked As Boolean = False

    ' ==== État chiffrement RELAY ====
    Private ReadOnly _relayKeys As New Dictionary(Of String, Byte())(StringComparer.OrdinalIgnoreCase)       ' clé sym AES-GCM
    Private ReadOnly _relayEcdhPending As New Dictionary(Of String, ECDiffieHellmanCng)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _pendingEncMsgs As New Dictionary(Of String, Queue(Of String))(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _rng As RandomNumberGenerator = RandomNumberGenerator.Create()

    ' ---------- Utils ----------
    Private Function Canon(ByVal s As String) As String
        If s Is Nothing Then Return String.Empty
        Dim r As String = s.Replace(vbCr, "")
        Do While r.EndsWith(vbLf, StringComparison.Ordinal)
            r = r.Substring(0, r.Length - 1)
        Loop
        Return r.TrimEnd()
    End Function

    ' === Helpers PrivateChatForm (uniques) ===
    Private Function TryGetPrivateChatForm(peer As String, ByRef frm As PrivateChatForm) As Boolean
        frm = Nothing
        If String.IsNullOrWhiteSpace(peer) Then Return False
        Dim f As PrivateChatForm = Nothing
        If _privateChats.TryGetValue(peer, f) AndAlso f IsNot Nothing AndAlso Not f.IsDisposed Then
            frm = f
            Return True
        End If
        Return False
    End Function

    Private Function EnsureAndGetPrivateChatForm(peer As String) As PrivateChatForm
        ' Ouvre la fenêtre si besoin
        EnsurePrivateChat(peer)
        Dim f As PrivateChatForm = Nothing
        If TryGetPrivateChatForm(peer, f) Then Return f
        Return Nothing
    End Function

    ' Anti-duplication P2P
    Private Function SeenRecently(peer As String, text As String) As Boolean
        Dim nowUtc = DateTime.UtcNow
        Dim list As List(Of RecentItem) = Nothing
        If Not _recentP2P.TryGetValue(peer, list) OrElse list Is Nothing Then
            list = New List(Of RecentItem)()
            _recentP2P(peer) = list
        End If

        list.RemoveAll(Function(it) (nowUtc - it.TimeUtc).TotalSeconds > 2.0)

        Dim token = If(text, String.Empty)
        If token.Length > 256 Then token = token.Substring(0, 256) & "#" & text.Length.ToString()

        If list.Any(Function(it) it.Token = token) Then Return True
        list.Add(New RecentItem(nowUtc, token))
        If list.Count > 20 Then list.RemoveAt(0)
        Return False
    End Function

    ' === Snapshot pour SecurityCenter ===
    Private Function GetSessionSnapshot(peer As String) As SessionSnapshot
        Dim snap As New SessionSnapshot()
        snap.Peer = peer
        snap.IsStrict = IsStrictEnabled()
        snap.P2PConnected = (_p2pConn.ContainsKey(peer) AndAlso _p2pConn(peer))
        snap.RelayKeyEstablished = HasRelayKey(peer)
        snap.IdentityVerified = (_idVerified.ContainsKey(peer) AndAlso _idVerified(peer))
        snap.PeerTrusted = IsPeerTrusted(peer)
        snap.PqRelayEnabled = IsPqRelayEnabled()
        snap.RelayKdfLabel = If(snap.PqRelayEnabled, "relay-hybrid:", If(snap.RelayKeyEstablished, "relay-classic:", ""))
        ' fingerprints
        Try
            Dim mypk As Byte() = Nothing, mysk As Byte() = Nothing
            ChatP2P.Core.LocalDbExtensions.IdentityEnsureEd25519(mypk, mysk)
            If mypk IsNot Nothing Then snap.MyFingerprint = SecurityCenterForm_FormatFpLocal(mypk)
        Catch
        End Try
        Try
            Dim ppk = ChatP2P.Core.LocalDbExtensions.PeerGetEd25519(peer)
            If ppk IsNot Nothing AndAlso ppk.Length > 0 Then snap.PeerFingerprint = SecurityCenterForm_FormatFpLocal(ppk)
        Catch
        End Try
        Try
            snap.LastSeenUtc = ChatP2P.Core.LocalDbExtensionsSecurity.PeerGetField(peer, "LastSeenUtc")
        Catch
        End Try
        Return snap
    End Function

    ' formatage fingerprint style SecurityCenter
    Private Function SecurityCenterForm_FormatFpLocal(pub As Byte()) As String
        Using sha As SHA256 = SHA256.Create()
            Dim fp = sha.ComputeHash(pub)
            Dim hex = BitConverter.ToString(fp).Replace("-", "")
            Dim sb As New StringBuilder()
            For i = 0 To hex.Length - 1 Step 4
                If sb.Length > 0 Then sb.Append("-")
                Dim take = Math.Min(4, hex.Length - i)
                sb.Append(hex.Substring(i, take))
            Next
            Return sb.ToString()
        End Using
    End Function

    Private Sub Log(msg As String, Optional verbose As Boolean = False)
        Try
            If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
                Me.BeginInvoke(New Action(Of String, Boolean)(AddressOf Log), msg, verbose)
                Return
            End If
        Catch
        End Try

        If txtLog Is Nothing Then Exit Sub

        If Not verbose Then
            txtLog.AppendText(msg & Environment.NewLine)
        Else
            If chkVerbose IsNot Nothing AndAlso chkVerbose.Checked Then
                txtLog.AppendText(msg & Environment.NewLine)
            End If
        End If
    End Sub

    Private Function GetRecvFolder() As String
        If String.IsNullOrWhiteSpace(_recvFolder) OrElse Not Directory.Exists(_recvFolder) Then
            Dim def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ChatP2P_Recv")
            If Not Directory.Exists(def) Then Directory.CreateDirectory(def)
            _recvFolder = def
        End If
        Return _recvFolder
    End Function

    Private Function IsStrictEnabled() As Boolean
        Try
            If chkStrictTrust Is Nothing Then Return False
            Return chkStrictTrust.Checked
        Catch
            Return False
        End Try
    End Function

    ' Détection via Controls.Find pour éviter une dépendance dure au Designer
    Private Function IsEncryptRelayEnabled() As Boolean
        Try
            Dim arr = Me.Controls.Find("chkEncryptRelay", True)
            If arr IsNot Nothing AndAlso arr.Length > 0 Then
                Dim cb = TryCast(arr(0), CheckBox)
                If cb IsNot Nothing Then Return cb.Checked
            End If
        Catch
        End Try
        Return False
    End Function

    ' Checkbox optionnelle "Utiliser PQ pour le relay ?"
    Private Function IsPqRelayEnabled() As Boolean
        Try
            Dim arr = Me.Controls.Find("chkPqRelay", True)
            If arr IsNot Nothing AndAlso arr.Length > 0 Then
                Dim cb = TryCast(arr(0), CheckBox)
                If cb IsNot Nothing Then Return cb.Checked
            End If
        Catch
        End Try
        Return False
    End Function

    Private Function IsPeerTrusted(peer As String) As Boolean
        Try
            Return ChatP2P.Core.LocalDb.GetPeerTrusted(peer)
        Catch
            Return False
        End Try
    End Function

    Private Sub SaveSettings()
        Try
            If txtName IsNot Nothing Then My.Settings.DisplayName = txtName.Text.Trim()
            If txtLocalPort IsNot Nothing Then My.Settings.LocalPort = txtLocalPort.Text.Trim()
            If txtRemoteIp IsNot Nothing Then My.Settings.RemoteIp = txtRemoteIp.Text.Trim()
            If txtRemotePort IsNot Nothing Then My.Settings.RemotePort = txtRemotePort.Text.Trim()
            If Not String.IsNullOrWhiteSpace(_recvFolder) Then My.Settings.RecvFolder = _recvFolder
            My.Settings.Save()
        Catch
        End Try
        PersistStrictToSettingsIfPossible()
    End Sub

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

    Private Sub SendIdHello(peer As String)
        Try
            If _myEdPk Is Nothing OrElse _myEdPk.Length = 0 Then Exit Sub
            ' challenge: 32 octets aléatoires
            Dim nonce(31) As Byte
            _rng.GetBytes(nonce)

            SyncLock _idChallenge
                _idChallenge(peer) = nonce
            End SyncLock

            Dim body = ID_HELLO & Convert.ToBase64String(_myEdPk) & ":" & Convert.ToBase64String(nonce)
            Dim payload = $"{Proto.TAG_PRIV}{_displayName}:{peer}:{body}{MSG_TERM}"
            Dim data = Encoding.UTF8.GetBytes(payload)

            If _isHost Then
                If _hub IsNot Nothing Then _hub.SendToAsync(peer, data)
            Else
                If _stream IsNot Nothing Then _stream.SendAsync(data, Threading.CancellationToken.None)
            End If
            Log("[ID] HELLO => " & peer, verbose:=True)
        Catch ex As Exception
            Log("[ID] SendIdHello error: " & ex.Message)
        End Try
    End Sub

    ' ---------- Form ----------
    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' DB
        Try
            ChatP2P.Core.LocalDb.Init()
        Catch ex As Exception
            Log("[DB] init failed: " & ex.Message)
        End Try

        ' Identité locale Ed25519
        Try
            ChatP2P.Core.LocalDbExtensions.IdentityEnsureEd25519(_myEdPk, _myEdSk)
            Log("[ID] Ed25519 locale OK (" & If(_myEdPk IsNot Nothing, _myEdPk.Length.ToString(), "0") & " octets)", verbose:=True)
        Catch ex As Exception
            Log("[ID] init Ed25519 FAILED: " & ex.Message)
        End Try

        ' (Optionnel) init crypto
        Try
            ChatP2P.Crypto.KexX25519.GenerateKeyPair()
        Catch
        End Try

        ' Prefs
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

        If chkStrictTrust IsNot Nothing Then
            Try
                Dim pi = GetType(My.MySettings).GetProperty("StrictTrust", BindingFlags.Instance Or BindingFlags.Public)
                If pi IsNot Nothing Then
                    Dim raw = pi.GetValue(My.Settings, Nothing)
                    Dim v As Boolean = False
                    If raw IsNot Nothing Then
                        If TypeOf raw Is Boolean Then
                            v = CBool(raw)
                        Else
                            Dim s = TryCast(raw, String)
                            If s IsNot Nothing Then Boolean.TryParse(s, v)
                        End If
                    End If
                    chkStrictTrust.Checked = v
                End If
            Catch
            End Try
            AddHandler chkStrictTrust.CheckedChanged, Sub() PersistStrictToSettingsIfPossible()
        End If

        ' init P2P au Load
        InitP2PManager()

        _fileWatchdog = New System.Windows.Forms.Timer() With {.Interval = 1500}
        AddHandler _fileWatchdog.Tick, AddressOf FileWatchdog_Tick
        _fileWatchdog.Start()

        AddHandler lstPeers.DoubleClick, AddressOf lstPeers_DoubleClick
    End Sub

    Private Sub Form1_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        SaveSettings()
    End Sub

    ' ---------- DB helpers ----------
    Private Sub EnsurePeerRow(peer As String)
        Try
            If String.IsNullOrWhiteSpace(peer) Then Return
            Dim nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            LocalDb.ExecNonQuery(
                "INSERT INTO Peers(Name, CreatedUtc, Trusted) VALUES(@n,@ts,0)
                 ON CONFLICT(Name) DO NOTHING;",
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
            Log("[DB] insert message failed: " & ex.Message, verbose:=True)
        End Try
    End Sub

    ' ---------- Historique -> fenêtre privée ----------
    Private Sub LoadHistoryIntoPrivate(peer As String, frm As PrivateChatForm, take As Integer)
        Dim dt As Data.DataTable = Nothing
        Try
            dt = LocalDb.Query(
                "SELECT Sender, Body, IsP2P, Direction, CreatedUtc
                 FROM Messages WHERE PeerName=@p
                 ORDER BY Id DESC LIMIT @n;",
                LocalDb.P("@p", peer), LocalDb.P("@n", take)
            )
        Catch ex As Exception
            Log("[DB] query messages failed: " & ex.Message, verbose:=True)
            Return
        End Try

        Dim rows = New List(Of Data.DataRow)
        For Each r As Data.DataRow In dt.Rows : rows.Add(r) : Next
        rows.Reverse()

        frm.ClearMessages()
        For Each r In rows
            Dim dir As String = TryCast(r!Direction, String)
            Dim body As String = TryCast(r!Body, String)
            Dim isp2p As Boolean = False
            If Not IsDBNull(r!IsP2P) Then isp2p = (Convert.ToInt32(r!IsP2P, CultureInfo.InvariantCulture) <> 0)
            Dim sender As String = If(String.Equals(dir, "recv", StringComparison.OrdinalIgnoreCase), peer, _displayName)
            frm.AppendMessage(sender, If(isp2p, "[P2P] ", "[RELAY] ") & body)
        Next
    End Sub

    ' ---------- UI: sélection pair ----------
    Private Sub UpdatePeers(names As List(Of String))
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(New Action(Of List(Of String))(AddressOf UpdatePeers), names)
            Return
        End If

        If lstPeers Is Nothing Then Exit Sub

        Try
            Dim added = names.Except(_lastPeers, StringComparer.OrdinalIgnoreCase).ToList()
            Dim removed = _lastPeers.Except(names, StringComparer.OrdinalIgnoreCase).ToList()
            For Each a In added : Log($"[HUB] + {a}") : Next
            For Each r In removed : Log($"[HUB] - {r}") : Next

            Dim cleaned = names.
                Where(Function(p) Not String.IsNullOrWhiteSpace(p)).
                Select(Function(p) p.Trim()).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()

            _lastPeers = cleaned

            lstPeers.BeginUpdate()
            lstPeers.Items.Clear()
            For Each peerName As String In cleaned
                lstPeers.Items.Add(peerName)
            Next
            lstPeers.EndUpdate()

            Log("[UI] lstPeers <= [" & String.Join(", ", cleaned) & "]", verbose:=True)
        Catch ex As Exception
            Log("[UI] UpdatePeers error: " & ex.Message)
        End Try
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

        _displayName = If(String.IsNullOrWhiteSpace(txtName.Text), "Server", txtName.Text.Trim())
        _isHost = True
        SaveSettings()

        InitP2PManager()

        _hub = New RelayHub(port) With {.HostDisplayName = _displayName}

        AddHandler _hub.PeerListUpdated, Sub(names As List(Of String)) UpdatePeers(names)
        AddHandler _hub.LogLine, Sub(t) Log(t)
        AddHandler _hub.MessageArrived, Sub(senderName, text) Log($"{senderName}: {text}")
        AddHandler _hub.PrivateArrived,
            Sub(senderName, dest, body)
                If String.Equals(dest, _displayName, StringComparison.OrdinalIgnoreCase) Then
                    If IsStrictEnabled() AndAlso Not IsPeerTrusted(senderName) Then
                        Log($"[SECURITY] Message RELAY IN bloqué (pair non de confiance): {senderName}")
                    Else
                        EnsurePrivateChat(senderName)
                        RefreshPrivateChatStatus(senderName)
                        AppendToPrivate(senderName, senderName, "[RELAY] " & body)
                        StoreMsg(senderName, outgoing:=False, body:=Canon(body), viaP2P:=False)
                    End If
                End If
            End Sub
        AddHandler _hub.FileSignal, Sub(raw) OnHubFileSignal(raw)
        AddHandler _hub.IceSignal, Sub(raw) OnHubIceSignal(raw)

        Log("[UI] Starting hub…")
        _hub.Start()
        Log("Hub démarré.")
        UpdatePeers(New List(Of String) From {_displayName})
    End Sub

    Private Sub btnStopHost_Click(sender As Object, e As EventArgs) Handles btnStopHost.Click
        Try
            If _hub IsNot Nothing Then _hub.[Stop]()
            _hub = Nothing
            Log("Hub arrêté.")
        Catch ex As Exception
            Log("Stop hub error: " & ex.Message)
        End Try
    End Sub

    ' ---------- Client ----------
    Private Async Sub btnConnect_Click(sender As Object, e As EventArgs) Handles btnConnect.Click
        Dim ip As String = txtRemoteIp.Text.Trim()
        Dim port As Integer
        If Not Integer.TryParse(txtRemotePort.Text, port) Then Log("Port invalide.") : Return

        _displayName = If(String.IsNullOrWhiteSpace(txtName.Text), "Client", txtName.Text.Trim())
        _isHost = False
        SaveSettings()

        InitP2PManager()

        Try
            _cts = New CancellationTokenSource()
            Dim cli As New TcpClient()
            Await cli.ConnectAsync(ip, port)
            _stream = New TcpNetworkStreamAdapter(cli)

            Log($"Connecté au hub {ip}:{port}")
            Await _stream.SendAsync(Encoding.UTF8.GetBytes(Proto.TAG_NAME & _displayName & MSG_TERM), CancellationToken.None)
            Log("[CLI] TAG_NAME envoyé.")
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
        txtMessage.Clear()
    End Sub

    ' ---------- Fichiers TX ----------
    Private Sub btnSendFile_Click(sender As Object, e As EventArgs) Handles btnSendFile.Click
        Dim dest As String = ""
        If lstPeers IsNot Nothing AndAlso lstPeers.SelectedItem IsNot Nothing Then
            dest = lstPeers.SelectedItem.ToString().Trim()
        End If
        If String.IsNullOrWhiteSpace(dest) Then
            Log("Sélectionne d'abord un pair dans la liste (lstPeers).")
            Return
        End If
        ' délègue au flux centralisé (ouvre la PrivateChatForm, gère la progress et le log)
        SendFileToPeer(dest)
    End Sub

    ' ---------- Fichiers RX ----------
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

        Dim iAmDest As Boolean = If(_isHost, String.Equals(dest, _displayName, StringComparison.OrdinalIgnoreCase), True)
        If Not iAmDest Then Return

        If IsStrictEnabled() AndAlso Not IsPeerTrusted(fromName) Then
            Log($"[SECURITY] Fichier RELAY IN bloqué (pair non de confiance): {fromName}")
            Return
        End If

        Dim enc As Boolean = False
        If fname.StartsWith(ENC_FILE_MARK, StringComparison.Ordinal) Then
            enc = True
            fname = fname.Substring(ENC_FILE_MARK.Length)
        End If

        ' 👉 Nom original, sans préfixe "fromName__"
        Dim savePath = Path.Combine(GetRecvFolder(), fname)
        savePath = MakeUniquePath(savePath)

        ' Ouvre / montre la fenêtre privée et démarre la progression RX
        Try
            EnsurePrivateChat(fromName)
            Dim frmPeer As PrivateChatForm = EnsureAndGetPrivateChatForm(fromName)
            If frmPeer IsNot Nothing Then
                frmPeer.StartRecvProgress(fname, fsize)
            End If
        Catch
            ' ignore
        End Try

        Dim st As New FileRecvState() With {
        .Id = tid, .FromName = fromName, .Path = savePath,
        .Expected = fsize, .Received = 0, .LastTickUtc = DateTime.UtcNow,
        .EncRelay = enc
    }
        Try
            st.File = New FileStream(st.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, useAsync:=True)
        Catch ex As Exception
            Log("[FICHIER] Erreur ouverture fichier: " & ex.Message)
            Return
        End Try

        SyncLock _fileRecv
            _fileRecv(tid) = st
        End SyncLock

        Log($"Réception fichier {(If(enc, "(ENC) ", ""))}{fname} de {fromName} (taille {fsize} octets)")
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

        Dim plainBytes As Byte()

        If st.EncRelay Then
            If Not chunkB64.StartsWith(ENC_PREFIX, StringComparison.Ordinal) Then
                Try
                    plainBytes = Convert.FromBase64String(chunkB64)
                Catch
                    Return
                End Try
            Else
                Dim payload = chunkB64.Substring(ENC_PREFIX.Length)
                Dim encBytes As Byte()
                Try
                    encBytes = Convert.FromBase64String(payload)
                Catch
                    Return
                End Try

                Try
                    plainBytes = DecryptFromPeer(st.FromName, encBytes)
                Catch ex As Exception
                    Log("[FICHIER] Decrypt error: " & ex.Message)
                    Return
                End Try
            End If
        Else
            Try
                plainBytes = Convert.FromBase64String(chunkB64)
            Catch
                Return
            End Try
        End If

        Try
            st.File.Write(plainBytes, 0, plainBytes.Length)
            st.Received += plainBytes.Length
            st.LastTickUtc = DateTime.UtcNow

            ' MAJ progression réception (thread-safe côté form)
            Dim frmPeer As PrivateChatForm = Nothing
            If TryGetPrivateChatForm(st.FromName, frmPeer) Then
                frmPeer.UpdateRecvProgress(st.Received, st.Expected)
            End If
        Catch ex As Exception
            Log("[FICHIER] Erreur écriture: " & ex.Message)
            Return
        End Try
    End Sub


    Private Sub HandleFileEnd(msg As String)
        Dim parts = msg.Split(":"c, 2)
        If parts.Length < 2 Then Return
        Dim tid = parts(1).Trim()

        Dim st As FileRecvState = Nothing
        SyncLock _fileRecv
            If _fileRecv.ContainsKey(tid) Then st = _fileRecv(tid)
        End SyncLock
        If st Is Nothing Then Return

        Try
            st.File.Flush()
            st.File.Close()
        Catch
        End Try

        ' Progress OFF + petite notif dans la fenêtre privée
        Try
            Dim frmPeer As PrivateChatForm = Nothing
            If TryGetPrivateChatForm(st.FromName, frmPeer) Then
                frmPeer.EndRecvProgress()
                frmPeer.AppendMessage(st.FromName, $"[FILE] ✅ Reçu : {IO.Path.GetFileName(st.Path)} ({st.Expected} octets)")
            End If
        Catch
        End Try

        Log("Fichier reçu: " & st.Path)

        SyncLock _fileRecv
            _fileRecv.Remove(tid)
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

                    Dim msg = Canon(line)
                    If String.IsNullOrEmpty(msg) Then Continue While

                    If msg.StartsWith(Proto.TAG_MSG, StringComparison.Ordinal) Then
                        Dim rest = msg.Substring(Proto.TAG_MSG.Length)
                        Dim parts = rest.Split(":"c, 2)
                        If parts.Length = 2 Then Log($"{parts(0)}: {parts(1)}")

                    ElseIf msg.StartsWith(Proto.TAG_PRIV, StringComparison.Ordinal) Then
                        Dim rest = msg.Substring(Proto.TAG_PRIV.Length)
                        Dim parts = rest.Split(":"c, 3)
                        If parts.Length = 3 Then
                            Dim fromPeer = parts(0)
                            Dim toPeer = parts(1)
                            Dim body = parts(2)
                            If String.Equals(toPeer, _displayName, StringComparison.OrdinalIgnoreCase) Then

                                ' --- Auth Ed25519 / TOFU ---
                                If body.StartsWith(ID_HELLO, StringComparison.Ordinal) Then
                                    ProcessIdHello(fromPeer, body.Substring(ID_HELLO.Length))
                                    Continue While
                                ElseIf body.StartsWith(ID_PROOF, StringComparison.Ordinal) Then
                                    ProcessIdProof(fromPeer, body.Substring(ID_PROOF.Length))
                                    Continue While
                                End If

                                ' Handshake ENC ?
                                If body.StartsWith(ENC_HELLO, StringComparison.Ordinal) Then
                                    ProcessEncHello(fromPeer, body.Substring(ENC_HELLO.Length))
                                    Continue While
                                ElseIf body.StartsWith(ENC_ACK, StringComparison.Ordinal) Then
                                    ProcessEncAck(fromPeer, body.Substring(ENC_ACK.Length))
                                    Continue While
                                ElseIf body.StartsWith(ENC_PREFIX, StringComparison.Ordinal) Then
                                    Try
                                        Dim encPayloadB64 = body.Substring(ENC_PREFIX.Length)
                                        Dim encBytes = Convert.FromBase64String(encPayloadB64)
                                        Dim plain = Encoding.UTF8.GetString(DecryptFromPeer(fromPeer, encBytes))
                                        EnsurePrivateChat(fromPeer)
                                        RefreshPrivateChatStatus(fromPeer)
                                        AppendToPrivate(fromPeer, fromPeer, "[RELAY] " & plain)
                                        StoreMsg(fromPeer, outgoing:=False, body:=Canon(plain), viaP2P:=False)
                                    Catch ex As Exception
                                        Log("[ENC] decrypt PRIV error: " & ex.Message)
                                    End Try
                                    Continue While
                                End If

                                ' Message RELAY en clair
                                If IsStrictEnabled() AndAlso Not IsPeerTrusted(fromPeer) Then
                                    Log($"[SECURITY] Message RELAY IN bloqué (pair non de confiance): {fromPeer}")
                                Else
                                    EnsurePrivateChat(fromPeer)
                                    RefreshPrivateChatStatus(fromPeer)
                                    AppendToPrivate(fromPeer, fromPeer, "[RELAY] " & body)
                                    StoreMsg(fromPeer, outgoing:=False, body:=Canon(body), viaP2P:=False)
                                End If
                            End If
                        End If

                    ElseIf msg.StartsWith(Proto.TAG_PEERS, StringComparison.Ordinal) Then
                        Dim listPart = msg.Substring(Proto.TAG_PEERS.Length)
                        Dim names = listPart.Split(";"c).
                            Select(Function(x) x.Trim()).
                            Where(Function(x) x <> "").
                            Distinct(StringComparer.OrdinalIgnoreCase).
                            ToList()
                        Log("[CLI] TAG_PEERS reçu: [" & String.Join(", ", names) & "]")
                        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
                            Me.BeginInvoke(Sub() UpdatePeers(names))
                        Else
                            UpdatePeers(names)
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
                    Else
                        Log("[CLI] inconnu: " & msg, verbose:=True)
                    End If
                End While
            End While
        Catch ex As Exception
            If Not ct.IsCancellationRequested Then
                Log("Listen error: " & ex.Message)
            End If
        End Try
    End Sub

    Private Sub ProcessIdHello(fromPeer As String, payload As String)
        Try
            ' payload: <b64(pub)>:<b64(nonce)>
            Dim p = payload.Split(":"c, 2)
            If p.Length <> 2 Then Exit Sub
            Dim peerPk = Convert.FromBase64String(p(0).Trim())
            Dim theirNonce = Convert.FromBase64String(p(1).Trim())

            ' TOFU: si on n'a pas encore le pubkey du pair en DB, on l’enregistre
            Dim known As Byte() = Nothing
            If Not _peerEdPk.TryGetValue(fromPeer, known) OrElse known Is Nothing Then
                known = ChatP2P.Core.LocalDbExtensions.PeerGetEd25519(fromPeer)
                If known IsNot Nothing Then
                    SyncLock _peerEdPk
                        _peerEdPk(fromPeer) = known
                    End SyncLock
                End If
            End If

            If known Is Nothing Then
                ' Première vue => TOFU
                ChatP2P.Core.LocalDbExtensions.PeerSetEd25519_Tofu(fromPeer, peerPk)
                SyncLock _peerEdPk
                    _peerEdPk(fromPeer) = peerPk
                End SyncLock
                Log("[ID] TOFU: pubkey enregistré pour " & fromPeer, verbose:=True)
            Else
                ' Si déjà connu et différent => alerte (+ strict mode : on refuse)
                If Not known.SequenceEqual(peerPk) Then
                    Log("[SECURITY] Pubkey Ed25519 MISMATCH pour " & fromPeer & " (possible MITM) !")
                    If IsStrictEnabled() Then
                        Exit Sub ' on coupe court en mode strict
                    End If
                    ' en mode non strict on garde l’ancien pour la vérification
                    peerPk = known
                End If
            End If

            ' Répondre par une preuve: signer leur nonce
            If _myEdSk Is Nothing OrElse _myEdSk.Length = 0 Then Exit Sub

            ' On signe le "contexte|nonce" pour lier l'identité aux noms
            Dim context = "chatp2p-id-proof:" & SortedPairTag(_displayName, fromPeer)
            Dim toSign = Concat(Encoding.UTF8.GetBytes(context), theirNonce)
            Dim sig = ChatP2P.Crypto.Ed25519Util.Sign(toSign, _myEdSk)

            Dim proofBody = ID_PROOF & Convert.ToBase64String(theirNonce) & ":" & Convert.ToBase64String(sig)
            Dim payloadOut = $"{Proto.TAG_PRIV}{_displayName}:{fromPeer}:{proofBody}{MSG_TERM}"
            Dim data = Encoding.UTF8.GetBytes(payloadOut)

            If _isHost Then
                If _hub IsNot Nothing Then _hub.SendToAsync(fromPeer, data)
            Else
                If _stream IsNot Nothing Then _stream.SendAsync(data, Threading.CancellationToken.None)
            End If
            Log("[ID] PROOF => " & fromPeer, verbose:=True)

        Catch ex As Exception
            Log("[ID] ProcessIdHello error: " & ex.Message)
        End Try
    End Sub

    Private Sub ProcessIdProof(fromPeer As String, payload As String)
        Try
            ' payload: <b64(nonce)>:<b64(sig)>
            Dim p = payload.Split(":"c, 2)
            If p.Length <> 2 Then Exit Sub
            Dim nonce = Convert.FromBase64String(p(0).Trim())
            Dim sig = Convert.FromBase64String(p(1).Trim())

            ' Retrouver le challenge qu'on avait envoyé à ce peer
            Dim expected As Byte() = Nothing
            SyncLock _idChallenge
                _idChallenge.TryGetValue(fromPeer, expected)
            End SyncLock
            If expected Is Nothing Then
                Log("[ID] PROOF reçu sans challenge en attente de " & fromPeer, verbose:=True)
                Exit Sub
            End If

            ' Vérifier que le nonce correspond
            If Not expected.SequenceEqual(nonce) Then
                Log("[ID] PROOF nonce mismatch de " & fromPeer)
                Exit Sub
            End If

            ' Récupérer (ou recharger) le pubkey stocké pour ce peer
            Dim peerPk As Byte() = Nothing
            If Not _peerEdPk.TryGetValue(fromPeer, peerPk) OrElse peerPk Is Nothing Then
                peerPk = ChatP2P.Core.LocalDbExtensions.PeerGetEd25519(fromPeer)
                If peerPk IsNot Nothing Then
                    SyncLock _peerEdPk
                        _peerEdPk(fromPeer) = peerPk
                    End SyncLock
                End If
            End If
            If peerPk Is Nothing Then
                Log("[ID] PROOF mais pubkey inconnu pour " & fromPeer)
                Exit Sub
            End If

            ' Vérif signature
            Dim context = "chatp2p-id-proof:" & SortedPairTag(_displayName, fromPeer)
            Dim toVerify = Concat(Encoding.UTF8.GetBytes(context), nonce)
            Dim ok = ChatP2P.Crypto.Ed25519Util.Verify(toVerify, sig, peerPk)

            If ok Then
                _idVerified(fromPeer) = True
                ChatP2P.Core.LocalDbExtensions.PeerMarkVerified(fromPeer)
                ' nettoyage du challenge (anti-replay / fuite)
                SyncLock _idChallenge
                    _idChallenge.Remove(fromPeer)
                End SyncLock
                RefreshPrivateChatStatus(fromPeer)
                Log("[ID] Auth OK pour " & fromPeer)
            Else
                Log("[ID] Auth FAILED pour " & fromPeer)
            End If
        Catch ex As Exception
            Log("[ID] ProcessIdProof error: " & ex.Message)
        End Try
    End Sub

    Private Sub OnHubFileSignal(raw As String)
        Try
            If raw.StartsWith(Proto.TAG_FILEMETA, StringComparison.Ordinal) Then
                HandleFileMeta(raw)
            ElseIf raw.StartsWith(Proto.TAG_FILECHUNK, StringComparison.Ordinal) Then
                HandleFileChunk(raw)
            ElseIf raw.StartsWith(Proto.TAG_FILEEND, StringComparison.Ordinal) Then
                HandleFileEnd(raw)
            End If
        Catch ex As Exception
            Log("[FILE] parse error: " & ex.Message)
        End Try
    End Sub

    Private Sub OnHubIceSignal(raw As String)
        Try
            If raw.StartsWith(Proto.TAG_ICE_OFFER, StringComparison.Ordinal) Then
                Dim p = raw.Substring(Proto.TAG_ICE_OFFER.Length).Split(":"c, 3)
                If p.Length = 3 Then
                    Dim fromPeer = p(0)
                    If String.Equals(fromPeer, "Server", StringComparison.OrdinalIgnoreCase) _
                       OrElse String.Equals(fromPeer, _displayName, StringComparison.OrdinalIgnoreCase) Then Exit Sub
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
                        Log($"[SECURITY] Candidate ignorée (pair non de confiance): {fromPeer}")
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
            Me.BeginInvoke(Sub() OnP2PText_FromP2P(peer, text))
            Return
        End If
        If IsStrictEnabled() AndAlso Not IsPeerTrusted(peer) Then
            Log($"[SECURITY] Message P2P IN bloqué (pair non de confiance): {peer}")
            Return
        End If
        If text.StartsWith(Proto.TAG_FILEMETA, StringComparison.Ordinal) _
           OrElse text.StartsWith(Proto.TAG_FILECHUNK, StringComparison.Ordinal) _
           OrElse text.StartsWith(Proto.TAG_FILEEND, StringComparison.Ordinal) Then
            OnHubFileSignal(text)
            Return
        End If

        Dim norm As String = Canon(text)
        If SeenRecently(peer, norm) Then Return

        StoreMsg(peer, outgoing:=False, body:=norm, viaP2P:=True)
        EnsurePrivateChat(peer)
        RefreshPrivateChatStatus(peer)
        AppendToPrivate(peer, peer, "[P2P] " & norm)
    End Sub
    ' ---------- Fenêtres privées ----------
    Private Sub OpenPrivateChat(peer As String)
        If String.IsNullOrWhiteSpace(peer) Then Return

        Dim frm As PrivateChatForm = Nothing
        If Not _privateChats.TryGetValue(peer, frm) OrElse frm Is Nothing OrElse frm.IsDisposed Then

            ' --- Envoi texte ---
            Dim sendCb As Action(Of String) =
            Sub(text As String)
                Try
                    If String.IsNullOrWhiteSpace(text) Then Return
                    If IsStrictEnabled() AndAlso Not IsPeerTrusted(peer) Then
                        Log($"[SECURITY] Envoi privé bloqué (pair non de confiance): {peer}")
                        Return
                    End If

                    Dim norm = Canon(text)
                    Dim viaP2P As Boolean =
                        _p2pConn.ContainsKey(peer) AndAlso _p2pConn(peer) AndAlso P2PManager.TrySendText(peer, norm)

                    If Not viaP2P Then
                        Dim useEnc As Boolean = IsEncryptRelayEnabled()
                        Dim bodyToSend As String

                        If useEnc Then
                            If Not HasRelayKey(peer) Then
                                EnsureRelayHandshake(peer)
                                QueuePendingEnc(peer, norm)
                                Log("[ENC] Clé en négociation avec " & peer & ". Message mis en attente.")
                                GoTo EchoAndPersist
                            End If
                            Dim encBytes = EncryptForPeer(peer, Encoding.UTF8.GetBytes(norm))
                            bodyToSend = ENC_PREFIX & Convert.ToBase64String(encBytes)
                        Else
                            bodyToSend = norm
                        End If

                        Dim payload = $"{Proto.TAG_PRIV}{_displayName}:{peer}:{bodyToSend}{MSG_TERM}"
                        Dim data = Encoding.UTF8.GetBytes(payload)

                        If _isHost Then
                            If _hub Is Nothing Then
                                Log("Hub non initialisé.")
                                Return
                            End If
                            Dim _ignore = _hub.SendToAsync(peer, data)
                        Else
                            If _stream Is Nothing Then
                                Log("Not connected.")
                                Return
                            End If
                            Dim _ignore = _stream.SendAsync(data, CancellationToken.None)
                        End If
                    End If

EchoAndPersist:
                    Dim line As String = If(viaP2P, "[P2P] ", "[RELAY] ") & norm
                    If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
                        Me.BeginInvoke(Sub() AppendToPrivate(peer, _displayName, line))
                    Else
                        AppendToPrivate(peer, _displayName, line)
                    End If

                    StoreMsg(peer, outgoing:=True, body:=norm, viaP2P:=viaP2P)

                Catch ex As Exception
                    Log("[PRIVATE] send error: " & ex.Message)
                End Try
            End Sub

            ' --- Envoi fichier (déclenché depuis PrivateChatForm) ---  ✅ version Task.Run async
            Dim sendFileCb As Action =
            Sub()
                Try
                    Dim dest As String = peer

                    If IsStrictEnabled() AndAlso Not IsPeerTrusted(dest) Then
                        Log($"[SECURITY] Envoi fichier bloqué (pair non de confiance): {dest}")
                        Return
                    End If

                    Dim useEnc As Boolean = IsEncryptRelayEnabled()
                    If useEnc AndAlso Not HasRelayKey(dest) Then
                        EnsureRelayHandshake(dest)
                        Log("[ENC] Clé non établie avec " & dest & ". Relance l’envoi une fois la clé OK.")
                        Return
                    End If

                    Using ofd As New OpenFileDialog()
                        If ofd.ShowDialog(Me) <> DialogResult.OK Then Return
                        Dim fi As New FileInfo(ofd.FileName)
                        If Not fi.Exists Then Return

                        ' Ouvre/active la fenêtre privée et démarre la progression TX
                        Dim frmPeer = EnsureAndGetPrivateChatForm(dest)
                        If frmPeer IsNot Nothing Then
                            frmPeer.StartSendProgress(fi.Name, fi.Length)
                        End If

                        ' 🚀 en arrière-plan pour ne pas bloquer l’UI
                        Dim _bg = Threading.Tasks.Task.Run(
                            Async Function() As Threading.Tasks.Task
                                Try
                                    Dim transferId = Guid.NewGuid().ToString("N")
                                    Dim fnameToSend = If(useEnc, ENC_FILE_MARK & fi.Name, fi.Name)
                                    Dim meta = $"{Proto.TAG_FILEMETA}{transferId}:{_displayName}:{dest}:{fnameToSend}:{fi.Length}{MSG_TERM}"
                                    Dim metaBytes = Encoding.UTF8.GetBytes(meta)

                                    If _isHost Then
                                        If _hub Is Nothing Then
                                            Log("Hub non initialisé.") : Return
                                        End If
                                        Await _hub.SendToAsync(dest, metaBytes)
                                    Else
                                        If _stream Is Nothing Then
                                            Log("Not connected.") : Return
                                        End If
                                        Await _stream.SendAsync(metaBytes, Threading.CancellationToken.None)
                                    End If

                                    Using fs = fi.OpenRead()
                                        Dim buffer(32768 - 1) As Byte
                                        Dim totalSent As Long = 0
                                        While True
                                            Dim read = fs.Read(buffer, 0, buffer.Length) ' sync OK (Task.Run)
                                            If read <= 0 Then Exit While

                                            Dim chunkData As String
                                            If useEnc Then
                                                Dim enc = EncryptForPeer(dest, buffer, read)
                                                chunkData = ENC_PREFIX & Convert.ToBase64String(enc)
                                            Else
                                                chunkData = Convert.ToBase64String(buffer, 0, read)
                                            End If

                                            Dim chunkMsg = $"{Proto.TAG_FILECHUNK}{transferId}:{chunkData}{MSG_TERM}"
                                            Dim chunkBytes = Encoding.UTF8.GetBytes(chunkMsg)

                                            If _isHost Then
                                                Await _hub.SendToAsync(dest, chunkBytes)
                                            Else
                                                Await _stream.SendAsync(chunkBytes, Threading.CancellationToken.None)
                                            End If

                                            totalSent += read
                                            If frmPeer IsNot Nothing Then frmPeer.UpdateSendProgress(totalSent, fi.Length)
                                        End While
                                    End Using

                                    Dim endMsg = $"{Proto.TAG_FILEEND}{transferId}{MSG_TERM}"
                                    Dim endBytes = Encoding.UTF8.GetBytes(endMsg)
                                    If _isHost Then
                                        Await _hub.SendToAsync(dest, endBytes)
                                    Else
                                        Await _stream.SendAsync(endBytes, Threading.CancellationToken.None)
                                    End If

                                    Log($"[RELAY{If(useEnc, "+ENC", "")}] Fichier {fi.Name} envoyé à {dest}")
                                    If frmPeer IsNot Nothing Then
                                        frmPeer.AppendMessage(_displayName, $"[FILE] ✅ Envoi terminé : {fi.Name} ({fi.Length} octets)")
                                    End If
                                Catch ex As Exception
                                    Log("[PRIVATE] send file error: " & ex.Message)
                                Finally
                                    If frmPeer IsNot Nothing Then frmPeer.EndSendProgress()
                                End Try
                            End Function)
                    End Using
                Catch ex As Exception
                    Log("[PRIVATE] send file error: " & ex.Message)
                End Try
            End Sub

            ' --- Création de la fenêtre privée avec les 2 callbacks ---
            frm = New PrivateChatForm(_displayName, peer, sendCb, sendFileCb)
            _privateChats(peer) = frm

            ' États initiaux + refresh immédiat
            RefreshPrivateChatStatus(peer)

            Dim initialTake = GetAndEnsureHistCount(peer)
            LoadHistoryIntoPrivate(peer, frm, initialTake)

            AddHandler frm.StartP2PRequested,
            Sub()
                Try
                    P2PManager.StartP2P(peer, New String() {"stun:stun.l.google.com:19302"})
                Catch ex As Exception
                    Log("[P2P] start error: " & ex.Message)
                End Try
            End Sub

            AddHandler frm.PurgeRequested,
            Sub()
                Try
                    LocalDb.ExecNonQuery("DELETE FROM Messages WHERE PeerName=@p;", LocalDb.P("@p", peer))
                    frm.ClearMessages()
                    _histCount(peer) = 0
                Catch ex As Exception
                    Log("[DB] purge error: " & ex.Message)
                End Try
            End Sub

            ' ✅ Démarre l’auth Ed25519 dès la 1ère ouverture
            SendIdHello(peer)

            frm.Show(Me)
            frm.Activate()
            frm.BringToFront()

        Else
            ' fenêtre déjà existante → relance un HELLO si besoin
            SendIdHello(peer)

            frm.Show(Me)
            frm.Activate()
            frm.BringToFront()
            RefreshPrivateChatStatus(peer)
        End If
    End Sub


    ' ---------- Envoi fichier centralisé (ouvre la PrivateChatForm + progress + notif) ----------
    Private Sub SendFileToPeer(dest As String)
        Try
            If String.IsNullOrWhiteSpace(dest) Then
                Log("Destination invalide pour l'envoi de fichier.")
                Return
            End If

            If IsStrictEnabled() AndAlso Not IsPeerTrusted(dest) Then
                Log($"[SECURITY] Envoi fichier bloqué (pair non de confiance): {dest}")
                Return
            End If

            Dim useEnc As Boolean = IsEncryptRelayEnabled()
            If useEnc AndAlso Not HasRelayKey(dest) Then
                EnsureRelayHandshake(dest)
                Log("[ENC] Clé non établie avec " & dest & ". Relance l’envoi une fois la clé OK.")
                Return
            End If

            Using ofd As New OpenFileDialog()
                If ofd.ShowDialog(Me) <> DialogResult.OK Then Return
                Dim fi As New FileInfo(ofd.FileName)
                If Not fi.Exists Then Return

                ' --- Toujours ouvrir/activer la PrivateChatForm et démarrer la progress ---
                Dim frmPeer = EnsureAndGetPrivateChatForm(dest)
                If frmPeer IsNot Nothing Then
                    frmPeer.StartSendProgress(fi.Name, fi.Length)
                End If

                Try
                    Dim transferId = Guid.NewGuid().ToString("N")
                    Dim fnameToSend = If(useEnc, ENC_FILE_MARK & fi.Name, fi.Name)
                    Dim meta = $"{Proto.TAG_FILEMETA}{transferId}:{_displayName}:{dest}:{fnameToSend}:{fi.Length}{MSG_TERM}"
                    Dim metaBytes = Encoding.UTF8.GetBytes(meta)

                    If _isHost Then
                        If _hub Is Nothing Then Log("Hub non initialisé.") : Return
                        Dim _ignore0 = _hub.SendToAsync(dest, metaBytes)
                        Using fs = fi.OpenRead()
                            Dim buffer(32768 - 1) As Byte
                            Dim totalSent As Long = 0
                            While True
                                Dim read = fs.Read(buffer, 0, buffer.Length)
                                If read <= 0 Then Exit While

                                Dim chunkData As String
                                If useEnc Then
                                    Dim enc = EncryptForPeer(dest, buffer, read)
                                    chunkData = ENC_PREFIX & Convert.ToBase64String(enc)
                                Else
                                    chunkData = Convert.ToBase64String(buffer, 0, read)
                                End If

                                Dim chunkMsg = $"{Proto.TAG_FILECHUNK}{transferId}:{chunkData}{MSG_TERM}"
                                Dim _ignore1 = _hub.SendToAsync(dest, Encoding.UTF8.GetBytes(chunkMsg))

                                totalSent += read
                                If frmPeer IsNot Nothing Then frmPeer.UpdateSendProgress(totalSent, fi.Length)
                            End While
                        End Using
                        Dim endMsg = $"{Proto.TAG_FILEEND}{transferId}{MSG_TERM}"
                        Dim _ignore2 = _hub.SendToAsync(dest, Encoding.UTF8.GetBytes(endMsg))
                    Else
                        If _stream Is Nothing Then Log("Not connected.") : Return
                        Dim _ignore0 = _stream.SendAsync(metaBytes, Threading.CancellationToken.None)
                        Using fs = fi.OpenRead()
                            Dim buffer(32768 - 1) As Byte
                            Dim totalSent As Long = 0
                            While True
                                Dim read = fs.Read(buffer, 0, buffer.Length)
                                If read <= 0 Then Exit While

                                Dim chunkData As String
                                If useEnc Then
                                    Dim enc = EncryptForPeer(dest, buffer, read)
                                    chunkData = ENC_PREFIX & Convert.ToBase64String(enc)
                                Else
                                    chunkData = Convert.ToBase64String(buffer, 0, read)
                                End If

                                Dim chunkMsg = $"{Proto.TAG_FILECHUNK}{transferId}:{chunkData}{MSG_TERM}"
                                Dim _ignore1 = _stream.SendAsync(Encoding.UTF8.GetBytes(chunkMsg), Threading.CancellationToken.None)

                                totalSent += read
                                If frmPeer IsNot Nothing Then frmPeer.UpdateSendProgress(totalSent, fi.Length)
                            End While
                        End Using
                        Dim endMsg = $"{Proto.TAG_FILEEND}{transferId}{MSG_TERM}"
                        Dim _ignore2 = _stream.SendAsync(Encoding.UTF8.GetBytes(endMsg), Threading.CancellationToken.None)
                    End If

                    Log($"[RELAY{If(useEnc, "+ENC", "")}] Fichier {fi.Name} envoyé à {dest}")

                Finally
                    If frmPeer IsNot Nothing Then
                        frmPeer.EndSendProgress()
                        ' petite notif dans le log de la fenêtre privée
                        Try
                            frmPeer.AppendMessage(_displayName, $"[FILE] ✅ Envoi terminé : {fi.Name} ({fi.Length} octets)")
                        Catch
                        End Try
                    End If
                End Try
            End Using

        Catch ex As Exception
            Log("[PRIVATE] send file error: " & ex.Message)
        End Try
    End Sub

    Private Sub RefreshPrivateChatStatus(peer As String)
        Dim frm As PrivateChatForm = Nothing
        If Not _privateChats.TryGetValue(peer, frm) OrElse frm Is Nothing OrElse frm.IsDisposed Then Return

        Dim connected As Boolean = (_p2pConn.ContainsKey(peer) AndAlso _p2pConn(peer))
        ' Crypto ON si P2P est connecté (DTLS) ou si clé relay établie
        Dim crypto As Boolean = connected OrElse HasRelayKey(peer)
        Dim idok As Boolean = (_idVerified.ContainsKey(peer) AndAlso _idVerified(peer))

        frm.SetP2PState(connected)
        frm.SetCryptoState(crypto)
        frm.SetAuthState(idok)
    End Sub

    Private Function GetAndEnsureHistCount(peer As String) As Integer
        Dim take As Integer = 50
        If _histCount.ContainsKey(peer) Then take = Math.Max(10, _histCount(peer))
        _histCount(peer) = take
        Return take
    End Function

    Private Sub EnsurePrivateChat(peer As String)
        If String.IsNullOrWhiteSpace(peer) Then Return
        If Not _privateChats.ContainsKey(peer) OrElse _privateChats(peer) Is Nothing OrElse _privateChats(peer).IsDisposed Then
            OpenPrivateChat(peer)
        End If
    End Sub

    Private Sub AppendToPrivate(peer As String, sender As String, body As String)
        Dim frm As PrivateChatForm = Nothing
        If _privateChats.TryGetValue(peer, frm) AndAlso frm IsNot Nothing AndAlso Not frm.IsDisposed Then
            frm.AppendMessage(sender, body)
        End If
    End Sub

    Private Sub FileWatchdog_Tick(sender As Object, e As EventArgs)
        ' timeouts éventuels…
    End Sub

    Private Sub InitP2PManager()
        P2PManager.Init(
            sendSignal:=Function(dest As String, line As String)
                            Dim data = Encoding.UTF8.GetBytes(line & MSG_TERM)
                            If _isHost Then
                                If _hub Is Nothing Then
                                    Return Threading.Tasks.Task.CompletedTask
                                End If
                                Return _hub.SendToAsync(dest, data)
                            Else
                                If _stream Is Nothing Then
                                    Return Threading.Tasks.Task.CompletedTask
                                End If
                                Return _stream.SendAsync(data, CancellationToken.None)
                            End If
                        End Function,
            localDisplayName:=_displayName
        )

        If Not _p2pHandlersHooked Then
            AddHandler P2PManager.OnP2PText, AddressOf OnP2PText_FromP2P
            AddHandler P2PManager.OnP2PState,
                Sub(peer As String, connected As Boolean)
                    _p2pConn(peer) = connected
                    ' DTLS = crypto ON côté P2P
                    _cryptoActive(peer) = connected
                    Dim frm As PrivateChatForm = Nothing
                    If _privateChats.TryGetValue(peer, frm) AndAlso frm IsNot Nothing AndAlso Not frm.IsDisposed Then
                        RefreshPrivateChatStatus(peer)
                    End If
                End Sub
            _p2pHandlersHooked = True
        End If
    End Sub

    ' ---------- Security Center ----------
    Private Sub btnSecurityCenter_Click(sender As Object, e As EventArgs) Handles btnSecurity.Click
        Try
            Dim f As New SecurityCenterForm(String.Empty, AddressOf GetSessionSnapshot)
            f.Show(Me)
        Catch ex As Exception
            Log("[UI] Security Center: " & ex.Message)
        End Try
    End Sub

    ' ====== Helpers chiffrement RELAY ======
    Private Function HasRelayKey(peer As String) As Boolean
        Return _relayKeys.ContainsKey(peer)
    End Function

    Private Sub EnsureRelayHandshake(peer As String)
        ' Pour l’instant, le mode "hybrid" n’active pas un vrai PQ (le stub ne fournit pas KeyGen/Decapsulate(sk,ct)).
        ' On reste en ECDH classique mais on change le label d’info HKDF pour la future compat.
        EnsureRelayHandshakeClassic(peer)
    End Sub

    ' --- Classique ECDH ---
    Private Sub EnsureRelayHandshakeClassic(peer As String)
        SyncLock _relayEcdhPending
            If _relayEcdhPending.ContainsKey(peer) OrElse _relayKeys.ContainsKey(peer) Then Exit Sub
            Dim ecdh = New ECDiffieHellmanCng(ECCurve.NamedCurves.nistP256)
            ecdh.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash
            ecdh.HashAlgorithm = CngAlgorithm.Sha256
            _relayEcdhPending(peer) = ecdh

            Dim pub = ecdh.PublicKey.ToByteArray() ' CNG blob
            Dim body = ENC_HELLO & Convert.ToBase64String(pub)
            Dim payload = $"{Proto.TAG_PRIV}{_displayName}:{peer}:{body}{MSG_TERM}"
            Dim bytes = Encoding.UTF8.GetBytes(payload)

            If _isHost Then
                If _hub IsNot Nothing Then _hub.SendToAsync(peer, bytes)
            Else
                If _stream IsNot Nothing Then _stream.SendAsync(bytes, CancellationToken.None)
            End If
            Log("[ENC] HELLO(classic) => " & peer)
        End SyncLock
    End Sub

    Private Sub ProcessEncHello(fromPeer As String, payload As String)
        Try
            Dim parts = payload.Trim()
            If parts.StartsWith(":") Then parts = parts.Substring(1) ' tolérance si ":" traîne

            ' Autoriser formellement un séparateur '|' pour compat future, mais on n'utilise que la 1ère partie (ECDH)
            Dim pieces = parts.Split("|"c)
            Dim ecdhPubRemoteB64 As String = pieces(0).Trim()

            Dim remotePub = Convert.FromBase64String(ecdhPubRemoteB64)
            ' ECDH répondant
            Dim ecdh As ECDiffieHellmanCng
            SyncLock _relayEcdhPending
                If Not _relayEcdhPending.TryGetValue(fromPeer, ecdh) Then
                    ecdh = New ECDiffieHellmanCng(ECCurve.NamedCurves.nistP256)
                    ecdh.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash
                    ecdh.HashAlgorithm = CngAlgorithm.Sha256
                    _relayEcdhPending(fromPeer) = ecdh
                End If
            End SyncLock

            Dim remoteKey = CngKey.Import(remotePub, CngKeyBlobFormat.EccPublicBlob)
            Dim sharedMat = ecdh.DeriveKeyMaterial(remoteKey)
            Dim myPub = ecdh.PublicKey.ToByteArray()

            Dim infoLabel As String = If(IsPqRelayEnabled(), "relay-hybrid:", "relay-classic:")
            Dim root = sharedMat
            Dim info As String = infoLabel & SortedPairTag(_displayName, fromPeer)
            Dim derivedKey As Byte() = ChatP2P.Crypto.KeyScheduleHKDF.DeriveKey(Nothing, root, info, 32)

            SyncLock _relayKeys : _relayKeys(fromPeer) = derivedKey : End SyncLock
            ' ❌ ne pas toucher à _idVerified ici (ECDH ≠ identité)
            RefreshPrivateChatStatus(fromPeer)

            ' Envoi ACK (ECDH pub uniquement)
            Dim ackBody = ENC_ACK & Convert.ToBase64String(myPub)
            Dim payloadAck = $"{Proto.TAG_PRIV}{_displayName}:{fromPeer}:{ackBody}{MSG_TERM}"
            Dim bytes = Encoding.UTF8.GetBytes(payloadAck)
            If _isHost Then
                If _hub IsNot Nothing Then _hub.SendToAsync(fromPeer, bytes)
            Else
                If _stream IsNot Nothing Then _stream.SendAsync(bytes, Threading.CancellationToken.None)
            End If


            ' ✅ libère le pending côté répondeur
            SyncLock _relayEcdhPending
                _relayEcdhPending.Remove(fromPeer)
            End SyncLock

            Log("[ENC] HELLO<= & ACK=> " & fromPeer)
        Catch ex As Exception
            Log("[ENC] ProcessEncHello error: " & ex.Message)
        End Try
    End Sub

    Private Sub ProcessEncAck(fromPeer As String, payload As String)
        Try
            Dim parts = payload.Trim()
            If parts.StartsWith(":") Then parts = parts.Substring(1)

            Dim pieces = parts.Split("|"c)
            Dim ecdhPubResponderB64 As String = pieces(0).Trim()

            Dim remotePub = Convert.FromBase64String(ecdhPubResponderB64)

            Dim ecdh As ECDiffieHellmanCng = Nothing
            SyncLock _relayEcdhPending
                If Not _relayEcdhPending.TryGetValue(fromPeer, ecdh) OrElse ecdh Is Nothing Then
                    Log("[ENC] ACK sans ECDH pending pour " & fromPeer)
                    Return
                End If
                _relayEcdhPending.Remove(fromPeer)
            End SyncLock

            Dim remoteKey = CngKey.Import(remotePub, CngKeyBlobFormat.EccPublicBlob)
            Dim sharedMat = ecdh.DeriveKeyMaterial(remoteKey)

            Dim infoLabel As String = If(IsPqRelayEnabled(), "relay-hybrid:", "relay-classic:")
            Dim root = sharedMat
            Dim info As String = infoLabel & SortedPairTag(_displayName, fromPeer)
            Dim derivedKey As Byte() = ChatP2P.Crypto.KeyScheduleHKDF.DeriveKey(Nothing, root, info, 32)

            SyncLock _relayKeys : _relayKeys(fromPeer) = derivedKey : End SyncLock
            ' ❌ ne pas marquer _idVerified ici
            RefreshPrivateChatStatus(fromPeer)

            ' Déclenche l’auth Ed25519
            SendIdHello(fromPeer)

            ' vide la file de messages en attente
            FlushPendingEnc(fromPeer)

            Log("[ENC] Clé établie avec " & fromPeer)
        Catch ex As Exception
            Log("[ENC] ProcessEncAck error: " & ex.Message)
        End Try
    End Sub

    Private Function SortedPairTag(a As String, b As String) As String
        Dim x = If(a, "")
        Dim y = If(b, "")
        If StringComparer.OrdinalIgnoreCase.Compare(x, y) <= 0 Then
            Return x & "|" & y
        Else
            Return y & "|" & x
        End If
    End Function

    Private Function Concat(a As Byte(), b As Byte()) As Byte()
        If a Is Nothing OrElse a.Length = 0 Then Return If(b, Array.Empty(Of Byte)())
        If b Is Nothing OrElse b.Length = 0 Then Return a.ToArray()
        Dim r(a.Length + b.Length - 1) As Byte
        Buffer.BlockCopy(a, 0, r, 0, a.Length)
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length)
        Return r
    End Function

    Private Sub QueuePendingEnc(peer As String, msg As String)
        SyncLock _pendingEncMsgs
            Dim q As Queue(Of String) = Nothing
            If Not _pendingEncMsgs.TryGetValue(peer, q) OrElse q Is Nothing Then
                q = New Queue(Of String)()
                _pendingEncMsgs(peer) = q
            End If
            q.Enqueue(msg)
        End SyncLock
    End Sub

    Private Sub FlushPendingEnc(peer As String)
        Dim items As List(Of String) = Nothing
        SyncLock _pendingEncMsgs
            Dim q As Queue(Of String) = Nothing
            If _pendingEncMsgs.TryGetValue(peer, q) AndAlso q IsNot Nothing AndAlso q.Count > 0 Then
                items = q.ToList()
                q.Clear()
            End If
        End SyncLock
        If items Is Nothing Then Exit Sub

        For Each m In items
            Try
                Dim encBytes = EncryptForPeer(peer, Encoding.UTF8.GetBytes(m))
                Dim bodyToSend = ENC_PREFIX & Convert.ToBase64String(encBytes)
                Dim payload = $"{Proto.TAG_PRIV}{_displayName}:{peer}:{bodyToSend}{MSG_TERM}"
                Dim data = Encoding.UTF8.GetBytes(payload)
                If _isHost Then
                    If _hub IsNot Nothing Then _hub.SendToAsync(peer, data)
                Else
                    If _stream IsNot Nothing Then _stream.SendAsync(data, CancellationToken.None)
                End If
                Log("[ENC] (flush) => " & peer)
            Catch ex As Exception
                Log("[ENC] flush error: " & ex.Message)
            End Try
        Next
    End Sub

    Private Function EncryptForPeer(peer As String, src As Byte(), Optional count As Integer = -1) As Byte()
        Dim key As Byte() = Nothing
        SyncLock _relayKeys : _relayKeys.TryGetValue(peer, key) : End SyncLock
        If key Is Nothing Then Throw New InvalidOperationException("Clé non disponible pour " & peer)

        If count < 0 Then count = src.Length

        Dim plain(count - 1) As Byte
        Buffer.BlockCopy(src, 0, plain, 0, count)

        Dim nonce(11) As Byte
        _rng.GetBytes(nonce)

        Dim ct(count - 1) As Byte
        Dim tag(15) As Byte

        Using aes As New AesGcm(key)
            aes.Encrypt(nonce, plain, ct, tag)
        End Using

        Dim output(nonce.Length + ct.Length + tag.Length - 1) As Byte
        Buffer.BlockCopy(nonce, 0, output, 0, nonce.Length)
        Buffer.BlockCopy(ct, 0, output, nonce.Length, ct.Length)
        Buffer.BlockCopy(tag, 0, output, nonce.Length + ct.Length, tag.Length)
        Return output
    End Function

    Private Function DecryptFromPeer(peer As String, blob As Byte()) As Byte()
        Dim key As Byte() = Nothing
        SyncLock _relayKeys : _relayKeys.TryGetValue(peer, key) : End SyncLock
        If key Is Nothing Then Throw New InvalidOperationException("Clé non disponible pour " & peer)

        If blob.Length < 12 + 16 Then Throw New ArgumentException("Blob crypté invalide")

        Dim nonce(11) As Byte
        Buffer.BlockCopy(blob, 0, nonce, 0, 12)
        Dim tag(15) As Byte
        Buffer.BlockCopy(blob, blob.Length - 16, tag, 0, 16)
        Dim ctLen As Integer = blob.Length - 12 - 16
        Dim ct(ctLen - 1) As Byte
        Buffer.BlockCopy(blob, 12, ct, 0, ctLen)

        Dim plain(ctLen - 1) As Byte
        Using aes As New AesGcm(key)
            aes.Decrypt(nonce, ct, tag, plain)
        End Using
        Return plain
    End Function

    Private Sub btnTestPQ_Click(sender As Object, e As EventArgs) Handles btnTestPQ.Click
        Try
            ' --- KeyGen ---
            Dim kp = ChatP2P.Crypto.KemPqStub.KeyGen()
            Dim pk As Byte() = kp.pk
            Dim sk As Byte() = kp.sk
            Log("[SELFTEST] KEM KeyGen ok.")

            ' --- Encapsulate ---
            Dim enc = ChatP2P.Crypto.KemPqStub.Encapsulate(pk)
            Dim ct As Byte() = enc.cipherText
            Dim ssEnc As Byte() = enc.sharedSecret
            Log("[SELFTEST] KEM Encapsulate ok.")

            ' --- Decapsulate ---
            Dim ssDec As Byte() = ChatP2P.Crypto.KemPqStub.Decapsulate(sk, ct)
            Log("[SELFTEST] KEM Decapsulate ok.")

            ' --- Vérification ---
            Dim ok As Boolean = ssEnc.SequenceEqual(ssDec)
            Log($"[SELFTEST] KEM ss match = {ok}")

            ' HKDF sanity check
            Dim key As Byte() = ChatP2P.Crypto.KeyScheduleHKDF.DeriveKey(Nothing, ssEnc, "pq-test", 32)
            Log($"[SELFTEST] HKDF key len = {key.Length}")

        Catch ex As Exception
            Log("[SELFTEST] EXC: " & ex.ToString())
        End Try
    End Sub

End Class
