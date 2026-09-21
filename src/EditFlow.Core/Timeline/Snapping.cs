// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EditFlow.Core.Timeline;

/// <summary>
/// Imán: alinea un clip que se arrastra con los bordes cercanos.
/// </summary>
/// <remarks>
/// Sin imán, alinear una música exactamente con un corte del video exige acertar el
/// píxel. Vive en <c>Core</c> y no en el control que dibuja la timeline para poder
/// probarlo: la lógica de arrastre del control solo se puede ejercitar a mano.
/// </remarks>
public static class Snapping
{
    /// <summary>
    /// Devuelve el inicio ajustado al punto más cercano, o el pedido si no hay ninguno a
    /// menos de <paramref name="threshold"/>.
    /// </summary>
    /// <param name="requestedStart">Inicio que el usuario está pidiendo con el arrastre.</param>
    /// <param name="duration">Duración del clip que se arrastra.</param>
    /// <param name="points">Instantes candidatos a los que imantarse.</param>
    /// <param name="threshold">Distancia máxima a la que se imanta.</param>
    /// <remarks>
    /// Se comprueban los dos bordes del clip, no solo el de inicio: lo habitual es querer
    /// que <i>termine</i> donde termina el video tanto como que empiece donde empieza.
    /// </remarks>
    public static TimeSpan Snap(
        TimeSpan requestedStart,
        TimeSpan duration,
        IEnumerable<TimeSpan> points,
        TimeSpan threshold)
    {
        ArgumentNullException.ThrowIfNull(points);

        var best = requestedStart;
        var bestDistance = threshold;
        var requestedEnd = requestedStart + duration;

        foreach (var point in points)
        {
            var fromStart = (requestedStart - point).Duration();
            if (fromStart <= bestDistance)
            {
                bestDistance = fromStart;
                best = point;
            }

            var fromEnd = (requestedEnd - point).Duration();
            if (fromEnd <= bestDistance)
            {
                bestDistance = fromEnd;
                best = point - duration;
            }
        }

        // Imantar el final a un punto cercano al origen podría dejar el inicio negativo.
        return best < TimeSpan.Zero ? TimeSpan.Zero : best;
    }

    /// <summary>
    /// Reúne los instantes a los que puede imantarse un clip de audio.
    /// </summary>
    /// <param name="sequence">Secuencia en la que se arrastra.</param>
    /// <param name="playhead">Posición del cabezal.</param>
    /// <param name="moving">Clip que se arrastra; sus propios bordes no cuentan.</param>
    /// <remarks>
    /// El propio clip se excluye: imantarse a su posición anterior haría que arrastrarlo
    /// unos píxeles no lo moviera, porque siempre volvería a donde estaba.
    /// </remarks>
    public static IReadOnlyList<TimeSpan> PointsFor(EditSequence sequence, TimeSpan playhead, AudioClip? moving) =>
        Collect(sequence, playhead, moving, null);

    /// <summary>Igual que la sobrecarga para audio, pero excluyendo un elemento superpuesto que se arrastra.</summary>
    public static IReadOnlyList<TimeSpan> PointsForOverlay(EditSequence sequence, TimeSpan playhead, OverlayItem? moving) =>
        Collect(sequence, playhead, null, moving);

    private static List<TimeSpan> Collect(
        EditSequence sequence, TimeSpan playhead, AudioClip? movingAudio, OverlayItem? movingOverlay)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        var points = new List<TimeSpan> { TimeSpan.Zero, playhead };

        var cursor = TimeSpan.Zero;
        foreach (var clip in sequence.Video.Clips)
        {
            points.Add(cursor);
            cursor += clip.Duration;
        }

        points.Add(cursor);

        foreach (var track in sequence.AudioTracks)
        {
            foreach (var other in track.Clips)
            {
                if (ReferenceEquals(other, movingAudio))
                {
                    continue;
                }

                points.Add(other.TimelineStart);
                points.Add(other.TimelineEnd);
            }
        }

        foreach (var layer in sequence.OverlayTracks)
        {
            foreach (var other in layer.Items)
            {
                if (ReferenceEquals(other, movingOverlay))
                {
                    continue;
                }

                points.Add(other.Start);
                points.Add(other.End);
            }
        }

        return points;
    }
}
