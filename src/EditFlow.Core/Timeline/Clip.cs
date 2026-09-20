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

    /// <summary>Identidad estable del clip, para seguirlo entre operaciones y al deshacer.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Archivo de origen.</summary>
    public MediaInfo Source { get; }

    /// <summary>Instante del archivo origen donde empieza el clip.</summary>
    public TimeSpan SourceIn => _sourceIn;

    /// <summary>Instante del archivo origen donde termina el clip.</summary>
    public TimeSpan SourceOut => _sourceOut;

    /// <summary>Duración del clip en la timeline.</summary>
    public TimeSpan Duration => _sourceOut - _sourceIn;

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
    public Clip Clone() => new(Source, _sourceIn, _sourceOut);

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

        var cutPoint = _sourceIn + offsetFromClipStart;
        var secondHalf = new Clip(Source, cutPoint, _sourceOut);
        _sourceOut = cutPoint;

        return secondHalf;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    /// <inheritdoc/>
    public override string ToString() =>
        $"{System.IO.Path.GetFileName(Source.Path)} [{_sourceIn:hh\\:mm\\:ss\\.fff} → {_sourceOut:hh\\:mm\\:ss\\.fff}]";
}
