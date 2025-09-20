# Simple WinDivert downloader
$version = "2.2.2-A"
$url = "https://github.com/basil00/Divert/releases/download/v$version/WinDivert-$version.zip"
$zip = "$env:TEMP\windivert.zip"
$extract = "$env:TEMP\windivert"

Write-Host "Downloading WinDivert..." -ForegroundColor Green
Invoke-WebRequest -Uri $url -OutFile $zip

Write-Host "Extracting..." -ForegroundColor Yellow
Expand-Archive -Path $zip -DestinationPath $extract -Force

# Find WinDivert files
$files = Get-ChildItem -Path $extract -Recurse -Include "WinDivert.dll", "WinDivert64.sys", "WinDivert32.sys"

# Create destination folders
$destFolder = "ChatP2P.SecurityTester\WinDivert"
New-Item -ItemType Directory -Path $destFolder -Force

$binFolder = "ChatP2P.SecurityTester\bin\Debug\net8.0-windows"
New-Item -ItemType Directory -Path $binFolder -Force

# Copy files
foreach ($file in $files) {
    Copy-Item $file.FullName "$destFolder\$($file.Name)" -Force
    Copy-Item $file.FullName "$binFolder\$($file.Name)" -Force
    Write-Host "Copied: $($file.Name)" -ForegroundColor Green
}

Write-Host "WinDivert installed successfully!" -ForegroundColor Green
Write-Host "Run Security Tester as ADMINISTRATOR" -ForegroundColor Red

# Cleanup
Remove-Item $zip -Force
Remove-Item $extract -Recurse -Force