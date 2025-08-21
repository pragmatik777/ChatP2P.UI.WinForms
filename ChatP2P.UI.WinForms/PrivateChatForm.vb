Option Strict On
Imports System
Imports System.Windows.Forms
Imports System.Text
Imports ChatP2P.Core                  ' <-- rend le module P2PManager visible
Imports PM = ChatP2P.Core.P2PManager  ' <-- alias pratique

Public Class PrivateChatForm
    Inherits Form

    Private ReadOnly _myName As String
    Private _peerName As String
    Private ReadOnly _sendAction As Action(Of String)

    ' UI
    Private ReadOnly pnlTop As Panel
    Private ReadOnly lblP2PState As Label
    Private ReadOnly btnP2P As Button

    Private ReadOnly txtHistory As RichTextBox
    Private ReadOnly txtInput As TextBox
    Private ReadOnly btnSend As Button

    Public Sub New(myName As String, peerName As String, sendAction As Action(Of String))
        ' --- State ---
        _myName = If(String.IsNullOrWhiteSpace(myName), "Me", myName)
        _peerName = If(String.IsNullOrWhiteSpace(peerName), "Peer", peerName)
        _sendAction = sendAction

        ' --- Form ---
        Me.Text = $"Chat privé avec {_peerName}"
        Me.StartPosition = FormStartPosition.CenterParent
        Me.Width = 560
        Me.Height = 460
        Me.MinimizeBox = True
        Me.MaximizeBox = True

        ' --- Top panel : état + bouton P2P ---
        pnlTop = New Panel() With {
            .Dock = DockStyle.Top,
            .Height = 36
        }

        lblP2PState = New Label() With {
            .Text = "P2P: inconnu",
            .AutoSize = True,
            .Left = 8,
            .Top = 10
        }

        btnP2P = New Button() With {
            .Text = "Démarrer P2P",
            .Width = 110,
            .Height = 26,
            .Top = 5,
            .Left = 160
        }
        AddHandler btnP2P.Click, AddressOf BtnP2P_Click

        pnlTop.Controls.Add(lblP2PState)
        pnlTop.Controls.Add(btnP2P)

        ' --- Zone d'historique ---
        txtHistory = New RichTextBox() With {
            .ReadOnly = True,
            .DetectUrls = True,
            .HideSelection = False,
            .Dock = DockStyle.Fill
        }

        ' --- Zone de saisie + bouton envoyer ---
        txtInput = New TextBox() With {
            .Dock = DockStyle.Bottom
        }
        AddHandler txtInput.KeyDown, AddressOf TxtInput_KeyDown

        btnSend = New Button() With {
            .Text = "Envoyer",
            .Dock = DockStyle.Bottom,
            .Height = 32
        }
        AddHandler btnSend.Click, AddressOf BtnSend_Click

        ' --- Ordre d'empilement ---
        Me.Controls.Add(txtHistory)
        Me.Controls.Add(btnSend)
        Me.Controls.Add(txtInput)
        Me.Controls.Add(pnlTop)

        ' --- Abonnements P2PManager (module statique) ---
        AddHandler PM.OnLog, AddressOf OnP2PLog
        AddHandler PM.OnP2PState, AddressOf OnP2PState
        AddHandler PM.OnP2PText, AddressOf OnP2PText

        ' --- Etat initial du label selon l’état courant du P2P ---
        UpdateStateText(PM.IsConnected(_peerName))
    End Sub

    ' Expose (lecture seule) le nom du peer
    Public ReadOnly Property PeerName As String
        Get
            Return _peerName
        End Get
    End Property

    ' --- API: changer le nom du peer (ex: après NAME:) ---
    Public Sub UpdatePeerName(newName As String)
        If String.IsNullOrWhiteSpace(newName) Then Return
        _peerName = newName
        Try
            Me.Text = $"Chat privé avec {_peerName}"
            ' met aussi à jour le label selon le nouvel interlocuteur
            UpdateStateText(PM.IsConnected(_peerName))
        Catch
        End Try
    End Sub

    ' --- API: ajouter un message dans l'historique ---
    Public Sub AppendMessage(senderName As String, message As String)
        If message Is Nothing Then Return
        If txtHistory.InvokeRequired Then
            txtHistory.Invoke(Sub() AppendMessage(senderName, message))
        Else
            txtHistory.AppendText($"{senderName}: {message}{Environment.NewLine}")
            ' Auto scroll
            txtHistory.SelectionStart = txtHistory.TextLength
            txtHistory.ScrollToCaret()
        End If
    End Sub

    ' ======================
    ' ==  Envois & UI   ====
    ' ======================
    Private Sub BtnSend_Click(sender As Object, e As EventArgs)
        SendCurrent()
    End Sub

    Private Sub TxtInput_KeyDown(sender As Object, e As KeyEventArgs)
        If e.KeyCode = Keys.Enter Then
            e.SuppressKeyPress = True
            SendCurrent()
        End If
    End Sub

    Private Sub SendCurrent()
        Dim msg = txtInput.Text.Trim()
        If msg = "" Then Return

        ' Affiche localement d'abord
        AppendMessage(_myName, msg)
        txtInput.Clear()

        ' ⚠️ Envoi par défaut via le relais (pas de tentative P2P ici)
        Try
            _sendAction?.Invoke(msg)
        Catch ex As Exception
            MessageBox.Show("Envoi privé échoué: " & ex.Message, "Chat privé", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub

    ' Bouton pour déclencher la négociation ICE
    Private Sub BtnP2P_Click(sender As Object, e As EventArgs)
        Try
            ' feedback immédiat non bloquant
            lblP2PState.Text = "P2P: en cours…"
            btnP2P.Enabled = False

            ' démarre la nego (asynchrone via le manager)
            PM.StartP2P(_peerName, New String() {"stun:stun.l.google.com:19302"})
            SafeLog($"[P2P] Négociation démarrée vers {_peerName}")
        Catch ex As Exception
            btnP2P.Enabled = True
            SafeLog("[P2P] Démarrage échoué: " & ex.Message)
        End Try
    End Sub

    ' ======================
    ' ==  Handlers P2P   ===
    ' ======================

    ' Log P2P (filtré par peer)
    Private Sub OnP2PLog(peer As String, line As String)
        If Not String.Equals(peer, _peerName, StringComparison.OrdinalIgnoreCase) Then Return
        SafeLog("[P2P] " & line)
    End Sub

    ' Etat connecté/déconnecté (filtré par peer)
    Private Sub OnP2PState(peer As String, connected As Boolean)
        If Not String.Equals(peer, _peerName, StringComparison.OrdinalIgnoreCase) Then Return
        If lblP2PState.InvokeRequired Then
            lblP2PState.Invoke(Sub() UpdateStateText(connected))
        Else
            UpdateStateText(connected)
        End If
    End Sub

    Private Sub UpdateStateText(connected As Boolean)
        lblP2PState.Text = If(connected, "P2P: connecté", "P2P: déconnecté")
        btnP2P.Enabled = Not connected
    End Sub

    ' Texte reçu via DataChannel P2P (filtré par peer)
    Private Sub OnP2PText(peer As String, text As String)
        If Not String.Equals(peer, _peerName, StringComparison.OrdinalIgnoreCase) Then Return
        AppendMessage(peer, text)
    End Sub

    ' Utilitaire log thread-safe
    Private Sub SafeLog(line As String)
        If txtHistory.InvokeRequired Then
            txtHistory.Invoke(Sub() SafeLog(line))
        Else
            txtHistory.AppendText(line & Environment.NewLine)
            txtHistory.SelectionStart = txtHistory.TextLength
            txtHistory.ScrollToCaret()
        End If
    End Sub

    ' Nettoyage : on se désabonne des events statiques
    Protected Overrides Sub OnFormClosed(e As FormClosedEventArgs)
        MyBase.OnFormClosed(e)
        RemoveHandler PM.OnLog, AddressOf OnP2PLog
        RemoveHandler PM.OnP2PState, AddressOf OnP2PState
        RemoveHandler PM.OnP2PText, AddressOf OnP2PText
    End Sub
End Class
