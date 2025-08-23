Option Strict On
Imports System
Imports System.Text
Imports ChatP2P.Crypto

Namespace ChatP2P.App
    ''' <summary>
    ''' Session chiffrée simple : AEAD XChaCha20-Poly1305 + nonce aléatoire par message.
    ''' Paquet = [nonce(24)][ciphertext+tag]
    ''' </summary>
    Public NotInheritable Class CryptoSession
        Private ReadOnly _aead As IAead
        Private ReadOnly _suite As String

        Public Sub New(key32 As Byte(), Optional suiteName As String = "XCHACHA20POLY1305+X25519")
            _aead = New AeadXChaCha20(key32)
            _suite = suiteName
        End Sub

        Public ReadOnly Property SuiteName As String
            Get
                Return _suite
            End Get
        End Property

        Public Function EncryptPacket(plaintext As Byte(), Optional aad As Byte() = Nothing) As Byte()
            Dim nonce = Sodium.SodiumCore.GetRandomBytes(_aead.NonceSize)
            Dim ctTag = _aead.Seal(nonce, If(plaintext, Array.Empty(Of Byte)()), aad)
            Dim outp(ctTag.Length + nonce.Length - 1) As Byte
            Buffer.BlockCopy(nonce, 0, outp, 0, nonce.Length)
            Buffer.BlockCopy(ctTag, 0, outp, nonce.Length, ctTag.Length)
            Return outp
        End Function

        Public Function DecryptPacket(packet As Byte(), Optional aad As Byte() = Nothing) As Byte()
            If packet Is Nothing OrElse packet.Length < _aead.NonceSize + 16 Then Throw New ArgumentException("Paquet AEAD trop court.")
            Dim nonce(_aead.NonceSize - 1) As Byte
            Buffer.BlockCopy(packet, 0, nonce, 0, nonce.Length)
            Dim ctLen = packet.Length - nonce.Length
            Dim ct(ctLen - 1) As Byte
            Buffer.BlockCopy(packet, nonce.Length, ct, 0, ctLen)
            Return _aead.Open(nonce, ct, aad)
        End Function

        Public Function EncryptText(text As String) As Byte()
            Return EncryptPacket(Encoding.UTF8.GetBytes(If(text, "")))
        End Function

        Public Function DecryptText(packet As Byte()) As String
            Return Encoding.UTF8.GetString(DecryptPacket(packet))
        End Function
    End Class
End Namespace
