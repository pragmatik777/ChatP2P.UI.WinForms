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
Imports ChatP2P.App
Imports ChatP2P.Core
Imports ChatP2P.App.Protocol
Imports Proto = ChatP2P.App.Protocol.Tags

' ChatP2P.UI.WinForms/Form1.vb
Public Class Form1

    Private Const MSG_TERM As String = vbLf

    Private Class FileRecvState
        Public Id As String = ""
        Public FromName As String = ""
        Public File As FileStream
        Public Path As String = ""
        Public Expected As Long
        Public Received As Long
        Public LastTickUtc As DateTime
    End Class

    ' remplace les tuples (DateTime, String) par une petite classe
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

    ' ---- guard migration schéma DB
    Private _dbSchemaChecked As Boolean = False

    ' ---------- Utils ----------
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

    Private Sub Log(msg As String, Optional verbose As Boolean = False)
        ' Toujours renvoyer sur le thread UI si besoin
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

    ' ---------- DB: migration schéma ----------
    Private Sub EnsureDbSchema()
        If _dbSchemaChecked Then Exit Sub
        _dbSchemaChecked = True

        Try
            Dim peersInfo = LocalDb.Query("PRAGMA table_info(Peers);")
            Dim peerCols As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each r As Data.DataRow In peersInfo.Rows
                Dim cn = TryCast(r("name"), String)
                If Not String.IsNullOrWhiteSpace(cn) Then peerCols.Add(cn)
            Next
            If Not peerCols.Contains("CreatedUtc") Then
                Try : LocalDb.ExecNonQuery("ALTER TABLE Peers ADD COLUMN CreatedUtc TEXT;") : Catch : End Try
            End If
            If Not peerCols.Contains("LastSeenUtc") Then
                Try : LocalDb.ExecNonQuery("ALTER TABLE Peers ADD COLUMN LastSeenUtc TEXT;") : Catch : End Try
            End If
            If Not peerCols.Contains("Trusted") Then
                Try : LocalDb.ExecNonQuery("ALTER TABLE Peers ADD COLUMN Trusted INTEGER NOT NULL DEFAULT 0;") : Catch : End Try
            End If
        Catch ex As Exception
            Log("[DB] schema (Peers) check failed: " & ex.Message, verbose:=True)
        End Try

        Try
            Dim msgInfo = LocalDb.Query("PRAGMA table_info(Messages);")
            Dim msgCols As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each r As Data.DataRow In msgInfo.Rows
                Dim cn = TryCast(r("name"), String)
                If Not String.IsNullOrWhiteSpace(cn) Then msgCols.Add(cn)
            Next
            If Not msgCols.Contains("IsP2P") Then
                Try : LocalDb.ExecNonQuery("ALTER TABLE Messages ADD COLUMN IsP2P INTEGER NOT NULL DEFAULT 0;") : Catch : End Try
            End If
            If Not msgCols.Contains("Direction") Then
                Try : LocalDb.ExecNonQuery("ALTER TABLE Messages ADD COLUMN Direction TEXT;") : Catch : End Try
            End If
            If Not msgCols.Contains("CreatedUtc") Then
                Try : LocalDb.ExecNonQuery("ALTER TABLE Messages ADD COLUMN CreatedUtc TEXT;") : Catch : End Try
            End If
        Catch ex As Exception
            Log("[DB] schema (Messages) check failed: " & ex.Message, verbose:=True)
        End Try
    End Sub

    ' ---------- Form ----------
    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' DB
        Try
            ChatP2P.Core.LocalDb.Init()
            EnsureDbSchema() ' migration auto
        Catch ex As Exception
            Log("[DB] init failed: " & ex.Message)
        End Try

        ' (Optionnel) Génération X25519 si nécessaire par le core
        Try
            ' juste pour s’assurer que la crypto s’initialise — résultat ignoré
            Call ChatP2P.Crypto.KexX25519.GenerateKeyPair()
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
            ' voie normale (schéma migré)
            LocalDb.ExecNonQuery(
                "INSERT INTO Peers(Name, CreatedUtc, Trusted) VALUES(@n,@ts,0)
                 ON CONFLICT(Name) DO NOTHING;",
                LocalDb.P("@n", peer), LocalDb.P("@ts", nowIso)
            )
            LocalDb.ExecNonQuery(
                "UPDATE Peers SET LastSeenUtc=@ts WHERE Name=@n;",
                LocalDb.P("@ts", nowIso), LocalDb.P("@n", peer)
            )
        Catch
            ' fallback anciens schémas (sans colonnes)
            Try
                LocalDb.ExecNonQuery(
                    "INSERT INTO Peers(Name, Trusted) VALUES(@n,0)
                     ON CONFLICT(Name) DO NOTHING;",
                    LocalDb.P("@n", peer)
                )
            Catch
            End Try
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
            frm.AppendMessage(sender, If(isp2p, "[P2P] ", "") & body)
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

        ' ré-init avec le bon nom
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
                        AppendToPrivate(senderName, senderName, body)
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

        ' ré-init avec le bon nom
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
            ' écho local pour le host (sinon il ne se voit pas)
            Log($"{_displayName}: {msg}")
        Else
            If _stream Is Nothing Then Log("Not connected.") : Return
            Await _stream.SendAsync(data, CancellationToken.None)
            ' Facultatif: si tu constates un double-affichage côté clients,
            ' commente la ligne suivante (le hub renverra aussi le message).
            ' Log($"{_displayName}: {msg}")
        End If

        txtMessage.Clear()
    End Sub


    ' ---------- Fichiers TX ----------
    Private Async Sub btnSendFile_Click(sender As Object, e As EventArgs) Handles btnSendFile.Click
        Dim dest As String = ""
        If lstPeers IsNot Nothing AndAlso lstPeers.SelectedItem IsNot Nothing Then
            dest = lstPeers.SelectedItem.ToString().Trim()
        End If
        If String.IsNullOrWhiteSpace(dest) Then
            Log("Sélectionne d'abord un pair dans la liste (lstPeers).")
            Return
        End If

        If IsStrictEnabled() AndAlso Not IsPeerTrusted(dest) Then
            Log($"[SECURITY] Envoi fichier bloqué (pair non de confiance): {dest}")
            Return
        End If

        Using ofd As New OpenFileDialog()
            If ofd.ShowDialog(Me) <> DialogResult.OK Then Return
            Dim fi As New FileInfo(ofd.FileName)
            If Not fi.Exists Then Return

            Dim transferId = Guid.NewGuid().ToString("N")
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

        Dim savePath = Path.Combine(GetRecvFolder(), $"{fromName}__{fname}")
        savePath = MakeUniquePath(savePath)

        Dim st As New FileRecvState() With {
            .Id = tid, .FromName = fromName, .Path = savePath,
            .Expected = fsize, .Received = 0, .LastTickUtc = DateTime.UtcNow
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

        Log($"Réception fichier {fname} de {fromName} (taille {fsize} octets)")
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
        If pbRecv IsNot Nothing Then pbRecv.Value = Math.Max(0, Math.Min(100, percent))
        If lblRecvProgress IsNot Nothing Then lblRecvProgress.Text = pbRecv.Value & "%"
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

        Log("Fichier reçu: " & st.Path)
        SyncLock _fileRecv
            _fileRecv.Remove(tid)
        End SyncLock

        If pbRecv IsNot Nothing Then pbRecv.Value = 0
        If lblRecvProgress IsNot Nothing Then lblRecvProgress.Text = ""
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
                                If IsStrictEnabled() AndAlso Not IsPeerTrusted(fromPeer) Then
                                    Log($"[SECURITY] Message RELAY IN bloqué (pair non de confiance): {fromPeer}")
                                Else
                                    EnsurePrivateChat(fromPeer)
                                    AppendToPrivate(fromPeer, fromPeer, body)
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
        AppendToPrivate(peer, peer, "[P2P] " & norm)
    End Sub
    ' ---------- Fenêtres privées ----------
    Private Sub OpenPrivateChat(peer As String)
        If String.IsNullOrWhiteSpace(peer) Then Return

        Dim frm As PrivateChatForm = Nothing
        If Not _privateChats.TryGetValue(peer, frm) OrElse frm Is Nothing OrElse frm.IsDisposed Then

            ' Callback d’envoi appelé par la fenêtre
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
                        ' RELAY
                        Dim payload = $"{Proto.TAG_PRIV}{_displayName}:{peer}:{norm}{MSG_TERM}"
                        Dim data = Encoding.UTF8.GetBytes(payload)

                        If _isHost Then
                            If _hub Is Nothing Then
                                Log("Hub non initialisé.")
                                Return
                            End If
                            Dim _ignore = _hub.SendToAsync(peer, data) ' fire & forget
                        Else
                            If _stream Is Nothing Then
                                Log("Not connected.")
                                Return
                            End If
                            Dim _ignore = _stream.SendAsync(data, CancellationToken.None) ' fire & forget
                        End If
                    End If

                    ' ÉCHO LOCAL garanti
                    Dim line As String = If(viaP2P, "[P2P] ", "[RELAY] ") & norm
                    If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
                        Me.BeginInvoke(Sub() AppendToPrivate(peer, _displayName, line))
                    Else
                        AppendToPrivate(peer, _displayName, line)
                    End If

                    ' Persistance
                    StoreMsg(peer, outgoing:=True, body:=norm, viaP2P:=viaP2P)

                Catch ex As Exception
                    Log("[PRIVATE] send error: " & ex.Message)
                End Try
            End Sub

            ' Création de la fenêtre privée
            frm = New PrivateChatForm(_displayName, peer, sendCb)
            _privateChats(peer) = frm

            ' États initiaux
            Dim cryptoActive = _cryptoActive.ContainsKey(peer) AndAlso _cryptoActive(peer)
            Dim connected = _p2pConn.ContainsKey(peer) AndAlso _p2pConn(peer)
            Dim idok = _idVerified.ContainsKey(peer) AndAlso _idVerified(peer)
            frm.SetAuthState(idok)
            frm.SetCryptoState(cryptoActive)
            frm.SetP2PState(connected)

            ' Historique initial
            Dim initialTake = GetAndEnsureHistCount(peer)
            LoadHistoryIntoPrivate(peer, frm, initialTake)

            ' Démarrer P2P depuis la fenêtre
            AddHandler frm.StartP2PRequested,
            Sub()
                Try
                    P2PManager.StartP2P(peer, New String() {"stun:stun.l.google.com:19302"})
                Catch ex As Exception
                    Log("[P2P] start error: " & ex.Message)
                End Try
            End Sub

            ' Purge de l’historique
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

            frm.Show(Me)
            frm.Activate()
            frm.BringToFront()

        Else
            frm.Show(Me)
            frm.Activate()
            frm.BringToFront()
        End If
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
        ' idempotent : appelé au Load + après avoir fixé _displayName
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
                    If Not connected Then _cryptoActive(peer) = False

                    ' Petit log utile
                    Log($"[P2P] {peer} {(If(connected, "CONNECTÉ", "DÉCONNECTÉ"))}")

                    Dim frm As PrivateChatForm = Nothing
                    If _privateChats.TryGetValue(peer, frm) AndAlso frm IsNot Nothing AndAlso Not frm.IsDisposed Then
                        frm.SetP2PState(connected)
                        frm.SetCryptoState(_cryptoActive.ContainsKey(peer) AndAlso _cryptoActive(peer))
                        frm.SetAuthState(_idVerified.ContainsKey(peer) AndAlso _idVerified(peer))
                    End If
                End Sub

            _p2pHandlersHooked = True
        End If
    End Sub

    ' ---------- Security Center ----------
    Private Sub btnSecurityCenter_Click(sender As Object, e As EventArgs) Handles btnSecurity.Click
        Try
            Dim f As New SecurityCenterForm(String.Empty)
            f.Show(Me)
        Catch ex As Exception
            Log("[UI] Security Center: " & ex.Message)
        End Try
    End Sub

End Class
