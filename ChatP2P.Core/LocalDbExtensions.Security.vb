Option Strict On
Option Explicit On

Imports System
Imports System.Data
Imports System.Globalization
Imports System.Text
Imports System.Security.Cryptography


Namespace ChatP2P.Core

    Public Module LocalDbExtensionsSecurity

        ' --- Migrations très légères : ajoute des colonnes "VerifiedUtc" et "Note" si elles n'existent pas encore.
        Public Sub EnsurePeerExtraColumns()
            Try
                ' Ajout "VerifiedUtc"
                Try
                    LocalDb.ExecNonQuery("ALTER TABLE Peers ADD COLUMN VerifiedUtc TEXT;")
                Catch
                    ' ignore si existe déjà
                End Try
                ' Ajout "Note"
                Try
                    LocalDb.ExecNonQuery("ALTER TABLE Peers ADD COLUMN Note TEXT;")
                Catch
                    ' ignore si existe déjà
                End Try
            Catch
                ' silencieux
            End Try
        End Sub

        Public Sub PeerSetTrusted(name As String, trusted As Boolean)
            If String.IsNullOrWhiteSpace(name) Then Return
            LocalDb.ExecNonQuery("UPDATE Peers SET Trusted=@t WHERE Name=@n;",
                                 LocalDb.P("@t", If(trusted, 1, 0)),
                                 LocalDb.P("@n", name))
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
                                 LocalDb.P("@x", If(note, "")),
                                 LocalDb.P("@n", name))
        End Sub

        ' Oublie la pubkey Ed25519 stockée (Reset TOFU)
        Public Sub PeerForgetEd25519(name As String)
            If String.IsNullOrWhiteSpace(name) Then Return
            ' Essaie la table attendue. Si ton schéma diffère, adapte cette requête.
            Try
                LocalDb.ExecNonQuery("UPDATE Peers SET Ed25519Pub=NULL WHERE Name=@n;", LocalDb.P("@n", name))
            Catch
                ' Si tu stockes ailleurs (ex: table Keyring), tombe à une méthode d'extension existante si dispo.
                Try
                    ' Option: une API dédiée si tu l’as créée
                    ' LocalDbExtensions.PeerSetEd25519_Tofu(name, Nothing) ' si autorise NULL
                Catch
                End Try
            End Try
        End Sub

        ' Récupère une vue consolidée pour la grille
        Public Function PeerList() As DataTable
            Dim dt = LocalDb.Query("SELECT Name, Trusted, CreatedUtc, LastSeenUtc, VerifiedUtc, Note FROM Peers ORDER BY Name;")

            ' Ajoute colonnes calculées
            If Not dt.Columns.Contains("AuthOk") Then dt.Columns.Add("AuthOk", GetType(Boolean))
            If Not dt.Columns.Contains("Fingerprint") Then dt.Columns.Add("Fingerprint", GetType(String))

            For Each r As DataRow In dt.Rows
                Dim name = Convert.ToString(r!Name)
                Dim pk As Byte() = Nothing
                Try
                    pk = LocalDbExtensions.PeerGetEd25519(name)
                Catch
                    pk = Nothing
                End Try

                Dim fp As String = ""
                If pk IsNot Nothing AndAlso pk.Length > 0 Then
                    fp = FormatFp(ComputeFp(pk))
                End If

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

        ' === helpers de fingerprint ===
        Private Function ComputeFp(pub As Byte()) As Byte()
            Using sha = SHA256.Create()
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
