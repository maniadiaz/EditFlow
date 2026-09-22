// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Exporting;

/// <summary>
/// Convierte un efecto de estilo (<see cref="VisualEffectKind"/>) en los filtros de FFmpeg que lo
/// aplican.
/// </summary>
/// <remarks>
/// Mismo patrón que <see cref="VisualFilterCatalog"/>: un único sitio, usado por igual en el
/// preview y en la exportación. Los efectos de esta lista son deliberadamente los que no cambian
/// con el tiempo (un fragmento fijo, igual en cualquier fotograma del clip); un zoom progresivo,
/// un giro o un flash exigirían expresiones de FFmpeg dependientes de <c>t</c>, la misma clase de
/// riesgo que se dejó fuera de las animaciones de texto.
/// </remarks>
public static class VisualEffectCatalog
{
    /// <summary>Filtros que aplican el efecto elegido, o <see langword="null"/> si es «Sin efecto».</summary>
    public static string? Build(VisualEffectKind kind) => kind switch
    {
        // Ruido de cinta, algo de contraste y saturación de menos: el aspecto de una grabación VHS.
        VisualEffectKind.Vhs =>
            "eq=contrast=1.15:saturation=0.75:brightness=0.02,noise=alls=10:allf=t,gblur=sigma=0.4",

        // Desplaza el rojo y el azul en direcciones opuestas: el borde de color de una lente barata.
        VisualEffectKind.ChromaticAberration => "rgbashift=rh=-3:bh=3",

        VisualEffectKind.FilmGrain => "noise=alls=22:allf=t",

        VisualEffectKind.Blur => "gblur=sigma=6",

        // Vira hacia el magenta y el cian y realza el contraste: la estética vaporwave.
        VisualEffectKind.Vaporwave => "hue=h=280:s=1.3,eq=contrast=1.1:saturation=1.2",

        _ => null,
    };
}
