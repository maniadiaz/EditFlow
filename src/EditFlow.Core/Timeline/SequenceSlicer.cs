// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EditFlow.Core.Timeline;

/// <summary>Corta un intervalo de una secuencia para tratarlo como una secuencia propia.</summary>
/// <remarks>
/// Sirve para renderizar por trozos el preview: cada trozo se convierte en un montaje pequeño
/// e independiente que se procesa con el mismo grafo que la exportación, y del que se puede
/// calcular una huella para saber si cambió. El resultado es una copia; la secuencia original
/// no se modifica.
/// </remarks>
public static class SequenceSlicer
{
    /// <summary>Trozos de video más cortos que esto se descartan: no llegan a ser un fotograma.</summary>
    private static readonly TimeSpan Sliver = TimeSpan.FromMilliseconds(1);

    /// <summary>Devuelve el intervalo <c>[start, end)</c> de la secuencia, con los tiempos rebasados a cero.</summary>
    /// <param name="source">Secuencia de la que se corta.</param>
    /// <param name="start">Inicio del intervalo.</param>
    /// <param name="end">Fin del intervalo.</param>
    /// <returns>
    /// Una secuencia con solo la imagen: los clips de video recortados al intervalo (sin su
    /// audio) y las capas de superposición visibles. No lleva pistas de audio.
    /// </returns>
    public static EditSequence Slice(EditSequence source, TimeSpan start, TimeSpan end)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);

        var slice = new EditSequence();

        var clipStart = TimeSpan.Zero;
        foreach (var clip in source.Video.Clips)
        {
            var clipEnd = clipStart + clip.Duration;

            var from = clipStart > start ? clipStart : start;
            var to = clipEnd < end ? clipEnd : end;

            if (to - from >= Sliver)
            {
                var sourceIn = clip.SourceIn + (from - clipStart);
                var sourceOut = sourceIn + (to - from);

                // Un redondeo no puede sacar el recorte del archivo.
                if (sourceOut > clip.Source.Duration)
                {
                    sourceOut = clip.Source.Duration;
                }

                if (sourceOut > sourceIn)
                {
                    // El audio no se usa para la imagen: se silencia para que el grafo no lo decodifique.
                    slice.Video.Append(new Clip(clip.Source, sourceIn, sourceOut) { IsAudioMuted = true, Color = clip.Color });
                }
            }

            clipStart = clipEnd;
        }

        // Se conserva el orden de las capas (la primera queda delante) y se dejan fuera las ocultas.
        // Las capas se recorren de atrás adelante porque AddOverlayTrack inserta al principio.
        for (var t = source.OverlayTracks.Count - 1; t >= 0; t--)
        {
            var track = source.OverlayTracks[t];
            if (track.IsHidden)
            {
                continue;
            }

            // Una capa sin nada dentro del intervalo no se copia: que exista o no en otro punto
            // de la timeline no cambia lo que se ve aquí, y no debe cambiar su huella.
            OverlayTrack? copy = null;
            foreach (var item in track.Items)
            {
                if (item.Slice(start, end) is { } piece)
                {
                    copy ??= slice.AddOverlayTrack(track.Name);
                    copy.AddSlice(piece);
                }
            }
        }

        return slice;
    }
}
