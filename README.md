<div align="center">

# EditFlow

**Editor de video de escritorio — nativo, ligero y multiplataforma.**

[![CI](https://github.com/maniadiaz/EditFlow/actions/workflows/ci.yml/badge.svg)](https://github.com/maniadiaz/EditFlow/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
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
| `v0.0.1` | Esqueleto del proyecto, CI y convenciones | 🚧 En curso |
| `v0.1.0` | Importar · unir · cortar · previsualizar · exportar | Planificado |
| `v0.2.0` | Pista de audio y superposiciones de texto | Planificado |
| `v0.3.0` | Transiciones, velocidad y corrección de color | Planificado |

El plan completo, con las decisiones de arquitectura y su justificación, está en **[`docs/PLAN.md`](docs/PLAN.md)**.

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

# Descarga FFmpeg y verifica su SHA-256
pwsh tools/fetch-ffmpeg.ps1

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

**MIT** — ver [`LICENSE`](LICENSE). Puedes usar, modificar y distribuir este código libremente, **siempre que conserves el aviso de copyright**: la atribución al autor es obligatoria y viaja con el código.

### Sobre FFmpeg

EditFlow **no incluye ni enlaza** FFmpeg en este repositorio: lo invoca como proceso externo y el script `tools/fetch-ffmpeg.ps1` lo descarga en tu máquina.

Ten en cuenta que las builds *full* de FFmpeg son **GPL** (incluyen x264 y x265). Si en el futuro distribuyes un instalador de EditFlow con FFmpeg dentro, lo limpio es empaquetar una build **LGPL** —que conserva NVENC, Quick Sync, AMF y AV1, justo los codificadores por hardware que más interesan aquí— o descargar FFmpeg en el primer arranque.

---

<div align="center">
<sub>MIT © 2026 maniadiaz</sub>
</div>
