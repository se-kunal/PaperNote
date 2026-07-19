# PaperNote — one-click build.
# Run this and it builds the editor, publishes the app, and produces the installer.
#   Right-click -> "Run with PowerShell", or from a terminal:  ./build.ps1
# Output: Publish\PaperNote-Setup-<version>.exe

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publishDir = Join-Path $root '_temp\publish'
$outDir     = Join-Path $root 'Publish'

function Step($n, $msg) { Write-Host "`n[$n/4] $msg" -ForegroundColor Green }

# Locate the Inno Setup compiler (default install path).
$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) {
    throw "Inno Setup compiler not found at $iscc. Install Inno Setup 6, then re-run."
}

# 1. Editor web bundle (TipTap -> dist).
Step 1 'Building editor bundle...'
Push-Location (Join-Path $root 'app\editor-web')
if (-not (Test-Path 'node_modules')) { npm install }
npm run build
Pop-Location

# 2. Publish the WPF app, self-contained (no .NET needed on the target machine).
Step 2 'Publishing app (self-contained)...'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $root 'app\PaperNote\PaperNote.csproj') `
    -c Release -r win-x64 --self-contained true -o $publishDir

# 3. Build the installer.
Step 3 'Building installer...'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
& $iscc "/DAppSrc=$publishDir" "/DOutDir=$outDir" (Join-Path $root 'app\installer\PaperNote.iss')

# 4. Done.
Step 4 'Done.'
$exe = Get-ChildItem $outDir -Filter 'PaperNote-Setup-*.exe' | Sort-Object LastWriteTime | Select-Object -Last 1
Write-Host "`nInstaller ready: $($exe.FullName)" -ForegroundColor Cyan
explorer.exe "/select,`"$($exe.FullName)`""
