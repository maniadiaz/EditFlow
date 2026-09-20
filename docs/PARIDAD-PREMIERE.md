# Paridad con Premiere Pro — qué entra, qué no y por qué

Este documento clasifica el catálogo de funciones de Adobe Premiere Pro según lo que
EditFlow puede alcanzar de verdad. Son alrededor de **800 funciones**.

**Paridad literal no es el objetivo y no sería honesto prometerla**: Premiere lleva tres
décadas y cientos de ingenieros. El objetivo es el 90 % que se usa a diario.

La clasificación importa más que el total, porque buena parte de la lista **no es cara**:
FFmpeg ya implementa el algoritmo y lo que falta es la interfaz. `Ultra Key` es el filtro
`chromakey`; el compresor de audio es `acompressor`; la reducción de ruido es `arnndn`.
Donde aparece un filtro en la columna de la derecha, el trabajo es de interfaz, no de
inventar nada.

## Resumen

| Nivel | Qué significa | Proporción aproximada |
|---|---|---|
| ✅ **Alcanzable** | FFmpeg, SkiaSharp o lógica propia lo cubren | ~70 % |
| 🟡 **Posible, caro** | Requiere una biblioteca pesada o mucho trabajo | ~10 % |
| ❌ **Fuera de alcance** | Servicios de Adobe, modelos de frontera o SDK con licencia | ~20 % |

---

## ✅ Alcanzable

### 1. Gestión de proyectos y medios

Bins y subcarpetas, etiquetas de color, metadatos, importación de archivos y carpetas,
secuencias de imágenes, **relink y offline media**, *replace footage*, **flujo de proxy**,
consolidar y archivar proyectos, media cache y su limpieza, interpretación de material.

Ya construido: crear, abrir y guardar proyectos, e importación con lectura de metadatos.

### 2. Formatos

Vía FFmpeg, sin trabajo adicional: MP4, MOV, AVI, MXF, MPEG, WebM, M4V, WMV, **ProRes**,
**DNxHD/DNxHR**, H.264, H.265, MPEG-2, XAVC.

*Excepciones en la sección de fuera de alcance.*

### 3. Timeline

Multipista de video y audio, secuencias anidadas, y el juego completo de herramientas de
recorte: **ripple, roll, slip, slide**, lift, extract, overwrite, insert, replace. Snap,
marcadores de clip y de secuencia, puntos de entrada y salida, *track targeting*, bloquear,
ocultar, silenciar y aislar pistas, activar y desactivar clips, capas de ajuste.

Es lógica de modelo, no de video: no depende de FFmpeg en absoluto.

### 4. Edición de video

| Función | Cómo |
|---|---|
| Crop, resize, scale, position, rotation, anchor point | `crop`, `scale`, `rotate`, `pad` |
| Opacity y modos de fusión | `blend` |
| Velocidad y *speed ramping* | `setpts` + `atempo` |
| Reverse | `reverse` + `areverse` |
| Freeze frame y frame hold | `trim` + `loop` |
| Interpolación de fotogramas | `minterpolate` (aproxima *Optical Flow*) |
| Link/unlink, agrupar, anidar | Lógica de modelo |

### 6. Keyframes y animación

Keyframes de posición, escala, rotación, opacidad y punto de anclaje, con interpolación
lineal, bezier, hold y *ease*. Es modelo y curvas de interpolación; el grafo de FFmpeg
recibe el valor ya calculado por fotograma.

### 7. Máscaras · 9. Chroma key

Máscaras de elipse, rectángulo y pluma, con *feather*, expansión, inversión y keyframes.
Chroma key con `chromakey`, `colorkey` y `despill` para la supresión de derrame.

### 10–11. Color y herramientas profesionales

Todo el panel Lumetri: balance de blancos, temperatura, tinte, exposición, contraste,
luces, sombras, blancos, negros, saturación, *vibrance*; **curvas RGB por canal**,
**curvas Hue vs Hue/Sat/Luma**, **ruedas de color** para sombras, medios y altas,
**HSL secundario**, LUTs de entrada y creativos, *look presets*.

Scopes: **forma de onda, vectorscopio, RGB parade e histograma**, calculados desde los
fotogramas ya decodificados.

Filtros base: `eq`, `colorbalance`, `curves`, `colorchannelmixer`, `hue`, `lut3d`,
`colorlevels`, `selectivecolor`.

### 12. Efectos

Blur gaussiano y direccional, *sharpen* y *unsharp*, ruido y grano, distorsión, transform,
warp, lente, espejo, flip, posterize, mosaico, estroboscópico, y los grupos de estilizado,
generación, keying, control de imagen, perspectiva y tiempo.

Filtros: `gblur`, `boxblur`, `unsharp`, `noise`, `lenscorrection`, `perspective`,
`hflip`, `vflip`, `pixelize`, `edgedetect`, `vignette`.

### 13. Transiciones

Cross dissolve, *dip to black* y *to white*, *film* y *additive dissolve*, push, slide,
wipe, zoom, iris y sus variantes: los 50+ modos del filtro `xfade`.

*Morph Cut queda fuera.*

### 14. Texto y títulos

Capas de texto múltiples, fuente, tamaño, peso, cursiva, alineación, *tracking*, *leading*,
*kerning*, color, contorno, sombra, fondo, formas, y animación de posición, escala y
opacidad. Plantillas propias equivalentes a los MOGRT.

Se renderiza con **SkiaSharp**, que ya viene con Avalonia, y se compone con `overlay`. El
mismo código dibuja el preview y la exportación, así que lo que se ve es lo que sale.

### 15–16. Subtítulos y edición basada en texto 🟢

**Transcripción automática, subtítulos automáticos y edición por texto son alcanzables**,
y conviene subrayarlo porque suele darse por hecho que exigen un servicio de pago:
[`whisper.cpp`](https://github.com/ggerganov/whisper.cpp) transcribe **en local**, sin
nube, sin cuenta y sin coste por minuto.

Eso habilita: transcripción, subtítulos editables y exportables a SRT, sincronización,
estilos, búsqueda de palabras dentro del video, **eliminar palabras borrándolas del
transcript**, eliminar silencios y generar un *rough cut* desde el texto.

Es de lo más valioso de todo el catálogo.

### 17–20. Audio

Mezclador de pistas y de clips, automatización de volumen con keyframes, pan y balance,
mono, estéreo y multicanal, ganancia, normalización, transiciones de audio, y el conjunto
de efectos profesionales.

| Efecto | Filtro |
|---|---|
| EQ paramétrico, paso alto, paso bajo, notch | `equalizer`, `highpass`, `lowpass`, `bandreject` |
| Compresor, multibanda, limitador, puerta de ruido | `acompressor`, `acrossover`, `alimiter`, `agate` |
| Reverb, delay, chorus, flanger | `aecho`, `afreqshift`, `chorus`, `flanger` |
| DeNoise | `arnndn` (red neuronal incluida en FFmpeg) |
| Cambio de tono | `rubberband` |
| Normalización de sonoridad | `loudnorm` (EBU R128) |
| *Auto ducking* | `sidechaincompress` |

Grabación de voz en off con NAudio.

### 5. Multicámara

Secuencias multicámara, monitor dedicado, cambio de ángulo en tiempo real y sincronización
por timecode, por marcadores y **por audio** mediante correlación cruzada de las formas de
onda. Caro, pero nada impide hacerlo.

### 24. Auto Reframe 🟡

Conversión entre 16:9, 9:16, 1:1 y 21:9. El reencuadre manual y con keyframes es directo;
el **seguimiento automático del sujeto** necesita detección de objetos y entra en la
categoría cara.

### 28–31. Exportación

Cola de exportación, lotes, presets, múltiples formatos y resoluciones, exportación en
segundo plano; H.264, H.265, ProRes, DNx, MXF, WAV, MP3, AAC, PNG, JPEG, GIF y secuencias
de imágenes; presets de YouTube, Shorts, Instagram, Reels, TikTok, Facebook, Vimeo y X;
de 8K a SD, en 16:9, 9:16, 4:3, 1:1, 21:9 y personalizados.

Ya construido: exportación de 480p a 4K con elección de codificador, modo de tasa y bitrate.

### 32. HDR · 44. Administración de color

HDR10, HLG, PQ, Rec. 709, Rec. 2020, sRGB, *tone mapping*, espacios de entrada, trabajo y
salida, material log. Mediante `zscale`, `tonemap` y `colorspace`.

### 33. Video profesional

Timecode con y sin *drop frame*, superposiciones de timecode, márgenes de seguridad,
colores seguros para emisión, flujos de proxy y edición offline/online.

### 34. VR y 360° 🟡

Video equirectangular monoscópico y estereoscópico con el filtro `v360`. Factible; el
preview interactivo en VR, no.

### 38, 40–42, 46, 50. Interfaz y productividad

Atajos personalizables, espacios de trabajo, varios monitores, paneles propios, búsqueda,
exportación rápida, marcadores, favoritos, etiquetas de color, pegar atributos, copiar
efectos y guardar presets.

Las 17 herramientas de edición (selección, ripple, roll, *rate stretch*, cuchilla, slip,
slide, pluma, mano, zoom, texto, formas), los monitores de origen y programa, la vista de
comparación, el fotograma de referencia, las guías y reglas.

Presets de secuencia, frecuencia de fotogramas, resolución, base de tiempo, frecuencia de
muestreo, relación de aspecto de píxel y espacio de color.

Deshacer y rehacer con historial, autoguardado y su recuperación.

### 43. Rendimiento

Aceleración por GPU, codificación y decodificación por hardware, edición con proxy,
renderizado de previsualizaciones, media cache y procesamiento en segundo plano.

Ya construido: detección de codificadores por hardware con prueba real de funcionamiento.

### 45, 49. Formatos de intercambio

Exportar e importar **XML** y **EDL** para intercambiar con Premiere, DaVinci y Final Cut.
AAF y OMF son formatos complejos y quedan como posibles más adelante.

---

## 🟡 Posible, pero caro

| Función | Qué haría falta |
|---|---|
| **8. Motion tracking** | OpenCV. Point tracking, seguimiento de máscaras y de objetos |
| **Auto Reframe con seguimiento** | Un modelo de detección de personas |
| **Scene y shot detection** | Comparación de histogramas entre fotogramas; el filtro `scdet` da la base |
| **Color matching automático** | Comparación estadística entre fotogramas de referencia |
| **39. Plugins y extensiones** | Diseñar una API de extensión propia y estable |

---

## ❌ Fuera de alcance, y el motivo

| Función | Por qué no |
|---|---|
| **25–27, 48.** Dynamic Link, After Effects, Photoshop, Audition, Creative Cloud, Adobe Fonts, Adobe Stock | Protocolos y servicios propietarios de Adobe. No hay vía legal ni técnica |
| **35–36.** Team Projects, Productions, Frame.io, edición colaborativa, revisiones y aprobaciones | Infraestructura en la nube de Adobe. **Descartado por decisión de producto**: ese esfuerzo se dedica al editor |
| **21–23.** Generative Extend, Media Intelligence, búsqueda semántica de medios, generación de fotogramas | Modelos generativos de video de frontera |
| **13.** Morph Cut | Síntesis de fotogramas intermedios con ML |
| **2.** RED, ARRIRAW, Blackmagic RAW, CinemaDNG | Requieren el SDK del fabricante, con licencia |
| **26.** Importar PSD por capas | Formato propietario; se admitirá la imagen aplanada |
| **45.** DCP | Flujo de cine digital con certificación |

---

## Orden acordado

1. **Reproductor propio** — FFmpeg a `WriteableBitmap`, con audio por NAudio y proxies
   automáticos. Desbloquea la superposición de controles, el color sobre el preview y el
   scrubbing fluido en 8 GB de RAM.
2. **Timeline profesional** — multipista, separar audio, ripple/roll/slip/slide, snap,
   marcadores, bloquear y silenciar pistas, menú contextual.
3. **Color completo** — Lumetri y scopes.
4. **Texto, títulos y subtítulos** — SkiaSharp y Whisper en local.
5. **Transiciones, efectos y keyframes.**

---

*Este documento se actualiza conforme se entrega cada bloque.*
