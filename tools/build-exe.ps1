# Builds the standalone Klip exe.
# Output: publish\Klip.exe (single file, self-contained, no .NET install needed).
#
# Usage:  .\tools\build-exe.ps1  (roda de qualquer lugar)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent  # tools/ fica um nivel abaixo da raiz

# kill running instances first, senao o build nao consegue sobrescrever o exe travado
Get-Process Klip -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

$publishDir = Join-Path $repoRoot "publish"
Write-Host "Publicando Klip (self-contained, single-file)..." -ForegroundColor Cyan
dotnet publish "$repoRoot\src\Klip.App" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o $publishDir

# drop debug symbols from the final folder
Remove-Item "$publishDir\*.pdb" -ErrorAction SilentlyContinue

$exe = Get-Item "$publishDir\Klip.exe"
Write-Host ""
Write-Host "Pronto: $($exe.FullName)" -ForegroundColor Green
Write-Host ("Tamanho: {0} MB  |  Versao: {1}" -f [math]::Round($exe.Length/1MB,1), $exe.VersionInfo.FileVersion)
Write-Host "Rode com duplo clique ou copie o .exe para qualquer lugar."
