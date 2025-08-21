Option Strict On
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports ChatP2P.App.Protocol
Imports Proto = ChatP2P.App.Protocol.Tags

Namespace ChatP2P.App
    ''' <summary>
    ''' Hub TCP très simple : relaie chat, privé, fichiers et signaux ICE.
    ''' Diffuse la liste des pairs à chaque changement.
    ''' </summary>
    Public Class RelayHub

        ' --- Events vers l’UI host ---
        Public Event PeerListUpdated(names As List(Of String))
        Public Event LogLine(line As String)
        Public Event MessageArrived(sender As String, text As String)
        Public Event PrivateArrived(sender As String, dest As String, text As String)

        Public Delegate Sub FileSignalEventHandler(raw As String)
        Public Event FileSignal As FileSignalEventHandler

        Public Delegate Sub IceSignalEventHandler(raw As String)
        Public Event IceSignal As IceSignalEventHandler

        Public Property HostDisplayName As String = "Server"

        ' --- Etat interne ---
        Private ReadOnly _gate As New Object()
        Private ReadOnly _clients As New Dictionary(Of String, TcpClient)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _rev As New Dictionary(Of TcpClient, String)()
        Private ReadOnly _listener As TcpListener
        Private _cts As CancellationTokenSource

        Public Sub New(port As Integer)
            _listener = New TcpListener(IPAddress.Any, port)
        End Sub

        Public Sub Start()
            _cts = New CancellationTokenSource()
            _listener.Start()
            RaiseEvent LogLine($"[Hub] Listening on port {DirectPort()}")
            Dim _ignore As Task = AcceptLoop(_cts.Token)
        End Sub

        Public Sub [Stop]()
            Try
                _cts?.Cancel()
                _listener.Stop()
                Dim all As TcpClient()
                SyncLock _gate
                    all = _clients.Values.ToArray()
                    _clients.Clear()
                    _rev.Clear()
                End SyncLock
                For Each c In all
                    Try : c.Close() : Catch : End Try
                Next
            Catch
            End Try
        End Sub

        Private Function DirectPort() As Integer
            Return DirectCast(_listener.LocalEndpoint, IPEndPoint).Port
        End Function

        ' === accept ===
        Private Async Function AcceptLoop(ct As CancellationToken) As Task
            While Not ct.IsCancellationRequested
                Try
                    Dim cli = Await _listener.AcceptTcpClientAsync().ConfigureAwait(False)
                    Dim name = $"peer_{Guid.NewGuid():N}"

                    SyncLock _gate
                        _clients(name) = cli
                        _rev(cli) = name
                    End SyncLock

                    RaiseEvent LogLine($"[Hub] Nouveau client connecté: {name}")
                    BroadcastPeers()

                    Dim _ignore As Task = ListenClientLoopAsync(cli, ct)
                Catch ex As Exception
                    If Not ct.IsCancellationRequested Then
                        RaiseEvent LogLine($"[Hub] Accept error: {ex.Message}")
                    End If
                End Try
            End While
        End Function

        Private Async Function ListenClientLoopAsync(cli As TcpClient, ct As CancellationToken) As Task
            Dim meName As String
            SyncLock _gate
                meName = If(_rev.ContainsKey(cli), _rev(cli), "?")
            End SyncLock

            Dim s = cli.GetStream()
            Dim buf(8191) As Byte

            Try
                While Not ct.IsCancellationRequested
                    Dim read = Await s.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(False)
                    If read <= 0 Then Exit While
                    Dim msg = Encoding.UTF8.GetString(buf, 0, read)

                    ' log court
                    Dim preview = msg
                    If preview.Length > 180 Then preview = preview.Substring(0, 180) & "…"
                    RaiseEvent LogLine($"[Hub] {meName} → {preview}")

                    If msg.StartsWith(Proto.TAG_NAME) Then
                        Dim newName = msg.Substring(Proto.TAG_NAME.Length).Trim()
                        If newName <> "" Then
                            SyncLock _gate
                                If _clients.ContainsKey(meName) AndAlso _clients(meName) Is cli Then
                                    _clients.Remove(meName)
                                    Dim finalName = newName
                                    If _clients.ContainsKey(finalName) Then
                                        finalName &= "_" & DateTime.UtcNow.Ticks.ToString()
                                    End If
                                    _clients(finalName) = cli
                                    _rev(cli) = finalName
                                    meName = finalName
                                End If
                            End SyncLock
                            RaiseEvent LogLine($"[Hub] Client renommé en {meName}")
                            BroadcastPeers()
                        End If

                    ElseIf msg.StartsWith(Proto.TAG_MSG) Then
                        ' broadcast à tous sauf l’émetteur
                        Await BroadcastStringAsync(msg, exclude:=meName).ConfigureAwait(False)
                        ' et l’UI host
                        Dim parts = msg.Split(":"c, 3)
                        If parts.Length = 3 Then RaiseEvent MessageArrived(parts(1), parts(2))

                    ElseIf msg.StartsWith(Proto.TAG_PRIV) Then
                        ' PRIV:sender:dest:body  → route uniquement au dest
                        Dim rest = msg.Substring(Proto.TAG_PRIV.Length)
                        Dim parts = rest.Split(":"c, 3)
                        If parts.Length = 3 Then
                            Dim sender = parts(0)
                            Dim dest = parts(1)
                            Await SendStringToAsync(dest, msg).ConfigureAwait(False)
                            RaiseEvent PrivateArrived(sender, dest, parts(2))
                        End If

                    ElseIf msg.StartsWith(Proto.TAG_FILEMETA) OrElse
                           msg.StartsWith(Proto.TAG_FILECHUNK) OrElse
                           msg.StartsWith(Proto.TAG_FILEEND) Then
                        ' Même logique : toujours "…:tid:from:dest:…" pour META, et CHUNK/END : route par tid côté client,
                        ' ici on route par dest quand présent (META). Pour CHUNK/END, on rebalance à tous sauf émetteur
                        ' (les clients font le tri par tid).
                        If msg.StartsWith(Proto.TAG_FILEMETA) Then
                            Dim p = msg.Split(":"c, 6)
                            If p.Length >= 4 Then
                                Dim dest = p(3)
                                Await SendStringToAsync(dest, msg).ConfigureAwait(False)
                            End If
                        Else
                            Await BroadcastStringAsync(msg, exclude:=meName).ConfigureAwait(False)
                        End If
                        RaiseEvent FileSignal(msg)

                    ElseIf msg.StartsWith(Proto.TAG_ICE_OFFER) OrElse
                           msg.StartsWith(Proto.TAG_ICE_ANSWER) OrElse
                           msg.StartsWith(Proto.TAG_ICE_CAND) Then
                        ' Format attendu: ICE_XXX:from:to:b64  → route au "to"
                        Dim body = msg.Substring(msg.IndexOf(":"c) + 1)
                        Dim p = body.Split(":"c, 3)
                        If p.Length = 3 Then
                            Dim toPeer = p(1)
                            Await SendStringToAsync(toPeer, msg).ConfigureAwait(False)
                        End If
                        RaiseEvent IceSignal(msg)
                    End If
                End While
            Catch ex As Exception
                RaiseEvent LogLine($"[Hub] {meName} déconnecté: {ex.Message}")
            Finally
                SyncLock _gate
                    If _clients.ContainsKey(meName) AndAlso _clients(meName) Is cli Then
                        _clients.Remove(meName)
                    End If
                    If _rev.ContainsKey(cli) Then _rev.Remove(cli)
                End SyncLock
                BroadcastPeers()
                Try : cli.Close() : Catch : End Try
            End Try
        End Function

        ' ========= envois =========

        Private Async Function BroadcastStringAsync(msg As String, Optional exclude As String = Nothing) As Task
            Dim data = Encoding.UTF8.GetBytes(msg)
            Dim targets As List(Of TcpClient)
            SyncLock _gate
                targets = _clients.Where(Function(kv) Not String.Equals(kv.Key, exclude, StringComparison.OrdinalIgnoreCase)).
                                   Select(Function(kv) kv.Value).ToList()
            End SyncLock
            For Each c In targets
                Try
                    Dim ns = c.GetStream()
                    Await ns.WriteAsync(data, 0, data.Length).ConfigureAwait(False)
                Catch
                End Try
            Next
        End Function

        Private Async Function SendStringToAsync(dest As String, msg As String) As Task
            Dim data = Encoding.UTF8.GetBytes(msg)
            Dim cli As TcpClient = Nothing
            SyncLock _gate
                If _clients.ContainsKey(dest) Then cli = _clients(dest)
            End SyncLock
            If cli Is Nothing Then Return
            Try
                Dim ns = cli.GetStream()
                Await ns.WriteAsync(data, 0, data.Length).ConfigureAwait(False)
            Catch
            End Try
        End Function

        Public Async Function BroadcastFromHostAsync(data As Byte()) As Task
            Dim targets As List(Of TcpClient)
            SyncLock _gate
                targets = _clients.Values.ToList()
            End SyncLock
            For Each c In targets
                Try
                    Dim ns = c.GetStream()
                    Await ns.WriteAsync(data, 0, data.Length).ConfigureAwait(False)
                Catch
                End Try
            Next
        End Function

        Public Async Function SendToAsync(target As String, data As Byte()) As Task
            Dim cli As TcpClient = Nothing
            SyncLock _gate
                If _clients.ContainsKey(target) Then cli = _clients(target)
            End SyncLock
            If cli Is Nothing Then Return
            Try
                Dim ns = cli.GetStream()
                Await ns.WriteAsync(data, 0, data.Length).ConfigureAwait(False)
            Catch
            End Try
        End Function

        ' === Diffuse la liste des pairs ===
        Private Sub BroadcastPeers()
            Dim names As List(Of String)
            SyncLock _gate
                names = New List(Of String) From {HostDisplayName}
                names.AddRange(_clients.Keys)
            End SyncLock

            ' UI host
            RaiseEvent PeerListUpdated(names)

            ' Envoi aux clients
            Dim msg = Proto.TAG_PEERS & String.Join(";", names)
            Dim _ignore As Task = BroadcastStringAsync(msg)
        End Sub

    End Class
End Namespace
