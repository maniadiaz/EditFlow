// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;

namespace EditFlow.Core.Timeline;

/// <summary>
/// Un fragmento de audio colocado en una pista de audio.
/// </summary>
/// <remarks>
/// A diferencia de <see cref="Clip"/> en la pista de video, que hereda su posición del
/// orden, este guarda su propia posición: una música puede empezar en el segundo 7 sin
/// que nada ocupe el hueco anterior. Es la diferencia que hace falta para mezclar.
/// </remarks>
public sealed class AudioClip : IAnimatable
{
    /// <summary>Volumen mínimo admitido, en dB. Por debajo se considera silencio.</summary>
    public const double MinimumGainDb = -60;

    /// <summary>Volumen máximo admitido, en dB.</summary>
    /// <remarks>
    /// +12 dB es amplificar cuatro veces la señal. Más allá, casi cualquier material
    /// satura, y permitirlo solo llevaría a exportaciones distorsionadas.
    /// </remarks>
    public const double MaximumGainDb = 12;

    /// <summary>Duración mínima de un clip de audio, igual que la de los de video.</summary>
    public static TimeSpan MinimumDuration { get; } = TimeSpan.FromMilliseconds(40);

    private TimeSpan _sourceIn;
    private TimeSpan _sourceOut;
    private TimeSpan _timelineStart;
    private TimeSpan _fadeIn;
    private TimeSpan _fadeOut;
    private double _gainDb;

    /// <summary>Crea un clip de audio.</summary>
    /// <param name="source">Archivo del que procede el audio.</param>
    /// <param name="sourceIn">Instante del archivo donde empieza.</param>
    /// <param name="sourceOut">Instante del archivo donde termina.</param>
    /// <param name="timelineStart">Posición en la timeline.</param>
    /// <exception cref="ArgumentException">Si el archivo no tiene pista de audio.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Si el intervalo no es válido.</exception>
    public AudioClip(MediaInfo source, TimeSpan sourceIn, TimeSpan sourceOut, TimeSpan timelineStart)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.HasAudio)
        {
            throw new ArgumentException(
                $"'{Path.GetFileName(source.Path)}' no tiene pista de audio.", nameof(source));
        }

        if (sourceIn < TimeSpan.Zero || sourceOut > source.Duration || sourceOut <= sourceIn)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceOut), $"Intervalo de audio no válido: {sourceIn} a {sourceOut}.");
        }

        if (timelineStart < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineStart), "La posición no puede ser negativa.");
        }

        Source = source;
        _sourceIn = sourceIn;
        _sourceOut = sourceOut;
        _timelineStart = timelineStart;
    }

    /// <summary>Identidad estable, para seguirlo entre operaciones y al deshacer.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Archivo de origen.</summary>
    public MediaInfo Source { get; internal set; }

    /// <summary>Instante del archivo donde empieza.</summary>
    public TimeSpan SourceIn => _sourceIn;

    /// <summary>Instante del archivo donde termina.</summary>
    public TimeSpan SourceOut => _sourceOut;

    /// <summary>Duración en la timeline.</summary>
    public TimeSpan Duration => _sourceOut - _sourceIn;

    /// <summary>Posición en la timeline.</summary>
    public TimeSpan TimelineStart => _timelineStart;

    /// <summary>Instante de la timeline en que termina.</summary>
    public TimeSpan TimelineEnd => _timelineStart + Duration;

    /// <summary>Volumen del clip en dB; 0 deja la señal como está.</summary>
    public double GainDb
    {
        get => _gainDb;
        set => _gainDb = Math.Clamp(value, MinimumGainDb, MaximumGainDb);
    }

    /// <summary>Indica si el clip está silenciado.</summary>
    /// <remarks>
    /// Es un interruptor aparte del volumen: silenciar y volver a activar debe devolver el
    /// volumen que había, no dejarlo a cero.
    /// </remarks>
    public bool IsMuted { get; set; }

    /// <summary>Automatización del volumen: cómo sube y baja a lo largo del clip.</summary>
    /// <remarks>
    /// Es lo que permite agachar la música bajo una voz sin cortar el clip en trozos. Sin puntos,
    /// el volumen es el fijo de <see cref="GainDb"/>.
    /// </remarks>
    public Animation Animation { get; set; } = Animation.None;

    /// <summary>Volumen en dB en un instante del clip, con la automatización ya aplicada.</summary>
    /// <param name="offsetFromClipStart">Instante contado desde el inicio del clip.</param>
    public double GainAt(TimeSpan offsetFromClipStart) =>
        Animation.Track(AnimatedProperty.Volume).ValueAt(offsetFromClipStart, GainDb);

    /// <inheritdoc/>
    double IAnimatable.StaticValue(AnimatedProperty property) =>
        property == AnimatedProperty.Volume ? GainDb : 0;

    /// <inheritdoc/>
    /// <remarks>Un clip de audio no tiene imagen: lo único que hay que animar es su volumen.</remarks>
    bool IAnimatable.Supports(AnimatedProperty property) => property == AnimatedProperty.Volume;

    private double _pan;

    /// <summary>Balance estéreo: -1 deja solo el canal izquierdo, 1 solo el derecho, 0 no cambia nada.</summary>
    public double Pan
    {
        get => _pan;
        set => _pan = Math.Clamp(value, -1, 1);
    }

    /// <summary>Efecto de sonido (voz clara, quitar ruido, compresor...) sobre el propio audio del clip.</summary>
    public AudioEffectKind Effect { get; set; } = AudioEffectKind.None;

    /// <summary>Duración del fundido de entrada.</summary>
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
    /// Acota un fundido para que ambos, sumados, no superen la duración del clip.
    /// </summary>
    /// <remarks>
    /// Dos fundidos que se solaparan darían una curva de volumen incoherente: el clip
    /// estaría subiendo y bajando a la vez.
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
    /// Cambia a la vez el fragmento del archivo y la posición, y reajusta los fundidos.
    /// </summary>
    /// <remarks>
    /// Recortar por el inicio mueve las tres cosas juntas: el audio que se conserva tiene que
    /// seguir sonando en el mismo instante de la timeline, o el recorte desplazaría el clip.
    /// Es responsabilidad de la pista comprobar que no choque.
    /// </remarks>
    internal void SetRange(TimeSpan sourceIn, TimeSpan sourceOut, TimeSpan timelineStart)
    {
        if (sourceIn < TimeSpan.Zero || sourceOut > Source.Duration || sourceOut - sourceIn < MinimumDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceOut), $"Intervalo de audio no válido: {sourceIn} a {sourceOut}.");
        }

        _sourceIn = sourceIn;
        _sourceOut = sourceOut;
        _timelineStart = timelineStart < TimeSpan.Zero ? TimeSpan.Zero : timelineStart;

        // Un clip más corto puede no dejar sitio a los fundidos que tenía.
        _fadeIn = ClampFade(_fadeIn, TimeSpan.Zero);
        _fadeOut = ClampFade(_fadeOut, _fadeIn);
    }

    /// <summary>Restaura un estado guardado, incluidos los fundidos, sin reajustarlos.</summary>
    internal void Restore(TimeSpan sourceIn, TimeSpan sourceOut, TimeSpan timelineStart, TimeSpan fadeIn, TimeSpan fadeOut)
    {
        _sourceIn = sourceIn;
        _sourceOut = sourceOut;
        _timelineStart = timelineStart;
        _fadeIn = fadeIn;
        _fadeOut = fadeOut;
    }

    /// <summary>Mueve el clip. Es responsabilidad de la pista comprobar que no choque.</summary>
    internal void MoveTo(TimeSpan start)
    {
        _timelineStart = start < TimeSpan.Zero ? TimeSpan.Zero : start;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Path.GetFileName(Source.Path)} [{_timelineStart:mm\\:ss\\.ff} → {TimelineEnd:mm\\:ss\\.ff}]";
}
