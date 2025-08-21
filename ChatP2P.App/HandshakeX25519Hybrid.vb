' ChatP2P.App/HandshakeX25519Hybrid.vb
Option Strict On
Imports System
Imports System.Text
Imports ChatP2P.Crypto

Namespace ChatP2P.App
    Public NotInheritable Class HandshakeX25519Hybrid
        Public Shared Function MakeMyKex() As KexX25519
            Return New KexX25519()
        End Function

        ''' <summary>
        ''' Dérive la clé AEAD 32o à partir de X25519 (obligatoire) + PQC (optionnel).
        ''' info/ctx permet de binder l’usage (ex: "chatp2p-v1" & pair names).
        ''' </summary>
        Public Shared Function DeriveAeadKey(myKex As KexX25519,
                                            peerX25519Pub32 As Byte(),
                                            Optional kemSharedSecret As Byte() = Nothing,
                                            Optional contextInfo As String = "chatp2p-v1") As Byte()
            If myKex Is Nothing Then Throw New ArgumentNullException(NameOf(myKex))
            Dim xShared = myKex.DeriveSharedSecret(peerX25519Pub32) ' 32o
            Dim ikm As Byte()
            If kemSharedSecret IsNot Nothing AndAlso kemSharedSecret.Length > 0 Then
                ' hybride : concat
                ikm = New Byte(xShared.Length + kemSharedSecret.Length - 1) {}
                Buffer.BlockCopy(xShared, 0, ikm, 0, xShared.Length)
                Buffer.BlockCopy(kemSharedSecret, 0, ikm, xShared.Length, kemSharedSecret.Length)
            Else
                ' classique : X25519 seul
                ikm = xShared
            End If

            ' HKDF-SHA256 → 32o
            Dim salt As Byte() = Nothing
            Dim key32 = KeyScheduleHKDF.DeriveKey(salt, ikm, contextInfo, 32)
            Return key32
        End Function

        Public Shared Function CreateSession(aeadKey32 As Byte()) As CryptoSession
            Return New CryptoSession(aeadKey32, "XCHACHA20POLY1305+X25519")
        End Function
    End Class
End Namespace
