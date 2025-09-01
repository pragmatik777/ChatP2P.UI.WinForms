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

    ' mémorise les noms pour maj % propre
    Private _sendName As String = ""
    Private _recvName As String = ""

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

    ' Statuts (Fill)
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

    ' ==== Progress (dans panelBottom) ====
    Private Const BaseBottomHeight As Integer = 44
    Private Const ProgHeight As Integer = 64

    Private ReadOnly panelBottom As New Panel() With {.Dock = DockStyle.Bottom, .Height = BaseBottomHeight}
    Private ReadOnly txt As New TextBox() With {.Dock = DockStyle.Fill, .Font = New Font("Segoe UI", 10.0F)}
    Private ReadOnly btnSend As New Button() With {.Text = "Envoyer", .Dock = DockStyle.Right, .Width = 110}
    Private ReadOnly btnSendFile As New Button() With {.Text = "Envoyer fichier", .Dock = DockStyle.Right, .Width = 140}

    ' 👉 Dock en bas pour être SOUS la zone de saisie
    Private ReadOnly pnlProg As New Panel() With {.Dock = DockStyle.Bottom, .Height = ProgHeight, .Visible = False, .Padding = New Padding(6, 4, 6, 4)}
    Private ReadOnly lblSendProg As New Label() With {.AutoSize = False, .Height = 16, .Dock = DockStyle.Top, .Text = "", .ForeColor = Color.Gold}
    Private ReadOnly pbSend As New ProgressBar() With {.Dock = DockStyle.Top, .Height = 12, .Minimum = 0, .Maximum = 100, .Visible = False}
    Private ReadOnly lblRecvProg As New Label() With {.AutoSize = False, .Height = 16, .Dock = DockStyle.Top, .Text = "", .ForeColor = Color.DeepSkyBlue}
    Private ReadOnly pbRecv As New ProgressBar() With {.Dock = DockStyle.Top, .Height = 12, .Minimum = 0, .Maximum = 100, .Visible = False}

    ' ==== Events publics ====
    Public Event ScrollTopReached()
    Public Event PurgeRequested()
    Public Event StartP2PRequested()

    ' --- Nouveau ctor (texte + fichier)
    Public Sub New(myName As String, peer As String, sendCb As Action(Of String), sendFileCb As Action)
        _myName = myName
        _peer = peer
        _send = sendCb
        _sendFile = sendFileCb

        Me.Text = $"Chat privé avec {peer}"
        Me.Width = 700
        Me.Height = 560
        Me.MinimumSize = New Size(520, 360)

        ' Top: statuts + actions
        flStatus.Controls.Add(lblP2P)
        flStatus.Controls.Add(lblCrypto)
        flStatus.Controls.Add(lblAuth)
        panelTop.Controls.Add(btnStartP2P)
        panelTop.Controls.Add(btnPurge)
        panelTop.Controls.Add(flStatus)

        ' Progress panel (ordre: send-label, send-bar, recv-label, recv-bar)
        pnlProg.Controls.Add(pbRecv)
        pnlProg.Controls.Add(lblRecvProg)
        pnlProg.Controls.Add(pbSend)
        pnlProg.Controls.Add(lblSendProg)

        ' Bottom: input (Fill) + boutons (Right) + progress (Bottom)
        panelBottom.Controls.Add(btnSend)
        panelBottom.Controls.Add(btnSendFile)
        panelBottom.Controls.Add(txt)
        panelBottom.Controls.Add(pnlProg)

        ' Ajout global
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

    ' --- Overload rétro-compatible (3 paramètres)
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
            _send(msg)
        Catch
            ' ignore; le parent loguera
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
            ' ignore; Form1 loguera
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

    ' ===== Progress API (thread-safe) =====
    Public Sub StartSendProgress(fileName As String, expectedBytes As Long)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() StartSendProgress(fileName, expectedBytes)) : Return
        End If
        _sendName = fileName
        lblSendProg.Text = "Envoi : " & fileName
        pbSend.Value = 0
        pbSend.Visible = True
        RefreshProgressPanel()
    End Sub

    Public Sub UpdateSendProgress(sent As Long, expected As Long)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() UpdateSendProgress(sent, expected)) : Return
        End If
        Dim p As Integer = 0
        If expected > 0 Then p = CInt((sent * 100L) \ expected)
        p = Math.Max(0, Math.Min(100, p))
        pbSend.Value = p
        lblSendProg.Text = $"Envoi : {_sendName} — {p}%"
        RefreshProgressPanel()
    End Sub

    Public Sub EndSendProgress()
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() EndSendProgress()) : Return
        End If
        pbSend.Visible = False
        lblSendProg.Text = ""
        _sendName = ""
        RefreshProgressPanel()
    End Sub

    Public Sub StartRecvProgress(fileName As String, expectedBytes As Long)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() StartRecvProgress(fileName, expectedBytes)) : Return
        End If
        _recvName = fileName
        lblRecvProg.Text = "Réception : " & fileName
        pbRecv.Value = 0
        pbRecv.Visible = True
        RefreshProgressPanel()
    End Sub

    Public Sub UpdateRecvProgress(received As Long, expected As Long)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() UpdateRecvProgress(received, expected)) : Return
        End If
        Dim p As Integer = 0
        If expected > 0 Then p = CInt((received * 100L) \ expected)
        p = Math.Max(0, Math.Min(100, p))
        pbRecv.Value = p
        lblRecvProg.Text = $"Réception : {_recvName} — {p}%"
        RefreshProgressPanel()
    End Sub

    Public Sub EndRecvProgress()
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() EndRecvProgress()) : Return
        End If
        pbRecv.Visible = False
        lblRecvProg.Text = ""
        _recvName = ""
        RefreshProgressPanel()
    End Sub

    Private Sub RefreshProgressPanel()
        ' Affiche le panneau s’il y a au moins une barre visible
        Dim anyVisible = (pbSend.Visible OrElse pbRecv.Visible)
        pnlProg.Visible = anyVisible
        panelBottom.Height = BaseBottomHeight + If(anyVisible, ProgHeight, 0)
    End Sub

    Private Sub PrivateChatForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' rien
    End Sub
End Class
