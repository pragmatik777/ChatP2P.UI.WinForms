' ChatP2P.Core/IceP2PSession.vb
Option Strict On
Imports System.Text
Imports SIPSorcery.Net

Namespace ChatP2P.Core

    ''' <summary>
    ''' Session ICE + DataChannel basée sur SIPSorcery.Net.
    ''' - setLocal/RemoteDescription renvoient SetDescriptionResultEnum (non-awaitable).
    ''' - createOffer/createAnswer renvoient un RTCSessionDescriptionInit (sync).
    ''' - DataChannel: event OnMessage(chan, proto, payload()).
    ''' - Logs détaillés via événements vers l’UI.
    ''' </summary>
    Public Class IceP2PSession
        Implements IAsyncDisposable

        ' ---- Events vers l’UI / orchestration ----
        Public Event OnLocalSdp(sdpType As String, sdp As String)
        Public Event OnLocalCandidate(candidate As String)
        Public Event OnConnected()
        Public Event OnClosed(reason As String)

        Public Event OnTextMessage(text As String)
        Public Event OnBinaryMessage(data As Byte())

        ' ---- Logs ICE détaillés ----
        Public Event OnIceStateChanged(state As RTCIceConnectionState)
        Public Event OnNegotiationLog(line As String)

        Private ReadOnly _pc As RTCPeerConnection
        Private _dc As RTCDataChannel
        Private ReadOnly _isCaller As Boolean
        Private ReadOnly _label As String

        Public Sub New(stunUrls As IEnumerable(Of String), label As String, isCaller As Boolean)
            _isCaller = isCaller
            _label = If(String.IsNullOrWhiteSpace(label), "data", label)

            ' Config ICE
            Dim cfg As New RTCConfiguration() With {
                .iceServers = New List(Of RTCIceServer)()
            }
            If stunUrls IsNot Nothing Then
                For Each u In stunUrls
                    If Not String.IsNullOrWhiteSpace(u) Then
                        cfg.iceServers.Add(New RTCIceServer With {.urls = u})
                    End If
                Next
            End If

            _pc = New RTCPeerConnection(cfg)
            RaiseEvent OnNegotiationLog($"ICE cfg: caller={_isCaller}, label='{_label}'")

            ' Candidats locaux
            AddHandler _pc.onicecandidate,
                Sub(ic)
                    If ic IsNot Nothing AndAlso Not String.IsNullOrEmpty(ic.candidate) Then
                        RaiseEvent OnLocalCandidate(ic.candidate)
                        RaiseEvent OnNegotiationLog($"Local candidate: {ic.candidate}")
                    End If
                End Sub

            ' États ICE
            AddHandler _pc.oniceconnectionstatechange,
                Sub(st)
                    RaiseEvent OnIceStateChanged(st)
                    RaiseEvent OnNegotiationLog($"ICE state → {st}")
                End Sub

            ' États connexion (global)
            AddHandler _pc.onconnectionstatechange,
                Sub(state)
                    Select Case state
                        Case RTCPeerConnectionState.connected
                            RaiseEvent OnConnected()
                        Case RTCPeerConnectionState.failed, RTCPeerConnectionState.disconnected, RTCPeerConnectionState.closed
                            RaiseEvent OnClosed(state.ToString())
                    End Select
                End Sub

            ' DataChannel entrant (callee)
            AddHandler _pc.ondatachannel,
                Sub(chan As RTCDataChannel)
                    _dc = chan
                    WireDataChannel(_dc)
                    RaiseEvent OnNegotiationLog("ondatachannel (callee) → wired")
                End Sub

            ' Caller → crée le datachannel (selon version SIPSorcery : sync ou Task)
            If _isCaller Then
                Dim created As Object = _pc.createDataChannel(_label)
                If TypeOf created Is Threading.Tasks.Task Then
                    Dim t = DirectCast(created, Threading.Tasks.Task)
                    t.Wait()
                    Dim resProp = created.GetType().GetProperty("Result")
                    _dc = DirectCast(resProp.GetValue(created), RTCDataChannel)
                Else
                    _dc = DirectCast(created, RTCDataChannel)
                End If
                WireDataChannel(_dc)
                RaiseEvent OnNegotiationLog("createDataChannel() (caller) → wired")
            End If
        End Sub

        Private Sub WireDataChannel(dc As RTCDataChannel)
            If dc Is Nothing Then Return

            ' Signature SIPSorcery: (RTCDataChannel, DataChannelPayloadProtocols, Byte())
            AddHandler dc.onmessage,
                Sub(channel As RTCDataChannel, proto As DataChannelPayloadProtocols, payload As Byte())
                    If payload Is Nothing Then Return

                    ' Binaire
                    RaiseEvent OnBinaryMessage(payload)

                    ' Essai texte UTF8
                    Try
                        Dim txt = Encoding.UTF8.GetString(payload)
                        If Not String.IsNullOrEmpty(txt) Then
                            RaiseEvent OnTextMessage(txt)
                        End If
                    Catch
                        ' ignore si non-text
                    End Try
                End Sub

            AddHandler dc.onopen, Sub() RaiseEvent OnNegotiationLog("DataChannel open")
            AddHandler dc.onclose, Sub() RaiseEvent OnNegotiationLog("DataChannel close")
        End Sub

        ''' <summary>Caller : crée l’offer et publie OnLocalSdp("offer", sdp).</summary>
        Public Sub Start()
            If _isCaller Then
                RaiseEvent OnNegotiationLog("createOffer()")
                Dim offer = _pc.createOffer() ' RTCSessionDescriptionInit
                Call _pc.setLocalDescription(offer) ' SetDescriptionResultEnum (non-awaitable)
                RaiseEvent OnNegotiationLog("localDescription set (offer)")
                RaiseEvent OnLocalSdp("offer", offer.sdp)
            End If
        End Sub

        ''' <summary>Applique la SDP distante. Si callee et type=offer, crée l’answer et publie OnLocalSdp("answer", sdp).</summary>
        Public Sub SetRemoteDescription(sdpType As String, sdp As String)
            If String.IsNullOrWhiteSpace(sdpType) OrElse String.IsNullOrWhiteSpace(sdp) Then Return

            RaiseEvent OnNegotiationLog($"setRemoteDescription({sdpType})")
            Dim t As RTCSdpType = If(
                String.Equals(sdpType, "offer", StringComparison.OrdinalIgnoreCase),
                RTCSdpType.offer,
                RTCSdpType.answer
            )

            Dim remote As New RTCSessionDescriptionInit With {.type = t, .sdp = sdp}
            Call _pc.setRemoteDescription(remote)

            If (Not _isCaller) AndAlso t = RTCSdpType.offer Then
                RaiseEvent OnNegotiationLog("createAnswer()")
                Dim answer = _pc.createAnswer()
                Call _pc.setLocalDescription(answer)
                RaiseEvent OnNegotiationLog("localDescription set (answer)")
                RaiseEvent OnLocalSdp("answer", answer.sdp)
            End If
        End Sub

        ''' <summary>Ajoute un ICE candidate distant.</summary>
        Public Sub AddRemoteCandidate(candidate As String)
            If String.IsNullOrWhiteSpace(candidate) Then Return
            RaiseEvent OnNegotiationLog($"addRemoteCandidate: {candidate}")
            _pc.addIceCandidate(New RTCIceCandidateInit With {.candidate = candidate})
        End Sub

        Public ReadOnly Property IsOpen As Boolean
            Get
                If _dc Is Nothing Then Return False
                Return _dc.readyState = RTCDataChannelState.open
            End Get
        End Property

        Public Sub SendText(text As String)
            If _dc Is Nothing OrElse _dc.readyState <> RTCDataChannelState.open Then
                Throw New InvalidOperationException("DataChannel non prêt.")
            End If
            Dim bytes = Encoding.UTF8.GetBytes(text)
            _dc.send(bytes)
        End Sub

        Public Sub SendBinary(data As Byte())
            If _dc Is Nothing OrElse _dc.readyState <> RTCDataChannelState.open Then
                Throw New InvalidOperationException("DataChannel non prêt.")
            End If
            If data Is Nothing Then Return
            _dc.send(data)
        End Sub

        ' IAsyncDisposable: ValueTask, pas Async/Await
        Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
            Try
                If _dc IsNot Nothing Then _dc.close()
            Catch
            End Try
            Try
                _pc.Close("dispose")
            Catch
            End Try
            Return ValueTask.CompletedTask
        End Function

    End Class

End Namespace
