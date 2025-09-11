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

    ' ==== UI - Style WhatsApp ====
    Private ReadOnly messagesPanel As New Panel() With {
        .Dock = DockStyle.Fill,
        .BackColor = Color.FromArgb(11, 20, 26), ' Couleur sombre WhatsApp
        .AutoScroll = True,
        .Padding = New Padding(10, 5, 10, 5)
    }
    
    Private ReadOnly messagesContainer As New FlowLayoutPanel() With {
        .FlowDirection = FlowDirection.TopDown,
        .WrapContents = False,
        .AutoSize = True,
        .AutoSizeMode = AutoSizeMode.GrowAndShrink,
        .Dock = DockStyle.Top
    }
    
    ' Cache pour éviter de dupliquer les séparateurs de date
    Private ReadOnly _datesSeen As New HashSet(Of String)
    
    ' RichTextBox gardé en fallback pour compatibilité
    Private ReadOnly rtb As New RichTextBox() With {
        .Dock = DockStyle.Fill,
        .ReadOnly = True,
        .DetectUrls = False,
        .BorderStyle = BorderStyle.None,
        .HideSelection = False,
        .BackColor = Color.Black,
        .ForeColor = Color.Lime,
        .Font = New Font("Consolas", 10.0F),
        .Visible = False
    }

    Private ReadOnly panelTop As New Panel() With {.Dock = DockStyle.Top, .Height = 32}

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

    ' ==== Bottom (saisie + barre unique) ====
    Private Const BaseBottomHeight As Integer = 44
    Private Const ProgHeight As Integer = 40

    Private ReadOnly panelBottom As New Panel() With {.Dock = DockStyle.Bottom, .Height = BaseBottomHeight}
    Private ReadOnly txt As New TextBox() With {.Dock = DockStyle.Fill, .Font = New Font("Segoe UI", 10.0F)}
    Private ReadOnly btnSend As New Button() With {.Text = "Envoyer", .Dock = DockStyle.Right, .Width = 110}
    Private ReadOnly btnSendFile As New Button() With {.Text = "Envoyer fichier", .Dock = DockStyle.Right, .Width = 140}

    ' Panneau de progression unique (Dock=Top dans panelBottom)
    Private ReadOnly pnlProg As New Panel() With {.Dock = DockStyle.Top, .Height = ProgHeight, .Visible = False, .Padding = New Padding(6, 4, 6, 4), .BackColor = Color.FromArgb(24, 24, 24)}
    Private ReadOnly lblProg As New Label() With {.AutoSize = False, .Height = 16, .Dock = DockStyle.Top, .Text = "", .ForeColor = Color.Gold}
    Private ReadOnly pbProg As New ProgressBar() With {.Dock = DockStyle.Top, .Height = 12, .Minimum = 0, .Maximum = 100, .Visible = True}

    ' ==== Events publics ====
    Public Event ScrollTopReached()
    Public Event PurgeRequested()
    Public Event StartP2PRequested()

    ' Etat de la barre unique
    Private Enum TransferMode
        None = 0
        Sending = 1
        Receiving = 2
    End Enum
    Private _mode As TransferMode = TransferMode.None
    Private _curName As String = String.Empty
    Private _transferStartTime As DateTime
    Private _lastSentBytes As Long = 0
    Private _lastSpeedUpdate As DateTime

    ' --- Ctor (texte + fichier)
    Public Sub New(myName As String, peer As String, sendCb As Action(Of String), sendFileCb As Action)
        _myName = myName
        _peer = peer
        _send = sendCb
        _sendFile = sendFileCb

        Me.Text = $"Chat privé avec {peer}"
        Me.Width = 700
        Me.Height = 560
        Me.MinimumSize = New Size(520, 360)

        ' Top
        flStatus.Controls.Add(lblP2P)
        flStatus.Controls.Add(lblCrypto)
        flStatus.Controls.Add(lblAuth)
        panelTop.Controls.Add(btnStartP2P)
        panelTop.Controls.Add(btnPurge)
        panelTop.Controls.Add(flStatus)

        ' Progress unique
        pnlProg.Controls.Add(pbProg)
        pnlProg.Controls.Add(lblProg)

        ' Bottom: boutons (Right) + input (Fill) + progress (Top)
        panelBottom.Controls.Add(btnSend)
        panelBottom.Controls.Add(btnSendFile)
        panelBottom.Controls.Add(txt)
        panelBottom.Controls.Add(pnlProg) ' ← ajouté en dernier pour être docké en premier (Top) lors du layout

        ' Setup du nouveau système de messages WhatsApp
        messagesPanel.Controls.Add(messagesContainer)
        
        ' Ajout global - nouveau style WhatsApp
        Me.Controls.Add(messagesPanel)
        Me.Controls.Add(rtb) ' Gardé pour fallback
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

    ' Overload rétro (3 paramètres)
    Public Sub New(myName As String, peer As String, sendCb As Action(Of String))
        Me.New(myName, peer, sendCb, AddressOf NoopSendFile)
    End Sub
    Private Shared Sub NoopSendFile()
    End Sub

    ' ===== Scroll haut détecté =====
    Private Sub OnRtbScroll(sender As Object, e As EventArgs)
        Dim firstCharIdx As Integer = rtb.GetCharIndexFromPosition(New Point(2, 2))
        Dim firstLine As Integer = rtb.GetLineFromCharIndex(firstCharIdx)
        If firstLine <= 0 Then RaiseEvent ScrollTopReached()
    End Sub

    ' ===== Envoi texte =====
    Private Sub OnSend(sender As Object, e As EventArgs)
        Dim msg = txt.Text.Trim()
        If msg.Length = 0 Then Return
        Try
            _send(msg)
        Catch
            ' parent loguera
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

    ' ===== Envoi fichier (callback parent) =====
    Private Sub OnSendFile(sender As Object, e As EventArgs)
        Try
            If _sendFile IsNot Nothing Then _sendFile()
        Catch
            ' Form1 loguera
        End Try
    End Sub

    ' ===== Ajout de messages =====
    Public Sub AppendMessage(sender As String, body As String)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() AppendMessage(sender, body))
            Return
        End If

        Try
            ' Nouveau système WhatsApp avec groupement par date
            AppendMessageWhatsApp(sender, body)
        Catch ex As Exception
            ' Fallback sur l'ancien système si erreur
            AppendMessageLegacy(sender, body)
        End Try
    End Sub

    ''' <summary>
    ''' Nouveau système de messages style WhatsApp avec groupement par date
    ''' </summary>
    Private Sub AppendMessageWhatsApp(sender As String, body As String)
        ' Générer la date d'aujourd'hui pour groupement
        Dim today As DateTime = DateTime.Now
        Dim dateKey As String = today.ToString("yyyy-MM-dd")
        Dim dateDisplay As String
        
        ' Format de date style WhatsApp
        If today.Date = DateTime.Today Then
            dateDisplay = "Aujourd'hui"
        ElseIf today.Date = DateTime.Today.AddDays(-1) Then
            dateDisplay = "Hier"
        Else
            dateDisplay = today.ToString("dd MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture)
        End If
        
        ' Ajouter un séparateur de date si c'est une nouvelle date
        If Not _datesSeen.Contains(dateKey) Then
            _datesSeen.Add(dateKey)
            Dim dateSeparator = CreateDateSeparator(dateDisplay)
            messagesContainer.Controls.Add(dateSeparator)
        End If
        
        ' Déterminer si c'est un message sortant (de nous)
        Dim isOutgoing As Boolean = sender.Equals(_myName, StringComparison.OrdinalIgnoreCase) OrElse 
                                   sender.Equals("Me", StringComparison.OrdinalIgnoreCase)
        
        ' Créer et ajouter la bulle de message
        Dim messageBubble = CreateMessageBubble(sender, body, isOutgoing)
        messagesContainer.Controls.Add(messageBubble)
        
        ' Auto-scroll vers le bas
        messagesPanel.ScrollControlIntoView(messageBubble)
    End Sub

    ''' <summary>
    ''' Ancien système pour fallback
    ''' </summary>
    Private Sub AppendMessageLegacy(sender As String, body As String)
        rtb.Visible = True
        messagesPanel.Visible = False
        
        Dim line As String = $"{sender}: {body}{Environment.NewLine}"
        Dim startLine As Integer = rtb.TextLength
        rtb.SelectionStart = startLine
        rtb.SelectionLength = 0
        rtb.SelectionColor = Color.Lime
        rtb.AppendText(line)

        Dim bodyStartInRtb As Integer = startLine + sender.Length + 2
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

    ''' <summary>
    ''' Crée un séparateur de date style WhatsApp
    ''' </summary>
    Private Function CreateDateSeparator(dateText As String) As Control
        Dim separatorPanel As New Panel() With {
            .Height = 40,
            .Width = messagesContainer.Width - 20,
            .Margin = New Padding(0, 10, 0, 5)
        }
        
        Dim dateLabel As New Label() With {
            .Text = dateText,
            .BackColor = Color.FromArgb(42, 57, 66), ' Gris sombre WhatsApp
            .ForeColor = Color.FromArgb(241, 241, 242), ' Blanc cassé
            .Font = New Font("Segoe UI", 9.0F, FontStyle.Regular),
            .TextAlign = ContentAlignment.MiddleCenter,
            .AutoSize = False,
            .Height = 24,
            .Padding = New Padding(12, 4, 12, 4)
        }
        
        ' Centrer le label dans le panel
        dateLabel.Width = TextRenderer.MeasureText(dateText, dateLabel.Font).Width + 24
        dateLabel.Left = (separatorPanel.Width - dateLabel.Width) \ 2
        dateLabel.Top = (separatorPanel.Height - dateLabel.Height) \ 2
        
        ' Arrondir les coins du label
        Dim cornerRadius As Integer = 12
        Dim path As Drawing2D.GraphicsPath = CreateRoundedRectanglePath(New Rectangle(0, 0, dateLabel.Width, dateLabel.Height), cornerRadius)
        dateLabel.Region = New Region(path)
        
        separatorPanel.Controls.Add(dateLabel)
        Return separatorPanel
    End Function
    
    ''' <summary>
    ''' Crée un contrôle de message style WhatsApp
    ''' </summary>
    Private Function CreateMessageBubble(sender As String, body As String, isOutgoing As Boolean) As Control
        Dim messagePanel As New Panel() With {
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .Width = messagesContainer.Width - 40,
            .Margin = New Padding(If(isOutgoing, 60, 0), 2, If(isOutgoing, 0, 60), 2),
            .Padding = New Padding(12, 8, 12, 8)
        }
        
        ' Couleurs selon type de message
        Dim bubbleColor As Color
        Dim textColor As Color = Color.White
        
        If body.Contains("[P2P🔒]") Then
            bubbleColor = Color.FromArgb(0, 120, 60) ' Vert sécurisé pour PQC
            body = body.Replace("[P2P🔒] ", "🔒 ")
        ElseIf body.Contains("[P2P]") Then
            bubbleColor = Color.FromArgb(0, 92, 75) ' Vert WhatsApp pour P2P
            body = body.Replace("[P2P] ", "")
        ElseIf body.Contains("[RELAY]") Then
            bubbleColor = Color.FromArgb(66, 103, 137) ' Bleu pour RELAY
            body = body.Replace("[RELAY] ", "")
        Else
            bubbleColor = If(isOutgoing, Color.FromArgb(0, 92, 75), Color.FromArgb(42, 57, 66))
        End If
        
        messagePanel.BackColor = bubbleColor
        
        ' Label pour le message
        Dim messageLabel As New Label() With {
            .Text = body,
            .ForeColor = textColor,
            .Font = New Font("Segoe UI", 10.0F, FontStyle.Regular),
            .AutoSize = True,
            .MaximumSize = New Size(messagesContainer.Width - 120, 0),
            .Padding = New Padding(0)
        }
        
        ' Label pour l'expéditeur (si pas nous)
        If Not isOutgoing Then
            Dim senderLabel As New Label() With {
                .Text = sender,
                .ForeColor = Color.FromArgb(255, 213, 79), ' Jaune WhatsApp pour nom
                .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold),
                .AutoSize = True,
                .Location = New Point(0, 0)
            }
            
            messagePanel.Controls.Add(senderLabel)
            messageLabel.Location = New Point(0, senderLabel.Height + 2)
        Else
            messageLabel.Location = New Point(0, 0)
        End If
        
        messagePanel.Controls.Add(messageLabel)
        
        ' Ajuster la taille du panel
        Dim totalHeight As Integer = messageLabel.Location.Y + messageLabel.Height + messagePanel.Padding.Vertical
        messagePanel.Height = totalHeight
        
        ' Arrondir les coins de la bulle
        Dim cornerRadius As Integer = 12
        Dim bubblePath As Drawing2D.GraphicsPath = CreateRoundedRectanglePath(New Rectangle(0, 0, messagePanel.Width, messagePanel.Height), cornerRadius)
        messagePanel.Region = New Region(bubblePath)
        
        Return messagePanel
    End Function
    
    ''' <summary>
    ''' Crée un chemin pour un rectangle aux coins arrondis
    ''' </summary>
    Private Function CreateRoundedRectanglePath(rect As Rectangle, radius As Integer) As Drawing2D.GraphicsPath
        Dim path As New Drawing2D.GraphicsPath()
        Dim diameter As Integer = radius * 2
        
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90)
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90)
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90)
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90)
        path.CloseFigure()
        
        Return path
    End Function

    Public Sub ClearMessages()
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() ClearMessages())
            Return
        End If
        
        ' Vider le nouveau système WhatsApp
        messagesContainer.Controls.Clear()
        _datesSeen.Clear()
        
        ' Vider aussi l'ancien système
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

    ' ===== Barre unique (thread-safe) =====
    Public Sub StartSendProgress(fileName As String, expectedBytes As Long)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() StartSendProgress(fileName, expectedBytes)) : Return
        End If
        _mode = TransferMode.Sending
        _curName = fileName
        _transferStartTime = DateTime.Now
        _lastSentBytes = 0
        _lastSpeedUpdate = DateTime.Now
        pbProg.Value = 0
        lblProg.Text = "Envoi : " & fileName & " — 0% | 0 KB/s"
        ShowProgressPanel(True)
    End Sub

    Public Sub UpdateSendProgress(sent As Long, expected As Long)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() UpdateSendProgress(sent, expected)) : Return
        End If
        If _mode <> TransferMode.Sending Then Return
        Dim p As Integer = 0
        If expected > 0 Then p = CInt((sent * 100L) \ expected)
        p = Math.Max(0, Math.Min(100, p))
        pbProg.Value = p
        
        ' Calcul du débit (mise à jour toutes les 500ms minimum)
        Dim now = DateTime.Now
        Dim speedText = "0 KB/s"
        If (now - _lastSpeedUpdate).TotalMilliseconds >= 500 Then
            Dim elapsedTotal = (now - _transferStartTime).TotalSeconds
            If elapsedTotal > 0 Then
                Dim speedBytesPerSec = sent / elapsedTotal
                If speedBytesPerSec >= 1048576 Then ' >= 1MB/s
                    speedText = $"{speedBytesPerSec / 1048576:F1} MB/s"
                ElseIf speedBytesPerSec >= 1024 Then ' >= 1KB/s
                    speedText = $"{speedBytesPerSec / 1024:F0} KB/s"
                Else
                    speedText = $"{speedBytesPerSec:F0} B/s"
                End If
            End If
            _lastSpeedUpdate = now
        End If
        
        lblProg.Text = $"Envoi : {_curName} — {p}% | {speedText}"
    End Sub

    Public Sub EndSendProgress()
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() EndSendProgress()) : Return
        End If
        If _mode = TransferMode.Sending Then
            _mode = TransferMode.None
            _curName = ""
            ShowProgressPanel(False)
        End If
    End Sub

    Public Sub StartRecvProgress(fileName As String, expectedBytes As Long)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() StartRecvProgress(fileName, expectedBytes)) : Return
        End If
        _mode = TransferMode.Receiving
        _curName = fileName
        _transferStartTime = DateTime.Now
        _lastSentBytes = 0
        _lastSpeedUpdate = DateTime.Now
        pbProg.Value = 0
        lblProg.Text = "Réception : " & fileName & " — 0% | 0 KB/s"
        ShowProgressPanel(True)
    End Sub

    Public Sub UpdateRecvProgress(received As Long, expected As Long)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() UpdateRecvProgress(received, expected)) : Return
        End If
        If _mode <> TransferMode.Receiving Then Return
        Dim p As Integer = 0
        If expected > 0 Then p = CInt((received * 100L) \ expected)
        p = Math.Max(0, Math.Min(100, p))
        pbProg.Value = p
        
        ' Calcul du débit (mise à jour toutes les 500ms minimum)
        Dim now = DateTime.Now
        Dim speedText = "0 KB/s"
        If (now - _lastSpeedUpdate).TotalMilliseconds >= 500 Then
            Dim elapsedTotal = (now - _transferStartTime).TotalSeconds
            If elapsedTotal > 0 Then
                Dim speedBytesPerSec = received / elapsedTotal
                If speedBytesPerSec >= 1048576 Then ' >= 1MB/s
                    speedText = $"{speedBytesPerSec / 1048576:F1} MB/s"
                ElseIf speedBytesPerSec >= 1024 Then ' >= 1KB/s
                    speedText = $"{speedBytesPerSec / 1024:F0} KB/s"
                Else
                    speedText = $"{speedBytesPerSec:F0} B/s"
                End If
            End If
            _lastSpeedUpdate = now
        End If
        
        lblProg.Text = $"Réception : {_curName} — {p}% | {speedText}"
    End Sub

    Public Sub EndRecvProgress()
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(Sub() EndRecvProgress()) : Return
        End If
        If _mode = TransferMode.Receiving Then
            _mode = TransferMode.None
            _curName = ""
            ShowProgressPanel(False)
        End If
    End Sub

    Private Sub ShowProgressPanel(visible As Boolean)
        pnlProg.Visible = visible
        panelBottom.Height = BaseBottomHeight + If(visible, ProgHeight, 0)
        pnlProg.Invalidate()
        panelBottom.Invalidate()
        Me.PerformLayout()
    End Sub

    Private Sub PrivateChatForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' rien
    End Sub
End Class
