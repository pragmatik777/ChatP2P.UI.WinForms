' ChatP2P.Core/LocalDb.vb
Option Strict On
Imports System
Imports System.Data
Imports System.Data.SQLite
Imports System.IO

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
        ''' Initialise la base si nécessaire (création fichier + tables).
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
                ' Migration légère possible ici (ALTER TABLE IF NOT EXISTS…) si besoin
            End If
        End Sub

        Private Sub CreateSchema()
            Const ddl As String =
"CREATE TABLE IF NOT EXISTS Peers(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL UNIQUE,
    Fingerprint TEXT NULL,
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
                        If o Is Nothing OrElse o Is DBNull.Value Then Return Nothing
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

        ' Shorthands paramètres
        Public Function P(name As String, value As Object) As SQLiteParameter
            Return New SQLiteParameter(name, value)
        End Function

    End Module

End Namespace
