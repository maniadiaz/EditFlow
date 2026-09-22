# EditFlow — Plan de Implementación y Decisiones de Arquitectura

> Documento vivo. Registra **qué** se construye, **cómo** y sobre todo **por qué**, para que cualquiera que clone el repositorio entienda las decisiones sin depender del contexto en que se tomaron.

**Repositorio**: https://github.com/maniadiaz/EditFlow

---

## 1. Objetivo

**EditFlow** es un editor de video de escritorio estilo CapCut/Premiere: **nativo** (sin HTML ni WebView), **ligero y eficiente**, multiplataforma.

Requisitos fundacionales:

1. Importar uno o varios videos y unirlos.
2. Cortar y seccionar clips en una timeline.
3. Exportar en 480p / 720p / 1080p / 1440p / 4K, eligiendo **codificación por GPU o CPU**, **códec**, **modo de control de tasa** y **bitrate**.
4. Ser una aplicación de escritorio ligera.
5. Un solo código base, portable a Windows, Linux y macOS.

> **Nota sobre resoluciones**: el requisito original mencionaba "420". Se interpreta como **480p (854×480)**, la resolución estándar de esa gama. No existe un formato de 420 líneas; `420` suele referirse al submuestreo de croma *yuv420p*, que sí se usa como formato de píxel de salida.

---

## 2. Stack y por qué

| Componente | Elección | Razón |
|---|---|---|
| Lenguaje / runtime | **C# / .NET 10 (LTS)** | Soporte hasta nov-2028. Rendimiento muy superior a lenguajes interpretados para una timeline con cientos de clips. |
| UI | **Avalonia 11.3.x** | UI nativa real renderizada con Skia sobre GPU — sin HTML ni WebView. Un solo código para Windows, Linux y macOS. |
| Motor de video | **FFmpeg** (binario externo) | Estándar de la industria. Invocado como proceso separado, lo que mantiene el código desacoplado y permite actualizar FFmpeg sin recompilar. |
| Preview | **LibVLCSharp** | Resuelve video, audio y sincronía A/V de una sola vez, y reproduce prácticamente cualquier contenedor y códec. |
| MVVM | **CommunityToolkit.Mvvm** | Generadores de código, cero *boilerplate*. |
| Tests | **xUnit** | Estándar de facto en .NET. |

### Alternativas descartadas

| Alternativa | Por qué se descartó |
|---|---|
| **Tauri / Electron** | Generan aplicaciones de escritorio reales, pero renderizan la UI con HTML/CSS. El requisito era UI nativa. |
| **Python + PySide6 (Qt)** | Qt es nativo y válido, pero el techo de rendimiento es menor y el empaquetado en macOS (firma y notarización) es notablemente más doloroso. |
| **C++ / Qt 6** | Máximo rendimiento y lo que usan Shotcut y Kdenlive, pero con un coste de desarrollo 3–4× mayor para el mismo resultado funcional. |

### ⚠️ Restricción de versión: Avalonia queda fijado en 11.3.x

`LibVLCSharp.Avalonia` 3.10.1 (ago-2026) declara `Avalonia >= 11.3.13` y **no soporta la rama 12.x**. El fork `LibVLCSharp.Avalonia.Unofficial`, que resolvía las limitaciones del `VideoView`, está **archivado desde octubre de 2023** y no se publica en NuGet, por lo que no es una dependencia viable.

**Decisión**: fijar `Avalonia` de forma **exacta** a `[11.3.22]` en `Directory.Packages.props`.
Es una rama madura y con soporte. El reproductor se aísla tras la interfaz `IPreviewPlayer`, de
modo que migrar a Avalonia 12 —o sustituir LibVLC por un renderer propio— no obligue a tocar el
resto de la aplicación.

**Al crear el proyecto hay un detalle que sorprende**: `dotnet new avalonia.app` genera código
para la rama **12.x**. Usa `.WithDeveloperTools()`, del paquete `AvaloniaUI.DiagnosticsSupport`,
que no existe en 11.3. El equivalente en esta rama es el paquete `Avalonia.Diagnostics` con
`this.AttachDevTools()` en el constructor de la ventana, referenciado **solo en configuración
`Debug`** para que las herramientas de desarrollo no viajen en los binarios publicados.

---

## 3. Arquitectura

```
EditFlow/
├─ EditFlow.slnx                      # formato de solución de .NET 10
├─ Directory.Build.props              # net10.0, nullable, analizadores, metadatos
├─ Directory.Packages.props           # versiones centralizadas (CPM)
├─ src/
│  ├─ EditFlow.Core/                  # Modelo puro. CERO dependencias de UI y de FFmpeg.
│  │  ├─ Models/{Project,Track,Clip,TextClip,TimeRange,MediaInfo}.cs
│  │  ├─ Timeline/TimelineOps.cs      # Split / Trim / Move / RippleDelete
│  │  ├─ Undo/{IUndoableCommand,UndoStack}.cs
│  │  └─ Serialization/ProjectSerializer.cs   # .editflow = JSON (System.Text.Json source-gen)
│  ├─ EditFlow.Engine/                # Todo lo que toca FFmpeg. Testeable sin UI.
│  │  ├─ FFmpegLocator.cs             # resuelve ffmpeg/ffprobe según plataforma
│  │  ├─ FFprobeService.cs            # duración, resolución, fps, códec, ¿audio?, rotación
│  │  ├─ EncoderDetector.cs           # detección + smoke test real
│  │  ├─ ExportSettings.cs
│  │  ├─ FilterGraphBuilder.cs        # ← el corazón: construye el filter_complex
│  │  ├─ FFmpegArgumentBuilder.cs     # ExportSettings → flags por familia de encoder
│  │  ├─ ExportJob.cs                 # spawn + progreso + cancelación
│  │  └─ ThumbnailService.cs          # miniaturas con caché en disco
│  └─ EditFlow.App/                   # Avalonia 11.3.x
│     ├─ Controls/TimelineControl.cs  # render custom con OnRender(DrawingContext)
│     ├─ Views/{MainWindow,MediaPoolView,PreviewView,ExportDialog}.axaml
│     ├─ ViewModels/
│     └─ Services/{IPreviewPlayer,LibVlcPreviewPlayer}.cs
├─ tests/EditFlow.Engine.Tests/
├─ tools/{fetch-ffmpeg.ps1, ffmpeg/{win-x64,linux-x64,osx-arm64}/}
├─ docs/PLAN.md                       # este documento
└─ .githooks/{commit-msg, pre-commit}
```

> **Regla de oro**: `Core` y `Engine` **no referencian Avalonia**. Toda la lógica de exportación es testeable desde consola, sin abrir la interfaz. Es lo que permite que la suite de tests corra en CI sobre Linux sin entorno gráfico.

> **Matiz (v0.4)**: `Engine` sí usa **SkiaSharp** (MIT, biblioteca de dibujo 2D que funciona sin ventana) para dibujar los textos superpuestos a PNG. Así el preview y la exportación comparten un único código de dibujo —`drawtext` de FFmpeg tiene su propio motor de fuentes y no coincidiría con lo que se ve al editar—. `Core` sigue sin depender de ella, y el test de capas lo comprueba.

---

## 4. 🔒 Regla inquebrantable: nada sensible llega al repositorio

Esta regla tiene **prioridad sobre cualquier otra parte del plan**. Si hay duda sobre un archivo, no se commitea.

### Riesgos concretos de este proyecto

| Qué puede filtrarse | Por qué en este proyecto | Mitigación |
|---|---|---|
| **Ruta de usuario del sistema** | Rutas absolutas en código, `launchSettings.json` y archivos de proyecto | El código usa `Environment.GetFolderPath` y rutas relativas, nunca literales. Hook que bloquea rutas de usuario en el diff |
| **Videos personales** | Se importan constantemente para probar la aplicación | `.gitignore` bloquea todos los formatos de medios. Los tests **generan** sus propios videos con `ffmpeg -f lavfi -i testsrc` |
| **Archivos de proyecto `.editflow`** | Guardan **rutas absolutas a los videos del usuario** — filtran tanto la ruta como qué contenido existe en la máquina | `*.editflow` ignorado por completo |
| **Caché de miniaturas** | Son fotogramas extraídos de videos personales | `cache/`, `thumbnails/` ignorados |
| **Logs y volcados de fallo** | Contienen rutas completas y a veces fragmentos de medios | `logs/`, `*.log`, `crashdumps/` ignorados |
| **Tokens y certificados** | GitHub Actions, firma del instalador | `.gitignore` + hook que detecta prefijos de token conocidos |
| **Email real en el historial** | El historial de git es público y permanente | Se usa el alias `@users.noreply.github.com` |

### Tres barreras, no una

1. **`.gitignore` endurecido**, escrito **antes** del primer `git add`. Es la única forma de garantizar que nada sensible entre al historial inicial.
2. **Hook `pre-commit`**, que bloquea el commit si detecta en lo preparado: rutas de usuario, prefijos de token (`ghp_`, `github_pat_`, `gho_`, `AKIA`, bloques `BEGIN ... PRIVATE KEY`), archivos de más de 10 MB, o cualquier extensión de medios —incluso si se forzó con `git add -f`.
3. **Revisión manual** de `git status` y `git diff --cached` antes de cada push relevante.

### Procedimiento

- `git init` se ejecuta **únicamente** en la raíz de `EditFlow/`, nunca en un directorio superior.
- **Nunca** `git add -A` ni `git add .` desde fuera del repositorio.
- CI sin secretos. Si alguna vez hiciera falta uno, va en **GitHub Secrets**, jamás en el YAML.
- Si algo sensible llegara a entrar: **borrarlo en un commit posterior no sirve**, queda en el historial de forma permanente. Hay que reescribir con `git filter-repo`, forzar el push y **rotar** toda credencial expuesta.

---

## 5. Convenciones de Git

### Ramas — Git Flow completo

| Rama | Origen | Destino | Propósito |
|---|---|---|---|
| `main` | — | — | Solo releases. Cada merge lleva un tag SemVer. Nunca se commitea directo. |
| `develop` | `main` | — | Rama de integración. Base de todo el desarrollo. |
| `feature/*` | `develop` | `develop` | Una funcionalidad. Ej: `feature/encoder-detection` |
| `release/*` | `develop` | `main` + `develop` | Estabilización de una versión. Ej: `release/0.1.0` |
| `hotfix/*` | `main` | `main` + `develop` | Corrección urgente sobre producción. Ej: `hotfix/0.1.1` |

```
main     ──o───────────────────o──────────────o──►   v0.1.0    v0.2.0
            \                 /              /
develop  ────o───o───o───o───o───o───o───o──o──►
                  \ /         \ /     \ /
        feature/engine  feature/timeline  feature/audio-track
```

Merges a `develop` con **squash** (un commit limpio por funcionalidad). Merges de `release`/`hotfix` a `main` con **`--no-ff`**, para que el punto de release quede visible en el grafo.

### Mensajes — Conventional Commits 1.0.0

Formato: `<type>(<scope>)<!>: <description>`. Descripción en **inglés**, modo imperativo, ≤ 72 caracteres, sin punto final.

**Types**: `feat` · `fix` · `docs` · `style` · `refactor` · `perf` · `test` · `build` · `ci` · `chore` · `revert`
**Scopes**: `core` · `engine` · `app` · `timeline` · `export` · `preview` · `ffmpeg` · `deps`

```
feat(engine): detect NVENC/QSV/AMF encoders with smoke test
feat(timeline): split clip at playhead with S key
fix(export): force -b:v 0 in NVENC CQ mode
perf(timeline): cache clip thumbnails on disk
test(engine): cover filter graph for clip without audio
build(ffmpeg): add download script with SHA-256 verification
chore(deps): pin Avalonia to [11.3.13,12.0.0)
```

Cambios incompatibles: `feat(core)!: ...` más un footer `BREAKING CHANGE: <explicación>`.

Validación automática por el hook `commit-msg` (activado con `git config core.hooksPath .githooks`):

```
^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([a-z0-9-]+\))?!?: .{1,72}$
```

### Versionado — SemVer 2.0.0

Tags anotados `v0.1.0`, `v0.2.0`, `v1.0.0`. `CHANGELOG.md` siguiendo **Keep a Changelog**, agrupado en `Added` / `Changed` / `Fixed` / `Removed`.

---

## 6. Fase 0 — Toolchain, repositorio y esqueleto

1. Instalar el SDK de .NET 10 (`winget install Microsoft.DotNet.SDK.10`) y las plantillas de Avalonia (`dotnet new install Avalonia.Templates`).
2. Inicializar el repositorio con `.gitignore` endurecido, `.gitattributes`, `LICENSE`, `README.md`, `CHANGELOG.md`, `CONTRIBUTING.md` y los hooks. CI en verde desde el primer día.
3. Crear la solución y los cuatro proyectos. Las versiones de paquetes se gestionan de forma
   centralizada en `Directory.Packages.props` (Central Package Management), y Avalonia queda
   **fijado de forma exacta** a `[11.3.22]` para que no pueda saltar a la rama 12.x.
4. `tools/fetch-ffmpeg.ps1`: descarga la **full build** de FFmpeg, **verifica su SHA-256** y la extrae a `tools/ffmpeg/<rid>/`. Los binarios **no entran al repositorio**; se copian a la salida de compilación desde el `.csproj`.

### `.gitattributes` — por qué importa

Sin él, los editores de Windows introducen CRLF y los diffs se llenan de ruido que oculta los cambios reales. Se fija `* text=auto eol=lf`, con `eol=crlf` solo donde Windows lo exige (`*.ps1`, `*.cmd`, `*.sln`) y `binary` para `*.exe` y `*.dll`.

### ⚖️ Licencia: GPL-3.0, sin ambigüedades

EditFlow es **GPL-3.0-or-later**. Cualquiera puede usarlo, estudiarlo, modificarlo y
redistribuirlo; quien distribuya una versión derivada debe conservar el aviso de copyright
y publicar su código bajo la misma licencia.

El proyecto arrancó siendo MIT, y se cambió al fijar como objetivo que fuera **100 % libre
y gratuito, sin componentes de pago ni SDK con licencia**. Empaquetar la build completa de
FFmpeg —que incluye x264 y x265, ambos GPL— junto a código MIT era una zona gris legal;
con GPL-3.0 desaparece.

La alternativa evaluada fue mantener MIT y pasar a la build LGPL de FFmpeg. Se midió lo que
costaba: 5 codificadores y 38 filtros de 569, con sustituto igual o mejor en casi todos los
casos, pero `libopenh264` comprime de forma medible peor que x264 (SSIM 0,975 frente a
0,987 al mismo bitrate en 720p). Se descartó por esa pérdida de calidad y porque GPL-3.0
además garantiza que el proyecto siga libre para quien venga después.

Es la misma decisión, por el mismo motivo, que tomaron Shotcut y Kdenlive.

**Release**: `v0.0.1` — esqueleto compilando y CI en verde.

---

## 7. Fase 1 — Núcleo usable (importar · unir · cortar · exportar)

Cubre los requisitos 1 a 5. Al cerrarla, EditFlow ya es un editor funcional.

### 7.1 Modelo (`EditFlow.Core`)

```csharp
record MediaInfo(string Path, TimeSpan Duration, int Width, int Height,
                 double Fps, string VideoCodec, bool HasAudio, int Rotation);

class Clip {
    string SourcePath;
    TimeSpan SourceIn, SourceOut;   // recorte dentro del archivo origen
    TimeSpan TimelineStart;         // posición en la timeline
    TimeSpan Duration => SourceOut - SourceIn;
}
```

`TimelineOps` expone operaciones puras y testeables: `Split(track, atTime)`, `Trim(clip, edge, delta)` (acotado al material origen), `Move(clip, newStart)` y `RippleDelete(clip)`. Cada una se envuelve en un `IUndoableCommand`, de modo que **deshacer y rehacer funcionan desde el primer día** en lugar de añadirse a posteriori.

### 7.2 Detección de codificadores (`EncoderDetector`)

Dos pasos, y **el segundo es el que la mayoría de aplicaciones omite**:

1. `ffmpeg -hide_banner -encoders` → localizar `h264_nvenc`, `hevc_nvenc`, `av1_nvenc`, `h264_qsv`, `hevc_qsv`, `av1_qsv`, `h264_amf`, `hevc_amf`, `av1_amf`, `h264_videotoolbox`, `hevc_videotoolbox`, `h264_vaapi`, `libx264`, `libx265`, `libsvtav1`.

2. **Prueba real de funcionamiento** de cada candidato. Que un codificador aparezca listado **no significa que funcione**: puede faltar la GPU, el driver puede ser demasiado antiguo, o las sesiones NVENC pueden estar agotadas.

   ```
   ffmpeg -hide_banner -loglevel error -f lavfi -i color=c=black:s=256x256:r=30:d=0.2 \
          -c:v h264_nvenc -f null -
   ```

   Código de salida 0 → se habilita en la interfaz. Distinto de 0 → se muestra **deshabilitado junto con el motivo**, nunca oculto: el usuario debe poder entender *por qué* no puede usar su GPU.

El resultado se cachea y se invalida al cambiar la versión de FFmpeg.

### 7.3 Construcción del grafo (`FilterGraphBuilder`)

Normaliza todos los clips a un lienzo común y los concatena:

```
ffmpeg -y
  -ss <in_i> -t <dur_i> -i "clip_i.mp4"                 (por clip; -ss antes de -i = seek rápido)
  -f lavfi -t <dur_i> -i anullsrc=r=48000:cl=stereo     (solo para clips SIN audio)
  -filter_complex "
     [0:v]fps=FPS,scale=W:H:force_original_aspect_ratio=decrease,
          pad=W:H:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,format=yuv420p[v0];
     [0:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[a0];
     ... (ídem por clip) ...
     [v0][a0][v1][a1]...concat=n=N:v=1:a=1[vout][aout]"
  -map "[vout]" -map "[aout]"
  <flags de codificador>  -c:a aac -b:a 192k  -movflags +faststart  "salida.mp4"
```

Detalles que rompen a los editores construidos a la ligera:

- **Clips sin pista de audio** → hay que inyectar `anullsrc`, o `concat` falla con un error poco descriptivo.
- **Mezcla de fps, resolución y SAR** → por eso `fps=`, `scale+pad` y `setsar=1` van en **cada rama**, no al final del grafo.
- **Rotación en grabaciones de móvil** → leer la metadata con `ffprobe` y aplicar `transpose` **antes** del `scale`.
- El grafo se escribe a un archivo temporal y se pasa con **`-filter_complex_script`**: con muchos clips se supera el límite de longitud de línea de comandos de Windows (~32 KB).

### 7.4 Calidad, GPU/CPU y bitrate (`FFmpegArgumentBuilder`)

| Preset | Resolución | Bitrate VBR sugerido (H.264) |
|---|---|---|
| 480p | 854×480 | 2.5 Mbps |
| 720p | 1280×720 | 5 Mbps |
| 1080p | 1920×1080 | 10 Mbps |
| 1440p | 2560×1440 | 20 Mbps |
| 4K | 3840×2160 | 40 Mbps |

HEVC y AV1 alcanzan calidad equivalente en torno al 60 % de esos valores.

| Familia | Calidad constante | VBR (bitrate objetivo) | CBR | Preset |
|---|---|---|---|---|
| `libx264` / `libx265` | `-crf N` (0–51) | `-b:v X -maxrate 1.5X -bufsize 2X` | `-b:v X -minrate X -maxrate X -bufsize 2X` | `ultrafast`…`veryslow` |
| `libsvtav1` | `-crf N` (0–63) | `-b:v X` | — | `-preset 0..13` |
| `*_nvenc` | `-rc vbr -cq N -b:v 0` | `-rc vbr -b:v X -maxrate 1.5X -bufsize 2X` | `-rc cbr -b:v X` | `-preset p1..p7 -tune hq` |
| `*_qsv` | `-global_quality N -look_ahead 1` | `-b:v X -maxrate 1.5X` | `-b:v X -maxrate X` | `veryfast`…`veryslow` |
| `*_amf` | `-rc cqp -qp_i N -qp_p N` | `-rc vbr_peak -b:v X` | `-rc cbr -b:v X` | `speed`/`balanced`/`quality` |
| `*_videotoolbox` | `-q:v N` | `-b:v X` | `-b:v X` | — |

> ⚠️ El `-b:v 0` en el modo CQ de NVENC **es obligatorio**: sin él, el codificador aplica un límite de bitrate por defecto y el `-cq` queda silenciosamente ignorado. Hay un test dedicado a esta condición.

Audio: AAC a 128 / 192 / 256 / 320 kbps.

### 7.5 Progreso y cancelación (`ExportJob`)

Se lanza con `-progress pipe:1 -nostats -hide_banner` y se parsean las líneas `clave=valor` de stdout:

- `out_time_us=` → **porcentaje** = `out_time / duración total de la timeline`
- `frame=`, `fps=`, `speed=` → tiempo restante estimado y velocidad (p. ej. "2.4x")
- `progress=end` → finalizado

stderr se acumula en un buffer circular para poder mostrar el error real si el proceso falla. Cancelar implica terminar el árbol de procesos **y** eliminar el archivo parcial.

### 7.6 Interfaz

- **Media Pool** (izquierda): importación por botón o **arrastrar y soltar**; `ffprobe` al importar; muestra duración, resolución y códec.
- **Preview** (arriba derecha): `VideoView` de LibVLCSharp detrás de `IPreviewPlayer`. Al mover el cabezal se resuelve qué clip está debajo y se hace `SetMedia` + `SeekTo(offset)`. Espacio = reproducir/pausar; ←/→ = fotograma a fotograma.
- **TimelineControl** (abajo): un `Control` con `OnRender(DrawingContext)` propio — **no** un `ItemsControl`, para que rinda con cientos de clips.
  - Zoom en píxeles por segundo (Ctrl + rueda), regla de tiempo, cabezal arrastrable.
  - Clips con miniatura de fondo (`ThumbnailService`, con caché).
  - Hit-testing: el cuerpo del clip mueve; 6 px en los bordes recortan (el cursor cambia para indicarlo).
  - Atajos: **S** dividir · **Supr** eliminar · **Ctrl+Z / Ctrl+Y** deshacer y rehacer.
- **ExportDialog**: resolución · fps · códec (H.264 / HEVC / AV1) · **motor de codificación** (los detectados, con los no disponibles en gris y su motivo) · modo de control de tasa · bitrate · preset de velocidad · bitrate de audio · ruta de salida. Incluye un panel desplegable con el **comando FFmpeg generado** —de enorme valor para depurar— y una barra de progreso con tiempo restante y velocidad.

**Entregable** → `release/0.1.0` → tag **v0.1.0**

---

## 8. Hoja de ruta a partir de la 0.1.x

La Fase 1 entregó lo pedido originalmente. A partir de aquí el alcance creció hacia
un editor con las funciones que la gente usa de verdad en Premiere y CapCut.

> **Sobre "las mismas opciones que Premiere"**: paridad literal no es alcanzable —son
> tres décadas y cientos de ingenieros—. Lo que sí lo es, y es lo que se persigue aquí,
> es cubrir el 90 % de lo que se usa a diario. Cuando en este documento se diga
> "estilo Premiere", se refiere a ese 90 %.

### Restricción que manda sobre el diseño: 8 GB de RAM

Windows consume entre 4 y 6 GB, así que la aplicación dispone realmente de 2 a 4 GB.
Esto descarta decodificar 4K a pelo para el preview y obliga a **media proxy**: al
importar se genera en segundo plano una copia a 480p; la edición y el preview usan
esa copia y la exportación usa siempre el original. Es como lo resuelven Premiere y
DaVinci, y es la única forma de que adelantar un 4K no vaya a tirones en esa máquina.

### v0.2.0 — Base técnica

| Entrega | Por qué va primero |
|---|---|
| Guardar y abrir proyectos (`.editflow`) | Independiente del resto; sin esto el trabajo se pierde al cerrar |
| Botones de reproducción: pausa, −5 s, −30 s | Funcionan ya con el reproductor actual |
| Media proxy automático al importar | Prerrequisito del scrubbing fluido |
| **Reproductor propio: FFmpeg → `WriteableBitmap`** | Sustituye a LibVLC; desbloquea todo lo demás |
| Salida de audio con NAudio | Lo único que LibVLC daba gratis |

**Por qué el reproductor es el cimiento y no un paso más.** Tres requisitos distintos
apuntan al mismo obstáculo: previsualizar texto y color sobre el video, mostrar un menú
contextual encima del preview, y adelantar sin tirones. El `VideoView` de LibVLCSharp
impide los tres —es una ventana nativa que tapa todo lo que se dibuje sobre ella, y no
da control de fotogramas— según se comprobó en la sección 13.

**Detalles que deciden el rendimiento**, recogidos de la experiencia publicada de otros
proyectos Avalonia antes de escribir una línea:

- El formato de píxel debe ser **`Bgra8888`**. Con `Bgr24` un video de 30 fps cae por
  debajo de 10: la conversión por fotograma se come el presupuesto.
- Los píxeles se copian en el hilo productor y solo el volcado final ocurre en el hilo
  de interfaz. Crear el `WriteableBitmap` en el hilo de interfaz pierde fotogramas.

**Audio**: NAudio 3.1.0, que en su rama 3 selecciona el backend por plataforma (ALSA en
Linux). Alternativa evaluada: OwnAudioSharp, que empaqueta sus binarios nativos; se
descarta SoundFlow porque su autor anunció una pausa de mantenimiento hasta 2027.

> **Alcance frente a Premiere**: el catálogo completo de Premiere, clasificado por lo que
> es alcanzable y lo que no, está en **[`PARIDAD-PREMIERE.md`](PARIDAD-PREMIERE.md)**.
> Resumen: alrededor del 70 % es alcanzable porque FFmpeg ya implementa el algoritmo y lo
> que falta es interfaz. Las funciones de **colaboración quedan descartadas por decisión
> de producto**, junto con las integraciones del ecosistema Adobe y los modelos
> generativos, que no dependen de nosotros.

### v0.3.0 — Multipista y edición

Entregado ya en la **0.2.0**: pistas de audio con reordenación, separar el audio de un clip
de video, volumen y silencio por clip, fundidos, imán, mezcla completa en el preview y el
menú contextual con clic derecho.

Entregado en `develop` para esta versión: recortar y dividir **clips de audio**, forma de onda
en los clips de audio y miniaturas dentro de los clips de video.

Queda para esta versión:

- Las varias pistas de video con superposición pasan a la 0.4.0: texto, transiciones y capas necesitan
  la misma composición en el grafo de filtros, y tiene sentido construirla una sola vez.

Herramientas de la timeline entregadas: mover corte (*rolling*), *slip* y *slide*, con Alt. El
*ripple* ya es el comportamiento normal de la pista, que no admite huecos.

### v0.4.0 — Capas, subtítulos y color ✅ publicada

Entregado:

- **Capas sobre el video**: textos e imágenes (renderizados con SkiaSharp, mismo código de dibujo en preview
  y exportación) y **videos superpuestos** con su sonido, que se suben desde la pista principal con un
  botón, con U o arrastrando, dejando un hueco; también se bajan de vuelta. En el preview se ven en vivo
  (un decodificador por capa visible, atado al reloj de audio) y se exportan compuestos con `overlay`.
- **Capa «Sub»** única y siempre delante para los subtítulos.
- **Subtítulos automáticos**: Whisper (whisper.cpp, MIT) en local, alineados con el detector de voz Silero, y
  **traducción** entre idiomas con Qwen3.5 4B (Apache-2.0) sobre llama.cpp (MIT). Todo se descarga bajo
  demanda con huella SHA-256 fijada.
- **Ajuste de color** por clip: exposición, contraste, saturación y temperatura, con filtros YUV baratos
  (`lutyuv`, `eq`) compartidos por el preview y la exportación.
- **Copia de preview** por tramos con estado verde/amarillo/rojo, **resolución de reproducción** y
  **exportación dividida** en partes con un diálogo de exportar detallado.
- Reloj `m:ss.cc`, cabezal fluido, cursor de referencia, Guardar y salir.

Aplazado a versiones posteriores: curvas RGB, ruedas de color, LUTs `.cube`, vectorscopio y forma de onda de
color, y los paneles acoplables. El panel de color actual cubre lo cotidiano; lo avanzado solo tiene sentido
cuando haya transiciones y efectos con los que combinarlo.

### v0.5.0 — Transiciones y efectos ✅ publicada

- **Transiciones ✅ entregado.** `xfade` (video) y `acrossfade` (audio) entre dos clips consecutivos, con
  disolvencia, fundido a negro/blanco, barridos, deslizamientos y apertura circular. Era el cambio estructural
  de mayor calado que quedaba: `FilterGraphBuilder` dejó de ser un `concat` plano y pasó a encadenar los
  clips de dos en dos, con corte seco o fundido según toque en cada límite. El modelo de la timeline
  (`Transition`, `TransitionMath.Overlap`, `VideoTimeline.Layout`) es la única fuente de verdad de cuánto se
  solapan dos clips, y la usan por igual el preview en vivo, la copia de preview por tramos y la exportación,
  para que no puedan desincronizarse entre sí. Se añade y edita con un clic en la marca `+`/duración que
  aparece en cada límite entre clips de la timeline, o seleccionando el clip entrante y abriendo el panel
  *Transición*. Formato de proyecto en versión 6 (los anteriores se abren sin cambios).
- **Velocidad ✅ entregado.** De 0,1× a 16×, con `setpts` (video) y `atempo` (audio, encadenado tantas veces
  como haga falta para cubrir ese rango: el filtro de FFmpeg solo admite un factor de 0,5 a 2 por instancia).
  Cambia cuánto ocupa el clip en la timeline —más rápido lo acorta, más lento lo alarga— sin tocar su
  recorte de origen; `Clip.SourceDuration` (lo que se lee del archivo) se separó de `Clip.Duration` (lo que
  ocupa en la timeline) precisamente para esto, y `Clip.SourceTimeAt` es el único sitio que convierte entre
  los dos tiempos, para que recorte, división, arrastre, tiras de fotogramas y reproducción en vivo usen
  siempre la misma cuenta. El preview en vivo sigue al mismo reloj que el resto: el video decodifica el
  archivo a su ritmo normal y el reloj maestro le pide el fotograma que toca según la velocidad, así que se
  ve tan sincronizado como cualquier otro clip. Se ajusta con un deslizador logarítmico o los atajos
  0,25×/0,5×/1×/2×/4× en el panel *Velocidad*, con una marca en el clip cuando no va a velocidad normal.
  Formato de proyecto en versión 7 (los anteriores se abren sin cambios).
- **Recorte, zoom y rotación ✅ entregado.** Encuadre fijo por clip (no animado): zoom de 1× a 5×, posición y
  rotación libre, con tiradores directamente sobre el preview —las esquinas acercan o alejan manteniendo el
  centro, el círculo de arriba gira— y un panel *Encuadre* con los valores numéricos como respaldo. Se aplica
  con `scale`+`crop`+`rotate` de FFmpeg sobre el propio fotograma del clip (antes de encajarlo en el lienzo
  de salida), así que el resultado mide igual que antes del encuadre y no le importa al resto de la rama
  —normalización de tamaño y color— que venga después. Girar sin haber acercado lo suficiente deja ver las
  esquinas en negro, igual que en cualquier editor: es el propio zoom quien lo evita, no un cálculo
  automático. El filtro se comparte entre el preview en vivo (encadenado en la decodificación, igual que el
  color) y la exportación, así que es el mismo resultado en los dos sitios. Formato de proyecto en versión 8
  (los anteriores se abren sin cambios).
- **Fuentes, filtros y fundidos ✅ entregado.** Tres piezas independientes, compartiendo todas el mismo
  patrón de «un único sitio, usado por igual en preview y exportación» del resto de esta versión:
  - *Fuente* del texto superpuesto: desplegable en el panel *Capa* con las tipografías instaladas
    (`SKFontManager.Default.FontFamilies`, consultadas desde `EditFlow.Engine` porque SkiaSharp no es
    dependencia de la app). `(Predeterminada)` sigue siendo `null` —la de siempre—; con una fuente no
    instalada, Skia cae sola a la del sistema.
  - *Filtros* de aspecto de un clic (blanco y negro, sepia, vintage, viñeta, cálido, frío) en el panel
    *Filtros* del clip seleccionado, con `VisualFilterCatalog` traduciendo cada uno a su fragmento de
    FFmpeg (`hue`, `colorchannelmixer`, `colorbalance`, `vignette`…).
  - *Fundidos* a negro (imagen) y a silencio (el propio audio del clip) en el panel *Efectos*, con
    `FadeFilter` generando `fade`/`afade` a partir de los mismos dos valores; los deslizadores de entrada y
    salida se acotan entre sí para que no se solapen, igual que ya hacían los de un clip de audio.

  Formato de proyecto en versión 9 (los anteriores se abren sin cambios).
- **Animaciones de aparición y desaparición ✅ entregado.** La pieza que se había aplazado, resuelta con
  el mecanismo más simple que sigue siendo una animación de verdad: fundido de opacidad, no
  desplazamiento. `OverlayItem.FadeIn`/`FadeOut` (mismo patrón de acotarse entre sí que ya tenían los de
  un clip) alimentan `FadeFilter.BuildAlpha`, una variante del fundido existente con `alpha=1` en vez de
  fundir a negro —pensada justo para esto: subir o bajar la opacidad de una imagen con canal alfa—, así
  que no hizo falta escribir expresiones de FFmpeg dependientes de `t` en la posición del `overlay`: el
  fundido entra en el mismo punto de la cadena (antes del `setpts` que lo coloca en su instante) que el
  resto de fragmentos por elemento, con un ajuste añadido —un `setpts=PTS-STARTPTS` previo, aparte del que
  ya existía— porque un video superpuesto (a diferencia de un texto o una imagen, un único fotograma con
  marca de tiempo 0 de por sí) no llega garantizado con marca de tiempo exacta cero. El preview en vivo
  calcula la misma curva por su cuenta (`MainWindow.EffectiveOpacity`), ya que ahí los elementos se
  dibujan con Skia y no pasan por este filtro. Se ajusta con dos deslizadores («Aparece en» / «Desaparece
  en») en el panel *Capa*, para texto, imagen o video en una capa por igual. Formato de proyecto en
  versión 10 (los anteriores se abren sin cambios).

  Deslizarse u otro movimiento con el tiempo queda fuera: exigiría expresiones de posición dependientes
  de `t` de verdad, con entrada y salida combinadas en una sola fórmula por tramos —el riesgo que motivó
  aplazar esto en un principio—, y se deja para si hace falta más adelante.
- **Galería de transiciones, miniaturas reales, efectos nuevos y tipografías propias ✅ entregado.**
  Un pase de pulido pedido tras comparar el panel derecho con el de otros editores, con cinco piezas:
  - *Transiciones*: la pestaña izquierda dejó de ser un texto explicativo y pasó a ser una galería de
    verdad con los ocho tipos, que se aplican con un clic al clip seleccionado.
  - *Filtros* con miniaturas reales: cada tarjeta de la galería es un fotograma del propio clip
    seleccionado con ese filtro aplicado (`FrameExtractor` con el fragmento de `VisualFilterCatalog`
    como `-vf`), no solo un botón de texto. Se generan en segundo plano y se cachean por clip y
    filtro, con un buscador para filtrar la lista por nombre.
  - *Efectos* de estilo, aparte de los filtros de color: `VisualEffectKind` (VHS, aberración
    cromática, grano de película, desenfocado, vaporwave), con el mismo patrón que `VisualFilterKind`
    —un fragmento fijo de FFmpeg por clip, sin dependencia del tiempo— y su propia galería con
    miniaturas. Se combinan con el filtro de color si hay uno puesto: los dos fragmentos entran
    seguidos en la misma cadena. El fundido de entrada/salida, que antes vivía en el panel *Efectos*,
    pasó al panel *Filtros* para dejarle sitio a esta galería nueva. Quedan fuera de este primer
    paquete el zoom, el giro, el flash o los pulsos (piden expresiones dependientes de `t`, el mismo
    riesgo que las animaciones de posición) y la pantalla verde (pide combinar con otra fuente, no es
    un fragmento de filtro).
  - *Volumen de un video en una capa*: se ajusta ahora en la pestaña *Audio*, como el de cualquier
    otro clip, en vez de tener su propio deslizador dentro del panel *Capa*.
  - *Importar tipografía propia*: un botón junto al desplegable de fuentes abre el selector de
    archivos del sistema para un `.ttf`/`.otf` cualquiera; `TextStyle.FontFilePath` le da prioridad
    sobre la tipografía instalada elegida (`SKTypeface.FromFile` en vez de `FromFamilyName`). Si el
    archivo desaparece, el texto cae solo a la tipografía del sistema en vez de perderse.

  Formato de proyecto en versión 11 (los anteriores se abren sin cambios). Aplazado, y a propósito:
  importar LUTs `.cube` —ya estaba en la lista de cosas avanzadas de color pendientes desde la Fase 2,
  y el riesgo real (escapar rutas de Windows dentro de la sintaxis de filtro de FFmpeg, con las
  comillas y los dos puntos después de la letra de unidad) no es de los que conviene resolver deprisa—
  y «paquetes de transiciones» importables, que no tienen un equivalente real en un modelo de
  transiciones paramétrico como `xfade` en vez de archivos de plantilla.

- **Audio profesional ✅ entregado.** No estaba en el alcance original de esta versión: es el primer
  bloque de la lista de paridad con Premiere (`docs/PARIDAD-PREMIERE.md`), y entró aquí porque ya
  estaba terminado cuando se cortó el release. Es el bloque más parecido a Filtros/Efectos: son
  filtros de FFmpeg ya listos, a los que solo les faltaba interfaz. `AudioEffectKind` (voz clara,
  quitar ruido, compresor, limitador, reverb, coro, normalizar volumen) y un balance estéreo
  (`Clip.Pan`/`AudioClip.Pan`, filtro `stereotools`), en el panel *Audio*, para el propio audio de un
  clip de video o de un clip de una pista de audio —no para un video superpuesto en una capa, que ya
  tampoco tiene fundidos propios, por la misma razón—. Aquí no hizo falta duplicar nada entre preview
  y exportación: los dos comparten literalmente el mismo grafo
  (`FilterGraphBuilder.BuildAudioOnly`, que ya usaba `PreviewMixRenderer`), a diferencia del video,
  donde sí hay un filtro de decodificación aparte del de exportación. Formato de proyecto en versión
  12 (los anteriores se abren sin cambios).

  Deliberadamente no es un mezclador completo: sin automatización por volumen con keyframes (que
  depende del bloque de keyframes genéricos, todavía sin construir), y con un preset de un clic en
  vez de un ecualizador paramétrico de bandas ajustables a mano.

### v0.6.0 — Paridad con Premiere (siguiente)

El trabajo a partir de aquí sale de `docs/PARIDAD-PREMIERE.md`: lo que ese documento marca como
alcanzable y todavía no se ha construido. Se aborda por bloques, no todos a la vez —varios
(máscaras/chroma key, keyframes genéricos) son cambios de arquitectura, no un panel más— y este
apartado se va ampliando según se entrega cada uno. El primero, audio profesional, ya salió con
v0.5.0.

**Entregado:**

- ✅ **Chroma key (pantalla verde)** en los videos de una capa: `chromakey` + `despill`, con color,
  tolerancia y suavizado del borde. Se aplazó en la ronda de Efectos porque, a diferencia de un
  filtro por clip, pide componerse con otra fuente; el trabajo real estuvo en los dos caminos del
  preview, no en el grafo de exportación:
  - El fotograma que se ve con el cabezal **parado** salía como JPEG, que no tiene canal alfa: se
    escribe como PNG cuando hay recorte. Un JPEG habría devuelto el fondo entero, tapando lo de abajo.
  - El decodificador **en vivo** de las capas entrega BGRA **premultiplicado** a Avalonia, pero
    `chromakey` deja alfa recto: los píxeles recortados conservaban su verde con alfa 0 y dejaban un
    velo verdoso. Se cierra la cadena con `premultiply=inplace=1`.
  - Y la trampa de fondo, que solo salió al mirar los píxeles: `chromakey` mide la distancia de color
    sobre los **planos de croma**, así que hay que dárselo en `yuva444p`. Encadenado tras un
    `format=rgba` —que era lo natural, porque el canal alfa tiene que existir antes— compara canales
    que no son los que cree y recorta de más: un azul saturado salía con un 75 % de opacidad. El
    fragmento pasó a llevar sus dos conversiones dentro y a terminar en `rgba`, de modo que hay un
    único sitio que sabe en qué formato trabaja el filtro.
  - En la **exportación** el fragmento ocupa el sitio del `format=rgba` que la rama de la capa ya
    hacía, antes de la opacidad y del `overlay`.
  - Las máscaras de forma (rectángulo, elipse, trazado) quedan pendientes: son otra cosa, y encajan
    mejor sobre los keyframes.

**Sin empezar:**

- **Keyframes genéricos** de posición, escala y opacidad. Es el que desbloquea lo demás: animaciones
  de texto con movimiento, zoom progresivo y automatización de volumen dependen de él.
- **Color avanzado**: curvas RGB, ruedas de color, HSL secundario, LUTs `.cube` y scopes.
- **Edición basada en texto**: borrar palabras desde el transcript, quitar silencios y generar un
  *rough cut*. El propio documento de paridad lo marca como de lo más valioso del catálogo.
- **Gestión de proyectos**: bins y subcarpetas, etiquetas de color, *relink* y *replace footage*.

---

## 10. Verificación

### Tests automatizados

Corren con `dotnet test`, sin necesidad de FFmpeg ni de entorno gráfico, en CI sobre Windows y Linux:

- **`FilterGraphBuilder`**: snapshot de los argumentos generados para 1 clip, 3 clips, clip sin audio, mezcla de resoluciones y video rotado 90°.
- **`FFmpegArgumentBuilder`**: un test por celda de la tabla de control de tasa; en particular, que NVENC en modo CQ emita siempre `-b:v 0`.
- **`EncoderDetector`**: parseo contra un fixture con salida real de `ffmpeg -encoders`.
- **`TimelineOps`**: división en el borde exacto de un clip, recorte más allá del material origen, eliminación con desplazamiento.

### Verificación manual de extremo a extremo (al cerrar la Fase 1)

1. `dotnet run --project src/EditFlow.App`
2. Importar **tres videos deliberadamente distintos**: uno 1080p/30fps, uno 4K/60fps y uno **vertical de móvil sin pista de audio**. Este trío revienta cualquier grafo mal construido.
3. Dividir el segundo por la mitad (S), eliminar una mitad, mover el tercero al inicio.
4. Comprobar que el preview salta correctamente entre los cortes.
5. Exportar 1080p con **`h264_nvenc`**: la barra debe avanzar, la velocidad superar 5x y el archivo reproducirse completo con audio sincronizado.
6. Exportar lo mismo con **`libx264`**: notablemente más lento, resultado equivalente.
7. Exportar con **`av1_nvenc`** a 4K y confirmar con `ffprobe` que el códec de salida es AV1.
8. Probar los tres modos de control de tasa y confirmar con `ffprobe` que el bitrate real se aproxima al solicitado.
9. Cancelar una exportación a mitad: el proceso debe terminar y no dejar archivo parcial.
10. Guardar el proyecto, cerrar la aplicación y reabrirla: la timeline debe restaurarse idéntica.

---

## 11. Riesgos conocidos

| Riesgo | Mitigación |
|---|---|
| ~~El `VideoView` de LibVLCSharp arrastra limitaciones conocidas~~ **COMPROBADO — ver sección 13** | El reproductor vive detrás de `IPreviewPlayer`. La Fase 1 coloca los controles **debajo** del preview. La Fase 2 obligará a cambiar de renderer. |
| Avalonia queda fijado en 11.3.x por la dependencia de LibVLCSharp | Aceptado conscientemente: 11.3 es una rama madura y con soporte. La migración a 12 se abordará cuando LibVLCSharp la soporte, o al cambiar de renderer. |
| Los binarios de FFmpeg (~90 MB) no deben entrar al repositorio | `.gitignore` más `tools/fetch-ffmpeg.ps1` con verificación SHA-256. CI los descarga con caché. |
| Línea de comandos demasiado larga con muchos clips | `-filter_complex_script` desde archivo temporal. |
| `-ss` antes de `-i` puede desincronizar el audio en ciertos contenedores | Si se manifiesta, mover el recorte a los filtros `trim`/`atrim` con `setpts`/`asetpts`: más lento, pero exacto al fotograma. |

---

## 12. Distribución

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishTrimmed=true
```

Resultado aproximado: aplicación ~70 MB más FFmpeg ~90 MB. Linux (`linux-x64` → AppImage) y macOS (`osx-arm64` → `.app`) se generan desde el mismo código, cambiando únicamente el RID y los binarios correspondientes de `tools/ffmpeg/`.

---

*EditFlow · MIT © 2026 maniadiaz*

---

## 13. RESUELTO: el `VideoView` no admitía controles superpuestos

> **Estado: resuelto.** Se sustituyó el `VideoView` por una superficie propia que dibuja
> fotogramas decodificados por EditFlow en un control normal de Avalonia. La misma bandera
> magenta que antes era invisible sobre el video ahora se ve. Con ello quedan desbloqueadas
> la previsualización de texto, la de color y los controles superpuestos sobre el preview.
>
> LibVLC se conserva únicamente como **reloj y salida de audio**, con `--no-video`, de modo
> que ya no crea ninguna ventana nativa. El problema era el `VideoView`, no el audio.

### Cómo era el problema

**Fecha de la comprobación**: 2026-09-20 · **Veredicto**: la limitación es real.

### Cómo se comprobó

Se colocaron dos `Border` magenta **idénticos** en la misma ventana: uno centrado sobre
el `VideoView` reproduciendo, y otro sobre el panel de medios, que es contenido normal
de Avalonia. Mismo estilo, mismo código, misma ventana; la única diferencia es qué hay
debajo.

| Bandera | Situada sobre | Resultado |
|---|---|---|
| A | `VideoView` reproduciendo | **Invisible** |
| B | Contenido normal de Avalonia | **Visible** |

Una primera versión de la prueba colocó una sola franja cruzando el borde inferior del
área de video, esperando ver la mitad de fuera. No se vio ninguna de las dos mitades,
porque el panel de la timeline se dibuja después y tapaba la parte que sobresalía. El
resultado fue ambiguo hasta rehacer la prueba con dos banderas comparables.

### Qué sí funciona

- El `VideoView` **sí** funciona dentro de una celda de un `Grid`, no solo ocupando la
  ventana entera. La limitación que circula sobre eso no se reproduce en la versión 3.10.1.
- Reproduce el archivo y respeta el layout que lo rodea.

### Qué no funciona

- Cualquier control de Avalonia dibujado sobre el área del reproductor queda oculto. Es
  el problema clásico de *airspace*: el `VideoView` es una ventana nativa hija que se
  dibuja por encima de todo lo que ocupe su región.

### Consecuencias

| Fase | Impacto |
|---|---|
| **Fase 1** | **Ninguno.** Los controles de reproducción van **debajo** del preview, como en Premiere o DaVinci Resolve, no flotando sobre él. |
| **Fase 2** | **Bloqueante.** No se puede previsualizar texto superpuesto sobre el video, que es justamente el punto de la funcionalidad. |
| **Fase 3** | **Bloqueante.** Sin controles superpuestos no hay tiradores de recorte ni de zoom sobre el preview. |

### Decisión

Se mantiene LibVLCSharp para la Fase 1: resuelve video, audio y sincronía sin escribir
código, y la restricción de los controles no afecta a lo que la Fase 1 necesita.

La Fase 2 **empieza sustituyendo el reproductor** por un renderer propio que decodifique
con FFmpeg hacia un `WriteableBitmap`. Al pintar los fotogramas en un control de Avalonia
normal, la superposición deja de ser un problema y el preview de texto puede compartir el
código de dibujo de SkiaSharp con la exportación.

Esa sustitución es exactamente para lo que existe `IPreviewPlayer`. El coste añadido es
la salida de audio, que LibVLC daba gratis y habrá que resolver con otra biblioteca.

