Option Strict On
Imports System.Data
Imports System.Globalization

Namespace ChatP2P.Core

    ' Helpers DB additionnels (alignés roadmap) :
    ' - KV (Set/Get)
    ' - PeerKeys (Add / Get liste pour UI Révocations)
    ' - Purge messages plus anciens que N jours
    ' - Lecture simple des N derniers messages
    ' S’appuie sur les helpers de LocalDb.vb : ExecNonQuery / ExecScalar / Query / P

    Public Module LocalDbExtensions

        ' --- KV ---
        Public Sub KvSet(k As String, v As String)
            If String.IsNullOrWhiteSpace(k) Then Exit Sub
            LocalDb.ExecNonQuery(
                "INSERT INTO Kv(K,V) VALUES(@k,@v) ON CONFLICT(K) DO UPDATE SET V=excluded.V;",
                LocalDb.P("@k", k), LocalDb.P("@v", If(v, ""))
            )
        End Sub

        Public Function KvGet(k As String) As String
            If String.IsNullOrWhiteSpace(k) Then Return Nothing
            Dim o = LocalDb.ExecScalar(Of Object)("SELECT V FROM Kv WHERE K=@k;", LocalDb.P("@k", k))
            If o Is Nothing OrElse o Is DBNull.Value Then Return Nothing
            Return Convert.ToString(o, CultureInfo.InvariantCulture)
        End Function

        ' --- PeerKeys ---
        Public Sub PeerKeyAdd(peerName As String, kind As String, publicKey As Byte(), Optional note As String = Nothing)
            If String.IsNullOrWhiteSpace(peerName) OrElse String.IsNullOrWhiteSpace(kind) OrElse publicKey Is Nothing Then Exit Sub
            Dim nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            LocalDb.ExecNonQuery(
                "INSERT INTO PeerKeys(PeerName, Kind, Public, CreatedUtc, Revoked, RevokedUtc, Note)
                 VALUES(@p,@k,@pub,@ts,0,NULL,@n);",
                LocalDb.P("@p", peerName), LocalDb.P("@k", kind),
                LocalDb.P("@pub", publicKey), LocalDb.P("@ts", nowIso),
                LocalDb.P("@n", If(note, ""))
            )
        End Sub

        Public Function PeerKeysGet(peerName As String) As DataTable
            If String.IsNullOrWhiteSpace(peerName) Then Return New DataTable()
            Return LocalDb.Query(
                "SELECT Id, Kind, hex(Public) AS PubHex, CreatedUtc, Revoked, RevokedUtc, Note
                 FROM PeerKeys WHERE PeerName=@p ORDER BY Id DESC;",
                LocalDb.P("@p", peerName)
            )
        End Function

        ' --- Purge Messages ---
        Public Sub MessagesPurgeOlderThan(days As Integer)
            If days <= 0 Then Exit Sub
            Dim threshold = DateTime.UtcNow.AddDays(-days).ToString("o", CultureInfo.InvariantCulture)
            LocalDb.ExecNonQuery("DELETE FROM Messages WHERE CreatedUtc < @t;", LocalDb.P("@t", threshold))
        End Sub

        ' --- Historique simple (N derniers) ---
        Public Function MessagesGetLast(peer As String, take As Integer) As DataTable
            If String.IsNullOrWhiteSpace(peer) OrElse take <= 0 Then Return New DataTable()
            Return LocalDb.Query(
                "SELECT Sender, Body, IsP2P, Direction, CreatedUtc
                 FROM Messages WHERE PeerName=@p
                 ORDER BY Id DESC LIMIT @n;",
                LocalDb.P("@p", peer), LocalDb.P("@n", take)
            )
        End Function

        ' ====== Ed25519 Identity & Peer pubkeys (TOFU) ======

        Public Sub IdentityEnsureEd25519(ByRef pk As Byte(), ByRef sk As Byte())
            Dim dt = LocalDb.Query("SELECT Ed25519Pub, Ed25519Priv FROM Identities WHERE Id=1;")
            If dt.Rows.Count > 0 AndAlso Not IsDBNull(dt.Rows(0)(0)) AndAlso Not IsDBNull(dt.Rows(0)(1)) Then
                pk = CType(dt.Rows(0)(0), Byte())
                sk = CType(dt.Rows(0)(1), Byte())
                Exit Sub
            End If
            Dim gen = ChatP2P.Crypto.Ed25519Util.GenerateKeyPair()
            pk = gen.pk : sk = gen.sk
            LocalDb.ExecNonQuery(
        "UPDATE Identities SET Ed25519Pub=@p, Ed25519Priv=@s WHERE Id=1;",
        LocalDb.P("@p", pk), LocalDb.P("@s", sk))
        End Sub

        Public Function PeerGetEd25519(pubName As String) As Byte()
            Dim dt = LocalDb.Query("SELECT Public FROM PeerKeys WHERE PeerName=@p AND Kind='Ed25519' AND Revoked=0 ORDER BY Id DESC LIMIT 1;",
                           LocalDb.P("@p", pubName))
            If dt.Rows.Count = 0 OrElse IsDBNull(dt.Rows(0)(0)) Then Return Nothing
            Return CType(dt.Rows(0)(0), Byte())
        End Function

        Public Sub PeerSetEd25519_Tofu(pubName As String, pk As Byte())
            If pk Is Nothing Then Exit Sub
            LocalDb.EnsurePeer(pubName)
            Dim nowIso = DateTime.UtcNow.ToString("o", Globalization.CultureInfo.InvariantCulture)
            LocalDb.ExecNonQuery(
        "INSERT INTO PeerKeys(PeerName,Kind,Public,CreatedUtc,Revoked) VALUES(@p,'Ed25519',@k,@ts,0);",
        LocalDb.P("@p", pubName), LocalDb.P("@k", pk), LocalDb.P("@ts", nowIso))
        End Sub

        Public Sub PeerMarkVerified(name As String)
            LocalDb.ExecNonQuery("UPDATE Peers SET Verified=1 WHERE Name=@n;", LocalDb.P("@n", name))
        End Sub




    End Module

End Namespace
