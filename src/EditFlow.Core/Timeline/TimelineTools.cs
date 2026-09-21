// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>
/// Herramientas de edición fina sobre la pista de video: deslizar el contenido, mover un
/// corte y deslizar un clip entre sus vecinos.
/// </summary>
/// <remarks>
/// <para>
/// Las tres <b>conservan la duración total del montaje</b>. Es lo que las distingue del
/// recorte normal, que alarga o acorta la pista entera y desplaza todo lo que viene detrás.
/// Aquí lo que crece por un lado mengua por el otro, así que el resto del montaje —y todo lo
/// que esté sincronizado con él, como la música— no se mueve ni un fotograma.
/// </para>
/// <para>
/// Cada herramienta ofrece dos métodos: <c>Clamp*</c>, que calcula qué desplazamiento cabe sin
/// tocar nada (para la vista previa de un arrastre), y el que lo aplica y devuelve lo aplicado.
/// Como en un arrastre, se acota al material disponible en lugar de rechazar.
/// </para>
/// </remarks>
public static class TimelineTools
{
    // ---------------------------------------------------------------- slip

    /// <summary>
    /// Desplazamiento de <c>slip</c> que cabe: cuánto puede correrse el fragmento dentro del archivo.
    /// </summary>
    /// <param name="clip">Clip cuyo contenido se desliza.</param>
    /// <param name="delta">Desplazamiento pedido; positivo muestra un momento posterior del archivo.</param>
    public static TimeSpan ClampSlip(Clip clip, TimeSpan delta)
    {
        ArgumentNullException.ThrowIfNull(clip);

        var lowest = -clip.SourceIn;
        var highest = clip.Source.Duration - clip.SourceOut;
        return Clamp(delta, lowest, highest);
    }

    /// <summary>
    /// Cambia qué fragmento del archivo se ve, sin cambiar la duración ni la posición del clip.
    /// </summary>
    /// <returns>El desplazamiento realmente aplicado.</returns>
    public static TimeSpan Slip(Clip clip, TimeSpan delta)
    {
        var applied = ClampSlip(clip, delta);
        clip.RestoreRange(clip.SourceIn + applied, clip.SourceOut + applied);
        return applied;
    }

    // ---------------------------------------------------------------- roll

    /// <summary>Desplazamiento de <c>roll</c> que cabe para el corte entre un clip y el siguiente.</summary>
    /// <param name="timeline">Pista.</param>
    /// <param name="left">Clip anterior al corte.</param>
    /// <param name="delta">Desplazamiento pedido; positivo retrasa el corte.</param>
    /// <returns>Cero si <paramref name="left"/> es el último clip y no hay corte que mover.</returns>
    public static TimeSpan ClampRoll(VideoTimeline timeline, Clip left, TimeSpan delta)
    {
        var right = NeighbourAfter(timeline, left);
        return right is null ? TimeSpan.Zero : ClampBoundary(left, right, delta);
    }

    /// <summary>
    /// Mueve el corte entre dos clips contiguos: uno gana lo que el otro pierde.
    /// </summary>
    /// <returns>El desplazamiento realmente aplicado.</returns>
    public static TimeSpan Roll(VideoTimeline timeline, Clip left, TimeSpan delta)
    {
        var right = NeighbourAfter(timeline, left);
        return right is null ? TimeSpan.Zero : MoveBoundary(left, right, delta);
    }

    // --------------------------------------------------------------- slide

    /// <summary>Desplazamiento de <c>slide</c> que cabe para un clip entre sus dos vecinos.</summary>
    /// <param name="timeline">Pista.</param>
    /// <param name="clip">Clip que se desliza.</param>
    /// <param name="delta">Desplazamiento pedido; positivo lo retrasa.</param>
    /// <returns>Cero si le falta un vecino: el primero y el último clip no tienen a ambos lados con qué compensar.</returns>
    public static TimeSpan ClampSlide(VideoTimeline timeline, Clip clip, TimeSpan delta)
    {
        var (previous, next) = Neighbours(timeline, clip);
        return previous is null || next is null ? TimeSpan.Zero : ClampBoundary(previous, next, delta);
    }

    /// <summary>
    /// Mueve un clip en la timeline sin cambiar su contenido: el anterior se alarga o acorta
    /// y el siguiente hace lo contrario.
    /// </summary>
    /// <returns>El desplazamiento realmente aplicado.</returns>
    public static TimeSpan Slide(VideoTimeline timeline, Clip clip, TimeSpan delta)
    {
        var (previous, next) = Neighbours(timeline, clip);
        return previous is null || next is null ? TimeSpan.Zero : MoveBoundary(previous, next, delta);
    }

    // ------------------------------------------------------------- común

    /// <summary>
    /// Cuánto puede correrse el final de <paramref name="left"/> y el inicio de
    /// <paramref name="right"/> a la vez. Es la misma restricción para <c>roll</c> y <c>slide</c>.
    /// </summary>
    private static TimeSpan ClampBoundary(Clip left, Clip right, TimeSpan delta)
    {
        // Hacia atrás: el anterior no puede quedar por debajo de la duración mínima, ni el
        // siguiente pedir material anterior al inicio de su archivo.
        var lowest = Max(-(left.Duration - Clip.MinimumDuration), -right.SourceIn);

        // Hacia delante: el anterior no puede pedir más material del que tiene su archivo, ni
        // el siguiente quedar por debajo de la duración mínima.
        var highest = Min(right.Duration - Clip.MinimumDuration, left.Source.Duration - left.SourceOut);

        return Clamp(delta, lowest, highest);
    }

    private static TimeSpan MoveBoundary(Clip left, Clip right, TimeSpan delta)
    {
        var applied = ClampBoundary(left, right, delta);

        left.RestoreRange(left.SourceIn, left.SourceOut + applied);
        right.RestoreRange(right.SourceIn + applied, right.SourceOut);
        return applied;
    }

    private static Clip? NeighbourAfter(VideoTimeline timeline, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clip);

        var index = timeline.IndexOf(clip);
        return index >= 0 && index + 1 < timeline.Clips.Count ? timeline.Clips[index + 1] : null;
    }

    private static (Clip? Previous, Clip? Next) Neighbours(VideoTimeline timeline, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clip);

        var index = timeline.IndexOf(clip);
        if (index < 0)
        {
            return (null, null);
        }

        return (
            index > 0 ? timeline.Clips[index - 1] : null,
            index + 1 < timeline.Clips.Count ? timeline.Clips[index + 1] : null);
    }

    // Math.Clamp lanza si el mínimo supera al máximo; aquí ambos extremos son cotas que
    // pueden cruzarse en un clip degenerado, y en ese caso no cabe desplazamiento alguno.
    private static TimeSpan Clamp(TimeSpan value, TimeSpan lowest, TimeSpan highest) =>
        lowest > highest ? TimeSpan.Zero : value < lowest ? lowest : value > highest ? highest : value;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}

/// <summary>Desliza el contenido de un clip dentro de su archivo (slip).</summary>
public sealed class SlipClipCommand : IUndoableCommand
{
    private readonly Clip _clip;
    private readonly TimeSpan _delta;
    private TimeSpan _in, _out;
    private bool _applied;

    /// <summary>Crea la operación.</summary>
    public SlipClipCommand(Clip clip, TimeSpan delta)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clip = clip;
        _delta = delta;
    }

    /// <inheritdoc/>
    public string Description => "Deslizar contenido del clip";

    /// <summary>Desplazamiento realmente aplicado.</summary>
    public TimeSpan Applied { get; private set; }

    /// <inheritdoc/>
    public void Execute()
    {
        _in = _clip.SourceIn;
        _out = _clip.SourceOut;
        Applied = TimelineTools.Slip(_clip, _delta);
        _applied = Applied != TimeSpan.Zero;
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_applied)
        {
            _clip.RestoreRange(_in, _out);
        }
    }
}

/// <summary>Mueve el corte entre un clip y el siguiente (roll).</summary>
public sealed class RollEditCommand : IUndoableCommand
{
    private readonly VideoTimeline _timeline;
    private readonly Clip _left;
    private readonly TimeSpan _delta;
    private Clip? _right;
    private TimeSpan _leftOut, _rightIn;
    private bool _applied;

    /// <summary>Crea la operación.</summary>
    /// <param name="timeline">Pista.</param>
    /// <param name="left">Clip anterior al corte.</param>
    /// <param name="delta">Desplazamiento; positivo retrasa el corte.</param>
    public RollEditCommand(VideoTimeline timeline, Clip left, TimeSpan delta)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(left);
        _timeline = timeline;
        _left = left;
        _delta = delta;
    }

    /// <inheritdoc/>
    public string Description => "Mover corte";

    /// <summary>Desplazamiento realmente aplicado.</summary>
    public TimeSpan Applied { get; private set; }

    /// <inheritdoc/>
    public void Execute()
    {
        var index = _timeline.IndexOf(_left);
        _right = index >= 0 && index + 1 < _timeline.Clips.Count ? _timeline.Clips[index + 1] : null;

        if (_right is null)
        {
            return;
        }

        _leftOut = _left.SourceOut;
        _rightIn = _right.SourceIn;
        Applied = TimelineTools.Roll(_timeline, _left, _delta);
        _applied = Applied != TimeSpan.Zero;
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_applied && _right is not null)
        {
            _left.RestoreRange(_left.SourceIn, _leftOut);
            _right.RestoreRange(_rightIn, _right.SourceOut);
        }
    }
}

/// <summary>Desliza un clip entre sus vecinos sin cambiar su contenido (slide).</summary>
public sealed class SlideClipCommand : IUndoableCommand
{
    private readonly VideoTimeline _timeline;
    private readonly Clip _clip;
    private readonly TimeSpan _delta;
    private Clip? _previous, _next;
    private TimeSpan _previousOut, _nextIn;
    private bool _applied;

    /// <summary>Crea la operación.</summary>
    public SlideClipCommand(VideoTimeline timeline, Clip clip, TimeSpan delta)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clip);
        _timeline = timeline;
        _clip = clip;
        _delta = delta;
    }

    /// <inheritdoc/>
    public string Description => "Deslizar clip";

    /// <summary>Desplazamiento realmente aplicado.</summary>
    public TimeSpan Applied { get; private set; }

    /// <inheritdoc/>
    public void Execute()
    {
        var index = _timeline.IndexOf(_clip);
        if (index <= 0 || index + 1 >= _timeline.Clips.Count)
        {
            return;
        }

        _previous = _timeline.Clips[index - 1];
        _next = _timeline.Clips[index + 1];
        _previousOut = _previous.SourceOut;
        _nextIn = _next.SourceIn;

        Applied = TimelineTools.Slide(_timeline, _clip, _delta);
        _applied = Applied != TimeSpan.Zero;
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_applied && _previous is not null && _next is not null)
        {
            _previous.RestoreRange(_previous.SourceIn, _previousOut);
            _next.RestoreRange(_nextIn, _next.SourceOut);
        }
    }
}
