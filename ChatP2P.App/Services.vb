' ChatP2P.App/Services.vb
Option Strict On
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports ChatP2P.Core

Namespace ChatP2P.App

    Public Interface IMessageBus
        Event MessageReceived(peer As PeerDescriptor, plaintext As String)
        Function SendAsync(peer As PeerDescriptor, plaintext As String, ct As CancellationToken) As Task
    End Interface

    Public Interface IFileTransfer
        Event Progress(peer As PeerDescriptor, fileId As String, sentBytes As Long, totalBytes As Long)
        Function SendFileAsync(peer As PeerDescriptor, filePath As String, ct As CancellationToken) As Task
        Function ReceiveToAsync(peer As PeerDescriptor, fileId As String, destFolder As String, ct As CancellationToken) As Task
    End Interface

    Public NotInheritable Class ChatService
        Private ReadOnly _pm As IPathManager
        Private ReadOnly _hb As IHandshake
        Private _stream As INetworkStream

        Public Sub New(pm As IPathManager, hb As IHandshake)
            _pm = pm : _hb = hb
        End Sub

        Public Async Function ConnectAsync(local As PeerDescriptor,
                                           remote As PeerDescriptor,
                                           paths As IEnumerable(Of INetworkPath),
                                           ct As CancellationToken) As Task
            _stream = Await _pm.NegotiateAsync(local, remote, paths, ct).ConfigureAwait(False)
            ' … envoyer ClientHello, recevoir PeerHello, lancer KeyAgreement, etc.
        End Function

        Public Async Function SendMessageAsync(text As String, ct As CancellationToken) As Task
            If _stream Is Nothing Then Throw New InvalidOperationException("Not connected")
            Dim payload = Encoding.UTF8.GetBytes(text)
            ' … packer en frame Data + AEAD …
            Await _stream.SendAsync(payload, ct).ConfigureAwait(False)
        End Function

    End Class

End Namespace
