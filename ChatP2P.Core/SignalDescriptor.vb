Option Strict On
Imports System.Collections.Generic

Namespace ChatP2P.Core
    ' Conteneur minimal pour transporter PeerId + Tags de signaling.
    Public Class SignalDescriptor
        Public Property PeerId As String
        Public Property Tags As Dictionary(Of String, String)
        Public Sub New(peerId As String)
            Me.PeerId = peerId
            Me.Tags = New Dictionary(Of String, String)(StringComparer.Ordinal)
        End Sub
    End Class
End Namespace
