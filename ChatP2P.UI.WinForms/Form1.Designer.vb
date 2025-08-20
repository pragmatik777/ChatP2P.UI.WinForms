<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class Form1
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Required by the Windows Form Designer
    Private components As System.ComponentModel.IContainer

    'NOTE: The following procedure is required by the Windows Form Designer
    'It can be modified using the Windows Form Designer.  
    'Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        Dim resources As System.ComponentModel.ComponentResourceManager = New System.ComponentModel.ComponentResourceManager(GetType(Form1))
        GroupBox1 = New GroupBox()
        txtFilePort = New TextBox()
        Label5 = New Label()
        btnStartHost = New Button()
        txtLocalPort = New TextBox()
        Label1 = New Label()
        chkVerbose = New CheckBox()
        GroupBox2 = New GroupBox()
        btnConnect = New Button()
        txtRemotePort = New TextBox()
        Label3 = New Label()
        txtRemoteIp = New TextBox()
        Label2 = New Label()
        GroupBox3 = New GroupBox()
        btnChooseRecvFolder = New Button()
        lblRecvProgress = New Label()
        btnSendFile = New Button()
        pbSend = New ProgressBar()
        lblSendProgress = New Label()
        pbRecv = New ProgressBar()
        lstPeers = New ListBox()
        Label4 = New Label()
        txtName = New TextBox()
        btnSend = New Button()
        txtMessage = New TextBox()
        txtLog = New RichTextBox()
        GroupBox1.SuspendLayout()
        GroupBox2.SuspendLayout()
        GroupBox3.SuspendLayout()
        SuspendLayout()
        ' 
        ' GroupBox1
        ' 
        GroupBox1.Controls.Add(txtFilePort)
        GroupBox1.Controls.Add(Label5)
        GroupBox1.Controls.Add(btnStartHost)
        GroupBox1.Controls.Add(txtLocalPort)
        GroupBox1.Controls.Add(Label1)
        GroupBox1.Location = New Point(12, 32)
        GroupBox1.Name = "GroupBox1"
        GroupBox1.Size = New Size(367, 139)
        GroupBox1.TabIndex = 0
        GroupBox1.TabStop = False
        GroupBox1.Text = "Local Host"
        ' 
        ' txtFilePort
        ' 
        txtFilePort.Location = New Point(113, 80)
        txtFilePort.Name = "txtFilePort"
        txtFilePort.Size = New Size(130, 31)
        txtFilePort.TabIndex = 4
        ' 
        ' Label5
        ' 
        Label5.AutoSize = True
        Label5.Location = New Point(21, 76)
        Label5.Name = "Label5"
        Label5.Size = New Size(75, 25)
        Label5.TabIndex = 3
        Label5.Text = "File Port"
        ' 
        ' btnStartHost
        ' 
        btnStartHost.Location = New Point(249, 76)
        btnStartHost.Name = "btnStartHost"
        btnStartHost.Size = New Size(101, 34)
        btnStartHost.TabIndex = 2
        btnStartHost.Text = "Start Host"
        btnStartHost.UseVisualStyleBackColor = True
        ' 
        ' txtLocalPort
        ' 
        txtLocalPort.Location = New Point(114, 37)
        txtLocalPort.Name = "txtLocalPort"
        txtLocalPort.Size = New Size(129, 31)
        txtLocalPort.TabIndex = 1
        ' 
        ' Label1
        ' 
        Label1.AutoSize = True
        Label1.Location = New Point(19, 37)
        Label1.Name = "Label1"
        Label1.Size = New Size(44, 25)
        Label1.TabIndex = 0
        Label1.Text = "Port"
        ' 
        ' chkVerbose
        ' 
        chkVerbose.AutoSize = True
        chkVerbose.Location = New Point(24, 374)
        chkVerbose.Name = "chkVerbose"
        chkVerbose.Size = New Size(145, 29)
        chkVerbose.TabIndex = 8
        chkVerbose.Text = "Logs détaillés"
        chkVerbose.UseVisualStyleBackColor = True
        ' 
        ' GroupBox2
        ' 
        GroupBox2.Controls.Add(btnConnect)
        GroupBox2.Controls.Add(txtRemotePort)
        GroupBox2.Controls.Add(Label3)
        GroupBox2.Controls.Add(txtRemoteIp)
        GroupBox2.Controls.Add(Label2)
        GroupBox2.Location = New Point(397, 32)
        GroupBox2.Name = "GroupBox2"
        GroupBox2.Size = New Size(396, 139)
        GroupBox2.TabIndex = 1
        GroupBox2.TabStop = False
        GroupBox2.Text = "Remote Connect"
        ' 
        ' btnConnect
        ' 
        btnConnect.Location = New Point(290, 83)
        btnConnect.Name = "btnConnect"
        btnConnect.Size = New Size(98, 34)
        btnConnect.TabIndex = 4
        btnConnect.Text = "Connect"
        btnConnect.UseVisualStyleBackColor = True
        ' 
        ' txtRemotePort
        ' 
        txtRemotePort.Location = New Point(134, 82)
        txtRemotePort.Name = "txtRemotePort"
        txtRemotePort.Size = New Size(150, 31)
        txtRemotePort.TabIndex = 3
        ' 
        ' Label3
        ' 
        Label3.AutoSize = True
        Label3.Location = New Point(24, 88)
        Label3.Name = "Label3"
        Label3.Size = New Size(110, 25)
        Label3.TabIndex = 2
        Label3.Text = "Remote Port"
        ' 
        ' txtRemoteIp
        ' 
        txtRemoteIp.Location = New Point(134, 42)
        txtRemoteIp.Name = "txtRemoteIp"
        txtRemoteIp.Size = New Size(254, 31)
        txtRemoteIp.TabIndex = 1
        ' 
        ' Label2
        ' 
        Label2.AutoSize = True
        Label2.Location = New Point(24, 45)
        Label2.Name = "Label2"
        Label2.Size = New Size(88, 25)
        Label2.TabIndex = 0
        Label2.Text = "RemoteIP"
        ' 
        ' GroupBox3
        ' 
        GroupBox3.Controls.Add(chkVerbose)
        GroupBox3.Controls.Add(btnChooseRecvFolder)
        GroupBox3.Controls.Add(lblRecvProgress)
        GroupBox3.Controls.Add(btnSendFile)
        GroupBox3.Controls.Add(pbSend)
        GroupBox3.Controls.Add(lblSendProgress)
        GroupBox3.Controls.Add(pbRecv)
        GroupBox3.Controls.Add(lstPeers)
        GroupBox3.Controls.Add(Label4)
        GroupBox3.Controls.Add(txtName)
        GroupBox3.Controls.Add(btnSend)
        GroupBox3.Controls.Add(txtMessage)
        GroupBox3.Controls.Add(txtLog)
        GroupBox3.Location = New Point(38, 334)
        GroupBox3.Name = "GroupBox3"
        GroupBox3.Size = New Size(755, 476)
        GroupBox3.TabIndex = 2
        GroupBox3.TabStop = False
        GroupBox3.Text = "Messaging"
        ' 
        ' btnChooseRecvFolder
        ' 
        btnChooseRecvFolder.Location = New Point(615, 374)
        btnChooseRecvFolder.Name = "btnChooseRecvFolder"
        btnChooseRecvFolder.Size = New Size(120, 34)
        btnChooseRecvFolder.TabIndex = 8
        btnChooseRecvFolder.Text = "Dossier"
        btnChooseRecvFolder.UseVisualStyleBackColor = True
        ' 
        ' lblRecvProgress
        ' 
        lblRecvProgress.AutoSize = True
        lblRecvProgress.Location = New Point(615, 415)
        lblRecvProgress.Name = "lblRecvProgress"
        lblRecvProgress.Size = New Size(129, 25)
        lblRecvProgress.TabIndex = 6
        lblRecvProgress.Text = "Réception : 0%"
        ' 
        ' btnSendFile
        ' 
        btnSendFile.Location = New Point(616, 327)
        btnSendFile.Name = "btnSendFile"
        btnSendFile.Size = New Size(119, 34)
        btnSendFile.TabIndex = 6
        btnSendFile.Text = "Send File"
        btnSendFile.UseVisualStyleBackColor = True
        ' 
        ' pbSend
        ' 
        pbSend.Location = New Point(-1, 446)
        pbSend.Name = "pbSend"
        pbSend.Size = New Size(611, 30)
        pbSend.TabIndex = 3
        ' 
        ' lblSendProgress
        ' 
        lblSendProgress.AutoSize = True
        lblSendProgress.Location = New Point(617, 446)
        lblSendProgress.Name = "lblSendProgress"
        lblSendProgress.Size = New Size(94, 25)
        lblSendProgress.TabIndex = 4
        lblSendProgress.Text = "Envoi : 0%"
        ' 
        ' pbRecv
        ' 
        pbRecv.Location = New Point(1, 415)
        pbRecv.Name = "pbRecv"
        pbRecv.Size = New Size(610, 31)
        pbRecv.TabIndex = 5
        ' 
        ' lstPeers
        ' 
        lstPeers.FormattingEnabled = True
        lstPeers.ItemHeight = 25
        lstPeers.Location = New Point(616, 36)
        lstPeers.Name = "lstPeers"
        lstPeers.Size = New Size(128, 279)
        lstPeers.TabIndex = 5
        ' 
        ' Label4
        ' 
        Label4.AutoSize = True
        Label4.Location = New Point(184, 327)
        Label4.Name = "Label4"
        Label4.Size = New Size(59, 25)
        Label4.TabIndex = 4
        Label4.Text = "Name"
        ' 
        ' txtName
        ' 
        txtName.Location = New Point(19, 327)
        txtName.Name = "txtName"
        txtName.Size = New Size(150, 31)
        txtName.TabIndex = 3
        ' 
        ' btnSend
        ' 
        btnSend.Location = New Point(282, 325)
        btnSend.Name = "btnSend"
        btnSend.Size = New Size(86, 35)
        btnSend.TabIndex = 2
        btnSend.Text = "Send"
        btnSend.UseVisualStyleBackColor = True
        ' 
        ' txtMessage
        ' 
        txtMessage.Location = New Point(23, 284)
        txtMessage.Name = "txtMessage"
        txtMessage.Size = New Size(572, 31)
        txtMessage.TabIndex = 1
        ' 
        ' txtLog
        ' 
        txtLog.Location = New Point(23, 36)
        txtLog.Name = "txtLog"
        txtLog.Size = New Size(587, 227)
        txtLog.TabIndex = 0
        txtLog.Text = ""
        ' 
        ' Form1
        ' 
        AutoScaleDimensions = New SizeF(10F, 25F)
        AutoScaleMode = AutoScaleMode.Font
        AutoSize = True
        BackColor = SystemColors.ActiveBorder
        BackgroundImage = CType(resources.GetObject("$this.BackgroundImage"), Image)
        BackgroundImageLayout = ImageLayout.Center
        ClientSize = New Size(822, 868)
        Controls.Add(GroupBox3)
        Controls.Add(GroupBox2)
        Controls.Add(GroupBox1)
        Name = "Form1"
        Text = "Form1"
        GroupBox1.ResumeLayout(False)
        GroupBox1.PerformLayout()
        GroupBox2.ResumeLayout(False)
        GroupBox2.PerformLayout()
        GroupBox3.ResumeLayout(False)
        GroupBox3.PerformLayout()
        ResumeLayout(False)
    End Sub

    Friend WithEvents GroupBox1 As GroupBox
    Friend WithEvents txtLocalPort As TextBox
    Friend WithEvents Label1 As Label
    Friend WithEvents btnStartHost As Button
    Friend WithEvents GroupBox2 As GroupBox
    Friend WithEvents Label3 As Label
    Friend WithEvents txtRemoteIp As TextBox
    Friend WithEvents Label2 As Label
    Friend WithEvents btnConnect As Button
    Friend WithEvents txtRemotePort As TextBox
    Friend WithEvents GroupBox3 As GroupBox
    Friend WithEvents txtLog As RichTextBox
    Friend WithEvents btnSend As Button
    Friend WithEvents txtMessage As TextBox
    Friend WithEvents Label4 As Label
    Friend WithEvents txtName As TextBox
    Friend WithEvents lstPeers As ListBox
    Friend WithEvents btnSendFile As Button
    Friend WithEvents Label5 As Label
    Friend WithEvents txtFilePort As TextBox
    Friend WithEvents pbSend As ProgressBar
    Friend WithEvents lblSendProgress As Label
    Friend WithEvents pbRecv As ProgressBar
    Friend WithEvents lblRecvProgress As Label
    Friend WithEvents btnChooseRecvFolder As Button
    Friend WithEvents chkVerbose As CheckBox

End Class
