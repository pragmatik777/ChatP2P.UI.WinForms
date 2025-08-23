Option Strict On
Imports System
Imports System.Collections.Generic
Imports System.Text

Namespace ChatP2P.Core
    Partial Module P2PManager

#Region "=== Identité Ed25519 (Shared) ==="
        Private _idPub As Byte() = Nothing   ' 32
        Private _idPriv As Byte() = Nothing  ' 64
        Private ReadOnly _peerIdPub As New Dictionary(Of String, Byte())(StringComparer.Ordinal)

        Public Sub SetIdentity(ed25519Pub As Byte(), ed25519Priv As Byte())
            If ed25519Pub Is Nothing OrElse ed25519Pub.Length <> 32 Then Throw New ArgumentException("ed25519Pub must be 32 bytes.")
            If ed25519Priv Is Nothing OrElse ed25519Priv.Length <> 64 Then Throw New ArgumentException("ed25519Priv must be 64 bytes.")
            _idPub = CType(ed25519Pub.Clone(), Byte())
            _idPriv = CType(ed25519Priv.Clone(), Byte())
        End Sub

        Public Function TryGetPeerIdentity(peerId As String, ByRef ed25519Pub As Byte()) As Boolean
            Return _peerIdPub.TryGetValue(peerId, ed25519Pub)
        End Function
#End Region

#Region "=== Signaling (KX_PUB + ID_PUB/ID_SIG) ==="
        Public Sub HandleOutgoingSignalWithIdentity(sig As SignalDescriptor)
            If sig Is Nothing Then Throw New ArgumentNullException(NameOf(sig))
            If sig.Tags Is Nothing Then sig.Tags = New Dictionary(Of String, String)(StringComparer.Ordinal)

            ' Ne pas écraser KX_PUB si déjà posé (ex: PFS)
            If Not sig.Tags.ContainsKey("KX_PUB") Then
                HandleOutgoingSignal(sig) ' pose KX_PUB = clé statique locale
            End If

            If _idPub IsNot Nothing AndAlso _idPriv IsNot Nothing Then
                Dim kx As String = Nothing
                If Not sig.Tags.TryGetValue("KX_PUB", kx) Then Exit Sub
                Dim msg = BuildSigMessage(kx, sig.PeerId)
                Dim sig64 = Convert.ToBase64String(Sodium.PublicKeyAuth.SignDetached(msg, _idPriv))
                sig.Tags("ID_PUB") = Convert.ToBase64String(_idPub)
                sig.Tags("ID_SIG") = sig64
            End If
        End Sub

        Public Sub HandleIncomingSignalWithIdentity(sig As SignalDescriptor)
            If sig Is Nothing Then Throw New ArgumentNullException(NameOf(sig))
            HandleIncomingSignal(sig) ' hydrate _cryptoPeerPub via KX_PUB

            If sig.Tags Is Nothing Then Return
            Dim kx As String = Nothing, idpub64 As String = Nothing, sig64 As String = Nothing
            If Not sig.Tags.TryGetValue("KX_PUB", kx) Then Return
            If Not sig.Tags.TryGetValue("ID_PUB", idpub64) Then Return
            If Not sig.Tags.TryGetValue("ID_SIG", sig64) Then Return

            Try
                Dim idpub = Convert.FromBase64String(idpub64)
                Dim sigb = Convert.FromBase64String(sig64)
                If idpub Is Nothing OrElse idpub.Length <> 32 Then Exit Sub
                Dim msg = BuildSigMessage(kx, sig.PeerId)
                Dim ok = Sodium.PublicKeyAuth.VerifyDetached(sigb, msg, idpub)
                If ok Then
                    _peerIdPub(sig.PeerId) = idpub
                    RaiseEvent OnPeerIdentityVerified(sig.PeerId, idpub, True)
                Else
                    RaiseEvent OnPeerIdentityVerified(sig.PeerId, idpub, False)
                End If
            Catch
            End Try
        End Sub
#End Region

#Region "=== Helpers ==="
        Private Function BuildSigMessage(kxPubB64 As String, peerId As String) As Byte()
            Return Encoding.UTF8.GetBytes("ChatP2P|KX|" & kxPubB64 & "|" & peerId)
        End Function
#End Region

        Public Event OnPeerIdentityVerified(peerId As String, ed25519Pub As Byte(), ok As Boolean)

    End Module
End Namespace
