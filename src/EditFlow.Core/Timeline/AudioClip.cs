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
public sealed class AudioClip
{
    /// <summary>Volumen mínimo admitido, en dB. Por debajo se considera silencio.</summary>
    public const double MinimumGainDb = -60;

    /// <summary>Volumen máximo admitido, en dB.</summary>
    /// <remarks>
    /// +12 dB es amplificar cuatro veces la señal. Más allá, casi cualquier material
    /// satura, y permitirlo solo llevaría a exportaciones distorsionadas.
    /// </remarks>
    public const double MaximumGainDb = 12;

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
    public MediaInfo Source { get; }

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

    /// <summary>Mueve el clip. Es responsabilidad de la pista comprobar que no choque.</summary>
    internal void MoveTo(TimeSpan start)
    {
        _timelineStart = start < TimeSpan.Zero ? TimeSpan.Zero : start;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Path.GetFileName(Source.Path)} [{_timelineStart:mm\\:ss\\.ff} → {TimelineEnd:mm\\:ss\\.ff}]";
}
