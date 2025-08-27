Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports ChatP2P.App.Protocol
Imports Proto = ChatP2P.App.Protocol.Tags

Namespace ChatP2P.App

    Public Class RelayHub

        ' ---- Événements pour la WinForms ----
        Public Event PeerListUpdated(names As List(Of String))
        Public Event LogLine(text As String)
        Public Event MessageArrived(senderName As String, text As String)
        Public Event PrivateArrived(senderName As String, dest As String, body As String)
        Public Event FileSignal(raw As String)
        Public Event IceSignal(raw As String)

        Public Property HostDisplayName As String = ""

        Private ReadOnly _port As Integer
        Private _listener As TcpListener
        Private _cts As CancellationTokenSource

        Private Class ClientConn
            Public Id As String = Guid.NewGuid().ToString("N")
            Public Cli As TcpClient
            Public Net As NetworkStream
            Public Name As String = ""
            Public Buf As New StringBuilder()
            Public Sub CloseQuiet()
                Try : Net?.Close() : Catch : End Try
                Try : Cli?.Close() : Catch : End Try
            End Sub
        End Class

        Private ReadOnly syncObj As New Object()
        Private ReadOnly clients As New Dictionary(Of String, ClientConn)() ' id -> conn
        Private ReadOnly nameToId As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly fileRoutes As New Dictionary(Of String, String)(StringComparer.Ordinal) ' transferId -> destName

        Public Sub New(port As Integer)
            _port = port
        End Sub

        ' ----------------- API -----------------

        Public Sub Start()
            SyncLock syncObj
                If _cts IsNot Nothing Then Return
                _cts = New CancellationTokenSource()
                _listener = New TcpListener(IPAddress.Any, _port)
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, True)
                _listener.Start()
            End SyncLock

            RaiseEvent LogLine($"[HUB] Écoute sur port {_port}")

            ' Accept loop SYNCHRONE sur thread pool
            Task.Run(Sub() AcceptLoop(_cts.Token))
        End Sub

        Public Sub [Stop]()
            Dim localCts As CancellationTokenSource = Nothing
            SyncLock syncObj
                localCts = _cts
                _cts = Nothing
            End SyncLock

            If localCts IsNot Nothing Then
                Try : localCts.Cancel() : Catch : End Try
            End If

            Try
                SyncLock syncObj
                    For Each c In clients.Values.ToList()
                        c.CloseQuiet()
                    Next
                    clients.Clear()
                    nameToId.Clear()
                    fileRoutes.Clear()
                End SyncLock
            Catch
            End Try

            Try : _listener?.Stop() : Catch : End Try
            RaiseEvent LogLine("[HUB] Arrêté")
        End Sub

        Public Function BroadcastFromHostAsync(data As Byte()) As Task
            Return BroadcastRawAsync(data)
        End Function

        Public Async Function SendToAsync(destName As String, data As Byte()) As Task
            Dim target As ClientConn = Nothing
            SyncLock syncObj
                Dim id As String = Nothing
                If nameToId.TryGetValue(destName, id) Then
                    clients.TryGetValue(id, target)
                End If
            End SyncLock

            If target Is Nothing Then
                RaiseEvent LogLine($"[HUB] Échec envoi: pair introuvable: {destName}")
                Return
            End If

            Await SendRawAsync(target, data)
        End Function

        ' ------------- Accept / Clients -------------

        Private Sub AcceptLoop(ct As CancellationToken)
            Try
                While Not ct.IsCancellationRequested
                    Dim cli As TcpClient = Nothing
                    Try
                        cli = _listener.AcceptTcpClient() ' SYNCHRONE
                    Catch ex As SocketException
                        If ct.IsCancellationRequested Then Exit While
                        RaiseEvent LogLine("[HUB] Accept error: " & ex.Message)
                        Continue While
                    Catch ex As ObjectDisposedException
                        Exit While
                    End Try

                    Dim conn As New ClientConn() With {
                        .Cli = cli,
                        .Net = cli.GetStream()
                    }

                    SyncLock syncObj
                        clients(conn.Id) = conn
                    End SyncLock

                    Dim lep = TryCast(cli.Client.LocalEndPoint, IPEndPoint)
                    Dim rep = TryCast(cli.Client.RemoteEndPoint, IPEndPoint)
                    RaiseEvent LogLine($"[HUB] Client connecté (id={conn.Id}, from={rep}, to={lep})")

                    Task.Run(Function() ClientLoopAsync(conn, ct))
                End While
            Catch ex As Exception
                If Not ct.IsCancellationRequested Then
                    RaiseEvent LogLine("[HUB] AcceptLoop fatal: " & ex.Message)
                End If
            End Try
        End Sub

        Private Async Function ClientLoopAsync(c As ClientConn, ct As CancellationToken) As Task
            Try
                Dim buf(8191) As Byte
                While Not ct.IsCancellationRequested
                    Dim n As Integer
#If NET6_0_OR_GREATER Then
                    n = Await c.Net.ReadAsync(buf, 0, buf.Length, ct)
#Else
                    n = Await c.Net.ReadAsync(buf, 0, buf.Length)
#End If
                    If n <= 0 Then Exit While
                    Dim chunk = Encoding.UTF8.GetString(buf, 0, n)
                    c.Buf.Append(chunk)
                    Await ParseClientBufferAsync(c, ct)
                End While
            Catch ex As Exception
                If Not ct.IsCancellationRequested Then
                    RaiseEvent LogLine($"[HUB] Client {If(String.IsNullOrEmpty(c.Name), c.Id, c.Name)}: lecture interrompue: {ex.Message}")
                End If
            Finally
                CleanupClient(c)
            End Try
        End Function

        Private Async Function ParseClientBufferAsync(c As ClientConn, ct As CancellationToken) As Task
            While True
                Dim full = c.Buf.ToString()
                Dim idx = full.IndexOf(vbLf, StringComparison.Ordinal)
                If idx < 0 Then Exit While

                Dim line = full.Substring(0, idx)
                c.Buf.Remove(0, idx + 1)

                Dim msg = Canon(line)
                If msg = "" Then Continue While

                If msg.StartsWith(Proto.TAG_NAME, StringComparison.Ordinal) Then
                    Dim newName = msg.Substring(Proto.TAG_NAME.Length).Trim()
                    HandleSetName(c, newName)

                ElseIf msg.StartsWith(Proto.TAG_MSG, StringComparison.Ordinal) Then
                    Await BroadcastRawAsync(Encoding.UTF8.GetBytes(msg & vbLf))
                    If c.Name <> "" Then
                        Dim payload = msg.Substring(Proto.TAG_MSG.Length)
                        RaiseEvent MessageArrived(c.Name, payload)
                    End If

                ElseIf msg.StartsWith(Proto.TAG_MSG, StringComparison.Ordinal) Then
                    ' Format attendu côté clients: TAG_MSG + "<nom>:<texte>"
                    ' On FIABILISE ici: on impose le nom connu par le hub (c.Name) pour éviter l’usurpation
                    Dim rest = msg.Substring(Proto.TAG_MSG.Length)
                    Dim parts = rest.Split(":"c, 2)
                    Dim fromName As String = If(String.IsNullOrWhiteSpace(c.Name), "", c.Name)
                    Dim body As String = If(parts.Length = 2, parts(1), rest)

                    ' Recompose un message propre garanti: TAG_MSG + hubName:body
                    Dim normalized = $"{Proto.TAG_MSG}{fromName}:{body}{vbLf}"
                    Await BroadcastRawAsync(Encoding.UTF8.GetBytes(normalized))

                    If Not String.IsNullOrWhiteSpace(fromName) Then
                        RaiseEvent MessageArrived(fromName, body)
                    Else
                        RaiseEvent LogLine("[HUB] TAG_MSG reçu mais nom client inconnu (non initialisé ?).")
                    End If

                ElseIf msg.StartsWith(Proto.TAG_ICE_OFFER, StringComparison.Ordinal) OrElse
                       msg.StartsWith(Proto.TAG_ICE_ANSWER, StringComparison.Ordinal) OrElse
                       msg.StartsWith(Proto.TAG_ICE_CAND, StringComparison.Ordinal) Then
                    Dim whichTag As String =
                        If(msg.StartsWith(Proto.TAG_ICE_OFFER, StringComparison.Ordinal), Proto.TAG_ICE_OFFER,
                        If(msg.StartsWith(Proto.TAG_ICE_ANSWER, StringComparison.Ordinal), Proto.TAG_ICE_ANSWER, Proto.TAG_ICE_CAND))
                    Dim p = msg.Substring(whichTag.Length).Split(":"c, 3)
                    If p.Length = 3 Then
                        Dim dest = p(1)
                        If NamesEqual(dest, HostDisplayName) Then
                            RaiseEvent IceSignal(msg)
                        Else
                            Await SendToAsync(dest, Encoding.UTF8.GetBytes(msg & vbLf))
                        End If
                    End If

                ElseIf msg.StartsWith(Proto.TAG_FILEMETA, StringComparison.Ordinal) Then
                    ' transferId:from:dest:filename:size
                    Dim meta = msg.Substring(Proto.TAG_FILEMETA.Length)
                    Dim pr = meta.Split(":"c, 5)
                    If pr.Length = 5 Then
                        Dim transferId = pr(0)
                        Dim dest = pr(2)
                        SyncLock syncObj
                            fileRoutes(transferId) = dest
                        End SyncLock
                        If NamesEqual(dest, HostDisplayName) Then
                            RaiseEvent FileSignal(msg)
                        Else
                            Await SendToAsync(dest, Encoding.UTF8.GetBytes(msg & vbLf))
                        End If
                    End If

                ElseIf msg.StartsWith(Proto.TAG_FILECHUNK, StringComparison.Ordinal) Then
                    ' transferId:base64
                    Dim rest = msg.Substring(Proto.TAG_FILECHUNK.Length)
                    Dim p2 = rest.Split(":"c, 2)
                    If p2.Length = 2 Then
                        Dim transferId = p2(0)
                        Dim dest As String = Nothing
                        SyncLock syncObj
                            fileRoutes.TryGetValue(transferId, dest)
                        End SyncLock
                        If Not String.IsNullOrWhiteSpace(dest) Then
                            If NamesEqual(dest, HostDisplayName) Then
                                RaiseEvent FileSignal(msg)
                            Else
                                Await SendToAsync(dest, Encoding.UTF8.GetBytes(msg & vbLf))
                            End If
                        End If
                    End If

                ElseIf msg.StartsWith(Proto.TAG_FILEEND, StringComparison.Ordinal) Then
                    Dim transferId = msg.Substring(Proto.TAG_FILEEND.Length).Trim()
                    Dim dest As String = Nothing
                    Dim had As Boolean
                    SyncLock syncObj
                        had = fileRoutes.TryGetValue(transferId, dest)
                        If had Then fileRoutes.Remove(transferId)
                    End SyncLock
                    If had AndAlso Not String.IsNullOrWhiteSpace(dest) Then
                        If NamesEqual(dest, HostDisplayName) Then
                            RaiseEvent FileSignal(msg)
                        Else
                            Await SendToAsync(dest, Encoding.UTF8.GetBytes(msg & vbLf))
                        End If
                    End If

                ElseIf msg.StartsWith(Proto.TAG_PEERS, StringComparison.Ordinal) Then
                    ' ignoré côté hub

                Else
                    RaiseEvent LogLine("[HUB] Inconnu: " & msg)
                    Await BroadcastRawAsync(Encoding.UTF8.GetBytes(msg & vbLf))
                End If
            End While
        End Function

        Private Sub HandleSetName(c As ClientConn, newName As String)
            Dim oldName As String = ""
            Dim changed As Boolean = False

            SyncLock syncObj
                oldName = c.Name
                If Not String.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase) Then
                    If Not String.IsNullOrWhiteSpace(oldName) AndAlso
                       nameToId.ContainsKey(oldName) AndAlso nameToId(oldName) = c.Id Then
                        nameToId.Remove(oldName)
                    End If

                    c.Name = newName
                    If Not String.IsNullOrWhiteSpace(newName) Then
                        nameToId(newName) = c.Id
                    End If
                    changed = True
                End If
            End SyncLock

            If changed Then
                RaiseEvent LogLine($"[HUB] Nom client fixé: {oldName} -> {newName}")
                BroadcastPeersWithTag()
                RaisePeerListForHost()
            End If
        End Sub

        Private Sub CleanupClient(c As ClientConn)
            Dim removedName As String = ""
            SyncLock syncObj
                If clients.ContainsKey(c.Id) Then clients.Remove(c.Id)
                If Not String.IsNullOrWhiteSpace(c.Name) AndAlso nameToId.ContainsKey(c.Name) AndAlso nameToId(c.Name) = c.Id Then
                    removedName = c.Name
                    nameToId.Remove(c.Name)
                End If
            End SyncLock

            c.CloseQuiet()
            RaiseEvent LogLine($"[HUB] Client déconnecté {(If(removedName = "", c.Id, removedName))}")

            BroadcastPeersWithTag()
            RaisePeerListForHost()
        End Sub

        Private Sub BroadcastPeersWithTag()
            Dim names = GetPeerNames()
            Dim line = Proto.TAG_PEERS & String.Join(";", names) & vbLf
            Dim bytes = Encoding.UTF8.GetBytes(line)
            Task.Run(Function() BroadcastRawAsync(bytes)) ' fire & forget
        End Sub

        Private Sub RaisePeerListForHost()
            Dim names = GetPeerNames()
            RaiseEvent PeerListUpdated(names)
        End Sub

        Private Function GetPeerNames() As List(Of String)
            Dim list As New List(Of String)
            SyncLock syncObj
                For Each c In clients.Values
                    If Not String.IsNullOrWhiteSpace(c.Name) Then list.Add(c.Name)
                Next
            End SyncLock
            If Not String.IsNullOrWhiteSpace(HostDisplayName) Then list.Add(HostDisplayName)
            Return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        Private Async Function BroadcastRawAsync(data As Byte()) As Task
            Dim targets As List(Of ClientConn)
            SyncLock syncObj
                targets = clients.Values.ToList()
            End SyncLock

            For Each c In targets
                Try
                    Await SendRawAsync(c, data)
                Catch ex As Exception
                    RaiseEvent LogLine($"[HUB] Broadcast vers {If(String.IsNullOrEmpty(c.Name), c.Id, c.Name)}: {ex.Message}")
                End Try
            Next
        End Function

        Private Async Function SendRawAsync(c As ClientConn, data As Byte()) As Task
            If c Is Nothing OrElse c.Net Is Nothing Then Return
            Await c.Net.WriteAsync(data, 0, data.Length)
#If NET6_0_OR_GREATER Then
            Await c.Net.FlushAsync()
#Else
            c.Net.Flush()
#End If
        End Function

        Private Shared Function Canon(s As String) As String
            If s Is Nothing Then Return String.Empty
            Dim r = s.Replace(vbCr, "")
            While r.EndsWith(vbLf, StringComparison.Ordinal)
                r = r.Substring(0, r.Length - 1)
            End While
            Return r.TrimEnd()
        End Function

        Private Shared Function NamesEqual(a As String, b As String) As Boolean
            Return String.Equals(If(a, ""), If(b, ""), StringComparison.OrdinalIgnoreCase)
        End Function

    End Class

End Namespace
