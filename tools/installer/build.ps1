<#
.SYNOPSIS
    Construye el instalador de Windows de EditFlow.

.DESCRIPTION
    Hace las tres cosas por orden y falla en cuanto una no sale:

      1. Comprueba que FFmpeg esta descargado (tools/ffmpeg/win-x64).
      2. Publica la aplicacion self-contained para win-x64.
      3. Llama a ISCC (Inno Setup) para empaquetarlo todo en un unico .exe.

    El resultado queda en artifacts/installer/.

.PARAMETER Version
    Version que se escribe en el instalador y en el nombre del archivo. Si no se
    indica, se toma del ultimo tag de git (v0.6.0 -> 0.6.0).

.PARAMETER SkipPublish
    Reutiliza lo que ya haya en artifacts/publish/win-x64. Util para iterar sobre
    el .iss sin esperar a que se vuelva a publicar.

.EXAMPLE
    tools\installer\build.cmd
    tools\installer\build.cmd 0.6.0
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $Version,

    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'

function Write-Step { param([string] $m) Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok   { param([string] $m) Write-Host "    $m" -ForegroundColor Green }

$installerDir = Split-Path -Parent $PSCommandPath
$repo = Resolve-Path (Join-Path $installerDir '..\..')

# --- Version ------------------------------------------------------------------
if (-not $Version) {
    $tag = & git -C $repo describe --tags --abbrev=0 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $tag) {
        throw "No hay ningun tag del que tomar la version. Indicala: build.cmd 0.6.0"
    }
    $Version = $tag -replace '^v', ''
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "La version debe ser MAJOR.MINOR.PATCH (recibido: '$Version')."
}

Write-Step "EditFlow $Version"

# --- FFmpeg -------------------------------------------------------------------
$ffmpegDir = Join-Path $repo 'tools\ffmpeg\win-x64'
if (-not (Test-Path (Join-Path $ffmpegDir 'ffmpeg.exe'))) {
    throw @"
Falta FFmpeg en tools\ffmpeg\win-x64.

Los binarios no se versionan en git; descargalos primero:

    tools\fetch-ffmpeg.cmd
"@
}

$ffmpegMb = [math]::Round((Get-ChildItem $ffmpegDir -File | Measure-Object Length -Sum).Sum / 1MB)
Write-Ok "FFmpeg listo ($ffmpegMb MB)"

# --- Publicacion --------------------------------------------------------------
$publishDir = Join-Path $repo 'artifacts\publish\win-x64'

if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $publishDir 'EditFlow.exe'))) {
        throw "-SkipPublish pero no hay nada publicado en $publishDir."
    }
    Write-Ok 'Reutilizando la publicacion anterior (-SkipPublish).'
}
else {
    Write-Step 'Publicando la aplicacion (self-contained, win-x64)'
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    # Con recorte: 47 MB en vez de 104. Fue un camino con trampa, y conviene dejarlo
    # escrito: el recorte desactiva la serializacion JSON por reflexion, y la aplicacion
    # publicada moria al arrancar con un InvalidOperationException que no aparecia ni al
    # compilar ni en los tests. Se arreglo pasando a contextos generados en compilacion
    # (SettingsJson, RecentProjectsJson, TranslationJson). Si alguien vuelve a usar
    # JsonSerializer por reflexion en algun sitio, esto volvera a romperse igual de
    # silenciosamente: la unica red es publicar y abrir la aplicacion.
    & dotnet publish (Join-Path $repo 'src\EditFlow.App') `
        -c Release -r win-x64 --self-contained `
        -p:PublishTrimmed=true `
        -p:Version=$Version `
        -o $publishDir

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish fallo (codigo $LASTEXITCODE)." }
}

$publishMb = [math]::Round((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
Write-Ok "aplicacion publicada ($publishMb MB)"

# --- Inno Setup ---------------------------------------------------------------
Write-Step 'Buscando Inno Setup'

# Se mira tambien en %LOCALAPPDATA%\Programs: winget instala Inno Setup por usuario
# cuando no hay permisos de administrador, que es el caso habitual.
$isccCandidates = @()
foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, "$env:LOCALAPPDATA\Programs")) {
    if (-not $root) { continue }
    foreach ($v in @('Inno Setup 6', 'Inno Setup 7')) {
        $isccCandidates += (Join-Path (Join-Path $root $v) 'ISCC.exe')
    }
}

$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

# Y si alguien lo tiene en el PATH, tambien vale.
if (-not $iscc) {
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { $iscc = $onPath.Source }
}

if (-not $iscc) {
    throw @"
No se encontro ISCC.exe (el compilador de Inno Setup).

Instalalo con:

    winget install --id JRSoftware.InnoSetup --accept-package-agreements --accept-source-agreements
"@
}

Write-Ok $iscc

$outputDir = Join-Path $repo 'artifacts\installer'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

Write-Step 'Empaquetando'
& $iscc `
    "/DAppVersion=$Version" `
    "/DPublishDir=$publishDir" `
    "/DFFmpegDir=$ffmpegDir" `
    "/DOutputDir=$outputDir" `
    (Join-Path $installerDir 'EditFlow.iss')

if ($LASTEXITCODE -ne 0) { throw "ISCC fallo (codigo $LASTEXITCODE)." }

$setup = Join-Path $outputDir "EditFlow-$Version-win-x64-setup.exe"
$setupMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)

Write-Host ''
Write-Ok "Listo: $setup"
Write-Ok "$setupMb MB (instala $($publishMb + $ffmpegMb) MB)"
Write-Host ''
Write-Host '    El instalador NO esta firmado: Windows SmartScreen avisara al abrirlo, y' -ForegroundColor Yellow
Write-Host '    un equipo con Smart App Control activado lo bloqueara sin dar opcion.' -ForegroundColor Yellow
