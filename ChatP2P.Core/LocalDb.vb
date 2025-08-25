' ChatP2P.Core/LocalDb.vb
Option Strict On
Imports System
Imports System.Data
Imports System.Data.SQLite
Imports System.Globalization
Imports System.IO

Namespace ChatP2P.Core

    Public Module LocalDb

        Private _dbPath As String
        Private _connString As String
        Private ReadOnly _gate As New Object()

        Public ReadOnly Property DbPath As String
            Get
                Return _dbPath
            End Get
        End Property

        ' ------------------ Init / Schema ------------------

        Public Sub Init(Optional appFolderName As String = "ChatP2P", Optional dbFileName As String = "chat.db")
            Dim dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appFolderName)
            If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
            _dbPath = Path.Combine(dir, dbFileName)
            _connString = $"Data Source={_dbPath};Version=3;Pooling=True;Journal Mode=WAL;"

            Dim isNew As Boolean = Not File.Exists(_dbPath)
            If isNew Then
                SQLiteConnection.CreateFile(_dbPath)
            End If

            Using cn = Open()
                Using cmd = cn.CreateCommand()
                    cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;"
                    cmd.ExecuteNonQuery()
                End Using
            End Using

            If isNew Then
                CreateSchema()
            Else
                Try : EnsureSchemaUpgrades() : Catch : End Try
            End If
        End Sub

        Private Sub CreateSchema()
            Const ddl As String =
"CREATE TABLE IF NOT EXISTS Peers(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL UNIQUE,
    Fingerprint TEXT NULL,
    Verified INTEGER NOT NULL DEFAULT 0,
    LastSeenUtc TEXT NULL,
    Trusted INTEGER NOT NULL DEFAULT 0,
    TrustNote TEXT NULL,
    DtlsFingerprint TEXT NULL
);

CREATE TABLE IF NOT EXISTS Messages(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PeerName TEXT NOT NULL,
    Sender TEXT NOT NULL,
    Body TEXT NOT NULL,
    IsP2P INTEGER NOT NULL,
    Direction TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Messages_Peer_Created ON Messages(PeerName, CreatedUtc);

CREATE TABLE IF NOT EXISTS Identities(
    Id INTEGER PRIMARY KEY CHECK (Id = 1),
    Ed25519Pub BLOB NULL,
    Ed25519Priv BLOB NULL
);
INSERT OR IGNORE INTO Identities(Id) VALUES(1);

CREATE TABLE IF NOT EXISTS Kv(
    K TEXT PRIMARY KEY,
    V TEXT NULL
);

CREATE TABLE IF NOT EXISTS PeerKeys(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PeerName TEXT NOT NULL,
    Kind TEXT NOT NULL,
    PublicKey BLOB NOT NULL,
    CreatedUtc TEXT NOT NULL,
    Revoked INTEGER NOT NULL DEFAULT 0,
    RevokedUtc TEXT NULL,
    Note TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_PeerKeys_Peer ON PeerKeys(PeerName, CreatedUtc);

CREATE TABLE IF NOT EXISTS Sessions(
    PeerName TEXT PRIMARY KEY,
    Secret BLOB NOT NULL,
    Kdf TEXT NOT NULL,
    Cipher TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL,
    LastUsedUtc TEXT NULL
);"
            ExecNonQueryBatch(ddl)
        End Sub

        Private Sub EnsureSchemaUpgrades()
            Dim alterList As String() = {
                "ALTER TABLE Peers ADD COLUMN Trusted INTEGER NOT NULL DEFAULT 0;",
                "ALTER TABLE Peers ADD COLUMN TrustNote TEXT NULL;",
                "ALTER TABLE Peers ADD COLUMN DtlsFingerprint TEXT NULL;"
            }
            For Each sql In alterList
                Try : ExecNonQuery(sql) : Catch : End Try
            Next
            Try : ExecNonQuery("CREATE TABLE IF NOT EXISTS PeerKeys(Id INTEGER PRIMARY KEY AUTOINCREMENT, PeerName TEXT NOT NULL, Kind TEXT NOT NULL, PublicKey BLOB NOT NULL, CreatedUtc TEXT NOT NULL, Revoked INTEGER NOT NULL DEFAULT 0, RevokedUtc TEXT NULL, Note TEXT NULL);") : Catch : End Try
            Try : ExecNonQuery("CREATE INDEX IF NOT EXISTS IX_PeerKeys_Peer ON PeerKeys(PeerName, CreatedUtc);") : Catch : End Try
            Try : ExecNonQuery("CREATE TABLE IF NOT EXISTS Sessions(PeerName TEXT PRIMARY KEY, Secret BLOB NOT NULL, Kdf TEXT NOT NULL, Cipher TEXT NOT NULL, CreatedUtc TEXT NOT NULL, LastUsedUtc TEXT NULL);") : Catch : End Try
        End Sub

        ' ------------------ Low-level exec ------------------

        Private Function Open() As SQLiteConnection
            Dim cn As New SQLiteConnection(_connString)
            cn.Open()
            Return cn
        End Function

        Public Function ExecNonQuery(sql As String, ParamArray ps() As SQLiteParameter) As Integer
            SyncLock _gate
                Using cn = Open()
                    Using cmd = cn.CreateCommand()
                        cmd.CommandText = sql
                        If ps IsNot Nothing AndAlso ps.Length > 0 Then cmd.Parameters.AddRange(ps)
                        Return cmd.ExecuteNonQuery()
                    End Using
                End Using
            End SyncLock
        End Function

        Public Sub ExecNonQueryBatch(sqlBatch As String)
            SyncLock _gate
                Using cn = Open()
                    Using tx = cn.BeginTransaction()
                        Using cmd = cn.CreateCommand()
                            cmd.Transaction = tx
                            cmd.CommandText = sqlBatch
                            cmd.ExecuteNonQuery()
                        End Using
                        tx.Commit()
                    End Using
                End Using
            End SyncLock
        End Sub

        Public Function ExecScalar(Of T)(sql As String, ParamArray ps() As SQLiteParameter) As T
            SyncLock _gate
                Using cn = Open()
                    Using cmd = cn.CreateCommand()
                        cmd.CommandText = sql
                        If ps IsNot Nothing AndAlso ps.Length > 0 Then cmd.Parameters.AddRange(ps)
                        Dim o = cmd.ExecuteScalar()
                        If o Is Nothing OrElse o Is DBNull.Value Then
                            Return Nothing
                        End If
                        Return CType(o, T)
                    End Using
                End Using
            End SyncLock
        End Function

        Public Function Query(sql As String, ParamArray ps() As SQLiteParameter) As DataTable
            SyncLock _gate
                Using cn = Open()
                    Using cmd = cn.CreateCommand()
                        cmd.CommandText = sql
                        If ps IsNot Nothing AndAlso ps.Length > 0 Then cmd.Parameters.AddRange(ps)
                        Using da As New SQLiteDataAdapter(cmd)
                            Dim dt As New DataTable()
                            da.Fill(dt)
                            Return dt
                        End Using
                    End Using
                End Using
            End SyncLock
        End Function

        ' Param helper
        Public Function P(name As String, value As Object) As SQLiteParameter
            Return New SQLiteParameter(name, value)
        End Function

        ' ------------------ Utils ------------------

        Private Function NowIso() As String
            Return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        End Function

        Private Function B2I(b As Boolean) As Integer
            Return If(b, 1, 0)
        End Function

        ' ------------------ Peers ------------------

        ''' <summary>Crée la ligne Peers si absente + update LastSeenUtc.</summary>
        Public Sub TouchPeer(name As String)
            Dim ts = NowIso()
            ExecNonQuery("INSERT OR IGNORE INTO Peers(Name, LastSeenUtc) VALUES(@n,@ts);", P("@n", name), P("@ts", ts))
            ExecNonQuery("UPDATE Peers SET LastSeenUtc=@ts WHERE Name=@n;", P("@ts", ts), P("@n", name))
        End Sub

        ' --- Compat shim pour anciens appels Form1 ---
        Public Sub EnsurePeer(name As String)
            TouchPeer(name)
        End Sub

        Public Function GetPeerDtlsFp(name As String) As String
            Return ExecScalar(Of String)("SELECT DtlsFingerprint FROM Peers WHERE Name=@n;", P("@n", name))
        End Function

        Public Function GetPeerTrusted(name As String) As Boolean
            Dim v As Object = ExecScalar(Of Object)("SELECT Trusted FROM Peers WHERE Name=@n;", P("@n", name))
            If v Is Nothing OrElse v Is DBNull.Value Then Return False
            Return Convert.ToInt32(v, CultureInfo.InvariantCulture) <> 0
        End Function
        ' ----------------------------------------------

        ''' <summary>Met à jour confiance/note/empreinte DTLS du peer.</summary>
        Public Sub UpsertPeerTrust(name As String,
                                   Optional trusted As Boolean? = Nothing,
                                   Optional note As String = Nothing,
                                   Optional dtlsFp As String = Nothing)
            TouchPeer(name)

            If trusted.HasValue Then
                ExecNonQuery("UPDATE Peers SET Trusted=@t WHERE Name=@n;",
                             P("@t", B2I(trusted.Value)), P("@n", name))
            End If

            If note IsNot Nothing Then
                ExecNonQuery("UPDATE Peers SET TrustNote=@note WHERE Name=@n;",
                             P("@note", note), P("@n", name))
            End If

            If dtlsFp IsNot Nothing Then
                ExecNonQuery("UPDATE Peers SET DtlsFingerprint=@fp WHERE Name=@n;",
                             P("@fp", dtlsFp), P("@n", name))
            End If
        End Sub

        Public Sub SetPeerDtlsFingerprint(name As String, fp As String)
            TouchPeer(name)
            ExecNonQuery("UPDATE Peers SET DtlsFingerprint=@fp WHERE Name=@n;", P("@fp", fp), P("@n", name))
        End Sub

        ' ------------------ PeerKeys ------------------

        Public Sub InsertPeerKey(peer As String, kind As String, publicKey As Byte(), Optional note As String = Nothing)
            TouchPeer(peer)
            ExecNonQuery(
                "INSERT INTO PeerKeys(PeerName, Kind, PublicKey, CreatedUtc, Revoked, Note)
                 VALUES(@p,@k,@pk,@c,0,@note);",
                P("@p", peer), P("@k", kind), P("@pk", publicKey), P("@c", NowIso()), P("@note", If(note, CType(DBNull.Value, Object)))
            )
        End Sub

        Public Sub RevokePeerKey(id As Long, Optional reasonNote As String = Nothing)
            ExecNonQuery("UPDATE PeerKeys SET Revoked=1, RevokedUtc=@ru, Note=COALESCE(Note,'') || CASE WHEN @r IS NULL THEN '' ELSE (' | '||@r) END WHERE Id=@id;",
                         P("@ru", NowIso()), P("@r", If(reasonNote, CType(DBNull.Value, Object))), P("@id", id))
        End Sub

        Public Function ListPeerKeys(Optional peer As String = Nothing) As DataTable
            If String.IsNullOrWhiteSpace(peer) Then
                Return Query("SELECT Id, PeerName, Kind, CreatedUtc, Revoked, RevokedUtc, Note FROM PeerKeys ORDER BY CreatedUtc DESC;")
            Else
                Return Query("SELECT Id, PeerName, Kind, CreatedUtc, Revoked, RevokedUtc, Note FROM PeerKeys WHERE PeerName=@p ORDER BY CreatedUtc DESC;",
                             P("@p", peer))
            End If
        End Function

        ' ------------------ Sessions ------------------

        Public Sub UpsertSession(peer As String, secret As Byte(), kdf As String, cipher As String)
            TouchPeer(peer)
            Dim now = NowIso()
            Dim n = ExecNonQuery(
                "UPDATE Sessions SET Secret=@s, Kdf=@k, Cipher=@c, LastUsedUtc=@lu WHERE PeerName=@p;",
                P("@s", secret), P("@k", kdf), P("@c", cipher), P("@lu", now), P("@p", peer)
            )
            If n = 0 Then
                ExecNonQuery(
                    "INSERT INTO Sessions(PeerName, Secret, Kdf, Cipher, CreatedUtc, LastUsedUtc)
                     VALUES(@p,@s,@k,@c,@cr,@lu);",
                    P("@p", peer), P("@s", secret), P("@k", kdf), P("@c", cipher), P("@cr", now), P("@lu", now)
                )
            End If
        End Sub

        Public Function GetSession(peer As String) As (Secret As Byte(), Kdf As String, Cipher As String)?
            Dim dt = Query("SELECT Secret, Kdf, Cipher FROM Sessions WHERE PeerName=@p;", P("@p", peer))
            If dt Is Nothing OrElse dt.Rows.Count = 0 Then Return Nothing
            Dim r = dt.Rows(0)
            Dim sec = CType(r!Secret, Byte())
            Dim kdf = TryCast(r!Kdf, String)
            Dim cph = TryCast(r!Cipher, String)
            Return (sec, kdf, cph)
        End Function

        Public Sub DeleteSession(peer As String)
            ExecNonQuery("DELETE FROM Sessions WHERE PeerName=@p;", P("@p", peer))
        End Sub

    End Module

End Namespace
