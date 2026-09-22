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

        // Se usa la posición ya compuesta (Layout), no la suma plana de duraciones: con
        // transiciones, un clip puede empezar antes de que termine el anterior, y cortar por
        // el tiempo plano recortaría el trozo equivocado.
        foreach (var entry in source.Video.Layout())
        {
            var clip = entry.Clip;
            var clipStart = entry.Start;
            var clipEnd = entry.End;

            var from = clipStart > start ? clipStart : start;
            var to = clipEnd < end ? clipEnd : end;

            if (to - from >= Sliver)
            {
                // El intervalo se pide en tiempo de timeline; a una velocidad distinta de 1, hay
                // que convertirlo a cuánto material de archivo ocupa eso.
                var sourceIn = clip.SourceIn + clip.SourceTimeAt(from - clipStart);
                var sourceOut = sourceIn + clip.SourceTimeAt(to - from);

                // Un redondeo no puede sacar el recorte del archivo.
                if (sourceOut > clip.Source.Duration)
                {
                    sourceOut = clip.Source.Duration;
                }

                if (sourceOut > sourceIn)
                {
                    // Si el trozo no arranca justo donde el clip empieza en la timeline
                    // compuesta, se perdió su principio —y con él, si lo tenía, el tramo que
                    // se funde con el anterior—. Conservar la transición aquí la fundiría con
                    // lo que sea que quede justo delante en este trozo, que ya no es el clip
                    // correcto: se prefiere un corte seco a un fundido mal hecho. El mismo
                    // razonamiento vale para el fundido a negro de entrada; el de salida es el
                    // espejo, mirando si el trozo llega hasta el final de verdad del clip.
                    var keepsStart = from <= clipStart;
                    var keepsEnd = to >= clipEnd;

                    // El audio no se usa para la imagen: se silencia para que el grafo no lo decodifique.
                    slice.Video.Append(new Clip(clip.Source, sourceIn, sourceOut)
                    {
                        IsAudioMuted = true,
                        Color = clip.Color,
                        TransitionIn = keepsStart ? clip.TransitionIn : Transition.None,
                        Speed = clip.Speed,
                        Transform = clip.Transform,
                        Filter = clip.Filter,
                        FadeIn = keepsStart ? clip.FadeIn : TimeSpan.Zero,
                        FadeOut = keepsEnd ? clip.FadeOut : TimeSpan.Zero,
                    });
                }
            }
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
