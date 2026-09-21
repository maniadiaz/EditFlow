<div align="center">

# EditFlow

**Editor de video de escritorio — nativo, ligero y multiplataforma.**

[![CI](https://github.com/maniadiaz/EditFlow/actions/workflows/ci.yml/badge.svg)](https://github.com/maniadiaz/EditFlow/actions/workflows/ci.yml)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-11.3-8B44AC)](https://avaloniaui.net/)

</div>

---

## Qué es

EditFlow es un editor de video de escritorio al estilo de CapCut o Premiere, construido con **interfaz nativa** —sin HTML ni WebView de por medio— sobre un único código base que corre en Windows, Linux y macOS.

El objetivo es que sea **ligero**: arranque rápido, poca memoria y codificación acelerada por hardware cuando la máquina lo permita.

## Funcionalidades

- **Importar y unir** uno o varios videos en una timeline.
- **Cortar y seccionar** clips: dividir en el cabezal, recortar arrastrando los bordes, reordenar.
- **Exportar** en 480p, 720p, 1080p, 1440p y 4K.
- **Elegir el motor de codificación**: GPU (NVENC de NVIDIA, Quick Sync de Intel, AMF de AMD, VideoToolbox de Apple) o CPU (x264, x265, SVT-AV1).
- **Control total del bitrate**: calidad constante (CRF/CQ), VBR con bitrate objetivo, o CBR.
- **Deshacer y rehacer** en toda operación de edición.

### Hoja de ruta

| Versión | Alcance | Estado |
|---|---|---|
| `v0.1.0` | Importar · unir · cortar · previsualizar · exportar | ✅ Publicada |
| `v0.2.0` | Proyectos guardados · reproductor propio · audio multipista mezclado · copias de edición | ✅ Publicada |
| `v0.3.0` | Pantalla de inicio · editor rehecho · audio recortable con forma de onda · miniaturas · roll/slip/slide | ✅ Publicada |
| `v0.4.0` | Varias pistas de video con superposición, texto y color estilo Lumetri | 🚧 Siguiente |
| `v0.5.0` | Transiciones, velocidad y recorte con tiradores | Planificado |

El plan completo, con las decisiones de arquitectura y su justificación, está en
**[`docs/PLAN.md`](docs/PLAN.md)**. La comparación función por función con Premiere Pro,
clasificada por lo que es alcanzable y lo que no, está en
**[`docs/PARIDAD-PREMIERE.md`](docs/PARIDAD-PREMIERE.md)**.

## Stack

| Componente | Tecnología |
|---|---|
| Runtime | .NET 10 (LTS) |
| Interfaz | Avalonia 11.3.x — nativa, renderizada con Skia sobre GPU |
| Motor de video | FFmpeg, invocado como proceso externo |
| Preview | LibVLCSharp |
| MVVM | CommunityToolkit.Mvvm |
| Tests | xUnit |

> **Nota de versión**: Avalonia está fijado al rango `[11.3.13,12.0.0)` porque `LibVLCSharp.Avalonia` todavía no soporta la rama 12.x. El reproductor está aislado tras la interfaz `IPreviewPlayer` para que esa migración, cuando llegue, no afecte al resto de la aplicación.

## Compilar desde el código

### Requisitos

- [.NET 10 SDK](https://dotnet.microsoft.com/download) o superior
- FFmpeg y ffprobe (se obtienen con el script incluido)

### Pasos

```bash
git clone https://github.com/maniadiaz/EditFlow.git
cd EditFlow
```

Descarga FFmpeg y verifica su SHA-256 contra el hash fijado en `tools/ffmpeg.lock.json`:

```bat
REM Windows
tools\fetch-ffmpeg.cmd
```

```bash
# Linux y macOS
pwsh tools/fetch-ffmpeg.ps1
```

> En Windows se usa el envoltorio `.cmd` porque `pwsh` es PowerShell 7 y **no viene
> instalado con el sistema**: solo está Windows PowerShell 5.1, que se invoca como
> `powershell.exe`. El envoltorio además evita la política de ejecución que bloquea
> los `.ps1` por defecto.

Después, lo de siempre:

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/EditFlow.App
```

### Publicar

```bash
dotnet publish src/EditFlow.App -c Release -r win-x64   --self-contained -p:PublishTrimmed=true
dotnet publish src/EditFlow.App -c Release -r linux-x64 --self-contained -p:PublishTrimmed=true
dotnet publish src/EditFlow.App -c Release -r osx-arm64 --self-contained -p:PublishTrimmed=true
```

## Contribuir

El proyecto sigue **Git Flow**, **[Conventional Commits](https://www.conventionalcommits.org/)** y **[SemVer](https://semver.org/)**. Los detalles están en [`CONTRIBUTING.md`](CONTRIBUTING.md).

Tras clonar, activa los hooks del repositorio:

```bash
git config core.hooksPath .githooks
```

Validan el formato de los mensajes de commit y bloquean que datos sensibles entren al historial.

## Licencia

**GPL-3.0-or-later** — ver [`LICENSE`](LICENSE).

EditFlow es software **100 % libre y gratuito**. Puedes usarlo, estudiarlo, modificarlo y
redistribuirlo. A cambio, cualquier versión derivada que distribuyas debe conservar tu
aviso de copyright **y publicar también su código fuente** bajo la misma licencia.

Es la misma elección que hacen [Shotcut](https://shotcut.org) y
[Kdenlive](https://kdenlive.org), y por el mismo motivo: permite empaquetar la build
completa de FFmpeg —con x264 y x265— sin ninguna ambigüedad legal, y garantiza que el
proyecto siga siendo libre para quien venga después.

### Nada propietario

El proyecto no depende de ningún componente de pago, servicio externo ni SDK con licencia:

| Componente | Licencia |
|---|---|
| .NET, Avalonia, NAudio, CommunityToolkit.Mvvm, SkiaSharp | MIT |
| xUnit | Apache-2.0 |
| FFmpeg (build completa, con x264 y x265) | GPL-2.0-or-later |
| whisper.cpp y los modelos de Whisper (subtítulos automáticos, se descargan bajo demanda) | MIT |

Quedan deliberadamente fuera del proyecto los formatos de cámara RAW que exigen el SDK del
fabricante (RED, ARRIRAW, Blackmagic RAW) y cualquier integración con servicios de pago.
El detalle está en [`docs/PARIDAD-PREMIERE.md`](docs/PARIDAD-PREMIERE.md).

---

<div align="center">
<sub>GPL-3.0-or-later © 2026 maniadiaz</sub>
</div>
