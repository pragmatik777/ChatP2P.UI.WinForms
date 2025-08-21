' ChatP2P.Crypto/KemPqStub.vb
Option Strict On
Imports System

Namespace ChatP2P.Crypto
    ''' <summary>
    ''' Stub KEM post-quantique. Pour l’instant, renvoie vide.
    ''' Quand une lib PQC stable sera choisie, on branchera ici.
    ''' </summary>
    Public NotInheritable Class KemPqStub
        Public Shared Function Encapsulate(peerKemPublic As Byte()) As (cipherText As Byte(), sharedSecret As Byte())
            Return (Array.Empty(Of Byte)(), Array.Empty(Of Byte)())
        End Function

        Public Shared Function Decapsulate(cipherText As Byte()) As Byte()
            Return Array.Empty(Of Byte)()
        End Function
    End Class
End Namespace
