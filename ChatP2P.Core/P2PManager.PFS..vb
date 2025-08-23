' ChatP2P.Core/P2PManager.PFS.vb
Option Strict On
Imports System
Imports System.Collections.Generic
Imports System.Text

Namespace ChatP2P.Core
    Partial Module P2PManager

#Region "=== Éphémères PFS (Shared) ==="
        Private ReadOnly _cryptoEphPriv As New Dictionary(Of String, Byte())(StringComparer.Ordinal)
        Private ReadOnly _cryptoEphPub As New Dictionary(Of String, Byte())(StringComparer.Ordinal)

        Public Sub PrepareEphemeral(peerId As String)
            If String.IsNullOrEmpty(peerId) Then Throw New ArgumentException(NameOf(peerId))
            Dim kp = GenerateKeyPairReflect()
            _cryptoEphPriv(peerId) = kp.priv
            _cryptoEphPub(peerId) = kp.pub
        End Sub

        Public Sub HandleOutgoingSignalWithPfs(sig As SignalDescriptor)
            If sig Is Nothing Then Throw New ArgumentNullException(NameOf(sig))
            If sig.Tags Is Nothing Then sig.Tags = New Dictionary(Of String, String)(StringComparer.Ordinal)
            Dim pub As Byte() = Nothing
            If Not _cryptoEphPub.TryGetValue(sig.PeerId, pub) Then
                If _cryptoLocalPub Is Nothing Then Throw New InvalidOperationException("Local key not initialized. Call InitializeCrypto().")
                pub = _cryptoLocalPub
            End If
            sig.Tags("KX_PUB") = Convert.ToBase64String(pub)
        End Sub

        Public Sub OnDataChannelOpenWithPfs(peerId As String, dc As Object)
            If String.IsNullOrEmpty(peerId) Then Throw New ArgumentException("peerId")
            If dc Is Nothing Then Throw New ArgumentNullException(NameOf(dc))
            Dim peerPub As Byte() = Nothing
            If Not _cryptoPeerPub.TryGetValue(peerId, peerPub) Then Exit Sub

            Dim myPriv = If(_cryptoEphPriv.ContainsKey(peerId), _cryptoEphPriv(peerId), _cryptoLocalPriv)
            Dim myPub = If(_cryptoEphPub.ContainsKey(peerId), _cryptoEphPub(peerId), _cryptoLocalPub)
            If myPriv Is Nothing OrElse myPub Is Nothing Then
                Throw New InvalidOperationException("Key not initialized. Call InitializeCrypto()/PrepareEphemeral().")
            End If

            Dim key32 = DeriveKey32(myPriv, myPub, peerPub)
            Dim sess As Object = CreateAppCryptoSession(key32)
            If sess Is Nothing Then Exit Sub
            _cryptoSessions(peerId) = sess
            _cryptoChannels(peerId) = dc

            _cryptoEphPriv.Remove(peerId) : _cryptoEphPub.Remove(peerId)
        End Sub
#End Region

#Region "=== KEX helpers (réflexion) ==="
        Private Function GenerateKeyPairReflect() As (priv As Byte(), pub As Byte())
            Dim kexType As Type = Nothing
            For Each asm In AppDomain.CurrentDomain.GetAssemblies()
                kexType = asm.GetType("ChatP2P.Crypto.KexX25519", False)
                If kexType IsNot Nothing Then Exit For
            Next
            If kexType Is Nothing Then Throw New MissingMethodException("ChatP2P.Crypto.KexX25519 type not found.")

            Dim cand = New String() {"GenerateKeyPair", "GenerateKeypair", "NewKeyPair", "CreateKeyPair"}
            For Each n In cand
                Dim m = kexType.GetMethod(n, Reflection.BindingFlags.Public Or Reflection.BindingFlags.Static, Nothing, Type.EmptyTypes, Nothing)
                If m IsNot Nothing Then
                    Dim kv = m.Invoke(Nothing, Nothing)
                    Dim priv As Byte() = TryGetFieldOrProp(Of Byte())(kv, {"priv", "Priv", "PrivateKey"})
                    Dim pub As Byte() = TryGetFieldOrProp(Of Byte())(kv, {"pub", "Pub", "PublicKey"})
                    If priv IsNot Nothing AndAlso pub IsNot Nothing Then Return (priv, pub)
                End If
            Next
            Throw New MissingMethodException("No X25519 keypair generator found on KexX25519.")
        End Function

        ' Renommage du paramètre de type pour éviter BC32089
        Private Function TryGetFieldOrProp(Of TRes)(obj As Object, names As IEnumerable(Of String)) As TRes
            If obj Is Nothing Then Return Nothing
            Dim tp = obj.GetType()
            For Each name In names
                Dim pi = tp.GetProperty(name, Reflection.BindingFlags.Public Or Reflection.BindingFlags.Instance)
                If pi IsNot Nothing Then
                    Dim v = pi.GetValue(obj, Nothing)
                    If TypeOf v Is TRes Then Return CType(v, TRes)
                End If
                Dim fi = tp.GetField(name, Reflection.BindingFlags.Public Or Reflection.BindingFlags.Instance)
                If fi IsNot Nothing Then
                    Dim v = fi.GetValue(obj)
                    If TypeOf v Is TRes Then Return CType(v, TRes)
                End If
            Next
            Return Nothing
        End Function
#End Region

    End Module
End Namespace
