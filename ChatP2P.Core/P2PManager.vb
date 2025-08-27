Option Strict On
Imports System
Imports System.Collections.Generic
Imports System.Text
Imports System.Threading.Tasks
Imports SIPSorcery.Net

Namespace ChatP2P.Core

    Public Module P2PManager

        ' ==== Tags de signalisation ====
        Public Const TAG_ICE_OFFER As String = "ICE_OFFER:"
        Public Const TAG_ICE_ANSWER As String = "ICE_ANSWER:"
        Public Const TAG_ICE_CAND As String = "ICE_CAND:"

        ' ==== Events exposés à l’UI ====
        Public Event OnLog(peer As String, line As String)
        Public Event OnP2PState(peer As String, connected As Boolean)
        Public Event OnP2PText(peer As String, text As String)

        ' ==== Signaling et état ====
        Private _sendSignal As Func(Of String, String, Task) = Nothing
        Private _localName As String = "Me"

        Private ReadOnly _gate As New Object()
        Private ReadOnly _sessions As New Dictionary(Of String, IceP2PSession)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _startingPeers As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        ' anti-doublons de candidates
        Private ReadOnly _seenCandOut As New Dictionary(Of String, HashSet(Of String))(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _seenCandIn As New Dictionary(Of String, HashSet(Of String))(StringComparer.OrdinalIgnoreCase)

        ' candidates reçues avant la SDP / session
        Private ReadOnly _pendingCands As New Dictionary(Of String, List(Of String))(StringComparer.OrdinalIgnoreCase)

        ' ==== API publique ====
        Public Sub Init(sendSignal As Func(Of String, String, Task), localDisplayName As String)
            If sendSignal Is Nothing Then Throw New ArgumentNullException(NameOf(sendSignal))
            _sendSignal = sendSignal
            If Not String.IsNullOrWhiteSpace(localDisplayName) Then _localName = localDisplayName
        End Sub

        Public Sub StartP2P(peer As String, stunUrls As IEnumerable(Of String))
            If String.IsNullOrWhiteSpace(peer) Then Exit Sub
            If stunUrls Is Nothing Then stunUrls = New String() {"stun:stun.l.google.com:19302"}

            SyncLock _gate
                If _startingPeers.Contains(peer) Then
                    RaiseEvent OnLog(peer, "Négociation déjà en cours.")
                    Exit Sub
                End If
                _startingPeers.Add(peer)
            End SyncLock

            Try
                Dim existing As IceP2PSession = Nothing
                Dim exists As Boolean
                SyncLock _gate
                    exists = _sessions.TryGetValue(peer, existing)
                End SyncLock

                If exists AndAlso existing IsNot Nothing AndAlso existing.IsOpen Then
                    RaiseEvent OnLog(peer, "Session déjà connectée.")
                    SyncLock _gate : _startingPeers.Remove(peer) : End SyncLock
                    Exit Sub
                End If

                ' Reset dedup OUT/IN pour un nouveau cycle de nego
                ResetDedup(peer)

                Dim sess As New IceP2PSession(stunUrls, "dc", isCaller:=True)
                WireSessionHandlers(peer, sess)
                SyncLock _gate
                    _sessions(peer) = sess
                End SyncLock

                RaiseEvent OnLog(peer, "Négociation démarrée vers " & peer)
                sess.Start()

            Catch ex As Exception
                RaiseEvent OnLog(peer, "createOffer error: " & ex.Message)
                SyncLock _gate
                    _sessions.Remove(peer)
                    _startingPeers.Remove(peer)
                End SyncLock
            End Try
        End Sub

        ''' <summary>Envoi rapide d’un message texte via datachannel P2P si ouvert.</summary>
        Public Function TrySendText(peer As String, text As String) As Boolean
            If String.IsNullOrWhiteSpace(peer) OrElse String.IsNullOrEmpty(text) Then Return False
            Dim s As IceP2PSession = Nothing
            SyncLock _gate
                _sessions.TryGetValue(peer, s)
            End SyncLock
            If s Is Nothing OrElse Not s.IsOpen Then Return False
            Try
                s.SendText(text)
                Return True
            Catch ex As Exception
                RaiseEvent OnLog(peer, "SendText error: " & ex.Message)
                Return False
            End Try
        End Function

        Public Sub HandleOffer(fromPeer As String, sdp As String, stunUrls As IEnumerable(Of String))
            If String.IsNullOrWhiteSpace(fromPeer) OrElse String.IsNullOrWhiteSpace(sdp) Then Exit Sub
            If stunUrls Is Nothing Then stunUrls = New String() {"stun:stun.l.google.com:19302"}

            Dim sess As IceP2PSession = Nothing
            Dim exists As Boolean
            SyncLock _gate
                exists = _sessions.TryGetValue(fromPeer, sess)
            End SyncLock

            If Not exists OrElse sess Is Nothing Then
                sess = New IceP2PSession(stunUrls, "dc", isCaller:=False)
                WireSessionHandlers(fromPeer, sess)
                SyncLock _gate
                    _sessions(fromPeer) = sess
                End SyncLock
            End If

            Try
                ' ⚠️ IMPORTANT: démarrer la session côté callee
                ' On le fait avant (ou juste après) l'application de l'offer.
                ' Si ta classe supporte les 2, ceci est suffisant et idempotent.
                sess.Start()
            Catch ex As Exception
                RaiseEvent OnLog(fromPeer, "Start (callee) error: " & ex.Message)
            End Try

            ' Applique l’offer distante : la classe créera l’answer + OnLocalSdp("answer", …)
            sess.SetRemoteDescription("offer", sdp)

            ' --- enregistrer fingerprint DTLS ---
            Try
                Dim fp = ExtractDtlsFingerprintFromSdp(sdp)
                If Not String.IsNullOrWhiteSpace(fp) Then ChatP2P.Core.LocalDb.SetPeerDtlsFp(fromPeer, fp)
            Catch
            End Try

            ' Déroule les candidates reçues avant la session
            FlushPendingCandidates(fromPeer)

            SyncLock _gate : _startingPeers.Remove(fromPeer) : End SyncLock
        End Sub


        Public Sub HandleAnswer(fromPeer As String, sdp As String)
            If String.IsNullOrWhiteSpace(fromPeer) OrElse String.IsNullOrWhiteSpace(sdp) Then Exit Sub

            Dim sess As IceP2PSession = Nothing
            Dim exists As Boolean
            SyncLock _gate
                exists = _sessions.TryGetValue(fromPeer, sess)
            End SyncLock
            If Not exists OrElse sess Is Nothing Then
                RaiseEvent OnLog(fromPeer, "Answer reçue mais session introuvable.")
                Exit Sub
            End If

            sess.SetRemoteDescription("answer", sdp)
            ' --- enregistrer fingerprint DTLS ---
            Try
                Dim fp = ExtractDtlsFingerprintFromSdp(sdp)
                If Not String.IsNullOrWhiteSpace(fp) Then ChatP2P.Core.LocalDb.SetPeerDtlsFp(fromPeer, fp)
            Catch
            End Try

            FlushPendingCandidates(fromPeer)
            SyncLock _gate : _startingPeers.Remove(fromPeer) : End SyncLock
        End Sub

        Public Sub HandleCandidate(fromPeer As String, candidate As String)
            If String.IsNullOrWhiteSpace(fromPeer) OrElse String.IsNullOrWhiteSpace(candidate) Then Exit Sub

            ' dedup IN
            Dim canon = CanonCandidate(candidate)
            SyncLock _gate
                Dim setIn As HashSet(Of String) = Nothing
                If Not _seenCandIn.TryGetValue(fromPeer, setIn) OrElse setIn Is Nothing Then
                    setIn = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                    _seenCandIn(fromPeer) = setIn
                End If
                If setIn.Contains(canon) Then
                    RaiseEvent OnLog(fromPeer, "Candidate ignorée (dupli IN).")
                    Exit Sub
                End If
                setIn.Add(canon)
                If setIn.Count > 128 Then setIn.Remove(setIn.First())
            End SyncLock

            Dim sess As IceP2PSession = Nothing
            Dim exists As Boolean
            SyncLock _gate
                exists = _sessions.TryGetValue(fromPeer, sess)
            End SyncLock

            If Not exists OrElse sess Is Nothing Then
                ' queue en attendant la session
                SyncLock _gate
                    Dim list As List(Of String) = Nothing
                    If Not _pendingCands.TryGetValue(fromPeer, list) OrElse list Is Nothing Then
                        list = New List(Of String)()
                        _pendingCands(fromPeer) = list
                    End If
                    list.Add(candidate)
                End SyncLock
                Exit Sub
            End If

            Try
                sess.AddRemoteCandidate(candidate)
            Catch ex As Exception
                RaiseEvent OnLog(fromPeer, "AddRemoteCandidate error: " & ex.Message)
            End Try
        End Sub

        ' ======== handlers internes ========
        Private Sub WireSessionHandlers(peer As String, sess As IceP2PSession)
            ' 1) SDP locale (offer/answer) => signal vers l’autre
            AddHandler sess.OnLocalSdp,
                Sub(kind As String, localSdp As String)
                    Try
                        Dim b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(localSdp))
                        If String.Equals(kind, "answer", StringComparison.OrdinalIgnoreCase) Then
                            If _sendSignal IsNot Nothing Then _sendSignal(peer, $"{TAG_ICE_ANSWER}{_localName}:{peer}:{b64}")
                        Else
                            If _sendSignal IsNot Nothing Then _sendSignal(peer, $"{TAG_ICE_OFFER}{_localName}:{peer}:{b64}")
                        End If
                    Catch ex As Exception
                        RaiseEvent OnLog(peer, "[signal] OnLocalSdp error: " & ex.Message)
                    End Try
                End Sub

            ' 2) Candidates locales => dedup OUT + signal
            AddHandler sess.OnLocalCandidate,
                Sub(cand As String)
                    Try
                        Dim canon = CanonCandidate(cand)
                        If IsDupCandidateOut(peer, canon) Then Exit Sub
                        Dim b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cand))
                        If _sendSignal IsNot Nothing Then _sendSignal(peer, $"{TAG_ICE_CAND}{_localName}:{peer}:{b64}")
                    Catch ex As Exception
                        RaiseEvent OnLog(peer, "[signal] OnLocalCandidate error: " & ex.Message)
                    End Try
                End Sub

            ' 3) Etat ICE
            AddHandler sess.OnIceStateChanged,
                Sub(st As RTCIceConnectionState)
                    Select Case st
                        Case RTCIceConnectionState.connected
                            RaiseEvent OnP2PState(peer, True)
                        Case RTCIceConnectionState.failed, RTCIceConnectionState.disconnected, RTCIceConnectionState.closed
                            RaiseEvent OnP2PState(peer, False)
                    End Select
                End Sub

            ' 4) Messages texte P2P
            AddHandler sess.OnTextMessage,
                Sub(txt As String)
                    RaiseEvent OnP2PText(peer, txt)
                End Sub

            ' 5) Logs de négo détaillés
            AddHandler sess.OnNegotiationLog,
                Sub(l As String)
                    RaiseEvent OnLog(peer, l)
                End Sub
        End Sub



        Private Sub ResetDedup(peer As String)
            SyncLock _gate
                _seenCandOut.Remove(peer)
                _seenCandIn.Remove(peer)
            End SyncLock
        End Sub

        Private Sub FlushPendingCandidates(peer As String)
            Dim listToApply As List(Of String) = Nothing
            SyncLock _gate
                If _pendingCands.TryGetValue(peer, listToApply) AndAlso listToApply IsNot Nothing AndAlso listToApply.Count > 0 Then
                    listToApply = New List(Of String)(listToApply)
                    _pendingCands.Remove(peer)
                End If
            End SyncLock

            If listToApply Is Nothing OrElse listToApply.Count = 0 Then Return

            Dim sess As IceP2PSession = Nothing
            Dim exists As Boolean
            SyncLock _gate
                exists = _sessions.TryGetValue(peer, sess)
            End SyncLock
            If sess Is Nothing Then
                RaiseEvent OnLog(peer, "Flush pending ignoré (session absente).")
                Return
            End If

            For Each c In listToApply
                Try
                    sess.AddRemoteCandidate(c)
                Catch ex As Exception
                    RaiseEvent OnLog(peer, "Flush cand erreur: " & ex.Message)
                End Try
            Next
        End Sub

        Private Function IsDupCandidateOut(peer As String, canon As String) As Boolean
            SyncLock _gate
                Dim setOut As HashSet(Of String) = Nothing
                If Not _seenCandOut.TryGetValue(peer, setOut) OrElse setOut Is Nothing Then
                    setOut = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                    _seenCandOut(peer) = setOut
                End If
                If setOut.Contains(canon) Then
                    RaiseEvent OnLog(peer, "Local candidate ignorée (dupli OUT).")
                    Return True
                End If
                setOut.Add(canon)
                If setOut.Count > 128 Then setOut.Remove(setOut.First())
                Return False
            End SyncLock
        End Function

        Private Function CanonCandidate(c As String) As String
            If String.IsNullOrWhiteSpace(c) Then Return ""
            Return c.Trim()
        End Function

        ' ===== Extraction fingerprint DTLS depuis SDP =====
        Private Function ExtractDtlsFingerprintFromSdp(sdp As String) As String
            If String.IsNullOrWhiteSpace(sdp) Then Return Nothing
            Dim t = sdp.Replace(vbCr, "")
            For Each raw In t.Split(vbLf)
                Dim line = raw.Trim()
                If line.StartsWith("a=fingerprint:", StringComparison.OrdinalIgnoreCase) Then
                    Return line.Substring("a=fingerprint:".Length).Trim()
                End If
            Next
            Return Nothing
        End Function

        Private Sub SafeClose(sess As IceP2PSession)
            If sess Is Nothing Then Exit Sub
            Try
                ' Tente d'appeler DisposeAsync() si présent (IAsyncDisposable)
                Try
                    Dim mi = sess.GetType().GetMethod("DisposeAsync", Type.EmptyTypes)
                    If mi IsNot Nothing Then
                        ' Invoke retourne un ValueTask ; on l'ignore volontairement
                        Dim _ignored = mi.Invoke(sess, Nothing)
                    End If
                Catch
                    ' on ignore toute erreur de réflexion
                End Try

                ' Puis tente un Dispose() classique si dispo
                Dim disp = TryCast(sess, IDisposable)
                If disp IsNot Nothing Then
                    disp.Dispose()
                End If
            Catch
                ' on avale toute exception de fermeture
            End Try
        End Sub


    End Module

End Namespace
