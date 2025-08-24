' ChatP2P.Crypto/KexX25519.vb
Option Strict On
Imports Sodium

Namespace ChatP2P.Crypto
    ''' <summary>
    ''' Wrapper minimal X25519 pour KEX (Curve25519) via libsodium.
    ''' Fournit les méthodes recherchées par réflexion :
    ''' - GenerateKeyPair()
    ''' - GetPublic(priv)
    ''' - SharedSecret(priv, peerPub)
    ''' </summary>
    Public NotInheritable Class KexX25519

        ''' <summary>
        ''' Génère une paire X25519 (Curve25519) : pub(32), priv(32).
        ''' Utilise PublicKeyBox (keys Curve25519).
        ''' </summary>
        Public Shared Function GenerateKeyPair() As (pub As Byte(), priv As Byte())
            Dim kp = PublicKeyBox.GenerateKeyPair() ' Curve25519 (X25519)
            Return (kp.PublicKey, kp.PrivateKey)
        End Function

        ''' <summary>
        ''' Calcule la clé publique à partir de la clé privée X25519 (32).
        ''' </summary>
        Public Shared Function GetPublic(ByVal priv As Byte()) As Byte()
            If priv Is Nothing OrElse priv.Length <> 32 Then Throw New ArgumentException("X25519 priv must be 32 bytes.")
            Return ScalarMult.Base(priv) ' X25519 scalar * basepoint
        End Function

        ''' <summary>
        ''' Secret partagé X25519 : scalar-mult(priv, peerPub) → 32 bytes.
        ''' </summary>
        Public Shared Function SharedSecret(ByVal priv As Byte(), ByVal peerPub As Byte()) As Byte()
            If priv Is Nothing OrElse priv.Length <> 32 Then Throw New ArgumentException("X25519 priv must be 32 bytes.")
            If peerPub Is Nothing OrElse peerPub.Length <> 32 Then Throw New ArgumentException("X25519 peer pub must be 32 bytes.")
            Return ScalarMult.Mult(priv, peerPub)
        End Function

    End Class
End Namespace
