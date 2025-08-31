' ChatP2P.Core/LocalDb.vb
Option Strict On
Imports System
Imports System.Data
Imports System.Data.SQLite
Imports System.IO
Imports System.Globalization

Namespace ChatP2P.Core

    ''' <summary>
    ''' Initialisation et helpers de bas niveau pour SQLite (fichier .db dans AppData\ChatP2P).
    ''' </summary>
    Public Module LocalDb

        Private _dbPath As String
        Private _connString As String
        Private ReadOnly _gate As New Object()

        ''' <summary>Chemin du fichier .db (lecture seule).</summary>
        Public ReadOnly Property DbPath As String
            Get
                Return _dbPath
            End Get
        End Property

        ''' <summary>
        ''' Initialise la base si nécessaire (création fichier + tables) + migrations légères.
        ''' </summary>
        Public Sub Init(Optional appFolderName As String = "ChatP2P", Optional dbFileName As String = "chat.db")
            Dim dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appFolderName)
            If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
            _dbPath = Path.Combine(dir, dbFileName)
            _connString = $"Data Source={_dbPath};Version=3;Pooling=True;Journal Mode=WAL;"

            Dim isNew As Boolean = Not File.Exists(_dbPath)
            If isNew Then
                SQLiteConnection.CreateFile(_dbPath)
            End If

            ' PRAGMA de base
            Using cn = Open()
                Using cmd = cn.CreateCommand()
                    cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;"
                    cmd.ExecuteNonQuery()
                End Using
            End Using

            If isNew Then
                CreateSchema()
            Else
                EnsureSchemaUpgrades()
            End If
        End Sub

        ' ====================== Schéma ======================

        Private Sub CreateSchema()
            Const ddl As String =
"CREATE TABLE IF NOT EXISTS Peers(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL UNIQUE,
    Fingerprint TEXT NULL,             -- legacy (non utilisé)
    Verified INTEGER NOT NULL DEFAULT 0,
    CreatedUtc TEXT NULL,              -- NEW
    LastSeenUtc TEXT NULL,
    Trusted INTEGER NOT NULL DEFAULT 0,        -- NEW
    TrustNote TEXT NULL,                        -- NEW
    DtlsFingerprint TEXT NULL                  -- NEW
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
);

-- NEW: clés par pair (pour révocation/traçabilité)
CREATE TABLE IF NOT EXISTS PeerKeys(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PeerName TEXT NOT NULL,
    Kind TEXT NOT NULL,                 -- e.g. 'DTLS', 'PQ', 'X25519'
    Public BLOB NULL,
    CreatedUtc TEXT NOT NULL,
    Revoked INTEGER NOT NULL DEFAULT 0,
    RevokedUtc TEXT NULL,
    Note TEXT NULL
);

CREATE INDEX IF NOT EXISTS IX_PeerKeys_Peer ON PeerKeys(PeerName, CreatedUtc);

-- NEW: état de session par pair (optionnel)
CREATE TABLE IF NOT EXISTS Sessions(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PeerName TEXT NOT NULL UNIQUE,
    State BLOB NULL,
    CreatedUtc TEXT NOT NULL
);"
            ExecNonQueryBatch(ddl)
        End Sub

        ''' <summary>Migrations légères si la DB existe déjà.</summary>
        Private Sub EnsureSchemaUpgrades()
            SyncLock _gate
                Using cn = Open()
                    ' Peers.Trusted
                    If Not ColumnExists(cn, "Peers", "Trusted") Then
                        ExecNonQuery("ALTER TABLE Peers ADD COLUMN Trusted INTEGER NOT NULL DEFAULT 0;")
                    End If
                    ' Peers.TrustNote
                    If Not ColumnExists(cn, "Peers", "TrustNote") Then
                        ExecNonQuery("ALTER TABLE Peers ADD COLUMN TrustNote TEXT NULL;")
                    End If
                    ' Peers.DtlsFingerprint
                    If Not ColumnExists(cn, "Peers", "DtlsFingerprint") Then
                        ExecNonQuery("ALTER TABLE Peers ADD COLUMN DtlsFingerprint TEXT NULL;")
                    End If
                    ' Peers.CreatedUtc
                    If Not ColumnExists(cn, "Peers", "CreatedUtc") Then
                        ExecNonQuery("ALTER TABLE Peers ADD COLUMN CreatedUtc TEXT NULL;")
                    End If
                    ' Peers.LastSeenUtc (par prudence sur d’anciennes DB)
                    If Not ColumnExists(cn, "Peers", "LastSeenUtc") Then
                        ExecNonQuery("ALTER TABLE Peers ADD COLUMN LastSeenUtc TEXT NULL;")
                    End If
                End Using
            End SyncLock

            ' (Ré)assure les tables récentes
            ExecNonQueryBatch(
"CREATE TABLE IF NOT EXISTS PeerKeys(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PeerName TEXT NOT NULL,
    Kind TEXT NOT NULL,
    Public BLOB NULL,
    CreatedUtc TEXT NOT NULL,
    Revoked INTEGER NOT NULL DEFAULT 0,
    RevokedUtc TEXT NULL,
    Note TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_PeerKeys_Peer ON PeerKeys(PeerName, CreatedUtc);
CREATE TABLE IF NOT EXISTS Sessions(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PeerName TEXT NOT NULL UNIQUE,
    State BLOB NULL,
    CreatedUtc TEXT NOT NULL
);")
        End Sub

        Private Function ColumnExists(cn As SQLiteConnection, table As String, column As String) As Boolean
            Using cmd = cn.CreateCommand()
                cmd.CommandText = "PRAGMA table_info(" & table & ");"
                Using rd = cmd.ExecuteReader()
                    While rd.Read()
                        Dim colName = Convert.ToString(rd("name"), CultureInfo.InvariantCulture)
                        If String.Equals(colName, column, StringComparison.OrdinalIgnoreCase) Then
                            Return True
                        End If
                    End While
                End Using
            End Using
            Return False
        End Function

        ' ====================== Bas niveau ======================

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

        ' Shorthand paramètres
        Public Function P(name As String, value As Object) As SQLiteParameter
            Return New SQLiteParameter(name, value)
        End Function

        ' ====================== Helpers métier ======================

        ''' <summary>Crée le peer s’il n’existe pas (avec CreatedUtc/LastSeenUtc) et met à jour LastSeenUtc.</summary>
        Public Sub EnsurePeer(name As String)
            If String.IsNullOrWhiteSpace(name) Then Return
            Dim nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)

            ' Création si absent
            ExecNonQuery("
                INSERT OR IGNORE INTO Peers(Name, CreatedUtc, LastSeenUtc)
                VALUES(@n, @ts, @ts);",
                P("@n", name), P("@ts", nowIso))

            ' Mise à jour du LastSeen à chaque passage
            ExecNonQuery("
                UPDATE Peers SET LastSeenUtc=@ts WHERE Name=@n;",
                P("@ts", nowIso), P("@n", name))
        End Sub

        Public Function GetPeerTrusted(name As String) As Boolean
            Dim o = ExecScalar(Of Object)("SELECT Trusted FROM Peers WHERE Name=@n;", P("@n", name))
            If o Is Nothing OrElse o Is DBNull.Value Then Return False
            Return Convert.ToInt32(o, CultureInfo.InvariantCulture) <> 0
        End Function

        ''' <summary>
        ''' Met à jour la confiance et/ou la note. Les paramètres Nothing sont ignorés.
        ''' </summary>
        Public Sub UpsertPeerTrust(name As String,
                                   Optional trusted As Boolean? = Nothing,
                                   Optional note As String = Nothing)
            EnsurePeer(name)
            If trusted.HasValue Then
                ExecNonQuery("UPDATE Peers SET Trusted=@t WHERE Name=@n;",
                             P("@t", If(trusted.Value, 1, 0)), P("@n", name))
            End If
            If note IsNot Nothing Then
                ExecNonQuery("UPDATE Peers SET TrustNote=@x WHERE Name=@n;",
                             P("@x", note), P("@n", name))
            End If
        End Sub

        Public Function GetPeerDtlsFp(name As String) As String
            Return ExecScalar(Of String)("SELECT DtlsFingerprint FROM Peers WHERE Name=@n;", P("@n", name))
        End Function

        Public Sub SetPeerDtlsFp(name As String, fp As String)
            EnsurePeer(name)
            ExecNonQuery("UPDATE Peers SET DtlsFingerprint=@f WHERE Name=@n;",
                         P("@f", fp), P("@n", name))
        End Sub

        ''' <summary>Supprime l’état de session pour forcer un nouveau handshake.</summary>
        Public Sub DeleteSession(peer As String)
            ExecNonQuery("DELETE FROM Sessions WHERE PeerName=@p;", P("@p", peer))
        End Sub

        ''' <summary>Marque une clé comme révoquée.</summary>
        Public Sub RevokePeerKey(id As Long, reason As String)
            Dim nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            ExecNonQuery("UPDATE PeerKeys SET Revoked=1, RevokedUtc=@ts, Note=COALESCE(Note,'')||' | '||@r WHERE Id=@id;",
                         P("@ts", nowIso), P("@r", If(reason, "")), P("@id", id))
        End Sub

    End Module

End Namespace
