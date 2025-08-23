' ChatP2P.Core/P2PManager.Crypto.vb
Option Strict On
Imports System
Imports System.Collections.Generic
Imports System.Security.Cryptography
Imports System.Text

Namespace ChatP2P.Core
    Partial Module P2PManager

#Region "=== État Crypto (Shared) ==="
        Private ReadOnly _cryptoSessions As New Dictionary(Of String, Object)(StringComparer.Ordinal)
        Private ReadOnly _cryptoPeerPub As New Dictionary(Of String, Byte())(StringComparer.Ordinal)
        Private ReadOnly _cryptoChannels As New Dictionary(Of String, Object)(StringComparer.Ordinal)
        Private _cryptoLocalPriv As Byte() = Nothing
        Private _cryptoLocalPub As Byte() = Nothing
        Private ReadOnly HKDF_INFO_PREFIX As Byte() = Encoding.UTF8.GetBytes("ChatP2P/1:AppSession")
#End Region

#Region "=== Initialisation des clés locales ==="
        Public Sub InitializeCrypto(localPriv As Byte(), localPub As Byte())
            If localPriv Is Nothing OrElse localPriv.Length <> 32 Then Throw New ArgumentException("localPriv must be 32 bytes (X25519).")
            If localPub Is Nothing OrElse localPub.Length <> 32 Then Throw New ArgumentException("localPub must be 32 bytes (X25519).")
            _cryptoLocalPriv = CType(localPriv.Clone(), Byte())
            _cryptoLocalPub = CType(localPub.Clone(), Byte())
        End Sub
#End Region

#Region "=== Signaling : KX_PUB ==="
        Public Sub HandleOutgoingSignal(sig As SignalDescriptor)
            If sig Is Nothing Then Throw New ArgumentNullException(NameOf(sig))
            If sig.Tags Is Nothing Then sig.Tags = New Dictionary(Of String, String)(StringComparer.Ordinal)
            If _cryptoLocalPub Is Nothing Then Throw New InvalidOperationException("Local key not initialized. Call InitializeCrypto().")
            sig.Tags("KX_PUB") = Convert.ToBase64String(_cryptoLocalPub)
        End Sub

        Public Sub HandleIncomingSignal(sig As SignalDescriptor)
            If sig Is Nothing Then Throw New ArgumentNullException(NameOf(sig))
            If sig.Tags Is Nothing Then Return
            Dim b64 As String = Nothing
            If sig.Tags.TryGetValue("KX_PUB", b64) Then
                _cryptoPeerPub(sig.PeerId) = Convert.FromBase64String(b64)
            End If
        End Sub
#End Region

#Region "=== DataChannel lifecycle ==="
        Public Sub OnDataChannelOpen(peerId As String, dc As Object)
            If String.IsNullOrEmpty(peerId) Then Throw New ArgumentException("peerId")
            If dc Is Nothing Then Throw New ArgumentNullException(NameOf(dc))
            Dim peerPub As Byte() = Nothing
            If Not _cryptoPeerPub.TryGetValue(peerId, peerPub) Then Exit Sub
            If _cryptoLocalPriv Is Nothing OrElse _cryptoLocalPub Is Nothing Then
                Throw New InvalidOperationException("Local key not initialized. Call InitializeCrypto().")
            End If

            Dim key32 = DeriveKey32(_cryptoLocalPriv, _cryptoLocalPub, peerPub)
            Dim sess As Object = CreateAppCryptoSession(key32)
            If sess Is Nothing Then Exit Sub
            _cryptoSessions(peerId) = sess
            _cryptoChannels(peerId) = dc
        End Sub

        Public Sub OnDataChannelMessage(peerId As String, data As Byte())
            Dim sess As Object = Nothing
            If Not _cryptoSessions.TryGetValue(peerId, sess) Then Exit Sub
            Try
                Dim aad = Encoding.UTF8.GetBytes(peerId)
                Dim plain = DecryptWithAppSession(sess, data, aad)
                RaiseEvent OnP2PMessage(peerId, plain)
            Catch
            End Try
        End Sub

        Public Sub SendP2P(peerId As String, payload As Byte())
            If payload Is Nothing Then payload = Array.Empty(Of Byte)()
            Dim sess As Object = Nothing
            If Not _cryptoSessions.TryGetValue(peerId, sess) Then
                Throw New InvalidOperationException("No crypto session for " & peerId)
            End If
            Dim dc As Object = Nothing
            If Not _cryptoChannels.TryGetValue(peerId, dc) Then
                Throw New InvalidOperationException("No DataChannel for " & peerId)
            End If
            Dim aad = Encoding.UTF8.GetBytes(peerId)
            Dim packet = EncryptWithAppSession(sess, payload, aad)
            CallDynamicSend(dc, peerId, packet)
        End Sub
#End Region

#Region "=== DeriveKey : X25519 + HKDF-SHA256 → 32B ==="
        Private Function DeriveKey32(myPriv As Byte(), myPub As Byte(), peerPub As Byte()) As Byte()
            Dim dh As Byte() = KexSharedSecretReflect(myPriv, peerPub)
            Dim minPub As Byte(), maxPub As Byte()
            If ByteArrayLessOrEqual(myPub, peerPub) Then
                minPub = myPub : maxPub = peerPub
            Else
                minPub = peerPub : maxPub = myPub
            End If
            Dim info = New Byte(HKDF_INFO_PREFIX.Length + 32 + 32 - 1) {}
            Buffer.BlockCopy(HKDF_INFO_PREFIX, 0, info, 0, HKDF_INFO_PREFIX.Length)
            Buffer.BlockCopy(minPub, 0, info, HKDF_INFO_PREFIX.Length, 32)
            Buffer.BlockCopy(maxPub, 0, info, HKDF_INFO_PREFIX.Length + 32, 32)
            Return HKDF_SHA256(dh, Nothing, info, 32)
        End Function

        ' HKDF-Extract+Expand (RFC 5869) HMACSHA256
        Private Function HKDF_SHA256(ikm As Byte(), salt As Byte(), info As Byte(), outLen As Integer) As Byte()
            Dim prk As Byte()
            Using h As New HMACSHA256(If(salt, New Byte(0) {}))
                prk = h.ComputeHash(ikm)
            End Using
            Dim res(outLen - 1) As Byte
            Dim prev() As Byte = Array.Empty(Of Byte)()
            Dim pos As Integer = 0
            Dim ctr As Byte = 0
            While pos < outLen
                ctr = CByte(ctr + 1)
                Using h As New HMACSHA256(prk)
                    Dim input As Byte() = Concat(prev, info, New Byte() {ctr})
                    prev = h.ComputeHash(input)
                    Dim toCopy = Math.Min(prev.Length, outLen - pos)
                    Buffer.BlockCopy(prev, 0, res, pos, toCopy)
                    pos += toCopy
                End Using
            End While
            Return res
        End Function

        ' ParamArray de tableaux (VB)
        Private Function Concat(ParamArray arrays()() As Byte) As Byte()
            Dim total As Integer = 0
            For Each a In arrays
                If a IsNot Nothing Then total += a.Length
            Next
            Dim r(If(total > 0, total - 1, 0)) As Byte
            Dim off As Integer = 0
            For Each a In arrays
                If a Is Nothing Then Continue For
                Buffer.BlockCopy(a, 0, r, off, a.Length)
                off += a.Length
            Next
            If total = 0 Then Return Array.Empty(Of Byte)()
            Return r
        End Function

        Private Function ByteArrayLessOrEqual(a As Byte(), b As Byte()) As Boolean
            Dim len As Integer = Math.Min(a.Length, b.Length)
            For i As Integer = 0 To len - 1
                If a(i) < b(i) Then Return True
                If a(i) > b(i) Then Return False
            Next
            Return a.Length <= b.Length
        End Function

        ' Cherche une méthode KexX25519.*Shared*(priv, pub) par réflexion
        Private Function KexSharedSecretReflect(myPriv As Byte(), peerPub As Byte()) As Byte()
            Dim candNames = New String() {"SharedSecret", "ComputeSharedSecret", "Derive", "GetSharedSecret"}
            Dim kexType As Type = Nothing
            For Each asm In AppDomain.CurrentDomain.GetAssemblies()
                kexType = asm.GetType("ChatP2P.Crypto.KexX25519", False)
                If kexType IsNot Nothing Then Exit For
            Next
            If kexType Is Nothing Then Throw New MissingMethodException("ChatP2P.Crypto.KexX25519 type not found.")
            For Each n In candNames
                Dim m = kexType.GetMethod(n, Reflection.BindingFlags.Public Or Reflection.BindingFlags.Static, Nothing, {GetType(Byte()), GetType(Byte())}, Nothing)
                If m IsNot Nothing Then
                    Dim res = TryCast(m.Invoke(Nothing, New Object() {myPriv, peerPub}), Byte())
                    If res IsNot Nothing Then Return res
                End If
            Next
            Throw New MissingMethodException("No X25519 shared-secret method found on KexX25519.")
        End Function
#End Region

#Region "=== Ponts dynamiques (réflexion) ==="
        Private Function CreateAppCryptoSession(key32 As Byte()) As Object
            Dim t As Type = Nothing
            For Each asm In AppDomain.CurrentDomain.GetAssemblies()
                t = asm.GetType("ChatP2P.App.CryptoSession", False)
                If t IsNot Nothing Then Exit For
            Next
            If t Is Nothing Then Return Nothing
            Return Activator.CreateInstance(t, New Object() {key32})
        End Function

        Private Function EncryptWithAppSession(sess As Object, plaintext As Byte(), aad As Byte()) As Byte()
            Dim m = sess.GetType().GetMethod("EncryptPacket", {GetType(Byte()), GetType(Byte())})
            If m Is Nothing Then Throw New MissingMethodException("EncryptPacket(Byte[], Byte[]) not found.")
            Return CType(m.Invoke(sess, New Object() {plaintext, aad}), Byte())
        End Function

        Private Function DecryptWithAppSession(sess As Object, packet As Byte(), aad As Byte()) As Byte()
            Dim m = sess.GetType().GetMethod("DecryptPacket", {GetType(Byte()), GetType(Byte())})
            If m Is Nothing Then Throw New MissingMethodException("DecryptPacket(Byte[], Byte[]) not found.")
            Return CType(m.Invoke(sess, New Object() {packet, aad}), Byte())
        End Function

        Private Sub CallDynamicSend(dc As Object, peerId As String, data As Byte())
            Dim m = dc.GetType().GetMethod("Send", {GetType(String), GetType(Byte())})
            If m Is Nothing Then Throw New MissingMethodException($"DataChannel.Send(String, Byte[]) not found on {dc.GetType().FullName}")
            m.Invoke(dc, New Object() {peerId, data})
        End Sub
#End Region

        Public Event OnP2PMessage(peerId As String, payload As Byte())

    End Module
End Namespace
