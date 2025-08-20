' ChatP2P.Core/Paths.vb
Option Strict On
Imports System.Net
Imports System.Net.Sockets
Imports System.Threading
Imports System.Threading.Tasks

Namespace ChatP2P.Core

    Public NotInheritable Class PathProbeResult
        Public Property Type As PathType
        Public Property Success As Boolean
        Public Property Candidates As List(Of IPEndPoint)
        Public Property RttMs As Integer
        Public Property Notes As String

        Public Sub New()
            Candidates = New List(Of IPEndPoint)()
        End Sub
    End Class

    Public Interface INetworkPath
        ReadOnly Property Type As PathType
        ReadOnly Property Name As String
        ReadOnly Property SupportsMultiplexing As Boolean
        Function ProbeAsync(local As PeerDescriptor,
                            remote As PeerDescriptor,
                            ct As CancellationToken) As Task(Of PathProbeResult)
        Function ConnectAsync(remote As PeerDescriptor,
                              ct As CancellationToken) As Task(Of INetworkStream)
    End Interface

    Public Interface INetworkStream
        Inherits IAsyncDisposable
        ReadOnly Property IsDatagram As Boolean
        Function SendAsync(data As Byte(), ct As CancellationToken) As Task
        Function ReceiveAsync(ct As CancellationToken) As Task(Of Byte())
        Sub RequestAck(seq As UInteger)
    End Interface

    Public Interface IPathManager
        Event PathChanged(newType As PathType, details As String)
        Function NegotiateAsync(local As PeerDescriptor,
                                remote As PeerDescriptor,
                                candidates As IEnumerable(Of INetworkPath),
                                ct As CancellationToken) As Task(Of INetworkStream)
    End Interface

End Namespace
