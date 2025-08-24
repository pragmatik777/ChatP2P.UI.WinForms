' ChatP2P.UI.WinForms/SecurityCenterForm.vb
Option Strict On
Imports System
Imports System.IO
Imports System.Text
Imports System.Security.Cryptography
Imports System.Windows.Forms
Imports System.ComponentModel

Public Class SecurityCenterForm
    Inherits Form

    ' ------------------------ Types & API “extensible” ------------------------

    ' Stats étendues optionnelles par peer (si non fournies, la grille s’adapte)
    Public Class ExtendedPeerInfo
        Public Property LastContactUtc As DateTime?       ' Dernier contact (UTC)
        Public Property MsgsSent As Integer               ' Messages P2P envoyés
        Public Property MsgsRecv As Integer               ' Messages P2P reçus
        Public Property FilesSent As Integer              ' Fichiers envoyés
        Public Property FilesRecv As Integer              ' Fichiers reçus
        Public Property TrustLevel As String              ' ex: "pinned", "known", "unverified"
        Public Property Note As String                    ' Note libre
    End Class

    ' --- Providers & Actions branchés par Form1 (tous optionnels sauf Peers/Status) ---
    Public Property PeersProvider As Func(Of List(Of String))                       ' requis pour la liste
    Public Property PeerStatusProvider As Func(Of String, (Verified As Boolean, Fp As String, P2PConnected As Boolean, CryptoActive As Boolean)) ' requis état de base
    Public Property ExtendedPeerStatusProvider As Func(Of String, ExtendedPeerInfo) ' optionnel
    Public Property ForgetPeerAction As Action(Of String)                           ' optionnel (oublier confiance)
    Public Property PurgeEphemeralAction As Action                                  ' optionnel (purge PFS)
    Public Property ViewPeerDetailsAction As Action(Of String)                      ' optionnel (ouvrir détails côté UI)
    Public Property SetPeerNoteAction As Action(Of String, String)                  ' optionnel (persister une note)

    ' ------------------------ UI ------------------------
    Private lblLocalTitle As Label
    Private lblLocalFp As Label
    Private txtIdPath As TextBox
    Private btnOpenDir As Button
    Private btnBackup As Button
    Private btnImport As Button
    Private btnRegenerate As Button

    Private toolPanel As Panel
    Private txtSearch As TextBox
    Private chkAutoRefresh As CheckBox
    Private btnRefresh As Button
    Private btnCopyFp As Button
    Private btnExport As Button
    Private btnImportCsv As Button
    Private btnForgetTrust As Button
    Private btnPurgeEph As Button

    Private dgvPeers As DataGridView
    Private ctxMenu As ContextMenuStrip
    Private WithEvents tmrAuto As Timer

    ' Chemin identité
    Private ReadOnly _idFilePath As String

    ' colonnes (index)
    Private Const COL_PEER As Integer = 0
    Private Const COL_VERIF As Integer = 1
    Private Const COL_FP As Integer = 2
    Private Const COL_P2P As Integer = 3
    Private Const COL_CRYPTO As Integer = 4
    Private Const COL_LAST As Integer = 5
    Private Const COL_MSGS As Integer = 6
    Private Const COL_FILES As Integer = 7
    Private Const COL_TRUST As Integer = 8
    Private Const COL_NOTE As Integer = 9

    Public Sub New(idFilePath As String)
        _idFilePath = idFilePath

        Me.Text = "Security Center — Auth / Crypto / Identité"
        Me.StartPosition = FormStartPosition.CenterParent
        Me.Width = 1050
        Me.Height = 640
        Me.MinimizeBox = False
        Me.MaximizeBox = False
        Me.FormBorderStyle = FormBorderStyle.Sizable
        Me.KeyPreview = True

        BuildUI()
        LoadLocalIdentityInfo()
        LoadPeers() ' premier affichage

        tmrAuto = New Timer() With {.Interval = 1500} ' 1.5s par défaut
        AddHandler tmrAuto.Tick, AddressOf OnAutoTick
    End Sub

    ' ------------------------ Build UI ------------------------
    Private Sub BuildUI()
        ' Haut : bloc identité locale
        Dim topPanel As New Panel With {.Dock = DockStyle.Top, .Height = 160}
        lblLocalTitle = New Label With {.Text = "Identité locale (Ed25519)", .AutoSize = True, .Font = New Font(Font, FontStyle.Bold), .Left = 12, .Top = 12}
        lblLocalFp = New Label With {.Text = "Empreinte: (n/a)", .AutoSize = True, .Left = 12, .Top = 42}

        Dim lblPath As New Label With {.Text = "Fichier:", .AutoSize = True, .Left = 12, .Top = 72}
        txtIdPath = New TextBox With {.Left = 70, .Top = 68, .Width = 720, .ReadOnly = True}
        btnOpenDir = New Button With {.Left = 800, .Top = 66, .Width = 200, .Text = "Ouvrir le dossier"}
        AddHandler btnOpenDir.Click, AddressOf OnOpenDir

        btnBackup = New Button With {.Left = 12, .Top = 100, .Width = 120, .Text = "Backup…"}
        btnImport = New Button With {.Left = 140, .Top = 100, .Width = 120, .Text = "Importer…"}
        btnRegenerate = New Button With {.Left = 268, .Top = 100, .Width = 220, .Text = "Régénérer (nouvelle paire)"}
        AddHandler btnBackup.Click, AddressOf OnBackup
        AddHandler btnImport.Click, AddressOf OnImport
        AddHandler btnRegenerate.Click, AddressOf OnRegenerate

        topPanel.Controls.AddRange({lblLocalTitle, lblLocalFp, lblPath, txtIdPath, btnOpenDir, btnBackup, btnImport, btnRegenerate})
        Me.Controls.Add(topPanel)

        ' Barre d’outils
        toolPanel = New Panel With {.Dock = DockStyle.Top, .Height = 44}
        Dim lblSearch As New Label With {.Text = "Filtrer:", .AutoSize = True, .Left = 12, .Top = 13}
        txtSearch = New TextBox With {.Left = 60, .Top = 10, .Width = 220}
        AddHandler txtSearch.TextChanged, AddressOf OnSearchChanged

        chkAutoRefresh = New CheckBox With {.Left = 290, .Top = 12, .AutoSize = True, .Text = "Auto"}
        AddHandler chkAutoRefresh.CheckedChanged, Sub() tmrAuto.Enabled = chkAutoRefresh.Checked

        btnRefresh = New Button With {.Left = 350, .Top = 8, .Width = 90, .Text = "Rafraîchir"}
        AddHandler btnRefresh.Click, AddressOf OnRefresh

        btnCopyFp = New Button With {.Left = 446, .Top = 8, .Width = 110, .Text = "Copier FP"}
        AddHandler btnCopyFp.Click, AddressOf OnCopyFp

        btnExport = New Button With {.Left = 562, .Top = 8, .Width = 110, .Text = "Export CSV"}
        AddHandler btnExport.Click, AddressOf OnExportCsv

        btnImportCsv = New Button With {.Left = 678, .Top = 8, .Width = 110, .Text = "Import CSV"}
        AddHandler btnImportCsv.Click, AddressOf OnImportCsv

        btnForgetTrust = New Button With {.Left = 794, .Top = 8, .Width = 130, .Text = "Oublier confiance"}
        AddHandler btnForgetTrust.Click, AddressOf OnForgetTrust

        btnPurgeEph = New Button With {.Left = 928, .Top = 8, .Width = 110, .Text = "Purger éphémères"}
        AddHandler btnPurgeEph.Click, AddressOf OnPurgeEph

        toolPanel.Controls.AddRange({lblSearch, txtSearch, chkAutoRefresh, btnRefresh, btnCopyFp, btnExport, btnImportCsv, btnForgetTrust, btnPurgeEph})
        Me.Controls.Add(toolPanel)

        ' Grid
        dgvPeers = New DataGridView With {
            .Dock = DockStyle.Fill,
            .ReadOnly = False, ' notes éditables
            .SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            .AllowUserToAddRows = False,
            .AllowUserToDeleteRows = False,
            .AllowUserToResizeRows = False,
            .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            .RowHeadersVisible = False
        }
        dgvPeers.Columns.Add("Peer", "Peer")
        dgvPeers.Columns.Add("Verified", "Identité OK")
        dgvPeers.Columns.Add("Fp", "Empreinte ID (SHA‑256)")
        dgvPeers.Columns.Add("P2P", "P2P")
        dgvPeers.Columns.Add("Crypto", "Crypto")
        dgvPeers.Columns.Add("Last", "Dernier contact")
        dgvPeers.Columns.Add("Msgs", "Msgs P2P (→ / ←)")
        dgvPeers.Columns.Add("Files", "Fichiers (→ / ←)")
        dgvPeers.Columns.Add("Trust", "Trust")
        dgvPeers.Columns.Add("Note", "Note")
        dgvPeers.Columns(COL_VERIF).ReadOnly = True
        dgvPeers.Columns(COL_FP).ReadOnly = True
        dgvPeers.Columns(COL_P2P).ReadOnly = True
        dgvPeers.Columns(COL_CRYPTO).ReadOnly = True
        dgvPeers.Columns(COL_LAST).ReadOnly = True
        dgvPeers.Columns(COL_MSGS).ReadOnly = True
        dgvPeers.Columns(COL_FILES).ReadOnly = True
        dgvPeers.Columns(COL_TRUST).ReadOnly = True
        ' Note éditable
        AddHandler dgvPeers.CellEndEdit, AddressOf OnCellEndEdit

        ' Menu contextuel
        ctxMenu = New ContextMenuStrip()
        ctxMenu.Items.Add("Copier empreinte", Nothing, AddressOf OnCopyFp)
        ctxMenu.Items.Add("Ouvrir détails", Nothing, AddressOf OnOpenDetails)
        ctxMenu.Items.Add("Oublier confiance", Nothing, AddressOf OnForgetTrust)
        AddHandler dgvPeers.MouseDown, AddressOf OnGridMouseDown

        Me.Controls.Add(dgvPeers)
    End Sub

    ' ------------------------ Local identity ------------------------
    Private Sub LoadLocalIdentityInfo()
        Try
            txtIdPath.Text = _idFilePath
            ' Essaye de lire 96 octets (pub 32 + priv 64)
            Dim fpText As String = "(n/a)"
            If File.Exists(_idFilePath) Then
                Dim blob As Byte() = File.ReadAllBytes(_idFilePath)
                If blob IsNot Nothing AndAlso blob.Length >= 32 Then
                    Dim pub(31) As Byte
                    Buffer.BlockCopy(blob, 0, pub, 0, 32)
                    Using sha As SHA256 = SHA256.Create()
                        fpText = BitConverter.ToString(sha.ComputeHash(pub)).Replace("-", "").ToLowerInvariant()
                    End Using
                End If
            End If
            lblLocalFp.Text = "Empreinte: " & fpText
        Catch
            lblLocalFp.Text = "Empreinte: (n/a)"
        End Try
    End Sub

    ' ------------------------ Load peers ------------------------
    Private Sub LoadPeers()
        dgvPeers.SuspendLayout()
        dgvPeers.Rows.Clear()
        Try
            If PeersProvider Is Nothing OrElse PeerStatusProvider Is Nothing Then Exit Sub

            Dim getPeers As Func(Of List(Of String)) = PeersProvider
            If getPeers Is Nothing Then Exit Sub
            Dim allPeers As List(Of String) = getPeers.Invoke()
            If allPeers Is Nothing Then Exit Sub

            ' filtre
            Dim filter As String = txtSearch.Text.Trim()
            Dim peers As IEnumerable(Of String) = allPeers
            If filter.Length > 0 Then
                peers = allPeers.Where(Function(s) s.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            End If

            For Each p As String In peers
                If String.IsNullOrWhiteSpace(p) Then Continue For
                Dim st = PeerStatusProvider(p)

                Dim lastTxt As String = "—"
                Dim msgsTxt As String = "—"
                Dim filesTxt As String = "—"
                Dim trustTxt As String = "—"
                Dim noteTxt As String = ""

                If ExtendedPeerStatusProvider IsNot Nothing Then
                    Dim ex = ExtendedPeerStatusProvider(p)
                    If ex IsNot Nothing Then
                        If ex.LastContactUtc.HasValue Then
                            ' Affiche en heure locale courte
                            lastTxt = ex.LastContactUtc.Value.ToLocalTime().ToString("g")
                        End If
                        msgsTxt = CStr(ex.MsgsSent) & " / " & CStr(ex.MsgsRecv)
                        filesTxt = CStr(ex.FilesSent) & " / " & CStr(ex.FilesRecv)
                        If Not String.IsNullOrEmpty(ex.TrustLevel) Then trustTxt = ex.TrustLevel
                        If ex.Note IsNot Nothing Then noteTxt = ex.Note
                    End If
                End If

                dgvPeers.Rows.Add(
                    p,
                    If(st.Verified, "OK", "—"),
                    If(String.IsNullOrEmpty(st.Fp), "—", st.Fp),
                    If(st.P2PConnected, "ON", "OFF"),
                    If(st.CryptoActive, "ON", "OFF"),
                    lastTxt,
                    msgsTxt,
                    filesTxt,
                    trustTxt,
                    noteTxt
                )
            Next

            ' Ajuste la largeur de quelques colonnes
            dgvPeers.Columns(COL_VERIF).Width = 90
            dgvPeers.Columns(COL_P2P).Width = 70
            dgvPeers.Columns(COL_CRYPTO).Width = 75
            dgvPeers.Columns(COL_LAST).Width = 140
            dgvPeers.Columns(COL_TRUST).Width = 100
        Finally
            dgvPeers.ResumeLayout()
        End Try
    End Sub

    ' ------------------------ Toolbar actions ------------------------
    Private Sub OnOpenDir(sender As Object, e As EventArgs)
        Try
            Dim dir As String = Path.GetDirectoryName(_idFilePath)
            If Not String.IsNullOrEmpty(dir) AndAlso Directory.Exists(dir) Then
                Process.Start("explorer.exe", dir)
            End If
        Catch
        End Try
    End Sub

    Private Sub OnBackup(sender As Object, e As EventArgs)
        Try
            If Not File.Exists(_idFilePath) Then
                MessageBox.Show("Fichier identité introuvable.", "Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If
            Using sfd As New SaveFileDialog()
                sfd.Title = "Backup identité"
                sfd.Filter = "Identity bin|*.bin|Tous fichiers|*.*"
                sfd.FileName = "id_ed25519_backup.bin"
                If sfd.ShowDialog(Me) <> DialogResult.OK Then Return
                File.Copy(_idFilePath, sfd.FileName, True)
                MessageBox.Show("Backup effectué.", "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information)
            End Using
        Catch ex As Exception
            MessageBox.Show("Backup: " & ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub OnImport(sender As Object, e As EventArgs)
        Try
            Using ofd As New OpenFileDialog()
                ofd.Title = "Importer identité"
                ofd.Filter = "Identity bin|*.bin|Tous fichiers|*.*"
                If ofd.ShowDialog(Me) <> DialogResult.OK Then Return
                Dim buf As Byte() = File.ReadAllBytes(ofd.FileName)
                If buf Is Nothing OrElse buf.Length <> 96 Then
                    MessageBox.Show("Format invalide (attendu 96 octets).", "Importer", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return
                End If
                File.Copy(ofd.FileName, _idFilePath, True)
                LoadLocalIdentityInfo()
                MessageBox.Show("Identité importée.", "Importer", MessageBoxButtons.OK, MessageBoxIcon.Information)
            End Using
        Catch ex As Exception
            MessageBox.Show("Import: " & ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub OnRegenerate(sender As Object, e As EventArgs)
        If MessageBox.Show("Générer une NOUVELLE identité ? L’ancienne sera perdue.", "Régénérer",
                           MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then Return
        Try
            ' Juste régénérer et écraser le fichier (la Form1 ré-applique côté Core)
            Dim kp = Sodium.PublicKeyAuth.GenerateKeyPair() ' Ed25519
            Dim blob(95) As Byte
            Buffer.BlockCopy(kp.PublicKey, 0, blob, 0, 32)
            Buffer.BlockCopy(kp.PrivateKey, 0, blob, 32, 64)
            Directory.CreateDirectory(Path.GetDirectoryName(_idFilePath))
            File.WriteAllBytes(_idFilePath, blob)
            LoadLocalIdentityInfo()
            MessageBox.Show("Nouvelle identité générée.", "Régénérer", MessageBoxButtons.OK, MessageBoxIcon.Information)
        Catch ex As Exception
            MessageBox.Show("Régénérer: " & ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub OnRefresh(sender As Object, e As EventArgs)
        LoadPeers()
    End Sub

    Private Sub OnCopyFp(sender As Object, e As EventArgs)
        Try
            If dgvPeers.SelectedRows.Count = 0 Then Return
            Dim fp As String = CStr(dgvPeers.SelectedRows(0).Cells(COL_FP).Value)
            If String.IsNullOrEmpty(fp) OrElse fp = "—" Then Return
            Clipboard.SetText(fp)
        Catch
        End Try
    End Sub

    Private Sub OnExportCsv(sender As Object, e As EventArgs)
        Try
            Using sfd As New SaveFileDialog()
                sfd.Title = "Exporter tableau"
                sfd.Filter = "CSV|*.csv"
                sfd.FileName = "peers_security.csv"
                If sfd.ShowDialog(Me) <> DialogResult.OK Then Return
                Using sw As New StreamWriter(sfd.FileName, False, Encoding.UTF8)
                    sw.WriteLine("Peer;Verified;Fingerprint;P2P;Crypto;LastContact;Msgs;Files;Trust;Note")
                    For Each row As DataGridViewRow In dgvPeers.Rows
                        Dim vals As New List(Of String)
                        For i = 0 To dgvPeers.Columns.Count - 1
                            Dim v As Object = row.Cells(i).Value
                            Dim s As String = If(v Is Nothing, "", CStr(v))
                            ' Échappe les ;
                            s = s.Replace(";", ",")
                            vals.Add(s)
                        Next
                        sw.WriteLine(String.Join(";", vals))
                    Next
                End Using
                MessageBox.Show("Export terminé.", "Export CSV", MessageBoxButtons.OK, MessageBoxIcon.Information)
            End Using
        Catch ex As Exception
            MessageBox.Show("Export: " & ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub OnImportCsv(sender As Object, e As EventArgs)
        Try
            Using ofd As New OpenFileDialog()
                ofd.Title = "Importer notes/trust (CSV)"
                ofd.Filter = "CSV|*.csv"
                If ofd.ShowDialog(Me) <> DialogResult.OK Then Return
                ' Minimal : recharge la grille, Puis remappe Note/Trust si SetPeerNoteAction fourni
                Dim lines As String() = File.ReadAllLines(ofd.FileName, Encoding.UTF8)
                If lines.Length <= 1 Then Return
                Dim dictNotes As New Dictionary(Of String, String)(StringComparer.Ordinal)
                Dim dictTrust As New Dictionary(Of String, String)(StringComparer.Ordinal)
                For i = 1 To lines.Length - 1
                    Dim parts = lines(i).Split(";"c)
                    If parts.Length >= 10 Then
                        Dim peer = parts(0)
                        Dim trust = parts(8)
                        Dim note = parts(9)
                        If Not String.IsNullOrWhiteSpace(peer) Then
                            dictTrust(peer) = trust
                            dictNotes(peer) = note
                        End If
                    End If
                Next
                ' Applique les notes si callback dispo
                If SetPeerNoteAction IsNot Nothing Then
                    For Each kv In dictNotes
                        SetPeerNoteAction.Invoke(kv.Key, kv.Value)
                    Next
                End If
                ' (le trust n’est pas appliqué ici pour éviter les erreurs métier)
                LoadPeers()
                MessageBox.Show("Import terminé (notes appliquées si supportées).", "Import CSV", MessageBoxButtons.OK, MessageBoxIcon.Information)
            End Using
        Catch ex As Exception
            MessageBox.Show("Import: " & ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub OnForgetTrust(sender As Object, e As EventArgs)
        Try
            If dgvPeers.SelectedRows.Count = 0 Then Return
            Dim peer As String = CStr(dgvPeers.SelectedRows(0).Cells(COL_PEER).Value)
            If String.IsNullOrWhiteSpace(peer) Then Return
            If ForgetPeerAction Is Nothing Then
                MessageBox.Show("Action non branchée dans Form1.", "Oublier la confiance", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            If MessageBox.Show("Oublier la confiance de " & peer & " ?", "Confirmer",
                               MessageBoxButtons.YesNo, MessageBoxIcon.Question) <> DialogResult.Yes Then Return
            ForgetPeerAction.Invoke(peer)
            LoadPeers()
        Catch ex As Exception
            MessageBox.Show("Oublier: " & ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub OnPurgeEph(sender As Object, e As EventArgs)
        Try
            If PurgeEphemeralAction Is Nothing Then
                MessageBox.Show("Action non branchée dans Form1.", "Purger clés éphémères", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            PurgeEphemeralAction.Invoke()
            MessageBox.Show("Clés éphémères purgées.", "Éphémères", MessageBoxButtons.OK, MessageBoxIcon.Information)
        Catch ex As Exception
            MessageBox.Show("Purge: " & ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    ' ------------------------ Grid helpers ------------------------
    Private Sub OnGridMouseDown(sender As Object, e As MouseEventArgs)
        If e.Button <> MouseButtons.Right Then Return
        Dim hit = dgvPeers.HitTest(e.X, e.Y)
        If hit.RowIndex >= 0 Then
            dgvPeers.ClearSelection()
            dgvPeers.Rows(hit.RowIndex).Selected = True
            ctxMenu.Show(dgvPeers, e.Location)
        End If
    End Sub

    Private Sub OnOpenDetails(sender As Object, e As EventArgs)
        Try
            If dgvPeers.SelectedRows.Count = 0 Then Return
            Dim peer As String = CStr(dgvPeers.SelectedRows(0).Cells(COL_PEER).Value)
            If String.IsNullOrWhiteSpace(peer) Then Return
            If ViewPeerDetailsAction IsNot Nothing Then
                ViewPeerDetailsAction.Invoke(peer)
            Else
                MessageBox.Show("Action non branchée dans Form1.", "Détails", MessageBoxButtons.OK, MessageBoxIcon.Information)
            End If
        Catch
        End Try
    End Sub

    Private Sub OnCellEndEdit(sender As Object, e As DataGridViewCellEventArgs)
        If e.RowIndex < 0 Then Return
        If e.ColumnIndex <> COL_NOTE Then Return
        Try
            Dim peer As String = CStr(dgvPeers.Rows(e.RowIndex).Cells(COL_PEER).Value)
            Dim note As String = CStr(dgvPeers.Rows(e.RowIndex).Cells(COL_NOTE).Value)
            If SetPeerNoteAction IsNot Nothing Then
                SetPeerNoteAction.Invoke(peer, If(note, ""))
            End If
        Catch
        End Try
    End Sub

    Private Sub OnSearchChanged(sender As Object, e As EventArgs)
        LoadPeers()
    End Sub

    Private Sub OnAutoTick(sender As Object, e As EventArgs)
        LoadPeers()
    End Sub
End Class
