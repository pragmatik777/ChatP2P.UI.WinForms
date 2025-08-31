' ChatP2P.Crypto/Ed25519Util.vb
Option Strict On
Imports System
Imports System.Text
Imports Sodium

Namespace ChatP2P.Crypto
    Public NotInheritable Class Ed25519Util

        Public Shared Function GenerateKeyPair() As (pk As Byte(), sk As Byte())
            Dim kp = PublicKeyAuth.GenerateKeyPair()
            Return (pk:=kp.PublicKey, sk:=kp.PrivateKey)
        End Function

        Public Shared Function Sign(message As Byte(), sk As Byte()) As Byte()
            Return PublicKeyAuth.SignDetached(message, sk)
        End Function

        Public Shared Function Verify(message As Byte(), sig As Byte(), pk As Byte()) As Boolean
            Return PublicKeyAuth.VerifyDetached(sig, message, pk)
        End Function

        Public Shared Function SignUtf8(s As String, sk As Byte()) As Byte()
            Return Sign(Encoding.UTF8.GetBytes(If(s, "")), sk)
        End Function

        Public Shared Function VerifyUtf8(s As String, sig As Byte(), pk As Byte()) As Boolean
            Return Verify(Encoding.UTF8.GetBytes(If(s, "")), sig, pk)
        End Function
    End Class
End Namespace
