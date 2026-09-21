// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EditFlow.Engine.Waveforms;

/// <summary>Consultas sobre una lista de picos.</summary>
public static class WaveformPeaks
{
    /// <summary>Amplitud máxima, de 0 a 255, en un intervalo de tiempo del audio.</summary>
    /// <param name="peaks">Picos, a <see cref="WaveformExtractor.PeaksPerSecond"/> por segundo.</param>
    /// <param name="from">Inicio del intervalo dentro del audio.</param>
    /// <param name="to">Fin del intervalo.</param>
    /// <remarks>
    /// Se devuelve el <b>máximo</b> del intervalo, no la media: al alejar el zoom, cada píxel
    /// cubre muchos picos, y promediarlos borraría los golpes, que son justo lo que se busca
    /// ver en una forma de onda para cortar en el sitio adecuado.
    /// </remarks>
    public static int MaxIn(ReadOnlySpan<byte> peaks, TimeSpan from, TimeSpan to)
    {
        if (peaks.IsEmpty)
        {
            return 0;
        }

        var first = (int)Math.Floor(from.TotalSeconds * WaveformExtractor.PeaksPerSecond);
        var last = (int)Math.Ceiling(to.TotalSeconds * WaveformExtractor.PeaksPerSecond);

        // Siempre se mira al menos un pico, aunque el intervalo sea más estrecho que uno.
        first = Math.Clamp(first, 0, peaks.Length - 1);
        last = Math.Clamp(Math.Max(last, first + 1), first + 1, peaks.Length);

        var max = 0;
        for (var i = first; i < last; i++)
        {
            if (peaks[i] > max)
            {
                max = peaks[i];
            }
        }

        return max;
    }
}
