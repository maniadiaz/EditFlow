// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace EditFlow.Core.Projects;

/// <summary>Forma en disco de un archivo <c>.editflow</c>.</summary>
/// <remarks>
/// Es un tipo aparte del modelo en memoria a propósito. El modelo puede reorganizarse
/// cuando convenga; este contrato no, porque hay archivos guardados que dependen de él.
/// Mezclar ambos haría que cualquier refactorización rompiera los proyectos ya existentes.
/// </remarks>
public sealed class ProjectFile
{
    /// <summary>Versión del formato. Se incrementa solo ante cambios incompatibles.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = ProjectSerializer.CurrentVersion;

    /// <summary>Versión de EditFlow que generó el archivo, para diagnóstico.</summary>
    [JsonPropertyName("createdWith")]
    public string? CreatedWith { get; set; }

    /// <summary>Momento del último guardado, en UTC.</summary>
    [JsonPropertyName("savedAt")]
    public DateTimeOffset SavedAt { get; set; }

    /// <summary>Medios importados.</summary>
    [JsonPropertyName("media")]
    public List<ProjectMedia> Media { get; set; } = [];

    /// <summary>Clips del montaje, en orden de reproducción.</summary>
    [JsonPropertyName("clips")]
    public List<ProjectClip> Clips { get; set; } = [];

    /// <summary>
    /// Pistas de audio. Ausente en los proyectos de la versión 1, que se abren con la
    /// lista vacía: añadir un campo no invalida los archivos anteriores.
    /// </summary>
    [JsonPropertyName("audioTracks")]
    public List<ProjectAudioTrack> AudioTracks { get; set; } = [];

    /// <summary>
    /// Capas de textos e imágenes, de arriba abajo. Ausente en los proyectos anteriores a la
    /// versión 3, que se abren sin superposiciones.
    /// </summary>
    [JsonPropertyName("overlayTracks")]
    public List<ProjectOverlayTrack> OverlayTracks { get; set; } = [];
}

/// <summary>Una capa de superposiciones guardada.</summary>
public sealed class ProjectOverlayTrack
{
    /// <summary>Nombre visible.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Si la capa está oculta.</summary>
    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    /// <summary>Si la capa está bloqueada.</summary>
    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    /// <summary>Si es la capa de subtítulos («Sub»).</summary>
    [JsonPropertyName("subtitles")]
    public bool Subtitles { get; set; }

    /// <summary>Elementos de la capa.</summary>
    [JsonPropertyName("items")]
    public List<ProjectOverlayItem> Items { get; set; } = [];
}

/// <summary>Un texto o una imagen superpuestos, guardados.</summary>
public sealed class ProjectOverlayItem
{
    /// <summary>Tipo: <c>text</c>, <c>image</c> o <c>video</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "text";

    /// <summary>Instante de la timeline en que aparece.</summary>
    [JsonPropertyName("start")]
    public TimeSpan Start { get; set; }

    /// <summary>Cuánto tiempo se ve.</summary>
    [JsonPropertyName("duration")]
    public TimeSpan Duration { get; set; }

    /// <summary>Centro horizontal, de 0 a 1.</summary>
    [JsonPropertyName("centerX")]
    public double CenterX { get; set; } = 0.5;

    /// <summary>Centro vertical, de 0 a 1.</summary>
    [JsonPropertyName("centerY")]
    public double CenterY { get; set; } = 0.5;

    /// <summary>Ancho de una imagen como fracción del ancho del video.</summary>
    [JsonPropertyName("width")]
    public double Width { get; set; } = 0.25;

    /// <summary>Transparencia, de 0 a 1.</summary>
    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 1;

    /// <summary>Texto, en los elementos de texto.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>Alto de la letra como fracción del alto del video.</summary>
    [JsonPropertyName("textSize")]
    public double TextSize { get; set; } = 0.08;

    /// <summary>Color del texto, <c>#RRGGBB</c>.</summary>
    [JsonPropertyName("textColor")]
    public string? TextColor { get; set; }

    /// <summary>Si el texto va en negrita.</summary>
    [JsonPropertyName("bold")]
    public bool Bold { get; set; } = true;

    /// <summary>Si el texto va en cursiva.</summary>
    [JsonPropertyName("italic")]
    public bool Italic { get; set; }

    /// <summary>Si el texto lleva sombra.</summary>
    [JsonPropertyName("shadow")]
    public bool Shadow { get; set; } = true;

    /// <summary>
    /// Tipografía del texto; ausente para la del sistema (lo mismo que en los proyectos
    /// anteriores a la versión 9, que no tenían este campo).
    /// </summary>
    [JsonPropertyName("fontFamily")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FontFamily { get; set; }

    /// <summary>Ruta absoluta de la imagen, en los elementos de imagen.</summary>
    [JsonPropertyName("imagePath")]
    public string? ImagePath { get; set; }

    /// <summary>Ruta de la imagen relativa al proyecto, si está cerca de él.</summary>
    [JsonPropertyName("imageRelativePath")]
    public string? ImageRelativePath { get; set; }

    /// <summary>Ancho entre alto de la imagen.</summary>
    [JsonPropertyName("aspectRatio")]
    public double AspectRatio { get; set; } = 1;

    /// <summary>Identificador del medio, en los elementos de video.</summary>
    [JsonPropertyName("mediaId")]
    public string? MediaId { get; set; }

    /// <summary>Instante del archivo donde empieza lo que se ve, en los elementos de video.</summary>
    [JsonPropertyName("sourceIn")]
    public TimeSpan SourceIn { get; set; }

    /// <summary>Si el sonido del video entra en la mezcla.</summary>
    [JsonPropertyName("playsAudio")]
    public bool PlaysAudio { get; set; } = true;

    /// <summary>Volumen del sonido del video, en dB.</summary>
    [JsonPropertyName("audioGainDb")]
    public double AudioGainDb { get; set; }

    /// <summary>Ajuste de color del video superpuesto.</summary>
    [JsonPropertyName("color")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectColor? Color { get; set; }
}

/// <summary>Una pista de audio guardada.</summary>
public sealed class ProjectAudioTrack
{
    /// <summary>Nombre visible.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Si la pista está silenciada.</summary>
    [JsonPropertyName("muted")]
    public bool Muted { get; set; }

    /// <summary>Si la pista está en solo.</summary>
    [JsonPropertyName("solo")]
    public bool Solo { get; set; }

    /// <summary>Si la pista está bloqueada.</summary>
    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    /// <summary>Volumen de la pista en dB.</summary>
    [JsonPropertyName("gainDb")]
    public double GainDb { get; set; }

    /// <summary>Clips de la pista.</summary>
    [JsonPropertyName("clips")]
    public List<ProjectAudioClip> Clips { get; set; } = [];
}

/// <summary>Un clip de audio guardado.</summary>
public sealed class ProjectAudioClip
{
    /// <summary>Identificador del medio del que procede.</summary>
    [JsonPropertyName("mediaId")]
    public string MediaId { get; set; } = string.Empty;

    /// <summary>Inicio dentro del archivo origen.</summary>
    [JsonPropertyName("sourceIn")]
    public TimeSpan SourceIn { get; set; }

    /// <summary>Fin dentro del archivo origen.</summary>
    [JsonPropertyName("sourceOut")]
    public TimeSpan SourceOut { get; set; }

    /// <summary>Posición en la timeline.</summary>
    [JsonPropertyName("start")]
    public TimeSpan Start { get; set; }

    /// <summary>Volumen del clip en dB.</summary>
    [JsonPropertyName("gainDb")]
    public double GainDb { get; set; }

    /// <summary>Si el clip está silenciado.</summary>
    [JsonPropertyName("muted")]
    public bool Muted { get; set; }

    /// <summary>Fundido de entrada.</summary>
    [JsonPropertyName("fadeIn")]
    public TimeSpan FadeIn { get; set; }

    /// <summary>Fundido de salida.</summary>
    [JsonPropertyName("fadeOut")]
    public TimeSpan FadeOut { get; set; }
}

/// <summary>Un medio referenciado por el proyecto.</summary>
public sealed class ProjectMedia
{
    /// <summary>Identificador dentro del archivo, al que apuntan los clips.</summary>
    /// <remarks>
    /// Los clips referencian un identificador en lugar de repetir los datos del medio.
    /// Cortar un video en diez trozos produce diez clips del mismo archivo; repetir sus
    /// metadatos en cada uno multiplicaría el tamaño y abriría la puerta a que diez
    /// copias del mismo dato dejaran de coincidir entre sí.
    /// </remarks>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Ruta absoluta del archivo cuando se guardó el proyecto.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Ruta relativa al propio proyecto, cuando el medio está dentro de su carpeta.
    /// </summary>
    /// <remarks>
    /// Se guardan las dos. Si el proyecto y sus videos se mueven juntos a otra carpeta
    /// —u otro equipo—, la ruta absoluta deja de existir pero la relativa sigue siendo
    /// válida. Guardar solo la absoluta convierte cualquier reorganización de carpetas
    /// en un proyecto roto.
    /// </remarks>
    [JsonPropertyName("relativePath")]
    public string? RelativePath { get; set; }

    /// <summary>Duración total del archivo.</summary>
    [JsonPropertyName("duration")]
    public TimeSpan Duration { get; set; }

    /// <summary>Ancho codificado.</summary>
    [JsonPropertyName("width")]
    public int Width { get; set; }

    /// <summary>Alto codificado.</summary>
    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>Fotogramas por segundo.</summary>
    [JsonPropertyName("frameRate")]
    public double FrameRate { get; set; }

    /// <summary>Códec de video.</summary>
    [JsonPropertyName("codec")]
    public string Codec { get; set; } = string.Empty;

    /// <summary>Si el archivo trae pista de audio.</summary>
    [JsonPropertyName("hasAudio")]
    public bool HasAudio { get; set; }

    /// <summary>Rotación declarada en los metadatos.</summary>
    [JsonPropertyName("rotation")]
    public int Rotation { get; set; }
}

/// <summary>Un clip del montaje.</summary>
public sealed class ProjectClip
{
    /// <summary>Identificador del medio del que procede.</summary>
    [JsonPropertyName("mediaId")]
    public string MediaId { get; set; } = string.Empty;

    /// <summary>Instante del archivo origen donde empieza el clip.</summary>
    [JsonPropertyName("sourceIn")]
    public TimeSpan SourceIn { get; set; }

    /// <summary>Instante del archivo origen donde termina el clip.</summary>
    [JsonPropertyName("sourceOut")]
    public TimeSpan SourceOut { get; set; }

    /// <summary>Si el audio de este clip se separó a una pista de audio.</summary>
    [JsonPropertyName("audioDetached")]
    public bool AudioDetached { get; set; }

    /// <summary>Volumen del audio propio del clip, en dB.</summary>
    /// <remarks>Ausente en proyectos anteriores, que se abren con el valor 0.</remarks>
    [JsonPropertyName("audioGainDb")]
    public double AudioGainDb { get; set; }

    /// <summary>Si el audio propio del clip está silenciado.</summary>
    [JsonPropertyName("audioMuted")]
    public bool AudioMuted { get; set; }

    /// <summary>Si es un hueco (tiempo en negro sin archivo); en ese caso no hay medio.</summary>
    [JsonPropertyName("gap")]
    public bool Gap { get; set; }

    /// <summary>Ajuste de color; ausente en los proyectos anteriores, que se abren sin ajuste.</summary>
    [JsonPropertyName("color")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectColor? Color { get; set; }

    /// <summary>
    /// Transición desde el clip anterior; ausente en los proyectos de la versión 5 y
    /// anteriores, y en los de la 6 cuando el clip no tiene transición.
    /// </summary>
    [JsonPropertyName("transitionIn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectTransition? TransitionIn { get; set; }

    /// <summary>
    /// Velocidad de reproducción; ausente en los proyectos anteriores a la versión 7, y en los
    /// de la 7 cuando el clip va a velocidad normal (1 si no se guardó nada).
    /// </summary>
    [JsonPropertyName("speed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Speed { get; set; }

    /// <summary>
    /// Encuadre (zoom, posición, rotación); ausente en los proyectos anteriores a la versión 8,
    /// y en los de la 8 cuando el clip muestra el fotograma completo sin girar.
    /// </summary>
    [JsonPropertyName("transform")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectClipTransform? Transform { get; set; }

    /// <summary>
    /// Filtro de aspecto, como el nombre del valor de <c>VisualFilterKind</c>; ausente en los
    /// proyectos anteriores a la versión 9, y en los de la 9 sin filtro.
    /// </summary>
    [JsonPropertyName("filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filter { get; set; }

    /// <summary>Fundido de entrada; ausente antes de la versión 9, o en un clip sin fundidos.</summary>
    [JsonPropertyName("fadeIn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TimeSpan FadeIn { get; set; }

    /// <summary>Fundido de salida; ausente antes de la versión 9, o en un clip sin fundidos.</summary>
    [JsonPropertyName("fadeOut")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TimeSpan FadeOut { get; set; }
}

/// <summary>Encuadre guardado.</summary>
public sealed class ProjectClipTransform
{
    /// <summary>Zoom; 1 muestra el fotograma completo.</summary>
    [JsonPropertyName("scale")]
    public double Scale { get; set; } = 1;

    /// <summary>Desplazamiento horizontal del recorte, como fracción del fotograma.</summary>
    [JsonPropertyName("offsetX")]
    public double OffsetX { get; set; }

    /// <summary>Desplazamiento vertical del recorte, como fracción del fotograma.</summary>
    [JsonPropertyName("offsetY")]
    public double OffsetY { get; set; }

    /// <summary>Giro en grados, sentido horario.</summary>
    [JsonPropertyName("rotation")]
    public double Rotation { get; set; }
}

/// <summary>Transición guardada.</summary>
public sealed class ProjectTransition
{
    /// <summary>Tipo, como el nombre del valor de <c>TransitionKind</c> (p. ej. <c>"Dissolve"</c>).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "None";

    /// <summary>Cuánto se solapan los clips.</summary>
    [JsonPropertyName("duration")]
    public TimeSpan Duration { get; set; }
}

/// <summary>Ajuste de color guardado.</summary>
public sealed class ProjectColor
{
    /// <summary>Exposición, de -100 a 100.</summary>
    [JsonPropertyName("exposure")]
    public double Exposure { get; set; }

    /// <summary>Contraste, de -100 a 100.</summary>
    [JsonPropertyName("contrast")]
    public double Contrast { get; set; }

    /// <summary>Saturación, de -100 a 100.</summary>
    [JsonPropertyName("saturation")]
    public double Saturation { get; set; }

    /// <summary>Temperatura, de -100 a 100.</summary>
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }
}

/// <summary>Contexto de serialización generado en compilación.</summary>
/// <remarks>
/// La generación por código fuente evita la reflexión en tiempo de ejecución, lo que
/// permite publicar la aplicación recortada sin que el guardado deje de funcionar.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProjectFile))]
internal sealed partial class ProjectJsonContext : JsonSerializerContext;
