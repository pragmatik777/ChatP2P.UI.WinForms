' ChatP2P.Crypto/Ed25519.vb
Option Strict On
Imports Sodium

Namespace ChatP2P.Crypto
    ''' <summary>
    ''' Wrapper minimal Ed25519 pour génération de paire (pub=32, priv=64).
    ''' S'appuie sur Sodium.Core (libsodium).
    ''' </summary>
    Public NotInheritable Class Ed25519

        ''' <summary>
        ''' Génère une paire Ed25519 : pub(32), priv(64).
        ''' </summary>
        Public Shared Function GenerateKeyPair() As (pub As Byte(), priv As Byte())
            Dim kp = PublicKeyAuth.GenerateKeyPair() ' Ed25519
            ' kp.PrivateKey est 64 octets (seed+pk), kp.PublicKey 32 octets.
            Return (kp.PublicKey, kp.PrivateKey)
        End Function

    End Class
End Namespace
