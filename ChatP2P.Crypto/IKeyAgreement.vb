' ChatP2P.Crypto/IKeyAgreement.vb
Option Strict On
Imports System

Namespace ChatP2P.Crypto
    Public Interface IKeyAgreement
        ReadOnly Property PublicKey As Byte()      ' 32 bytes
        Function DeriveSharedSecret(peerPublic As Byte()) As Byte() ' 32 bytes
    End Interface
End Namespace
