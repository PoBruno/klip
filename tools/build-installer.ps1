# Gera o INSTALADOR completo do Klip (Inno Setup).
# 1) publica o .exe self-contained  2) compila o instalador .exe
#
# Saida: dist\Klip-Setup-<versao>.exe
#
# Requisitos: Inno Setup 6 instalado (winget install JRSoftware.InnoSetup)
# Uso:  .\tools\build-installer.ps1  (roda de qualquer lugar)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent  # tools/ fica um nivel abaixo da raiz

# 1. Publica o executavel standalone
Write-Host "[1/2] Publicando o executavel..." -ForegroundColor Cyan
& "$PSScriptRoot\build-exe.ps1"

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
Write-Host "[2/2] Compilando o instalador com Inno Setup..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "$repoRoot\dist" | Out-Null
& $iscc "$repoRoot\installer\Klip.iss"
if ($LASTEXITCODE -ne 0) { throw "Falha na compilacao do instalador (codigo $LASTEXITCODE)." }

$setup = Get-ChildItem "$repoRoot\dist\Klip-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ""
Write-Host "Instalador pronto: $($setup.FullName)" -ForegroundColor Green
Write-Host ("Tamanho: {0} MB" -f [math]::Round($setup.Length/1MB,1))
