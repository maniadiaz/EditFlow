// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Exporting;

/// <summary>
/// Convierte un efecto de sonido (<see cref="AudioEffectKind"/>) y un balance estéreo en los
/// filtros de FFmpeg que los aplican.
/// </summary>
/// <remarks>
/// Mismo patrón que <see cref="VisualEffectCatalog"/>: un único sitio, usado por igual en el
/// preview y en la exportación —aquí los dos comparten literalmente el mismo grafo
/// (<see cref="FilterGraphBuilder.BuildAudioOnly"/>), así que no hace falta ni la duplicación
/// que sí existe en video entre el filtro de decodificación y el de exportación.
/// </remarks>
public static class AudioEffectCatalog
{
    /// <summary>Filtros que aplican el efecto elegido, o <see langword="null"/> si es «Sin efecto».</summary>
    public static string? Build(AudioEffectKind kind) => kind switch
    {
        // Recorta los graves que solo ensucian el diálogo y realza la zona de presencia de la voz.
        AudioEffectKind.Voice => "highpass=f=90,equalizer=f=3000:width_type=o:width=2:g=4",

        // Reductor de ruido por FFT: no necesita un modelo externo, a diferencia de 'arnndn'.
        AudioEffectKind.Denoise => "afftdn=nr=12:nf=-25",

        AudioEffectKind.Compressor => "acompressor=threshold=0.1:ratio=4:attack=5:release=50",

        AudioEffectKind.Limiter => "alimiter=limit=0.9",

        // Varios ecos cortos y suaves superpuestos simulan la reverberación de una sala.
        AudioEffectKind.Reverb => "aecho=0.8:0.9:40|60:0.3|0.25",

        AudioEffectKind.Chorus => "chorus=0.7:0.9:55:0.4:0.25:2",

        // Una sola pasada (sin el segundo análisis de 'loudnorm' en dos fases): suficiente
        // para un ajuste de un clic, no para masterizar con precisión de laboratorio.
        AudioEffectKind.Normalize => "loudnorm=I=-16:TP=-1.5:LRA=11",

        _ => null,
    };

    /// <summary>Filtro de balance estéreo, o <see langword="null"/> si está centrado.</summary>
    public static string? BuildPan(double pan)
    {
        if (Math.Abs(pan) < 0.001)
        {
            return null;
        }

        return "stereotools=balance_in=" + pan.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
