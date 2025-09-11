' ChatP2P.UI.WinForms/P2PAdvancedForm.vb
Option Strict On
Imports System.Windows.Forms
Imports System.Drawing

Public Class P2PAdvancedForm
    Inherits Form

    ' ==== Configuration P2P BitTorrent ====
    Public Property ChunkSize As Integer = 8192
    Public Property BatchSize As Integer = 200
    Public Property BatchDelayMs As Integer = 10
    Public Property SetupDelayMs As Integer = 100
    Public Property MaxRetries As Integer = 3
    Public Property EnableDebugLogs As Boolean = True
    
    ' Bandwidth limiting
    Public Property EnableBandwidthLimit As Boolean = False
    Public Property MaxSpeedKBps As Integer = 1000  ' 1MB/s par défaut
    
    ' Legacy properties (pour petits fichiers)
    Public Property MaxConcurrentChunks As Integer = 1
    Public Property ChunkDelayMs As Integer = 20

    ' ==== Controls ====
    Private ReadOnly grpChunks As New GroupBox() With {
        .Text = "Configuration des Chunks",
        .Location = New Point(10, 10),
        .Size = New Size(360, 140),
        .ForeColor = Color.DarkBlue,
        .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
    }

    Private ReadOnly lblChunkSize As New Label() With {
        .Text = "Taille chunk (bytes):",
        .Location = New Point(10, 25),
        .Size = New Size(120, 20),
        .Font = New Font("Segoe UI", 9.0F)
    }
    Private ReadOnly numChunkSize As New NumericUpDown() With {
        .Location = New Point(140, 23),
        .Size = New Size(80, 23),
        .Minimum = 1024,
        .Maximum = 65536,
        .Value = 8192,
        .Increment = 1024
    }

    Private ReadOnly lblBatchSize As New Label() With {
        .Text = "Taille batch (chunks):",
        .Location = New Point(10, 55),
        .Size = New Size(120, 20),
        .Font = New Font("Segoe UI", 9.0F)
    }
    Private ReadOnly numBatchSize As New NumericUpDown() With {
        .Location = New Point(140, 53),
        .Size = New Size(80, 23),
        .Minimum = 10,
        .Maximum = 500,
        .Value = 200,
        .Increment = 50
    }

    Private ReadOnly lblBatchDelay As New Label() With {
        .Text = "Délai entre batches (ms):",
        .Location = New Point(10, 85),
        .Size = New Size(120, 20),
        .Font = New Font("Segoe UI", 9.0F)
    }
    Private ReadOnly numBatchDelay As New NumericUpDown() With {
        .Location = New Point(140, 83),
        .Size = New Size(80, 23),
        .Minimum = 0,
        .Maximum = 1000,
        .Value = 10,
        .Increment = 5
    }

    Private ReadOnly lblSetupDelay As New Label() With {
        .Text = "Délai setup (ms):",
        .Location = New Point(240, 25),
        .Size = New Size(90, 20),
        .Font = New Font("Segoe UI", 9.0F)
    }
    Private ReadOnly numSetupDelay As New NumericUpDown() With {
        .Location = New Point(240, 45),
        .Size = New Size(80, 23),
        .Minimum = 0,
        .Maximum = 2000,
        .Value = 100,
        .Increment = 50
    }

    ' ==== Groupe BitTorrent ====
    Private ReadOnly grpBitTorrent As New GroupBox() With {
        .Text = "Configuration BitTorrent",
        .Location = New Point(10, 160),
        .Size = New Size(360, 110),
        .ForeColor = Color.DarkGreen,
        .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
    }

    Private ReadOnly lblMaxRetries As New Label() With {
        .Text = "Max retry par chunk:",
        .Location = New Point(10, 25),
        .Size = New Size(120, 20),
        .Font = New Font("Segoe UI", 9.0F)
    }
    Private ReadOnly numMaxRetries As New NumericUpDown() With {
        .Location = New Point(140, 23),
        .Size = New Size(80, 23),
        .Minimum = 1,
        .Maximum = 10,
        .Value = 3
    }
    
    Private ReadOnly chkHashVerification As New CheckBox() With {
        .Text = "Vérification SHA256",
        .Location = New Point(240, 25),
        .Size = New Size(120, 20),
        .Checked = True,
        .Font = New Font("Segoe UI", 9.0F)
    }
    
    Private ReadOnly chkBandwidthLimit As New CheckBox() With {
        .Text = "Limiter bande passante",
        .Location = New Point(10, 50),
        .Size = New Size(140, 20),
        .Checked = False,
        .Font = New Font("Segoe UI", 9.0F)
    }
    
    Private ReadOnly lblMaxSpeed As New Label() With {
        .Text = "Vitesse max (KB/s):",
        .Location = New Point(160, 50),
        .Size = New Size(100, 20),
        .Font = New Font("Segoe UI", 9.0F)
    }
    Private ReadOnly numMaxSpeed As New NumericUpDown() With {
        .Location = New Point(265, 48),
        .Size = New Size(80, 23),
        .Minimum = 10,
        .Maximum = 999999,
        .Value = 1000,
        .Increment = 50,
        .Enabled = False
    }

    ' ==== Groupe Presets & Debug ====
    Private ReadOnly grpPresets As New GroupBox() With {
        .Text = "Presets de Performance",
        .Location = New Point(10, 280),
        .Size = New Size(360, 120),
        .ForeColor = Color.DarkRed,
        .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
    }

    Private ReadOnly chkDebugLogs As New CheckBox() With {
        .Text = "Logs détaillés P2P",
        .Location = New Point(10, 25),
        .Size = New Size(140, 20),
        .Checked = True,
        .Font = New Font("Segoe UI", 9.0F)
    }

    Private ReadOnly chkProgressUpdates As New CheckBox() With {
        .Text = "Mises à jour progression",
        .Location = New Point(160, 25),
        .Size = New Size(150, 20),
        .Checked = True,
        .Font = New Font("Segoe UI", 9.0F)
    }

    Private ReadOnly btnUltraFast As New Button() With {
        .Text = "🚀 ULTRA RAPIDE",
        .Location = New Point(10, 50),
        .Size = New Size(105, 25),
        .BackColor = Color.Red,
        .ForeColor = Color.White,
        .Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
    }

    Private ReadOnly btnFast As New Button() With {
        .Text = "⚡ RAPIDE",
        .Location = New Point(125, 50),
        .Size = New Size(105, 25),
        .BackColor = Color.Orange,
        .ForeColor = Color.White,
        .Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
    }

    Private ReadOnly btnBalanced As New Button() With {
        .Text = "⚖️ ÉQUILIBRÉ",
        .Location = New Point(240, 50),
        .Size = New Size(105, 25),
        .BackColor = Color.Green,
        .ForeColor = Color.White,
        .Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
    }

    Private ReadOnly btnSafe As New Button() With {
        .Text = "🛡️ SÉCURISÉ",
        .Location = New Point(10, 80),
        .Size = New Size(105, 25),
        .BackColor = Color.Blue,
        .ForeColor = Color.White,
        .Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
    }

    Private ReadOnly btnResetDefaults As New Button() With {
        .Text = "↺ DÉFAUT",
        .Location = New Point(125, 80),
        .Size = New Size(105, 25),
        .BackColor = Color.Gray,
        .ForeColor = Color.White,
        .Font = New Font("Segoe UI", 8.5F)
    }

    ' ==== Boutons de contrôle ====
    Private ReadOnly btnOK As New Button() With {
        .Text = "Appliquer",
        .Location = New Point(220, 420),
        .Size = New Size(80, 30),
        .BackColor = Color.FromArgb(0, 120, 215),
        .ForeColor = Color.White,
        .FlatStyle = FlatStyle.Flat,
        .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold),
        .DialogResult = DialogResult.OK
    }

    Private ReadOnly btnCancel As New Button() With {
        .Text = "Annuler",
        .Location = New Point(310, 420),
        .Size = New Size(80, 30),
        .BackColor = Color.Gray,
        .ForeColor = Color.White,
        .FlatStyle = FlatStyle.Flat,
        .Font = New Font("Segoe UI", 9.0F),
        .DialogResult = DialogResult.Cancel
    }

    Public Sub New()
        ' Configuration de la form
        Me.Text = "Configuration P2P BitTorrent Avancée"
        Me.Size = New Size(400, 490)
        Me.FormBorderStyle = FormBorderStyle.FixedDialog
        Me.MaximizeBox = False
        Me.MinimizeBox = False
        Me.StartPosition = FormStartPosition.CenterParent
        Me.BackColor = Color.White

        ' Ajout des contrôles aux groupes
        grpChunks.Controls.AddRange({lblChunkSize, numChunkSize, lblBatchSize, numBatchSize, 
                                    lblBatchDelay, numBatchDelay, lblSetupDelay, numSetupDelay})

        grpBitTorrent.Controls.AddRange({lblMaxRetries, numMaxRetries, chkHashVerification, 
                                         chkBandwidthLimit, lblMaxSpeed, numMaxSpeed})

        grpPresets.Controls.AddRange({chkDebugLogs, chkProgressUpdates, btnUltraFast, 
                                     btnFast, btnBalanced, btnSafe, btnResetDefaults})

        ' Ajout à la form
        Me.Controls.AddRange({grpChunks, grpBitTorrent, grpPresets, btnOK, btnCancel})

        ' Événements
        AddHandler btnResetDefaults.Click, AddressOf BtnResetDefaults_Click
        AddHandler btnUltraFast.Click, AddressOf BtnUltraFast_Click
        AddHandler btnFast.Click, AddressOf BtnFast_Click
        AddHandler btnBalanced.Click, AddressOf BtnBalanced_Click
        AddHandler btnSafe.Click, AddressOf BtnSafe_Click
        AddHandler chkBandwidthLimit.CheckedChanged, AddressOf ChkBandwidthLimit_CheckedChanged
        AddHandler Me.Load, AddressOf P2PAdvancedForm_Load

        Me.AcceptButton = btnOK
        Me.CancelButton = btnCancel
    End Sub

    Private Sub P2PAdvancedForm_Load(sender As Object, e As EventArgs)
        ' Charger les valeurs depuis My.Settings en priorité, sinon valeurs par défaut
        LoadFromSettings()
    End Sub

    Private Sub LoadCurrentValues()
        numChunkSize.Value = ChunkSize
        numBatchSize.Value = BatchSize
        numBatchDelay.Value = BatchDelayMs
        numSetupDelay.Value = SetupDelayMs
        numMaxRetries.Value = MaxRetries
        chkHashVerification.Checked = True
        chkBandwidthLimit.Checked = EnableBandwidthLimit
        numMaxSpeed.Value = MaxSpeedKBps
        numMaxSpeed.Enabled = EnableBandwidthLimit
        chkDebugLogs.Checked = EnableDebugLogs
        chkProgressUpdates.Checked = True
    End Sub

    Private Sub BtnResetDefaults_Click(sender As Object, e As EventArgs)
        ' Valeurs par défaut BitTorrent équilibrées
        numChunkSize.Value = 8192
        numBatchSize.Value = 200
        numBatchDelay.Value = 10
        numSetupDelay.Value = 100
        numMaxRetries.Value = 3
        chkHashVerification.Checked = True
        chkBandwidthLimit.Checked = False
        numMaxSpeed.Value = 1000
        chkDebugLogs.Checked = True
        chkProgressUpdates.Checked = True
        MessageBox.Show("✅ Configuration par défaut restaurée", "Configuration", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub
    
    Private Sub ChkBandwidthLimit_CheckedChanged(sender As Object, e As EventArgs)
        numMaxSpeed.Enabled = chkBandwidthLimit.Checked
        lblMaxSpeed.Enabled = chkBandwidthLimit.Checked
    End Sub

    Private Sub BtnUltraFast_Click(sender As Object, e As EventArgs)
        ' 🚀 ULTRA RAPIDE - Maximum performance (risqué)
        numChunkSize.Value = 16384      ' 16KB chunks
        numBatchSize.Value = 500        ' 500 chunks par batch
        numBatchDelay.Value = 0         ' Aucun délai
        numSetupDelay.Value = 0         ' Aucune attente setup
        numMaxRetries.Value = 1         ' Retry minimal
        chkHashVerification.Checked = False  ' Skip verification pour vitesse
        chkBandwidthLimit.Checked = False    ' Pas de limite pour vitesse max
        chkDebugLogs.Checked = False
        chkProgressUpdates.Checked = False
        
        MessageBox.Show("🚀 ULTRA RAPIDE activé!" & Environment.NewLine & 
                       "⚠️ ATTENTION: Très risqué!" & Environment.NewLine &
                       "• Peut causer des crashes WebRTC" & Environment.NewLine &
                       "• Pas de vérification SHA256" & Environment.NewLine &
                       "• Pour petits fichiers uniquement", 
                       "ULTRA RAPIDE", MessageBoxButtons.OK, MessageBoxIcon.Warning)
    End Sub

    Private Sub BtnFast_Click(sender As Object, e As EventArgs)
        ' ⚡ RAPIDE - Performance élevée mais contrôlée
        numChunkSize.Value = 12288      ' 12KB chunks
        numBatchSize.Value = 350        ' 350 chunks par batch
        numBatchDelay.Value = 2         ' 2ms délai minimal
        numSetupDelay.Value = 25        ' Setup rapide
        numMaxRetries.Value = 2         ' Retry limité
        chkHashVerification.Checked = True
        chkBandwidthLimit.Checked = True    ' Limite pour stabilité
        numMaxSpeed.Value = 2000           ' 2MB/s
        chkDebugLogs.Checked = False
        chkProgressUpdates.Checked = True
        
        MessageBox.Show("⚡ RAPIDE activé!" & Environment.NewLine & 
                       "Performance élevée avec sécurité minimale" & Environment.NewLine &
                       "Bon compromis vitesse/stabilité", 
                       "RAPIDE", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub

    Private Sub BtnBalanced_Click(sender As Object, e As EventArgs)
        ' ⚖️ ÉQUILIBRÉ - Performance et stabilité équilibrées  
        numChunkSize.Value = 8192       ' 8KB chunks (défaut)
        numBatchSize.Value = 200        ' 200 chunks par batch
        numBatchDelay.Value = 10        ' 10ms délai
        numSetupDelay.Value = 100       ' 100ms setup
        numMaxRetries.Value = 3         ' 3 retry
        chkHashVerification.Checked = True
        chkBandwidthLimit.Checked = True    ' Limite raisonnable
        numMaxSpeed.Value = 1000           ' 1MB/s équilibré
        chkDebugLogs.Checked = True
        chkProgressUpdates.Checked = True
        
        MessageBox.Show("⚖️ ÉQUILIBRÉ activé!" & Environment.NewLine & 
                       "Configuration recommandée" & Environment.NewLine &
                       "Bon équilibre vitesse/stabilité", 
                       "ÉQUILIBRÉ", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub

    Private Sub BtnSafe_Click(sender As Object, e As EventArgs)
        ' 🛡️ SÉCURISÉ - Stabilité maximale
        numChunkSize.Value = 4096       ' 4KB chunks petits
        numBatchSize.Value = 100        ' 100 chunks par batch
        numBatchDelay.Value = 50        ' 50ms délai sécurisé
        numSetupDelay.Value = 500       ' 500ms setup prudent
        numMaxRetries.Value = 5         ' 5 retry max
        chkHashVerification.Checked = True
        chkBandwidthLimit.Checked = True    ' Limite conservatrice
        numMaxSpeed.Value = 500            ' 500KB/s sécurisé
        chkDebugLogs.Checked = True
        chkProgressUpdates.Checked = True
        
        MessageBox.Show("🛡️ SÉCURISÉ activé!" & Environment.NewLine & 
                       "Stabilité maximale garantie" & Environment.NewLine &
                       "Idéal pour gros fichiers critiques", 
                       "SÉCURISÉ", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub

    ''' <summary>
    ''' Sauvegarde les valeurs des contrôles dans les propriétés ET dans My.Settings
    ''' </summary>
    Public Sub SaveValues()
        ChunkSize = CInt(numChunkSize.Value)
        BatchSize = CInt(numBatchSize.Value)
        BatchDelayMs = CInt(numBatchDelay.Value)
        SetupDelayMs = CInt(numSetupDelay.Value)
        MaxRetries = CInt(numMaxRetries.Value)
        EnableBandwidthLimit = chkBandwidthLimit.Checked
        MaxSpeedKBps = CInt(numMaxSpeed.Value)
        EnableDebugLogs = chkDebugLogs.Checked
        
        ' Sauvegarde dans My.Settings
        SaveToSettings()
    End Sub
    
    ''' <summary>
    ''' Sauvegarde les valeurs actuelles dans un fichier simple
    ''' </summary>
    Public Sub SaveToSettings()
        Try
            Dim settingsFile = IO.Path.Combine(Application.StartupPath, "p2p_settings.txt")
            Using writer As New IO.StreamWriter(settingsFile)
                writer.WriteLine($"ChunkSize={ChunkSize}")
                writer.WriteLine($"BatchSize={BatchSize}")
                writer.WriteLine($"BatchDelayMs={BatchDelayMs}")
                writer.WriteLine($"SetupDelayMs={SetupDelayMs}")
                writer.WriteLine($"MaxRetries={MaxRetries}")
                writer.WriteLine($"EnableBandwidthLimit={EnableBandwidthLimit}")
                writer.WriteLine($"MaxSpeedKBps={MaxSpeedKBps}")
                writer.WriteLine($"EnableDebugLogs={EnableDebugLogs}")
                writer.WriteLine($"EnableHashVerification={chkHashVerification.Checked}")
                writer.WriteLine($"EnableProgressUpdates={chkProgressUpdates.Checked}")
            End Using
            Console.WriteLine($"[P2P SETTINGS] Settings saved to {settingsFile}")
        Catch ex As Exception
            Console.WriteLine($"[P2P SETTINGS] Error saving: {ex.Message}")
        End Try
    End Sub
    
    ''' <summary>
    ''' Charge les valeurs depuis un fichier simple vers les contrôles UI
    ''' </summary>
    Public Sub LoadFromSettings()
        Try
            Dim settingsFile = IO.Path.Combine(Application.StartupPath, "p2p_settings.txt")
            If IO.File.Exists(settingsFile) Then
                Using reader As New IO.StreamReader(settingsFile)
                    While Not reader.EndOfStream
                        Dim line = reader.ReadLine()
                        If String.IsNullOrEmpty(line) OrElse Not line.Contains("="c) Then Continue While
                        
                        Dim parts = line.Split("="c, 2)
                        If parts.Length <> 2 Then Continue While
                        
                        Dim key = parts(0).Trim()
                        Dim value = parts(1).Trim()
                        
                        Select Case key
                            Case "ChunkSize"
                                If Integer.TryParse(value, Nothing) Then numChunkSize.Value = Integer.Parse(value)
                            Case "BatchSize"
                                If Integer.TryParse(value, Nothing) Then numBatchSize.Value = Integer.Parse(value)
                            Case "BatchDelayMs"
                                If Integer.TryParse(value, Nothing) Then numBatchDelay.Value = Integer.Parse(value)
                            Case "SetupDelayMs"
                                If Integer.TryParse(value, Nothing) Then numSetupDelay.Value = Integer.Parse(value)
                            Case "MaxRetries"
                                If Integer.TryParse(value, Nothing) Then numMaxRetries.Value = Integer.Parse(value)
                            Case "EnableBandwidthLimit"
                                chkBandwidthLimit.Checked = (value = "True")
                            Case "MaxSpeedKBps"
                                If Integer.TryParse(value, Nothing) Then numMaxSpeed.Value = Integer.Parse(value)
                            Case "EnableDebugLogs"
                                chkDebugLogs.Checked = (value = "True")
                            Case "EnableHashVerification"
                                chkHashVerification.Checked = (value = "True")
                            Case "EnableProgressUpdates"
                                chkProgressUpdates.Checked = (value = "True")
                        End Select
                    End While
                End Using
                Console.WriteLine($"[P2P SETTINGS] Settings loaded from {settingsFile}")
            Else
                Console.WriteLine("[P2P SETTINGS] No saved settings found, using defaults")
                SetDefaultValues()
                Return
            End If
            
            ' Mise à jour de l'état des contrôles
            numMaxSpeed.Enabled = chkBandwidthLimit.Checked
            
            ' Mise à jour des propriétés
            ChunkSize = CInt(numChunkSize.Value)
            BatchSize = CInt(numBatchSize.Value)
            BatchDelayMs = CInt(numBatchDelay.Value)
            SetupDelayMs = CInt(numSetupDelay.Value)
            MaxRetries = CInt(numMaxRetries.Value)
            EnableBandwidthLimit = chkBandwidthLimit.Checked
            MaxSpeedKBps = CInt(numMaxSpeed.Value)
            EnableDebugLogs = chkDebugLogs.Checked
            
        Catch ex As Exception
            Console.WriteLine($"[P2P SETTINGS] Error loading (using defaults): {ex.Message}")
            SetDefaultValues()
        End Try
    End Sub
    
    ''' <summary>
    ''' Définit les valeurs par défaut (équilibré)
    ''' </summary>
    Public Sub SetDefaultValues()
        numChunkSize.Value = 8192
        numBatchSize.Value = 200
        numBatchDelay.Value = 10
        numSetupDelay.Value = 100
        numMaxRetries.Value = 3
        chkBandwidthLimit.Checked = True
        numMaxSpeed.Value = 1000
        chkDebugLogs.Checked = True
        chkHashVerification.Checked = True
        chkProgressUpdates.Checked = True
        numMaxSpeed.Enabled = True
        
        SaveValues() ' Sauvegarde les valeurs par défaut
    End Sub

    ''' <summary>
    ''' Récupère un résumé de la configuration actuelle
    ''' </summary>
    Public Function GetConfigSummary() As String
        Dim speedLimit = If(EnableBandwidthLimit, $"Max: {MaxSpeedKBps}KB/s", "Illimité")
        Return $"Chunks: {ChunkSize}B, Batch: {BatchSize}, " &
               $"Délais: {BatchDelayMs}ms/{SetupDelayMs}ms, {speedLimit}"
    End Function

End Class