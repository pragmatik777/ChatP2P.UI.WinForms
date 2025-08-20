Imports System.Windows.Forms

Public Class PrivateChatForm
    Inherits Form

    Private ReadOnly _myName As String
    Private _peerName As String
    Private ReadOnly _sendAction As Action(Of String)

    ' UI
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
        Me.Width = 520
        Me.Height = 420
        Me.MinimizeBox = True
        Me.MaximizeBox = True

        ' --- Controls ---
        txtHistory = New RichTextBox() With {
            .ReadOnly = True,
            .DetectUrls = True,
            .HideSelection = False,
            .Dock = DockStyle.Fill
        }

        txtInput = New TextBox() With {
            .Dock = DockStyle.Bottom
        }

        btnSend = New Button() With {
            .Text = "Envoyer",
            .Dock = DockStyle.Bottom,
            .Height = 32
        }

        ' Order: history (fill), send (bottom), input (bottom)
        Me.Controls.Add(txtHistory)
        Me.Controls.Add(btnSend)
        Me.Controls.Add(txtInput)

        ' Handlers
        AddHandler btnSend.Click, AddressOf BtnSend_Click
        AddHandler txtInput.KeyDown, AddressOf TxtInput_KeyDown
    End Sub

    ' --- API: changer le nom du peer (ex: après NAME:) ---
    Public Sub UpdatePeerName(newName As String)
        If String.IsNullOrWhiteSpace(newName) Then Return
        _peerName = newName
        Try
            Me.Text = $"Chat privé avec {_peerName}"
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

    ' --- Envoi via bouton ---
    Private Sub BtnSend_Click(sender As Object, e As EventArgs)
        SendCurrent()
    End Sub

    ' --- Envoi via ENTER dans la zone de saisie ---
    Private Sub TxtInput_KeyDown(sender As Object, e As KeyEventArgs)
        If e.KeyCode = Keys.Enter Then
            e.SuppressKeyPress = True
            SendCurrent()
        End If
    End Sub

    Private Sub SendCurrent()
        Dim msg = txtInput.Text.Trim()
        If msg = "" Then Return

        ' Affiche localement
        AppendMessage(_myName, msg)
        txtInput.Clear()

        ' Délégué vers Form1 (réseau)
        Try
            _sendAction?.Invoke(msg)
        Catch ex As Exception
            MessageBox.Show("Envoi privé échoué: " & ex.Message, "Chat privé", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub
End Class
