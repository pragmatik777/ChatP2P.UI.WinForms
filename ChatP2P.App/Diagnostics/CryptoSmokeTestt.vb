Option Strict On
Imports System
Imports System.Text
Imports System.Security.Cryptography

Namespace ChatP2P.App.Diagnostics
    Public Module CryptoSmokeTest
        Public Sub Run()
            Dim kpA = GenerateKeyPairReflect()
            Dim kpB = GenerateKeyPairReflect()

            Dim kA = DeriveKey32(kpA.priv, kpA.pub, kpB.pub)
            Dim kB = DeriveKey32(kpB.priv, kpB.pub, kpA.pub)

            If Not BytesEqual(kA, kB) Then Throw New Exception("ECDH/HKDF mismatch.")

            Dim sA = New CryptoSession(kA)
            Dim sB = New CryptoSession(kB)

            Dim aad = Encoding.UTF8.GetBytes("peer:B")
            Dim msg = Encoding.UTF8.GetBytes("hello world ✓")

            Dim pkt = sA.EncryptPacket(msg, aad)
            Dim outp = sB.DecryptPacket(pkt, aad)

            Dim ok = Encoding.UTF8.GetString(outp)
            Console.WriteLine(If(ok, ""))
            If ok <> "hello world ✓" Then Throw New Exception("Decrypt mismatch.")
            Console.WriteLine("[OK] Crypto smoke test passed.")
        End Sub

        Private Function DeriveKey32(myPriv As Byte(), myPub As Byte(), peerPub As Byte()) As Byte()
            Dim dh = KexSharedSecretReflect(myPriv, peerPub)
            Dim prefix = Encoding.UTF8.GetBytes("ChatP2P/1:AppSession")
            Dim minPub As Byte(), maxPub As Byte()
            If ByteArrayLessOrEqual(myPub, peerPub) Then
                minPub = myPub : maxPub = peerPub
            Else
                minPub = peerPub : maxPub = myPub
            End If
            Dim info = New Byte(prefix.Length + 32 + 32 - 1) {}
            Buffer.BlockCopy(prefix, 0, info, 0, prefix.Length)
            Buffer.BlockCopy(minPub, 0, info, prefix.Length, 32)
            Buffer.BlockCopy(maxPub, 0, info, prefix.Length + 32, 32)
            Return HKDF_SHA256(dh, Nothing, info, 32)
        End Function

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

        Private Function Concat(ParamArray arrays()() As Byte) As Byte()
            Dim total = 0
            For Each a In arrays
                If a IsNot Nothing Then total += a.Length
            Next
            Dim r(total - 1) As Byte
            Dim off = 0
            For Each a In arrays
                If a Is Nothing Then Continue For
                Buffer.BlockCopy(a, 0, r, off, a.Length)
                off += a.Length
            Next
            Return r
        End Function

        Private Function ByteArrayLessOrEqual(a As Byte(), b As Byte()) As Boolean
            Dim len = Math.Min(a.Length, b.Length)
            For i = 0 To len - 1
                If a(i) < b(i) Then Return True
                If a(i) > b(i) Then Return False
            Next
            Return a.Length <= b.Length
        End Function

        Private Function BytesEqual(a As Byte(), b As Byte()) As Boolean
            If a Is Nothing OrElse b Is Nothing Then Return False
            If a.Length <> b.Length Then Return False
            For i = 0 To a.Length - 1
                If a(i) <> b(i) Then Return False
            Next
            Return True
        End Function

        ' ==== Réflexion KEX ====
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
    End Module
End Namespace
