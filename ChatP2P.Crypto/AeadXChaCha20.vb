' ChatP2P.Crypto/AeadXChaCha20.vb
Option Strict On
Imports System
Imports Sodium

Namespace ChatP2P.Crypto
    Public NotInheritable Class AeadXChaCha20
        Implements IAead

        Public Const KeySize As Integer = 32
        Public Const NonceSize As Integer = 24

        Private ReadOnly _key As Byte()

        Public Sub New(key32 As Byte())
            If key32 Is Nothing OrElse key32.Length <> KeySize Then Throw New ArgumentException("Clé AEAD invalide (32 octets).", NameOf(key32))
            _key = CType(key32.Clone(), Byte())
        End Sub

        Public ReadOnly Property Key As Byte() Implements IAead.Key
            Get
                Return CType(_key.Clone(), Byte())
            End Get
        End Property

        Public Function Seal(nonce As Byte(), plaintext As Byte(), Optional aad As Byte() = Nothing) As Byte() Implements IAead.Seal
            If nonce Is Nothing OrElse nonce.Length <> NonceSize Then Throw New ArgumentException("Nonce XChaCha20 = 24 octets requis.", NameOf(nonce))
            If plaintext Is Nothing Then plaintext = Array.Empty(Of Byte)()
            If aad Is Nothing Then aad = Array.Empty(Of Byte)()
            Return SecretAeadXChaCha20Poly1305.Encrypt(plaintext, aad, nonce, _key)
        End Function

        Public Function Open(nonce As Byte(), ciphertext As Byte(), Optional aad As Byte() = Nothing) As Byte() Implements IAead.Open
            If nonce Is Nothing OrElse nonce.Length <> NonceSize Then Throw New ArgumentException("Nonce XChaCha20 = 24 octets requis.", NameOf(nonce))
            If ciphertext Is Nothing Then ciphertext = Array.Empty(Of Byte)()
            If aad Is Nothing Then aad = Array.Empty(Of Byte)()
            Return SecretAeadXChaCha20Poly1305.Decrypt(ciphertext, aad, nonce, _key)
        End Function
    End Class
End Namespace
