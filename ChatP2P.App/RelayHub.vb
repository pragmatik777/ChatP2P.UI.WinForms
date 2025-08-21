' ChatP2P.App/RelayHub.vb
Option Strict On
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports ChatP2P.App.Protocol
Imports Proto = ChatP2P.App.Protocol.Tags

Namespace ChatP2P.App

    ''' <summary>
    ''' Hub TCP (relay + peers + routage ICE/Fichier).
    ''' - Gère la liste des pairs.
    ''' - Relaye MSG/PRIV/FILE*.
    ''' - Relaye ICE_* au destinataire indiqué (format: ICE_{X}:from:to:base64payload).
    ''' </summary>
    Public Class RelayHub

        ' --- Events UI ---
        Public Event PeerListUpdated(names As List(Of String))
        Public Event LogLine(line As String)
        Public Event MessageArrived(sender As String, text As String)
        Public Event PrivateArrived(sender As String, dest As String, text As String)
        Public Delegate Sub FileSignalEventHandler(raw As String)
        Public Event FileSignal As FileSignalEventHandler

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

            ' Récupérer le port effectivement écouté
            Dim ep = TryCast(_listener.LocalEndpoint, IPEndPoint)
            Dim portInfo As String = If(ep IsNot Nothing, ep.Port.ToString(), "?")
            RaiseEvent LogLine($"[Hub] Listening on port {portInfo}")

            ' Lancer la boucle d'acceptation en fire-and-forget
            Dim _ignore As Task = AcceptLoop(_cts.Token)
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
                Return DirectCast(_listener.LocalEndpoint, IPEndPoint).Port
            Catch
                Return 0
            End Try
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

        ' === read per client ===
        Private Async Function ListenClientLoopAsync(cli As TcpClient, ct As CancellationToken) As Task
            Dim ns = cli.GetStream()
            Dim buffer(8192 - 1) As Byte

            Dim myName As String
            SyncLock _gate
                myName = If(_rev.ContainsKey(cli), _rev(cli), "?")
            End SyncLock

            Try
                While Not ct.IsCancellationRequested
                    Dim read = Await ns.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(False)
                    If read <= 0 Then Exit While

                    Dim msg = Encoding.UTF8.GetString(buffer, 0, read)
                    RaiseEvent LogLine($"[Hub] {myName} → {Left(msg, Math.Min(100, msg.Length))}")

                    ' ====== ROUTAGE ======
                    If msg.StartsWith(Proto.TAG_NAME) Then
                        Dim newName = msg.Substring(Proto.TAG_NAME.Length).Trim()
                        If Not String.IsNullOrWhiteSpace(newName) Then
                            SyncLock _gate
                                If _clients.ContainsKey(myName) Then _clients.Remove(myName)
                                _clients(newName) = cli
                                _rev(cli) = newName
                                myName = newName
                            End SyncLock
                            RaiseEvent LogLine($"[Hub] Client renommé en {myName}")
                            BroadcastPeers()
                        End If

                    ElseIf msg.StartsWith(Proto.TAG_MSG) Then
                        ' broadcast texte simple
                        RaiseEvent MessageArrived(myName, msg.Substring(Proto.TAG_MSG.Length))
                        Await BroadcastRawAsync(msg, exceptName:=myName).ConfigureAwait(False)

                    ElseIf msg.StartsWith(Proto.TAG_PRIV) Then
                        ' PRIV:sender:dest:message
                        Dim rest = msg.Substring(Proto.TAG_PRIV.Length)
                        Dim parts = rest.Split(":"c, 3)
                        If parts.Length = 3 Then
                            Dim dest = parts(1)
                            RaiseEvent PrivateArrived(parts(0), parts(1), parts(2))
                            Await SendRawToAsync(dest, msg).ConfigureAwait(False)
                        End If

                    ElseIf msg.StartsWith(Proto.TAG_FILEMETA) _
                        OrElse msg.StartsWith(Proto.TAG_FILECHUNK) _
                        OrElse msg.StartsWith(Proto.TAG_FILEEND) Then

                        ' Laisse l’UI voir le signal si besoin
                        RaiseEvent FileSignal(msg)

                        ' filemeta: ...:dest:...
                        Dim dest As String = Nothing
                        If msg.StartsWith(Proto.TAG_FILEMETA) Then
                            Dim p = msg.Split(":"c, 6)
                            If p.Length >= 6 Then dest = p(3)
                        End If
                        If String.IsNullOrEmpty(dest) Then
                            ' chunks / end routés à partir de l’émetteur précédent si tu veux,
                            ' ou laisse le client qui a reçu le meta rediriger source→dest.
                            ' Ici on broadcast par simplicité:
                            Await BroadcastRawAsync(msg, exceptName:=myName).ConfigureAwait(False)
                        Else
                            Await SendRawToAsync(dest, msg).ConfigureAwait(False)
                        End If

                    ElseIf msg.StartsWith(Proto.TAG_ICE_OFFER) _
                        OrElse msg.StartsWith(Proto.TAG_ICE_ANSWER) _
                        OrElse msg.StartsWith(Proto.TAG_ICE_CAND) Then

                        ' Format attendu: TAG:from:to:BASE64
                        Dim rest = msg.Substring(msg.IndexOf(":"c) + 1)
                        Dim parts = rest.Split(":"c, 3)
                        If parts.Length = 3 Then
                            Dim toPeer = parts(1)
                            ' Relais direct vers le destinataire (pas d’event UI requis)
                            Await SendRawToAsync(toPeer, msg).ConfigureAwait(False)
                        End If
                    End If
                End While

            Catch ex As Exception
                RaiseEvent LogLine($"[Hub] {myName} déconnecté: {ex.Message}")

            Finally
                SyncLock _gate
                    If _clients.ContainsKey(myName) Then _clients.Remove(myName)
                    If _rev.ContainsKey(cli) Then _rev.Remove(cli)
                End SyncLock
                Try : cli.Close() : Catch : End Try
                BroadcastPeers()
            End Try
        End Function

        ' === helpers d’envoi ===
        Private Async Function SendRawToAsync(targetName As String, payload As String) As Task
            Dim cli As TcpClient = Nothing
            SyncLock _gate
                If _clients.ContainsKey(targetName) Then cli = _clients(targetName)
            End SyncLock
            If cli Is Nothing Then
                RaiseEvent LogLine($"[Hub] Destinataire introuvable: {targetName}")
                Exit Function
            End If

            Dim data = Encoding.UTF8.GetBytes(payload)
            Try
                Await cli.GetStream().WriteAsync(data, 0, data.Length).ConfigureAwait(False)
            Catch ex As Exception
                RaiseEvent LogLine($"[Hub] SendTo {targetName} failed: {ex.Message}")
            End Try
        End Function

        Private Async Function BroadcastRawAsync(payload As String, Optional exceptName As String = Nothing) As Task
            Dim data = Encoding.UTF8.GetBytes(payload)
            Dim targets As List(Of TcpClient)
            SyncLock _gate
                targets = _clients.
                    Where(Function(kv) String.IsNullOrEmpty(exceptName) OrElse Not kv.Key.Equals(exceptName, StringComparison.OrdinalIgnoreCase)).
                    Select(Function(kv) kv.Value).
                    ToList()
            End SyncLock

            For Each cli In targets
                Try
                    Await cli.GetStream().WriteAsync(data, 0, data.Length).ConfigureAwait(False)
                Catch
                End Try
            Next
        End Function

        ' === peers ===
        Private Sub BroadcastPeers()
            Dim names As New List(Of String) From {HostDisplayName}
            SyncLock _gate
                names.AddRange(_clients.Keys)
            End SyncLock

            RaiseEvent PeerListUpdated(names)

            Dim line = Proto.TAG_PEERS & String.Join(";", names)
            Dim _ignore As Task = BroadcastRawAsync(line)
        End Sub

        ' === API utilisé par le host si besoin (non requis pour le routage ICE désormais) ===
        Public Async Function BroadcastFromHostAsync(data As Byte()) As Task
            Dim s = Encoding.UTF8.GetString(data)
            Await BroadcastRawAsync(s).ConfigureAwait(False)
        End Function

        Public Async Function SendToAsync(dest As String, data As Byte()) As Task
            Dim s = Encoding.UTF8.GetString(data)
            Await SendRawToAsync(dest, s).ConfigureAwait(False)
        End Function

    End Class
End Namespace
