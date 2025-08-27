Option Strict On

Namespace ChatP2P.Core

    ' Fichier complet à ajouter.
    ' Ajoute l’enregistrement du fingerprint DTLS extrait du SDP.
    ' À appeler juste APRÈS avoir appliqué le SDP distant (offer/answer).
    ' Nécessite LocalDb.SetPeerDtlsFp(peer, fp) qui existe déjà dans ton LocalDb.

    Public Class P2PManagerExtensions

        ' Appelle cette méthode après SetRemoteDescription("offer"/"answer", sdp)
        Public Shared Sub RecordDtlsFingerprint(peer As String, sdp As String)
            If String.IsNullOrWhiteSpace(peer) OrElse String.IsNullOrWhiteSpace(sdp) Then Exit Sub
            Dim fp = ExtractDtlsFingerprintFromSdp(sdp)
            If Not String.IsNullOrWhiteSpace(fp) Then
                Try
                    LocalDb.SetPeerDtlsFp(peer, fp)
                    SafeLog(peer, "DTLS fingerprint enregistré.")
                Catch ex As Exception
                    SafeLog(peer, "Erreur enregistrement fingerprint: " & ex.Message)
                End Try
            End If
        End Sub

        Private Shared Function ExtractDtlsFingerprintFromSdp(sdp As String) As String
            Dim text = sdp
            If String.IsNullOrEmpty(text) Then Return Nothing
            text = text.Replace(vbCr, "")
            For Each raw In text.Split(vbLf)
                Dim line = raw.Trim()
                If line.StartsWith("a=fingerprint:", StringComparison.OrdinalIgnoreCase) Then
                    Return line.Substring("a=fingerprint:".Length).Trim()
                End If
            Next
            Return Nothing
        End Function

        Private Shared Sub SafeLog(peer As String, msg As String)
            Try
                ' Si tu as un event global de log P2P, appelle-le ici.
                ' Sinon, no-op.
                ' Exemple si tu as P2PManager.OnLog :
                ' P2PManager.RaiseLog(peer, msg)
            Catch
            End Try
        End Sub

    End Class

End Namespace
