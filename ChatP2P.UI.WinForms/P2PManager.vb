' ChatP2P.UI.WinForms/P2PManager.vb
Option Strict On
Imports System.Text
Imports ChatP2P.Core
Imports Proto = ChatP2P.App.Protocol.Tags

Public NotInheritable Class P2PManager

    Private Sub New()
    End Sub

    ' Nom local (affiché et utilisé pour taguer les trames)
    Public Shared Property LocalName As String = "Me"

    ' Callback pour émettre une trame ICE via le hub (dest, payload texte)
    Public Delegate Function SendSignalDelegate(dest As String, payload As String) As Threading.Tasks.Task
    Private Shared _sendSignal As SendSignalDelegate

    ' Log vers l’UI
    Public Shared Event OnLog(peer As String, line As String)

    ' Message texte reçu via ICE
    Public Shared Event OnP2PText(peer As String, text As String)

    ' Etat P2P connecté/déconnecté (pour UI)
    Public Shared Event OnP2PState(peer As String, connected As Boolean)

    ' Sessions ICE par pair
    Private Shared ReadOnly _sessions As New Dictionary(Of String, IceP2PSession)()

    ' --- Appelé par Form1 au démarrage pour fournir la fonction d’envoi via hub ---
    Public Shared Sub Init(sendSignal As SendSignalDelegate, localDisplayName As String)
        _sendSignal = sendSignal
        If Not String.IsNullOrWhiteSpace(localDisplayName) Then LocalName = localDisplayName
    End Sub

    ' --- Démarre une connexion ICE vers un peer (côté "caller") ---
    Public Shared Sub StartP2P(peer As String, Optional stunUrls As IEnumerable(Of String) = Nothing)
        StopP2P(peer) ' nettoie une éventuelle session existante

        Dim sess = New IceP2PSession(stunUrls, label:="p2p-" & peer, isCaller:=True)
        Wire(peer, sess)

        ' création de l’offre + envoi via hub (taggé et adressé)
        AddHandler sess.OnLocalSdp,
            Async Sub(tp As String, sdp As String)
                RaiseEvent OnLog(peer, $"[ICE] Local SDP ({tp})")
                If _sendSignal IsNot Nothing Then
                    Dim payload = $"{Proto.TAG_ICE_OFFER}{LocalName}:{peer}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(sdp))}"
                    Await _sendSignal(peer, payload)
                End If
            End Sub

        ' candidats locaux
        AddHandler sess.OnLocalCandidate,
            Async Sub(c As String)
                RaiseEvent OnLog(peer, $"[ICE] Local candidate")
                If _sendSignal IsNot Nothing Then
                    Dim payload = $"{Proto.TAG_ICE_CAND}{LocalName}:{peer}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(c))}"
                    Await _sendSignal(peer, payload)
                End If
            End Sub

        _sessions(peer) = sess
        sess.Start()
    End Sub

    ' --- Côté callee : on reçoit une offre, on crée la session si absente, on répond ---
    Public Shared Sub HandleOffer(fromPeer As String, offerSdp As String, Optional stunUrls As IEnumerable(Of String) = Nothing)
        Dim sess As IceP2PSession = Nothing
        If Not _sessions.TryGetValue(fromPeer, sess) Then
            sess = New IceP2PSession(stunUrls, label:="p2p-" & fromPeer, isCaller:=False)
            Wire(fromPeer, sess)
            _sessions(fromPeer) = sess
        End If

        sess.SetRemoteDescription("offer", offerSdp)
        ' Réponse locale => envoyée via OnLocalSdp
        AddHandler sess.OnLocalSdp,
            Async Sub(tp As String, sdp As String)
                RaiseEvent OnLog(fromPeer, $"[ICE] Local SDP ({tp})")
                If tp.Equals("answer", StringComparison.OrdinalIgnoreCase) AndAlso _sendSignal IsNot Nothing Then
                    Dim payload = $"{Proto.TAG_ICE_ANSWER}{LocalName}:{fromPeer}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(sdp))}"
                    Await _sendSignal(fromPeer, payload)
                End If
            End Sub

        AddHandler sess.OnLocalCandidate,
            Async Sub(c As String)
                RaiseEvent OnLog(fromPeer, $"[ICE] Local candidate")
                If _sendSignal IsNot Nothing Then
                    Dim payload = $"{Proto.TAG_ICE_CAND}{LocalName}:{fromPeer}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(c))}"
                    Await _sendSignal(fromPeer, payload)
                End If
            End Sub
    End Sub

    ' --- Les réponses SDP (answer) côté caller ---
    Public Shared Sub HandleAnswer(fromPeer As String, answerSdp As String)
        Dim sess As IceP2PSession = Nothing
        If _sessions.TryGetValue(fromPeer, sess) Then
            sess.SetRemoteDescription("answer", answerSdp)
        End If
    End Sub

    ' --- Ajout de candidats distants ---
    Public Shared Sub HandleCandidate(fromPeer As String, cand As String)
        Dim sess As IceP2PSession = Nothing
        If _sessions.TryGetValue(fromPeer, sess) Then
            sess.AddRemoteCandidate(cand)
        End If
    End Sub

    ' --- Envoi texte via P2P si connecté, sinon False ---
    Public Shared Function TrySendP2P(peer As String, text As String) As Boolean
        Dim sess As IceP2PSession = Nothing
        If _sessions.TryGetValue(peer, sess) AndAlso sess IsNot Nothing Then
            Try
                sess.SendText(text)
                Return True
            Catch
            End Try
        End If
        Return False
    End Function

    ' --- Stopper une session P2P ---
    Public Shared Sub StopP2P(peer As String)
        Dim sess As IceP2PSession = Nothing
        If _sessions.TryGetValue(peer, sess) Then
            _sessions.Remove(peer)
            Try
                sess.DisposeAsync()
            Catch
            End Try
        End If
        RaiseEvent OnP2PState(peer, False)
    End Sub

    ' --- Wiring commun des callbacks IceP2PSession ---
    Private Shared Sub Wire(peer As String, sess As IceP2PSession)
        AddHandler sess.OnNegotiationLog, Sub(line) RaiseEvent OnLog(peer, line)
        AddHandler sess.OnConnected, Sub() RaiseEvent OnP2PState(peer, True)
        AddHandler sess.OnClosed, Sub(r) RaiseEvent OnP2PState(peer, False)
        AddHandler sess.OnTextMessage, Sub(t) RaiseEvent OnP2PText(peer, t)
        ' (OnBinaryMessage dispo si besoin plus tard)
    End Sub

End Class
