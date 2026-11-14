# Gera o INSTALADOR completo do Klip (Inno Setup) e o exe portable.
# 1) publica o .exe self-contained  2) compila o instalador  3) copia o portable
#
# Saida: dist\Klip-Setup-<versao>.exe e dist\Klip-<versao>-portable.exe
#
# Requisitos: Inno Setup 6 instalado (winget install JRSoftware.InnoSetup)
# Uso:  .\tools\build-installer.ps1 [-Version 1.6.0]  (roda de qualquer lugar)
# The release workflow calls this same script, so a local run builds exactly what ships.

param([string]$Version)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent  # tools/ fica um nivel abaixo da raiz

if (-not $Version) {
    $csproj = Get-Content "$repoRoot\src\Klip.App\Klip.App.csproj" -Raw
    if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw "Versao nao encontrada em Klip.App.csproj." }
    $Version = $Matches[1]
}

# 1. Publica o executavel standalone
Write-Host "[1/3] Publicando o executavel $Version..." -ForegroundColor Cyan
& "$PSScriptRoot\build-exe.ps1" -Version $Version

# 2. Localiza o compilador do Inno Setup
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup nao encontrado. Instale com: winget install JRSoftware.InnoSetup"
}

Write-Host ""
Write-Host "[2/3] Compilando o instalador com Inno Setup..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "$repoRoot\dist" | Out-Null
& $iscc "/DAppVersion=$Version" "$repoRoot\installer\Klip.iss"
if ($LASTEXITCODE -ne 0) { throw "Falha na compilacao do instalador (codigo $LASTEXITCODE)." }

Write-Host ""
Write-Host "[3/3] Copiando o portable..." -ForegroundColor Cyan
$setup = Get-Item "$repoRoot\dist\Klip-Setup-$Version.exe"
$portable = "$repoRoot\dist\Klip-$Version-portable.exe"
Copy-Item "$repoRoot\publish\Klip.exe" $portable -Force

Write-Host ""
Write-Host "Instalador pronto: $($setup.FullName)" -ForegroundColor Green
Write-Host ("Tamanho: {0} MB" -f [math]::Round($setup.Length/1MB,1))
Write-Host "Portable: $portable"
