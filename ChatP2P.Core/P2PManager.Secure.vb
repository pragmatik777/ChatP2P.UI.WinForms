' ChatP2P.Core/P2PManager.Secure.vb
Option Strict On
Imports System
Imports System.Collections.Generic

Namespace ChatP2P.Core
    Partial Module P2PManager

        ''' <summary>
        ''' Signal sortant "secure": PFS d'abord (pose/force KX_PUB éphémère si dispo),
        ''' puis identité (ID_PUB/ID_SIG) sans écraser KX_PUB.
        ''' </summary>
        Public Sub HandleOutgoingSignalSecure(sig As SignalDescriptor)
            If sig Is Nothing Then Throw New ArgumentNullException(NameOf(sig))
            ' 1) PFS place KX_PUB (éphémère si dispo, sinon clé statique)
            HandleOutgoingSignalWithPfs(sig)
            ' 2) Identité signe (n'écrase pas KX_PUB si déjà posé)
            HandleOutgoingSignalWithIdentity(sig)
        End Sub

        ''' <summary>
        ''' Signal entrant "secure": hydrate KX_PUB et vérifie l'identité.
        ''' (HandleIncomingSignalWithIdentity appelle déjà HandleIncomingSignal.)
        ''' </summary>
        Public Sub HandleIncomingSignalSecure(sig As SignalDescriptor)
            If sig Is Nothing Then Throw New ArgumentNullException(NameOf(sig))
            HandleIncomingSignalWithIdentity(sig)
        End Sub

        ''' <summary>
        ''' Ouverture DataChannel : si des éphémères existent pour ce peer, utilise PFS,
        ''' sinon dérivation avec la clé statique.
        ''' </summary>
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
