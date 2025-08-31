' ChatP2P.UI.WinForms/PrivateChatForm.vb
Option Strict On
Imports System.Windows.Forms
Imports System.Drawing

Public Class PrivateChatForm
    Inherits Form

    Private ReadOnly _myName As String
    Private ReadOnly _peer As String
    Private ReadOnly _send As Action(Of String)
    Private ReadOnly _sendFile As Action

    ' ==== UI ====
    Private ReadOnly rtb As New RichTextBox() With {
        .Dock = DockStyle.Fill,
        .ReadOnly = True,
        .DetectUrls = False,
        .BorderStyle = BorderStyle.None,
        .HideSelection = False,
        .BackColor = Color.Black,
        .ForeColor = Color.Lime,
        .Font = New Font("Consolas", 10.0F)
    }

    Private ReadOnly panelTop As New Panel() With {.Dock = DockStyle.Top, .Height = 32}

    ' Le panneau de statuts occupe tout l’espace restant (Fill)
    Private ReadOnly flStatus As New FlowLayoutPanel() With {
        .Dock = DockStyle.Fill,
        .AutoSize = False,
        .WrapContents = False,
        .FlowDirection = FlowDirection.LeftToRight,
        .Padding = New Padding(6, 4, 6, 4)
    }

    Private ReadOnly lblP2P As New Label() With {.AutoSize = True, .Text = "P2P: —", .Margin = New Padding(6, 6, 12, 6)}
    Private ReadOnly lblCrypto As New Label() With {.AutoSize = True, .Text = "Crypto: OFF", .Margin = New Padding(0, 6, 12, 6)}
    Private ReadOnly lblAuth As New Label() With {.AutoSize = True, .Text = "Auth: ❌", .Margin = New Padding(0, 6, 12, 6)}

    Private ReadOnly btnStartP2P As New Button() With {.Text = "Démarrer P2P", .Dock = DockStyle.Right, .Width = 120}
    Private ReadOnly btnPurge As New Button() With {.Text = "🗑 Purger", .Dock = DockStyle.Right, .Width = 90}

    Private ReadOnly panelBottom As New Panel() With {.Dock = DockStyle.Bottom, .Height = 44}
    Private ReadOnly txt As New TextBox() With {.Dock = DockStyle.Fill, .Font = New Font("Segoe UI", 10.0F)}
    Private ReadOnly btnSend As New Button() With {.Text = "Envoyer", .Dock = DockStyle.Right, .Width = 110}
    Private ReadOnly btnSendFile As New Button() With {.Text = "Envoyer fichier", .Dock = DockStyle.Right, .Width = 140}

    ' ==== Events publics ====
    Public Event ScrollTopReached()
    Public Event PurgeRequested()
    Public Event StartP2PRequested()

    ' --- Nouveau ctor à 4 paramètres (texte + fichier)
    Public Sub New(myName As String, peer As String, sendCb As Action(Of String), sendFileCb As Action)
        _myName = myName
        _peer = peer
        _send = sendCb
        _sendFile = sendFileCb

        Me.Text = $"Chat privé avec {peer}"
        Me.Width = 700
        Me.Height = 520
        Me.MinimumSize = New Size(520, 360)

        ' Top: statuts (Fill) + actions dockées à droite
        flStatus.Controls.Add(lblP2P)
        flStatus.Controls.Add(lblCrypto)
        flStatus.Controls.Add(lblAuth)

        ' IMPORTANT : ajouter d'abord les boutons (Dock Right), puis flStatus (Dock Fill)
        panelTop.Controls.Add(btnStartP2P)
        panelTop.Controls.Add(btnPurge)
        panelTop.Controls.Add(flStatus)

        ' Bottom: ordre Dock Right (btnSendFile puis btnSend) puis Fill (txt)
        panelBottom.Controls.Add(btnSend)      ' Right (le plus à droite)
        panelBottom.Controls.Add(btnSendFile)  ' Right (à gauche de Envoyer)
        panelBottom.Controls.Add(txt)          ' Fill

        ' Ordre d'ajout global
        Me.Controls.Add(rtb)
        Me.Controls.Add(panelBottom)
        Me.Controls.Add(panelTop)

        ' Enter = envoyer
        Me.AcceptButton = btnSend

        ' Handlers
        AddHandler btnSend.Click, AddressOf OnSend
        AddHandler btnSendFile.Click, AddressOf OnSendFile
        AddHandler txt.KeyDown, AddressOf OnTxtKeyDown
        AddHandler btnPurge.Click, Sub() RaiseEvent PurgeRequested()
        AddHandler btnStartP2P.Click, Sub() RaiseEvent StartP2PRequested()
        AddHandler rtb.VScroll, AddressOf OnRtbScroll
        AddHandler rtb.Resize, AddressOf OnRtbScroll
    End Sub

    ' --- Overload rétro-compatible (3 paramètres) -> appelle une no-op
    Public Sub New(myName As String, peer As String, sendCb As Action(Of String))
        Me.New(myName, peer, sendCb, AddressOf NoopSendFile)
    End Sub

    Private Shared Sub NoopSendFile()
        ' no-op
    End Sub

    ' ===== Scroll haut détecté =====
    Private Sub OnRtbScroll(sender As Object, e As EventArgs)
        Dim firstCharIdx As Integer = rtb.GetCharIndexFromPosition(New Point(2, 2))
        Dim firstLine As Integer = rtb.GetLineFromCharIndex(firstCharIdx)
        If firstLine <= 0 Then
            RaiseEvent ScrollTopReached()
        End If
    End Sub

    ' ===== Envoi texte =====
    Private Sub OnSend(sender As Object, e As EventArgs)
        Dim msg = txt.Text.Trim()
        If msg.Length = 0 Then Return
        Try
            _send(msg) ' -> Form1 décidera P2P/RELAY et fera l’écho local
        Catch
            ' on ignore (Form1 loguera si besoin)
        End Try
        txt.Clear()
        txt.Focus()
    End Sub

    Private Sub OnTxtKeyDown(sender As Object, e As KeyEventArgs)
        If e.KeyCode = Keys.Enter Then
            e.SuppressKeyPress = True
            OnSend(Nothing, EventArgs.Empty)
        End If
    End Sub

    ' ===== Envoi fichier =====
    Private Sub OnSendFile(sender As Object, e As EventArgs)
        Try
            If _sendFile IsNot Nothing Then _sendFile()
        Catch
            ' ignore; Form1 loguera les erreurs
        End Try
    End Sub

    ' ===== Ajout de messages =====
    Public Sub AppendMessage(sender As String, body As String)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() AppendMessage(sender, body))
            Return
        End If

        Dim line As String = $"{sender}: {body}{Environment.NewLine}"
        Dim startLine As Integer = rtb.TextLength
        rtb.SelectionStart = startLine
        rtb.SelectionLength = 0
        rtb.SelectionColor = Color.Lime
        rtb.AppendText(line)

        Dim bodyStartInRtb As Integer = startLine + sender.Length + 2 ' "sender: "
        ColorizeTag(body, "[P2P]", Color.DarkOrange, bodyStartInRtb)
        ColorizeTag(body, "[RELAY]", Color.DeepSkyBlue, bodyStartInRtb)

        rtb.SelectionStart = rtb.TextLength
        rtb.SelectionLength = 0
        rtb.SelectionColor = Color.Lime
        rtb.ScrollToCaret()
    End Sub

    Private Sub ColorizeTag(body As String, tag As String, color As Color, baseInRtb As Integer)
        Dim idxInBody As Integer = body.IndexOf(tag, StringComparison.Ordinal)
        If idxInBody < 0 Then Return
        Dim tagStartInRtb As Integer = baseInRtb + idxInBody

        Dim selStart = rtb.SelectionStart
        Dim selLen = rtb.SelectionLength
        Dim selColor = rtb.SelectionColor
        Dim selFont = rtb.SelectionFont

        rtb.Select(tagStartInRtb, tag.Length)
        rtb.SelectionColor = color
        rtb.SelectionFont = New Font(rtb.Font, FontStyle.Bold)

        ' restore
        rtb.Select(selStart + selLen, 0)
        rtb.SelectionColor = selColor
        rtb.SelectionFont = selFont
    End Sub

    Public Sub ClearMessages()
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() ClearMessages())
            Return
        End If
        rtb.Clear()
    End Sub

    ' ===== Statuts =====
    Public Sub SetP2PState(connected As Boolean)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() SetP2PState(connected)) : Return
        End If
        lblP2P.Text = If(connected, "P2P: CONNECTÉ", "P2P: —")
        lblP2P.ForeColor = If(connected, Color.ForestGreen, Color.DimGray)
    End Sub

    Public Sub SetCryptoState(active As Boolean)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() SetCryptoState(active)) : Return
        End If
        lblCrypto.Text = If(active, "Crypto: ON", "Crypto: OFF")
        lblCrypto.ForeColor = If(active, Color.MediumBlue, Color.DimGray)
    End Sub

    Public Sub SetAuthState(verified As Boolean)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() SetAuthState(verified)) : Return
        End If
        lblAuth.Text = If(verified, "Auth: ✅", "Auth: ❌")
        lblAuth.ForeColor = If(verified, Color.DarkGreen, Color.Firebrick)
    End Sub

    Private Sub PrivateChatForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' rien
    End Sub
End Class
