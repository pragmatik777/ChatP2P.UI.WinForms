' ChatP2P.Core/P2PManager.Identity.vb
Option Strict On
Imports System
Imports System.Collections.Generic
Imports System.Text

Namespace ChatP2P.Core
    Partial Module P2PManager

#Region "=== Identité Ed25519 (Shared) ==="
        ' Clés Ed25519 locales (libsodium): pub=32, priv=64
        Private _idPub As Byte() = Nothing
        Private _idPriv As Byte() = Nothing

        ' Mémo des peers (PeerId -> Ed25519 pub)
        Private ReadOnly _peerIdPub As New Dictionary(Of String, Byte())(StringComparer.Ordinal)

        ''' <summary>Définit l'identité locale (Ed25519). pub=32, priv=64.</summary>
        Public Sub SetIdentity(ed25519Pub As Byte(), ed25519Priv As Byte())
            If ed25519Pub Is Nothing OrElse ed25519Pub.Length <> 32 Then Throw New ArgumentException("ed25519Pub must be 32 bytes.")
            If ed25519Priv Is Nothing OrElse ed25519Priv.Length <> 64 Then Throw New ArgumentException("ed25519Priv must be 64 bytes.")
            _idPub = CType(ed25519Pub.Clone(), Byte())
            _idPriv = CType(ed25519Priv.Clone(), Byte())
        End Sub

        ''' <summary>Récupère la pub d'un peer si connue (après signaling vérifié).</summary>
        Public Function TryGetPeerIdentity(peerId As String, ByRef ed25519Pub As Byte()) As Boolean
            Return _peerIdPub.TryGetValue(peerId, ed25519Pub)
        End Function
#End Region

#Region "=== Intégration au signaling (KX_PUB + ID_PUB/ID_SIG) ==="
        ''' <summary>
        ''' SORTANT : enrichit le SignalDescriptor avec identité signée.
        ''' ⚠️ N'écrit PAS KX_PUB si déjà présent (ex: posé par PFS).
        ''' </summary>
        Public Sub HandleOutgoingSignalWithIdentity(sig As SignalDescriptor)
            If sig Is Nothing Then Throw New ArgumentNullException(NameOf(sig))
            If sig.Tags Is Nothing Then sig.Tags = New Dictionary(Of String, String)(StringComparer.Ordinal)

            ' Laisser PFS poser KX_PUB si déjà présent ; sinon fallback sur clé statique
            If Not sig.Tags.ContainsKey("KX_PUB") Then
                HandleOutgoingSignal(sig) ' écrit KX_PUB = _cryptoLocalPub (clé statique)
            End If

            ' Ajoute identité si disponible
            If _idPub IsNot Nothing AndAlso _idPriv IsNot Nothing Then
                Dim kx As String = Nothing
                If Not sig.Tags.TryGetValue("KX_PUB", kx) Then Exit Sub
                Dim msg = BuildSigMessage(kx, sig.PeerId)
                Dim sig64 = Convert.ToBase64String(SignEd25519(msg, _idPriv))
                sig.Tags("ID_PUB") = Convert.ToBase64String(_idPub)
                sig.Tags("ID_SIG") = sig64
            End If
        End Sub

        ''' <summary>
        ''' ENTRANT : vérifie la signature et mémorise la pub Ed25519 du peer.
        ''' Appelle aussi l'existant pour hydrater _cryptoPeerPub depuis KX_PUB.
        ''' </summary>
        Public Sub HandleIncomingSignalWithIdentity(sig As SignalDescriptor)
            If sig Is Nothing Then Throw New ArgumentNullException(NameOf(sig))

            ' Hydrate _cryptoPeerPub via KX_PUB (existant)
            HandleIncomingSignal(sig)

            If sig.Tags Is Nothing Then Return
            Dim kx As String = Nothing, idpub64 As String = Nothing, sig64 As String = Nothing
            If (Not sig.Tags.TryGetValue("KX_PUB", kx)) Then Return
            If (Not sig.Tags.TryGetValue("ID_PUB", idpub64)) Then Return
            If (Not sig.Tags.TryGetValue("ID_SIG", sig64)) Then Return

            Try
                Dim idpub = Convert.FromBase64String(idpub64)
                Dim sigb = Convert.FromBase64String(sig64)
                If idpub Is Nothing OrElse idpub.Length <> 32 Then Exit Sub

                Dim msg = BuildSigMessage(kx, sig.PeerId)
                Dim ok = VerifyEd25519(msg, sigb, idpub)
                If ok Then
                    _peerIdPub(sig.PeerId) = idpub
                    RaiseEvent OnPeerIdentityVerified(sig.PeerId, idpub, True)
                Else
                    RaiseEvent OnPeerIdentityVerified(sig.PeerId, idpub, False)
                End If
            Catch
                ' ignore
            End Try
        End Sub
#End Region

#Region "=== Helpers Ed25519 + framing ==="
        Private Function BuildSigMessage(kxPubB64 As String, peerId As String) As Byte()
            Dim s = "ChatP2P|KX|" & kxPubB64 & "|" & peerId
            Return Encoding.UTF8.GetBytes(s)
        End Function

        Private Function SignEd25519(message As Byte(), priv64 As Byte()) As Byte()
            Return Sodium.PublicKeyAuth.SignDetached(message, priv64)
        End Function

        Private Function VerifyEd25519(message As Byte(), sig64 As Byte(), pub32 As Byte()) As Boolean
            Return Sodium.PublicKeyAuth.VerifyDetached(sig64, message, pub32)
        End Function
#End Region

        ''' <summary>Notifie l'app qu'un peer a présenté une identité (ok/fail).</summary>
        Public Event OnPeerIdentityVerified(peerId As String, ed25519Pub As Byte(), ok As Boolean)

    End Module
End Namespace
