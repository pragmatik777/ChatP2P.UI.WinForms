' ChatP2P.App/DirectPath.vb
Option Strict On
Imports System.Net
Imports System.Net.Sockets
Imports System.Threading
Imports System.Threading.Tasks
Imports ChatP2P.Core

Namespace ChatP2P.App

    Public NotInheritable Class DirectNetworkStream
        Implements INetworkStream

        Private ReadOnly _client As TcpClient
        Private ReadOnly _stream As NetworkStream

        Public Sub New(c As TcpClient)
            _client = c
            _stream = c.GetStream()
        End Sub

        Public ReadOnly Property IsDatagram As Boolean Implements INetworkStream.IsDatagram
            Get
                Return False
            End Get
        End Property

        Public Async Function SendAsync(data As Byte(), ct As CancellationToken) As Task Implements INetworkStream.SendAsync
            Dim len = BitConverter.GetBytes(CUInt(data.Length))
            Await _stream.WriteAsync(len, 0, len.Length, ct).ConfigureAwait(False)
            Await _stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(False)
        End Function

        Public Async Function ReceiveAsync(ct As CancellationToken) As Task(Of Byte()) Implements INetworkStream.ReceiveAsync
            Dim lenBuf(3) As Byte
            Dim r = Await _stream.ReadAsync(lenBuf, 0, 4, ct).ConfigureAwait(False)
            If r = 0 Then Throw New IO.EndOfStreamException()
            Dim L = BitConverter.ToUInt32(lenBuf, 0)
            Dim buf(CInt(L) - 1) As Byte
            Dim off = 0
            While off < buf.Length
                Dim n = Await _stream.ReadAsync(buf, off, buf.Length - off, ct).ConfigureAwait(False)
                If n = 0 Then Throw New IO.EndOfStreamException()
                off += n
            End While
            Return buf
        End Function

        Public Sub RequestAck(seq As UInteger) Implements INetworkStream.RequestAck
            ' TCP fiable : pas d’ACK applicatif (MVP)
        End Sub

        ' --- Correction: signature VB pour IAsyncDisposable
        Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
            Try : _stream.Dispose() : Catch : End Try
            Try : _client.Dispose() : Catch : End Try
            Return ValueTask.CompletedTask
        End Function
    End Class

    Public NotInheritable Class DirectPath
        Implements INetworkPath

        Private ReadOnly _listenPort As Integer
        Private _listener As TcpListener

        Public Sub New(listenPort As Integer)
            _listenPort = listenPort
        End Sub

        Public ReadOnly Property Type As PathType Implements INetworkPath.Type
            Get
                Return PathType.Direct
            End Get
        End Property

        Public ReadOnly Property Name As String Implements INetworkPath.Name
            Get
                Return $"Direct:{_listenPort}"
            End Get
        End Property

        Public ReadOnly Property SupportsMultiplexing As Boolean Implements INetworkPath.SupportsMultiplexing
            Get
                Return True
            End Get
        End Property

        Public Async Function ProbeAsync(local As PeerDescriptor, remote As PeerDescriptor, ct As CancellationToken) As Task(Of PathProbeResult) Implements INetworkPath.ProbeAsync
            ' MVP : on considère Direct possible si on peut écouter localement
            Dim res As New PathProbeResult With {.Type = PathType.Direct}
            Try
                _listener = New TcpListener(IPAddress.Any, _listenPort)
                _listener.Start()
                res.Success = True
                res.Notes = "Listener OK (en attente d’une connexion entrante)."
            Catch ex As Exception
                res.Success = False
                res.Notes = "Impossible d’ouvrir le port local (droits/pare-feu ?)."
            End Try
            Return res
        End Function

        Public Async Function ConnectAsync(remote As PeerDescriptor, ct As CancellationToken) As Task(Of INetworkStream) Implements INetworkPath.ConnectAsync
            ' Deux modes :
            ' - si remote.Endpoints contient une IP:port → connexion sortante
            ' - sinon on attend une connexion entrante
            If remote IsNot Nothing AndAlso remote.Endpoints IsNot Nothing AndAlso remote.Endpoints.Count > 0 Then
                Dim ep = remote.Endpoints(0)
                Dim cl As New TcpClient()
                Await cl.ConnectAsync(ep.Address, ep.Port).WaitAsync(ct).ConfigureAwait(False)
                Return New DirectNetworkStream(cl)
            Else
                If _listener Is Nothing Then Throw New InvalidOperationException("Listener non initialisé (appelle ProbeAsync d’abord).")
                Dim cl = Await _listener.AcceptTcpClientAsync().WaitAsync(ct).ConfigureAwait(False)
                Return New DirectNetworkStream(cl)
            End If
        End Function

    End Class

End Namespace
