Option Strict On
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading

Namespace ChatP2P.App
    Public Class RelayHub

        ' --- Events exposés vers l’UI ---
        Public Event PeerListUpdated(names As List(Of String))
        Public Event LogLine(line As String)
        Public Event MessageArrived(sender As String, text As String)
        Public Event PrivateArrived(sender As String, dest As String, text As String)
        Public Event FileSignal(raw As String)
        Public Event IceSignal(raw As String)

        Public Property HostDisplayName As String

        Private ReadOnly _clients As New Dictionary(Of String, NetworkStream)
        Private ReadOnly _listener As TcpListener
        Private _cts As CancellationTokenSource

        Public Sub New(port As Integer)
            _listener = New TcpListener(IPAddress.Any, port)
        End Sub

        Public Sub Start()
            _cts = New CancellationTokenSource()
            _listener.Start()
            Task.Run(Function() AcceptLoop(_cts.Token))
        End Sub

        Public Sub [Stop]()
            Try
                _cts?.Cancel()
                _listener.Stop()
            Catch
            End Try
        End Sub

        Private Async Function AcceptLoop(ct As CancellationToken) As Task
            While Not ct.IsCancellationRequested
                Try
                    Dim client = Await _listener.AcceptTcpClientAsync()
                    Dim s = client.GetStream()
                    Dim clientName = "peer" & Guid.NewGuid().ToString("N")

                    SyncLock _clients
                        _clients(clientName) = s
                    End SyncLock

                    RaiseEvent LogLine($"[RelayHub] Nouveau client connecté : {clientName}")

                    ' Fire-and-forget
                    _ = Task.Run(Function() ListenClientLoopAsync(s, clientName, ct))
                Catch ex As Exception
                    If Not ct.IsCancellationRequested Then
                        RaiseEvent LogLine($"[RelayHub] Erreur AcceptLoop: {ex.Message}")
                    End If
                End Try
            End While
        End Function

        Private Async Function ListenClientLoopAsync(s As NetworkStream, clientName As String, ct As CancellationToken) As Task
            Dim buffer(4096) As Byte
            Try
                While Not ct.IsCancellationRequested
                    Dim read = Await s.ReadAsync(buffer, 0, buffer.Length, ct)
                    If read <= 0 Then Exit While

                    Dim msg = Encoding.UTF8.GetString(buffer, 0, read)
                    RaiseEvent LogLine($"[RelayHub] {clientName} → {msg.Substring(0, Math.Min(80, msg.Length))}")

                    If msg.StartsWith(Protocol.Tags.TAG_NAME) Then
                        Dim newName = msg.Substring(Protocol.Tags.TAG_NAME.Length).Trim()
                        If Not String.IsNullOrEmpty(newName) Then
                            SyncLock _clients
                                _clients.Remove(clientName)
                                _clients(newName) = s
                                clientName = newName
                            End SyncLock
                            RaiseEvent LogLine($"[RelayHub] Client renommé en {clientName}")
                            BroadcastPeers()
                        End If

                    ElseIf msg.StartsWith(Protocol.Tags.TAG_MSG) Then
                        RaiseEvent MessageArrived(clientName, msg.Substring(Protocol.Tags.TAG_MSG.Length))

                    ElseIf msg.StartsWith(Protocol.Tags.TAG_PRIV) Then
                        Dim parts = msg.Substring(Protocol.Tags.TAG_PRIV.Length).Split(":"c, 3)
                        If parts.Length = 3 Then
                            RaiseEvent PrivateArrived(parts(0), parts(1), parts(2))
                        End If

                    ElseIf msg.StartsWith(Protocol.Tags.TAG_FILEMETA) _
                        OrElse msg.StartsWith(Protocol.Tags.TAG_FILECHUNK) _
                        OrElse msg.StartsWith(Protocol.Tags.TAG_FILEEND) Then
                        RaiseEvent FileSignal(msg)

                    ElseIf msg.StartsWith(Protocol.Tags.TAG_ICE_OFFER) _
                        OrElse msg.StartsWith(Protocol.Tags.TAG_ICE_ANSWER) _
                        OrElse msg.StartsWith(Protocol.Tags.TAG_ICE_CAND) Then
                        RaiseEvent IceSignal(msg)

                    End If
                End While
            Catch ex As Exception
                RaiseEvent LogLine($"[RelayHub] {clientName} déconnecté: {ex.Message}")
            Finally
                SyncLock _clients
                    If _clients.ContainsKey(clientName) Then _clients.Remove(clientName)
                End SyncLock
                BroadcastPeers()
            End Try
        End Function

        Private Async Function BroadcastAsync(msg As String, sender As String) As Task
            Dim data = Encoding.UTF8.GetBytes(msg)
            Dim targets As New List(Of NetworkStream)

            SyncLock _clients
                For Each kvp In _clients
                    If kvp.Key <> sender Then targets.Add(kvp.Value)
                Next
            End SyncLock

            For Each t In targets
                Try
                    Await t.WriteAsync(data, 0, data.Length)
                Catch
                End Try
            Next
        End Function

        Public Async Function BroadcastFromHostAsync(data As Byte()) As Task
            Dim targets As New List(Of NetworkStream)
            SyncLock _clients
                For Each kvp In _clients
                    targets.Add(kvp.Value)
                Next
            End SyncLock

            For Each t In targets
                Try
                    Await t.WriteAsync(data, 0, data.Length)
                Catch
                End Try
            Next
        End Function

        Public Async Function SendToAsync(target As String, data As Byte()) As Task
            Dim s As NetworkStream = Nothing
            SyncLock _clients
                If _clients.ContainsKey(target) Then
                    s = _clients(target)
                End If
            End SyncLock
            If s Is Nothing Then Exit Function

            Try
                Await s.WriteAsync(data, 0, data.Length)
            Catch
            End Try
        End Function

        Private Sub BroadcastPeers()
            Dim peers As String
            SyncLock _clients
                peers = String.Join(";", _clients.Keys)
            End SyncLock
            Dim msg = Protocol.Tags.TAG_PEERS & peers
            RaiseEvent PeerListUpdated(_clients.Keys.ToList())
            _ = BroadcastAsync(msg, "")
        End Sub

        Public Sub AddClient(name As String, s As IO.Stream)
            Dim ns = TryCast(s, NetworkStream)
            If ns Is Nothing Then
                Dim ms As New IO.MemoryStream()
                s.CopyTo(ms)
                ns = New NetworkStream(New Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            End If
            SyncLock _clients
                _clients(name) = ns
            End SyncLock
            BroadcastPeers()
        End Sub

    End Class
End Namespace
