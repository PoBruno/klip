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

# ADR-P.09: flags de publish. Trade-off central = tamanho de download (uma vez)
# contra tempo de startup (TODO boot, porque o Klip roda com a chave Run).
#
#   PublishReadyToRun=true
#       Pre-compila Klip + WPF-UI + CommunityToolkit.Mvvm para codigo nativo (as libs
#       do framework ja vem R2R de fabrica). Corta o JIT do caminho de startup. Custo:
#       o publish fica lento e o exe cresce; ambos pagos uma vez, na maquina de build.
#
#   PublishSingleFile=true
#       Um unico arquivo. E o que faz o artefato "portable" da release existir e o que
#       permite o instalador copiar so Klip.exe (installer\Klip.iss, secao [Files]).
#
#   EnableCompressionInSingleFile=false   <-- MUDANCA
#       Antes era true. Com compressao o host precisa DESCOMPRIMIR (Brotli) dezenas de
#       MB de assemblies para a memoria a CADA start, em vez de mapear o bundle direto
#       do disco. Como o app sobe junto com o Windows, esse custo era cobrado em todo
#       logon, competindo com o resto da inicializacao - e ainda anulava boa parte do
#       ganho do R2R acima. Desligar troca ~algumas dezenas de MB de download (pagos
#       uma vez, e o instalador Inno recomprime tudo com LZMA2/max) por um startup
#       consistentemente mais rapido.
#
#   IncludeNativeLibrariesForSelfExtract=true   <-- MANTIDO DE PROPOSITO
#       Sim, isso extrai as libs nativas para %TEMP%\.net no PRIMEIRO start (depois o
#       host so confere se a pasta existe). Mesmo assim fica: com false o publish emite
#       Klip.exe MAIS 6 DLLs nativas soltas ao lado (wpfgfx_cor3, PresentationNative_cor3,
#       D3DCompiler_47_cor3, PenImc_cor3, vcruntime140_cor3 e e_sqlite3), e nesse cenario
#       Klip.exe sozinho NAO roda. Isso quebraria os dois consumidores desta pasta, que
#       copiam apenas o exe: installer\Klip.iss (secao [Files]) e o artefato portable da
#       release. O custo aqui e uma extracao unica; o da compressao era por start.
#
#   PublishTrimmed=false
#       Obrigatorio: WPF nao suporta trimming (o SDK falha com TrimmingWpfIsNotSupported).
dotnet publish "$repoRoot\src\Klip.App" -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o $publishDir

# drop debug symbols from the final folder
Remove-Item "$publishDir\*.pdb" -ErrorAction SilentlyContinue

$exe = Get-Item "$publishDir\Klip.exe"
Write-Host ""
Write-Host "Pronto: $($exe.FullName)" -ForegroundColor Green
Write-Host ("Tamanho: {0} MB  |  Versao: {1}" -f [math]::Round($exe.Length/1MB,1), $exe.VersionInfo.FileVersion)
Write-Host "Rode com duplo clique ou copie o .exe para qualquer lugar."
