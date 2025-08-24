' ChatP2P.Core/ChatRepository.vb
Option Strict On
Imports System
Imports System.Collections.Generic
Imports System.Data
Imports System.Data.SQLite

Namespace ChatP2P.Core

    ''' <summary>
    ''' Requêtes applicatives (messages, peers, identité).
    ''' </summary>
    Public Module ChatRepository

        ' -------- Messages --------

        Public Sub SaveMessage(peerName As String,
                               sender As String,
                               body As String,
                               isP2P As Boolean,
                               direction As String,   ' "send" | "recv"
                               createdUtc As DateTime)
            If String.IsNullOrWhiteSpace(peerName) OrElse String.IsNullOrWhiteSpace(sender) _
               OrElse body Is Nothing OrElse String.IsNullOrWhiteSpace(direction) Then Exit Sub

            LocalDb.ExecNonQuery(
                "INSERT INTO Messages(PeerName, Sender, Body, IsP2P, Direction, CreatedUtc)
                 VALUES(@p, @s, @b, @i, @d, @t);",
                LocalDb.P("@p", peerName),
                LocalDb.P("@s", sender),
                LocalDb.P("@b", body),
                LocalDb.P("@i", If(isP2P, 1, 0)),
                LocalDb.P("@d", direction.ToLowerInvariant()),
                LocalDb.P("@t", createdUtc.ToUniversalTime().ToString("o"))
            )

            ' upsert Peers.LastSeenUtc
            UpsertPeer(peerName, lastSeenUtc:=createdUtc, fingerprint:=Nothing, verified:=Nothing)
        End Sub

        Public Function GetLastMessages(peerName As String, Optional take As Integer = 100) As List(Of (Sender As String, Body As String, IsP2P As Boolean, Direction As String, Created As DateTime))
            Dim res As New List(Of (String, String, Boolean, String, DateTime))()
            If String.IsNullOrWhiteSpace(peerName) Then Return res
            Dim dt = LocalDb.Query(
                "SELECT Sender, Body, IsP2P, Direction, CreatedUtc
                 FROM Messages
                 WHERE PeerName=@p
                 ORDER BY Id DESC
                 LIMIT @n;",
                LocalDb.P("@p", peerName),
                LocalDb.P("@n", take)
            )
            ' On renvoie en ordre chronologique (le SELECT est DESC)
            For i As Integer = dt.Rows.Count - 1 To 0 Step -1
                Dim r = dt.Rows(i)
                Dim sender = CStr(r!Sender)
                Dim body = CStr(r!Body)
                Dim isP2P = (Convert.ToInt32(r!IsP2P) <> 0)
                Dim dir = CStr(r!Direction)
                Dim created As DateTime
                DateTime.TryParse(CStr(r!CreatedUtc), Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AdjustToUniversal, created)
                res.Add((sender, body, isP2P, dir, created))
            Next
            Return res
        End Function

        ' -------- Peers --------

        Public Sub UpsertPeer(name As String,
                              Optional lastSeenUtc As DateTime? = Nothing,
                              Optional fingerprint As String = Nothing,
                              Optional verified As Boolean? = Nothing)
            If String.IsNullOrWhiteSpace(name) Then Exit Sub

            ' INSERT si inexistant
            LocalDb.ExecNonQuery(
                "INSERT OR IGNORE INTO Peers(Name) VALUES(@n);",
                LocalDb.P("@n", name)
            )

            ' SET partiel
            Dim sets As New List(Of String)()
            Dim ps As New List(Of SQLiteParameter) From {LocalDb.P("@n", name)}
            If lastSeenUtc.HasValue Then
                sets.Add("LastSeenUtc=@ls")
                ps.Add(LocalDb.P("@ls", lastSeenUtc.Value.ToUniversalTime().ToString("o")))
            End If
            If fingerprint IsNot Nothing Then
                sets.Add("Fingerprint=@fp")
                ps.Add(LocalDb.P("@fp", fingerprint))
            End If
            If verified.HasValue Then
                sets.Add("Verified=@v")
                ps.Add(LocalDb.P("@v", If(verified.Value, 1, 0)))
            End If

            If sets.Count > 0 Then
                Dim sql = "UPDATE Peers SET " & String.Join(", ", sets) & " WHERE Name=@n;"
                LocalDb.ExecNonQuery(sql, ps.ToArray())
            End If
        End Sub

        Public Function GetPeer(name As String) As (Exists As Boolean, Fingerprint As String, Verified As Boolean, LastSeenUtc As DateTime?)
            If String.IsNullOrWhiteSpace(name) Then Return (False, "", False, Nothing)
            Dim dt = LocalDb.Query("SELECT Fingerprint, Verified, LastSeenUtc FROM Peers WHERE Name=@n;", LocalDb.P("@n", name))
            If dt.Rows.Count = 0 Then Return (False, "", False, Nothing)
            Dim r = dt.Rows(0)
            Dim fp As String = If(r!Fingerprint Is DBNull.Value, "", CStr(r!Fingerprint))
            Dim ver As Boolean = (Convert.ToInt32(r!Verified) <> 0)
            Dim ls As DateTime? = Nothing
            If Not (r!LastSeenUtc Is DBNull.Value) Then
                Dim d As DateTime
                If DateTime.TryParse(CStr(r!LastSeenUtc), Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AdjustToUniversal, d) Then
                    ls = d
                End If
            End If
            Return (True, fp, ver, ls)
        End Function

        ' -------- Identité locale (facultatif, pour plus tard) --------

        Public Sub SaveIdentity(ed25519Pub As Byte(), ed25519Priv As Byte())
            LocalDb.ExecNonQuery(
                "UPDATE Identities SET Ed25519Pub=@p, Ed25519Priv=@s WHERE Id=1;",
                LocalDb.P("@p", ed25519Pub),
                LocalDb.P("@s", ed25519Priv)
            )
        End Sub

        Public Function LoadIdentity() As (Pub As Byte(), Priv As Byte())
            Dim dt = LocalDb.Query("SELECT Ed25519Pub, Ed25519Priv FROM Identities WHERE Id=1;")
            If dt.Rows.Count = 0 Then Return (Nothing, Nothing)
            Dim r = dt.Rows(0)
            Dim pub As Byte() = If(r!Ed25519Pub Is DBNull.Value, Nothing, DirectCast(r!Ed25519Pub, Byte()))
            Dim priv As Byte() = If(r!Ed25519Priv Is DBNull.Value, Nothing, DirectCast(r!Ed25519Priv, Byte()))
            Return (pub, priv)
        End Function

    End Module

End Namespace
