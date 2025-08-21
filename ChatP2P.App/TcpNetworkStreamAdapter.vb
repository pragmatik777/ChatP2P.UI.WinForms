' ChatP2P.App/TcpNetworkStreamAdapter.vb
Option Strict On
Imports System.Net.Sockets
Imports System.Threading
Imports ChatP2P.Core

Namespace ChatP2P.App

    ''' <summary>
    ''' Adaptateur TcpClient → INetworkStream.
    ''' - IsDatagram = False (TCP est stream)
    ''' - RequestAck(seq) : no-op (pas d’ACK app côté TCP)
    ''' - DisposeAsync implémenté (IAsyncDisposable requis par INetworkStream)
    ''' </summary>
    Public Class TcpNetworkStreamAdapter
        Implements INetworkStream, IDisposable, IAsyncDisposable

        Private ReadOnly _client As TcpClient
        Private ReadOnly _ns As NetworkStream
        Private _disposed As Boolean = False

        Public Sub New(client As TcpClient)
            _client = client
            _ns = client.GetStream()
        End Sub

        ' --- INetworkStream ---

        Public Function SendAsync(data As Byte(), ct As CancellationToken) As Threading.Tasks.Task _
            Implements INetworkStream.SendAsync
            If data Is Nothing OrElse data.Length = 0 Then
                Return Threading.Tasks.Task.CompletedTask
            End If
            Return _ns.WriteAsync(data, 0, data.Length, ct)
        End Function

        Public Async Function ReceiveAsync(ct As CancellationToken) As Threading.Tasks.Task(Of Byte()) _
            Implements INetworkStream.ReceiveAsync
            Dim buf(4096 - 1) As Byte
            Dim read = Await _ns.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(False)
            If read <= 0 Then Return Array.Empty(Of Byte)()
            Dim res(read - 1) As Byte
            Buffer.BlockCopy(buf, 0, res, 0, read)
            Return res
        End Function

        Public ReadOnly Property IsDatagram As Boolean Implements INetworkStream.IsDatagram
            Get
                Return False ' TCP = flux, pas datagramme
            End Get
        End Property

        Public Sub RequestAck(seq As UInteger) Implements INetworkStream.RequestAck
            ' TCP garantit l’ordre & la fiabilité → pas d’ACK applicatif nécessaire.
            ' No-op.
        End Sub

        ' --- IDisposable / IAsyncDisposable ---

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            Try : _ns.Close() : Catch : End Try
            Try : _client.Close() : Catch : End Try
        End Sub

        Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
            Dispose()
            Return ValueTask.CompletedTask
        End Function

    End Class

End Namespace
