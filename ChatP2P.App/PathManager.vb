' ChatP2P.App/PathManager.vb
Option Strict On
Imports ChatP2P.Core

Namespace ChatP2P.App

    Public NotInheritable Class PathManager
        Implements IPathManager

        Public Event PathChanged(newType As PathType, details As String) Implements IPathManager.PathChanged

        Public Async Function NegotiateAsync(local As PeerDescriptor,
                                             remote As PeerDescriptor,
                                             candidates As IEnumerable(Of INetworkPath),
                                             ct As Threading.CancellationToken) As Task(Of INetworkStream) Implements IPathManager.NegotiateAsync
            For Each p In candidates
                Dim pr = Await p.ProbeAsync(local, remote, ct).ConfigureAwait(False)
                If pr.Success Then
                    RaiseEvent PathChanged(p.Type, $"Using {p.Name} : {pr.Notes}")
                    Dim s = Await p.ConnectAsync(remote, ct).ConfigureAwait(False)
                    Return s
                End If
            Next
            Throw New InvalidOperationException("Aucun chemin disponible")
        End Function

    End Class

End Namespace
