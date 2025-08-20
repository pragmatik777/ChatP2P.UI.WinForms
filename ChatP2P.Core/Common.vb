' ChatP2P.Core/Common.vb
Option Strict On
Imports System.Net

Namespace ChatP2P.Core

    Public Enum PathType
        Direct = 0
        Ice = 1
        PwnatAlpha = 2
        Relay = 3
    End Enum

    <Flags>
    Public Enum Feature
        None = 0
        Messaging = 1
        FileTransfer = 2
        AudioVideo = 4
    End Enum

    Public NotInheritable Class AlgorithmSuite
        Public Property Kem As String()          ' e.g., {"ml-kem768","ml-kem512"}
        Public Property Kex As String()          ' e.g., {"x25519"}
        Public Property Aead As String()         ' e.g., {"xchacha20poly1305","aes-256-gcm"}
    End Class

    Public NotInheritable Class PathHints
        Public Property Stun As String()         ' e.g., {"stun:stun.example.org:3478"}
        Public Property Turn As String()         ' optional
        Public Property HostPorts As String()    ' e.g., {"p=9002/udp","p=443/tcp"}
        Public Property AllowPwnatAlpha As Boolean
    End Class

    Public NotInheritable Class IdentityBundle
        Public Property DisplayName As String
        Public Property SigClassicPub As Byte()  ' Ed25519
        Public Property SigPqPub As Byte()       ' ML-DSA
        Public Property Fingerprint As String
        Public Property Features As Feature
        Public Property Algos As AlgorithmSuite
        Public Property Hints As PathHints
    End Class

    Public NotInheritable Class PeerDescriptor
        Public Property Identity As IdentityBundle
        Public Property Endpoints As List(Of IPEndPoint)

        Public Sub New()
            Endpoints = New List(Of IPEndPoint)()
        End Sub
    End Class

End Namespace
