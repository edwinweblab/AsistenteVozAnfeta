param(
    [string]$AppPackagesPath = "C:\Users\nanoc\source\repos\AsistenteVozAnfeta\App1\AppPackages",
    [string]$Repository = "neftaliweblab/anfeta-updates",
    [string]$ReleaseNotes = "",
    [switch]$Publish,
    [switch]$OpenRelease
)

$ErrorActionPreference = "Stop"

function Fail([string]$Message) { throw $Message }
function Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

Step "Validando GitHub CLI"

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail "No se encontró GitHub CLI. Instálalo con: winget install --id GitHub.cli"
}

& gh auth status 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
    Fail "GitHub CLI no está autenticado. Ejecuta: gh auth login"
}

if (-not (Test-Path -LiteralPath $AppPackagesPath)) {
    Fail "No existe AppPackages: $AppPackagesPath"
}

Step "Buscando el paquete x64 más reciente"

$packageFolder = Get-ChildItem -LiteralPath $AppPackagesPath -Directory |
    Where-Object { $_.Name -match '^Anfeta\.UI_(\d+\.\d+\.\d+\.\d+)_x64_Test$' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $packageFolder) {
    Fail "No se encontró una carpeta Anfeta.UI_<versión>_x64_Test."
}

$version = [regex]::Match(
    $packageFolder.Name,
    '^Anfeta\.UI_(\d+\.\d+\.\d+\.\d+)_x64_Test$'
).Groups[1].Value

$tag = "v$version"
$releaseTitle = "ANFETA v$version"
$msixName = "Anfeta.UI_${version}_x64.msix"
$msixPath = Join-Path $packageFolder.FullName $msixName
$runtimePath = Join-Path $packageFolder.FullName "Dependencies\x64\Microsoft.WindowsAppRuntime.1.8.msix"

if (-not (Test-Path -LiteralPath $msixPath)) {
    Fail "No se encontró: $msixPath"
}

if (-not (Test-Path -LiteralPath $runtimePath)) {
    Fail "No se encontró Microsoft.WindowsAppRuntime.1.8.msix."
}

Write-Host "Versión detectada: $version" -ForegroundColor Green

Step "Validando identidad y versión del paquete"

$manifestPath = Join-Path $packageFolder.FullName "AppxManifest.xml"
if (Test-Path -LiteralPath $manifestPath) {
    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
    $identity = $manifest.Package.Identity

    if ($identity.Name -ne "c6d297e3-90b3-45d7-8116-b599de47cc6a") {
        Fail "La identidad del paquete no coincide con ANFETA."
    }

    if ($identity.Publisher -ne "CN=nanoc") {
        Fail "El publicador del paquete no coincide con CN=nanoc."
    }

    if ($identity.Version -ne $version) {
        Fail "La versión del manifiesto ($($identity.Version)) no coincide con la carpeta ($version)."
    }
}

Step "Generando ANFETA.appinstaller"

$appInstallerPath = Join-Path $AppPackagesPath "ANFETA.appinstaller"

$appInstallerXml = @"
<?xml version="1.0" encoding="utf-8"?>

<AppInstaller
    Uri="https://github.com/$Repository/releases/latest/download/ANFETA.appinstaller"
    Version="$version"
    xmlns="http://schemas.microsoft.com/appx/appinstaller/2017/2">

    <MainPackage
        Name="c6d297e3-90b3-45d7-8116-b599de47cc6a"
        Version="$version"
        Publisher="CN=nanoc"
        Uri="https://github.com/$Repository/releases/latest/download/$msixName"
        ProcessorArchitecture="x64" />

    <Dependencies>
        <Package
            Name="Microsoft.WindowsAppRuntime.1.8"
            Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
            ProcessorArchitecture="x64"
            Uri="https://github.com/$Repository/releases/latest/download/Microsoft.WindowsAppRuntime.1.8.msix"
            Version="8000.675.1142.0" />
    </Dependencies>

    <UpdateSettings>
        <OnLaunch HoursBetweenUpdateChecks="0" />
    </UpdateSettings>

</AppInstaller>
"@

Set-Content -LiteralPath $appInstallerPath -Value $appInstallerXml -Encoding utf8

Step "Preparando notas"

if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    $ReleaseNotes = Join-Path $AppPackagesPath "release-notes-$version.md"

    if (-not (Test-Path -LiteralPath $ReleaseNotes)) {
        @"
## ANFETA $tag

### Cambios
- Actualización de ANFETA.
- Mejoras y correcciones generales.
"@ | Set-Content -LiteralPath $ReleaseNotes -Encoding utf8
    }
}

if (-not (Test-Path -LiteralPath $ReleaseNotes)) {
    Fail "No existe el archivo de notas: $ReleaseNotes"
}

Step "Validando la Release"

# GitHub CLI devuelve código 1 cuando la Release no existe.
# Con $ErrorActionPreference = "Stop", PowerShell puede interpretar
# el texto de stderr como un error fatal antes de revisar $LASTEXITCODE.
# Ejecutarlo mediante cmd.exe evita ese falso bloqueo.
$checkCommand = 'gh release view "{0}" --repo "{1}" >nul 2>nul' -f $tag, $Repository
& cmd.exe /d /c $checkCommand
$releaseCheckExitCode = $LASTEXITCODE

if ($releaseCheckExitCode -eq 0) {
    Fail "La Release $tag ya existe. Incrementa la versión."
}

if ($releaseCheckExitCode -ne 1) {
    Fail "No se pudo validar la Release en GitHub (código $releaseCheckExitCode). Revisa tu conexión y ejecuta: gh auth status"
}

Write-Host ""
Write-Host "Repositorio: $Repository"
Write-Host "Tag: $tag"
Write-Host "MSIX: $msixPath"
Write-Host "AppInstaller: $appInstallerPath"
Write-Host "Runtime: $runtimePath"
Write-Host "Notas: $ReleaseNotes"

if (-not $Publish) {
    Write-Host ""
    Write-Host "Modo de prueba. No se publicó nada." -ForegroundColor Yellow
    Write-Host "Para publicar ejecuta:" -ForegroundColor Yellow
    Write-Host "powershell -ExecutionPolicy Bypass -File .\Publicar-Anfeta.ps1 -Publish"
    exit 0
}

Step "Creando Release"

& gh release create $tag `
    --repo $Repository `
    --title $releaseTitle `
    --notes-file $ReleaseNotes `
    --latest `
    $appInstallerPath `
    $msixPath `
    $runtimePath

if ($LASTEXITCODE -ne 0) {
    Fail "No se pudo crear la Release."
}

Write-Host ""
Write-Host "Release publicada correctamente:" -ForegroundColor Green
$releaseUrl = "https://github.com/$Repository/releases/tag/$tag"
Write-Host $releaseUrl -ForegroundColor Green
Write-Host ""
Write-Host "Enlace estable de instalación/actualización:" -ForegroundColor Cyan
Write-Host "https://github.com/$Repository/releases/latest/download/ANFETA.appinstaller" -ForegroundColor Cyan

if ($OpenRelease) {
    Start-Process $releaseUrl
}
