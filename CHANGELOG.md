# Changelog

Todos los cambios relevantes de EditFlow se documentan en este archivo.

El formato sigue [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto se adhiere a [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

### Added

- **El reloj del preview muestra milisegundos** (`00:08.266 / 00:15.000`), truncados para no marcar un
  cuadro que aún no toca. Al pasar el ratón por encima dice qué número de cuadro es y cuánto dura
  cada uno (a 30 fps, 33,3 ms).
- **Subir un clip a una capa superior** (botón ↑ junto a la papelera, o la tecla **U**). Divide el
  video con **S**, selecciona el trozo y súbelo: pasa a una capa de video sobre la pista principal,
  con su sonido, y en la pista principal queda un **hueco** (tiempo en negro) para que nada se
  corra de sitio. El trozo ocupa el cuadro como antes y desde el panel *Capa* (o arrastrándolo en
  el preview) se reduce, se coloca y se le baja la opacidad; en la timeline se mueve y se recorta
  por los bordes sin salirse del material del archivo. Se exporta compuesto sobre lo que haya
  debajo y su sonido entra en la mezcla. Deshacer devuelve el clip a su sitio. Los huecos se
  dibujan con borde discontinuo y se guardan en el proyecto (formato `.editflow` 4; los proyectos
  anteriores siguen abriéndose).
  - En el preview, parado se ve el fotograma nítido de la capa; reproduciendo sin copia de preview
    se mueve a pocos fotogramas por segundo, y con *Render* se ve de corrido porque ya va
    compuesto.
- **Copia de preview (render de previsualización)**, botón *Render* junto a las tijeras. Renderiza el
  montaje por tramos de 5 s, en segundo plano, con textos e imágenes ya compuestos, a la
  resolución de reproducción elegida y con un códec ligero de decodificar. Una franja bajo la
  regla muestra el estado de cada tramo: **verde** renderizado, **amarillo** renderizando o en
  cola, **rojo** necesita render. Reproduciendo, los tramos listos se ven de corrido sin decodificar
  los originales (se encadenan como un solo video, sin cortes entre tramos); parado se vuelve al
  original, nítido y con los textos como capas movibles. Al editar solo se invalidan los tramos
  afectados (la huella de cada tramo incluye clips, recortes, textos, imágenes y ajustes), y
  deshacer recupera la copia anterior sin volver a renderizar. Los archivos son temporales (2 GB
  como máximo, se borran los menos usados), nunca sustituyen a los originales y no intervienen en
  la exportación. Clic derecho en el botón: limpiar copias. Independiente de la resolución de
  reproducción y de la decodificación por GPU, y combinable con ambas.
- **Resolución de reproducción** junto a los botones de reproducir (Completa, 1/2, 1/4, 1/8,
  1/16), como en Premiere. Es una fracción de la resolución del propio video: un 4K a 1/2 se
  decodifica en 1920×1080 y a 1/4 en 960×540, con mucha menos carga de CPU y GPU. Solo afecta a
  lo que se ve en el preview; ni los originales ni la exportación se tocan. Nunca cuesta más que
  «Completa», se combina con la decodificación por GPU, se recuerda entre sesiones y junto al
  selector se ve el tamaño real con el que se está decodificando.
- **Exportar dividido en partes**: se puede pedir un video cada X segundos o minutos. Un montaje
  de 3 minutos en partes de 1:30 genera 2 videos; uno de 6 minutos en partes de 1:30, 4. Salen
  como `nombre_001.mp4`, `nombre_002.mp4`… y el diálogo dice antes de exportar cuántos serán, cuánto
  dura cada uno y cómo se llamarán. Se codifica una sola vez y el corte cae en el segundo pedido
  (verificado con libx264, libx265 y NVENC, en MP4 y MKV). Cancelar borra solo las partes de esa
  exportación, nunca archivos anteriores de la misma carpeta.
- **Diálogo de exportación más completo**: ajustes predefinidos (YouTube 1080p y 4K, Instagram /
  TikTok vertical, WhatsApp pequeño, Máxima calidad), casilla *Vertical* (1080×1920), *Incluir
  audio*, contenedor MP4 / MKV / MOV con la extensión del archivo sincronizada, *Optimizar para
  web* y un resumen con duración, formato, audio, tamaño estimado y nombre(s) de salida. Al
  terminar lista los archivos generados.
- **Arrastrar textos e imágenes directamente sobre el preview**: se agarran con el ratón y se
  sueltan donde se quieran, con un imán al centro del video (con guías amarillas) y un contorno
  punteado en el elemento seleccionado. Es una sola entrada en el historial y se puede deshacer.
  El panel *Capa* y el deslizador de posición siguen disponibles para ajustes finos.
- **La timeline sigue al cabezal**: al reproducir, cuando el cabezal llega al borde derecho de
  la vista, esta pasa página y el cabezal reaparece cerca del borde izquierdo. También al saltar
  con los botones o las teclas a un punto fuera de la vista. Al arrastrar el cabezal con el ratón
  la vista no se mueve.

### Changed

- La posición del cabezal y el tamaño de la vista pasan a la fila de arriba del preview, para que
  el transporte, las herramientas y la resolución de reproducción quepan con el panel derecho abierto.

- **Preview nítido y a la velocidad del video.** Antes se decodificaba siempre a 854×480 y se
  estiraba al panel (un panel de casi 1800 píxeles mostraba una imagen de 480p, de ahí lo
  borroso), y siempre a 30 fotogramas por segundo, tirando la mitad de los de un video a 60.
  Ahora se decodifica a la altura que ocupa el preview en pantalla (360, 480, 720 o 1080,
  contando el escalado del monitor, sin pasar de lo que tiene el video) y a la velocidad del
  propio video, hasta 60.
- **Decodificación por la tarjeta gráfica** (NVDEC, D3D11VA…) en videos de 1080p o más, con
  reintento automático por software si el códec o el equipo no la admiten. Se puede desactivar
  con la variable `EDITFLOW_NO_HW=1`. Medido con un 1440p a 60 fps: 60 fotogramas por segundo
  estables y el decodificador de la GPU al 8–10 %.
- **Copia ligera solo para saltar.** La copia de 480p seguía siendo lo que se veía al
  reproducir. Ahora, reproduciendo, se usa siempre el original; con la reproducción parada se
  usa la copia para que arrastrar el cabezal sea rápido, y cuando el cabezal se detiene la
  imagen pasa al original a calidad completa.
- Los textos del preview se dibujan a 1080 y se reducen al lienzo, para que la letra no se vea
  pixelada en un preview grande; las superposiciones se colocan sobre un lienzo abstracto que ya
  no depende de la resolución de decodificación.
- El temporizador de Windows pasa de 15,6 ms a 1 ms mientras la aplicación corre: a 60 fps un
  fotograma dura 16,7 ms y con la resolución por defecto se entregaban a tirones.

### Fixed

- **La reproducción iba a unos 4 fotogramas por segundo.** Medido: LibVLC solo actualiza la
  posición del audio cada 256 ms, y el video —que sigue a ese reloj— esperaba a cada
  actualización y mostraba sus fotogramas en ráfagas, aunque se decodificaran cientos por
  segundo. Ahora el reloj se interpola entre lecturas (con un cronómetro, corrigiéndose
  suavemente con cada dato nuevo y de golpe tras un salto): 30 fotogramas por segundo estables
  con un video de 1080p a 60 fps, con la posición del audio avanzando 1,0 s por segundo.
- **Arrastrar el cabezal congelaba la imagen.** Cada movimiento del ratón lanzaba un salto
  nuevo sin esperar al anterior: se apilaban decenas de FFmpeg y el video iba cada vez más
  retrasado respecto al ratón, además de que operaciones simultáneas se pisaban entre sí. Ahora
  hay una sola operación a la vez y, mientras se atiende, solo se recuerda la última petición.
  Con 60 movimientos en un segundo se muestran unos 17 fotogramas y se acaba exactamente donde
  se soltó.
- El audio ya no se recoloca en cada movimiento del ratón al arrastrar con la reproducción
  parada; se aplica al reproducir.

### Added

- **Texto e imágenes sobre el video (capas de superposición)**. La pestaña *Texto* añade un
  título, un subtítulo o un texto sencillo en el cabezal (5 s), y *Superponer imagen…* un
  logotipo o cualquier imagen. Cada uno vive en una **capa** dibujada sobre la pista de video,
  con posición propia en la timeline: aparecer o desaparecer no desplaza nada. En la timeline se
  arrastran para moverlos, se recortan por los bordes con imán, se ocultan o bloquean por capa
  y se eliminan con Supr o clic derecho. Las capas se apilan: la última creada queda delante.
- **Panel *Capa*** (se abre solo al seleccionar un texto o una imagen): contenido del texto,
  tamaño, color (paleta o `#RRGGBB`), negrita, cursiva y sombra; posición horizontal y vertical,
  opacidad, ancho de la imagen y cuándo empieza y cuánto dura. Los deslizadores se aplican al
  soltar, el texto al salir del cuadro, y todo se deshace.
- **Lo que se ve es lo que se exporta**: el texto lo dibuja SkiaSharp a PNG con fondo
  transparente y esa misma imagen se muestra en el preview y se compone con `overlay` en la
  exportación, al alto exacto del video de salida (nítido en 4K, sin ampliar una imagen de 480p).
  El tamaño de la letra es una fracción del alto del video, así que no cambia de aspecto al
  exportar a otra resolución. Un título que dura más que el video lo extiende con negro, igual
  que una música larga.
- Los proyectos pasan al **formato 3** para guardar las capas; los anteriores se abren sin ellas.
  Una imagen que ya no existe se avisa y se omite en lugar de impedir abrir el proyecto.

## [0.3.0] - 2026-09-21

Interfaz nueva —pantalla de inicio y editor reorganizado— y una timeline con las herramientas de
edición fina que se esperan de un editor: recortar audio, forma de onda, miniaturas en los clips y
*roll*, *slip* y *slide*. Las varias pistas de video con superposición pasan a la 0.4.0, junto
al texto y las transiciones, que necesitan la misma composición por capas.

### Added

**Inicio y proyectos**

- **Pantalla de inicio**: barra lateral con *Inicio* y *Plantillas*, acceso destacado para
  **crear un nuevo proyecto**, otro para abrir un archivo, y debajo los proyectos guardados como
  tarjetas con portada, número de clips, duración y cuándo se usaron. Sin proyectos muestra
  «Empieza con un Nuevo Proyecto». Clic derecho sobre una tarjeta: abrir o quitar de la lista (el
  archivo del proyecto no se toca). *Plantillas* queda como página vacía hasta que existan.
- La lista de recientes y las portadas se guardan en los datos locales del usuario, nunca junto al
  proyecto; los proyectos cuyo archivo ya no existe se descartan solos y las portadas llevan un
  nombre derivado de un hash de la ruta.
- **Aviso de cambios sin guardar** al volver al inicio, crear o abrir otro proyecto y cerrar la
  ventana: guardar, no guardar o cancelar. Hasta ahora esas acciones tiraban el trabajo sin
  preguntar.

**Editor**

- **Interfaz rehecha** con la estructura de los editores de referencia, a nuestro estilo: columna
  de pestañas a cada lado, panel de medios a la izquierda, preview con deshacer/rehacer encima y el
  transporte centrado debajo, timeline con zoom (acercar, alejar, ajustar todo) y panel de
  propiedades a la derecha que se abre y cierra desde su pestaña. Las pestañas Texto,
  Transiciones, Filtros, Efectos, Color y Velocidad están a la vista, marcadas como próximamente.
- **Panel de medios con miniaturas**: cada archivo es una tarjeta con su fotograma y su duración.
  Un clic lo selecciona y muestra sus datos; doble clic, o el botón «+», lo añade a la timeline
  (los audios, en el cabezal).
- **Panel de audio** del clip seleccionado: volumen de −40 a +12 dB con deslizador, restablecer,
  silenciar, separar el audio del video y, en clips de audio, fundidos de entrada y salida. Los
  cambios se aplican al soltar el deslizador, de modo que arrastrarlo deja una sola entrada en el
  historial.
- Botones para dividir y eliminar junto al transporte, nombre del proyecto (con • si hay cambios)
  en la barra superior y barra de estado de una línea, con el texto completo al pasar el ratón.

**Timeline**

- **Recortar clips de audio arrastrando sus bordes**, con imán a cortes, cabezal y clips vecinos y
  vista previa mientras se arrastra. Recortar por el inicio conserva el audio en su sitio. Un
  recorte que chocaría con otro clip, pediría más audio del que tiene el archivo o dejaría el clip
  por debajo de 40 ms se rechaza y el clip vuelve a su tamaño.
- **Dividir clips de audio** con S (o desde su menú de clic derecho) cuando hay uno seleccionado.
  La segunda mitad hereda volumen y silencio; el fundido de entrada se queda en la primera y el de
  salida pasa a la segunda, sin inventar fundidos en el corte. Se deshace con los fundidos exactos.
- **Forma de onda** en los clips de audio: se extrae una vez por archivo a 100 picos por segundo,
  en segundo plano, se guarda en disco y se dibuja por columnas visibles con el máximo de cada una,
  de modo que los golpes no se pierden al alejar el zoom. Refleja el volumen del clip.
- **Miniaturas dentro de los clips de video**: un fotograma cada 2 s, generado de una pasada en
  segundo plano con dos hilos (de la copia de edición si existe) y guardado en disco. La tira se va
  llenando a medida que FFmpeg avanza; las carpetas sin uso en 30 días se limpian.
- **Herramientas de edición fina en la pista de video**, con Alt pulsado: **mover corte** (Alt +
  arrastrar el borde entre dos clips), **deslizar contenido** o *slip* (Alt + arrastrar un clip:
  cambia qué parte del archivo se ve) y **deslizar clip** o *slide* (Alt + Mayús + arrastrar: lo
  mueve entre sus vecinos sin tocar su contenido). Las tres conservan la duración total, así que la
  música y lo demás no se desplazan; se acotan al material disponible y muestran vista previa
  mientras se arrastra. La barra de estado las recuerda al pulsar Alt sobre la timeline.

### Changed

- Paleta y estilos centralizados en `App.axaml` (tema oscuro fijo, un solo color de acento). Los
  atajos de edición ya no actúan mientras se ve la pantalla de inicio.
- Las pruebas se ejecutan en serie: las de integración miden tiempos reales de reproducción y
  fallaban de vez en cuando al competir por la CPU.

### Fixed

- Cancelar una exportación podía dejar el archivo a medias: FFmpeg tarda unos milisegundos en
  soltar el archivo y el borrado se intentaba una sola vez. Ahora se reintenta hasta tres segundos.

## [0.2.0] - 2026-09-20

Base técnica del editor: proyectos guardados, reproductor propio, timeline multipista con
audio mezclado, copias de edición para que ir y venir por el video no se atasque en un equipo
de 8 GB, y el paso del proyecto a GPL-3.0.

### Changed

- **EditFlow pasa de MIT a GPL-3.0-or-later.** El objetivo es que el proyecto sea 100 %
  libre y gratuito, sin componentes de pago ni SDK con licencia. Empaquetar la build
  completa de FFmpeg —con x264 y x265, ambos GPL— junto a código MIT era una zona gris
  legal; con GPL-3.0 desaparece, y además garantiza que el proyecto siga libre para quien
  venga después. Es la misma elección que Shotcut y Kdenlive.
- Todo archivo de código lleva cabecera SPDX de dos líneas.
- **El `VideoView` de LibVLCSharp queda sustituido por una superficie propia.** Con ello
  se resuelve el bloqueo documentado en la sección 13 del plan: ya se pueden superponer
  controles sobre el preview, que era requisito de la previsualización de texto, la de
  color y el menú contextual.

### Added

**Proyectos**

- Guardar y abrir proyectos en archivos `.editflow` (formato 2; los del formato 1 se siguen
  abriendo). El proyecto guarda qué archivos se usaron y qué intervalo de cada uno se
  reproduce, no el video: un montaje de una hora ocupa unos pocos kilobytes.
- Los proyectos guardan la ruta absoluta y la relativa de cada medio, de modo que mover la
  carpeta entera —a otro disco o a otro equipo— no rompe el montaje. Un medio que ya no
  existe se informa al abrir, en vez de impedir abrir el proyecto.
- Atajos de proyecto: Ctrl+N, Ctrl+O, Ctrl+S y Ctrl+Mayús+S. El título de la ventana muestra
  el nombre del proyecto y si hay cambios sin guardar.
- Abrir un `.editflow` desde la línea de comandos carga el proyecto; cualquier otro archivo
  se importa como medio.

**Reproducción**

- Botones −30 s, −5 s, reproducir/pausar, +5 s y +30 s, con atajos ←/→ y Mayús+←/→, más
  Inicio y Fin. El cabezal sigue la reproducción y encadena los clips.
- Decodificador de video propio: FFmpeg produce fotogramas BGRA crudos que se dibujan en un
  control normal de Avalonia, con una reserva de fotogramas de memoria acotada (46 MB para
  30 fotogramas a 480p) y un reloj medido contra cronómetro.
- Sincronización con reloj maestro y descarte de fotogramas retrasados. `AudioClock` usa
  LibVLC en modo solo audio, sin ventana nativa.
- **El preview reproduce el audio completo del montaje**: música, efectos, volúmenes,
  fundidos y silencios suenan igual que en la exportación, porque se renderiza con el mismo
  grafo (sin la parte de video) a un único archivo FLAC que hace de reloj maestro. Se acabó
  el pequeño hueco en cada corte y el tope de +6 dB de LibVLC. La mezcla se vuelve a preparar
  unos 500 ms después de la última edición y mientras tanto el video sigue en silencio.
- **Copias de edición automáticas (proxies)**: al importar o abrir un proyecto, los videos
  por encima de 720p —y los de códecs pesados como HEVC, AV1 o ProRes— reciben en segundo
  plano una copia de 480p pensada para buscar rápido (un fotograma clave cada 12, sin
  fotogramas B, sin audio, tiempos idénticos al original). El preview cambia a ella solo
  cuando está lista; la exportación sigue usando siempre el original. Se generan de una en
  una con dos hilos para no ahogar un equipo de 8 GB, viven en la caché del usuario (nunca
  junto al proyecto), se invalidan solas si el original cambia y se limpian a 5 GB borrando
  las menos usadas.

**Timeline y audio**

- Timeline multipista: pista de video y pistas de audio con clips de posición libre, volumen,
  fundidos de entrada y salida, silenciar, solo y bloquear. Cabeceras **pegadas al borde
  izquierdo** aunque se desplace en horizontal; la timeline crece con el número de pistas.
- Arrastrar un clip de audio con **imán** a los bordes cercanos (cortes del video, cabezal,
  origen y bordes de otros clips). Soltar sobre otro clip lo rechaza en lugar de
  superponerlos. **Reordenar las pistas** arrastrando su cabecera.
- Importar audio (mp3, wav, aac, m4a, flac, ogg, opus) a una pista, en la posición del
  cabezal.
- **Separar el audio de un clip de video** a su propia pista, con deshacer. El clip de video
  deja de aportar su sonido para que no se oiga duplicado, y la segunda mitad de un clip
  cortado hereda ese estado.
- **Volumen y silencio por clip de video**: subir y bajar de 3 en 3 dB, restablecer y
  silenciar sin necesidad de separar el audio.
- **Menú de clic derecho** sobre un clip de video (dividir, separar audio, volumen, silenciar,
  eliminar), sobre un clip de audio (volumen, silenciar, fundidos, eliminar) y sobre una pista
  (añadir, silenciar, solo, bloquear, eliminar).
- La exportación mezcla las pistas de audio: desfase por posición, volumen del clip y de la
  pista, fundidos medidos desde el inicio del propio clip, y `normalize=0` para que añadir una
  música no baje el nivel del resto. Si la música dura más que el video, la imagen se extiende
  con negro hasta el final.

### Fixed

- Saltar en el video con la CPU cargada podía dejar el reproductor sin decodificador: al
  detenerlo se cerraba el lector antes de cancelar el bucle, que lo encontraba cerrado y
  fallaba en vez de parar con normalidad. Ahora se cancela primero.
- Abrir un proyecto dejaba el preview en negro hasta pulsar algo.

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

[Unreleased]: https://github.com/maniadiaz/EditFlow/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/maniadiaz/EditFlow/releases/tag/v0.3.0
[0.2.0]: https://github.com/maniadiaz/EditFlow/releases/tag/v0.2.0
[0.1.1]: https://github.com/maniadiaz/EditFlow/releases/tag/v0.1.1
[0.1.0]: https://github.com/maniadiaz/EditFlow/releases/tag/v0.1.0
