# Changelog

Todos los cambios relevantes de EditFlow se documentan en este archivo.

El formato sigue [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto se adhiere a [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

## [0.1.1] - 2026-09-20

### Fixed

- El comando documentado para descargar FFmpeg no existía en Windows. `pwsh` es
  PowerShell 7 y no viene instalado con el sistema, así que seguir el README en una
  máquina limpia fallaba en el primer paso. Se añade `toolsetch-ffmpeg.cmd`, que
  llama a `powershell.exe` y evita además la política de ejecución que bloquea los
  `.ps1` por defecto.
- `tools/fetch-ffmpeg.ps1 -Update` nunca encontraba los checksums publicados. En
  Windows PowerShell 5.1, `Invoke-WebRequest` devuelve el contenido como `Byte[]` y
  no como cadena, de modo que la división por líneas recorría el array byte a byte.

## [0.1.0] - 2026-09-20

### Added

- Estructura inicial del repositorio con licencia MIT.
- Plan de implementación y decisiones de arquitectura en `docs/PLAN.md`.
- Convenciones de Git Flow, Conventional Commits y SemVer en `CONTRIBUTING.md`.
- Hook `commit-msg` que valida el formato de los mensajes de commit.
- Hook `pre-commit` que bloquea archivos de medios, secretos, archivos de más
  de 10 MB, rutas absolutas de usuario y patrones de credenciales conocidos.
- `.gitignore` endurecido y `.gitattributes` con normalización de fin de línea.
- Solución `EditFlow.slnx` con cuatro proyectos: `EditFlow.Core`, `EditFlow.Engine`,
  `EditFlow.App` (Avalonia) y `EditFlow.Engine.Tests` (xUnit).
- Gestión centralizada de versiones de paquetes en `Directory.Packages.props`,
  con Avalonia fijado de forma exacta a `[11.3.22]` por compatibilidad con
  `LibVLCSharp.Avalonia`, que no soporta la rama 12.x.
- Test de arquitectura que verifica que `EditFlow.Core` y `EditFlow.Engine`
  no dependen de ningún framework de interfaz gráfica.
- `tools/fetch-ffmpeg.ps1` y `tools/ffmpeg.lock.json`: descarga de FFmpeg con
  verificación SHA-256 contra un hash fijado en control de versiones.
- `FFmpegLocator`: resuelve ffmpeg y ffprobe priorizando los binarios empaquetados
  sobre los del sistema, porque una build del sistema puede no incluir NVENC.
- `EncoderDetector`: detecta los codificadores realmente utilizables mediante una
  codificación de prueba, no solo leyendo `ffmpeg -encoders`, y traduce los errores
  de FFmpeg a explicaciones accionables.
- Filtro por plataforma: VideoToolbox solo se ofrece en macOS y VA-API solo en Linux.
- Modelo de timeline en `EditFlow.Core`: `MediaInfo`, `Clip` y `VideoTimeline`,
  con cortar, recortar, reordenar y eliminar. Las posiciones se derivan del orden
  de los clips, lo que hace imposibles los huecos y los solapamientos.
- `MediaInfo` corrige la rotación de los metadatos, de modo que un video vertical
  grabado con móvil no se trate como apaisado.
- Ajustes de exportación: resoluciones 480p, 720p, 1080p, 1440p y 4K; elección de
  codificador por GPU o CPU; modos de calidad constante, bitrate variable y bitrate
  constante; bitrate de video y audio; y preset de velocidad.
- `QualityScale`: escala de calidad normalizada de 1 a 100, traducida al rango nativo
  de cada familia, para que cambiar de códec no altere la calidad en silencio.
- `FFmpegArgumentBuilder`: traduce los ajustes a argumentos de FFmpeg por familia de
  codificador, verificado contra `ffmpeg -h encoder=<nombre>`.
- `RateControlCapabilities`: informa de las combinaciones con compromisos, como el
  bitrate constante en SVT-AV1.
- `FilterGraphBuilder`: recorta cada clip, lo normaliza a un lienzo común y los
  concatena, inyectando silencio sintético en los clips sin pista de audio.
- `ExportCommandBuilder`: pasa el grafo por archivo cuando supera el límite de
  longitud de la línea de comandos de Windows.
- `ExportJob`: ejecuta la exportación informando del avance y permitiendo cancelarla;
  una exportación fallida o cancelada no deja archivo parcial.
- `ProgressParser`: interpreta `-progress pipe:1` con porcentaje, velocidad y tiempo
  restante estimado.
- `FFprobeService`: lee duración, resolución, fotogramas por segundo, códec, presencia
  de audio y rotación desde la salida JSON de ffprobe.
- Ventana principal con panel de medios, reproductor y controles de transporte,
  mostrando los codificadores detectados en la máquina.
- Deshacer y rehacer para todas las operaciones de edición: añadir, eliminar,
  mover, dividir y recortar clips.
- Timeline dibujada a medida con regla de tiempo, clips proporcionales a su
  duración, cabezal arrastrable y zoom con Ctrl+rueda.
- Importación de videos por diálogo o pasándolos como argumentos al ejecutable,
  que los añade directamente al montaje.
- Atajos: S dividir, Supr eliminar, Ctrl+Z/Ctrl+Y deshacer y rehacer, Ctrl+E exportar,
  Espacio reproducir y pausar.
- Diálogo de exportación con resolución, fotogramas por segundo, códec, motor de
  codificación, modo de control de tasa, calidad o bitrate, velocidad y bitrate de
  audio. Muestra el comando de FFmpeg generado, informa del avance con velocidad y
  tiempo restante, y permite cancelar.
- Los motores no disponibles se enumeran con el motivo por el que no pueden usarse.
- La rueda del ratón ya no cambia el valor de las listas desplegables: desplazarse
  por el formulario alteraba ajustes de exportación sin avisar.

### Changed

- `UndoStack` pasa a llamarse `UndoHistory`: no es una pila, mantiene dos.

### Comprobado

- El `VideoView` de LibVLCSharp **sí** funciona dentro de una celda de un `Grid`, pero
  **no admite controles superpuestos**: su ventana nativa tapa cualquier contenido de
  Avalonia dibujado encima. Los controles de transporte pasan a una fila propia debajo
  del reproductor. Detalles en la sección 13 de `docs/PLAN.md`.

[0.1.1]: https://github.com/maniadiaz/EditFlow/releases/tag/v0.1.1
[0.1.0]: https://github.com/maniadiaz/EditFlow/releases/tag/v0.1.0
