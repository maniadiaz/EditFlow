// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;

namespace EditFlow.Core.Timeline;

/// <summary>
/// Un fragmento de un archivo de video colocado en la timeline.
/// </summary>
/// <remarks>
/// Un clip nunca copia ni modifica el archivo original: solo guarda qué intervalo de él
/// debe reproducirse. Cortar un clip en dos produce dos clips que apuntan al mismo
/// archivo con intervalos distintos, sin tocar un solo byte en disco.
/// </remarks>
public sealed class Clip : IAnimatable
{
    private TimeSpan _sourceIn;
    private TimeSpan _sourceOut;

    /// <summary>Crea un clip que abarca el archivo completo.</summary>
    public Clip(MediaInfo source)
        : this(source, TimeSpan.Zero, source?.Duration ?? TimeSpan.Zero)
    {
    }

    /// <summary>Crea un clip que abarca el intervalo indicado del archivo.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Si el intervalo está vacío, invertido o se sale del archivo.
    /// </exception>
    public Clip(MediaInfo source, TimeSpan sourceIn, TimeSpan sourceOut)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (sourceIn < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIn), sourceIn,
                "El punto de entrada no puede ser negativo.");
        }

        if (sourceOut > source.Duration)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceOut), sourceOut,
                $"El punto de salida excede la duración del archivo ({source.Duration}).");
        }

        if (sourceOut <= sourceIn)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceOut), sourceOut,
                "El punto de salida debe ser posterior al de entrada.");
        }

        Source = source;
        _sourceIn = sourceIn;
        _sourceOut = sourceOut;
    }

    /// <summary>Crea un hueco (tiempo en negro) de la duración indicada.</summary>
    public static Clip CreateGap(TimeSpan duration) => new(MediaInfo.Gap, TimeSpan.Zero, duration);

    /// <summary>Indica si es un hueco: no tiene archivo, ni imagen, ni sonido.</summary>
    public bool IsGap => Source.IsGap;

    /// <summary>Identidad estable del clip, para seguirlo entre operaciones y al deshacer.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Archivo de origen.</summary>
    /// <remarks>
    /// Se puede cambiar, pero solo desde dentro del modelo: es lo que hacen reconectar un archivo
    /// que se movió y sustituir el material de un clip. El intervalo no se toca aquí; de acotarlo
    /// al nuevo archivo se encarga quien ordena el cambio, que es el único que sabe cómo deshacerlo.
    /// </remarks>
    public MediaInfo Source { get; internal set; }

    /// <summary>Instante del archivo origen donde empieza el clip.</summary>
    public TimeSpan SourceIn => _sourceIn;

    /// <summary>Instante del archivo origen donde termina el clip.</summary>
    public TimeSpan SourceOut => _sourceOut;

    /// <summary>Cuánto material del archivo origen usa el clip, sin descontar la velocidad.</summary>
    /// <remarks>
    /// Es lo que se lee del archivo (el <c>-t</c> del recorte al exportar, o lo que limita
    /// <see cref="TrimStart"/>/<see cref="TrimEnd"/>); no cambia si se ajusta la velocidad,
    /// solo cambia cuánto tiempo ocupa eso en la timeline.
    /// </remarks>
    public TimeSpan SourceDuration => _sourceOut - _sourceIn;

    private double _speed = 1;

    /// <summary>
    /// Velocidad de reproducción: 1 deja el clip como está, 2 lo reproduce al doble (dura la
    /// mitad en la timeline), 0.5 a cámara lenta (dura el doble).
    /// </summary>
    public double Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, MinimumSpeed, MaximumSpeed);
    }

    /// <summary>Tope inferior de <see cref="Speed"/>: por debajo, un clip corto ocuparía minutos de timeline.</summary>
    public const double MinimumSpeed = 0.1;

    /// <summary>Tope superior de <see cref="Speed"/>.</summary>
    public const double MaximumSpeed = 16;

    /// <summary>Duración del clip en la timeline, ya con la velocidad aplicada.</summary>
    public TimeSpan Duration =>
        TimeSpan.FromTicks((long)Math.Round(SourceDuration.Ticks / _speed));

    /// <summary>
    /// Convierte un desplazamiento medido en el tiempo de la timeline (desde el propio inicio
    /// del clip) al desplazamiento equivalente dentro del archivo origen.
    /// </summary>
    /// <remarks>
    /// A velocidad 1 son el mismo número; a cualquier otra, hay que multiplicar por la
    /// velocidad para saber qué punto del archivo corresponde a un instante de la timeline.
    /// Centralizado aquí en vez de repetido en cada sitio que lo necesita (recorte por
    /// arrastre, tiras de fotogramas, reproducción en vivo), para que todos coincidan.
    /// </remarks>
    public TimeSpan SourceTimeAt(TimeSpan timelineOffset) =>
        TimeSpan.FromTicks((long)Math.Round(timelineOffset.Ticks * _speed));

    /// <summary>
    /// Indica que el audio de este clip se separó y ahora vive en una pista de audio.
    /// </summary>
    /// <remarks>
    /// Cuando es cierto, el clip de video no aporta su propio sonido: de lo contrario el
    /// audio sonaría dos veces, una desde el video y otra desde la pista.
    /// </remarks>
    public bool IsAudioDetached { get; internal set; }

    private double _audioGainDb;

    /// <summary>Volumen del propio audio del clip, en dB; 0 deja la señal como está.</summary>
    /// <remarks>
    /// Mismo rango que un clip de audio: más de +12 dB satura casi cualquier material, y
    /// permitirlo solo llevaría a exportaciones distorsionadas.
    /// </remarks>
    public double AudioGainDb
    {
        get => _audioGainDb;
        set => _audioGainDb = Math.Clamp(value, AudioClip.MinimumGainDb, AudioClip.MaximumGainDb);
    }

    /// <summary>Silencia el audio del propio clip sin separarlo a otra pista.</summary>
    public bool IsAudioMuted { get; set; }

    private double _pan;

    /// <summary>Balance estéreo del propio audio del clip: -1 solo el canal izquierdo, 1 solo el derecho.</summary>
    public double Pan
    {
        get => _pan;
        set => _pan = Math.Clamp(value, -1, 1);
    }

    /// <summary>Efecto de sonido (voz clara, quitar ruido, compresor...) sobre el propio audio del clip.</summary>
    public AudioEffectKind AudioEffect { get; set; } = AudioEffectKind.None;

    /// <summary>Ajuste de color (exposición, contraste, saturación, temperatura).</summary>
    public ColorAdjust Color { get; set; } = ColorAdjust.None;

    /// <summary>Transición desde el clip que precede a este en la pista principal.</summary>
    public Transition TransitionIn { get; set; } = Transition.None;

    /// <summary>Encuadre: zoom, posición y rotación sobre el propio fotograma.</summary>
    public ClipTransform Transform { get; set; } = ClipTransform.None;

    /// <summary>
    /// Animaciones del encuadre: cómo cambian el zoom, la posición y el giro a lo largo del clip.
    /// </summary>
    /// <remarks>
    /// Vacía por defecto. Lo que no esté animado sigue valiendo lo que diga <see cref="Transform"/>,
    /// así que poner un punto en una propiedad no toca las demás.
    /// </remarks>
    public Animation Animation { get; set; } = Animation.None;

    /// <summary>
    /// Corrección de color avanzada: curvas, ruedas, color selectivo y LUT.
    /// </summary>
    /// <remarks>
    /// Se aplica después de <see cref="Color"/>, que son los cuatro deslizadores rápidos. Los dos
    /// conviven a propósito: el ajuste de siempre resuelve casi todo, y este entra cuando no basta.
    /// </remarks>
    public ColorGrade Grade { get; set; } = ColorGrade.None;

    /// <summary>
    /// Encuadre en un instante del clip, con la animación ya aplicada.
    /// </summary>
    /// <param name="offsetFromClipStart">Instante contado desde el inicio del clip.</param>
    public ClipTransform TransformAt(TimeSpan offsetFromClipStart)
    {
        if (Animation.IsNone)
        {
            return Transform;
        }

        return new ClipTransform(
            Animation.Track(AnimatedProperty.Scale).ValueAt(offsetFromClipStart, Transform.Scale),
            Animation.Track(AnimatedProperty.OffsetX).ValueAt(offsetFromClipStart, Transform.OffsetX),
            Animation.Track(AnimatedProperty.OffsetY).ValueAt(offsetFromClipStart, Transform.OffsetY),
            Animation.Track(AnimatedProperty.Rotation).ValueAt(offsetFromClipStart, Transform.Rotation)).Clamped();
    }

    /// <inheritdoc/>
    double IAnimatable.StaticValue(AnimatedProperty property) => property switch
    {
        AnimatedProperty.Scale => Transform.Scale,
        AnimatedProperty.OffsetX => Transform.OffsetX,
        AnimatedProperty.OffsetY => Transform.OffsetY,
        AnimatedProperty.Rotation => Transform.Rotation,
        _ => 0,
    };

    /// <inheritdoc/>
    /// <remarks>
    /// Un clip de la pista principal anima su encuadre. La opacidad y el ancho son de las capas
    /// —debajo de un clip principal no hay nada que dejar ver— y el volumen, de un clip de audio.
    /// </remarks>
    bool IAnimatable.Supports(AnimatedProperty property) => property
        is AnimatedProperty.Scale
        or AnimatedProperty.OffsetX
        or AnimatedProperty.OffsetY
        or AnimatedProperty.Rotation;

    /// <summary>Filtro de aspecto (blanco y negro, sepia…) sobre la imagen del clip.</summary>
    public VisualFilterKind Filter { get; set; } = VisualFilterKind.None;

    /// <summary>Efecto de estilo (VHS, grano, desenfoque…) sobre la imagen del clip.</summary>
    public VisualEffectKind Effect { get; set; } = VisualEffectKind.None;

    private TimeSpan _fadeIn;
    private TimeSpan _fadeOut;

    /// <summary>Duración del fundido de entrada, a negro (imagen) y a silencio (el propio audio).</summary>
    public TimeSpan FadeIn
    {
        get => _fadeIn;
        set => _fadeIn = ClampFade(value, _fadeOut);
    }

    /// <summary>Duración del fundido de salida.</summary>
    public TimeSpan FadeOut
    {
        get => _fadeOut;
        set => _fadeOut = ClampFade(value, _fadeIn);
    }

    /// <summary>
    /// Acota un fundido para que, sumado al otro, no supere la duración del clip en la timeline.
    /// </summary>
    /// <remarks>
    /// Dos fundidos que se solaparan darían una curva incoherente, subiendo y bajando a la
    /// vez. Se acota contra <see cref="Duration"/> —la de la timeline, ya con la velocidad
    /// aplicada— porque es el tiempo en el que de verdad se ve el fundido.
    /// </remarks>
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
    /// Indica si este clip aporta su propio sonido a la mezcla.
    /// </summary>
    /// <remarks>
    /// Un clip mudo, con el audio separado o silenciado no suena por su cuenta. Es la
    /// condición que comparten el preview y la exportación, para que ambos coincidan.
    /// </remarks>
    /// <remarks>
    /// Un archivo que no se encontró no aporta nada: se trata como un clip sin pista de sonido, y
    /// el grafo lo sustituye por silencio. Así el preview sigue sonando con lo que sí está mientras
    /// se reconecta lo que falta, en vez de no sonar nada.
    /// </remarks>
    public bool HasOwnAudio => Source.HasAudio && !Source.IsOffline && !IsAudioDetached && !IsAudioMuted;

    /// <summary>Duración mínima admitida para un clip.</summary>
    /// <remarks>
    /// Sin este suelo, arrastrar el borde de un clip hasta pasarse produciría clips de
    /// duración cero o negativa que romperían el grafo de filtros al exportar.
    /// </remarks>
    public static TimeSpan MinimumDuration { get; } = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Restaura un intervalo exacto, sin acotar.
    /// </summary>
    /// <remarks>
    /// Reservado para deshacer. Los métodos públicos de recorte acotan al material
    /// disponible, lo que es correcto para un arrastre pero impide volver a un estado
    /// previo de forma exacta: deshacer dos recortes seguidos acumularía el redondeo
    /// y el clip no recuperaría su tamaño original.
    /// </remarks>
    internal void RestoreRange(TimeSpan sourceIn, TimeSpan sourceOut)
    {
        _sourceIn = sourceIn;
        _sourceOut = sourceOut;
    }

    /// <summary>Crea una copia independiente con su propia identidad.</summary>
    public Clip Clone() => new(Source, _sourceIn, _sourceOut)
    {
        IsAudioDetached = IsAudioDetached,
        AudioGainDb = AudioGainDb,
        IsAudioMuted = IsAudioMuted,
        Pan = Pan,
        AudioEffect = AudioEffect,
        Color = Color,
        TransitionIn = TransitionIn,
        Speed = Speed,
        Transform = Transform,
        Filter = Filter,
        Effect = Effect,
        FadeIn = FadeIn,
        FadeOut = FadeOut,
        Animation = Animation,
        Grade = Grade,
    };

    /// <summary>
    /// Ajusta el borde de entrada, acotado al material disponible.
    /// </summary>
    /// <param name="delta">Desplazamiento; positivo recorta, negativo extiende.</param>
    /// <returns>El desplazamiento realmente aplicado tras acotar.</returns>
    /// <remarks>
    /// Devuelve lo aplicado en lugar de lanzar una excepción porque el llamante habitual
    /// es un arrastre con el ratón: al llegar al límite el clip debe dejar de crecer, no
    /// interrumpir la interacción con un error.
    /// </remarks>
    public TimeSpan TrimStart(TimeSpan delta)
    {
        var lowerBound = TimeSpan.Zero;
        var upperBound = _sourceOut - MinimumDuration;

        var target = Clamp(_sourceIn + delta, lowerBound, upperBound);
        var applied = target - _sourceIn;
        _sourceIn = target;
        return applied;
    }

    /// <summary>
    /// Ajusta el borde de salida, acotado al material disponible.
    /// </summary>
    /// <param name="delta">Desplazamiento; positivo extiende, negativo recorta.</param>
    /// <returns>El desplazamiento realmente aplicado tras acotar.</returns>
    public TimeSpan TrimEnd(TimeSpan delta)
    {
        var lowerBound = _sourceIn + MinimumDuration;
        var upperBound = Source.Duration;

        var target = Clamp(_sourceOut + delta, lowerBound, upperBound);
        var applied = target - _sourceOut;
        _sourceOut = target;
        return applied;
    }

    /// <summary>
    /// Divide el clip en el instante indicado, medido desde su propio inicio.
    /// </summary>
    /// <param name="offsetFromClipStart">Punto de corte dentro del clip.</param>
    /// <returns>
    /// La segunda mitad. Este clip pasa a ser la primera. Devuelve <see langword="null"/>
    /// si el corte dejaría alguna mitad por debajo de <see cref="MinimumDuration"/>.
    /// </returns>
    public Clip? SplitAt(TimeSpan offsetFromClipStart)
    {
        if (offsetFromClipStart < MinimumDuration ||
            offsetFromClipStart > Duration - MinimumDuration)
        {
            return null;
        }

        // El punto de corte se pide en tiempo de timeline; a una velocidad distinta de 1, un
        // segundo de timeline no es un segundo de archivo.
        var cutPoint = _sourceIn + SourceTimeAt(offsetFromClipStart);
        // La segunda mitad hereda si el audio estaba separado. Si no, al cortar un clip cuyo
        // audio ya vive en una pista, esa mitad volvería a sonar por su cuenta y el audio
        // se oiría duplicado a partir del corte.
        // Un fundido pensado para el borde original ya no tiene sentido en el borde nuevo que
        // deja el corte: el de entrada se queda con la primera mitad, el de salida con la
        // segunda, y cada una pierde el que ya no le corresponde.
        var secondHalf = new Clip(Source, cutPoint, _sourceOut)
        {
            IsAudioDetached = IsAudioDetached,
            AudioGainDb = AudioGainDb,
            IsAudioMuted = IsAudioMuted,
            Pan = Pan,
            AudioEffect = AudioEffect,
            Color = Color,
            Speed = Speed,
            Transform = Transform,
            Filter = Filter,
            Effect = Effect,
            FadeOut = FadeOut,
            Grade = Grade,

            // Los puntos de animación se cuentan desde el inicio del clip, así que cada mitad se
            // queda con su tramo recolocado a cero. Repartirlos sin recolocar dejaría la segunda
            // mitad animándose con los tiempos de la primera.
            Animation = Animation.Section(offsetFromClipStart, Duration),
        };
        Animation = Animation.Section(TimeSpan.Zero, offsetFromClipStart);
        _sourceOut = cutPoint;
        FadeOut = TimeSpan.Zero;

        return secondHalf;
    }

    /// <summary>
    /// Fija el intervalo del archivo que se usa, sin comprobar nada más.
    /// </summary>
    /// <remarks>
    /// Reservado para reconectar y sustituir el material: ahí el intervalo ya viene calculado
    /// contra el archivo nuevo, y deshacer necesita poder devolverlo tal cual estaba aunque no
    /// cupiera en el archivo de ahora. Los recortes normales pasan por <c>TrimStart</c>
    /// y <c>TrimEnd</c>, que sí acotan.
    /// </remarks>
    internal void SetRange(TimeSpan sourceIn, TimeSpan sourceOut)
    {
        _sourceIn = sourceIn < TimeSpan.Zero ? TimeSpan.Zero : sourceIn;
        _sourceOut = sourceOut > _sourceIn ? sourceOut : _sourceIn + MinimumDuration;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    /// <inheritdoc/>
    public override string ToString() =>
        $"{System.IO.Path.GetFileName(Source.Path)} [{_sourceIn:hh\\:mm\\:ss\\.fff} → {_sourceOut:hh\\:mm\\:ss\\.fff}]";
}
