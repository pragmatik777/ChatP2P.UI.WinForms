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
        btnSecurity = New Button()
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
        lblCryptoStatus = New Label()
        lblAuthStatus = New Label()
        GroupBox1.SuspendLayout()
        GroupBox2.SuspendLayout()
        GroupBox3.SuspendLayout()
        SuspendLayout()
        ' 
        ' GroupBox1
        ' 
        GroupBox1.BackColor = Color.Transparent
        GroupBox1.Controls.Add(txtFilePort)
        GroupBox1.Controls.Add(Label5)
        GroupBox1.Controls.Add(btnStartHost)
        GroupBox1.Controls.Add(txtLocalPort)
        GroupBox1.Controls.Add(Label1)
        GroupBox1.Font = New Font("Segoe UI", 10.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        GroupBox1.ForeColor = Color.Lime
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
        txtFilePort.Size = New Size(130, 34)
        txtFilePort.TabIndex = 4
        ' 
        ' Label5
        ' 
        Label5.AutoSize = True
        Label5.Location = New Point(21, 76)
        Label5.Name = "Label5"
        Label5.Size = New Size(83, 28)
        Label5.TabIndex = 3
        Label5.Text = "File Port"
        ' 
        ' btnStartHost
        ' 
        btnStartHost.BackColor = Color.Black
        btnStartHost.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        btnStartHost.Location = New Point(249, 76)
        btnStartHost.Name = "btnStartHost"
        btnStartHost.Size = New Size(101, 34)
        btnStartHost.TabIndex = 2
        btnStartHost.Text = "Start Host"
        btnStartHost.UseVisualStyleBackColor = False
        ' 
        ' txtLocalPort
        ' 
        txtLocalPort.Location = New Point(114, 37)
        txtLocalPort.Name = "txtLocalPort"
        txtLocalPort.Size = New Size(129, 34)
        txtLocalPort.TabIndex = 1
        ' 
        ' Label1
        ' 
        Label1.AutoSize = True
        Label1.Location = New Point(19, 37)
        Label1.Name = "Label1"
        Label1.Size = New Size(48, 28)
        Label1.TabIndex = 0
        Label1.Text = "Port"
        ' 
        ' chkVerbose
        ' 
        chkVerbose.AutoSize = True
        chkVerbose.Location = New Point(24, 374)
        chkVerbose.Name = "chkVerbose"
        chkVerbose.Size = New Size(156, 32)
        chkVerbose.TabIndex = 8
        chkVerbose.Text = "Logs détaillés"
        chkVerbose.UseVisualStyleBackColor = True
        ' 
        ' GroupBox2
        ' 
        GroupBox2.BackColor = Color.Transparent
        GroupBox2.Controls.Add(btnConnect)
        GroupBox2.Controls.Add(txtRemotePort)
        GroupBox2.Controls.Add(Label3)
        GroupBox2.Controls.Add(txtRemoteIp)
        GroupBox2.Controls.Add(Label2)
        GroupBox2.Font = New Font("Segoe UI", 10.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        GroupBox2.ForeColor = Color.Lime
        GroupBox2.Location = New Point(397, 32)
        GroupBox2.Name = "GroupBox2"
        GroupBox2.Size = New Size(473, 139)
        GroupBox2.TabIndex = 1
        GroupBox2.TabStop = False
        GroupBox2.Text = "Remote Connect"
        ' 
        ' btnConnect
        ' 
        btnConnect.BackColor = Color.Black
        btnConnect.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        btnConnect.Location = New Point(349, 81)
        btnConnect.Name = "btnConnect"
        btnConnect.Size = New Size(106, 35)
        btnConnect.TabIndex = 4
        btnConnect.Text = "Connect"
        btnConnect.UseVisualStyleBackColor = False
        ' 
        ' txtRemotePort
        ' 
        txtRemotePort.Location = New Point(163, 80)
        txtRemotePort.Name = "txtRemotePort"
        txtRemotePort.Size = New Size(150, 34)
        txtRemotePort.TabIndex = 3
        ' 
        ' Label3
        ' 
        Label3.AutoSize = True
        Label3.Location = New Point(24, 88)
        Label3.Name = "Label3"
        Label3.Size = New Size(120, 28)
        Label3.TabIndex = 2
        Label3.Text = "Remote Port"
        ' 
        ' txtRemoteIp
        ' 
        txtRemoteIp.Location = New Point(163, 37)
        txtRemoteIp.Name = "txtRemoteIp"
        txtRemoteIp.Size = New Size(292, 34)
        txtRemoteIp.TabIndex = 1
        ' 
        ' Label2
        ' 
        Label2.AutoSize = True
        Label2.Location = New Point(24, 45)
        Label2.Name = "Label2"
        Label2.Size = New Size(95, 28)
        Label2.TabIndex = 0
        Label2.Text = "RemoteIP"
        ' 
        ' GroupBox3
        ' 
        GroupBox3.BackColor = Color.Transparent
        GroupBox3.Controls.Add(btnSecurity)
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
        GroupBox3.Font = New Font("Segoe UI", 10.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        GroupBox3.ForeColor = Color.Lime
        GroupBox3.Location = New Point(60, 208)
        GroupBox3.Name = "GroupBox3"
        GroupBox3.Size = New Size(792, 498)
        GroupBox3.TabIndex = 2
        GroupBox3.TabStop = False
        GroupBox3.Text = "Messaging"
        ' 
        ' btnSecurity
        ' 
        btnSecurity.BackColor = Color.Black
        btnSecurity.Location = New Point(461, 348)
        btnSecurity.Name = "btnSecurity"
        btnSecurity.Size = New Size(112, 34)
        btnSecurity.TabIndex = 9
        btnSecurity.Text = "Security"
        btnSecurity.UseVisualStyleBackColor = False
        ' 
        ' btnChooseRecvFolder
        ' 
        btnChooseRecvFolder.BackColor = Color.Black
        btnChooseRecvFolder.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        btnChooseRecvFolder.Location = New Point(625, 374)
        btnChooseRecvFolder.Name = "btnChooseRecvFolder"
        btnChooseRecvFolder.Size = New Size(119, 34)
        btnChooseRecvFolder.TabIndex = 8
        btnChooseRecvFolder.Text = "Dossier"
        btnChooseRecvFolder.UseVisualStyleBackColor = False
        ' 
        ' lblRecvProgress
        ' 
        lblRecvProgress.AutoSize = True
        lblRecvProgress.Location = New Point(615, 415)
        lblRecvProgress.Name = "lblRecvProgress"
        lblRecvProgress.Size = New Size(140, 28)
        lblRecvProgress.TabIndex = 6
        lblRecvProgress.Text = "Réception : 0%"
        ' 
        ' btnSendFile
        ' 
        btnSendFile.BackColor = Color.Black
        btnSendFile.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        btnSendFile.Location = New Point(625, 329)
        btnSendFile.Name = "btnSendFile"
        btnSendFile.Size = New Size(119, 34)
        btnSendFile.TabIndex = 6
        btnSendFile.Text = "Send File"
        btnSendFile.UseVisualStyleBackColor = False
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
        lblSendProgress.Size = New Size(101, 28)
        lblSendProgress.TabIndex = 4
        lblSendProgress.Text = "Envoi : 0%"
        ' 
        ' pbRecv
        ' 
        pbRecv.BackColor = Color.Black
        pbRecv.Location = New Point(1, 415)
        pbRecv.Name = "pbRecv"
        pbRecv.Size = New Size(610, 31)
        pbRecv.TabIndex = 5
        ' 
        ' lstPeers
        ' 
        lstPeers.BackColor = Color.Black
        lstPeers.ForeColor = Color.Lime
        lstPeers.FormattingEnabled = True
        lstPeers.ItemHeight = 28
        lstPeers.Location = New Point(625, 36)
        lstPeers.Name = "lstPeers"
        lstPeers.Size = New Size(128, 228)
        lstPeers.TabIndex = 5
        ' 
        ' Label4
        ' 
        Label4.AutoSize = True
        Label4.Location = New Point(184, 327)
        Label4.Name = "Label4"
        Label4.Size = New Size(64, 28)
        Label4.TabIndex = 4
        Label4.Text = "Name"
        ' 
        ' txtName
        ' 
        txtName.BackColor = Color.Black
        txtName.ForeColor = Color.Lime
        txtName.Location = New Point(21, 328)
        txtName.Name = "txtName"
        txtName.Size = New Size(150, 34)
        txtName.TabIndex = 3
        ' 
        ' btnSend
        ' 
        btnSend.BackColor = Color.Black
        btnSend.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        btnSend.Location = New Point(625, 284)
        btnSend.Name = "btnSend"
        btnSend.Size = New Size(68, 35)
        btnSend.TabIndex = 2
        btnSend.Text = "Send"
        btnSend.UseVisualStyleBackColor = False
        ' 
        ' txtMessage
        ' 
        txtMessage.BackColor = Color.Black
        txtMessage.ForeColor = Color.Lime
        txtMessage.Location = New Point(23, 284)
        txtMessage.Name = "txtMessage"
        txtMessage.Size = New Size(572, 34)
        txtMessage.TabIndex = 1
        ' 
        ' txtLog
        ' 
        txtLog.BackColor = Color.Black
        txtLog.ForeColor = Color.Lime
        txtLog.Location = New Point(23, 36)
        txtLog.Name = "txtLog"
        txtLog.Size = New Size(572, 227)
        txtLog.TabIndex = 0
        txtLog.Text = ""
        ' 
        ' lblCryptoStatus
        ' 
        lblCryptoStatus.AutoSize = True
        lblCryptoStatus.Location = New Point(31, 712)
        lblCryptoStatus.Name = "lblCryptoStatus"
        lblCryptoStatus.Size = New Size(63, 25)
        lblCryptoStatus.TabIndex = 3
        lblCryptoStatus.Text = "Label6"
        ' 
        ' lblAuthStatus
        ' 
        lblAuthStatus.AutoSize = True
        lblAuthStatus.Location = New Point(140, 712)
        lblAuthStatus.Name = "lblAuthStatus"
        lblAuthStatus.Size = New Size(63, 25)
        lblAuthStatus.TabIndex = 4
        lblAuthStatus.Text = "Label7"
        ' 
        ' Form1
        ' 
        AutoScaleDimensions = New SizeF(10.0F, 25.0F)
        AutoScaleMode = AutoScaleMode.Font
        AutoSize = True
        BackColor = SystemColors.ActiveBorder
        BackgroundImage = CType(resources.GetObject("$this.BackgroundImage"), Image)
        ClientSize = New Size(876, 746)
        Controls.Add(lblAuthStatus)
        Controls.Add(lblCryptoStatus)
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
        PerformLayout()
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
    Friend WithEvents btnSecurity As Button
    Friend WithEvents lblCryptoStatus As Label
    Friend WithEvents lblAuthStatus As Label

End Class
