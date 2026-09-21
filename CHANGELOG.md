# Changelog

Todos los cambios relevantes de EditFlow se documentan en este archivo.

El formato sigue [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto se adhiere a [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

### Changed

- **EditFlow pasa de MIT a GPL-3.0-or-later.** El objetivo es que el proyecto sea 100 %
  libre y gratuito, sin componentes de pago ni SDK con licencia. Empaquetar la build
  completa de FFmpeg —con x264 y x265, ambos GPL— junto a código MIT era una zona gris
  legal; con GPL-3.0 desaparece, y además garantiza que el proyecto siga libre para quien
  venga después. Es la misma elección que Shotcut y Kdenlive.
- Todo archivo de código lleva cabecera SPDX de dos líneas.

### Added

- **Copias de edición automáticas (proxies)**: al importar o abrir un proyecto, los videos por
  encima de 720p —y los de códecs pesados como HEVC, AV1 o ProRes— reciben en segundo plano
  una copia de 480p pensada para buscar rápido (un fotograma clave cada 12, sin fotogramas B,
  sin audio, tiempos idénticos al original). El preview cambia a ella solo cuando está lista;
  la exportación sigue usando siempre el original. Se generan de una en una con dos hilos
  para no ahogar un equipo de 8 GB, viven en la caché del usuario (nunca junto al proyecto),
  se invalidan solas si el original cambia y se limpian a 5 GB borrando las menos usadas.
- **El preview ahora reproduce el audio completo del montaje**: música, efectos, volúmenes,
  fundidos y silencios suenan igual que en la exportación, porque se renderiza con el mismo
  grafo (sin la parte de video) a un único archivo FLAC que hace de reloj maestro. Se acabó
  el pequeño hueco en cada corte y el aproximado de volumen de LibVLC (tope de +6 dB): un
  clip a +12 dB suena ya como sonará exportado. La mezcla se vuelve a preparar unos 500 ms
  después de la última edición y mientras tanto el video sigue en silencio.
- **Volumen y silencio por clip de video**: subir y bajar de 3 en 3 dB, restablecer y
  silenciar desde el menú de clic derecho, sin necesidad de separar el audio. Se aplica en
  la exportación y se guarda en el proyecto.

- Timeline multipista en la interfaz: pista de video y pistas de audio, con cabeceras
  **pegadas al borde izquierdo** aunque se desplace en horizontal, y botones M (silenciar),
  S (solo) y L (bloquear) por pista.
- Arrastrar un clip de audio por su pista con **imán** a los bordes cercanos: cortes del
  video, cabezal, origen y bordes de otros clips. Soltar sobre otro clip lo rechaza en
  lugar de superponerlos.
- **Reordenar las pistas de audio** arrastrando su cabecera.
- **Menú de clic derecho**: sobre un clip de video (dividir, separar audio, eliminar),
  sobre un clip de audio (subir y bajar volumen, restablecer, silenciar, fundidos de
  entrada y salida, eliminar) y sobre una pista (añadir, silenciar, solo, bloquear, eliminar).
- Importar audio (mp3, wav, aac, m4a, flac, ogg, opus) a una pista, en la posición del
  cabezal.
- La timeline crece con el número de pistas hasta un máximo y a partir de ahí se
  desplaza.
- Un clip con el audio separado no suena en el preview: ya sale de su pista.

### Fixed

- Abrir un proyecto dejaba el preview en negro hasta pulsar algo.

- Multipista: pistas de audio con clips de posición libre, volumen, fundidos de entrada
  y salida, silenciar, solo y bloquear, y **reordenar las pistas**.
- **Separar el audio de un clip de video** a su propia pista, con deshacer. El clip de
  video deja de aportar su sonido para que no se oiga duplicado, y la segunda mitad de un
  clip cortado hereda ese estado.
- La exportación mezcla las pistas de audio: desfase por posición, volumen del clip y de
  la pista, fundidos medidos desde el inicio del propio clip, y `normalize=0` para que
  añadir una música no baje el nivel del resto.
- Si una música dura más que el video, la imagen se extiende con negro hasta el final.
- El proyecto `.editflow` guarda las pistas de audio y qué clips tienen el audio
  separado. Formato 2; los proyectos del formato 1 se siguen abriendo.

- Decodificador de video propio: FFmpeg produce fotogramas BGRA crudos que se dibujan en
  un control normal de Avalonia. Sustituye al `VideoView` de LibVLCSharp, que es una
  ventana nativa y tapa cualquier control superpuesto.
- Reserva de fotogramas reutilizables, con memoria acotada y predecible: 46 MB para 30
  fotogramas a 480p.
- Reloj de reproducción medido contra cronómetro, que entrega los fotogramas a su ritmo
  en lugar de a la velocidad del decodificador.
- Sincronización con reloj maestro y descarte de fotogramas retrasados.
- `AudioClock`: LibVLC en modo solo audio, sin ventana nativa, como reloj maestro.

### Changed

- **El `VideoView` de LibVLCSharp queda sustituido por una superficie propia.** Con ello
  se resuelve el bloqueo documentado en la sección 13 del plan: ya se pueden superponer
  controles sobre el preview, que era requisito de la previsualización de texto, la de
  color y el menú contextual.

- Guardar y abrir proyectos en archivos `.editflow`. El proyecto guarda qué archivos se
  usaron y qué intervalo de cada uno se reproduce, no el video: un montaje de una hora
  ocupa unos pocos kilobytes.
- Los proyectos guardan la ruta absoluta y la relativa de cada medio, de modo que mover
  la carpeta entera —a otro disco o a otro equipo— no rompe el montaje.
- Un medio que ya no existe se informa al abrir, en vez de impedir abrir el proyecto.
- Botones de reproducción: −30 s, −5 s, reproducir/pausar, +5 s y +30 s, con atajos
  ←/→ y Mayús+←/→, más Inicio y Fin.
- El cabezal sigue la reproducción y encadena los clips, en lugar de detenerse en cada
  corte.
- Atajos de proyecto: Ctrl+N, Ctrl+O, Ctrl+S y Ctrl+Mayús+S.
- El título de la ventana muestra el nombre del proyecto y si hay cambios sin guardar.
- Abrir un `.editflow` desde la línea de comandos carga el proyecto; cualquier otro
  archivo se importa como medio.

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
