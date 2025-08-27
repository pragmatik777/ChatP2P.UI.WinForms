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

    End Module

End Namespace
