// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;

namespace EditFlow.Core.Timeline;

/// <summary>Qué contiene un elemento superpuesto.</summary>
public enum OverlayKind
{
    /// <summary>Un texto, por ejemplo un título o un subtítulo.</summary>
    Text,

    /// <summary>Una imagen, por ejemplo un logotipo.</summary>
    Image,

    /// <summary>Un video que se ve sobre el principal, con su propio sonido.</summary>
    Video,
}

/// <summary>Cómo se dibuja un texto superpuesto.</summary>
/// <param name="Content">El texto; puede tener varias líneas.</param>
/// <param name="Size">Alto de la letra como fracción del alto del video (0,08 es un 8 %).</param>
/// <param name="Color">Color de relleno, en <c>#RRGGBB</c>.</param>
/// <param name="Bold">Si va en negrita.</param>
/// <param name="Italic">Si va en cursiva.</param>
/// <param name="Shadow">Si lleva sombra, que ayuda a leerlo sobre cualquier fondo.</param>
/// <remarks>
/// El tamaño es relativo al video, no en píxeles: un título del 8 % ocupa lo mismo en el
/// preview a 480p que en la exportación a 4K. Con píxeles fijos, exportar a otra resolución
/// cambiaría el aspecto del texto respecto de lo que se vio al editar.
/// </remarks>
public sealed record TextStyle(
    string Content = "Título",
    double Size = 0.08,
    string Color = "#FFFFFF",
    bool Bold = true,
    bool Italic = false,
    bool Shadow = true)
{
    /// <summary>Tamaño mínimo admitido.</summary>
    public const double MinimumSize = 0.02;

    /// <summary>Tamaño máximo admitido.</summary>
    public const double MaximumSize = 0.5;
}

/// <summary>Posición, tamaño y transparencia de un elemento superpuesto.</summary>
/// <param name="CenterX">Centro horizontal, de 0 (izquierda) a 1 (derecha) del video.</param>
/// <param name="CenterY">Centro vertical, de 0 (arriba) a 1 (abajo).</param>
/// <param name="Width">Ancho de una imagen como fracción del ancho del video. Un texto ignora este valor.</param>
/// <param name="Opacity">De 0 (invisible) a 1 (opaco).</param>
public sealed record OverlayTransform(
    double CenterX = 0.5,
    double CenterY = 0.5,
    double Width = 0.25,
    double Opacity = 1)
{
    /// <summary>Ajusta todos los valores a su rango válido.</summary>
    public OverlayTransform Clamped() => new(
        Math.Clamp(CenterX, 0, 1),
        Math.Clamp(CenterY, 0, 1),
        Math.Clamp(Width, 0.02, 1),
        Math.Clamp(Opacity, 0, 1));
}

/// <summary>
/// Un texto o una imagen que se dibuja sobre el video durante un tramo de la timeline.
/// </summary>
/// <remarks>
/// A diferencia de los clips de la pista principal, un elemento superpuesto tiene posición
/// propia en la timeline y no ocupa lugar en el montaje: aparecer o desaparecer no desplaza
/// nada. Se coloca en pistas de superposición, apiladas sobre el video.
/// </remarks>
public sealed class OverlayItem
{
    /// <summary>Duración mínima de un elemento.</summary>
    public static TimeSpan MinimumDuration { get; } = TimeSpan.FromMilliseconds(200);

    private TimeSpan _start;
    private TimeSpan _duration;

    private OverlayItem(OverlayKind kind, TimeSpan start, TimeSpan duration, bool enforceMinimum = true)
    {
        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "La posición no puede ser negativa.");
        }

        if (duration <= TimeSpan.Zero || (enforceMinimum && duration < MinimumDuration))
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration), $"La duración mínima es {MinimumDuration.TotalMilliseconds:0} ms.");
        }

        Kind = kind;
        _start = start;
        _duration = duration;
    }

    /// <summary>Crea un texto.</summary>
    /// <param name="style">Estilo del texto.</param>
    /// <param name="start">Posición en la timeline.</param>
    /// <param name="duration">Cuánto tiempo se ve.</param>
    /// <param name="transform">Colocación inicial; sin ella, centrado y en la parte baja, como un rótulo.</param>
    public static OverlayItem CreateText(
        TextStyle style, TimeSpan start, TimeSpan duration, OverlayTransform? transform = null)
    {
        ArgumentNullException.ThrowIfNull(style);

        return new OverlayItem(OverlayKind.Text, start, duration)
        {
            Text = style,
            Transform = transform?.Clamped() ?? new OverlayTransform(0.5, 0.85),
        };
    }

    /// <summary>Crea una imagen.</summary>
    /// <param name="path">Archivo de imagen.</param>
    /// <param name="aspectRatio">Ancho entre alto de la imagen.</param>
    /// <param name="start">Posición en la timeline.</param>
    /// <param name="duration">Cuánto tiempo se ve.</param>
    public static OverlayItem CreateImage(string path, double aspectRatio, TimeSpan start, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(aspectRatio, 0);

        return new OverlayItem(OverlayKind.Image, start, duration)
        {
            ImagePath = path,
            AspectRatio = aspectRatio,
        };
    }

    /// <summary>Crea un video superpuesto.</summary>
    /// <param name="media">Archivo de video.</param>
    /// <param name="sourceIn">Instante del archivo donde empieza lo que se ve.</param>
    /// <param name="start">Posición en la timeline.</param>
    /// <param name="duration">Cuánto tiempo se ve.</param>
    /// <param name="transform">Colocación; sin ella, ocupa el cuadro entero.</param>
    /// <param name="playsAudio">Si su sonido entra en la mezcla.</param>
    /// <param name="audioGainDb">Volumen de su sonido, en dB.</param>
    public static OverlayItem CreateVideo(
        MediaInfo media,
        TimeSpan sourceIn,
        TimeSpan start,
        TimeSpan duration,
        OverlayTransform? transform = null,
        bool playsAudio = true,
        double audioGainDb = 0)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceIn, TimeSpan.Zero);

        if (media.IsGap)
        {
            throw new ArgumentException("Un hueco no se puede superponer.", nameof(media));
        }

        if (sourceIn + duration > media.Duration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration), "El video no tiene tanto material a partir de ese punto.");
        }

        return new OverlayItem(OverlayKind.Video, start, duration)
        {
            Media = media,
            SourceIn = sourceIn,
            AspectRatio = media.AspectRatio > 0 ? media.AspectRatio : 16.0 / 9,
            PlaysAudio = playsAudio && media.HasAudio,
            AudioGainDb = audioGainDb,
            Transform = (transform ?? FullFrame(media)).Clamped(),
        };
    }

    /// <summary>Colocación que deja el video ocupando el cuadro, respetando su proporción.</summary>
    /// <remarks>El lienzo de referencia es 16:9; un video más estrecho ocupa solo el ancho que le toca.</remarks>
    public static OverlayTransform FullFrame(MediaInfo media)
    {
        ArgumentNullException.ThrowIfNull(media);

        var aspect = media.AspectRatio > 0 ? media.AspectRatio : 16.0 / 9;
        return new OverlayTransform(0.5, 0.5, Math.Min(1.0, aspect / (16.0 / 9)), 1);
    }

    /// <summary>Identidad estable, para seguirlo entre operaciones y al deshacer.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Archivo de video; solo en los elementos de video.</summary>
    public MediaInfo? Media { get; private init; }

    /// <summary>Instante del archivo donde empieza lo que se ve; solo en los elementos de video.</summary>
    public TimeSpan SourceIn { get; private set; }

    /// <summary>Si el sonido del video entra en la mezcla; solo en los elementos de video.</summary>
    public bool PlaysAudio { get; private init; }

    /// <summary>Volumen del sonido del video, en dB; solo en los elementos de video.</summary>
    public double AudioGainDb { get; private init; }

    /// <summary>
    /// Comprueba que una colocación no se sale del material del video.
    /// </summary>
    /// <remarks>
    /// Recortar por la izquierda (cambian a la vez el inicio y la duración) avanza el punto de
    /// entrada; mover no lo toca. Los demás elementos no tienen nada que comprobar.
    /// </remarks>
    internal bool FitsSource(TimeSpan start, TimeSpan duration)
    {
        if (Kind != OverlayKind.Video || Media is null)
        {
            return true;
        }

        var sourceIn = SourceInAfter(start, duration);
        return sourceIn >= TimeSpan.Zero && sourceIn + duration <= Media.Duration;
    }

    private TimeSpan SourceInAfter(TimeSpan start, TimeSpan duration) =>
        duration != _duration && start != _start ? SourceIn + (start - _start) : SourceIn;

    /// <summary>Si es un texto o una imagen.</summary>
    public OverlayKind Kind { get; }

    /// <summary>Estilo del texto; solo en los elementos de texto.</summary>
    public TextStyle? Text { get; internal set; }

    /// <summary>Archivo de la imagen; solo en los elementos de imagen.</summary>
    public string? ImagePath { get; private init; }

    /// <summary>Ancho entre alto de la imagen; solo en los elementos de imagen.</summary>
    public double AspectRatio { get; private init; } = 1;

    /// <summary>Posición, tamaño y transparencia.</summary>
    public OverlayTransform Transform { get; internal set; } = new();

    /// <summary>
    /// Copia de la parte de este elemento que cae dentro de un intervalo, con los tiempos
    /// medidos desde el inicio del intervalo.
    /// </summary>
    /// <returns><see langword="null"/> si el elemento no se ve dentro del intervalo.</returns>
    /// <remarks>
    /// Reservado para trocear la secuencia (copias de preview). Un trozo puede durar menos
    /// que <see cref="MinimumDuration"/>: ese suelo protege al usuario al editar, no a una copia
    /// interna de lo que ya existe.
    /// </remarks>
    internal OverlayItem? Slice(TimeSpan from, TimeSpan to)
    {
        var start = _start > from ? _start : from;
        var end = End < to ? End : to;
        if (end <= start)
        {
            return null;
        }

        return new OverlayItem(Kind, start - from, end - start, enforceMinimum: false)
        {
            Text = Text,
            ImagePath = ImagePath,
            AspectRatio = AspectRatio,
            Transform = Transform,
            Media = Media,
            SourceIn = SourceIn + (start - _start),
            PlaysAudio = false,   // un trozo es solo imagen: el sonido sale de la mezcla, no de las copias
            AudioGainDb = AudioGainDb,
        };
    }

    /// <summary>Instante de la timeline en que aparece.</summary>
    public TimeSpan Start => _start;

    /// <summary>Cuánto tiempo se ve.</summary>
    public TimeSpan Duration => _duration;

    /// <summary>Instante de la timeline en que desaparece.</summary>
    public TimeSpan End => _start + _duration;

    /// <summary>Indica si se ve en un instante de la timeline.</summary>
    public bool IsVisibleAt(TimeSpan position) => position >= _start && position < End;

    /// <summary>Cambia la posición y la duración. La pista comprueba que no choque.</summary>
    internal void SetPlacement(TimeSpan start, TimeSpan duration)
    {
        if (start < TimeSpan.Zero || duration < MinimumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Colocación no válida.");
        }

        SourceIn = SourceInAfter(start, duration);
        _start = start;
        _duration = duration;
    }

    /// <inheritdoc/>
    public override string ToString() => Kind == OverlayKind.Text
        ? $"«{Text?.Content}» [{_start:mm\\:ss\\.ff} → {End:mm\\:ss\\.ff}]"
        : $"{Path.GetFileName(Kind == OverlayKind.Video ? Media?.Path : ImagePath)} [{_start:mm\\:ss\\.ff} → {End:mm\\:ss\\.ff}]";
}
