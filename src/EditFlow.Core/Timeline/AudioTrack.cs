// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EditFlow.Core.Timeline;

/// <summary>
/// Una pista de audio: clips con posición libre que no pueden solaparse entre sí.
/// </summary>
/// <remarks>
/// Dos clips solapados en una misma pista sonarían a la vez sin que nada lo indique,
/// que es justo lo que las pistas separadas existen para evitar. Para mezclar dos
/// sonidos se usan dos pistas; aquí el solapamiento se rechaza en lugar de tolerarse.
/// </remarks>
public sealed class AudioTrack
{
    private readonly List<AudioClip> _clips = [];

    /// <summary>Crea una pista con el nombre indicado.</summary>
    public AudioTrack(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Identidad estable de la pista.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Nombre visible, por ejemplo <c>A1</c> o <c>Música</c>.</summary>
    public string Name { get; set; }

    /// <summary>Clips ordenados por posición.</summary>
    public IReadOnlyList<AudioClip> Clips => _clips;

    /// <summary>Silencia toda la pista.</summary>
    public bool IsMuted { get; set; }

    /// <summary>Solo suena esta pista, silenciando las demás.</summary>
    public bool IsSolo { get; set; }

    /// <summary>Impide editar los clips de la pista.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Volumen de toda la pista en dB.</summary>
    public double GainDb { get; set; }

    /// <summary>Instante en que termina el último clip.</summary>
    public TimeSpan End => _clips.Count == 0 ? TimeSpan.Zero : _clips[^1].TimelineEnd;

    /// <summary>
    /// Comprueba si un intervalo cabe en la pista sin chocar con ningún clip.
    /// </summary>
    /// <param name="start">Inicio del intervalo.</param>
    /// <param name="duration">Duración del intervalo.</param>
    /// <param name="ignore">Clip que se está moviendo y no debe contar como obstáculo.</param>
    /// <remarks>
    /// Dos intervalos que solo se tocan por el borde no chocan: uno termina exactamente
    /// donde empieza el otro. Tratarlos como choque impediría colocar clips seguidos.
    /// </remarks>
    public bool CanPlace(TimeSpan start, TimeSpan duration, AudioClip? ignore = null)
    {
        if (start < TimeSpan.Zero || duration <= TimeSpan.Zero)
        {
            return false;
        }

        var end = start + duration;

        foreach (var clip in _clips)
        {
            if (ReferenceEquals(clip, ignore))
            {
                continue;
            }

            if (start < clip.TimelineEnd && clip.TimelineStart < end)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Añade un clip; falla si choca con otro.</summary>
    /// <returns><see langword="true"/> si se añadió.</returns>
    public bool TryAdd(AudioClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        if (IsLocked || !CanPlace(clip.TimelineStart, clip.Duration))
        {
            return false;
        }

        Insert(clip);
        return true;
    }

    /// <summary>Elimina un clip.</summary>
    public bool Remove(AudioClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return !IsLocked && _clips.Remove(clip);
    }

    /// <summary>Mueve un clip a otra posición de la misma pista.</summary>
    /// <returns><see langword="true"/> si se movió; falso si chocaba o la pista está bloqueada.</returns>
    public bool TryMove(AudioClip clip, TimeSpan newStart)
    {
        ArgumentNullException.ThrowIfNull(clip);

        if (IsLocked || !_clips.Contains(clip) || !CanPlace(newStart, clip.Duration, clip))
        {
            return false;
        }

        _clips.Remove(clip);
        clip.MoveTo(newStart);
        Insert(clip);
        return true;
    }

    /// <summary>Recorta uno de los bordes de un clip, llevándolo a otra posición de la timeline.</summary>
    /// <param name="clip">Clip a recortar.</param>
    /// <param name="edge">Borde que se mueve.</param>
    /// <param name="position">Nueva posición de ese borde en la timeline.</param>
    /// <returns>
    /// <see langword="true"/> si se aplicó. Se rechaza si el clip quedaría por debajo de la
    /// duración mínima, pediría más audio del que tiene el archivo o chocaría con otro clip.
    /// </returns>
    /// <remarks>
    /// Se rechaza en lugar de acotar: a diferencia de la pista de video, aquí los clips tienen
    /// vecinos con posición propia, y ajustar en silencio a otro valor haría que el clip acabe
    /// en un sitio distinto del que el usuario señaló.
    /// </remarks>
    public bool TryTrim(AudioClip clip, ClipEdge edge, TimeSpan position)
    {
        if (!Evaluate(clip, edge, position, out var sourceIn, out var sourceOut, out var start))
        {
            return false;
        }

        _clips.Remove(clip);
        clip.SetRange(sourceIn, sourceOut, start);
        Insert(clip);
        return true;
    }

    /// <summary>Indica si un recorte se aplicaría, sin aplicarlo. Sirve para la vista previa del arrastre.</summary>
    public bool CanTrim(AudioClip clip, ClipEdge edge, TimeSpan position) =>
        Evaluate(clip, edge, position, out _, out _, out _);

    private bool Evaluate(
        AudioClip clip, ClipEdge edge, TimeSpan position,
        out TimeSpan sourceIn, out TimeSpan sourceOut, out TimeSpan start)
    {
        ArgumentNullException.ThrowIfNull(clip);

        sourceIn = clip.SourceIn;
        sourceOut = clip.SourceOut;
        start = clip.TimelineStart;

        if (IsLocked || !_clips.Contains(clip))
        {
            return false;
        }

        if (edge == ClipEdge.Start)
        {
            sourceIn += position - clip.TimelineStart;
            start = position;
        }
        else
        {
            sourceOut += position - clip.TimelineEnd;
        }

        return start >= TimeSpan.Zero &&
               sourceIn >= TimeSpan.Zero &&
               sourceOut <= clip.Source.Duration &&
               sourceOut - sourceIn >= AudioClip.MinimumDuration &&
               CanPlace(start, sourceOut - sourceIn, clip);
    }

    /// <summary>Devuelve un clip a un estado anterior, sin comprobar choques: ese estado ya era válido.</summary>
    internal void Reinsert(
        AudioClip clip, TimeSpan sourceIn, TimeSpan sourceOut, TimeSpan start, TimeSpan fadeIn, TimeSpan fadeOut)
    {
        _clips.Remove(clip);
        clip.Restore(sourceIn, sourceOut, start, fadeIn, fadeOut);
        Insert(clip);
    }

    /// <summary>Divide en dos el clip que suena en un instante de la timeline.</summary>
    /// <returns>La segunda mitad, o <see langword="null"/> si no hay clip o el corte dejaría una mitad demasiado corta.</returns>
    public AudioClip? SplitAt(TimeSpan position)
    {
        if (IsLocked)
        {
            return null;
        }

        var clip = _clips.FirstOrDefault(c => c.TimelineStart <= position && position < c.TimelineEnd);
        if (clip is null)
        {
            return null;
        }

        var offset = position - clip.TimelineStart;
        if (offset < AudioClip.MinimumDuration || clip.Duration - offset < AudioClip.MinimumDuration)
        {
            return null;
        }

        var cut = clip.SourceIn + offset;
        var second = new AudioClip(clip.Source, cut, clip.SourceOut, position)
        {
            GainDb = clip.GainDb,
            IsMuted = clip.IsMuted,
        };

        // El fundido de entrada se queda en la primera mitad y el de salida pasa a la
        // segunda: cortar no debe inventar fundidos en el punto de corte, que sonaría como
        // un bache de volumen.
        var fadeOut = clip.FadeOut;
        clip.SetRange(clip.SourceIn, cut, clip.TimelineStart);
        clip.FadeOut = TimeSpan.Zero;
        second.FadeOut = fadeOut;

        Insert(second);
        return second;
    }

    /// <summary>Indica si esta pista debe oírse dada la situación del resto.</summary>
    /// <param name="anySolo">Si alguna pista de la secuencia está en solo.</param>
    /// <remarks>
    /// Con una pista en solo, las demás callan aunque no estén silenciadas. Es la
    /// razón de ser del solo: escuchar una sola sin tener que silenciar las otras una a una.
    /// </remarks>
    public bool IsAudible(bool anySolo) => !IsMuted && (!anySolo || IsSolo);

    private void Insert(AudioClip clip)
    {
        // Ordenado por posición: el mezclador y la interfaz recorren la pista de
        // izquierda a derecha y no deberían ordenarla cada vez.
        var index = _clips.FindIndex(c => c.TimelineStart > clip.TimelineStart);
        if (index < 0)
        {
            _clips.Add(clip);
        }
        else
        {
            _clips.Insert(index, clip);
        }
    }
}
