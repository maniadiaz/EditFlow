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
/// <param name="FontFamily">
/// Nombre de la tipografía instalada en el equipo, o <see langword="null"/> para la del sistema.
/// Se ignora si <paramref name="FontFilePath"/> está puesto.
/// </param>
/// <param name="FontFilePath">
/// Archivo de una tipografía propia (<c>.ttf</c>/<c>.otf</c>), traída de fuera en vez de elegida
/// de las instaladas en el equipo; <see langword="null"/> para no usar ninguna.
/// </param>
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
    bool Shadow = true,
    string? FontFamily = null,
    string? FontFilePath = null)
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
public sealed class OverlayItem : IAnimatable
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

    /// <summary>Ajuste de color; solo tiene efecto en los elementos de video.</summary>
    public ColorAdjust Color { get; set; } = ColorAdjust.None;

    /// <summary>Recorte por color (pantalla verde); solo tiene efecto en los elementos de video.</summary>
    public ChromaKey ChromaKey { get; set; } = ChromaKey.None;

    /// <summary>
    /// Animaciones: cómo se mueve, crece y aparece el elemento a lo largo del tiempo que se ve.
    /// </summary>
    /// <remarks>
    /// Es lo que permite que un título entre deslizándose o que un logotipo crezca. Lo que no
    /// esté animado sigue valiendo lo que diga <see cref="Transform"/>.
    /// </remarks>
    public Animation Animation { get; set; } = Animation.None;

    /// <summary>
    /// Colocación en un instante de la timeline, con la animación ya aplicada.
    /// </summary>
    /// <remarks>
    /// Es lo que dibuja el preview. Lo calcula el modelo, y no la interfaz, para que el mismo
    /// número que se ve al editar sea el que alimenta la expresión que va a FFmpeg: si cada uno
    /// interpolara por su cuenta, el preview y lo exportado acabarían discrepando.
    /// </remarks>
    public OverlayTransform TransformAt(TimeSpan position)
    {
        if (Animation.IsNone)
        {
            return Transform;
        }

        var local = position - _start;

        return new OverlayTransform(
            Animation.Track(AnimatedProperty.OffsetX).ValueAt(local, Transform.CenterX),
            Animation.Track(AnimatedProperty.OffsetY).ValueAt(local, Transform.CenterY),
            Animation.Track(AnimatedProperty.Width).ValueAt(local, Transform.Width),
            Animation.Track(AnimatedProperty.Opacity).ValueAt(local, Transform.Opacity)).Clamped();
    }

    /// <inheritdoc/>
    double IAnimatable.StaticValue(AnimatedProperty property) => property switch
    {
        AnimatedProperty.OffsetX => Transform.CenterX,
        AnimatedProperty.OffsetY => Transform.CenterY,
        AnimatedProperty.Width => Transform.Width,
        AnimatedProperty.Opacity => Transform.Opacity,
        _ => 0,
    };

    /// <inheritdoc/>
    /// <remarks>
    /// Una capa anima dónde está, cuánto ocupa y cuánto se transparenta. El giro no: el elemento
    /// se compone con <c>overlay</c>, que coloca pero no rota, y añadirlo obligaría a meter un
    /// filtro más en una rama que ya es la más cara del grafo.
    /// </remarks>
    bool IAnimatable.Supports(AnimatedProperty property) => property
        is AnimatedProperty.OffsetX
        or AnimatedProperty.OffsetY
        or AnimatedProperty.Width
        or AnimatedProperty.Opacity;

    /// <summary>Archivo de video; solo en los elementos de video.</summary>
    public MediaInfo? Media { get; internal set; }

    /// <summary>Instante del archivo donde empieza lo que se ve; solo en los elementos de video.</summary>
    public TimeSpan SourceIn { get; private set; }

    /// <summary>Si el sonido del video entra en la mezcla; solo en los elementos de video.</summary>
    public bool PlaysAudio { get; internal set; }

    /// <summary>Volumen del sonido del video, en dB; solo en los elementos de video.</summary>
    public double AudioGainDb { get; internal set; }

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

    private TimeSpan _fadeIn;
    private TimeSpan _fadeOut;

    /// <summary>Duración del fundido de aparición: entra con la opacidad subiendo en vez de golpe.</summary>
    public TimeSpan FadeIn
    {
        get => _fadeIn;
        set => _fadeIn = ClampFade(value, _fadeOut);
    }

    /// <summary>Duración del fundido de desaparición.</summary>
    public TimeSpan FadeOut
    {
        get => _fadeOut;
        set => _fadeOut = ClampFade(value, _fadeIn);
    }

    /// <summary>
    /// Acota un fundido para que, sumado al otro, no supere cuánto tiempo se ve el elemento.
    /// </summary>
    /// <remarks>Mismo cálculo que <see cref="Clip.FadeIn"/>: ver ahí el porqué.</remarks>
    private TimeSpan ClampFade(TimeSpan requested, TimeSpan other)
    {
        if (requested < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var room = Duration - other;
        return requested > room ? (room < TimeSpan.Zero ? TimeSpan.Zero : room) : requested;
    }

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

        // Igual que con el fundido de un clip de la pista principal: un fundido pensado para el
        // borde real del elemento no tiene sentido en un borde que solo existe porque el trozo
        // cortó por ahí.
        var keepsStart = start == _start;
        var keepsEnd = end == End;

        return new OverlayItem(Kind, start - from, end - start, enforceMinimum: false)
        {
            Text = Text,
            ImagePath = ImagePath,
            AspectRatio = AspectRatio,
            Transform = Transform,
            Media = Media,
            Color = Color,

            // El recorte por color no depende del tiempo: vale igual en cualquier trozo, al
            // contrario que los fundidos de aquí abajo.
            ChromaKey = ChromaKey,

            // La animación sí depende del tiempo, pero no se pierde al trocear: se queda con su
            // tramo, y con el valor que tenía justo en cada borde para que no haya saltos.
            Animation = Animation.Section(start - _start, end - _start),
            SourceIn = SourceIn + (start - _start),
            PlaysAudio = false,   // un trozo es solo imagen: el sonido sale de la mezcla, no de las copias
            AudioGainDb = AudioGainDb,
            FadeIn = keepsStart ? FadeIn : TimeSpan.Zero,
            FadeOut = keepsEnd ? FadeOut : TimeSpan.Zero,
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
    /// <summary>
    /// Fija el punto del archivo por el que empieza lo que se ve, sin mover el elemento.
    /// </summary>
    /// <remarks>Reservado para reconectar y sustituir el material, igual que en un clip.</remarks>
    internal void SetSourceIn(TimeSpan sourceIn) =>
        SourceIn = sourceIn < TimeSpan.Zero ? TimeSpan.Zero : sourceIn;

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
