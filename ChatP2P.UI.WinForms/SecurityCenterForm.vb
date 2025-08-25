' ChatP2P.UI.WinForms/SecurityCenterForm.vb
Option Strict On
Imports System
Imports System.Data
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms
Imports ChatP2P.Core

Public Class SecurityCenterForm
    Inherits Form

    Private ReadOnly _identityPath As String

    ' Fournies par Form1
    Public Property PeersProvider As Func(Of List(Of String))
    Public Property PeerStatusProvider As Func(Of String, (Verified As Boolean, Fp As String, P2PConnected As Boolean, CryptoActive As Boolean))

    Private tab As TabControl
    Private pagePeers As TabPage
    Private pageMe As TabPage
    Private pageSessions As TabPage
    Private pageRevocations As TabPage

    ' Peers tab controls
    Private lstPeers As ListBox
    Private lblTrusted As Label
    Private lblDtlsFp As TextBox
    Private btnCopyFp As Button
    Private btnTrust As Button
    Private btnUntrust As Button
    Private lblNote As TextBox
    Private btnSaveNote As Button
    Private lblInfo As Label

    ' Sessions tab
    Private btnResetSession As Button

    ' Revocations tab
    Private gridKeys As DataGridView
    Private btnRevoke As Button

    Public Sub New(identityPath As String)
        Me._identityPath = identityPath
        Me.Text = "Security Center"
        Me.StartPosition = FormStartPosition.CenterParent
        Me.Size = New Size(760, 520)
        BuildUi()
        LoadPeers()
    End Sub

    Private Sub BuildUi()
        tab = New TabControl() With {.Dock = DockStyle.Fill}
        pagePeers = New TabPage("Pairs")
        pageMe = New TabPage("Mon identité")
        pageSessions = New TabPage("Sessions")
        pageRevocations = New TabPage("Révocations")

        ' ---- Pairs page ----
        lstPeers = New ListBox() With {.Dock = DockStyle.Left, .Width = 220}
        AddHandler lstPeers.SelectedIndexChanged, AddressOf OnPeerSelected

        lblTrusted = New Label() With {.AutoSize = True, .Location = New Point(240, 20), .Text = "Trusted: ?"}
        lblDtlsFp = New TextBox() With {.Location = New Point(240, 50), .Width = 460, .ReadOnly = True}
        btnCopyFp = New Button() With {.Location = New Point(240, 80), .Width = 120, .Text = "Copier FP"}
        AddHandler btnCopyFp.Click, Sub()
                                        If lblDtlsFp.Text.Length > 0 Then
                                            Clipboard.SetText(lblDtlsFp.Text)
                                            MessageBox.Show("Empreinte copiée.", "Security", MessageBoxButtons.OK, MessageBoxIcon.Information)
                                        End If
                                    End Sub

        btnTrust = New Button() With {.Location = New Point(380, 80), .Width = 140, .Text = "Marquer de confiance"}
        AddHandler btnTrust.Click, AddressOf OnTrustClick

        btnUntrust = New Button() With {.Location = New Point(540, 80), .Width = 140, .Text = "Retirer confiance"}
        AddHandler btnUntrust.Click, AddressOf OnUntrustClick

        lblNote = New TextBox() With {.Location = New Point(240, 125), .Width = 460}
        btnSaveNote = New Button() With {.Location = New Point(710, 123), .Width = 60, .Text = "OK"}
        AddHandler btnSaveNote.Click, AddressOf OnSaveNoteClick

        lblInfo = New Label() With {.Location = New Point(240, 165), .AutoSize = True, .Text = "Sélectionne un pair pour voir/modifier sa confiance."}

        pagePeers.Controls.AddRange(New Control() {lstPeers, lblTrusted, lblDtlsFp, btnCopyFp, btnTrust, btnUntrust, lblNote, btnSaveNote, lblInfo})

        ' ---- My identity (placeholder) ----
        pageMe.Controls.Add(New Label() With {
            .Text = "Mon identité (Ed25519/X25519 OK). PQ (Dilithium/Kyber) viendra ensuite.",
            .AutoSize = True, .Location = New Point(16, 16)
        })

        ' ---- Sessions ----
        btnResetSession = New Button() With {.Text = "Réinitialiser la session sélectionnée", .Location = New Point(16, 16)}
        AddHandler btnResetSession.Click, AddressOf OnResetSessionClick
        pageSessions.Controls.Add(btnResetSession)

        ' ---- Revocations ----
        gridKeys = New DataGridView() With {
            .Location = New Point(16, 16), .Width = 700, .Height = 360,
            .ReadOnly = True, .AllowUserToAddRows = False, .AllowUserToDeleteRows = False,
            .SelectionMode = DataGridViewSelectionMode.FullRowSelect
        }
        btnRevoke = New Button() With {.Text = "Révoquer la clé sélectionnée", .Location = New Point(16, 390)}
        AddHandler btnRevoke.Click, AddressOf OnRevokeClick
        pageRevocations.Controls.AddRange(New Control() {gridKeys, btnRevoke})

        tab.TabPages.AddRange(New TabPage() {pagePeers, pageMe, pageSessions, pageRevocations})
        Me.Controls.Add(tab)
    End Sub

    ' ------------------ Data loading ------------------

    Private Sub LoadPeers()
        lstPeers.Items.Clear()
        Dim all As New List(Of String)
        Try
            ' Source 1: provider (si dispo)
            If PeersProvider IsNot Nothing Then
                For Each p As String In PeersProvider.Invoke()
                    If Not String.IsNullOrWhiteSpace(p) Then all.Add(p)
                Next
            End If
            ' Source 2: DB
            Dim dt = LocalDb.Query("SELECT Name FROM Peers ORDER BY Name;")
            For Each r As DataRow In dt.Rows
                Dim n = TryCast(r!Name, String)
                If Not String.IsNullOrWhiteSpace(n) Then all.Add(n)
            Next
        Catch
        End Try
        For Each n In all.Distinct(StringComparer.OrdinalIgnoreCase)
            lstPeers.Items.Add(n)
        Next
        If lstPeers.Items.Count > 0 Then lstPeers.SelectedIndex = 0
        LoadKeysGrid()
    End Sub

    Private Sub LoadKeysGrid()
        Try
            Dim dt = LocalDb.Query("SELECT Id, PeerName, Kind, CreatedUtc, Revoked, RevokedUtc, Note FROM PeerKeys ORDER BY CreatedUtc DESC;")
            gridKeys.DataSource = dt
        Catch
        End Try
    End Sub

    ' ------------------ Peers tab actions ------------------
    Private Sub OnPeerSelected(sender As Object, e As EventArgs)
        Dim peer = TryCast(lstPeers.SelectedItem, String)
        If String.IsNullOrWhiteSpace(peer) Then
            lblTrusted.Text = "Trusted: ?"
            lblDtlsFp.Text = ""
            lblNote.Text = ""
            Exit Sub
        End If

        ' Un seul paramètre @n réutilisé PARTOUT
        Dim pN As System.Data.SQLite.SQLiteParameter = LocalDb.P("@n", CType(peer, Object))

        ' Trusted ?
        Dim trusted As Boolean = False
        Try
            Dim vObj = LocalDb.ExecScalar(Of Object)(
                "SELECT Trusted FROM Peers WHERE Name=@n;",
                pN
            )
            If vObj IsNot Nothing AndAlso vObj IsNot DBNull.Value Then
                trusted = (Convert.ToInt32(vObj, Globalization.CultureInfo.InvariantCulture) <> 0)
            End If
        Catch
        End Try
        lblTrusted.Text = "Trusted: " & If(trusted, "✓", "×")

        ' Empreinte DTLS
        Dim fp As String = ""
        Try
            fp = LocalDb.ExecScalar(Of String)(
                "SELECT DtlsFingerprint FROM Peers WHERE Name=@n;",
                pN
            )
        Catch
        End Try
        lblDtlsFp.Text = If(fp, "")

        ' Note
        Dim note As String = ""
        Try
            note = LocalDb.ExecScalar(Of String)(
                "SELECT TrustNote FROM Peers WHERE Name=@n;",
                pN
            )
        Catch
        End Try
        lblNote.Text = If(note, "")
    End Sub

    Private Sub OnTrustClick(sender As Object, e As EventArgs)
        Dim peer = TryCast(lstPeers.SelectedItem, String)
        If String.IsNullOrWhiteSpace(peer) Then Return
        LocalDb.UpsertPeerTrust(peer, trusted:=True)
        OnPeerSelected(Nothing, EventArgs.Empty)
        MessageBox.Show("Le pair est maintenant marqué de confiance.", "Security", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub

    Private Sub OnUntrustClick(sender As Object, e As EventArgs)
        Dim peer = TryCast(lstPeers.SelectedItem, String)
        If String.IsNullOrWhiteSpace(peer) Then Return
        LocalDb.UpsertPeerTrust(peer, trusted:=False)
        OnPeerSelected(Nothing, EventArgs.Empty)
        MessageBox.Show("Le pair n'est plus marqué de confiance.", "Security", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub

    Private Sub OnSaveNoteClick(sender As Object, e As EventArgs)
        Dim peer = TryCast(lstPeers.SelectedItem, String)
        If String.IsNullOrWhiteSpace(peer) Then Return
        LocalDb.UpsertPeerTrust(peer, note:=lblNote.Text)
        MessageBox.Show("Note enregistrée.", "Security", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub

    ' ------------------ Sessions tab ------------------

    Private Sub OnResetSessionClick(sender As Object, e As EventArgs)
        Dim peer = TryCast(lstPeers.SelectedItem, String)
        If String.IsNullOrWhiteSpace(peer) Then
            MessageBox.Show("Sélectionne d'abord un pair.", "Sessions", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        LocalDb.DeleteSession(peer)
        MessageBox.Show("Session supprimée. Un nouveau handshake sera nécessaire.", "Sessions", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub

    ' ------------------ Revocations tab ------------------

    Private Sub OnRevokeClick(sender As Object, e As EventArgs)
        If gridKeys.SelectedRows Is Nothing OrElse gridKeys.SelectedRows.Count = 0 Then Return
        Dim idObj = gridKeys.SelectedRows(0).Cells("Id").Value
        If idObj Is Nothing Then Return
        Dim id As Long
        If Not Long.TryParse(idObj.ToString(), id) Then Return
        LocalDb.RevokePeerKey(id, "Révocation via Security Center")
        LoadKeysGrid()
        MessageBox.Show("Clé révoquée.", "Révocations", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub

End Class
