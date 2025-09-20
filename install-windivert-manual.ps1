# Manual WinDivert installation guide
Write-Host "🕷️ INSTALLATION MANUELLE WINDIVERT" -ForegroundColor Green
Write-Host ""
Write-Host "1. Télécharger WinDivert depuis:" -ForegroundColor Yellow
Write-Host "   https://github.com/basil00/Divert/releases/latest" -ForegroundColor Cyan
Write-Host ""
Write-Host "2. Chercher le fichier:" -ForegroundColor Yellow
Write-Host "   WinDivert-2.2.2-A.zip" -ForegroundColor Cyan
Write-Host ""
Write-Host "3. Extraire et copier ces fichiers:" -ForegroundColor Yellow
Write-Host "   - WinDivert.dll" -ForegroundColor White
Write-Host "   - WinDivert64.sys" -ForegroundColor White
Write-Host "   - WinDivert32.sys" -ForegroundColor White
Write-Host ""
Write-Host "4. Vers ces dossiers:" -ForegroundColor Yellow
Write-Host "   - ChatP2P.SecurityTester\WinDivert\" -ForegroundColor White
Write-Host "   - ChatP2P.SecurityTester\bin\Debug\net8.0-windows\" -ForegroundColor White
Write-Host ""
Write-Host "5. Lancer Security Tester en tant qu'ADMINISTRATEUR" -ForegroundColor Red
Write-Host ""

# Create directories
$winDivertDir = "ChatP2P.SecurityTester\WinDivert"
$binDir = "ChatP2P.SecurityTester\bin\Debug\net8.0-windows"

New-Item -ItemType Directory -Path $winDivertDir -Force | Out-Null
New-Item -ItemType Directory -Path $binDir -Force | Out-Null

Write-Host "✅ Dossiers créés:" -ForegroundColor Green
Write-Host "   - $winDivertDir" -ForegroundColor White
Write-Host "   - $binDir" -ForegroundColor White
Write-Host ""
Write-Host "🔧 Maintenant copier manuellement les fichiers WinDivert !" -ForegroundColor Yellow