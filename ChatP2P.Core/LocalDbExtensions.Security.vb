Option Strict On
Option Explicit On

Imports System
Imports System.Data
Imports System.Globalization
Imports System.Text
Imports System.Security.Cryptography

Namespace ChatP2P.Core

    Public Module LocalDbExtensionsSecurity

        ' Migrations légères : colonnes / table supplémentaires
        Public Sub EnsurePeerExtraColumns()
            Try
                ' Colonne VerifiedUtc
                Try : LocalDb.ExecNonQuery("ALTER TABLE Peers ADD COLUMN VerifiedUtc TEXT;") : Catch : End Try
                ' Colonne Note
                Try : LocalDb.ExecNonQuery("ALTER TABLE Peers ADD COLUMN Note TEXT;") : Catch : End Try
                ' Colonne Pinned (empêche reset TOFU sans confirmation admin)
                Try : LocalDb.ExecNonQuery("ALTER TABLE Peers ADD COLUMN Pinned INTEGER DEFAULT 0;") : Catch : End Try
                ' Table SecurityEvents (historique mismatches et évènements)
                Try
                    LocalDb.ExecNonQuery("
CREATE TABLE IF NOT EXISTS SecurityEvents(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CreatedUtc TEXT NOT NULL,
    PeerName TEXT NOT NULL,
    Kind TEXT NOT NULL,             -- ex: 'PUBKEY_MISMATCH', 'TOFU_RESET', 'PIN', 'UNPIN'
    Details TEXT
);")
                Catch
                End Try
            Catch
            End Try
        End Sub

        Public Sub PeerSetTrusted(name As String, trusted As Boolean)
            If String.IsNullOrWhiteSpace(name) Then Return
            LocalDb.ExecNonQuery("UPDATE Peers SET Trusted=@t WHERE Name=@n;",
                                 LocalDb.P("@t", If(trusted, 1, 0)), LocalDb.P("@n", name))
        End Sub

        Public Function PeerIsVerified(name As String) As Boolean
            If String.IsNullOrWhiteSpace(name) Then Return False
            Dim dt = LocalDb.Query("SELECT VerifiedUtc FROM Peers WHERE Name=@n;", LocalDb.P("@n", name))
            If dt.Rows.Count = 0 Then Return False
            Dim v = TryCast(dt.Rows(0)!VerifiedUtc, String)
            Return Not String.IsNullOrEmpty(v)
        End Function

        Public Sub PeerMarkVerified(name As String)
            If String.IsNullOrWhiteSpace(name) Then Return
            Dim ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            LocalDb.ExecNonQuery("UPDATE Peers SET VerifiedUtc=@ts WHERE Name=@n;",
                                 LocalDb.P("@ts", ts), LocalDb.P("@n", name))
        End Sub

        Public Sub PeerMarkUnverified(name As String)
            If String.IsNullOrWhiteSpace(name) Then Return
            LocalDb.ExecNonQuery("UPDATE Peers SET VerifiedUtc=NULL WHERE Name=@n;", LocalDb.P("@n", name))
        End Sub

        Public Function PeerGetNote(name As String) As String
            If String.IsNullOrWhiteSpace(name) Then Return ""
            Dim dt = LocalDb.Query("SELECT Note FROM Peers WHERE Name=@n;", LocalDb.P("@n", name))
            If dt.Rows.Count = 0 OrElse IsDBNull(dt.Rows(0)!Note) Then Return ""
            Return Convert.ToString(dt.Rows(0)!Note, CultureInfo.InvariantCulture)
        End Function

        Public Sub PeerSetNote(name As String, note As String)
            If String.IsNullOrWhiteSpace(name) Then Return
            LocalDb.ExecNonQuery("UPDATE Peers SET Note=@x WHERE Name=@n;",
                                 LocalDb.P("@x", If(note, "")), LocalDb.P("@n", name))
        End Sub

        Public Function PeerGetPinned(name As String) As Boolean
            If String.IsNullOrWhiteSpace(name) Then Return False
            Dim dt = LocalDb.Query("SELECT Pinned FROM Peers WHERE Name=@n;", LocalDb.P("@n", name))
            If dt.Rows.Count = 0 Then Return False
            Dim v As Integer = 0
            If Not IsDBNull(dt.Rows(0)!Pinned) Then v = Convert.ToInt32(dt.Rows(0)!Pinned, CultureInfo.InvariantCulture)
            Return (v <> 0)
        End Function

        Public Sub PeerSetPinned(name As String, pinned As Boolean)
            If String.IsNullOrWhiteSpace(name) Then Return
            LocalDb.ExecNonQuery("UPDATE Peers SET Pinned=@p WHERE Name=@n;",
                                 LocalDb.P("@p", If(pinned, 1, 0)), LocalDb.P("@n", name))
            LogSecurityEvent(name, If(pinned, "PIN", "UNPIN"), "")
        End Sub

        ' Oublie la pubkey Ed25519 (Reset TOFU) — respect du flag Pinned à gérer côté UI avant appel
        Public Sub PeerForgetEd25519(name As String)
            If String.IsNullOrWhiteSpace(name) Then Return
            Try
                LocalDb.ExecNonQuery("UPDATE Peers SET Ed25519Pub=NULL WHERE Name=@n;", LocalDb.P("@n", name))
                LogSecurityEvent(name, "TOFU_RESET", "Ed25519Pub cleared")
            Catch
                ' Si clé stockée ailleurs, plug-in ici.
            End Try
        End Sub

        Public Sub LogSecurityEvent(name As String, kind As String, details As String)
            Dim ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            LocalDb.ExecNonQuery("INSERT INTO SecurityEvents(CreatedUtc, PeerName, Kind, Details) VALUES(@ts,@n,@k,@d);",
                                 LocalDb.P("@ts", ts), LocalDb.P("@n", name),
                                 LocalDb.P("@k", kind), LocalDb.P("@d", If(details, "")))
        End Sub

        Public Function GetSecurityEvents(Optional peer As String = "") As DataTable
            If String.IsNullOrWhiteSpace(peer) Then
                Return LocalDb.Query("SELECT CreatedUtc, PeerName, Kind, Details FROM SecurityEvents ORDER BY Id DESC;")
            Else
                Return LocalDb.Query("SELECT CreatedUtc, PeerName, Kind, Details FROM SecurityEvents WHERE PeerName=@n ORDER BY Id DESC;",
                                     LocalDb.P("@n", peer))
            End If
        End Function

        ' Vue consolidée pour la grille
        Public Function PeerList() As DataTable
            Dim dt = LocalDb.Query("SELECT Name, Trusted, CreatedUtc, LastSeenUtc, VerifiedUtc, Note, Pinned FROM Peers ORDER BY Name;")

            If Not dt.Columns.Contains("AuthOk") Then dt.Columns.Add("AuthOk", GetType(Boolean))
            If Not dt.Columns.Contains("Fingerprint") Then dt.Columns.Add("Fingerprint", GetType(String))

            For Each r As DataRow In dt.Rows
                Dim name = Convert.ToString(r!Name)
                Dim pk As Byte() = Nothing
                Try : pk = LocalDbExtensions.PeerGetEd25519(name) : Catch : pk = Nothing : End Try
                Dim fp As String = If(pk IsNot Nothing AndAlso pk.Length > 0, FormatFp(ComputeFp(pk)), "")

                Dim verified As Boolean = False
                If Not IsDBNull(r!VerifiedUtc) Then
                    Dim s = TryCast(r!VerifiedUtc, String)
                    verified = Not String.IsNullOrEmpty(s)
                End If

                r!AuthOk = verified
                r!Fingerprint = fp
            Next
            Return dt
        End Function

        Public Function PeerGetField(name As String, field As String) As String
            If String.IsNullOrWhiteSpace(name) Then Return ""
            Dim sql = "SELECT " & field & " FROM Peers WHERE Name=@n;"
            Dim dt = LocalDb.Query(sql, LocalDb.P("@n", name))
            If dt.Rows.Count = 0 OrElse IsDBNull(dt.Rows(0)(0)) Then Return ""
            Return Convert.ToString(dt.Rows(0)(0), CultureInfo.InvariantCulture)
        End Function

        ' === helpers fingerprint ===
        Private Function ComputeFp(pub As Byte()) As Byte()
            Using sha As SHA256 = SHA256.Create()
                Return sha.ComputeHash(pub)
            End Using
        End Function

        Private Function FormatFp(fp As Byte()) As String
            Dim hex = BitConverter.ToString(fp).Replace("-", "")
            Dim sb As New StringBuilder()
            For i = 0 To hex.Length - 1 Step 4
                If sb.Length > 0 Then sb.Append("-")
                Dim take = Math.Min(4, hex.Length - i)
                sb.Append(hex.Substring(i, take))
            Next
            Return sb.ToString()
        End Function

    End Module

End Namespace
