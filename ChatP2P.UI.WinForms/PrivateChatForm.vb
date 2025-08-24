' ChatP2P.UI.WinForms/PrivateChatForm.vb
Option Strict On
Imports System
Imports System.Drawing
Imports System.Text
Imports System.Windows.Forms
Imports ChatP2P.Core                  ' P2PManager
Imports PM = ChatP2P.Core.P2PManager  ' alias

Public Class PrivateChatForm
    Inherits Form

    Private ReadOnly _myName As String
    Private _peerName As String
    Private ReadOnly _sendAction As Action(Of String)

    ' UI
    Private ReadOnly pnlTop As Panel
    Private ReadOnly lblP2PState As Label
    Private ReadOnly lblAuthStatus As Label
    Private ReadOnly lblCryptoStatus As Label
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
        Me.Width = 580
        Me.Height = 480
        Me.MinimizeBox = True
        Me.MaximizeBox = True

        ' --- Top panel : états + bouton P2P ---
        pnlTop = New Panel() With {
            .Dock = DockStyle.Top,
            .Height = 38
        }

        lblP2PState = New Label() With {
            .Text = "P2P: déconnecté",
            .AutoSize = True,
            .Left = 8,
            .Top = 10
        }

        lblAuthStatus = New Label() With {
            .Text = "Auth: —",
            .AutoSize = True,
            .Left = 140,
            .Top = 10
        }

        lblCryptoStatus = New Label() With {
            .Text = "Crypto: OFF",
            .AutoSize = True,
            .Left = 240,
            .Top = 10
        }

        btnP2P = New Button() With {
            .Text = "Démarrer P2P",
            .Width = 120,
            .Height = 26,
            .Top = 6,
            .Left = 380
        }
        AddHandler btnP2P.Click, AddressOf BtnP2P_Click

        pnlTop.Controls.Add(lblP2PState)
        pnlTop.Controls.Add(lblAuthStatus)
        pnlTop.Controls.Add(lblCryptoStatus)
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

        ' --- Empilement ---
        Me.Controls.Add(txtHistory)
        Me.Controls.Add(btnSend)
        Me.Controls.Add(txtInput)
        Me.Controls.Add(pnlTop)

        ' --- Abonnements P2PManager ---
        AddHandler PM.OnLog, AddressOf OnP2PLog    ' log filtré par peer
        AddHandler PM.OnP2PState, AddressOf OnP2PState

        ' ⚠️ IMPORTANT: PAS d'abonnement à PM.OnP2PText ici
        ' => Form1 centralise l'affichage des messages P2P pour éviter les doublons.

        ' --- État initial ---
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
            UpdateStateText(PM.IsConnected(_peerName))
        Catch
        End Try
    End Sub

    ' --- API publique: état Auth ---
    Public Sub SetAuthStatus(verified As Boolean, Optional fingerprint As String = "")
        If Me.InvokeRequired Then
            Me.BeginInvoke(Sub() SetAuthStatus(verified, fingerprint))
            Return
        End If
        lblAuthStatus.Text = If(verified,
                                If(String.IsNullOrWhiteSpace(fingerprint), "Auth: OK", $"Auth: OK ({fingerprint})"),
                                "Auth: —")
    End Sub

    ' --- API publique: état Crypto applicatif ---
    Public Sub SetCryptoActive(active As Boolean)
        If Me.InvokeRequired Then
            Me.BeginInvoke(Sub() SetCryptoActive(active))
            Return
        End If
        lblCryptoStatus.Text = If(active,
                                  "Crypto: ON",
                                  If(PM.IsConnected(_peerName), "Crypto: P2P", "Crypto: OFF"))
    End Sub

    ' ---------- Compat: anciens noms attendus par Form1 ----------
    Public Sub SetP2PState(connected As Boolean)
        If Me.InvokeRequired Then
            Me.BeginInvoke(Sub() SetP2PState(connected))
            Return
        End If
        UpdateStateText(connected)
    End Sub

    Public Sub SetCryptoState(active As Boolean)
        If Me.InvokeRequired Then
            Me.BeginInvoke(Sub() SetCryptoState(active))
            Return
        End If
        SetCryptoActive(active)
    End Sub

    Public Sub SetAuthState(verified As Boolean, Optional fingerprint As String = "")
        If Me.InvokeRequired Then
            Me.BeginInvoke(Sub() SetAuthState(verified, fingerprint))
            Return
        End If
        SetAuthStatus(verified, fingerprint)
    End Sub
    ' -------------------------------------------------------------

    ' --- API: ajouter un message dans l'historique ---
    Public Sub AppendMessage(senderName As String, message As String)
        If message Is Nothing Then Return
        If txtHistory.InvokeRequired Then
            txtHistory.Invoke(Sub() AppendMessage(senderName, message))
        Else
            ' "sender: " en gris
            txtHistory.SelectionColor = Color.DimGray
            txtHistory.AppendText(senderName & ": ")

            ' Si le message commence par "[P2P] ", on colore le tag
            If message.StartsWith("[P2P] ", StringComparison.Ordinal) Then
                txtHistory.SelectionColor = Color.SteelBlue
                txtHistory.AppendText("[P2P]")
                txtHistory.SelectionColor = Color.Black
                txtHistory.AppendText(" " & message.Substring(6))
            Else
                txtHistory.SelectionColor = Color.Black
                txtHistory.AppendText(message)
            End If

            txtHistory.AppendText(Environment.NewLine)
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

        ' ⚠️ NE PAS écho localement ici pour éviter les doublons.
        ' C'est Form1.SendPrivateMessage qui fait l'append (avec tag [P2P] si direct).
        txtInput.Clear()
        Try
            _sendAction?.Invoke(msg)
        Catch ex As Exception
            MessageBox.Show("Envoi privé échoué: " & ex.Message, "Chat privé", MessageBoxButtons.OK, MessageBoxIcon.Warning)
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
        ' MAJ du label crypto si non chiffrage app (affiche "P2P" quand connecté)
        If Not connected Then
            lblCryptoStatus.Text = "Crypto: OFF"
        ElseIf Not lblCryptoStatus.Text.StartsWith("Crypto: ON", StringComparison.Ordinal) Then
            lblCryptoStatus.Text = "Crypto: P2P"
        End If
    End Sub

    ' Utilitaire log thread-safe
    Private Sub SafeLog(line As String)
        If txtHistory.InvokeRequired Then
            txtHistory.Invoke(Sub() SafeLog(line))
        Else
            txtHistory.SelectionColor = Color.DimGray
            txtHistory.AppendText(line & Environment.NewLine)
            txtHistory.SelectionStart = txtHistory.TextLength
            txtHistory.ScrollToCaret()
        End If
    End Sub

    ' Bouton pour déclencher la négociation ICE
    Private Sub BtnP2P_Click(sender As Object, e As EventArgs)
        Try
            lblP2PState.Text = "P2P: en cours…"
            btnP2P.Enabled = False
            PM.StartP2P(_peerName, New String() {"stun:stun.l.google.com:19302"})
            SafeLog($"[P2P] Négociation démarrée vers {_peerName}")
        Catch ex As Exception
            btnP2P.Enabled = True
            SafeLog("[P2P] Démarrage échoué: " & ex.Message)
        End Try
    End Sub

    ' Nettoyage : on se désabonne des events statiques
    Protected Overrides Sub OnFormClosed(e As FormClosedEventArgs)
        MyBase.OnFormClosed(e)
        RemoveHandler PM.OnLog, AddressOf OnP2PLog
        RemoveHandler PM.OnP2PState, AddressOf OnP2PState
        ' (on n'a jamais souscrit à OnP2PText ici)
    End Sub

End Class
