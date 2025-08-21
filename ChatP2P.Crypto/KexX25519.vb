' ChatP2P.Crypto/KexX25519.vb
Option Strict On
Imports System
Imports Sodium

Namespace ChatP2P.Crypto
    ''' <summary>ECDH X25519 (ScalarMult) – secret partagé = 32 octets.</summary>
    Public NotInheritable Class KexX25519
        Implements IKeyAgreement

        Private ReadOnly _sk As Byte()   ' 32
        Private ReadOnly _pk As Byte()   ' 32

        Public Sub New()
            ' Clé privée 32o random (libsodium côté ScalarMult)
            _sk = SodiumCore.GetRandomBytes(32)
            _pk = ScalarMult.Base(_sk)
        End Sub

        Public Sub New(privateKey32 As Byte())
            If privateKey32 Is Nothing OrElse privateKey32.Length <> 32 Then Throw New ArgumentException("Clé privée X25519 invalide (32 octets).", NameOf(privateKey32))
            _sk = CType(privateKey32.Clone(), Byte())
            _pk = ScalarMult.Base(_sk)
        End Sub

        Public ReadOnly Property PublicKey As Byte() Implements IKeyAgreement.PublicKey
            Get
                Return CType(_pk.Clone(), Byte())
            End Get
        End Property

        Public Function DeriveSharedSecret(peerPublic As Byte()) As Byte() Implements IKeyAgreement.DeriveSharedSecret
            If peerPublic Is Nothing OrElse peerPublic.Length <> 32 Then Throw New ArgumentException("Clé publique X25519 invalide (32 octets).", NameOf(peerPublic))
            Return ScalarMult.Mult(_sk, peerPublic) ' 32 bytes
        End Function
    End Class
End Namespace
