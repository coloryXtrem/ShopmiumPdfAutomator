# ============================================================
# BUILD.ps1 — Script de build et préparation du setup
# Lance avec : clic droit → "Exécuter avec PowerShell"
# OU dans PowerShell : .\BUILD.ps1
# ============================================================

$ErrorActionPreference = "Stop"
$ProjectDir  = "$PSScriptRoot\ShopmiumPdfAutomator"
$SetupDir    = "$PSScriptRoot\Setup"
$FilesDir    = "$SetupDir\files"
$PublishDir  = "$ProjectDir\bin\Release\net8.0-windows\win-x64\publish"

Write-Host ""
Write-Host "=== SHOPMIUM PDF AUTOMATOR — BUILD ===" -ForegroundColor Cyan
Write-Host ""

# Étape 1 : Compiler en mode "self-contained + dossier" (PAS fichier unique)
Write-Host "[1/3] Compilation en cours..." -ForegroundColor Yellow
Write-Host "      Mode : autonome, win-x64, dossier (inclut toutes les DLL)"
Write-Host ""

Set-Location $ProjectDir
dotnet publish `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=false

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERREUR : La compilation a échoué !" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "[1/3] Compilation OK ✓" -ForegroundColor Green

# Étape 2 : Copier TOUS les fichiers dans Setup/files/
Write-Host ""
Write-Host "[2/3] Copie des fichiers vers Setup/files/..." -ForegroundColor Yellow

if (Test-Path $FilesDir) { Remove-Item $FilesDir -Recurse -Force }
New-Item -ItemType Directory -Path $FilesDir | Out-Null

# Copier TOUT le dossier publish (exe + toutes les DLL)
Copy-Item "$PublishDir\*" -Destination $FilesDir -Recurse

# Copier le template PSD dans Resources
$ResourcesDest = "$FilesDir\Resources"
New-Item -ItemType Directory -Path $ResourcesDest -Force | Out-Null

$PsdSource = "$ProjectDir\Resources\template.psd"
if (Test-Path $PsdSource) {
    Copy-Item $PsdSource -Destination $ResourcesDest
    Write-Host "      template.psd copié ✓"
} else {
    Write-Host "ATTENTION : template.psd non trouvé dans Resources\" -ForegroundColor Yellow
    Write-Host "            Copiez-le manuellement dans Setup\files\Resources\" -ForegroundColor Yellow
}

$FileCount = (Get-ChildItem $FilesDir -Recurse -File).Count
Write-Host ""
Write-Host "[2/3] $FileCount fichiers copiés ✓" -ForegroundColor Green

# Étape 3 : Compiler le setup NSIS (si installé)
Write-Host ""
Write-Host "[3/3] Compilation de l'installeur NSIS..." -ForegroundColor Yellow

$NsisMakensis = @(
    "C:\Program Files (x86)\NSIS\makensis.exe",
    "C:\Program Files\NSIS\makensis.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($NsisMakensis) {
    Set-Location $SetupDir
    & $NsisMakensis "installer.nsi"
    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "[3/3] Installeur créé ✓" -ForegroundColor Green
        $SetupExe = Get-ChildItem $SetupDir -Filter "*Setup*.exe" | Select-Object -First 1
        if ($SetupExe) {
            Write-Host ""
            Write-Host "=== RÉSULTAT ===" -ForegroundColor Cyan
            Write-Host "Installeur : $($SetupExe.FullName)" -ForegroundColor White
            Write-Host "Taille     : $([math]::Round($SetupExe.Length/1MB, 1)) MB" -ForegroundColor White
        }
    }
} else {
    Write-Host ""
    Write-Host "[3/3] NSIS non installé — ignoré" -ForegroundColor Yellow
    Write-Host "      Pour créer l'installeur :" -ForegroundColor Gray
    Write-Host "      1. Télécharge NSIS : https://nsis.sourceforge.io/Download" -ForegroundColor Gray
    Write-Host "      2. Relance ce script" -ForegroundColor Gray
    Write-Host ""
    Write-Host "=== RÉSULTAT PARTIEL ===" -ForegroundColor Cyan
    Write-Host "Dossier prêt : $FilesDir" -ForegroundColor White
    Write-Host "Contenu (distributable sans installeur) :"
    Get-ChildItem $FilesDir | ForEach-Object { Write-Host "  - $($_.Name)" }
}

Write-Host ""
Write-Host "Terminé !" -ForegroundColor Green
Write-Host ""
