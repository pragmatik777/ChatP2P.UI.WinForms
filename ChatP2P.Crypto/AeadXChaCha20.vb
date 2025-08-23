Option Strict On
Imports System

Namespace ChatP2P.Crypto
    ' AEAD XChaCha20-Poly1305 (libsodium)
    Public NotInheritable Class AeadXChaCha20
        Implements IAead

        Public Const NonceLen As Integer = 24
        Public Const TagLen As Integer = 16
        Private ReadOnly _key As Byte() ' 32B

        Public Sub New(key32 As Byte())
            If key32 Is Nothing OrElse key32.Length <> 32 Then
                Throw New ArgumentException("key32 must be 32 bytes.")
            End If
            _key = CType(key32.Clone(), Byte())
        End Sub

        Public ReadOnly Property NonceSize As Integer Implements IAead.NonceSize
            Get
                Return NonceLen
            End Get
        End Property

        Public ReadOnly Property TagSize As Integer Implements IAead.TagSize
            Get
                Return TagLen
            End Get
        End Property

        Public Function Seal(nonce As Byte(), plaintext As Byte(), Optional aad As Byte() = Nothing) As Byte() Implements IAead.Seal
            If nonce Is Nothing OrElse nonce.Length <> NonceLen Then Throw New ArgumentException("nonce must be 24 bytes.")
            If plaintext Is Nothing Then plaintext = Array.Empty(Of Byte)()
            Return Sodium.SecretAeadXChaCha20Poly1305.Encrypt(plaintext, nonce, _key, aad)
        End Function

        Public Function Open(nonce As Byte(), ciphertextAndTag As Byte(), Optional aad As Byte() = Nothing) As Byte() Implements IAead.Open
            If nonce Is Nothing OrElse nonce.Length <> NonceLen Then Throw New ArgumentException("nonce must be 24 bytes.")
            If ciphertextAndTag Is Nothing OrElse ciphertextAndTag.Length < TagLen Then
                Throw New ArgumentException("ciphertext+tag is invalid.")
            End If
            Return Sodium.SecretAeadXChaCha20Poly1305.Decrypt(ciphertextAndTag, nonce, _key, aad)
        End Function
    End Class
End Namespace
