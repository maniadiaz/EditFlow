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
