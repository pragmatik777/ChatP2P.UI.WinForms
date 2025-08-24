Option Strict On
Imports System
Imports System.Collections.Generic
Imports System.Linq
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

        ' anti‑doublons de candidates
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

        Public Sub HandleOffer(fromPeer As String, sdp As String, stunUrls As IEnumerable(Of String))
            If String.IsNullOrWhiteSpace(fromPeer) OrElse String.IsNullOrWhiteSpace(sdp) Then Exit Sub
            If stunUrls Is Nothing Then stunUrls = New String() {"stun:stun.l.google.com:19302"}

            ' S’il existe une session (probablement caller) -> on la ferme pour basculer callee
            Dim oldSess As IceP2PSession = Nothing
            SyncLock _gate
                If _sessions.TryGetValue(fromPeer, oldSess) AndAlso oldSess IsNot Nothing Then
                    RaiseEvent OnLog(fromPeer, "Offer reçue: bascule callee (fermeture ancienne session).")
                    SafeClose(oldSess)
                    _sessions.Remove(fromPeer)
                End If
            End SyncLock

            ' Reset dedup pour cycle callee
            ResetDedup(fromPeer)

            Dim sess As New IceP2PSession(stunUrls, "dc", isCaller:=False)
            WireSessionHandlers(fromPeer, sess)
            SyncLock _gate
                _sessions(fromPeer) = sess
            End SyncLock

            ' Applique l’offer distante : la classe créera l’answer + OnLocalSdp("answer", …)
            sess.SetRemoteDescription("offer", sdp)

            ' Déroule les candidates reçues avant la session
            FlushPendingCandidates(fromPeer)

            SyncLock _gate : _startingPeers.Remove(fromPeer) : End SyncLock
        End Sub

        Public Sub HandleAnswer(fromPeer As String, sdp As String)
            If String.IsNullOrWhiteSpace(fromPeer) OrElse String.IsNullOrWhiteSpace(sdp) Then Exit Sub

            Dim sess As IceP2PSession = Nothing
            SyncLock _gate
                _sessions.TryGetValue(fromPeer, sess)
            End SyncLock
            If sess Is Nothing Then
                RaiseEvent OnLog(fromPeer, "Answer reçue mais session introuvable.")
                Exit Sub
            End If

            sess.SetRemoteDescription("answer", sdp)
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
                ' mise en file si pas encore de session/SDP
                SyncLock _gate
                    Dim list As List(Of String) = Nothing
                    If Not _pendingCands.TryGetValue(fromPeer, list) OrElse list Is Nothing Then
                        list = New List(Of String)()
                        _pendingCands(fromPeer) = list
                    End If
                    list.Add(candidate)
                End SyncLock
                RaiseEvent OnLog(fromPeer, "Candidate mise en attente (pas encore de session/SDP).")
                Exit Sub
            End If

            sess.AddRemoteCandidate(candidate)
        End Sub

        Public Function TrySendP2P(dest As String, text As String) As Boolean
            If String.IsNullOrWhiteSpace(dest) OrElse text Is Nothing Then Return False
            Dim sess As IceP2PSession = Nothing
            SyncLock _gate
                _sessions.TryGetValue(dest, sess)
            End SyncLock
            If sess Is Nothing OrElse Not sess.IsOpen Then Return False
            sess.SendText(text)
            Return True
        End Function

        Public Function IsConnected(peer As String) As Boolean
            If String.IsNullOrWhiteSpace(peer) Then Return False
            Dim sess As IceP2PSession = Nothing
            SyncLock _gate
                _sessions.TryGetValue(peer, sess)
            End SyncLock
            Return (sess IsNot Nothing AndAlso sess.IsOpen)
        End Function

        ' ==== Handlers & helpers internes ====

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

            ' 3bis) Connexion haut niveau
            AddHandler sess.OnConnected,
                Sub()
                    RaiseEvent OnP2PState(peer, True)
                End Sub

            AddHandler sess.OnClosed,
                Sub(reason As String)
                    RaiseEvent OnP2PState(peer, False)
                End Sub

            ' 4) Messages texte P2P
            AddHandler sess.OnTextMessage,
                Sub(txt As String)
                    RaiseEvent OnP2PText(peer, txt)
                End Sub

            ' 5) Logs de négo détaillés (très utile ici)
            AddHandler sess.OnNegotiationLog,
                Sub(l As String)
                    RaiseEvent OnLog(peer, l)
                End Sub
        End Sub

        Private Sub FlushPendingCandidates(peer As String)
            Dim listToApply As List(Of String) = Nothing
            SyncLock _gate
                If _pendingCands.TryGetValue(peer, listToApply) AndAlso listToApply IsNot Nothing AndAlso listToApply.Count > 0 Then
                    ' copie locale + purge immédiate (évite double flush)
                    listToApply = New List(Of String)(listToApply)
                    _pendingCands.Remove(peer)
                End If
            End SyncLock

            If listToApply Is Nothing OrElse listToApply.Count = 0 Then Return

            RaiseEvent OnLog(peer, $"Application de {listToApply.Count} candidate(s) en attente.")
            Dim sess As IceP2PSession = Nothing
            SyncLock _gate
                _sessions.TryGetValue(peer, sess)
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

        Private Sub ResetDedup(peer As String)
            SyncLock _gate
                _seenCandOut.Remove(peer)
                _seenCandIn.Remove(peer)
                ' NB: on ne vide pas _pendingCands ici, on veut justement les rejouer après SDP
            End SyncLock
        End Sub

        Private Function CanonCandidate(c As String) As String
            If c Is Nothing Then Return ""
            Dim s = c.Trim().Replace(vbCr, "").Replace(vbLf, " ")
            Do While s.Contains("  ", StringComparison.Ordinal)
                s = s.Replace("  ", " ")
            Loop
            Return s
        End Function

        Private Sub SafeClose(sess As IceP2PSession)
            If sess Is Nothing Then Exit Sub
            Try
                Dim vt = sess.DisposeAsync()
                ' fire-and-forget
            Catch
            End Try
        End Sub

    End Module

End Namespace
