Option Strict On
Option Explicit On

Imports System
Imports System.Diagnostics
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

    ' ==== TAGs/Marqueurs crypto PQC P2P ====
    Private Const PQC_KEYEX As String = "[PQCKEY]"    ' [PQCKEY]<b64(pubkey_pqc)>
    Private Const PQC_MSG As String = "[PQCMSG]"      ' [PQCMSG]<b64(encrypted_data)>

    ' ====== État identité (Ed25519 / TOFU) ======
    Private _myEdPk As Byte()
    Private _myEdSk As Byte()
    ' Nonces en attente (challenge) par pair
    Private ReadOnly _idChallenge As New Dictionary(Of String, Byte())(StringComparer.OrdinalIgnoreCase)
    ' Pubkeys TOFU vues/mémorisées (cache)
    Private ReadOnly _peerEdPk As New Dictionary(Of String, Byte())(StringComparer.OrdinalIgnoreCase)

    ' ====== État crypto PQC pour messages P2P ======
    Private _myPqcKeyPair As ChatP2P.Crypto.P2PMessageCrypto.P2PKeyPair
    ' Clés publiques PQC des peers pour messages P2P
    Private ReadOnly _peerPqcPk As New Dictionary(Of String, Byte())(StringComparer.OrdinalIgnoreCase)

    ' ====== Configuration P2P avancée ======
    Private _p2pConfig As P2PAdvancedForm
    
    ' ====== BitTorrent-like File Transfer ======
    Private _transferManager As ChatP2P.Core.P2PFileTransfer.P2PTransferManager = Nothing
    Private _transferCleanupTimer As Threading.Timer = Nothing

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

        Dim shouldLog As Boolean = False
        If Not verbose Then
            shouldLog = True
            If txtLog IsNot Nothing Then
                txtLog.AppendText(msg & Environment.NewLine)
            End If
        Else
            If chkVerbose IsNot Nothing AndAlso chkVerbose.Checked Then
                shouldLog = True
                If txtLog IsNot Nothing Then
                    txtLog.AppendText(msg & Environment.NewLine)
                End If
            End If
        End If

        ' Écriture dans fichier log
        If shouldLog Then
            LogToFile(msg)
        End If
    End Sub

    Private Sub LogToFile(msg As String)
        Try
            Dim logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ChatP2P_Logs")
            If Not Directory.Exists(logDir) Then
                Directory.CreateDirectory(logDir)
            End If
            
            Dim logFile = Path.Combine(logDir, "logfile.txt")
            Dim timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            Dim logEntry = $"[{timestamp}] {msg}{Environment.NewLine}"
            
            File.AppendAllText(logFile, logEntry)
        Catch
            ' Ignore file logging errors to avoid infinite recursion
        End Try
    End Sub

    ''' <summary>
    ''' Envoi d'un message P2P avec chiffrement PQC si les clés sont disponibles.
    ''' Sinon, fallback vers envoi en clair.
    ''' </summary>
    Private Function TrySendP2PMessageEncrypted(peer As String, message As String) As Boolean
        If String.IsNullOrWhiteSpace(peer) OrElse String.IsNullOrEmpty(message) Then Return False
        
        ' Vérifier si P2P est connecté
        If Not (_p2pConn.ContainsKey(peer) AndAlso _p2pConn(peer)) Then Return False
        
        Try
            ' Vérifier si on a la clé publique PQC du peer
            If _peerPqcPk.ContainsKey(peer) AndAlso _myPqcKeyPair IsNot Nothing Then
                ' Envoi chiffré PQC
                Dim peerPubKey = _peerPqcPk(peer)
                Dim encryptedData = ChatP2P.Crypto.P2PMessageCrypto.EncryptMessage(message, peerPubKey)
                Dim encryptedB64 = Convert.ToBase64String(encryptedData)
                Dim pqcMessage = PQC_MSG & encryptedB64
                
                If P2PManager.TrySendText(peer, pqcMessage) Then
                    Log($"[PQC] Message chiffré envoyé à {peer}", verbose:=True)
                    Return True
                End If
            Else
                ' Pas de clé PQC disponible, envoyer l'échange de clé d'abord
                If _myPqcKeyPair IsNot Nothing Then
                    SendPqcKeyExchange(peer)
                End If
                
                ' Fallback: envoi en clair (comportement actuel)
                Log($"[PQC] Pas de clé PQC pour {peer}, envoi en clair", verbose:=True)
                Return P2PManager.TrySendText(peer, message)
            End If
            
        Catch ex As Exception
            Log($"[PQC] Erreur envoi chiffré à {peer}: {ex.Message}")
            ' Fallback: essayer en clair
            Return P2PManager.TrySendText(peer, message)
        End Try
        
        Return False
    End Function

    ''' <summary>
    ''' Envoie notre clé publique PQC au peer pour établir le chiffrement.
    ''' </summary>
    Private Sub SendPqcKeyExchange(peer As String)
        Try
            If _myPqcKeyPair Is Nothing Then Return
            
            Dim pubKeyB64 = Convert.ToBase64String(_myPqcKeyPair.PublicKey)
            Dim keyExMsg = PQC_KEYEX & pubKeyB64
            
            If P2PManager.TrySendText(peer, keyExMsg) Then
                Log($"[PQC] Clé publique envoyée à {peer}", verbose:=True)
            End If
            
        Catch ex As Exception
            Log($"[PQC] Erreur envoi clé à {peer}: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' Traite la réception d'une clé publique PQC d'un peer.
    ''' </summary>
    Private Sub HandlePqcKeyExchange(peer As String, message As String)
        Try
            If Not message.StartsWith(PQC_KEYEX, StringComparison.Ordinal) Then Return
            
            Dim pubKeyB64 = message.Substring(PQC_KEYEX.Length)
            Dim peerPubKey = Convert.FromBase64String(pubKeyB64)
            
            ' Stocker la clé publique du peer
            _peerPqcPk(peer) = peerPubKey
            Log($"[PQC] Clé publique reçue de {peer} ({peerPubKey.Length}b)", verbose:=True)
            
            ' Répondre avec notre clé publique si on ne l'a pas déjà envoyée
            If _myPqcKeyPair IsNot Nothing AndAlso Not _peerPqcPk.ContainsKey(peer) Then
                SendPqcKeyExchange(peer)
            End If
            
        Catch ex As Exception
            Log($"[PQC] Erreur traitement clé de {peer}: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' Traite la réception d'un message PQC chiffré d'un peer.
    ''' </summary>
    Private Sub HandlePqcMessage(peer As String, message As String)
        Try
            If Not message.StartsWith(PQC_MSG, StringComparison.Ordinal) Then Return
            
            Dim encryptedB64 = message.Substring(PQC_MSG.Length)
            Dim encryptedData = Convert.FromBase64String(encryptedB64)
            
            If _myPqcKeyPair Is Nothing Then
                Log($"[PQC] Impossible de déchiffrer message de {peer}: pas de clé privée")
                Return
            End If
            
            ' Déchiffrer le message
            Dim decryptedText = ChatP2P.Crypto.P2PMessageCrypto.DecryptMessage(encryptedData, _myPqcKeyPair.PrivateKey)
            Log($"[PQC] Message déchiffré reçu de {peer}", verbose:=True)
            
            ' Traiter le message déchiffré comme un message normal
            Dim norm As String = Canon(decryptedText)
            If SeenRecently(peer, norm) Then Return

            StoreMsg(peer, outgoing:=False, body:=norm, viaP2P:=True)
            EnsurePrivateChat(peer)
            RefreshPrivateChatStatus(peer)
            AppendToPrivate(peer, peer, "[P2P🔒] " & norm) ' 🔒 indique message chiffré PQC
            
        Catch ex As Exception
            Log($"[PQC] Erreur déchiffrement message de {peer}: {ex.Message}")
        End Try
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
            My.Settings.StrictTrust = chkStrictTrust.Checked.ToString()
            My.Settings.Save()
        Catch ex As Exception
            Log($"[SETTINGS] StrictTrust save error: {ex.Message}", verbose:=True)
        End Try
    End Sub

    Private Sub PersistVerboseToSettingsIfPossible()
        Try
            If chkVerbose Is Nothing Then Return
            My.Settings.Verbose = chkVerbose.Checked.ToString()
            My.Settings.Save()
        Catch ex As Exception
            Log($"[SETTINGS] Verbose save error: {ex.Message}", verbose:=True)
        End Try
    End Sub

    Private Sub PersistEncryptRelayToSettingsIfPossible()
        Try
            If chkEncryptRelay Is Nothing Then Return
            My.Settings.EncryptRelay = chkEncryptRelay.Checked.ToString()
            My.Settings.Save()
        Catch ex As Exception
            Log($"[SETTINGS] EncryptRelay save error: {ex.Message}", verbose:=True)
        End Try
    End Sub

    Private Sub PersistPqRelayToSettingsIfPossible()
        Try
            If chkPqRelay Is Nothing Then Return
            My.Settings.PqRelay = chkPqRelay.Checked.ToString()
            My.Settings.Save()
        Catch ex As Exception
            Log($"[SETTINGS] PqRelay save error: {ex.Message}", verbose:=True)
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

        ' Clés PQC pour messages P2P
        Try
            _myPqcKeyPair = ChatP2P.Crypto.P2PMessageCrypto.GenerateKeyPair()
            Log("[PQC] Clés P2P générées (" & _myPqcKeyPair.Algorithm & ", " & 
                If(_myPqcKeyPair.IsSimulated, "simulé", "natif") & ", pub:" & _myPqcKeyPair.PublicKey.Length & "b)", verbose:=True)
        Catch ex As Exception
            Log("[PQC] Génération clés P2P FAILED: " & ex.Message)
        End Try

        ' Configuration P2P avancée
        Try
            _p2pConfig = New P2PAdvancedForm()
            Log("[P2P] Configuration avancée initialisée", verbose:=True)
        Catch ex As Exception
            Log("[P2P] Init config FAILED: " & ex.Message)
        End Try

        ' BitTorrent-like Transfer Manager
        Try
            _transferManager = New ChatP2P.Core.P2PFileTransfer.P2PTransferManager()
            AddHandler _transferManager.OnTransferProgress, AddressOf OnBitTorrentProgress
            AddHandler _transferManager.OnTransferCompleted, AddressOf OnBitTorrentCompleted
            AddHandler _transferManager.OnLog, AddressOf OnBitTorrentLog
            
            ' Timer de nettoyage toutes les 2 minutes
            _transferCleanupTimer = New Threading.Timer(
                Sub() _transferManager?.CleanupTransfers(),
                Nothing,
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(2)
            )
            
            Log("[BT] Transfer Manager initialisé avec nettoyage automatique", verbose:=True)
        Catch ex As Exception
            Log("[BT] Init Transfer Manager FAILED: " & ex.Message)
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

        ' Checkbox loading moved to Form1_Shown

        ' init P2P au Load
        InitP2PManager()

        _fileWatchdog = New System.Windows.Forms.Timer() With {.Interval = 1500}
        AddHandler _fileWatchdog.Tick, AddressOf FileWatchdog_Tick
        _fileWatchdog.Start()

        AddHandler lstPeers.DoubleClick, AddressOf lstPeers_DoubleClick
    End Sub

    Private Sub Form1_Shown(sender As Object, e As EventArgs) Handles MyBase.Shown
        ' Load checkbox settings after form is fully shown
        LoadCheckboxSettings()
    End Sub

    Private Sub LoadCheckboxSettings()
        ' Add handlers first
        If chkStrictTrust IsNot Nothing Then
            AddHandler chkStrictTrust.CheckedChanged, Sub() PersistStrictToSettingsIfPossible()
        End If
        If chkVerbose IsNot Nothing Then
            AddHandler chkVerbose.CheckedChanged, Sub() PersistVerboseToSettingsIfPossible()
        End If
        If chkEncryptRelay IsNot Nothing Then
            AddHandler chkEncryptRelay.CheckedChanged, Sub() PersistEncryptRelayToSettingsIfPossible()
        End If
        If chkPqRelay IsNot Nothing Then
            AddHandler chkPqRelay.CheckedChanged, Sub() PersistPqRelayToSettingsIfPossible()
        End If

        ' Force update UI with timer
        Dim timer As New System.Windows.Forms.Timer() With {.Interval = 100}
        AddHandler timer.Tick, Sub()
            timer.Stop()
            timer.Dispose()
            
            If chkStrictTrust IsNot Nothing Then
                chkStrictTrust.Checked = (My.Settings.StrictTrust = "True")
                chkStrictTrust.Invalidate()
                chkStrictTrust.Refresh()
            End If

            If chkVerbose IsNot Nothing Then
                chkVerbose.Checked = (My.Settings.Verbose = "True")
                chkVerbose.Invalidate()
                chkVerbose.Refresh()
            End If

            If chkEncryptRelay IsNot Nothing Then
                chkEncryptRelay.Checked = (My.Settings.EncryptRelay = "True")
                chkEncryptRelay.Invalidate()
                chkEncryptRelay.Refresh()
            End If

            If chkPqRelay IsNot Nothing Then
                chkPqRelay.Checked = (My.Settings.PqRelay = "True")
                chkPqRelay.Invalidate()
                chkPqRelay.Refresh()
            End If
        End Sub
        timer.Start()
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

        ' Filtrer les messages BitTorrent AVANT PQC pour éviter le spam dans PrivateChatForm
        If text.StartsWith(ChatP2P.App.MessageProtocol.TAG_BT_META, StringComparison.Ordinal) _
           OrElse text.StartsWith(ChatP2P.App.MessageProtocol.TAG_BT_CHUNK, StringComparison.Ordinal) _
           OrElse text.StartsWith(ChatP2P.App.MessageProtocol.TAG_BT_REQUEST, StringComparison.Ordinal) Then
            Log($"[BT] Message BitTorrent reçu via P2P texte (ignoré pour UI): {text.Substring(0, Math.Min(20, text.Length))}...", verbose:=True)
            Return
        End If

        ' Gestion des messages PQC
        If text.StartsWith(PQC_KEYEX, StringComparison.Ordinal) Then
            HandlePqcKeyExchange(peer, text)
            Return
        End If
        
        If text.StartsWith(PQC_MSG, StringComparison.Ordinal) Then
            HandlePqcMessage(peer, text)
            Return
        End If

        Dim norm As String = Canon(text)
        If SeenRecently(peer, norm) Then Return

        StoreMsg(peer, outgoing:=False, body:=norm, viaP2P:=True)
        EnsurePrivateChat(peer)
        RefreshPrivateChatStatus(peer)
        AppendToPrivate(peer, peer, "[P2P] " & norm)
    End Sub

    ' ---------- P2P binaire (pour fichiers) ----------
    Private Sub OnP2PBinary_FromP2P(peer As String, data As Byte())
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() OnP2PBinary_FromP2P(peer, data))
            Return
        End If
        If IsStrictEnabled() AndAlso Not IsPeerTrusted(peer) Then
            Log($"[SECURITY] Message P2P binaire IN bloqué (pair non de confiance): {peer}")
            Return
        End If
        
        Try
            ' Convertir les données binaires en texte pour les traiter comme des messages relay/hub
            Dim text = Encoding.UTF8.GetString(data)
            
            ' Messages BitTorrent-like (priorité haute)
            If text.StartsWith(ChatP2P.App.MessageProtocol.TAG_BT_META, StringComparison.Ordinal) Then
                HandleBitTorrentMeta(peer, text)
                Return
            ElseIf text.StartsWith(ChatP2P.App.MessageProtocol.TAG_BT_CHUNK, StringComparison.Ordinal) Then
                HandleBitTorrentChunk(peer, text)
                Return
            ElseIf text.StartsWith(ChatP2P.App.MessageProtocol.TAG_BT_REQUEST, StringComparison.Ordinal) Then
                HandleBitTorrentRequest(peer, text)
                Return
            End If
            
            ' Les messages de fichier P2P legacy utilisent le même format que le relay
            If text.StartsWith(Proto.TAG_FILEMETA, StringComparison.Ordinal) _
               OrElse text.StartsWith(Proto.TAG_FILECHUNK, StringComparison.Ordinal) _
               OrElse text.StartsWith(Proto.TAG_FILEEND, StringComparison.Ordinal) Then
                Log($"[P2P] Message fichier legacy reçu de {peer}: {text.Substring(0, Math.Min(50, text.Length))}...")
                OnHubFileSignal(text)
                Return
            End If
            
            ' Pour l'instant, on ignore les autres messages binaires
            Log($"[P2P] Message binaire non reconnu de {peer} ({data.Length} octets)")
        Catch ex As Exception
            Log($"[P2P] Erreur traitement message binaire de {peer}: {ex.Message}")
        End Try
    End Sub

    ' ---------- BitTorrent-like Handlers ----------
    Private Sub HandleBitTorrentMeta(peer As String, message As String)
        Try
            ' Format: BTMETA:transferId:fileName:fileSize:chunkSize:fileHash
            Dim parts = message.Substring(ChatP2P.App.MessageProtocol.TAG_BT_META.Length).Split(":"c, 5)
            If parts.Length <> 5 Then
                Log($"[BT] BTMETA format invalide de {peer}")
                Return
            End If
            
            Dim transferId = parts(0)
            Dim fileName = parts(1)
            Dim fileSize = Long.Parse(parts(2))
            Dim chunkSize = Integer.Parse(parts(3))
            Dim fileHash = parts(4)
            
            Log($"[BT] Métadonnées reçues de {peer}: {fileName} ({fileSize} octets, chunks {chunkSize})")
            
            ' Créer la métadonnée et démarrer la réception
            Dim metadata As New ChatP2P.Core.P2PFileTransfer.FileMetadata(transferId, fileName, fileSize, chunkSize)
            metadata.FileHash = fileHash
            
            ' Définir le chemin de sortie vers ChatP2P_Recv
            Dim downloadDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ChatP2P_Recv")
            If Not System.IO.Directory.Exists(downloadDir) Then
                System.IO.Directory.CreateDirectory(downloadDir)
            End If
            Dim outputPath = System.IO.Path.Combine(downloadDir, fileName)
            
            ' Éviter les écrasements
            Dim counter = 1
            Dim originalPath = outputPath
            While System.IO.File.Exists(outputPath)
                Dim nameOnly = System.IO.Path.GetFileNameWithoutExtension(originalPath)
                Dim extension = System.IO.Path.GetExtension(originalPath)
                outputPath = System.IO.Path.Combine(downloadDir, $"{nameOnly}_{counter}{extension}")
                counter += 1
            End While
            
            ' Démarrer la réception avec le TransferManager
            If _transferManager.StartReceiveTransfer(metadata, outputPath) Then
                Log($"[BT] Transfert {transferId} démarré -> {outputPath}")
                
                ' Notifier la fenêtre de chat
                Dim frm = EnsureAndGetPrivateChatForm(peer)
                If frm IsNot Nothing Then
                    ' Démarrer la progress bar générique
                    frm.StartRecvProgress(fileName, fileSize)
                    frm.AppendMessage(peer, $"[FILE] 📥 Début réception BitTorrent: {fileName} ({fileSize} octets)")
                End If
                
                ' Demander les premiers chunks
                RequestMissingChunks(peer, transferId)
            Else
                Log($"[BT] Échec démarrage transfert {transferId}")
            End If
            
        Catch ex As Exception
            Log($"[BT] Erreur BTMETA de {peer}: {ex.Message}")
        End Try
    End Sub
    
    Private Sub HandleBitTorrentChunk(peer As String, message As String)
        Try
            ' Format: BTCHUNK:transferId:chunkIndex:hash:dataBase64
            Dim parts = message.Substring(ChatP2P.App.MessageProtocol.TAG_BT_CHUNK.Length).Split(":"c, 4)
            If parts.Length <> 4 Then
                Log($"[BT] BTCHUNK format invalide de {peer}")
                Return
            End If
            
            Dim transferId = parts(0)
            Dim chunkIndex = Integer.Parse(parts(1))
            Dim chunkHash = parts(2)
            Dim chunkData = Convert.FromBase64String(parts(3))
            
            Log($"[BT] Chunk {chunkIndex} reçu de {peer} pour {transferId} ({chunkData.Length} octets)", verbose:=True)
            
            ' Traiter le chunk avec le TransferManager
            If _transferManager.ProcessReceivedChunk(transferId, chunkIndex, chunkHash, chunkData) Then
                ' Log seulement tous les 100 chunks pour éviter le spam
                If chunkIndex Mod 100 = 0 Then
                    Log($"[BT] Chunk {chunkIndex} traité avec succès", verbose:=True)
                End If
                
                ' Demander d'autres chunks manquants de façon régulée
                If chunkIndex Mod 10 = 0 Then ' Demander tous les 10 chunks pour éviter la saturation
                    RequestMissingChunks(peer, transferId)
                End If
            Else
                Log($"[BT] Échec traitement chunk {chunkIndex} pour {transferId}")
            End If
            
        Catch ex As Exception
            Log($"[BT] Erreur BTCHUNK de {peer}: {ex.Message}")
        End Try
    End Sub
    
    Private Sub HandleBitTorrentRequest(peer As String, message As String)
        Try
            ' Format: BTREQ:transferId:chunkIndex1,chunkIndex2,chunkIndex3...
            Dim parts = message.Substring(ChatP2P.App.MessageProtocol.TAG_BT_REQUEST.Length).Split(":"c, 2)
            If parts.Length <> 2 Then
                Log($"[BT] BTREQ format invalide de {peer}")
                Return
            End If
            
            Dim transferId = parts(0)
            Dim chunkIndices = parts(1).Split(","c).Select(Function(s) Integer.Parse(s.Trim())).ToList()
            
            Log($"[BT] Demande de {chunkIndices.Count} chunks pour {transferId} de {peer}", verbose:=True)
            
            ' TODO: Implémenter l'envoi des chunks depuis le fichier source 
            ' Pour l'instant, on log juste la demande (nécessite de tracker les transferts d'envoi)
            Log($"[BT] Envoi chunks demandé mais non implémenté pour {transferId} (besoin de tracker fichiers source)")
            
        Catch ex As Exception
            Log($"[BT] Erreur BTREQ de {peer}: {ex.Message}")
        End Try
    End Sub
    
    Private Function ReadFileChunk(filePath As String, chunkIndex As Integer, chunkSize As Integer) As Byte()
        Try
            Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read)
                Dim offset As Long = CLng(chunkIndex) * CLng(chunkSize)
                If offset >= fs.Length Then Return Nothing
                
                fs.Seek(offset, SeekOrigin.Begin)
                Dim bytesToRead = Math.Min(chunkSize, CInt(fs.Length - offset))
                Dim buffer(bytesToRead - 1) As Byte
                fs.Read(buffer, 0, bytesToRead)
                Return buffer
            End Using
        Catch ex As Exception
            Log($"[BT] Erreur lecture chunk {chunkIndex} de {filePath}: {ex.Message}")
            Return Nothing
        End Try
    End Function
    
    Private Sub RequestMissingChunks(peer As String, transferId As String)
        Try
            If _transferManager Is Nothing Then Return
            
            ' Demander jusqu'à 20 chunks manquants à la fois pour éviter la saturation
            Dim missingChunks = _transferManager.GetMissingChunks(transferId, 20)
            If missingChunks.Count = 0 Then Return
            
            ' Créer la demande
            Dim chunkList = String.Join(",", missingChunks)
            Dim requestMsg = $"{ChatP2P.App.MessageProtocol.TAG_BT_REQUEST}{transferId}:{chunkList}{MSG_TERM}"
            Dim requestBytes = Encoding.UTF8.GetBytes(requestMsg)
            
            ' Envoyer via P2P
            If P2PManager.TrySendBinary(peer, requestBytes) Then
                Log($"[BT] Demande {missingChunks.Count} chunks envoyée à {peer}", verbose:=True)
            Else
                Log($"[BT] Échec envoi demande chunks à {peer}")
            End If
            
        Catch ex As Exception
            Log($"[BT] Erreur demande chunks: {ex.Message}")
        End Try
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
                    Dim viaP2P As Boolean = TrySendP2PMessageEncrypted(peer, norm)

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
                ' Déléguer à la méthode centralisée qui gère P2P + Relay (async)
                Threading.Tasks.Task.Run(Async Sub() Await SendFileToPeer(peer))
            End Sub

            ' ANCIEN CODE (maintenant remplacé par SendFileToPeer):
            Dim oldSendFileCb As Action =
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


    ' ---------- Transfert P2P BitTorrent-like pour contourner crash 400 chunks ----------
    Private Async Function SendFileP2POptimized(dest As String, transferId As String, fi As FileInfo, frmPeer As PrivateChatForm) As Threading.Tasks.Task(Of Boolean)
        ' Déterminer quelle méthode utiliser selon la taille
        If fi.Length >= 2 * 1024 * 1024 Then  ' 2MB et plus
            Log($"[P2P] Fichier volumineux détecté ({fi.Length} bytes), utilisation BitTorrent-like")
            Return Await SendFileP2PBitTorrentLike(dest, transferId, fi, frmPeer)
        Else
            Log($"[P2P] Fichier petit ({fi.Length} bytes), utilisation méthode optimisée legacy")
            Return Await SendFileP2POptimizedLegacy(dest, transferId, fi, frmPeer)
        End If
    End Function

    ' ---------- Ancien système pour petits fichiers ----------
    Private Async Function SendFileP2POptimizedLegacy(dest As String, transferId As String, fi As FileInfo, frmPeer As PrivateChatForm) As Threading.Tasks.Task(Of Boolean)
        Dim totalSent As Long = 0
        Dim chunkCount As Long = 0
        
        Try
            ' === Configuration WebRTC depuis le panel avancé ===
            Dim WEBRTC_CHUNK_SIZE As Integer = If(_p2pConfig IsNot Nothing, _p2pConfig.ChunkSize, 8192)
            Dim MAX_CONCURRENT_CHUNKS As Integer = If(_p2pConfig IsNot Nothing, _p2pConfig.MaxConcurrentChunks, 1)
            Dim CHUNK_DELAY_MS As Integer = If(_p2pConfig IsNot Nothing, _p2pConfig.ChunkDelayMs, 20)  ' Réduit de 100ms à 20ms
            Dim ENABLE_DEBUG_LOGS As Boolean = If(_p2pConfig IsNot Nothing, _p2pConfig.EnableDebugLogs, True)
            Const PROGRESS_UPDATE_INTERVAL As Integer = 1048576  ' Update UI tous les 1MB
            
            ' Log de la configuration utilisée
            If ENABLE_DEBUG_LOGS Then
                Log($"[P2P CONFIG] Chunk: {WEBRTC_CHUNK_SIZE}B, Concurrent: {MAX_CONCURRENT_CHUNKS}, Délai: {CHUNK_DELAY_MS}ms")
            End If
            
            ' Envoi metadata
            Dim meta = $"{Proto.TAG_FILEMETA}{transferId}:{_displayName}:{dest}:{fi.Name}:{fi.Length}{MSG_TERM}"
            Dim metaBytes = Encoding.UTF8.GetBytes(meta)
            
            If Not P2PManager.TrySendBinary(dest, metaBytes) Then
                Log($"[P2P] Échec envoi metadata vers {dest}")
                Return False
            End If
            
            ' === Transfert par chunks avec flow control ===
            Using fs = fi.OpenRead()
                Dim buffer(WEBRTC_CHUNK_SIZE - 1) As Byte
                Dim lastProgressUpdate As Long = 0
                Dim concurrentTasks As New List(Of Threading.Tasks.Task(Of Boolean))
                
                While totalSent < fi.Length
                    Dim read = fs.Read(buffer, 0, buffer.Length)
                    If read <= 0 Then Exit While
                    
                    ' Créer une copie du buffer pour ce chunk (important pour async)
                    Dim chunkData(read - 1) As Byte
                    Array.Copy(buffer, chunkData, read)
                    
                    ' Envoyer de manière asynchrone avec limitation de concurrence
                    Dim chunkTask = SendChunkAsync(dest, transferId, chunkData, chunkCount)
                    concurrentTasks.Add(chunkTask)
                    
                    ' Gestion flow control : attendre si trop de chunks en cours
                    If concurrentTasks.Count >= MAX_CONCURRENT_CHUNKS Then
                        Dim completedTask = Await Threading.Tasks.Task.WhenAny(concurrentTasks)
                        Dim success = Await completedTask
                        concurrentTasks.Remove(completedTask)
                        
                        If Not success Then
                            Log($"[P2P] Échec envoi chunk {chunkCount} vers {dest}")
                            Return False
                        End If
                    End If
                    
                    totalSent += read
                    chunkCount += 1
                    
                    ' Update progress moins fréquemment pour éviter UI spam
                    If totalSent - lastProgressUpdate >= PROGRESS_UPDATE_INTERVAL OrElse totalSent = fi.Length Then
                        If frmPeer IsNot Nothing Then
                            Await Threading.Tasks.Task.Run(Sub() frmPeer.UpdateSendProgress(totalSent, fi.Length))
                        End If
                        lastProgressUpdate = totalSent
                        
                        ' Log progress périodique au lieu de chaque chunk
                        Dim progress = (totalSent * 100) \ fi.Length
                        Log($"[P2P] Progression: {progress}% ({totalSent}/{fi.Length} octets)")
                    End If
                    
                    ' Délai SYSTÉMATIQUE pour éviter saturation WebRTC DataChannel
                    Await Threading.Tasks.Task.Delay(CHUNK_DELAY_MS)
                    
                    ' Debug verbeux tous les 50 chunks pour voir où ça plante
                    If ENABLE_DEBUG_LOGS AndAlso chunkCount Mod 50 = 0 Then
                        Log($"[P2P DEBUG] Chunk #{chunkCount}/{Math.Ceiling(fi.Length / WEBRTC_CHUNK_SIZE)}, concurrent tasks: {concurrentTasks.Count}")
                    End If
                End While
                
                ' Attendre que tous les chunks restants soient envoyés
                Dim remainingResults = Await Threading.Tasks.Task.WhenAll(concurrentTasks)
                If remainingResults.Any(Function(r) Not r) Then
                    Log($"[P2P] Certains chunks n'ont pas pu être envoyés")
                    Return False
                End If
            End Using
            
            ' *** DÉLAI CRITIQUE pour synchronisation WebRTC ***
            ' Attendre que le receiver traite les chunks en attente avant FILEEND
            Log($"[P2P] Attente synchronisation receiver avant FILEEND...")
            Await Threading.Tasks.Task.Delay(2000)  ' 2 secondes pour que receiver rattrape
            
            ' Message de fin
            Dim endMsg = $"{Proto.TAG_FILEEND}{transferId}{MSG_TERM}"
            Dim endBytes = Encoding.UTF8.GetBytes(endMsg)
            
            ' Retry FILEEND jusqu'à 3 fois pour s'assurer qu'il arrive
            Dim fileEndSent = False
            For attempt = 1 To 3
                If P2PManager.TrySendBinary(dest, endBytes) Then
                    fileEndSent = True
                    Exit For
                End If
                Log($"[P2P] FILEEND échec tentative {attempt}, retry dans 500ms...")
                Await Threading.Tasks.Task.Delay(500)
            Next
            
            If Not fileEndSent Then
                Log($"[P2P] ÉCHEC CRITIQUE: FILEEND n'a pas pu être envoyé après 3 tentatives")
                Return False
            End If
            
            Log($"[P2P] Transfert terminé: {chunkCount} chunks envoyés ({totalSent} octets)")
            Return True
            
        Catch ex As Exception
            Log($"[P2P] Erreur transfert optimisé: {ex.Message}")
            Return False
        End Try
    End Function

    ' ---------- Nouveau système BitTorrent-like pour gros fichiers ----------
    Private Async Function SendFileP2PBitTorrentLike(dest As String, transferId As String, fi As FileInfo, frmPeer As PrivateChatForm) As Threading.Tasks.Task(Of Boolean)
        Try
            ' === Configuration BitTorrent depuis panneau avancé ===
            Dim CHUNK_SIZE As Integer = If(_p2pConfig IsNot Nothing, _p2pConfig.ChunkSize, 8192)
            Dim BATCH_SIZE As Integer = If(_p2pConfig IsNot Nothing, _p2pConfig.BatchSize, 200)  
            Dim BATCH_DELAY_MS As Integer = If(_p2pConfig IsNot Nothing, _p2pConfig.BatchDelayMs, 10)
            Dim ENABLE_DEBUG_LOGS As Boolean = If(_p2pConfig IsNot Nothing, _p2pConfig.EnableDebugLogs, True)
            
            ' === Configuration limitation bande passante ===
            Dim ENABLE_BANDWIDTH_LIMIT As Boolean = If(_p2pConfig IsNot Nothing, _p2pConfig.EnableBandwidthLimit, False)
            Dim MAX_SPEED_KBPS As Integer = If(_p2pConfig IsNot Nothing, _p2pConfig.MaxSpeedKBps, 1000)
            Dim bandwidthTimer As Stopwatch = Stopwatch.StartNew()
            Dim bytesSentInInterval As Long = 0
            
            If ENABLE_DEBUG_LOGS Then
                Log($"[P2P TORRENT] Transfert gros fichier: {fi.Name} ({fi.Length} bytes)")
                Log($"[P2P TORRENT] Config: Chunk={CHUNK_SIZE}B, Batch={BATCH_SIZE}, Délai={BATCH_DELAY_MS}ms")
            End If
            
            ' Créer les métadonnées du fichier avec hash complet
            Dim totalChunks = CInt(Math.Ceiling(fi.Length / CDbl(CHUNK_SIZE)))
            Dim metadata As New ChatP2P.Core.P2PFileTransfer.FileMetadata(transferId, fi.Name, fi.Length, CHUNK_SIZE)
            
            ' Calculer le hash SHA256 du fichier complet sur un thread séparé pour éviter l'UI freeze
            Dim fileHash As String = Await Task.Run(Function()
                Using fs = fi.OpenRead()
                    Using sha = System.Security.Cryptography.SHA256.Create()
                        Dim hashBytes = sha.ComputeHash(fs)
                        Return Convert.ToBase64String(hashBytes)
                    End Using
                End Using
            End Function)
            
            metadata.FileHash = fileHash
            
            If ENABLE_DEBUG_LOGS Then
                Log($"[P2P TORRENT] Hash fichier calculé: {fileHash.Substring(0, Math.Min(16, fileHash.Length))}...")
            End If
            
            ' Envoyer les métadonnées BitTorrent-like
            Dim metaMsg = $"{ChatP2P.App.MessageProtocol.TAG_BT_META}{transferId}:{fi.Name}:{fi.Length}:{CHUNK_SIZE}:{fileHash}{MSG_TERM}"
            Dim metaBytes = Encoding.UTF8.GetBytes(metaMsg)
            
            If Not P2PManager.TrySendBinary(dest, metaBytes) Then
                Log($"[P2P TORRENT] Échec envoi metadata vers {dest}")
                Return False
            End If
            
            ' Attente setup configurable depuis panneau avancé
            Dim SETUP_DELAY_MS As Integer = If(_p2pConfig IsNot Nothing, _p2pConfig.SetupDelayMs, 100)
            If SETUP_DELAY_MS > 0 Then
                Await Threading.Tasks.Task.Delay(SETUP_DELAY_MS)
            End If
            
            ' === Envoi par batches pour éviter le crash 400 chunks ===
            Dim sentChunks = 0
            Dim totalSent As Long = 0
            Using fs = fi.OpenRead()
                Dim buffer = New Byte(CHUNK_SIZE - 1) {}
                
                For batchStart = 0 To totalChunks - 1 Step BATCH_SIZE
                    Dim batchEnd = Math.Min(batchStart + BATCH_SIZE - 1, totalChunks - 1)
                    
                    If ENABLE_DEBUG_LOGS Then
                        Log($"[P2P TORRENT] Envoi batch {batchStart}-{batchEnd} ({batchEnd - batchStart + 1} chunks)")
                    End If
                    
                    ' Envoyer les chunks de ce batch
                    For chunkIndex = batchStart To batchEnd
                        Dim position = chunkIndex * CHUNK_SIZE
                        fs.Seek(position, SeekOrigin.Begin)
                        
                        Dim bytesRead = fs.Read(buffer, 0, buffer.Length)
                        If bytesRead = 0 Then Exit For
                        
                        ' Créer le chunk avec hash
                        Dim chunkData = New Byte(bytesRead - 1) {}
                        Array.Copy(buffer, chunkData, bytesRead)
                        
                        Dim chunk As New ChatP2P.Core.P2PFileTransfer.FileChunk(chunkIndex, chunkData)
                        
                        ' Envoyer le chunk avec hash BitTorrent-like
                        Dim chunkMsg = $"{ChatP2P.App.MessageProtocol.TAG_BT_CHUNK}{transferId}:{chunkIndex}:{chunk.Hash}:{Convert.ToBase64String(chunkData)}{MSG_TERM}"
                        Dim chunkBytes = Encoding.UTF8.GetBytes(chunkMsg)
                        
                        If P2PManager.TrySendBinary(dest, chunkBytes) Then
                            sentChunks += 1
                            totalSent += bytesRead
                            bytesSentInInterval += bytesRead
                            
                            ' === Limitation de bande passante ===
                            If ENABLE_BANDWIDTH_LIMIT Then
                                Dim elapsedMs = bandwidthTimer.ElapsedMilliseconds
                                If elapsedMs >= 1000 Then ' Contrôle toutes les secondes
                                    Dim currentSpeedKBps = (bytesSentInInterval / 1024) / (elapsedMs / 1000.0)
                                    If currentSpeedKBps > MAX_SPEED_KBPS Then
                                        Dim delayMs = CInt((bytesSentInInterval / 1024 / MAX_SPEED_KBPS * 1000) - elapsedMs)
                                        If delayMs > 0 Then
                                            If ENABLE_DEBUG_LOGS Then
                                                Log($"[BANDWIDTH] Débit trop élevé ({currentSpeedKBps:F0} KB/s), pause {delayMs}ms")
                                            End If
                                            Await Threading.Tasks.Task.Delay(delayMs)
                                        End If
                                    End If
                                    ' Reset compteurs
                                    bandwidthTimer.Restart()
                                    bytesSentInInterval = 0
                                End If
                            End If
                            
                            ' Petit délai entre chunks dans le batch
                            Await Threading.Tasks.Task.Delay(10)
                        Else
                            Log($"[P2P TORRENT] Échec envoi chunk {chunkIndex}")
                        End If
                        
                        ' Mise à jour progression
                        If sentChunks Mod 10 = 0 Then
                            frmPeer?.UpdateSendProgress(totalSent, fi.Length)
                        End If
                    Next
                    
                    ' DÉLAI CRITIQUE entre batches pour éviter saturation WebRTC
                    If batchEnd < totalChunks - 1 Then
                        If ENABLE_DEBUG_LOGS Then
                            Log($"[P2P TORRENT] Attente {BATCH_DELAY_MS}ms avant batch suivant...")
                        End If
                        Await Threading.Tasks.Task.Delay(BATCH_DELAY_MS)
                    End If
                Next
            End Using
            
            ' BitTorrent-like: pas de FILEEND - complétion automatique par le receiver
            Log($"[P2P TORRENT] Tous les chunks envoyés: {sentChunks}/{totalChunks}")
            frmPeer?.UpdateSendProgress(fi.Length, fi.Length)
            
            Return True
            
        Catch ex As Exception
            Log($"[P2P TORRENT] Erreur transfert BitTorrent-like: {ex.Message}")
            Return False
        End Try
    End Function
    
    ' Envoi asynchrone d'un chunk individuel avec retry
    Private Async Function SendChunkAsync(dest As String, transferId As String, chunkData As Byte(), chunkIndex As Long) As Threading.Tasks.Task(Of Boolean)
        Try
            Const MAX_RETRIES As Integer = 3
            
            For attempt = 1 To MAX_RETRIES
                ' Format: TAG + transferId + ":" + base64(données) + MSG_TERM
                Dim chunkDataB64 = Convert.ToBase64String(chunkData)
                Dim chunkMsg = $"{Proto.TAG_FILECHUNK}{transferId}:{chunkDataB64}{MSG_TERM}"
                Dim chunkBytes = Encoding.UTF8.GetBytes(chunkMsg)
                
                If P2PManager.TrySendBinary(dest, chunkBytes) Then
                    Return True
                End If
                
                ' Retry avec backoff exponentiel
                If attempt < MAX_RETRIES Then
                    Await Threading.Tasks.Task.Delay(attempt * 50)
                End If
            Next
            
            Return False
            
        Catch ex As Exception
            Log($"[P2P] Erreur envoi chunk {chunkIndex}: {ex.Message}")
            Return False
        End Try
    End Function

    ' ---------- Envoi fichier centralisé (ouvre la PrivateChatForm + progress + notif) ----------
    Private Async Function SendFileToPeer(dest As String) As Threading.Tasks.Task
        Try
            If String.IsNullOrWhiteSpace(dest) Then
                Log("Destination invalide pour l'envoi de fichier.")
                Return
            End If

            If IsStrictEnabled() AndAlso Not IsPeerTrusted(dest) Then
                Log($"[SECURITY] Envoi fichier bloqué (pair non de confiance): {dest}")
                Return
            End If

            ' Vérifier la disponibilité P2P en premier - RETOUR AU P2P !
            Dim useP2P As Boolean = _p2pConn.ContainsKey(dest) AndAlso _p2pConn(dest)
            
            ' Debug logs  
            Dim managerSays As Boolean = P2PManager.IsP2PConnected(dest)
            Log($"[DEBUG] P2P check pour {dest}: Manager={managerSays}, Local={useP2P}")
            
            If useP2P Then
                Log($"[P2P] Envoi fichier via P2P direct vers {dest}")
            Else
                Log($"[RELAY] Envoi fichier via relay vers {dest} (P2P non disponible)")
            End If

            Dim useEnc As Boolean = IsEncryptRelayEnabled() AndAlso Not useP2P  ' Pas de crypto sur P2P pour l'instant
            If useEnc AndAlso Not HasRelayKey(dest) Then
                EnsureRelayHandshake(dest)
                Log("[ENC] Clé non établie avec " & dest & ". Relance l'envoi une fois la clé OK.")
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
                    
                    If useP2P Then
                        ' ===== ENVOI VIA P2P OPTIMISÉ =====
                        Log($"[P2P] Début transfert optimisé : {fi.Name} ({fi.Length} octets)")
                        
                        ' Démarrer le transfert asynchrone optimisé pour WebRTC
                        Dim success = Await SendFileP2POptimized(dest, transferId, fi, frmPeer)
                        
                        If success Then
                            Log($"[P2P] Fichier {fi.Name} envoyé avec succès à {dest}")
                        Else
                            Log($"[P2P] Échec transfert {fi.Name} vers {dest}")
                        End If
                    Else
                        ' ===== ENVOI VIA RELAY OPTIMISÉ =====
                        Dim success = Await SendFileRelayOptimized(dest, transferId, fi, frmPeer)
                        If Not success Then
                            Log($"[RELAY] Échec transfert optimisé {fi.Name} vers {dest}")
                        End If
                    End If

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
    End Function

    ' ---------- Transfert RELAY ORIGINAL (FAST) - Restauré depuis ANCIEN CODE ----------
    Private Async Function SendFileRelayOptimized(dest As String, transferId As String, fi As FileInfo, frmPeer As PrivateChatForm) As Threading.Tasks.Task(Of Boolean)
        Try
            ' Déterminer si le cryptage relay est activé
            Dim useEnc As Boolean = IsEncryptRelayEnabled()
            Dim fnameToSend = If(useEnc, ENC_FILE_MARK & fi.Name, fi.Name)
            
            ' Envoi metadata - Format original fast relay
            Dim meta = $"{Proto.TAG_FILEMETA}{transferId}:{_displayName}:{dest}:{fnameToSend}:{fi.Length}{MSG_TERM}"
            Dim metaBytes = Encoding.UTF8.GetBytes(meta)

            If _isHost Then
                If _hub Is Nothing Then
                    Log("Hub non initialisé.")
                    Return False
                End If
                Await _hub.SendToAsync(dest, metaBytes)
            Else
                If _stream Is Nothing Then
                    Log("Not connected.")
                    Return False
                End If
                Await _stream.SendAsync(metaBytes, Threading.CancellationToken.None)
            End If

            ' Transfert par chunks à VITESSE MAXIMALE (pas de délais!)
            Using fs = fi.OpenRead()
                Dim buffer(32768 - 1) As Byte  ' 32KB buffer comme l'original
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
                    Dim chunkBytes = Encoding.UTF8.GetBytes(chunkMsg)

                    If _isHost Then
                        Await _hub.SendToAsync(dest, chunkBytes)
                    Else
                        Await _stream.SendAsync(chunkBytes, Threading.CancellationToken.None)
                    End If

                    totalSent += read
                    If frmPeer IsNot Nothing Then frmPeer.UpdateSendProgress(totalSent, fi.Length)
                    
                    ' AUCUN DÉLAI - C'EST ÇA LE SECRET DE LA VITESSE!
                End While
            End Using

            ' Message de fin
            Dim endMsg = $"{Proto.TAG_FILEEND}{transferId}{MSG_TERM}"
            Dim endBytes = Encoding.UTF8.GetBytes(endMsg)
            If _isHost Then
                Await _hub.SendToAsync(dest, endBytes)
            Else
                Await _stream.SendAsync(endBytes, Threading.CancellationToken.None)
            End If

            Log($"[RELAY{If(useEnc, "+ENC", "")}] Fichier {fi.Name} envoyé à {dest} (FAST RELAY RESTORED)")
            Return True
            
        Catch ex As Exception
            Log($"[RELAY] Erreur transfert fast: {ex.Message}")
            Return False
        End Try
    End Function


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
            AddHandler P2PManager.OnP2PBinary, AddressOf OnP2PBinary_FromP2P
            AddHandler P2PManager.OnP2PState,
                Sub(peer As String, connected As Boolean)
                    Log($"[DEBUG] OnP2PState event: {peer} connected={connected}")
                    _p2pConn(peer) = connected
                    ' DTLS = crypto ON côté P2P
                    _cryptoActive(peer) = connected
                    Log($"[DEBUG] P2P state updated in _p2pConn for {peer}: {connected}")
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

    ' ---------- BitTorrent-like Events ----------
    Private Sub OnBitTorrentProgress(transferId As String, progress As Double, receivedBytes As Long, totalBytes As Long)
        Try
            If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
                Me.BeginInvoke(Sub() OnBitTorrentProgress(transferId, progress, receivedBytes, totalBytes))
                Return
            End If
            
            ' Log progress seulement tous les 5% pour éviter le spam
            Static lastLoggedProgress As New Dictionary(Of String, Integer)
            Dim currentProgressInt = CInt(progress / 5) * 5 ' Arrondir aux 5% près
            If Not lastLoggedProgress.ContainsKey(transferId) OrElse lastLoggedProgress(transferId) <> currentProgressInt Then
                lastLoggedProgress(transferId) = currentProgressInt
                Log($"[BT] Transfert {transferId}: {progress:F1}% ({receivedBytes}/{totalBytes} octets)", verbose:=True)
            End If
            
            ' Mettre à jour la progress bar de la fenêtre de chat concernée
            For Each kvp In _privateChats
                Dim frm = kvp.Value
                If frm IsNot Nothing AndAlso Not frm.IsDisposed Then
                    ' Pour l'instant, on met à jour toutes les fenêtres actives de réception
                    frm.UpdateRecvProgress(receivedBytes, totalBytes)
                End If
            Next
        Catch ex As Exception
            Log($"[BT] Erreur progress: {ex.Message}")
        End Try
    End Sub
    
    Private Sub OnBitTorrentCompleted(transferId As String, success As Boolean, outputPath As String)
        Try
            If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
                Me.BeginInvoke(Sub() OnBitTorrentCompleted(transferId, success, outputPath))
                Return
            End If
            
            Log($"[BT] Transfert {transferId} terminé: {If(success, "SUCCÈS", "ÉCHEC")} -> {outputPath}")
            
            ' Trouver et notifier la fenêtre de chat concernée
            For Each kvp In _privateChats
                Dim frm = kvp.Value
                If frm IsNot Nothing AndAlso Not frm.IsDisposed Then
                    ' Arrêter toutes les progress bars de réception
                    frm.EndRecvProgress()
                    If success Then
                        Dim fileName = System.IO.Path.GetFileName(outputPath)
                        frm.AppendMessage("System", $"[FILE] ✅ Fichier BitTorrent reçu: {fileName}")
                    Else
                        frm.AppendMessage("System", $"[FILE] ❌ Échec réception fichier BitTorrent")
                    End If
                End If
            Next
        Catch ex As Exception
            Log($"[BT] Erreur completion: {ex.Message}")
        End Try
    End Sub
    
    Private Sub OnBitTorrentLog(message As String)
        Log(message, verbose:=True)
    End Sub

    ' ---------- Panel P2P Avancé ----------
    Private Sub btnP2PAdvanced_Click(sender As Object, e As EventArgs) Handles btnP2PAdvanced.Click
        Try
            If _p2pConfig Is Nothing Then
                _p2pConfig = New P2PAdvancedForm()
            End If
            
            ' Afficher la configuration actuelle dans le titre du bouton
            btnP2PAdvanced.Text = "P2P Config*"
            
            ' Ouvrir le dialog
            If _p2pConfig.ShowDialog(Me) = DialogResult.OK Then
                ' Sauvegarder les nouvelles valeurs
                _p2pConfig.SaveValues()
                
                ' Afficher un résumé dans les logs
                Dim summary = _p2pConfig.GetConfigSummary()
                Log($"[P2P] Configuration mise à jour: {summary}")
                
                ' Mettre à jour le titre du bouton pour indiquer la config personnalisée
                btnP2PAdvanced.Text = If(_p2pConfig.ChunkSize = 8192 AndAlso 
                                        _p2pConfig.MaxConcurrentChunks = 1 AndAlso 
                                        _p2pConfig.ChunkDelayMs = 100,
                                        "P2P Config", "P2P Config*")
            End If
            
        Catch ex As Exception
            Log($"[P2P] Erreur panel avancé: {ex.Message}")
            MessageBox.Show($"Erreur lors de l'ouverture du panel P2P: {ex.Message}", 
                           "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

End Class
