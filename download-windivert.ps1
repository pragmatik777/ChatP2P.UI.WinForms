# 🕷️ Script PowerShell pour télécharger WinDivert automatiquement
# Usage: .\download-windivert.ps1

param(
    [string]$Version = "2.2.2-A",
    [string]$ProjectPath = "ChatP2P.SecurityTester"
)

Write-Host "🕷️ TÉLÉCHARGEMENT WINDIVERT AUTOMATIQUE" -ForegroundColor Green
Write-Host "Version: $Version" -ForegroundColor Yellow

# URLs
$downloadUrl = "https://github.com/basil00/Divert/releases/download/v$Version/WinDivert-$Version.zip"
$tempZip = "$env:TEMP\WinDivert-$Version.zip"
$tempExtract = "$env:TEMP\WinDivert-$Version"

# Dossiers destination
$binPath = "$ProjectPath\bin\Debug\net8.0-windows"
$winDivertPath = "$ProjectPath\WinDivert"

Write-Host "📥 Téléchargement depuis: $downloadUrl" -ForegroundColor Cyan

try {
    # Télécharger WinDivert
    Write-Host "⬇️ Téléchargement WinDivert..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri $downloadUrl -OutFile $tempZip -UseBasicParsing
    Write-Host "✅ Téléchargement terminé: $tempZip" -ForegroundColor Green

    # Extraire
    Write-Host "📦 Extraction en cours..." -ForegroundColor Yellow
    if (Test-Path $tempExtract) {
        Remove-Item $tempExtract -Recurse -Force
    }
    Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force
    Write-Host "✅ Extraction terminée" -ForegroundColor Green

    # Trouver les fichiers WinDivert
    $extractedFiles = Get-ChildItem -Path $tempExtract -Recurse -File | Where-Object {
        $_.Name -match "WinDivert\.(dll|sys)$"
    }

    if ($extractedFiles.Count -eq 0) {
        Write-Host "❌ Fichiers WinDivert non trouvés dans l'archive" -ForegroundColor Red
        exit 1
    }

    Write-Host "📁 Fichiers trouvés:" -ForegroundColor Cyan
    foreach ($file in $extractedFiles) {
        Write-Host "   - $($file.Name)" -ForegroundColor White
    }

    # Créer dossiers destination
    Write-Host "📂 Création des dossiers destination..." -ForegroundColor Yellow

    if (!(Test-Path $winDivertPath)) {
        New-Item -ItemType Directory -Path $winDivertPath -Force | Out-Null
        Write-Host "✅ Créé: $winDivertPath" -ForegroundColor Green
    }

    if (!(Test-Path $binPath)) {
        New-Item -ItemType Directory -Path $binPath -Force | Out-Null
        Write-Host "✅ Créé: $binPath" -ForegroundColor Green
    }

    # Copier les fichiers
    Write-Host "📋 Copie des fichiers WinDivert..." -ForegroundColor Yellow

    foreach ($file in $extractedFiles) {
        # Copier vers le dossier WinDivert (source)
        $destWinDivert = Join-Path $winDivertPath $file.Name
        Copy-Item $file.FullName $destWinDivert -Force
        Write-Host "✅ Copié vers source: $destWinDivert" -ForegroundColor Green

        # Copier vers le dossier bin (runtime)
        $destBin = Join-Path $binPath $file.Name
        Copy-Item $file.FullName $destBin -Force
        Write-Host "✅ Copié vers runtime: $destBin" -ForegroundColor Green
    }

    # Nettoyage
    Write-Host "🧹 Nettoyage des fichiers temporaires..." -ForegroundColor Yellow
    Remove-Item $tempZip -Force -ErrorAction SilentlyContinue
    Remove-Item $tempExtract -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host ""
    Write-Host "🎉 WINDIVERT INSTALLÉ AVEC SUCCÈS!" -ForegroundColor Green
    Write-Host "📁 Fichiers copiés dans:" -ForegroundColor Cyan
    Write-Host "   - $winDivertPath (source)" -ForegroundColor White
    Write-Host "   - $binPath (runtime)" -ForegroundColor White
    Write-Host ""
    Write-Host "⚠️  IMPORTANT:" -ForegroundColor Red
    Write-Host "   - Lancer Security Tester en tant qu'ADMINISTRATEUR" -ForegroundColor Yellow
    Write-Host "   - WinDivert nécessite des privilèges élevés" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "🚀 Prêt pour l'interception de packets niveau kernel!" -ForegroundColor Green

}
catch {
    Write-Host "❌ ERREUR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "💡 Solutions possibles:" -ForegroundColor Yellow
    Write-Host "   1. Vérifier la connexion internet" -ForegroundColor White
    Write-Host "   2. Exécuter en tant qu'administrateur" -ForegroundColor White
    Write-Host "   3. Télécharger manuellement depuis: https://github.com/basil00/Divert/releases" -ForegroundColor White
    exit 1
}