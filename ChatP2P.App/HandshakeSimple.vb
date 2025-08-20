' ChatP2P.App/HandshakeSimple.vb
Option Strict On
Imports System.Text
Imports ChatP2P.Core
Imports ChatP2P.Crypto

Namespace ChatP2P.App

    ' Handshake MVP : échange d'un "hello" signé (faux) + clé statique simulée
    ' Étape suivante : vrai X25519 + ML-KEM -> AEAD libsodium
    Public NotInheritable Class HandshakeSimple
        Implements IHandshake

        Public Event KeysDerived(kSend As Byte(), kRecv As Byte(), kFile As Byte(), kAv As Byte()) Implements IHandshake.KeysDerived

        Public Function MakeClientHello(local As IdentityBundle) As Byte() Implements IHandshake.MakeClientHello
            Dim s = $"HELLO:{local.DisplayName}"
            Return Encoding.UTF8.GetBytes(s)
        End Function

        Public Function HandlePeerHello(peerHello As Byte(), peer As IdentityBundle) As Boolean Implements IHandshake.HandlePeerHello
            Dim t = Encoding.UTF8.GetString(peerHello)
            Return t.StartsWith("HELLO:")
        End Function

        Public Async Function RunKeyAgreementAsync(kex As IKexClassic, kem As IKemPq, ks As IKeySchedule) As Threading.Tasks.Task Implements IHandshake.RunKeyAgreementAsync
            ' Stub : derive des clés factices (NE PAS UTILISER EN PRODUCTION)
            Dim k = Enumerable.Repeat(CByte(7), 32).ToArray()
            RaiseEvent KeysDerived(k, k, k, k)
            Await Task.CompletedTask
        End Function

    End Class

End Namespace
