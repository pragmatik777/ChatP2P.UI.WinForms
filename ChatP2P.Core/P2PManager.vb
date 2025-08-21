' ChatP2P.Core/P2PManager.vb
Option Strict On
Imports System.Text
Imports SIPSorcery.Net

Namespace ChatP2P.Core
    Public Module P2PManager

        Public Event OnLog(peer As String, line As String)
        Public Event OnP2PState(peer As String, connected As Boolean)
        Public Event OnP2PText(peer As String, text As String)

        Private _sendSignal As Func(Of String, String, Threading.Tasks.Task)
        Private _localName As String = "Me"

        Private ReadOnly _gate As New Object()
        Private ReadOnly _sessions As New Dictionary(Of String, IceP2PSession)(StringComparer.OrdinalIgnoreCase)

        ' 🔥 Suivi de l’état de connexion par pair
        Private ReadOnly _connected As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        Private Const TAG_ICE_OFFER As String = "ICE_OFFER:"
        Private Const TAG_ICE_ANSWER As String = "ICE_ANSWER:"
        Private Const TAG_ICE_CAND As String = "ICE_CAND:"

        Public Sub Init(sendSignal As Func(Of String, String, Threading.Tasks.Task), localDisplayName As String)
            _sendSignal = sendSignal
            If Not String.IsNullOrWhiteSpace(localDisplayName) Then _localName = localDisplayName
        End Sub

        Public Function HasSession(peer As String) As Boolean
            If String.IsNullOrWhiteSpace(peer) Then Return False
            SyncLock _gate
                Return _sessions.ContainsKey(peer)
            End SyncLock
        End Function

        ' 🔥 Nouveauté : lire l’état connecté
        Public Function IsConnected(peer As String) As Boolean
            If String.IsNullOrWhiteSpace(peer) Then Return False
            SyncLock _gate
                Return _connected.Contains(peer)
            End SyncLock
        End Function

        Public Sub SendText(peer As String, text As String)
            If String.IsNullOrWhiteSpace(peer) Then Throw New ArgumentException(NameOf(peer))
            SyncLock _gate
                If Not _sessions.ContainsKey(peer) Then Throw New InvalidOperationException("Session P2P absente.")
                _sessions(peer).SendText(text)
            End SyncLock
        End Sub

        Public Function TrySendP2P(peer As String, text As String) As Boolean
            If String.IsNullOrWhiteSpace(peer) OrElse String.IsNullOrWhiteSpace(text) Then Return False
            SyncLock _gate
                If Not _sessions.ContainsKey(peer) Then Return False
                Try
                    _sessions(peer).SendText(text)
                    Return True
                Catch
                    Return False
                End Try
            End SyncLock
        End Function

        Public Sub StartP2P(peer As String, stunUrls As IEnumerable(Of String))
            Dim __ = StartOfferAsync(peer, stunUrls)
        End Sub

        Public Async Function StartOfferAsync(peer As String, stunUrls As IEnumerable(Of String)) As Threading.Tasks.Task
            If String.IsNullOrWhiteSpace(peer) Then Return

            Dim existed As Boolean
            SyncLock _gate
                existed = _sessions.ContainsKey(peer)
            End SyncLock
            If existed Then Return

            Dim sess As New IceP2PSession(stunUrls, "dc", isCaller:=True)
            WireSessionHandlers(peer, sess)

            SyncLock _gate
                _sessions(peer) = sess
            End SyncLock

            AddHandler sess.OnLocalSdp,
                Async Sub(kind As String, sdp As String)
                    Try
                        Dim b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sdp))
                        If String.Equals(kind, "offer", StringComparison.OrdinalIgnoreCase) Then
                            If _sendSignal IsNot Nothing Then Await _sendSignal(peer, $"{TAG_ICE_OFFER}{_localName}:{peer}:{b64}")
                        Else
                            If _sendSignal IsNot Nothing Then Await _sendSignal(peer, $"{TAG_ICE_ANSWER}{_localName}:{peer}:{b64}")
                        End If
                    Catch
                    End Try
                End Sub

            AddHandler sess.OnLocalCandidate,
                Async Sub(cand As String)
                    Try
                        Dim b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cand))
                        If _sendSignal IsNot Nothing Then Await _sendSignal(peer, $"{TAG_ICE_CAND}{_localName}:{peer}:{b64}")
                    Catch
                    End Try
                End Sub

            sess.Start()
            Await Threading.Tasks.Task.CompletedTask
        End Function

        Public Sub HandleOffer(fromPeer As String, sdp As String, stunUrls As IEnumerable(Of String))
            If String.IsNullOrWhiteSpace(fromPeer) OrElse String.IsNullOrWhiteSpace(sdp) Then Return

            Dim sess As IceP2PSession = Nothing
            ' Récupère la session si elle existe déjà
            SyncLock _gate
                _sessions.TryGetValue(fromPeer, sess)
            End SyncLock

            ' Si absente → créer la session callee et câbler les handlers
            If sess Is Nothing Then
                sess = New IceP2PSession(stunUrls, "dc", isCaller:=False)
                WireSessionHandlers(fromPeer, sess)

                AddHandler sess.OnLocalSdp,
            Async Sub(kind As String, localSdp As String)
                Try
                    Dim b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(localSdp))
                    If String.Equals(kind, "answer", StringComparison.OrdinalIgnoreCase) Then
                        If _sendSignal IsNot Nothing Then Await _sendSignal(fromPeer, $"{TAG_ICE_ANSWER}{_localName}:{fromPeer}:{b64}")
                    Else
                        If _sendSignal IsNot Nothing Then Await _sendSignal(fromPeer, $"{TAG_ICE_OFFER}{_localName}:{fromPeer}:{b64}")
                    End If
                Catch
                End Try
            End Sub

                AddHandler sess.OnLocalCandidate,
            Async Sub(cand As String)
                Try
                    Dim b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cand))
                    If _sendSignal IsNot Nothing Then Await _sendSignal(fromPeer, $"{TAG_ICE_CAND}{_localName}:{fromPeer}:{b64}")
                Catch
                End Try
            End Sub

                SyncLock _gate
                    _sessions(fromPeer) = sess
                End SyncLock
            End If

            ' Applique l’offer distante (déclenchera l’answer via OnLocalSdp)
            sess.SetRemoteDescription("offer", sdp)
        End Sub


        Public Sub HandleAnswer(fromPeer As String, sdp As String)
            If String.IsNullOrWhiteSpace(fromPeer) OrElse String.IsNullOrWhiteSpace(sdp) Then Return
            SyncLock _gate
                If _sessions.ContainsKey(fromPeer) Then
                    _sessions(fromPeer).SetRemoteDescription("answer", sdp)
                End If
            End SyncLock
        End Sub

        Public Sub HandleCandidate(fromPeer As String, candidate As String)
            If String.IsNullOrWhiteSpace(fromPeer) OrElse String.IsNullOrWhiteSpace(candidate) Then Return
            SyncLock _gate
                If _sessions.ContainsKey(fromPeer) Then
                    _sessions(fromPeer).AddRemoteCandidate(candidate)
                End If
            End SyncLock
        End Sub

        ' ====== Wiring des events d’une session vers les events du module + suivi d’état ======
        Private Sub WireSessionHandlers(peer As String, sess As IceP2PSession)
            AddHandler sess.OnNegotiationLog,
                Sub(line As String)
                    RaiseEvent OnLog(peer, line)
                End Sub

            AddHandler sess.OnConnected,
                Sub()
                    SyncLock _gate
                        _connected.Add(peer)
                    End SyncLock
                    RaiseEvent OnP2PState(peer, True)
                    RaiseEvent OnLog(peer, "DataChannel connecté")
                End Sub

            AddHandler sess.OnClosed,
                Sub(reason As String)
                    SyncLock _gate
                        _connected.Remove(peer)
                    End SyncLock
                    RaiseEvent OnP2PState(peer, False)
                    RaiseEvent OnLog(peer, "Fermé: " & reason)
                End Sub

            AddHandler sess.OnTextMessage,
                Sub(text As String)
                    RaiseEvent OnP2PText(peer, text)
                End Sub
        End Sub

    End Module
End Namespace