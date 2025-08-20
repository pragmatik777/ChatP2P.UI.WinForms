' ChatP2P.Core/Handshake.vb
Option Strict On
Imports System.Security.Cryptography
Imports System.Text

Namespace ChatP2P.Core

    Public NotInheritable Class HandshakeTranscript
        Private ReadOnly _ms As New IO.MemoryStream()
        Public Sub Append(label As String, data As Byte())
            Dim tag = Encoding.UTF8.GetBytes(label & ":"c)
            _ms.Write(tag, 0, tag.Length)
            If data IsNot Nothing AndAlso data.Length > 0 Then
                _ms.Write(data, 0, data.Length)
            End If
        End Sub
        Public Function Hash() As Byte()
            Using sha As SHA256 = SHA256.Create()
                Return sha.ComputeHash(_ms.ToArray())
            End Using
        End Function
    End Class

    Public Interface IHandshake
        Event KeysDerived(kSend As Byte(), kRecv As Byte(), kFile As Byte(), kAv As Byte())
        Function MakeClientHello(local As IdentityBundle) As Byte()
        Function HandlePeerHello(peerHello As Byte(), peer As IdentityBundle) As Boolean
        Function RunKeyAgreementAsync(
          kex As ChatP2P.Crypto.IKexClassic,
          kem As ChatP2P.Crypto.IKemPq,
          ks As ChatP2P.Crypto.IKeySchedule
        ) As Threading.Tasks.Task
    End Interface

End Namespace
