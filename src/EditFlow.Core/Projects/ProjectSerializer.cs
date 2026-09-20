using System.Globalization;
using System.Text.Json;
using EditFlow.Core.Media;
using EditFlow.Core.Timeline;

namespace EditFlow.Core.Projects;

/// <summary>Resultado de abrir un proyecto.</summary>
/// <param name="Project">Proyecto cargado.</param>
/// <param name="MissingMedia">
/// Archivos que el proyecto referencia pero no se encontraron en disco.
/// </param>
public sealed record ProjectLoadResult(EditProject Project, IReadOnlyList<string> MissingMedia)
{
    /// <summary>Indica si faltó algún archivo.</summary>
    public bool HasMissingMedia => MissingMedia.Count > 0;
}

/// <summary>Se lanza cuando un archivo de proyecto no se puede interpretar.</summary>
public sealed class ProjectFormatException : Exception
{
    /// <inheritdoc cref="ProjectFormatException"/>
    public ProjectFormatException(string message) : base(message) { }

    /// <inheritdoc cref="ProjectFormatException"/>
    public ProjectFormatException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <inheritdoc cref="ProjectFormatException"/>
    public ProjectFormatException() : base("El archivo de proyecto no es válido.") { }
}

/// <summary>Guarda y abre archivos <c>.editflow</c>.</summary>
public static class ProjectSerializer
{
    /// <summary>Versión actual del formato.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Extensión de los archivos de proyecto.</summary>
    public const string Extension = ".editflow";

    /// <summary>Guarda el proyecto en la ruta indicada.</summary>
    public static async Task SaveAsync(
        EditProject project,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = ToFile(project, path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Se escribe a un temporal y se reemplaza al final. Si el disco se llena o el
        // proceso muere a mitad, el proyecto anterior sigue intacto en lugar de quedar
        // truncado: perder el montaje por un guardado interrumpido sería lo peor que
        // puede hacer un editor.
        var temporary = path + ".tmp";

        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer
                .SerializeAsync(stream, file, ProjectJsonContext.Default.ProjectFile, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);

        project.FilePath = path;
        project.MarkSaved();
    }

    /// <summary>Abre un proyecto desde disco.</summary>
    /// <exception cref="FileNotFoundException">Si el archivo no existe.</exception>
    /// <exception cref="ProjectFormatException">Si el contenido no es un proyecto válido.</exception>
    public static async Task<ProjectLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("No se encontró el archivo de proyecto.", path);
        }

        ProjectFile? file;
        try
        {
            await using var stream = File.OpenRead(path);
            file = await JsonSerializer
                .DeserializeAsync(stream, ProjectJsonContext.Default.ProjectFile, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException(
                $"'{Path.GetFileName(path)}' no es un proyecto de EditFlow válido: {ex.Message}", ex);
        }

        if (file is null)
        {
            throw new ProjectFormatException($"'{Path.GetFileName(path)}' está vacío.");
        }

        if (file.Version > CurrentVersion)
        {
            // Negarse es preferible a interpretar a medias un formato más nuevo: el
            // usuario perdería en silencio lo que esa versión añadiera al guardar encima.
            throw new ProjectFormatException(
                $"Este proyecto se guardó con una versión más reciente de EditFlow " +
                $"(formato {file.Version}, esta versión entiende hasta el {CurrentVersion}). " +
                "Actualiza EditFlow para abrirlo.");
        }

        return FromFile(file, path);
    }

    // ------------------------------------------------------------------ conversión

    internal static ProjectFile ToFile(EditProject project, string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));

        var file = new ProjectFile
        {
            Version = CurrentVersion,
            CreatedWith = "EditFlow",
            SavedAt = DateTimeOffset.UtcNow,
        };

        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < project.Media.Count; i++)
        {
            var info = project.Media[i];
            var id = "m" + i.ToString(CultureInfo.InvariantCulture);
            ids[info.Path] = id;

            file.Media.Add(new ProjectMedia
            {
                Id = id,
                Path = info.Path,
                RelativePath = MakeRelative(projectDirectory, info.Path),
                Duration = info.Duration,
                Width = info.Width,
                Height = info.Height,
                FrameRate = info.FrameRate,
                Codec = info.VideoCodec,
                HasAudio = info.HasAudio,
                Rotation = info.Rotation,
            });
        }

        foreach (var clip in project.Timeline.Clips)
        {
            // Un clip cuyo medio no esté registrado indicaría un fallo de coherencia
            // interna; se registra al vuelo en vez de perder el clip al guardar.
            if (!ids.TryGetValue(clip.Source.Path, out var mediaId))
            {
                mediaId = "m" + file.Media.Count.ToString(CultureInfo.InvariantCulture);
                ids[clip.Source.Path] = mediaId;

                file.Media.Add(new ProjectMedia
                {
                    Id = mediaId,
                    Path = clip.Source.Path,
                    RelativePath = MakeRelative(projectDirectory, clip.Source.Path),
                    Duration = clip.Source.Duration,
                    Width = clip.Source.Width,
                    Height = clip.Source.Height,
                    FrameRate = clip.Source.FrameRate,
                    Codec = clip.Source.VideoCodec,
                    HasAudio = clip.Source.HasAudio,
                    Rotation = clip.Source.Rotation,
                });
            }

            file.Clips.Add(new ProjectClip
            {
                MediaId = mediaId,
                SourceIn = clip.SourceIn,
                SourceOut = clip.SourceOut,
            });
        }

        return file;
    }

    internal static ProjectLoadResult FromFile(ProjectFile file, string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        var project = new EditProject { FilePath = projectPath };

        var byId = new Dictionary<string, MediaInfo>(StringComparer.Ordinal);
        var missing = new List<string>();
        var media = new List<MediaInfo>();

        foreach (var entry in file.Media)
        {
            var resolved = Resolve(entry, projectDirectory);
            if (resolved is null)
            {
                missing.Add(entry.Path);
                continue;
            }

            var info = new MediaInfo(
                resolved,
                entry.Duration,
                entry.Width,
                entry.Height,
                entry.FrameRate,
                entry.Codec,
                entry.HasAudio,
                entry.Rotation);

            media.Add(info);
            byId[entry.Id] = info;
        }

        project.ReplaceMedia(media);

        foreach (var clip in file.Clips)
        {
            if (!byId.TryGetValue(clip.MediaId, out var info))
            {
                // El medio faltaba en disco: su clip se omite y ya quedó anotado arriba.
                continue;
            }

            // Un proyecto guardado con un archivo que luego se recortó tendría intervalos
            // fuera de rango. Se acotan en vez de rechazar el proyecto entero.
            var sourceIn = Clamp(clip.SourceIn, TimeSpan.Zero, info.Duration);
            var sourceOut = Clamp(clip.SourceOut, TimeSpan.Zero, info.Duration);

            if (sourceOut - sourceIn < Clip.MinimumDuration)
            {
                continue;
            }

            project.Timeline.Append(new Clip(info, sourceIn, sourceOut));
        }

        project.MarkSaved();
        return new ProjectLoadResult(project, missing);
    }

    /// <summary>
    /// Localiza un medio, prefiriendo la ruta relativa al proyecto.
    /// </summary>
    /// <remarks>
    /// El orden importa: si el proyecto y sus videos se movieron juntos, la ruta relativa
    /// es la correcta y la absoluta apunta a un sitio que ya no existe —o peor, a un
    /// archivo distinto que ocupó ese hueco—.
    /// </remarks>
    private static string? Resolve(ProjectMedia entry, string? projectDirectory)
    {
        if (entry.RelativePath is not null && projectDirectory is not null)
        {
            var candidate = Path.GetFullPath(Path.Combine(projectDirectory, entry.RelativePath));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return File.Exists(entry.Path) ? entry.Path : null;
    }

    private static string? MakeRelative(string? projectDirectory, string mediaPath)
    {
        if (projectDirectory is null)
        {
            return null;
        }

        try
        {
            var relative = Path.GetRelativePath(projectDirectory, mediaPath);

            // Solo interesa si el medio está dentro o cerca del proyecto. Una relativa
            // que sube veinte niveles no aporta nada sobre la absoluta.
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                ? null
                : relative.Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            // Rutas en volúmenes distintos no admiten una relativa entre ellas.
            return null;
        }
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
