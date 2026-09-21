// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

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
    public const int CurrentVersion = 4;

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

        // Un mismo archivo lo usan clips de video y de audio: se registra una sola vez y
        // ambos apuntan a su identificador.
        string Register(MediaInfo info)
        {
            if (ids.TryGetValue(info.Path, out var existing))
            {
                return existing;
            }

            var id = "m" + file.Media.Count.ToString(CultureInfo.InvariantCulture);
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

            return id;
        }

        // Primero los medios del proyecto, en su orden, y después cualquier otro que
        // aparezca en el montaje sin estar registrado: indicaría un fallo de coherencia
        // interna, y registrarlo al vuelo es mejor que perder el clip al guardar.
        foreach (var info in project.Media)
        {
            Register(info);
        }

        foreach (var clip in project.Timeline.Clips)
        {
            if (clip.IsGap)
            {
                file.Clips.Add(new ProjectClip { Gap = true, SourceIn = clip.SourceIn, SourceOut = clip.SourceOut });
                continue;
            }

            file.Clips.Add(new ProjectClip
            {
                MediaId = Register(clip.Source),
                SourceIn = clip.SourceIn,
                SourceOut = clip.SourceOut,
                AudioDetached = clip.IsAudioDetached,
                AudioGainDb = clip.AudioGainDb,
                AudioMuted = clip.IsAudioMuted,
            });
        }

        foreach (var track in project.Sequence.AudioTracks)
        {
            var saved = new ProjectAudioTrack
            {
                Name = track.Name,
                Muted = track.IsMuted,
                Solo = track.IsSolo,
                Locked = track.IsLocked,
                GainDb = track.GainDb,
            };

            foreach (var audio in track.Clips)
            {
                saved.Clips.Add(new ProjectAudioClip
                {
                    MediaId = Register(audio.Source),
                    SourceIn = audio.SourceIn,
                    SourceOut = audio.SourceOut,
                    Start = audio.TimelineStart,
                    GainDb = audio.GainDb,
                    Muted = audio.IsMuted,
                    FadeIn = audio.FadeIn,
                    FadeOut = audio.FadeOut,
                });
            }

            file.AudioTracks.Add(saved);
        }

        foreach (var layer in project.Sequence.OverlayTracks)
        {
            var savedLayer = new ProjectOverlayTrack
            {
                Name = layer.Name,
                Hidden = layer.IsHidden,
                Locked = layer.IsLocked,
            };

            foreach (var item in layer.Items)
            {
                var saved = new ProjectOverlayItem
                {
                    Kind = item.Kind switch { OverlayKind.Text => "text", OverlayKind.Video => "video", _ => "image" },
                    Start = item.Start,
                    Duration = item.Duration,
                    CenterX = item.Transform.CenterX,
                    CenterY = item.Transform.CenterY,
                    Width = item.Transform.Width,
                    Opacity = item.Transform.Opacity,
                    AspectRatio = item.AspectRatio,
                };

                if (item.Text is { } text)
                {
                    saved.Text = text.Content;
                    saved.TextSize = text.Size;
                    saved.TextColor = text.Color;
                    saved.Bold = text.Bold;
                    saved.Italic = text.Italic;
                    saved.Shadow = text.Shadow;
                }

                if (item.ImagePath is { } image)
                {
                    saved.ImagePath = image;
                    saved.ImageRelativePath = MakeRelative(projectDirectory, image);
                }

                if (item.Media is { } video)
                {
                    saved.MediaId = Register(video);
                    saved.SourceIn = item.SourceIn;
                    saved.PlaysAudio = item.PlaysAudio;
                    saved.AudioGainDb = item.AudioGainDb;
                }

                savedLayer.Items.Add(saved);
            }

            file.OverlayTracks.Add(savedLayer);
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
            if (clip.Gap)
            {
                if (clip.SourceOut - clip.SourceIn >= Clip.MinimumDuration)
                {
                    project.Timeline.Append(new Clip(MediaInfo.Gap, clip.SourceIn, clip.SourceOut));
                }

                continue;
            }

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

            project.Timeline.Append(new Clip(info, sourceIn, sourceOut)
            {
                IsAudioDetached = clip.AudioDetached,
                AudioGainDb = clip.AudioGainDb,
                IsAudioMuted = clip.AudioMuted,
            });
        }

        foreach (var savedTrack in file.AudioTracks)
        {
            var track = project.Sequence.AddAudioTrack(
                string.IsNullOrWhiteSpace(savedTrack.Name) ? null : savedTrack.Name);

            track.IsMuted = savedTrack.Muted;
            track.IsSolo = savedTrack.Solo;
            track.GainDb = savedTrack.GainDb;

            foreach (var savedClip in savedTrack.Clips)
            {
                if (!byId.TryGetValue(savedClip.MediaId, out var info) || !info.HasAudio)
                {
                    continue;
                }

                var sourceIn = Clamp(savedClip.SourceIn, TimeSpan.Zero, info.Duration);
                var sourceOut = Clamp(savedClip.SourceOut, TimeSpan.Zero, info.Duration);
                if (sourceOut - sourceIn < Clip.MinimumDuration)
                {
                    continue;
                }

                var audio = new AudioClip(info, sourceIn, sourceOut, savedClip.Start)
                {
                    GainDb = savedClip.GainDb,
                    IsMuted = savedClip.Muted,
                };

                // Los fundidos se asignan después de fijar la duración: se acotan contra
                // ella, y un valor guardado que ya no cabe se ajusta en lugar de fallar.
                audio.FadeIn = savedClip.FadeIn;
                audio.FadeOut = savedClip.FadeOut;

                track.TryAdd(audio);
            }

            // El bloqueo se aplica al final: una pista bloqueada no admitiría sus propios clips.
            track.IsLocked = savedTrack.Locked;
        }

        LoadOverlays(file, project, projectDirectory, missing, byId);

        project.MarkSaved();
        return new ProjectLoadResult(project, missing);
    }

    private static void LoadOverlays(
        ProjectFile file,
        EditProject project,
        string? projectDirectory,
        List<string> missing,
        Dictionary<string, MediaInfo> byId)
    {
        // Se guardan de arriba abajo, y AddOverlayTrack inserta arriba: se recorre al revés para
        // que el orden final sea el guardado.
        foreach (var savedLayer in Enumerable.Reverse(file.OverlayTracks))
        {
            var layer = project.Sequence.AddOverlayTrack(
                string.IsNullOrWhiteSpace(savedLayer.Name) ? null : savedLayer.Name);
            layer.IsHidden = savedLayer.Hidden;

            foreach (var saved in savedLayer.Items)
            {
                var item = BuildOverlay(saved, projectDirectory, missing, byId);
                if (item is not null)
                {
                    layer.TryAdd(item);
                }
            }

            // El bloqueo va al final: una capa bloqueada no admitiría sus propios elementos.
            layer.IsLocked = savedLayer.Locked;
        }
    }

    private static OverlayItem? BuildOverlay(
        ProjectOverlayItem saved,
        string? projectDirectory,
        List<string> missing,
        Dictionary<string, MediaInfo> byId)
    {
        // Valores fuera de rango, por un archivo editado a mano o de otra versión, se ajustan
        // en lugar de rechazar el proyecto entero.
        var start = saved.Start < TimeSpan.Zero ? TimeSpan.Zero : saved.Start;
        var duration = saved.Duration < OverlayItem.MinimumDuration ? OverlayItem.MinimumDuration : saved.Duration;

        OverlayItem item;

        if (string.Equals(saved.Kind, "video", StringComparison.OrdinalIgnoreCase))
        {
            // Si el archivo ya no está, se avisó al cargar los medios: el elemento se omite.
            if (saved.MediaId is null || !byId.TryGetValue(saved.MediaId, out var media))
            {
                return null;
            }

            var sourceIn = Clamp(saved.SourceIn, TimeSpan.Zero, media.Duration);
            var available = media.Duration - sourceIn;
            if (available < TimeSpan.FromMilliseconds(40))
            {
                return null;
            }

            item = OverlayItem.CreateVideo(
                media, sourceIn, start, saved.Duration < available ? saved.Duration : available,
                playsAudio: saved.PlaysAudio, audioGainDb: saved.AudioGainDb);
        }
        else if (string.Equals(saved.Kind, "image", StringComparison.OrdinalIgnoreCase))
        {
            var path = ResolvePath(saved.ImageRelativePath, saved.ImagePath, projectDirectory);
            if (path is null)
            {
                missing.Add(saved.ImagePath ?? "(imagen)");
                return null;
            }

            item = OverlayItem.CreateImage(path, saved.AspectRatio > 0 ? saved.AspectRatio : 1, start, duration);
        }
        else
        {
            item = OverlayItem.CreateText(
                new TextStyle(
                    saved.Text ?? string.Empty,
                    Math.Clamp(saved.TextSize, TextStyle.MinimumSize, TextStyle.MaximumSize),
                    string.IsNullOrWhiteSpace(saved.TextColor) ? "#FFFFFF" : saved.TextColor,
                    saved.Bold,
                    saved.Italic,
                    saved.Shadow),
                start,
                duration);
        }

        item.Transform = new OverlayTransform(saved.CenterX, saved.CenterY, saved.Width, saved.Opacity).Clamped();
        return item;
    }

    private static string? ResolvePath(string? relativePath, string? absolutePath, string? projectDirectory) =>
        Resolve(
            new ProjectMedia { Path = absolutePath ?? string.Empty, RelativePath = relativePath },
            projectDirectory);

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
