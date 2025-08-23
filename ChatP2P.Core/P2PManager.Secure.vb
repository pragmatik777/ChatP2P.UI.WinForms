Option Strict On
Imports System

Namespace ChatP2P.Core
    Partial Module P2PManager

        ' Sortant: PFS (pose KX_PUB éphémère si dispo) puis Identité (signe sans écraser KX_PUB)
        Public Sub HandleOutgoingSignalSecure(sig As SignalDescriptor)
            If sig Is Nothing Then Throw New ArgumentNullException(NameOf(sig))
            HandleOutgoingSignalWithPfs(sig)
            HandleOutgoingSignalWithIdentity(sig)
        End Sub

        ' Entrant: hydrate KX_PUB + vérifie identité
        Public Sub HandleIncomingSignalSecure(sig As SignalDescriptor)
            If sig Is Nothing Then Throw New ArgumentNullException(NameOf(sig))
            HandleIncomingSignalWithIdentity(sig)
        End Sub

        ' DataChannel : si éphémères présents → PFS, sinon statique
        Public Sub OnDataChannelOpenSecure(peerId As String, dc As Object)
            If String.IsNullOrEmpty(peerId) Then Throw New ArgumentException(NameOf(peerId))
            If dc Is Nothing Then Throw New ArgumentNullException(NameOf(dc))
            Dim hasEph As Boolean =
                (_cryptoEphPriv IsNot Nothing AndAlso _cryptoEphPriv.ContainsKey(peerId)) OrElse
                (_cryptoEphPub IsNot Nothing AndAlso _cryptoEphPub.ContainsKey(peerId))
            If hasEph Then
                OnDataChannelOpenWithPfs(peerId, dc)
            Else
                OnDataChannelOpen(peerId, dc)
            End If
        End Sub

    End Module
End Namespace
