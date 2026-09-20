namespace EditFlow.Core.Timeline;

/// <summary>
/// Pista de video principal: una secuencia contigua de clips.
/// </summary>
/// <remarks>
/// <para>
/// Los clips se reproducen uno tras otro sin huecos, y su posición en la timeline se
/// <b>deriva del orden</b> en lugar de almacenarse en cada clip. Es lo que hace que
/// "unir varios videos" sea simplemente añadirlos, y que eliminar uno cierre el hueco
/// automáticamente.
/// </para>
/// <para>
/// Guardar la posición en cada clip permitiría huecos y solapamientos, y obligaría a
/// validar continuamente que no se produzcan. Esta forma los hace imposibles por
/// construcción. Las pistas de posición libre —audio y texto— llegan en la Fase 2 como
/// un tipo distinto, porque ahí el solapamiento sí es deseable.
/// </para>
/// </remarks>
public sealed class VideoTimeline
{
    private readonly List<Clip> _clips = [];

    /// <summary>Clips en orden de reproducción.</summary>
    public IReadOnlyList<Clip> Clips => _clips;

    /// <summary>Duración total de la secuencia.</summary>
    public TimeSpan Duration
    {
        get
        {
            var total = TimeSpan.Zero;
            foreach (var clip in _clips)
            {
                total += clip.Duration;
            }

            return total;
        }
    }

    /// <summary>Indica si no hay ningún clip.</summary>
    public bool IsEmpty => _clips.Count == 0;

    /// <summary>Añade un clip al final.</summary>
    public void Append(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clips.Add(clip);
    }

    /// <summary>Inserta un clip en la posición indicada.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Si el índice está fuera de rango.</exception>
    public void Insert(int index, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _clips.Count);

        _clips.Insert(index, clip);
    }

    /// <summary>Elimina un clip; los siguientes se desplazan para cerrar el hueco.</summary>
    /// <returns><see langword="true"/> si el clip estaba en la secuencia.</returns>
    public bool Remove(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return _clips.Remove(clip);
    }

    /// <summary>Cambia un clip de posición dentro de la secuencia.</summary>
    /// <returns><see langword="true"/> si el clip se movió.</returns>
    public bool Move(Clip clip, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(clip);

        var currentIndex = _clips.IndexOf(clip);
        if (currentIndex < 0)
        {
            return false;
        }

        // Acotar en lugar de lanzar: el llamante habitual es un arrastre, y soltar el
        // clip más allá del extremo debe significar "ponlo al final", no fallar.
        newIndex = Math.Clamp(newIndex, 0, _clips.Count - 1);
        if (newIndex == currentIndex)
        {
            return false;
        }

        _clips.RemoveAt(currentIndex);
        _clips.Insert(newIndex, clip);
        return true;
    }

    /// <summary>Instante de la timeline en el que empieza un clip.</summary>
    /// <exception cref="ArgumentException">Si el clip no pertenece a esta secuencia.</exception>
    public TimeSpan StartOf(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        var start = TimeSpan.Zero;
        foreach (var current in _clips)
        {
            if (ReferenceEquals(current, clip))
            {
                return start;
            }

            start += current.Duration;
        }

        throw new ArgumentException("El clip no pertenece a esta timeline.", nameof(clip));
    }

    /// <summary>
    /// Localiza el clip que se reproduce en un instante dado.
    /// </summary>
    /// <param name="timelinePosition">Instante medido desde el inicio de la timeline.</param>
    /// <returns>
    /// El clip y el desplazamiento dentro de él, o <see langword="null"/> si la posición
    /// cae fuera de la secuencia.
    /// </returns>
    /// <remarks>
    /// El final de un clip pertenece al siguiente, no al que termina. Con dos clips de
    /// cinco segundos, el instante 5 es el primer fotograma del segundo clip. Tratarlo
    /// al revés haría que el cabezal mostrara un fotograma que ya no se ve.
    /// </remarks>
    public ClipAtPosition? ClipAt(TimeSpan timelinePosition)
    {
        if (timelinePosition < TimeSpan.Zero)
        {
            return null;
        }

        var start = TimeSpan.Zero;
        foreach (var clip in _clips)
        {
            var end = start + clip.Duration;
            if (timelinePosition < end)
            {
                return new ClipAtPosition(clip, timelinePosition - start);
            }

            start = end;
        }

        return null;
    }

    /// <summary>
    /// Divide en el instante indicado el clip que se reproduce ahí.
    /// </summary>
    /// <param name="timelinePosition">Punto de corte, medido desde el inicio de la timeline.</param>
    /// <returns>
    /// La segunda mitad, ya insertada tras la primera, o <see langword="null"/> si no hay
    /// clip en esa posición o el corte caería demasiado cerca de un borde.
    /// </returns>
    public Clip? SplitAt(TimeSpan timelinePosition)
    {
        var located = ClipAt(timelinePosition);
        if (located is null)
        {
            return null;
        }

        var secondHalf = located.Value.Clip.SplitAt(located.Value.Offset);
        if (secondHalf is null)
        {
            return null;
        }

        _clips.Insert(_clips.IndexOf(located.Value.Clip) + 1, secondHalf);
        return secondHalf;
    }

    /// <summary>Vacía la secuencia.</summary>
    public void Clear() => _clips.Clear();
}

/// <summary>Un clip junto con la posición relativa dentro de él.</summary>
/// <param name="Clip">Clip localizado.</param>
/// <param name="Offset">Desplazamiento desde el inicio del clip.</param>
public readonly record struct ClipAtPosition(Clip Clip, TimeSpan Offset);
