' ChatP2P.Core/LocalDb.vb
Option Strict On
Imports System
Imports System.Data
Imports System.Data.SQLite
Imports System.IO
Imports System.Globalization

Namespace ChatP2P.Core

    ''' <summary>
    ''' Base SQLite locale (fichier .db dans %APPDATA%\ChatP2P)
    ''' </summary>
    Public Module LocalDb

        Private _dbPath As String
        Private _connString As String
        Private ReadOnly _gate As New Object()

        Public ReadOnly Property DbPath As String
            Get
                Return _dbPath
            End Get
        End Property

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
            End If

            ' Toujours tenter la migration légère
            Migrate()
        End Sub

        Private Sub CreateSchema()
            Const ddl As String =
"CREATE TABLE IF NOT EXISTS Peers(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL UNIQUE,
    Fingerprint TEXT NULL,            -- réservé (identité applicative)
    Verified INTEGER NOT NULL DEFAULT 0,
    LastSeenUtc TEXT NULL
);

CREATE TABLE IF NOT EXISTS Messages(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PeerName TEXT NOT NULL,
    Sender TEXT NOT NULL,           -- 'me' ou le nom du peer
    Body TEXT NOT NULL,
    IsP2P INTEGER NOT NULL,         -- 0/1
    Direction TEXT NOT NULL,        -- 'send' / 'recv'
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
);"
            ExecNonQueryBatch(ddl)
        End Sub

        ''' <summary>Migrations légères: colonnes trust/DTLS + nouvelles tables clés/sessions/audit.</summary>
        Public Sub Migrate()
            ' Colonnes Peers
            Try : ExecNonQuery("ALTER TABLE Peers ADD COLUMN DtlsFingerprint TEXT NULL;") : Catch : End Try
            Try : ExecNonQuery("ALTER TABLE Peers ADD COLUMN Trusted INTEGER NOT NULL DEFAULT 0;") : Catch : End Try
            Try : ExecNonQuery("ALTER TABLE Peers ADD COLUMN TrustNote TEXT NULL;") : Catch : End Try

            ' Tables clés, sessions, journal
            Const ddl As String =
"CREATE TABLE IF NOT EXISTS PeerKeys(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PeerName TEXT NOT NULL,
    Kind TEXT NOT NULL,                 -- 'mldsa','ed25519','mlkem','x25519', etc.
    Pub BLOB NOT NULL,
    CreatedUtc TEXT NOT NULL,
    Revoked INTEGER NOT NULL DEFAULT 0,
    RevokedUtc TEXT NULL,
    Note TEXT NULL
);

CREATE TABLE IF NOT EXISTS Sessions(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PeerName TEXT NOT NULL UNIQUE,      -- une seule session active par pair
    State BLOB NOT NULL,
    UpdatedUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS KeyExchanges(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PeerName TEXT NOT NULL,
    Direction TEXT NOT NULL,            -- 'init' / 'resp'
    Proto TEXT NOT NULL,                -- 'HYBRID_KEM_V1'
    TranscriptHash TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL,
    Note TEXT NULL
);"
            ExecNonQueryBatch(ddl)
        End Sub

        ' ----------------- Exécution bas niveau -----------------

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

        ' --------- Shorthands paramètres ---------
        Public Function P(name As String, value As Object) As SQLiteParameter
            Return New SQLiteParameter(name, value)
        End Function

        ' ----------------- Helpers métier -----------------

        Public Sub EnsurePeer(name As String)
            Dim nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            ExecNonQuery("INSERT OR IGNORE INTO Peers(Name, LastSeenUtc) VALUES(@n,@ts);", P("@n", name), P("@ts", nowIso))
            ExecNonQuery("UPDATE Peers SET LastSeenUtc=@ts WHERE Name=@n;", P("@ts", nowIso), P("@n", name))
        End Sub

        ''' <summary>Met à jour la confiance et/ou l’empreinte DTLS (ne remplace pas l’empreinte si une différente est déjà présente, sauf explicitement demandé).</summary>
        Public Sub UpsertPeerTrust(peer As String,
                                   Optional trusted As Boolean? = Nothing,
                                   Optional dtlsFp As String = Nothing,
                                   Optional note As String = Nothing,
                                   Optional overwriteFp As Boolean = False)
            EnsurePeer(peer)
            If trusted.HasValue Then
                ExecNonQuery("UPDATE Peers SET Trusted=@t WHERE Name=@n;", P("@t", If(trusted.Value, 1, 0)), P("@n", peer))
            End If
            If note IsNot Nothing Then
                ExecNonQuery("UPDATE Peers SET TrustNote=@x WHERE Name=@n;", P("@x", note), P("@n", peer))
            End If
            If dtlsFp IsNot Nothing Then
                Dim cur As String = ExecScalar(Of String)("SELECT DtlsFingerprint FROM Peers WHERE Name=@n;", P("@n", peer))
                If String.IsNullOrWhiteSpace(cur) OrElse overwriteFp OrElse String.Equals(cur, dtlsFp, StringComparison.OrdinalIgnoreCase) Then
                    ExecNonQuery("UPDATE Peers SET DtlsFingerprint=@f WHERE Name=@n;", P("@f", dtlsFp), P("@n", peer))
                End If
            End If
        End Sub

        Public Function GetPeerTrusted(peer As String) As Boolean
            Dim v = ExecScalar(Of Object)("SELECT Trusted FROM Peers WHERE Name=@n;", P("@n", peer))
            If v Is Nothing OrElse v Is DBNull.Value Then Return False
            Return Convert.ToInt32(v, CultureInfo.InvariantCulture) <> 0
        End Function

        Public Function GetPeerDtlsFp(peer As String) As String
            Return ExecScalar(Of String)("SELECT DtlsFingerprint FROM Peers WHERE Name=@n;", P("@n", peer))
        End Function

        ' --- Gestion des clés ---

        Public Sub AddPeerKey(peer As String, kind As String, pub As Byte(), Optional note As String = Nothing)
            EnsurePeer(peer)
            Dim nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            ExecNonQuery("INSERT INTO PeerKeys(PeerName,Kind,Pub,CreatedUtc,Note) VALUES(@p,@k,@b,@t,@n);",
                         P("@p", peer), P("@k", kind), P("@b", pub), P("@t", nowIso), P("@n", If(note, CType(DBNull.Value, Object))))
        End Sub

        Public Function GetPeerKeys(peer As String) As DataTable
            Return Query("SELECT Id,Kind,CreatedUtc,Revoked,RevokedUtc,Note FROM PeerKeys WHERE PeerName=@p ORDER BY CreatedUtc DESC;", P("@p", peer))
        End Function

        Public Sub RevokePeerKey(id As Long, Optional note As String = Nothing)
            Dim nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            ExecNonQuery("UPDATE PeerKeys SET Revoked=1, RevokedUtc=@r, Note=COALESCE(Note,'') || CASE WHEN @n IS NULL THEN '' ELSE (' | '||@n) END WHERE Id=@id;",
                         P("@r", nowIso), P("@n", If(note, CType(DBNull.Value, Object))), P("@id", id))
        End Sub

        Public Function GetActivePeerKey(peer As String, kind As String) As Byte()
            Return ExecScalar(Of Byte())("SELECT Pub FROM PeerKeys WHERE PeerName=@p AND Kind=@k AND Revoked=0 ORDER BY CreatedUtc DESC LIMIT 1;",
                                         P("@p", peer), P("@k", kind))
        End Function

        ' --- Sessions (ratchet/state) ---

        Public Sub SaveSession(peer As String, state As Byte())
            EnsurePeer(peer)
            Dim nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            Dim rows = ExecNonQuery("UPDATE Sessions SET State=@s, UpdatedUtc=@t WHERE PeerName=@p;",
                                    P("@s", state), P("@t", nowIso), P("@p", peer))
            If rows = 0 Then
                ExecNonQuery("INSERT INTO Sessions(PeerName,State,UpdatedUtc) VALUES(@p,@s,@t);",
                             P("@p", peer), P("@s", state), P("@t", nowIso))
            End If
        End Sub

        Public Function LoadSession(peer As String) As Byte()
            Return ExecScalar(Of Byte())("SELECT State FROM Sessions WHERE PeerName=@p;", P("@p", peer))
        End Function

        Public Sub DeleteSession(peer As String)
            ExecNonQuery("DELETE FROM Sessions WHERE PeerName=@p;", P("@p", peer))
        End Sub

        ' --- Journal des échanges de clés ---
        Public Sub AddKeyExchange(peer As String, direction As String, proto As String, transcriptHash As String, Optional note As String = Nothing)
            EnsurePeer(peer)
            Dim nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            ExecNonQuery("INSERT INTO KeyExchanges(PeerName,Direction,Proto,TranscriptHash,CreatedUtc,Note) VALUES(@p,@d,@r,@h,@t,@n);",
                         P("@p", peer), P("@d", direction), P("@r", proto), P("@h", transcriptHash), P("@t", nowIso), P("@n", If(note, CType(DBNull.Value, Object))))
        End Sub

    End Module

End Namespace
