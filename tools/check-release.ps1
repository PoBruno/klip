# Runs everything the github workflows run, on this machine, before a tag goes out.
# If this passes, the release workflow builds the same thing; it is also what the
# workflows themselves call, so the two can not drift apart.
#
# Usage:
#   .\tools\check-release.ps1 -Version 1.6.0      full check before tagging v1.6.0
#   .\tools\check-release.ps1 -SkipInstaller      build + tests only (what CI runs)
#
# Checks, in order:
#   1. version in Klip.App.csproj and installer\Klip.iss (and -Version, when given)
#   2. .NET SDK and Inno Setup versions match the ones pinned in the workflows
#   3. actionlint over .github\workflows
#   4. restore, Release build and tests
#   5. installer + portable, and the version stamped in the exe
#   6. the last CI run on github: a billing lock there fails every job in seconds

param(
    [string]$Version,
    [switch]$SkipInstaller,
    [switch]$SkipGitHub
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$failures = [System.Collections.Generic.List[string]]::new()

function Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "   ok  $text" -ForegroundColor Green }
function Fail($text) { Write-Host "   ERRO  $text" -ForegroundColor Red; $failures.Add($text) }
function Run([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what falhou (codigo $LASTEXITCODE)." }
}

# pinned values live in the release workflow, the ci workflow must agree with it
$releaseYml = Get-Content "$repoRoot\.github\workflows\release.yml" -Raw
$ciYml = Get-Content "$repoRoot\.github\workflows\ci.yml" -Raw
function Pinned($yml, $key) { if ($yml -match "(?m)^\s*$($key):\s*`"?([^`"\s]+)") { $Matches[1] } }

# 1. versao
Step "versao"
$csproj = Get-Content "$repoRoot\src\Klip.App\Klip.App.csproj" -Raw
$iss = Get-Content "$repoRoot\installer\Klip.iss" -Raw
$csprojVersion = if ($csproj -match '<Version>([^<]+)</Version>') { $Matches[1] }
$issVersion = if ($iss -match '#define AppVersion "([^"]+)"') { $Matches[1] }
if (-not $Version) { $Version = $csprojVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "versao '$Version' nao e X.Y.Z" }
if ($csprojVersion -ne $Version) { Fail "Klip.App.csproj tem $csprojVersion, esperado $Version" } else { Ok "Klip.App.csproj $csprojVersion" }
$fileVersion = if ($csproj -match '<FileVersion>([^<]+)</FileVersion>') { $Matches[1] }
if ($fileVersion -and $fileVersion -ne "$Version.0") { Fail "FileVersion do csproj e $fileVersion, esperado $Version.0" }
if ($issVersion -ne $Version) { Fail "Klip.iss tem $issVersion, esperado $Version" } else { Ok "Klip.iss $issVersion" }

# 2. ferramentas
Step "ferramentas"
$sdkPinned = Pinned $releaseYml "dotnet-version"
$sdkCi = Pinned $ciYml "dotnet-version"
$sdkLocal = (dotnet --version).Trim()
if ($sdkPinned -ne $sdkCi) { Fail "release.yml usa o SDK $sdkPinned e ci.yml usa $sdkCi" }
if ($sdkLocal -ne $sdkPinned) { Fail ".NET SDK local $sdkLocal, workflows usam $sdkPinned" } else { Ok ".NET SDK $sdkLocal" }

if (-not $SkipInstaller) {
    $innoPinned = Pinned $releaseYml "INNO_VERSION"
    $innoLocal = Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*, `
        HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*, `
        HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\* -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like "Inno Setup*" } | Select-Object -ExpandProperty DisplayVersion -First 1
    if ($innoLocal -ne $innoPinned) { Fail "Inno Setup local $innoLocal, release.yml usa $innoPinned" } else { Ok "Inno Setup $innoLocal" }
}

# 3. actionlint (pinned + hash checked, cached outside the repo)
Step "workflows (actionlint)"
$alVersion = "1.7.12"
$alHash = "6e7241b51e6817ea6a047693d8e6fed13b31819c9a0dd6c5a726e1592d22f6e9"
$alDir = Join-Path ([IO.Path]::GetTempPath()) "klip-tools\actionlint-$alVersion"
$alExe = Join-Path $alDir "actionlint.exe"
if (-not (Test-Path $alExe)) {
    New-Item -ItemType Directory -Force -Path $alDir | Out-Null
    $zip = Join-Path $alDir "actionlint.zip"
    Invoke-WebRequest "https://github.com/rhysd/actionlint/releases/download/v$alVersion/actionlint_${alVersion}_windows_amd64.zip" -OutFile $zip
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $alHash) { Remove-Item $zip; throw "hash do actionlint nao confere" }
    Expand-Archive $zip -DestinationPath $alDir -Force
}
Push-Location $repoRoot
try {
    & $alExe -shellcheck= -pyflakes=
    if ($LASTEXITCODE -ne 0) { Fail "actionlint achou problemas nos workflows" } else { Ok "workflows" }
} finally { Pop-Location }

# 4. build e testes, iguais ao CI
Step "build e testes (Release)"
Push-Location $repoRoot
try {
    Run "dotnet restore" { dotnet restore Klip.sln }
    Run "dotnet build" { dotnet build Klip.sln -c Release --no-restore }
    Run "dotnet test" { dotnet test Klip.sln -c Release --no-build }
    Ok "build e testes"
} catch { Fail $_.Exception.Message } finally { Pop-Location }

# 5. instalador e portable
if (-not $SkipInstaller -and $failures.Count -eq 0) {
    Step "instalador e portable"
    try {
        & "$PSScriptRoot\build-installer.ps1" -Version $Version
        foreach ($name in "Klip-Setup-$Version.exe", "Klip-$Version-portable.exe") {
            if (Test-Path "$repoRoot\dist\$name") { Ok $name } else { Fail "faltou dist\$name" }
        }
        $stamped = (Get-Item "$repoRoot\publish\Klip.exe").VersionInfo.ProductVersion -replace '\+.*$', ''
        if ($stamped -ne $Version) { Fail "Klip.exe saiu com a versao $stamped" } else { Ok "Klip.exe $stamped" }
        $fileStamped = (Get-Item "$repoRoot\publish\Klip.exe").VersionInfo.FileVersion
        if ($fileStamped -ne "$Version.0") { Fail "Klip.exe saiu com FileVersion $fileStamped" }
    } catch { Fail $_.Exception.Message }
}

# 6. github: does the account run jobs at all?
if (-not $SkipGitHub) {
    Step "github actions"
    $last = gh run list -R PoBruno/klip --workflow CI --limit 1 --json databaseId,conclusion,headBranch | ConvertFrom-Json
    if (-not $last) {
        Fail "nao consegui ler as execucoes (gh instalado e logado?)"
    } elseif ($last[0].conclusion -eq "success") {
        Ok "ultima CI passou ($($last[0].headBranch))"
    } else {
        $view = gh run view $last[0].databaseId -R PoBruno/klip 2>&1 | Out-String
        if ($view -match "billing") {
            Fail "a conta do github esta bloqueada por cobranca: nenhum job roda (github.com/settings/billing)"
        } else {
            Fail "ultima CI terminou como '$($last[0].conclusion)': gh run view $($last[0].databaseId) -R PoBruno/klip"
        }
    }
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "NAO PRONTO ($($failures.Count) problema(s)):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "Pronto para a tag v$Version." -ForegroundColor Green
