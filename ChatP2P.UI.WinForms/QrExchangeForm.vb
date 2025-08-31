Option Strict On
Option Explicit On

Imports System
Imports System.Text
Imports System.Windows.Forms

Public Class QrExchangeForm
    Inherits Form

    Private pic As PictureBox
    Private txt As TextBox
    Private btnClose As Button

    Public Sub New(title As String, payload As String)
        Me.Text = title
        Me.StartPosition = FormStartPosition.CenterParent
        Me.Width = 520
        Me.Height = 560

        pic = New PictureBox() With {.Dock = DockStyle.Top, .Height = 360, .SizeMode = PictureBoxSizeMode.Zoom, .BorderStyle = BorderStyle.FixedSingle}
        txt = New TextBox() With {.Dock = DockStyle.Fill, .Multiline = True, .ScrollBars = ScrollBars.Vertical, .Text = payload}
        btnClose = New Button() With {.Text = "Fermer", .Dock = DockStyle.Bottom}

        Me.Controls.Add(txt)
        Me.Controls.Add(pic)
        Me.Controls.Add(btnClose)

        AddHandler btnClose.Click, Sub() Me.Close()

        ' Génération QR si QRCoder est installé, sinon fallback texte
        Try
            Dim asm = System.Reflection.Assembly.Load("QRCoder")
            Dim qrgenType = asm.GetType("QRCoder.QRCodeGenerator")
            Dim qrType = asm.GetType("QRCoder.QRCode")
            Dim qrDataType = asm.GetType("QRCoder.QRCodeData")

            Dim gen = Activator.CreateInstance(qrgenType)
            Dim create = qrgenType.GetMethod("CreateQrCode", {GetType(String), asm.GetType("QRCoder.QRCodeGenerator+ECCLevel")})
            Dim eccEnum = asm.GetType("QRCoder.QRCodeGenerator+ECCLevel")
            Dim eccH = [Enum].Parse(eccEnum, "Q")

            Dim qrData = create.Invoke(gen, New Object() {payload, eccH})
            Dim qr = Activator.CreateInstance(qrType, qrData)
            Dim getGraphic = qrType.GetMethod("GetGraphic", {GetType(Integer)})
            Dim bmp = DirectCast(getGraphic.Invoke(qr, New Object() {10}), Drawing.Bitmap)
            pic.Image = bmp
        Catch
            ' pas de QRCoder: on laisse juste le payload en texte
            pic.Image = Nothing
        End Try
    End Sub
End Class
