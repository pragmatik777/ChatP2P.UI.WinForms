' ChatP2P.UI.WinForms/Form1.vb
Option Strict On
Imports System.Net
Imports System.Net.Sockets
Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Security.Cryptography
Imports ChatP2P.Core                    ' P2PManager, SignalDescriptor, INetworkStream
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

    Private Class Ed25519Identity
        Public Pub As Byte()
        Public Priv As Byte()
    End Class

    ' État de réception par transfertId (tid)
    Private ReadOnly _fileRecv As New Dictionary(Of String, FileRecvState)()

    Private _cts As CancellationTokenSource
    Private _isHost As Boolean = False

    Private _stream As INetworkStream         ' côté client (TcpNetworkStreamAdapter)
    Private _displayName As String = "Me"
    Private _recvFolder As String = ""
    Private _hub As RelayHub
    Private ReadOnly _privateChats As New Dictionary(Of String, PrivateChatForm)()

    ' ===== Load / Settings =====
    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' Identité locale Ed25519 persistée (pub=32, priv=64)
        Try
            Dim id = LoadOrCreateEd25519Identity()
            P2PManager.SetIdentity(id.Pub, id.Priv)
        Catch ex As Exception
            Log("[ID] init failed: " & ex.Message)
        End Try

        ' === Clé X25519 statique (requis par le signaling sécurisé)
        Try
            Dim kp = GenerateX25519KeyPairSodium()
            P2PManager.InitializeCrypto(kp.priv, kp.pub)
        Catch ex1 As Exception
            ' Fallback éventuel si tu ajoutes plus tard un type KexX25519 dans ChatP2P.Crypto
            Try
                Dim kp2 = GenerateX25519KeyPairReflect()
                P2PManager.InitializeCrypto(kp2.priv, kp2.pub)
            Catch ex2 As Exception
                Log("[KX] init failed: " & ex1.Message & " / reflect: " & ex2.Message)
            End Try
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

        ' Init P2PManager (routing signaling ICE sécurisé)
        InitP2PManager()

        ' Log identité vérifiée
        AddHandler P2PManager.OnPeerIdentityVerified,
            Sub(peer As String, idpub As Byte(), ok As Boolean)
                Try
                    Using sha As SHA256 = SHA256.Create()
                        Dim fp = BitConverter.ToString(sha.ComputeHash(idpub)).Replace("-", "").ToLowerInvariant()
                        Log($"[ID] {peer}: " & If(ok, "OK", "FAIL") & " fp256=" & fp)
                    End Using
                Catch
                    Log($"[ID] {peer}: " & If(ok, "OK", "FAIL"))
                End Try
            End Sub

        ' Ouvre/foreground la fenêtre privée à la réception d’un message P2P
        AddHandler P2PManager.OnP2PText, AddressOf OnP2PText_FromP2P
        AddHandler P2PManager.OnP2PState,
            Sub(peer As String, connected As Boolean)
                Log($"[P2P] {peer}: " & If(connected, "connecté", "déconnecté"), verbose:=True)
            End Sub
    End Sub

    ' Factorise l’initialisation P2PManager avec le sendSignal enrichi K/I/S
    Private Sub InitP2PManager()
        P2PManager.Init(
            sendSignal:=Function(dest As String, line As String)
                            ' Si ce n’est pas une ligne ICE, envoie tel quel
                            If Not (line.StartsWith(Proto.TAG_ICE_OFFER) OrElse line.StartsWith(Proto.TAG_ICE_ANSWER) OrElse line.StartsWith(Proto.TAG_ICE_CAND)) Then
                                Return SendRawSignal(dest, line)
                            End If

                            ' 1) Construire SignalDescriptor (peer = dest) et tagger OFFER/ANSWER/CAND
                            Dim kind As String, fromPeer As String, toPeer As String, b64 As String
                            If Not TryParseIceHead(line, kind, fromPeer, toPeer, b64) Then
                                Return SendRawSignal(dest, line) ' fallback
                            End If
                            Dim sig As New ChatP2P.Core.SignalDescriptor(dest)
                            Select Case kind
                                Case "OFFER" : sig.Tags("OFFER") = b64
                                Case "ANSWER" : sig.Tags("ANSWER") = b64
                                Case "CAND" : sig.Tags("CAND") = b64
                            End Select

                            ' 2) Enrichir via Core (PFS + Identité)
                            P2PManager.HandleOutgoingSignalSecure(sig)

                            ' 3) Reconstituer la ligne ICE avec segments K/I/S optionnels
                            Dim lineOut = BuildIceLine(kind, fromPeer, toPeer, b64, sig)

                            ' 4) Envoyer via hub/stream
                            Return SendRawSignal(dest, lineOut)
                        End Function,
            localDisplayName:=_displayName
        )
    End Sub

    Private Function SendRawSignal(dest As String, line As String) As Task
        Dim bytes = Encoding.UTF8.GetBytes(line)
        If _isHost Then
            If _hub IsNot Nothing Then Return _hub.SendToAsync(dest, bytes)
            Return Task.CompletedTask
        Else
            If _stream IsNot Nothing Then Return _stream.SendAsync(bytes, CancellationToken.None)
            Return Task.CompletedTask
        End If
    End Function

    ' ---------- Helpers sérialisation ICE sécurisée ----------
    Private Function TryParseIceHead(line As String, ByRef kind As String, ByRef fromPeer As String, ByRef toPeer As String, ByRef b64 As String) As Boolean
        kind = "" : fromPeer = "" : toPeer = "" : b64 = ""
        Dim tag As String = Nothing
        If line.StartsWith(Proto.TAG_ICE_OFFER) Then tag = Proto.TAG_ICE_OFFER : kind = "OFFER"
        If line.StartsWith(Proto.TAG_ICE_ANSWER) Then tag = Proto.TAG_ICE_ANSWER : kind = "ANSWER"
        If line.StartsWith(Proto.TAG_ICE_CAND) Then tag = Proto.TAG_ICE_CAND : kind = "CAND"
        If tag Is Nothing Then Return False
        Dim body = line.Substring(tag.Length)
        Dim p = body.Split(":"c)
        If p.Length < 3 Then Return False
        fromPeer = p(0) : toPeer = p(1) : b64 = p(2)
        Return True
    End Function

    Private Function BuildIceLine(kind As String, fromPeer As String, toPeer As String, b64 As String, sig As ChatP2P.Core.SignalDescriptor) As String
        Dim head As String = If(kind = "OFFER", Proto.TAG_ICE_OFFER, If(kind = "ANSWER", Proto.TAG_ICE_ANSWER, Proto.TAG_ICE_CAND))
        Dim line = New StringBuilder()
        line.Append(head).Append(fromPeer).Append(":").Append(toPeer).Append(":").Append(b64)
        If sig IsNot Nothing AndAlso sig.Tags IsNot Nothing Then
            Dim val As String = Nothing
            If sig.Tags.TryGetValue("KX_PUB", val) Then line.Append(":K=").Append(val)
            If sig.Tags.TryGetValue("ID_PUB", val) Then line.Append(":I=").Append(val)
            If sig.Tags.TryGetValue("ID_SIG", val) Then line.Append(":S=").Append(val)
        End If
        Return line.ToString()
    End Function

    Private Function ExtractSecureSegmentsToSig(line As String, ByRef sig As ChatP2P.Core.SignalDescriptor) As Boolean
        Dim parts = line.Split(":"c)
        If parts.Length < 4 Then Return False
        Dim toPeer = parts(2)
        sig = New ChatP2P.Core.SignalDescriptor(toPeer)
        For i = 4 To parts.Length - 1
            If parts(i).StartsWith("K=") Then sig.Tags("KX_PUB") = parts(i).Substring(2)
            If parts(i).StartsWith("I=") Then sig.Tags("ID_PUB") = parts(i).Substring(2)
            If parts(i).StartsWith("S=") Then sig.Tags("ID_SIG") = parts(i).Substring(2)
        Next
        Return sig.Tags.Count > 0
    End Function
    ' --------------------------------------------------------

    ' Reçoit un message DataChannel depuis P2PManager (ouvre/maj la fenêtre privée)
    Private Sub OnP2PText_FromP2P(peer As String, text As String)
        If Me.IsDisposed Then Return
        If Me.InvokeRequired Then
            Me.BeginInvoke(Sub() OnP2PText_FromP2P(peer, text))
            Return
        End If

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

        ' === Prépare une clé X25519 éphémère pour ce peer (PFS) ===
        Try
            P2PManager.PrepareEphemeral(sel)
        Catch
        End Try

        OpenPrivateChat(sel)
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
                    EnsurePrivateChat(senderName)
                    AppendToPrivate(senderName, senderName, text)
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

            ' === Connexion TCP directe au Hub ===
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

            If pbSend IsNot Nothing Then pbSend.Value = 0
            If lblSendProgress IsNot Nothing Then lblSendProgress.Text = "0%"

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
                            If pbSend IsNot Nothing Then pbSend.Value = percent
                            If lblSendProgress IsNot Nothing Then lblSendProgress.Text = percent & "%"
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
                            If pbSend IsNot Nothing Then pbSend.Value = percent
                            If lblSendProgress IsNot Nothing Then lblSendProgress.Text = percent & "%"
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

        If pbRecv IsNot Nothing Then pbRecv.Value = 0
        If lblRecvProgress IsNot Nothing Then lblRecvProgress.Text = "0%"
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
        Dim percent As Integer = 0
        If st.Expected > 0 Then
            percent = CInt((st.Received * 100L) \ st.Expected)
            If percent < 0 Then percent = 0
            If percent > 100 Then percent = 100
        End If
        If pbRecv IsNot Nothing Then pbRecv.Value = percent
        If lblRecvProgress IsNot Nothing Then lblRecvProgress.Text = percent & "%"
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
            If pbRecv IsNot Nothing Then pbRecv.Value = 100
            If lblRecvProgress IsNot Nothing Then lblRecvProgress.Text = "100%"
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
            ' 0) Extraire tête ICE et K/I/S éventuels → alimenter Core
            Dim kind As String = "", fromPeer As String = "", toPeer As String = "", b64 As String = ""
            If Not TryParseIceHead(raw, kind, fromPeer, toPeer, b64) Then Return

            Dim sig As ChatP2P.Core.SignalDescriptor = Nothing
            If ExtractSecureSegmentsToSig(raw, sig) Then
                ChatP2P.Core.P2PManager.HandleIncomingSignalSecure(sig)
            End If

            ' 1) Acheminer SDP/candidate vers Core
            If kind = "OFFER" Then
                Dim sdp = Encoding.UTF8.GetString(Convert.FromBase64String(b64))
                P2PManager.HandleOffer(fromPeer, sdp, New String() {"stun:stun.l.google.com:19302"})
            ElseIf kind = "ANSWER" Then
                Dim sdp = Encoding.UTF8.GetString(Convert.FromBase64String(b64))
                P2PManager.HandleAnswer(fromPeer, sdp)
            ElseIf kind = "CAND" Then
                Dim cand = Encoding.UTF8.GetString(Convert.FromBase64String(b64))
                P2PManager.HandleCandidate(fromPeer, cand)
            End If

        Catch ex As Exception
            Log("[ICE] parse error: " & ex.Message, verbose:=True)
        End Try
    End Sub

    ' ===== Fenêtres privées =====
    Private Sub OpenPrivateChat(peer As String)
        If String.IsNullOrWhiteSpace(peer) Then Return
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

            ' Ré-init pour que le callback sendSignal embarque le nouveau name
            InitP2PManager()

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
        Dim pathFile = Path.Combine(dir, "id_ed25519.bin")
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

    ' === Génération X25519 via libsodium (crypto_box) ===
    Private Function GenerateX25519KeyPairSodium() As (priv As Byte(), pub As Byte())
        ' Libsodium: crypto_box_* utilise Curve25519 (X25519) pour le KEX
        Dim kp = Sodium.PublicKeyBox.GenerateKeyPair() ' PublicKey:32, PrivateKey:32
        If kp Is Nothing OrElse kp.PublicKey Is Nothing OrElse kp.PrivateKey Is Nothing _
           OrElse kp.PublicKey.Length <> 32 OrElse kp.PrivateKey.Length <> 32 Then
            Throw New InvalidOperationException("libsodium: génération X25519 invalide.")
        End If
        Return (priv:=kp.PrivateKey, pub:=kp.PublicKey)
    End Function

    ' === Génération X25519 via réflexion (optionnel) ===
    Private Function GenerateX25519KeyPairReflect() As (priv As Byte(), pub As Byte())
        Dim kexType As Type = Nothing
        For Each asm In AppDomain.CurrentDomain.GetAssemblies()
            kexType = asm.GetType("ChatP2P.Crypto.KexX25519", False)
            If kexType IsNot Nothing Then Exit For
        Next
        If kexType Is Nothing Then Throw New MissingMethodException("ChatP2P.Crypto.KexX25519 type not found.")
        Dim cand = New String() {"GenerateKeyPair", "GenerateKeypair", "NewKeyPair", "CreateKeyPair"}
        For Each nm In cand
            Dim m = kexType.GetMethod(nm, Reflection.BindingFlags.Public Or Reflection.BindingFlags.Static, Nothing, Type.EmptyTypes, Nothing)
            If m IsNot Nothing Then
                Dim kv = m.Invoke(Nothing, Nothing)
                Dim priv As Byte() = TryGetFieldOrProp(Of Byte())(kv, New String() {"priv", "Priv", "PrivateKey"})
                Dim pub As Byte() = TryGetFieldOrProp(Of Byte())(kv, New String() {"pub", "Pub", "PublicKey"})
                If priv IsNot Nothing AndAlso pub IsNot Nothing Then Return (priv, pub)
            End If
        Next
        Throw New MissingMethodException("No X25519 keypair generator found on KexX25519.")
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

End Class
