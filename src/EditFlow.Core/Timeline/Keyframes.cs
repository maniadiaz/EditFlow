// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Immutable;
using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>Qué se puede animar a lo largo de un clip.</summary>
/// <remarks>
/// No todas valen en todos los sitios: un clip de la pista principal anima su encuadre, un
/// elemento de una capa su posición y opacidad, y un clip de audio su volumen. Quien las usa
/// sabe cuáles le tocan; esta lista es el vocabulario común para que el modelo, el guardado y
/// los filtros hablen de lo mismo.
/// </remarks>
public enum AnimatedProperty
{
    /// <summary>Zoom del encuadre de un clip de video.</summary>
    Scale,

    /// <summary>Desplazamiento horizontal: del recorte en un clip, del centro en una capa.</summary>
    OffsetX,

    /// <summary>Desplazamiento vertical.</summary>
    OffsetY,

    /// <summary>Giro en grados.</summary>
    Rotation,

    /// <summary>Opacidad de un elemento de una capa, de 0 a 1.</summary>
    Opacity,

    /// <summary>Ancho de un elemento de una capa, como fracción del ancho del video.</summary>
    Width,

    /// <summary>Volumen de un clip de audio, en dB.</summary>
    Volume,
}

/// <summary>Un valor fijado en un instante concreto.</summary>
/// <param name="At">
/// Instante <b>dentro del propio clip</b>, contado desde su inicio. Medirlo así y no desde el
/// principio de la timeline es lo que permite mover un clip sin que su animación se desbarate.
/// </param>
/// <param name="Value">Valor de la propiedad en ese instante.</param>
public sealed record Keyframe(TimeSpan At, double Value);

/// <summary>
/// Los puntos que marcan cómo cambia una propiedad a lo largo de un clip.
/// </summary>
/// <remarks>
/// <para>
/// Entre dos puntos el valor avanza en línea recta. Antes del primero y después del último se
/// mantiene el valor de ese extremo, que es lo que espera cualquiera que ponga un punto a mitad
/// de un clip: lo de antes no debería empezar a moverse solo.
/// </para>
/// <para>
/// Es inmutable, como el resto de ajustes de un clip: añadir o quitar un punto devuelve otra
/// lista. Así una operación de deshacer solo tiene que guardar la anterior.
/// </para>
/// </remarks>
public sealed class KeyframeTrack
{
    /// <summary>Dos puntos más cerca que esto se consideran el mismo instante.</summary>
    /// <remarks>
    /// Un fotograma a 60 por segundo dura 16,7 ms. Con una tolerancia menor, mover el cabezal
    /// un fotograma y volver a pulsar «añadir punto» dejaría dos puntos donde el usuario quería
    /// corregir uno.
    /// </remarks>
    public static TimeSpan SameInstant { get; } = TimeSpan.FromMilliseconds(8);

    private readonly ImmutableArray<Keyframe> _points;

    private KeyframeTrack(ImmutableArray<Keyframe> points) => _points = points;

    /// <summary>Sin animación: la propiedad vale lo que diga el ajuste fijo del clip.</summary>
    public static KeyframeTrack Empty { get; } = new([]);

    /// <summary>Puntos, ordenados por instante.</summary>
    public IReadOnlyList<Keyframe> Points => _points;

    /// <summary>Indica si no hay ningún punto.</summary>
    public bool IsEmpty => _points.Length == 0;

    /// <summary>Indica si el valor cambia de verdad a lo largo del clip.</summary>
    /// <remarks>
    /// Un único punto no es una animación: fija un valor, pero el mismo de principio a fin. Sirve
    /// para no emitir una expresión de FFmpeg donde basta un número.
    /// </remarks>
    public bool IsAnimated => _points.Length >= 2;

    /// <summary>Valor en un instante del clip.</summary>
    /// <param name="at">Instante, contado desde el inicio del clip.</param>
    /// <param name="fallback">Qué devolver si no hay ningún punto.</param>
    public double ValueAt(TimeSpan at, double fallback)
    {
        if (_points.Length == 0)
        {
            return fallback;
        }

        if (at <= _points[0].At)
        {
            return _points[0].Value;
        }

        var last = _points[^1];
        if (at >= last.At)
        {
            return last.Value;
        }

        for (var i = 1; i < _points.Length; i++)
        {
            var next = _points[i];
            if (at > next.At)
            {
                continue;
            }

            var previous = _points[i - 1];
            var span = (next.At - previous.At).TotalSeconds;

            // Dos puntos en el mismo instante serían un salto: se toma el segundo, que es el que
            // manda de ahí en adelante.
            if (span <= 0)
            {
                return next.Value;
            }

            var progress = (at - previous.At).TotalSeconds / span;
            return previous.Value + ((next.Value - previous.Value) * progress);
        }

        return last.Value;
    }

    /// <summary>Añade un punto, o cambia el que ya hubiera en ese instante.</summary>
    public KeyframeTrack With(TimeSpan at, double value)
    {
        if (at < TimeSpan.Zero)
        {
            at = TimeSpan.Zero;
        }

        var kept = _points.Where(p => Distance(p.At, at) > SameInstant);
        var points = kept.Append(new Keyframe(at, value)).OrderBy(p => p.At).ToImmutableArray();
        return new KeyframeTrack(points);
    }

    /// <summary>Quita el punto que haya en un instante; si no hay ninguno, devuelve lo mismo.</summary>
    public KeyframeTrack Without(TimeSpan at)
    {
        var points = _points.Where(p => Distance(p.At, at) > SameInstant).ToImmutableArray();
        return points.Length == _points.Length ? this : new KeyframeTrack(points);
    }

    /// <summary>Indica si hay un punto en un instante.</summary>
    public bool HasPointAt(TimeSpan at) => _points.Any(p => Distance(p.At, at) <= SameInstant);

    /// <summary>
    /// Los puntos que caen en un tramo del clip, recolocados para empezar a contar desde él.
    /// </summary>
    /// <remarks>
    /// Se usa al dividir un clip y al trocear la secuencia para la copia de preview. Los puntos
    /// que quedan fuera del tramo no se tiran sin más: se conserva el valor que la animación
    /// tenía justo en cada borde, añadiendo un punto ahí. Si se descartaran, el trozo empezaría
    /// con otro valor y se vería un salto donde antes había continuidad.
    /// </remarks>
    public KeyframeTrack Section(TimeSpan from, TimeSpan to)
    {
        if (_points.Length == 0 || to <= from)
        {
            return Empty;
        }

        var inside = _points
            .Where(p => p.At > from && p.At < to)
            .Select(p => new Keyframe(p.At - from, p.Value));

        var edges = new[]
        {
            new Keyframe(TimeSpan.Zero, ValueAt(from, 0)),
            new Keyframe(to - from, ValueAt(to, 0)),
        };

        var points = inside.Concat(edges).OrderBy(p => p.At).ToImmutableArray();

        // Un tramo entero con el mismo valor no necesita animación: se deja un solo punto para
        // que 'IsAnimated' diga la verdad y no se emita una expresión que no hace nada.
        return points.All(p => Math.Abs(p.Value - points[0].Value) < 0.0001)
            ? new KeyframeTrack([points[0]])
            : new KeyframeTrack(points);
    }

    private static TimeSpan Distance(TimeSpan a, TimeSpan b) => a > b ? a - b : b - a;
}

/// <summary>
/// Todas las animaciones de un clip: como mucho una lista de puntos por propiedad.
/// </summary>
/// <remarks>
/// Inmutable y vacía por defecto, de modo que un clip que nadie ha animado no cuesta nada ni
/// cambia en absoluto lo que se exporta.
/// </remarks>
public sealed class Animation
{
    private readonly ImmutableDictionary<AnimatedProperty, KeyframeTrack> _tracks;

    private Animation(ImmutableDictionary<AnimatedProperty, KeyframeTrack> tracks) => _tracks = tracks;

    /// <summary>Sin animación.</summary>
    public static Animation None { get; } =
        new(ImmutableDictionary<AnimatedProperty, KeyframeTrack>.Empty);

    /// <summary>Indica si no hay ninguna propiedad animada.</summary>
    public bool IsNone => _tracks.Count == 0;

    /// <summary>Propiedades con algún punto, en orden de declaración.</summary>
    public IEnumerable<AnimatedProperty> Animated =>
        Enum.GetValues<AnimatedProperty>().Where(p => !Track(p).IsEmpty);

    /// <summary>Puntos de una propiedad; vacío si no se ha animado.</summary>
    public KeyframeTrack Track(AnimatedProperty property) =>
        _tracks.TryGetValue(property, out var track) ? track : KeyframeTrack.Empty;

    /// <summary>Copia con otra lista de puntos para una propiedad.</summary>
    public Animation With(AnimatedProperty property, KeyframeTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        return new Animation(track.IsEmpty
            ? _tracks.Remove(property)
            : _tracks.SetItem(property, track));
    }

    /// <summary>Copia sin ninguna animación de una propiedad.</summary>
    public Animation Without(AnimatedProperty property) => With(property, KeyframeTrack.Empty);

    /// <summary>
    /// Copia con solo el tramo indicado de cada propiedad, recolocado para empezar en cero.
    /// </summary>
    public Animation Section(TimeSpan from, TimeSpan to)
    {
        var result = None;
        foreach (var property in Animated)
        {
            result = result.With(property, Track(property).Section(from, to));
        }

        return result;
    }
}

/// <summary>Lo que se puede animar: un clip de la pista principal, una capa o un clip de audio.</summary>
/// <remarks>
/// Existe para que las operaciones de añadir y quitar puntos se escriban una sola vez en vez de
/// tres casi iguales. Cada implementación ya sabe cuánto dura y qué propiedades admite.
/// </remarks>
public interface IAnimatable
{
    /// <summary>Animaciones del elemento.</summary>
    Animation Animation { get; set; }

    /// <summary>Cuánto ocupa en la timeline, que es el rango donde tienen sentido sus puntos.</summary>
    TimeSpan Duration { get; }

    /// <summary>Valor fijo de una propiedad: el que se usa cuando no está animada.</summary>
    double StaticValue(AnimatedProperty property);

    /// <summary>Indica si la propiedad se puede animar en este elemento.</summary>
    bool Supports(AnimatedProperty property);
}

/// <summary>Añade o mueve un punto de animación.</summary>
public sealed class SetKeyframeCommand : IUndoableCommand
{
    private readonly IAnimatable _target;
    private readonly AnimatedProperty _property;
    private readonly TimeSpan _at;
    private readonly double _value;
    private readonly Animation _previous;

    /// <summary>Crea la operación.</summary>
    /// <param name="target">Clip, capa o audio que se anima.</param>
    /// <param name="property">Propiedad.</param>
    /// <param name="at">Instante dentro del clip.</param>
    /// <param name="value">Valor en ese instante.</param>
    /// <exception cref="ArgumentException">Si el elemento no admite animar esa propiedad.</exception>
    public SetKeyframeCommand(IAnimatable target, AnimatedProperty property, TimeSpan at, double value)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!target.Supports(property))
        {
            throw new ArgumentException($"Aquí no se puede animar {property}.", nameof(property));
        }

        _target = target;
        _property = property;
        _at = at < TimeSpan.Zero ? TimeSpan.Zero : at;
        _value = value;
        _previous = target.Animation;
    }

    /// <inheritdoc/>
    public string Description => "Añadir un punto de animación";

    /// <inheritdoc/>
    public void Execute()
    {
        // El primer punto de una propiedad arrastra consigo el valor fijo que el clip tenía: sin
        // él, animar desde la mitad del clip haría que la primera mitad saltara de golpe al valor
        // nuevo en vez de mantenerse como estaba.
        var track = _previous.Track(_property);
        if (track.IsEmpty && _at > TimeSpan.Zero)
        {
            track = track.With(TimeSpan.Zero, _target.StaticValue(_property));
        }

        _target.Animation = _previous.With(_property, track.With(_at, _value));
    }

    /// <inheritdoc/>
    public void Undo() => _target.Animation = _previous;
}

/// <summary>Quita un punto de animación, o todos los de una propiedad.</summary>
public sealed class RemoveKeyframeCommand : IUndoableCommand
{
    private readonly IAnimatable _target;
    private readonly AnimatedProperty _property;
    private readonly TimeSpan? _at;
    private readonly Animation _previous;

    /// <summary>Crea la operación.</summary>
    /// <param name="target">Clip, capa o audio.</param>
    /// <param name="property">Propiedad.</param>
    /// <param name="at">Instante del punto, o <see langword="null"/> para quitar toda la animación.</param>
    public RemoveKeyframeCommand(IAnimatable target, AnimatedProperty property, TimeSpan? at)
    {
        ArgumentNullException.ThrowIfNull(target);

        _target = target;
        _property = property;
        _at = at;
        _previous = target.Animation;
    }

    /// <inheritdoc/>
    public string Description => _at is null ? "Quitar la animación" : "Quitar un punto de animación";

    /// <inheritdoc/>
    public void Execute()
    {
        var track = _at is null
            ? KeyframeTrack.Empty
            : _previous.Track(_property).Without(_at.Value);

        // Un único punto que sobrevive ya no anima nada: se deja la propiedad quieta en su valor
        // fijo en vez de conservar un resto invisible que el panel tendría que explicar.
        _target.Animation = _previous.With(_property, track.Points.Count == 1 ? KeyframeTrack.Empty : track);
    }

    /// <inheritdoc/>
    public void Undo() => _target.Animation = _previous;
}
