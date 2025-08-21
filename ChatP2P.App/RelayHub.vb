Option Strict On
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports ChatP2P.App.Protocol
Imports Proto = ChatP2P.App.Protocol.Tags

Namespace ChatP2P.App

    ''' <summary>
    ''' Hub TCP très simple : accepte des clients, gère le renommage (NAME:xxx),
    ''' diffuse la liste des pairs, relaye les messages (MSG/PRIV/FILE*/ICE_*).
    ''' Tous les messages sont des LIGNES UTF8 terminées par \n (vbLf).
    ''' </summary>
    Public Class RelayHub

        ' ---- Events vers l’UI ----
        Public Event PeerListUpdated(names As List(Of String))
        Public Event LogLine(line As String)
        Public Event MessageArrived(sender As String, text As String)
        Public Event PrivateArrived(sender As String, dest As String, text As String)

        Public Delegate Sub FileSignalEventHandler(raw As String)
        Public Event FileSignal As FileSignalEventHandler

        Public Delegate Sub IceSignalEventHandler(raw As String)
        Public Event IceSignal As IceSignalEventHandler

        Public Property HostDisplayName As String = "Host"

        ' ---- Etat interne ----
        Private ReadOnly _gate As New Object()
        Private ReadOnly _clients As New Dictionary(Of String, TcpClient)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _rev As New Dictionary(Of TcpClient, String)()
        Private ReadOnly _listener As TcpListener
        Private _cts As CancellationTokenSource

        Public Sub New(port As Integer)
            _listener = New TcpListener(IPAddress.Any, port)
        End Sub

        ' Petit utilitaire: s’assure que le buffer finit par \n
        Private Shared Function EnsureLine(data As Byte()) As Byte()
            If data Is Nothing OrElse data.Length = 0 Then
                Return Encoding.UTF8.GetBytes(vbLf)
            End If
            If data(data.Length - 1) = AscW(vbLf) Then Return data
            Dim s = Encoding.UTF8.GetString(data)
            Return Encoding.UTF8.GetBytes(s & vbLf)
        End Function

        Public Sub Start()
            _cts = New CancellationTokenSource()
            _listener.Start()
            RaiseEvent LogLine($"[Hub] Listening on port {_DirectCastPort()}")
            ' fire-and-forget propre
            AcceptLoop(_cts.Token)
        End Sub

        Public Sub [Stop]()
            Try
                _cts?.Cancel()
                _listener.Stop()
            Catch
            End Try
        End Sub

        Private Function _DirectCastPort() As Integer
            Try
                Dim ep = DirectCast(_listener.LocalEndpoint, IPEndPoint)
                Return ep.Port
            Catch
                Return -1
            End Try
        End Function

        ' === accept ===
        Private Async Sub AcceptLoop(ct As CancellationToken)
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

                    ' fire-and-forget propre
                    ListenClientLoopAsync(cli, ct)
                Catch ex As Exception
                    If Not ct.IsCancellationRequested Then
                        RaiseEvent LogLine($"[Hub] Accept error: {ex.Message}")
                    End If
                End Try
            End While
        End Sub

        ' lecture par LIGNES (UTF8 + \n)
        Private Async Sub ListenClientLoopAsync(cli As TcpClient, ct As CancellationToken)
            Dim ns As NetworkStream = Nothing
            Dim sb As New StringBuilder()
            Dim myName As String = ""
            Try
                ns = cli.GetStream()
                Dim buf(4095) As Byte

                While Not ct.IsCancellationRequested AndAlso cli.Connected
                    Dim read = Await ns.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(False)
                    If read <= 0 Then Exit While

                    Dim chunk = Encoding.UTF8.GetString(buf, 0, read)
                    ' découpe par lignes
                    Dim lines = ExtractLines(sb, chunk)
                    For Each msg In lines
                        Dim currentName As String
                        SyncLock _gate
                            currentName = If(_rev.ContainsKey(cli), _rev(cli), "")
                        End SyncLock

                        RaiseEvent LogLine($"[Hub] {If(currentName, "?")} → {Trunc80(msg)}")

                        If msg.StartsWith(Proto.TAG_NAME) Then
                            Dim newName = msg.Substring(Proto.TAG_NAME.Length).Trim()
                            If newName <> "" Then
                                SyncLock _gate
                                    Dim oldName As String = If(_rev.ContainsKey(cli), _rev(cli), "")
                                    If oldName <> "" AndAlso _clients.ContainsKey(oldName) Then
                                        _clients.Remove(oldName)
                                    End If
                                    ' collision éventuelle
                                    Dim finalName = newName
                                    If _clients.ContainsKey(finalName) Then
                                        finalName &= "_" & DateTime.UtcNow.Ticks.ToString()
                                    End If
                                    _clients(finalName) = cli
                                    _rev(cli) = finalName
                                    myName = finalName
                                End SyncLock
                                RaiseEvent LogLine($"[Hub] Client renommé en {myName}")
                                BroadcastPeers()
                            End If

                        ElseIf msg.StartsWith(Proto.TAG_MSG) Then
                            ' MSG:sender:text  => broadcast
                            RaiseEvent MessageArrived(myName, msg.Substring(Proto.TAG_MSG.Length))
                            Await BroadcastAsync(Encoding.UTF8.GetBytes(msg), myName).ConfigureAwait(False)

                        ElseIf msg.StartsWith(Proto.TAG_PRIV) Then
                            ' PRIV:sender:dest:text
                            Dim rest = msg.Substring(Proto.TAG_PRIV.Length)
                            Dim parts = rest.Split(":"c, 3)
                            If parts.Length = 3 Then
                                Dim senderName = parts(0)
                                Dim dest = parts(1)
                                Dim body = parts(2)
                                RaiseEvent PrivateArrived(senderName, dest, body)
                                Await SendToAsync(dest, Encoding.UTF8.GetBytes(msg)).ConfigureAwait(False)
                            End If

                        ElseIf msg.StartsWith(Proto.TAG_FILEMETA) _
                            OrElse msg.StartsWith(Proto.TAG_FILECHUNK) _
                            OrElse msg.StartsWith(Proto.TAG_FILEEND) Then
                            RaiseEvent FileSignal(msg)
                            ' forward au destinataire si PRIV dans la ligne (META inclut le dest)
                            If msg.StartsWith(Proto.TAG_FILEMETA) Then
                                ' FILEMETA:tid:from:dest:filename:size
                                Dim p = msg.Split(":"c, 6)
                                If p.Length >= 6 Then
                                    Dim dest = p(3)
                                    Await SendToAsync(dest, Encoding.UTF8.GetBytes(msg)).ConfigureAwait(False)
                                End If
                            Else
                                ' CHUNK/END : broadcast intelligent (simple) : tout le monde reçoit (client côté UI filtre)
                                Await BroadcastAsync(Encoding.UTF8.GetBytes(msg), myName).ConfigureAwait(False)
                            End If

                        ElseIf msg.StartsWith(Proto.TAG_ICE_OFFER) _
                            OrElse msg.StartsWith(Proto.TAG_ICE_ANSWER) _
                            OrElse msg.StartsWith(Proto.TAG_ICE_CAND) Then
                            RaiseEvent IceSignal(msg)
                            ' FORMAT: ICE_XXX:from:to:BASE64
                            Dim body = msg
                            Dim p = body.Substring(body.IndexOf(":"c) + 1).Split(":"c, 3)
                            If p.Length = 3 Then
                                Dim toName = p(1)
                                Await SendToAsync(toName, Encoding.UTF8.GetBytes(msg)).ConfigureAwait(False)
                            End If

                        ElseIf msg.StartsWith(Proto.TAG_PEERS) Then
                            ' ignoré (la liste est envoyée par hub)
                        End If
                    Next
                End While
            Catch ex As Exception
                RaiseEvent LogLine($"[Hub] client loop err: {ex.Message}")
            Finally
                ' cleanup
                SyncLock _gate
                    If _rev.ContainsKey(cli) Then
                        Dim n = _rev(cli)
                        _rev.Remove(cli)
                        If _clients.ContainsKey(n) Then _clients.Remove(n)
                    End If
                End SyncLock
                BroadcastPeers()
                Try
                    ns?.Close()
                Catch
                End Try
                Try
                    cli.Close()
                Catch
                End Try
            End Try
        End Sub

        ' Découpe un flux texte en lignes (\n)
        Private Shared Function ExtractLines(sb As StringBuilder, chunk As String) As List(Of String)
            sb.Append(chunk)
            Dim res As New List(Of String)
            While True
                Dim txt = sb.ToString()
                Dim idx = txt.IndexOf(vbLf, StringComparison.Ordinal)
                If idx < 0 Then Exit While
                Dim line = txt.Substring(0, idx)
                res.Add(line)
                sb.Remove(0, idx + 1)
            End While
            Return res
        End Function

        Private Shared Function Trunc80(s As String) As String
            If s Is Nothing Then Return ""
            If s.Length <= 80 Then Return s
            Return s.Substring(0, 80)
        End Function

        ' --- Broadcast texte (string) au format ligne ---
        Private Async Function BroadcastAsync(payload As Byte(), sender As String) As Task
            Dim data = EnsureLine(payload)
            Dim targets As List(Of TcpClient)
            SyncLock _gate
                targets = _clients.Where(Function(kv) Not kv.Key.Equals(sender, StringComparison.OrdinalIgnoreCase)) _
                                  .Select(Function(kv) kv.Value).ToList()
            End SyncLock
            For Each c In targets
                Try
                    Dim ns = c.GetStream()
                    Await ns.WriteAsync(data, 0, data.Length).ConfigureAwait(False)
                Catch
                End Try
            Next
        End Function

        Public Async Function BroadcastFromHostAsync(data As Byte()) As Task
            Dim payload = EnsureLine(data)
            Dim targets As List(Of TcpClient)
            SyncLock _gate
                targets = _clients.Values.ToList()
            End SyncLock
            For Each c In targets
                Try
                    Dim ns = c.GetStream()
                    Await ns.WriteAsync(payload, 0, payload.Length).ConfigureAwait(False)
                Catch
                End Try
            Next
        End Function

        Public Async Function SendToAsync(target As String, data As Byte()) As Task
            Dim payload = EnsureLine(data)
            Dim cli As TcpClient = Nothing
            SyncLock _gate
                If _clients.ContainsKey(target) Then cli = _clients(target)
            End SyncLock
            If cli Is Nothing Then Exit Function
            Try
                Dim ns = cli.GetStream()
                Await ns.WriteAsync(payload, 0, payload.Length).ConfigureAwait(False)
            Catch
            End Try
        End Function

        ' --- Envoie la liste des pairs (TAG_PEERS + nomHost + noms clients) ---
        Private Sub BroadcastPeers()
            Dim names As New List(Of String)
            SyncLock _gate
                names.Add(HostDisplayName)
                names.AddRange(_clients.Keys)
            End SyncLock

            RaiseEvent PeerListUpdated(names)

            Dim msg = Proto.TAG_PEERS & String.Join(";", names)
            ' fire-and-forget propre
            BroadcastFromHostAsync(Encoding.UTF8.GetBytes(msg))
        End Sub

    End Class
End Namespace
