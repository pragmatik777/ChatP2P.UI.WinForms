' ChatP2P.Core/P2PManager.vb
Option Strict On
Imports System.Text
Imports System.Threading.Tasks
Imports SIPSorcery.Net

Namespace ChatP2P.Core

    ''' <summary>
    ''' Orchestrateur simple des sessions ICE/DataChannel (peer logique → IceP2PSession).
    ''' Fournit le minimum pour l’UI : démarrer P2P, envoyer si dispo, et gérer le signaling.
    ''' </summary>
    Public Module P2PManager

        ' ========= Événements exposés vers l’UI =========
        ' Log détaillé par pair
        Public Event OnLog(peer As String, line As String)
        ' Changement d’état connecté/déconnecté
        Public Event OnP2PState(peer As String, connected As Boolean)
        ' Texte reçu via DataChannel
        Public Event OnP2PText(peer As String, text As String)

        ' ========= Callback de signalisation (fournie par Form1.Init) =========
        ' sendSignal(destName, payloadLineTexte)
        ' Le payload est une ligne avec préfixe: ICE_OFFER:/ICE_ANSWER:/ICE_CAND:
        Private _sendSignal As Func(Of String, String, Task) = Nothing
        Private _localName As String = "Me"

        ' ========= Sessions & état =========
        Private ReadOnly _gate As New Object()
        Private ReadOnly _sessions As New Dictionary(Of String, IceP2PSession)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _conn As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)

        ' ========= Tags de signaling (pas de dépendance à Proto côté Core) =========
        Private Const TAG_ICE_OFFER As String = "ICE_OFFER:"
        Private Const TAG_ICE_ANSWER As String = "ICE_ANSWER:"
        Private Const TAG_ICE_CAND As String = "ICE_CAND:"

        ' ============= API publique =====================

        ''' <summary>À appeler au démarrage pour fournir le callback de signalisation et le nom local.</summary>
        Public Sub Init(sendSignal As Func(Of String, String, Task), localDisplayName As String)
            _sendSignal = sendSignal
            If Not String.IsNullOrWhiteSpace(localDisplayName) Then _localName = localDisplayName
        End Sub

        ''' <summary>Démarre (caller) une session P2P vers un peer (ne bloque pas l’UI).</summary>
        Public Sub StartP2P(peer As String, stunUrls As IEnumerable(Of String))
            If String.IsNullOrWhiteSpace(peer) Then Exit Sub

            Dim already As Boolean
            SyncLock _gate
                already = _sessions.ContainsKey(peer)
            End SyncLock
            If already Then
                RaiseEvent OnLog(peer, "StartP2P ignoré: session déjà existante.")
                Exit Sub
            End If

            ' Crée session caller
            Dim sess As New IceP2PSession(stunUrls, "dc", isCaller:=True)
            WireSessionHandlers(peer, sess)

            ' Signaling sortant
            AddHandler sess.OnLocalSdp,
                Async Sub(kind As String, sdp As String)
                    Try
                        Dim b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sdp))
                        If String.Equals(kind, "offer", StringComparison.OrdinalIgnoreCase) Then
                            If _sendSignal IsNot Nothing Then
                                Await _sendSignal(peer, $"{TAG_ICE_OFFER}{_localName}:{peer}:{b64}")
                            End If
                        Else
                            If _sendSignal IsNot Nothing Then
                                Await _sendSignal(peer, $"{TAG_ICE_ANSWER}{_localName}:{peer}:{b64}")
                            End If
                        End If
                    Catch ex As Exception
                        RaiseEvent OnLog(peer, "[signal] OnLocalSdp error: " & ex.Message)
                    End Try
                End Sub

            AddHandler sess.OnLocalCandidate,
                Async Sub(cand As String)
                    Try
                        Dim b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cand))
                        If _sendSignal IsNot Nothing Then
                            Await _sendSignal(peer, $"{TAG_ICE_CAND}{_localName}:{peer}:{b64}")
                        End If
                    Catch ex As Exception
                        RaiseEvent OnLog(peer, "[signal] OnLocalCandidate error: " & ex.Message)
                    End Try
                End Sub

            ' Enregistre et lance l’offer (sync)
            SyncLock _gate
                _sessions(peer) = sess
            End SyncLock

            sess.Start()
        End Sub

        ''' <summary>Retourne True si on a une session et qu’elle est connectée.</summary>
        Public Function IsConnected(peer As String) As Boolean
            If String.IsNullOrWhiteSpace(peer) Then Return False
            SyncLock _gate
                Dim v As Boolean = False
                Return _conn.TryGetValue(peer, v) AndAlso v
            End SyncLock
        End Function

        ''' <summary>Envoi texte via DataChannel si session dispo et ouverte. Retourne True si l’envoi a été fait.</summary>
        Public Function TrySendP2P(peer As String, text As String) As Boolean
            If String.IsNullOrWhiteSpace(peer) OrElse text Is Nothing Then Return False
            SyncLock _gate
                Dim sess As IceP2PSession = Nothing
                If _sessions.TryGetValue(peer, sess) AndAlso sess IsNot Nothing Then
                    Try
                        sess.SendText(text)
                        Return True
                    Catch ex As Exception
                        ' échec → retourner False pour permettre le fallback hub
                        RaiseEvent OnLog(peer, "TrySendP2P: " & ex.Message)
                        Return False
                    End Try
                End If
            End SyncLock
            Return False
        End Function

        ''' <summary>Côté appelé : application d’une OFFER reçue (SDP). Répondra par une ANSWER via OnLocalSdp.</summary>
        Public Sub HandleOffer(fromPeer As String, sdp As String, stunUrls As IEnumerable(Of String))
            If String.IsNullOrWhiteSpace(fromPeer) OrElse String.IsNullOrWhiteSpace(sdp) Then Exit Sub

            Dim sess As IceP2PSession = Nothing
            Dim exists As Boolean
            SyncLock _gate
                exists = _sessions.TryGetValue(fromPeer, sess)
            End SyncLock

            If Not exists OrElse sess Is Nothing Then
                ' Callee
                sess = New IceP2PSession(stunUrls, "dc", isCaller:=False)
                WireSessionHandlers(fromPeer, sess)

                AddHandler sess.OnLocalSdp,
                    Async Sub(kind As String, localSdp As String)
                        Try
                            Dim b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(localSdp))
                            If String.Equals(kind, "answer", StringComparison.OrdinalIgnoreCase) Then
                                If _sendSignal IsNot Nothing Then
                                    Await _sendSignal(fromPeer, $"{TAG_ICE_ANSWER}{_localName}:{fromPeer}:{b64}")
                                End If
                            Else
                                ' cas rare: renegociation callee → offer
                                If _sendSignal IsNot Nothing Then
                                    Await _sendSignal(fromPeer, $"{TAG_ICE_OFFER}{_localName}:{fromPeer}:{b64}")
                                End If
                            End If
                        Catch ex As Exception
                            RaiseEvent OnLog(fromPeer, "[signal] OnLocalSdp(callee) error: " & ex.Message)
                        End Try
                    End Sub

                AddHandler sess.OnLocalCandidate,
                    Async Sub(cand As String)
                        Try
                            Dim b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cand))
                            If _sendSignal IsNot Nothing Then
                                Await _sendSignal(fromPeer, $"{TAG_ICE_CAND}{_localName}:{fromPeer}:{b64}")
                            End If
                        Catch ex As Exception
                            RaiseEvent OnLog(fromPeer, "[signal] OnLocalCandidate(callee) error: " & ex.Message)
                        End Try
                    End Sub

                SyncLock _gate
                    _sessions(fromPeer) = sess
                End SyncLock
            End If

            ' Applique l’offer distante → générera Answer via OnLocalSdp
            sess.SetRemoteDescription("offer", sdp)
        End Sub

        ''' <summary>Le caller reçoit une ANSWER du pair.</summary>
        Public Sub HandleAnswer(fromPeer As String, sdp As String)
            If String.IsNullOrWhiteSpace(fromPeer) OrElse String.IsNullOrWhiteSpace(sdp) Then Exit Sub
            SyncLock _gate
                If _sessions.ContainsKey(fromPeer) Then
                    _sessions(fromPeer).SetRemoteDescription("answer", sdp)
                End If
            End SyncLock
        End Sub

        ''' <summary>Ajoute un ICE candidate distant dans la session existante.</summary>
        Public Sub HandleCandidate(fromPeer As String, candidate As String)
            If String.IsNullOrWhiteSpace(fromPeer) OrElse String.IsNullOrWhiteSpace(candidate) Then Exit Sub
            SyncLock _gate
                If _sessions.ContainsKey(fromPeer) Then
                    _sessions(fromPeer).AddRemoteCandidate(candidate)
                End If
            End SyncLock
        End Sub

        ' ============= utilitaires internes =================

        Private Sub WireSessionHandlers(peer As String, sess As IceP2PSession)
            ' Log de négo
            AddHandler sess.OnNegotiationLog,
                Sub(line As String)
                    RaiseEvent OnLog(peer, line)
                End Sub

            ' Connexion établie
            AddHandler sess.OnConnected,
                Sub()
                    SyncLock _gate
                        _conn(peer) = True
                    End SyncLock
                    RaiseEvent OnP2PState(peer, True)
                End Sub

            ' Fermeture / échec
            AddHandler sess.OnClosed,
                Sub(reason As String)
                    SyncLock _gate
                        _conn(peer) = False
                    End SyncLock
                    RaiseEvent OnP2PState(peer, False)
                End Sub

            ' Message texte entrant
            AddHandler sess.OnTextMessage,
                Sub(text As String)
                    RaiseEvent OnP2PText(peer, text)
                End Sub
        End Sub

    End Module

End Namespace
