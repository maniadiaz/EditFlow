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
public sealed class Clip
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
    public MediaInfo Source { get; }

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

    /// <summary>Ajuste de color (exposición, contraste, saturación, temperatura).</summary>
    public ColorAdjust Color { get; set; } = ColorAdjust.None;

    /// <summary>Transición desde el clip que precede a este en la pista principal.</summary>
    public Transition TransitionIn { get; set; } = Transition.None;

    /// <summary>Encuadre: zoom, posición y rotación sobre el propio fotograma.</summary>
    public ClipTransform Transform { get; set; } = ClipTransform.None;

    /// <summary>
    /// Indica si este clip aporta su propio sonido a la mezcla.
    /// </summary>
    /// <remarks>
    /// Un clip mudo, con el audio separado o silenciado no suena por su cuenta. Es la
    /// condición que comparten el preview y la exportación, para que ambos coincidan.
    /// </remarks>
    public bool HasOwnAudio => Source.HasAudio && !IsAudioDetached && !IsAudioMuted;

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
        Color = Color,
        TransitionIn = TransitionIn,
        Speed = Speed,
        Transform = Transform,
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
        var secondHalf = new Clip(Source, cutPoint, _sourceOut)
        {
            IsAudioDetached = IsAudioDetached,
            AudioGainDb = AudioGainDb,
            IsAudioMuted = IsAudioMuted,
            Color = Color,
            Speed = Speed,
            Transform = Transform,
        };
        _sourceOut = cutPoint;

        return secondHalf;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    /// <inheritdoc/>
    public override string ToString() =>
        $"{System.IO.Path.GetFileName(Source.Path)} [{_sourceIn:hh\\:mm\\:ss\\.fff} → {_sourceOut:hh\\:mm\\:ss\\.fff}]";
}
