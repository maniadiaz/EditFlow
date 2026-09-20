<#
.SYNOPSIS
    Descarga FFmpeg y ffprobe, verificando su integridad contra tools/ffmpeg.lock.json.

.DESCRIPTION
    Los binarios de FFmpeg no se versionan en git (pesan ~90 MB). Este script los
    obtiene y los coloca en tools/ffmpeg/<rid>/, desde donde los proyectos los
    copian a la salida de compilacion.

    La verificacion se hace contra el hash guardado en el lockfile, que si esta
    versionado. Si el hash no coincide, el script falla y NO deja nada instalado.

.PARAMETER Platform
    RID objetivo: win-x64 o linux-x64. Por defecto se detecta automaticamente.

.PARAMETER Force
    Vuelve a descargar aunque los binarios ya existan.

.PARAMETER Update
    Consulta los checksums publicados por el proveedor y reescribe el lockfile.
    Accion deliberada: produce un diff que debe revisarse antes de commitear.

.EXAMPLE
    pwsh tools/fetch-ffmpeg.ps1
    pwsh tools/fetch-ffmpeg.ps1 -Update
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64')]
    [string] $Platform,

    [switch] $Force,
    [switch] $Update
)

$ErrorActionPreference = 'Stop'

$toolsDir = Split-Path -Parent $PSCommandPath
$lockPath = Join-Path $toolsDir 'ffmpeg.lock.json'

function Write-Step { param([string] $Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Message) Write-Host "    $Message" -ForegroundColor Green }

# --- Deteccion de plataforma --------------------------------------------------
if (-not $Platform) {
    if ($IsLinux)     { $Platform = 'linux-x64' }
    elseif ($IsMacOS) { throw "macOS todavia no esta soportado: el proveedor de builds (BtbN) no publica binarios de macOS. Ver la nota '_macos' en ffmpeg.lock.json." }
    else              { $Platform = 'win-x64' }
}

if (-not (Test-Path $lockPath)) { throw "No se encontro el lockfile: $lockPath" }
$lock = Get-Content $lockPath -Raw | ConvertFrom-Json

$platformNames = $lock.platforms.PSObject.Properties.Name
if ($platformNames -notcontains $Platform) {
    throw "El lockfile no define la plataforma '$Platform'."
}
$entry = $lock.platforms.$Platform

# --- Modo -Update: refrescar el lockfile desde los checksums publicados --------
if ($Update) {
    Write-Step "Consultando checksums publicados por $($lock.source)"
    $checksumsUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256'
    $checksums = (Invoke-WebRequest -Uri $checksumsUrl -UseBasicParsing).Content -split "`n"

    $changed = $false
    foreach ($name in $platformNames) {
        $p = $lock.platforms.$name
        $fileName = Split-Path -Leaf $p.url
        $line = $checksums | Where-Object { $_ -match ("\s" + [regex]::Escape($fileName) + "\s*$") } | Select-Object -First 1

        if (-not $line) {
            Write-Warning "No se encontro checksum publicado para $fileName"
            continue
        }

        $published = ($line -split '\s+')[0]
        if ($published -ne $p.sha256) {
            Write-Host "    $name" -ForegroundColor Yellow
            Write-Host "      anterior: $($p.sha256)" -ForegroundColor DarkGray
            Write-Host "      nuevo   : $published" -ForegroundColor Yellow
            $p.sha256 = $published
            $changed = $true
        }
        else {
            Write-Ok "$name sin cambios"
        }
    }

    if ($changed) {
        $lock.updated = (Get-Date -Format 'yyyy-MM-dd')
        $lock | ConvertTo-Json -Depth 10 | Set-Content $lockPath -Encoding utf8
        Write-Host ''
        Write-Host 'Lockfile actualizado. Revisa el diff antes de commitear:' -ForegroundColor Yellow
        Write-Host '    git diff tools/ffmpeg.lock.json' -ForegroundColor Yellow
    }
    else {
        Write-Ok 'El lockfile ya estaba al dia.'
    }
    return
}

# --- Ya esta instalado? -------------------------------------------------------
$targetDir = Join-Path (Join-Path $toolsDir 'ffmpeg') $Platform
$allPresent = $true
foreach ($b in $entry.binaries) {
    if (-not (Test-Path (Join-Path $targetDir $b))) { $allPresent = $false }
}

if ($allPresent -and -not $Force) {
    Write-Ok "FFmpeg ya esta en $targetDir (usa -Force para reinstalar)."
    & (Join-Path $targetDir $entry.binaries[0]) -hide_banner -version | Select-Object -First 1
    return
}

# --- Descarga -----------------------------------------------------------------
$fileName = Split-Path -Leaf $entry.url
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("editflow-ffmpeg-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    $archive = Join-Path $temp $fileName
    Write-Step "Descargando $fileName"
    Write-Host "    $($entry.url)" -ForegroundColor DarkGray

    $previousProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'   # acelera enormemente Invoke-WebRequest
    try   { Invoke-WebRequest -Uri $entry.url -OutFile $archive -UseBasicParsing }
    finally { $ProgressPreference = $previousProgress }

    $sizeMb = [math]::Round((Get-Item $archive).Length / 1MB, 1)
    Write-Ok "descargados $sizeMb MB"

    # --- Verificacion ---------------------------------------------------------
    Write-Step 'Verificando SHA-256 contra el lockfile'
    $actual = (Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $wanted = ([string]$entry.sha256).ToLowerInvariant()

    if ($actual -ne $wanted) {
        $msg = @"
VERIFICACION FALLIDA - no se instalo nada.

  esperado : $wanted
  obtenido : $actual

Las builds 'latest' de BtbN se reconstruyen periodicamente, asi que lo mas
probable es que el artefacto upstream se haya regenerado. Si es el caso:

    pwsh tools/fetch-ffmpeg.ps1 -Update

y revisa el diff del lockfile antes de commitearlo. Si NO esperabas un cambio
upstream, no continues: investiga primero.
"@
        throw $msg
    }
    Write-Ok "hash correcto: $actual"

    # --- Extraccion -----------------------------------------------------------
    Write-Step "Extrayendo a tools/ffmpeg/$Platform"
    $extract = Join-Path $temp 'x'
    New-Item -ItemType Directory -Path $extract | Out-Null

    if ($fileName.EndsWith('.zip')) {
        Expand-Archive -Path $archive -DestinationPath $extract -Force
    }
    else {
        & tar -xf $archive -C $extract
        if ($LASTEXITCODE -ne 0) { throw "tar fallo al extraer $fileName (codigo $LASTEXITCODE)" }
    }

    $rootDir = Join-Path $extract $entry.archiveRoot
    $binDir  = Join-Path $rootDir 'bin'
    if (-not (Test-Path $binDir)) { throw "No se encontro el directorio bin/ esperado en el archivo: $binDir" }

    if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force }
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

    foreach ($b in $entry.binaries) {
        $src = Join-Path $binDir $b
        if (-not (Test-Path $src)) { throw "El archivo descargado no contiene '$b'." }
        Copy-Item $src -Destination $targetDir
        if ($IsLinux -or $IsMacOS) { & chmod +x (Join-Path $targetDir $b) }
        Write-Ok $b
    }

    # La licencia del binario viaja junto a el.
    $licenseSrc = Join-Path $rootDir 'LICENSE.txt'
    if (Test-Path $licenseSrc) { Copy-Item $licenseSrc -Destination $targetDir }

    Write-Host ''
    & (Join-Path $targetDir $entry.binaries[0]) -hide_banner -version | Select-Object -First 1
    Write-Host ''
    Write-Ok "Listo. Variante '$($lock.variant)' del release $($lock.release)."
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
