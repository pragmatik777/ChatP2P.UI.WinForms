Option Strict On
Option Explicit On

Imports System
Imports System.Data
Imports System.Globalization
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Windows.Forms
Imports ChatP2P.Core

Public Class SecurityCenterForm
    Inherits Form

    Private ReadOnly _preselect As String

    Private dgv As DataGridView
    Private txtSearch As TextBox
    Private lblMyFp As Label
    Private btnTrust As Button
    Private btnUntrust As Button
    Private btnCopyFp As Button
    Private btnDetails As Button
    Private btnResetTofu As Button
    Private btnImportPk As Button
    Private btnExportMyFp As Button
    Private btnClose As Button

    Public Sub New(Optional preselectPeer As String = "")
        Me.Text = "Security Center"
        Me.StartPosition = FormStartPosition.CenterParent
        Me.Width = 980
        Me.Height = 620

        _preselect = preselectPeer

        BuildUi()
        AddHandlers()
    End Sub

    Private Sub BuildUi()
        dgv = New DataGridView() With {
            .Dock = DockStyle.Fill,
            .ReadOnly = True,
            .AllowUserToAddRows = False,
            .AllowUserToDeleteRows = False,
            .SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            .MultiSelect = False,
            .RowHeadersVisible = False,
            .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        }

        txtSearch = New TextBox() With {
            .Dock = DockStyle.Top,
            .PlaceholderText = "Rechercher (nom, empreinte)…"
        }

        Dim pnlTop As New Panel() With {.Dock = DockStyle.Top, .Height = 64}
        Dim pnlBtns As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .Height = 40, .FlowDirection = FlowDirection.LeftToRight, .WrapContents = False}

        btnTrust = New Button() With {.Text = "Marquer Trusted", .Width = 140}
        btnUntrust = New Button() With {.Text = "Retirer Trusted", .Width = 140}
        btnCopyFp = New Button() With {.Text = "Copier empreinte", .Width = 140}
        btnDetails = New Button() With {.Text = "Détails", .Width = 100}
        btnResetTofu = New Button() With {.Text = "Reset TOFU", .Width = 120}
        btnImportPk = New Button() With {.Text = "Importer pubkey…", .Width = 150}
        btnExportMyFp = New Button() With {.Text = "Exporter mon empreinte", .Width = 190}
        btnClose = New Button() With {.Text = "Fermer", .Width = 100}

        pnlBtns.Controls.AddRange({btnTrust, btnUntrust, btnCopyFp, btnDetails, btnResetTofu, btnImportPk, btnExportMyFp, btnClose})

        lblMyFp = New Label() With {.Dock = DockStyle.Top, .AutoSize = False, .Height = 20, .Text = "Mon empreinte : (chargement…)"}

        pnlTop.Controls.Add(pnlBtns)
        pnlTop.Controls.Add(lblMyFp)
        pnlTop.Controls.Add(txtSearch)

        Me.Controls.Add(dgv)
        Me.Controls.Add(pnlTop)
    End Sub

    Private Sub AddHandlers()
        AddHandler Me.Load, AddressOf SecurityCenterForm_Load
        AddHandler txtSearch.TextChanged, AddressOf txtSearch_TextChanged
        AddHandler btnTrust.Click, AddressOf btnTrust_Click
        AddHandler btnUntrust.Click, AddressOf btnUntrust_Click
        AddHandler btnCopyFp.Click, AddressOf btnCopyFp_Click
        AddHandler btnDetails.Click, AddressOf btnDetails_Click
        AddHandler btnResetTofu.Click, AddressOf btnResetTofu_Click
        AddHandler btnImportPk.Click, AddressOf btnImportPk_Click
        AddHandler btnExportMyFp.Click, AddressOf btnExportMyFp_Click
        AddHandler btnClose.Click, Sub() Me.Close()
        AddHandler dgv.CellDoubleClick, Sub() ShowDetailsForSelected()
    End Sub

    Private Sub SecurityCenterForm_Load(sender As Object, e As EventArgs)
        Try
            ' Migrations très légères (notes + VerifiedUtc si dispo)
            LocalDbExtensionsSecurity.EnsurePeerExtraColumns()

            RefreshMyFingerprintLabel()
            LoadPeers()

            If Not String.IsNullOrWhiteSpace(_preselect) Then
                SelectRowByPeer(_preselect)
            End If
        Catch ex As Exception
            MessageBox.Show("Erreur au chargement du Security Center: " & ex.Message)
        End Try
    End Sub

    Private Sub RefreshMyFingerprintLabel()
        Try
            Dim pk As Byte() = Nothing
            Dim sk As Byte() = Nothing
            LocalDbExtensions.IdentityEnsureEd25519(pk, sk)
            If pk Is Nothing OrElse pk.Length = 0 Then
                lblMyFp.Text = "Mon empreinte : (clé introuvable)"
                Return
            End If
            Dim fp = FormatFingerprint(ComputeFingerprint(pk))
            lblMyFp.Text = "Mon empreinte : " & fp
        Catch
            lblMyFp.Text = "Mon empreinte : (erreur)"
        End Try
    End Sub

    Private Sub LoadPeers(Optional filter As String = "")
        Dim dt = LocalDbExtensionsSecurity.PeerList()
        Dim view As DataView = dt.DefaultView

        If Not String.IsNullOrWhiteSpace(filter) Then
            ' Filtre simple sur nom et empreinte affichée
            Dim esc = filter.Replace("'", "''")
            view.RowFilter = $"Name LIKE '%{esc}%' OR Fingerprint LIKE '%{esc}%'"
        Else
            view.RowFilter = ""
        End If

        dgv.DataSource = view

        If dgv.Columns.Contains("Name") Then dgv.Columns("Name").HeaderText = "Pair"
        If dgv.Columns.Contains("Trusted") Then dgv.Columns("Trusted").HeaderText = "Trusted"
        If dgv.Columns.Contains("AuthOk") Then dgv.Columns("AuthOk").HeaderText = "Auth OK"
        If dgv.Columns.Contains("Fingerprint") Then dgv.Columns("Fingerprint").HeaderText = "Empreinte (SHA-256/Ed25519)"
        If dgv.Columns.Contains("CreatedUtc") Then dgv.Columns("CreatedUtc").HeaderText = "Créé"
        If dgv.Columns.Contains("LastSeenUtc") Then dgv.Columns("LastSeenUtc").HeaderText = "Vu"
        If dgv.Columns.Contains("Note") Then dgv.Columns("Note").HeaderText = "Note"

        dgv.AutoResizeColumns()
    End Sub

    Private Sub txtSearch_TextChanged(sender As Object, e As EventArgs)
        LoadPeers(txtSearch.Text.Trim())
    End Sub

    Private Function GetSelectedPeerName() As String
        If dgv.CurrentRow Is Nothing OrElse dgv.CurrentRow.DataBoundItem Is Nothing Then Return ""
        Dim drv = TryCast(dgv.CurrentRow.DataBoundItem, DataRowView)
        If drv Is Nothing Then Return ""
        Return Convert.ToString(drv("Name"), CultureInfo.InvariantCulture)
    End Function

    Private Sub btnTrust_Click(sender As Object, e As EventArgs)
        Dim peer = GetSelectedPeerName()
        If String.IsNullOrWhiteSpace(peer) Then Return
        Try
            LocalDbExtensionsSecurity.PeerSetTrusted(peer, True)
            LoadPeers(txtSearch.Text.Trim())
            SelectRowByPeer(peer)
        Catch ex As Exception
            MessageBox.Show("Erreur Trust: " & ex.Message)
        End Try
    End Sub

    Private Sub btnUntrust_Click(sender As Object, e As EventArgs)
        Dim peer = GetSelectedPeerName()
        If String.IsNullOrWhiteSpace(peer) Then Return
        Try
            LocalDbExtensionsSecurity.PeerSetTrusted(peer, False)
            LoadPeers(txtSearch.Text.Trim())
            SelectRowByPeer(peer)
        Catch ex As Exception
            MessageBox.Show("Erreur Untrust: " & ex.Message)
        End Try
    End Sub

    Private Sub btnCopyFp_Click(sender As Object, e As EventArgs)
        Dim peer = GetSelectedPeerName()
        If String.IsNullOrWhiteSpace(peer) Then Return
        Try
            Dim pk = LocalDbExtensions.PeerGetEd25519(peer)
            If pk Is Nothing OrElse pk.Length = 0 Then
                MessageBox.Show("Pas de clé Ed25519 connue pour ce pair (TOFU non établi).")
                Return
            End If
            Dim fp = FormatFingerprint(ComputeFingerprint(pk))
            Clipboard.SetText(fp)
            MessageBox.Show("Empreinte copiée dans le presse-papiers : " & fp)
        Catch ex As Exception
            MessageBox.Show("Erreur copie empreinte: " & ex.Message)
        End Try
    End Sub

    Private Sub btnDetails_Click(sender As Object, e As EventArgs)
        ShowDetailsForSelected()
    End Sub

    Private Sub ShowDetailsForSelected()
        Dim peer = GetSelectedPeerName()
        If String.IsNullOrWhiteSpace(peer) Then Return

        Try
            Dim pk = LocalDbExtensions.PeerGetEd25519(peer)
            Dim fp = If(pk IsNot Nothing AndAlso pk.Length > 0, FormatFingerprint(ComputeFingerprint(pk)), "(inconnu)")
            Dim trusted = LocalDb.GetPeerTrusted(peer)
            Dim verified = LocalDbExtensionsSecurity.PeerIsVerified(peer)
            Dim createdIso = LocalDbExtensionsSecurity.PeerGetField(peer, "CreatedUtc")
            Dim lastSeenIso = LocalDbExtensionsSecurity.PeerGetField(peer, "LastSeenUtc")
            Dim note = LocalDbExtensionsSecurity.PeerGetNote(peer)

            Dim sb As New StringBuilder()
            sb.AppendLine("Pair : " & peer)
            sb.AppendLine("Trusted : " & If(trusted, "Oui", "Non"))
            sb.AppendLine("Auth OK (Ed25519) : " & If(verified, "Oui", "Non"))
            sb.AppendLine("Empreinte : " & fp)
            sb.AppendLine("Créé (UTC) : " & createdIso)
            sb.AppendLine("Vu (UTC) : " & lastSeenIso)
            sb.AppendLine("Note : " & If(String.IsNullOrEmpty(note), "(vide)", note))

            Dim input = InputBox(sb.ToString() & vbCrLf & vbCrLf & "Modifier la note :", "Détails pair", note)
            If input IsNot Nothing Then
                LocalDbExtensionsSecurity.PeerSetNote(peer, input.Trim())
                LoadPeers(txtSearch.Text.Trim())
                SelectRowByPeer(peer)
            End If

        Catch ex As Exception
            MessageBox.Show("Erreur détails: " & ex.Message)
        End Try
    End Sub

    Private Sub btnResetTofu_Click(sender As Object, e As EventArgs)
        Dim peer = GetSelectedPeerName()
        If String.IsNullOrWhiteSpace(peer) Then Return

        If MessageBox.Show("Réinitialiser le TOFU pour " & peer & " ?" & vbCrLf &
                           "Cela oubliera la clé publique mémorisée. La prochaine rencontre sera considérée comme une première vue.",
                           "Reset TOFU", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) <> DialogResult.OK Then
            Return
        End If

        Try
            LocalDbExtensionsSecurity.PeerForgetEd25519(peer)
            LocalDbExtensionsSecurity.PeerMarkUnverified(peer)
            LoadPeers(txtSearch.Text.Trim())
            SelectRowByPeer(peer)
        Catch ex As Exception
            MessageBox.Show("Erreur reset TOFU: " & ex.Message)
        End Try
    End Sub

    Private Sub btnImportPk_Click(sender As Object, e As EventArgs)
        Dim peer = GetSelectedPeerName()
        If String.IsNullOrWhiteSpace(peer) Then
            MessageBox.Show("Sélectionne d'abord un pair.")
            Return
        End If

        Dim b64 = InputBox("Colle ici la clé publique Ed25519 du pair (Base64) :" & vbCrLf &
                           "Attention : écrase la clé TOFU existante.", "Importer pubkey")
        If String.IsNullOrWhiteSpace(b64) Then Return

        Try
            Dim pk = Convert.FromBase64String(b64.Trim())
            If pk Is Nothing OrElse pk.Length <> 32 Then
                MessageBox.Show("Clé invalide (attendu 32 octets en Ed25519).")
                Return
            End If
            LocalDbExtensions.PeerSetEd25519_Tofu(peer, pk)
            LocalDbExtensionsSecurity.PeerMarkUnverified(peer) ' on force une nouvelle preuve la prochaine fois
            LoadPeers(txtSearch.Text.Trim())
            SelectRowByPeer(peer)
            MessageBox.Show("Clé importée pour " & peer & " (TOFU mis à jour).")
        Catch ex As Exception
            MessageBox.Show("Erreur import clé: " & ex.Message)
        End Try
    End Sub

    Private Sub btnExportMyFp_Click(sender As Object, e As EventArgs)
        Try
            Dim pk As Byte() = Nothing
            Dim sk As Byte() = Nothing
            LocalDbExtensions.IdentityEnsureEd25519(pk, sk)
            If pk Is Nothing OrElse pk.Length = 0 Then
                MessageBox.Show("Clé locale introuvable.")
                Return
            End If
            Dim b64 = Convert.ToBase64String(pk)
            Dim fp = FormatFingerprint(ComputeFingerprint(pk))
            Clipboard.SetText("PubKey(Base64): " & b64 & vbCrLf & "Fingerprint: " & fp)
            MessageBox.Show("Ta pubkey + empreinte ont été copiées dans le presse-papiers." & vbCrLf & fp)
        Catch ex As Exception
            MessageBox.Show("Erreur export: " & ex.Message)
        End Try
    End Sub

    Private Sub SelectRowByPeer(peer As String)
        For Each row As DataGridViewRow In dgv.Rows
            Dim drv = TryCast(row.DataBoundItem, DataRowView)
            If drv IsNot Nothing AndAlso String.Equals(Convert.ToString(drv("Name")), peer, StringComparison.OrdinalIgnoreCase) Then
                row.Selected = True
                dgv.CurrentCell = row.Cells(0)
                dgv.FirstDisplayedScrollingRowIndex = row.Index
                Exit For
            End If
        Next
    End Sub

    ' === Utils fingerprint ===
    Private Shared Function ComputeFingerprint(pub As Byte()) As Byte()
        Using sha As SHA256 = SHA256.Create()
            Return sha.ComputeHash(pub)
        End Using
    End Function

    Private Shared Function FormatFingerprint(fp As Byte()) As String
        ' format lisible : groupes hex de 4 (ex: AB12-CD34-…)
        Dim hex = BitConverter.ToString(fp).Replace("-", "")
        Dim sb As New StringBuilder()
        For i = 0 To hex.Length - 1 Step 4
            If sb.Length > 0 Then sb.Append("-")
            Dim take = Math.Min(4, hex.Length - i)
            sb.Append(hex.Substring(i, take))
        Next
        Return sb.ToString()
    End Function

End Class
