<#
.SYNOPSIS
    Comprueba que FFmpeg arranca de verdad, y si no, dice por que.

.DESCRIPTION
    Ejecuta ffmpeg y ffprobe y mira el resultado. No basta con que los archivos esten
    ahi: pueden estar completos, con el hash correcto, y aun asi no poder ejecutarse.

    Si fallan, distingue las dos causas reales en vez de dejar un codigo de error a
    secas:

      · Smart App Control (Windows 11) bloqueando binarios sin firma. Se reconoce por
        el codigo 0xC0E90002 y queda registrado en el visor de eventos.
      · Un binario roto o incompleto.

    Se puede apuntar a cualquier instalacion, no solo a la del repositorio: util para
    diagnosticar una instalacion hecha con el instalador.

.PARAMETER Path
    Carpeta que contiene ffmpeg.exe. Si se omite, se buscan por orden: la del
    repositorio (tools/ffmpeg/win-x64), la de una instalacion del instalador, y el PATH.

.EXAMPLE
    tools\check-ffmpeg.cmd
    tools\check-ffmpeg.cmd -Path "$env:LOCALAPPDATA\Programs\EditFlow\ffmpeg"
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $Path
)

$ErrorActionPreference = 'Stop'

function Write-Step { param([string] $m) Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok   { param([string] $m) Write-Host "    $m" -ForegroundColor Green }
function Write-Bad  { param([string] $m) Write-Host "    $m" -ForegroundColor Red }
function Write-Note { param([string] $m) Write-Host "    $m" -ForegroundColor Yellow }

# Ejecuta un binario y devuelve si arranco, sin lanzar pase lo que pase.
#
# Hace falta porque un bloqueo de Control de aplicaciones no deja un codigo de salida:
# PowerShell lanza ApplicationFailedException, y con ErrorActionPreference='Stop' eso
# aborta el script antes de poder explicar nada. Se reintenta porque Smart App Control
# bloquea de forma INTERMITENTE mientras consulta la reputacion del binario: un unico
# intento fallido no demuestra nada.
function Invoke-Binary {
    param(
        [Parameter(Mandatory)] [string] $Exe,
        [string[]] $Arguments = @('-hide_banner', '-version'),
        [int] $Attempts = 4
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        foreach ($attempt in 1..$Attempts) {
            $output = $null
            $code = 0

            try {
                $output = & $Exe @Arguments 2>&1
                $code = $LASTEXITCODE
            }
            catch {
                # Bloqueo de Control de aplicaciones, o el binario no es ejecutable.
                $output = $null
                $code = -1058471934   # 0xC0E90002
            }

            if ($code -eq 0 -and $output) {
                return [PSCustomObject]@{ Ok = $true; Output = $output; Code = 0 }
            }

            if ($attempt -lt $Attempts) { Start-Sleep -Seconds (2 * $attempt) }
        }

        return [PSCustomObject]@{ Ok = $false; Output = $null; Code = $code }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}


# --- Donde esta FFmpeg --------------------------------------------------------
if (-not $Path) {
    # En el repositorio esto es la raiz; instalado, es la carpeta de la aplicacion.
    $repo = Resolve-Path (Join-Path (Split-Path -Parent $PSCommandPath) '..')

    $candidates = @(
        (Join-Path $repo 'tools\ffmpeg\win-x64'),
        (Join-Path $repo 'ffmpeg'),
        "$env:LOCALAPPDATA\Programs\EditFlow\ffmpeg"
    )

    $Path = $candidates | Where-Object { Test-Path (Join-Path $_ 'ffmpeg.exe') } | Select-Object -First 1

    if (-not $Path) {
        $onPath = Get-Command 'ffmpeg.exe' -ErrorAction SilentlyContinue
        if ($onPath) { $Path = Split-Path -Parent $onPath.Source }
    }
}

$found = $false
if ($Path) {
    try { $found = Test-Path (Join-Path $Path 'ffmpeg.exe') } catch { $found = $false }
}

if (-not $found) {
    if ($Path) { Write-Bad "No se encontro ffmpeg.exe en: $Path" }
    else       { Write-Bad 'No se encontro ffmpeg.exe.' }

    Write-Host ''
    Write-Host 'Descargalo con:  tools\fetch-ffmpeg.cmd'
    Write-Host ''
    Write-Host 'O indica donde esta, entre comillas si la ruta lleva espacios:'
    Write-Host '  tools\check-ffmpeg.cmd -Path "%LOCALAPPDATA%\Programs\EditFlow\ffmpeg"'
    exit 2
}

Write-Step "Comprobando $Path"

# --- Ejecutarlos de verdad ----------------------------------------------------
#
# Con reintentos: Smart App Control bloquea de forma intermitente mientras consulta la
# reputacion del binario, asi que un unico intento fallido no demuestra nada.
$failures = @()

foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
    $exe = Join-Path $Path $name

    if (-not (Test-Path $exe)) {
        Write-Bad "$name no esta"
        $failures += [PSCustomObject]@{ Name = $name; Code = 'ausente' }
        continue
    }

    $result = Invoke-Binary -Exe $exe

    if ($result.Ok) {
        Write-Ok ("{0,-12} {1}" -f $name, ($result.Output | Select-Object -First 1))
    }
    else {
        $hex = '0x{0:X8}' -f $result.Code
        Write-Bad ("{0,-12} NO ARRANCA ({1})" -f $name, $hex)
        $failures += [PSCustomObject]@{ Name = $name; Code = $hex }
    }
}

# --- Una prueba de verdad, no solo la version ---------------------------------
if ($failures.Count -eq 0) {
    Write-Step 'Probando una codificacion real'
    $encode = Invoke-Binary -Exe (Join-Path $Path 'ffmpeg.exe') -Attempts 2 -Arguments @(
        '-hide_banner', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'testsrc2=s=320x180:d=1',
        '-c:v', 'libx264', '-preset', 'ultrafast', '-f', 'null', '-'
    )

    # Una codificacion correcta no imprime nada, asi que aqui solo cuenta el codigo.
    if ($encode.Code -eq 0) {
        Write-Ok 'codifica correctamente'
        Write-Host ''
        Write-Ok 'FFmpeg esta listo.'
        exit 0
    }

    Write-Bad ('la codificacion fallo (0x{0:X8})' -f $encode.Code)
    $failures += [PSCustomObject]@{ Name = 'codificacion'; Code = ('0x{0:X8}' -f $encode.Code) }
}

# --- Diagnostico --------------------------------------------------------------
Write-Host ''
Write-Step 'Buscando la causa'

$sac = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' `
    -Name VerifiedAndReputablePolicyState -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState

$blocks = Get-WinEvent -FilterHashtable @{
    LogName   = 'Microsoft-Windows-CodeIntegrity/Operational'
    Id        = 3077
    StartTime = (Get-Date).AddMinutes(-10)
} -ErrorAction SilentlyContinue

$blockedNames = @()
foreach ($e in $blocks) {
    if ($e.Message -match 'attempted to load \\Device\\HarddiskVolume\d+(.+?) that did not') {
        $blockedNames += Split-Path -Leaf $matches[1]
    }
}

$blockedNames = $blockedNames | Select-Object -Unique

if ($sac -eq 1 -or $blockedNames.Count -gt 0) {
    Write-Bad 'Smart App Control esta bloqueando los binarios.'
    Write-Host ''
    Write-Host "    Estado de Smart App Control : $(if ($sac -eq 1) { 'ACTIVADO' } elseif ($sac -eq 2) { 'en evaluacion' } else { "desactivado ($sac)" })"

    if ($blockedNames.Count -gt 0) {
        Write-Host "    Bloqueos en los ultimos 10 min: $($blockedNames -join ', ')"
    }

    Write-Host ''
    Write-Host '    Que es: una funcion de Windows 11 que impide ejecutar programas sin firma'
    Write-Host '    digital. No avisa pidiendo confirmacion: bloquea. Solo se activa sola en'
    Write-Host '    instalaciones LIMPIAS de Windows 11, asi que la mayoria de equipos no la tienen.'
    Write-Host ''
    Write-Note 'IMPORTANTE: desactivarla es IRREVERSIBLE.'
    Write-Host ''
    Write-Host '    Microsoft no permite volver a activarla: la unica forma es reinstalar Windows.'
    Write-Host '    No existe una lista de excepciones ni se puede desactivar "solo un rato".'
    Write-Host ''
    Write-Host '    Opciones reales:'
    Write-Host ''
    Write-Host '      1. Desactivarla, asumiendo que es para siempre:'
    Write-Host '         Seguridad de Windows > Control de aplicaciones y navegador >'
    Write-Host '         Control inteligente de aplicaciones > Desactivado'
    Write-Host ''
    Write-Host '      2. Dejarla puesta y usar EditFlow en otro equipo. El instalador funciona'
    Write-Host '         con normalidad en cualquiera que no tenga esta funcion activada.'
    Write-Host ''
    Write-Host '    Para ver los bloqueos con tus propios ojos:'
    Write-Host '      Get-WinEvent -LogName Microsoft-Windows-CodeIntegrity/Operational -MaxEvents 10'

    exit 3
}

Write-Bad 'No parece Smart App Control: el binario esta roto o incompleto.'
Write-Host ''
Write-Host '    Vuelve a descargarlo:  tools\fetch-ffmpeg.cmd -Force'
Write-Host ''
Write-Host '    Si sigue fallando, la build del proveedor puede haber salido mal. Elige otra'
Write-Host '    release en https://github.com/BtbN/FFmpeg-Builds/releases y actualiza url,'
Write-Host '    sha256 y archiveRoot en tools/ffmpeg.lock.json.'

exit 4
