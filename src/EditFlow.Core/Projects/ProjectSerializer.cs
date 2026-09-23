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
    public const int CurrentVersion = 15;

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

        // Las carpetas se escriben antes que los medios porque cada medio guarda a cuál pertenece.
        // Van de fuera adentro, de modo que al abrir siempre existe ya el padre de la que toca.
        var binIds = new Dictionary<MediaBin, string>();
        foreach (var bin in project.Library.AllBins().Where(b => !b.IsRoot))
        {
            var binId = "b" + file.Bins.Count.ToString(CultureInfo.InvariantCulture);
            binIds[bin] = binId;

            file.Bins.Add(new ProjectBin
            {
                Id = binId,
                Name = bin.Name,
                ParentId = bin.Parent is { IsRoot: false } parent ? binIds.GetValueOrDefault(parent) : null,
            });
        }

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
                BinId = binIds.GetValueOrDefault(project.Library.BinOf(info)),
                Label = project.Library.LabelOf(info) is var label && label != MediaLabel.None
                    ? label.ToString()
                    : null,
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
                Color = ToSaved(clip.Color),
                TransitionIn = ToSaved(clip.TransitionIn),
                Speed = clip.Speed.Equals(1.0) ? null : clip.Speed,
                Transform = ToSaved(clip.Transform),
                Animation = ToSaved(clip.Animation),
                Grade = ToSaved(clip.Grade, projectDirectory),
                Filter = clip.Filter == VisualFilterKind.None ? null : clip.Filter.ToString(),
                FadeIn = clip.FadeIn,
                FadeOut = clip.FadeOut,
                Effect = clip.Effect == VisualEffectKind.None ? null : clip.Effect.ToString(),
                Pan = clip.Pan,
                AudioEffect = clip.AudioEffect == AudioEffectKind.None ? null : clip.AudioEffect.ToString(),
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
                    Pan = audio.Pan,
                    Effect = audio.Effect == AudioEffectKind.None ? null : audio.Effect.ToString(),
                    Animation = ToSaved(audio.Animation),
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
                Subtitles = layer.IsSubtitles,
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
                    FadeIn = item.FadeIn,
                    FadeOut = item.FadeOut,
                };

                if (item.Text is { } text)
                {
                    saved.Text = text.Content;
                    saved.TextSize = text.Size;
                    saved.TextColor = text.Color;
                    saved.Bold = text.Bold;
                    saved.Italic = text.Italic;
                    saved.Shadow = text.Shadow;
                    saved.FontFamily = text.FontFamily;

                    if (text.FontFilePath is { } fontFile)
                    {
                        saved.FontFilePath = fontFile;
                        saved.FontFileRelativePath = MakeRelative(projectDirectory, fontFile);
                    }
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
                    saved.Color = ToSaved(item.Color);
                    saved.ChromaKey = ToSaved(item.ChromaKey);
                }

                saved.Animation = ToSaved(item.Animation);

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

        // Las carpetas se reconstruyen antes que los medios, que dicen a cuál pertenecen.
        project.ClearLibrary();
        var bins = new Dictionary<string, MediaBin>(StringComparer.Ordinal);
        foreach (var saved in file.Bins)
        {
            var parent = saved.ParentId is not null ? bins.GetValueOrDefault(saved.ParentId) : null;
            bins[saved.Id] = project.Library.CreateBin(saved.Name, parent);
        }

        foreach (var entry in file.Media)
        {
            var resolved = Resolve(entry, projectDirectory);
            if (resolved is null)
            {
                missing.Add(entry.Path);
            }

            // Un archivo que no aparece no se descarta: entra marcado como ausente, con los datos
            // técnicos que quedaron guardados. Así sus clips siguen en el montaje —en su sitio, con
            // sus cortes y sus ajustes— y reconectarlo devuelve la imagen sin rehacer nada. Antes
            // se tiraban, y mover una carpeta equivalía a perder el trabajo.
            var info = new MediaInfo(
                resolved ?? entry.Path,
                entry.Duration,
                entry.Width,
                entry.Height,
                entry.FrameRate,
                entry.Codec,
                entry.HasAudio,
                entry.Rotation)
            {
                IsOffline = resolved is null,
            };

            media.Add(info);
            byId[entry.Id] = info;

            if (entry.BinId is not null && bins.TryGetValue(entry.BinId, out var bin))
            {
                project.Library.MoveToBin(info, bin);
            }

            if (Enum.TryParse<MediaLabel>(entry.Label, out var label))
            {
                project.Library.SetLabel(info, label);
            }
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
                // El proyecto referencia un medio que ni siquiera figura en su propia lista: es un
                // archivo corrupto o editado a mano, no un archivo que se movió.
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
                Color = FromSaved(clip.Color),
                TransitionIn = FromSaved(clip.TransitionIn),
                Speed = clip.Speed ?? 1,
                Transform = FromSaved(clip.Transform),
                Animation = FromSaved(clip.Animation),
                Grade = FromSaved(clip.Grade, projectDirectory),
                Filter = clip.Filter is not null && Enum.TryParse<VisualFilterKind>(clip.Filter, out var kind)
                    ? kind
                    : VisualFilterKind.None,
                FadeIn = clip.FadeIn,
                FadeOut = clip.FadeOut,
                Effect = clip.Effect is not null && Enum.TryParse<VisualEffectKind>(clip.Effect, out var effect)
                    ? effect
                    : VisualEffectKind.None,
                Pan = clip.Pan,
                AudioEffect = clip.AudioEffect is not null
                    && Enum.TryParse<AudioEffectKind>(clip.AudioEffect, out var clipAudioEffect)
                        ? clipAudioEffect
                        : AudioEffectKind.None,
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
                    Pan = savedClip.Pan,
                    Effect = savedClip.Effect is not null
                        && Enum.TryParse<AudioEffectKind>(savedClip.Effect, out var audioEffect)
                            ? audioEffect
                            : AudioEffectKind.None,
                    Animation = FromSaved(savedClip.Animation),
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
            layer.IsSubtitles = savedLayer.Subtitles;

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

        project.Sequence.KeepSubtitleLayerOnTop();
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
            item.Color = FromSaved(saved.Color);
            item.ChromaKey = FromSaved(saved.ChromaKey);
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
            // Una tipografía propia que ya no está en su sitio no se trata como el resto de
            // archivos que faltan: el texto se sigue viendo, solo que con la del sistema, en
            // vez de perder por completo el título o el subtítulo que la llevaba.
            var fontFile = saved.FontFilePath is null
                ? null
                : ResolvePath(saved.FontFileRelativePath, saved.FontFilePath, projectDirectory);

            item = OverlayItem.CreateText(
                new TextStyle(
                    saved.Text ?? string.Empty,
                    Math.Clamp(saved.TextSize, TextStyle.MinimumSize, TextStyle.MaximumSize),
                    string.IsNullOrWhiteSpace(saved.TextColor) ? "#FFFFFF" : saved.TextColor,
                    saved.Bold,
                    saved.Italic,
                    saved.Shadow,
                    saved.FontFamily,
                    fontFile),
                start,
                duration);
        }

        item.Transform = new OverlayTransform(saved.CenterX, saved.CenterY, saved.Width, saved.Opacity).Clamped();
        item.FadeIn = saved.FadeIn;
        item.FadeOut = saved.FadeOut;
        item.Animation = FromSaved(saved.Animation);
        return item;
    }

    // Un proyecto sin ajuste guarda nada: la lista de clips no se llena de ceros.
    private static ProjectColor? ToSaved(ColorAdjust color) => color.IsNone
        ? null
        : new ProjectColor
        {
            Exposure = color.Exposure,
            Contrast = color.Contrast,
            Saturation = color.Saturation,
            Temperature = color.Temperature,
        };

    private static ColorAdjust FromSaved(ProjectColor? saved) => saved is null
        ? ColorAdjust.None
        : new ColorAdjust(saved.Exposure, saved.Contrast, saved.Saturation, saved.Temperature).Clamped();

    // Una capa que no recorta el fondo tampoco guarda nada, igual que con el ajuste de color.
    private static ProjectChromaKey? ToSaved(ChromaKey key) => !key.Enabled
        ? null
        : new ProjectChromaKey
        {
            Color = key.Color,
            Similarity = key.Similarity,
            Blend = key.Blend,
            Despill = key.Despill,
        };

    // Un clip sin corregir no guarda nada.
    private static ProjectColorGrade? ToSaved(ColorGrade grade, string? projectDirectory)
    {
        if (grade.IsNone)
        {
            return null;
        }

        var saved = new ProjectColorGrade
        {
            Master = Curve(grade.MasterCurve),
            Red = Curve(grade.RedCurve),
            Green = Curve(grade.GreenCurve),
            Blue = Curve(grade.BlueCurve),
            Shadows = Wheel(grade.ShadowWheel),
            Midtones = Wheel(grade.MidtoneWheel),
            Highlights = Wheel(grade.HighlightWheel),
        };

        if (!grade.SelectiveAdjust.IsNone)
        {
            var selective = grade.SelectiveAdjust.Clamped();
            saved.SelectiveFamily = selective.Family.ToString();
            saved.Selective =
                [selective.CyanRed, selective.MagentaGreen, selective.YellowBlue, selective.Lightness];
        }

        if (!string.IsNullOrWhiteSpace(grade.LutPath))
        {
            // Igual que con los medios: la ruta relativa permite mover el proyecto y su carpeta
            // de LUT juntos sin que se pierda el archivo.
            saved.LutPath = grade.LutPath;
            saved.LutRelativePath = MakeRelative(projectDirectory, grade.LutPath);
        }

        return saved;

        static string? Curve(ToneCurve curve) => curve.IsIdentity ? null : curve.ToFilterValue();

        static double[]? Wheel(ColorWheel wheel) =>
            wheel.IsNeutral ? null : [wheel.Red, wheel.Green, wheel.Blue];
    }

    /// <summary>
    /// Reconstruye una corrección de color guardada.
    /// </summary>
    /// <remarks>
    /// Un LUT que ya no está se descarta en silencio, igual que una tipografía propia que
    /// desapareció: el resto de la corrección sigue valiendo, y hacer fallar la apertura del
    /// proyecto entero por un archivo auxiliar sería desproporcionado.
    /// </remarks>
    private static ColorGrade FromSaved(ProjectColorGrade? saved, string? projectDirectory)
    {
        if (saved is null)
        {
            return ColorGrade.None;
        }

        return new ColorGrade(
            Curve(saved.Master),
            Curve(saved.Red),
            Curve(saved.Green),
            Curve(saved.Blue),
            Wheel(saved.Shadows),
            Wheel(saved.Midtones),
            Wheel(saved.Highlights),
            Selective(saved),
            ResolvePath(saved.LutRelativePath, saved.LutPath, projectDirectory));

        static ToneCurve? Curve(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var points = new List<CurvePoint>();
            foreach (var pair in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var halves = pair.Split('/');
                if (halves.Length == 2
                    && double.TryParse(halves[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var input)
                    && double.TryParse(halves[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var output))
                {
                    points.Add(new CurvePoint(input, output));
                }
            }

            return points.Count == 0 ? null : ToneCurve.FromPoints(points);
        }

        static ColorWheel? Wheel(double[]? values) =>
            values is { Length: 3 } ? new ColorWheel(values[0], values[1], values[2]).Clamped() : null;

        static SelectiveColor? Selective(ProjectColorGrade saved)
        {
            if (saved.Selective is not { Length: 4 }
                || !Enum.TryParse<ColorFamily>(saved.SelectiveFamily, out var family))
            {
                return null;
            }

            return new SelectiveColor(
                family, saved.Selective[0], saved.Selective[1], saved.Selective[2], saved.Selective[3]).Clamped();
        }
    }

    // Una propiedad sin puntos no se guarda: el archivo no se llena de listas vacías.
    private static List<ProjectKeyframeTrack>? ToSaved(Animation animation)
    {
        if (animation.IsNone)
        {
            return null;
        }

        return animation.Animated
            .Select(property => new ProjectKeyframeTrack
            {
                Property = property.ToString(),
                Points = animation.Track(property).Points
                    .Select(point => new ProjectKeyframe { At = point.At, Value = point.Value })
                    .ToList(),
            })
            .ToList();
    }

    /// <summary>
    /// Reconstruye las animaciones guardadas, saltándose lo que ya no entienda.
    /// </summary>
    /// <remarks>
    /// Una propiedad con un nombre desconocido se ignora en vez de hacer fallar la apertura: es
    /// lo que pasaría al abrir con una versión vieja un proyecto guardado por una más nueva, y
    /// perder una animación es mucho menos grave que no poder abrir el proyecto.
    /// </remarks>
    private static Animation FromSaved(List<ProjectKeyframeTrack>? saved)
    {
        if (saved is null || saved.Count == 0)
        {
            return Animation.None;
        }

        var animation = Animation.None;

        foreach (var savedTrack in saved)
        {
            if (!Enum.TryParse<AnimatedProperty>(savedTrack.Property, out var property))
            {
                continue;
            }

            var track = KeyframeTrack.Empty;
            foreach (var point in savedTrack.Points)
            {
                track = track.With(point.At, point.Value);
            }

            animation = animation.With(property, track);
        }

        return animation;
    }

    private static ChromaKey FromSaved(ProjectChromaKey? saved) => saved is null
        ? ChromaKey.None
        : new ChromaKey(true, saved.Color, saved.Similarity, saved.Blend, saved.Despill).Clamped();

    // Un clip con el encuadre normal no guarda nada.
    private static ProjectClipTransform? ToSaved(ClipTransform transform) => transform.IsNone
        ? null
        : new ProjectClipTransform
        {
            Scale = transform.Scale,
            OffsetX = transform.OffsetX,
            OffsetY = transform.OffsetY,
            Rotation = transform.Rotation,
        };

    private static ClipTransform FromSaved(ProjectClipTransform? saved) => saved is null
        ? ClipTransform.None
        : new ClipTransform(saved.Scale, saved.OffsetX, saved.OffsetY, saved.Rotation).Clamped();

    // Un clip sin transición no guarda nada: los proyectos de antes de que existieran
    // (versión 5 e inferior) siguen abriendo exactamente igual.
    private static ProjectTransition? ToSaved(Transition transition) => transition.IsNone
        ? null
        : new ProjectTransition { Kind = transition.Kind.ToString(), Duration = transition.Duration };

    private static Transition FromSaved(ProjectTransition? saved)
    {
        if (saved is null || !Enum.TryParse<TransitionKind>(saved.Kind, out var kind) || kind == TransitionKind.None)
        {
            return Transition.None;
        }

        // Se acota al rango admitido: un valor de otra versión, o editado a mano, no debe
        // producir una transición absurdamente larga o de duración cero.
        var duration = saved.Duration < Transition.MinimumDuration
            ? Transition.MinimumDuration
            : saved.Duration > Transition.MaximumDuration ? Transition.MaximumDuration : saved.Duration;

        return new Transition(kind, duration);
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
