Option Strict On
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports ChatP2P.Core
Imports ChatP2P.App.Protocol
Imports Proto = ChatP2P.App.Protocol.Tags

Namespace ChatP2P.App

    ''' <summary>
    ''' Hub "manager" SANS socket : il gère des INetworkStream que l'hôte lui fournit
    ''' (par ex. acceptés via DirectPath). Il route MSG/PRIV/FILExxx/ICExxx, diffuse la
    ''' liste des pairs, et expose des événements de log.
    ''' </summary>
    Public Class RelayHub

        ' ---- Evénements vers l’UI ----
        Public Event PeerListUpdated(names As List(Of String))
        Public Event LogLine(line As String)
        Public Event MessageArrived(sender As String, text As String)
        Public Event PrivateArrived(sender As String, dest As String, text As String)

        ' Signaux bruts (texte) que Form1 traite
        Public Delegate Sub FileSignalEventHandler(raw As String)
        Public Event FileSignal As FileSignalEventHandler

        Public Delegate Sub IceSignalEventHandler(raw As String)
        Public Event IceSignal As IceSignalEventHandler

        Public Property HostDisplayName As String = "Host"

        ' ---- Etat ----
        Private ReadOnly _clients As New Dictionary(Of String, INetworkStream)()  ' nom -> stream
        Private ReadOnly _rev As New Dictionary(Of INetworkStream, String)()      ' stream -> nom
        Private ReadOnly _gate As New Object()

        ' === API : l’hôte ajoute un client déjà accepté (DirectPath) ===
        Public Sub AddClient(initialName As String, s As INetworkStream)
            If s Is Nothing Then
                RaiseEvent LogLine("[Hub] Stream NULL ignoré.")
                Exit Sub
            End If

            Dim name = If(String.IsNullOrWhiteSpace(initialName), $"Client{DateTime.UtcNow.Ticks}", initialName)

            SyncLock _gate
                If _clients.ContainsKey(name) Then
                    name &= "_" & DateTime.UtcNow.Ticks.ToString()
                End If
                _clients(name) = s
                _rev(s) = name
            End SyncLock

            RaiseEvent LogLine($"[Hub] {name} connecté (INetworkStream).")
            BroadcastPeers()

            ' Boucle de réception (fire-and-forget)
            Dim _ignoreListen As Task = ListenLoopAsync(s, name)
        End Sub

        ' === Envoi depuis le Host à tous ===
        Public Async Function BroadcastFromHostAsync(payload As String) As Task
            Dim data = Encoding.UTF8.GetBytes(payload)
            Await BroadcastFromHostAsync(data)
        End Function

        Public Async Function BroadcastFromHostAsync(data As Byte()) As Task
            Dim targets As List(Of INetworkStream)
            SyncLock _gate
                targets = New List(Of INetworkStream)(_clients.Values)
            End SyncLock

            For Each t In targets
                Try
                    Await t.SendAsync(data, CancellationToken.None)
                Catch ex As Exception
                    RaiseEvent LogLine($"[Hub] Erreur broadcast: {ex.Message}")
                End Try
            Next
        End Function

        ' === Envoi à un destinataire précis ===
        Public Async Function SendToAsync(targetName As String, payload As String) As Task
            Dim data = Encoding.UTF8.GetBytes(payload)
            Await SendToAsync(targetName, data)
        End Function

        Public Async Function SendToAsync(targetName As String, data As Byte()) As Task
            Dim s As INetworkStream = Nothing
            SyncLock _gate
                If _clients.ContainsKey(targetName) Then s = _clients(targetName)
            End SyncLock
            If s Is Nothing Then
                RaiseEvent LogLine($"[Hub] Destinataire introuvable: {targetName}")
                Exit Function
            End If
            Try
                Await s.SendAsync(data, CancellationToken.None)
            Catch ex As Exception
                RaiseEvent LogLine($"[Hub] Erreur SendTo {targetName}: {ex.Message}")
            End Try
        End Function

        ' === Diffuser la liste des pairs à tous ===
        Private Sub BroadcastPeers()
            Dim names As List(Of String)
            SyncLock _gate
                names = New List(Of String) From {HostDisplayName}
                names.AddRange(_clients.Keys)
            End SyncLock

            ' Notifie l’UI
            RaiseEvent PeerListUpdated(names)

            ' Notifie les pairs
            Dim msg = Proto.TAG_PEERS & String.Join(";", names)
            Dim _ignoreBroadcast As Task = BroadcastFromHostAsync(msg)
        End Sub

        ' === Boucle de réception par client ===
        Private Async Function ListenLoopAsync(s As INetworkStream, currentName As String) As Task
            Try
                While True
                    Dim data = Await s.ReceiveAsync(CancellationToken.None)
                    If data Is Nothing OrElse data.Length = 0 Then
                        Throw New IO.EndOfStreamException()
                    End If

                    Dim msg = Encoding.UTF8.GetString(data)
                    RaiseEvent LogLine($"[Hub] {currentName} → {Left(msg, Math.Min(80, msg.Length))}")

                    If msg.StartsWith(Proto.TAG_NAME) Then
                        Dim newName = msg.Substring(Proto.TAG_NAME.Length).Trim()
                        If newName <> "" Then
                            SyncLock _gate
                                If _rev.ContainsKey(s) Then
                                    Dim old = _rev(s)
                                    If _clients.ContainsKey(old) Then _clients.Remove(old)
                                    Dim finalName = newName
                                    If _clients.ContainsKey(finalName) Then finalName &= "_" & DateTime.UtcNow.Ticks.ToString()
                                    _clients(finalName) = s
                                    _rev(s) = finalName
                                    currentName = finalName
                                End If
                            End SyncLock
                            RaiseEvent LogLine($"[Hub] Client renommé → {currentName}")
                            BroadcastPeers()
                        End If

                    ElseIf msg.StartsWith(Proto.TAG_MSG) Then
                        RaiseEvent MessageArrived(currentName, msg.Substring(Proto.TAG_MSG.Length))
                        ' Re-diffuse aux autres
                        Await BroadcastOthersAsync(s, data)

                    ElseIf msg.StartsWith(Proto.TAG_PRIV) Then
                        ' PRIV:sender:dest:message
                        Dim rest = msg.Substring(Proto.TAG_PRIV.Length)
                        Dim parts = rest.Split(":"c, 3)
                        If parts.Length = 3 Then
                            Dim sender = parts(0)
                            Dim dest = parts(1)
                            Dim body = parts(2)
                            RaiseEvent PrivateArrived(sender, dest, body)
                            ' Forward au destinataire
                            Await SendToAsync(dest, data)
                        End If

                    ElseIf msg.StartsWith(Proto.TAG_FILEMETA) _
                        OrElse msg.StartsWith(Proto.TAG_FILECHUNK) _
                        OrElse msg.StartsWith(Proto.TAG_FILEEND) Then

                        RaiseEvent FileSignal(msg)
                        ' MVP: relay simple aux autres
                        Await BroadcastOthersAsync(s, data)

                    ElseIf msg.StartsWith(Proto.TAG_ICE_OFFER) _
                        OrElse msg.StartsWith(Proto.TAG_ICE_ANSWER) _
                        OrElse msg.StartsWith(Proto.TAG_ICE_CAND) Then

                        RaiseEvent IceSignal(msg)
                        ' Selon choix, router vers cible; MVP: broadcast autres
                        Await BroadcastOthersAsync(s, data)
                    End If
                End While

            Catch ex As Exception
                RaiseEvent LogLine($"[Hub] {currentName} déconnecté: {ex.Message}")
            Finally
                SyncLock _gate
                    If _rev.ContainsKey(s) Then
                        Dim old = _rev(s)
                        _rev.Remove(s)
                        If _clients.ContainsKey(old) Then _clients.Remove(old)
                    End If
                End SyncLock
                BroadcastPeers()
            End Try
        End Function

        Private Async Function BroadcastOthersAsync(senderStream As INetworkStream, data As Byte()) As Task
            Dim targets As List(Of INetworkStream)
            SyncLock _gate
                targets = _clients.Values.Where(Function(v) Not Object.ReferenceEquals(v, senderStream)).ToList()
            End SyncLock
            For Each t In targets
                Try
                    Await t.SendAsync(data, CancellationToken.None)
                Catch
                End Try
            Next
        End Function

    End Class
End Namespace
