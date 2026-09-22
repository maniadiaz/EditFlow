// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Exporting;

/// <summary>
/// Convierte un filtro de aspecto (<see cref="VisualFilterKind"/>) en los filtros de FFmpeg que
/// lo aplican.
/// </summary>
/// <remarks>
/// Igual que <see cref="ColorFilter"/>: un único sitio, usado por igual en el preview y en la
/// exportación. Los filtros trabajan sobre YUV, así que la entrada debe ser <c>yuv420p</c>;
/// quien lo use añade antes el <c>format</c> necesario (lo comparte con <see cref="ColorFilter"/>).
/// </remarks>
public static class VisualFilterCatalog
{
    /// <summary>Filtros que aplican el aspecto elegido, o <see langword="null"/> si es «Sin filtro».</summary>
    public static string? Build(VisualFilterKind kind) => kind switch
    {
        VisualFilterKind.BlackAndWhite => "hue=s=0",

        // Colorchannelmixer con los coeficientes clásicos de conversión a sepia.
        VisualFilterKind.Sepia =>
            "colorchannelmixer=.393:.769:.189:0:.349:.686:.168:0:.272:.534:.131:0",

        // Menos saturación, un ligero viraje cálido y las esquinas oscurecidas: el aspecto de
        // una grabación antigua.
        VisualFilterKind.Vintage =>
            "eq=saturation=0.75:contrast=1.05,colorbalance=rs=.10:bs=-.10,vignette=PI/5",

        VisualFilterKind.Vignette => "vignette=PI/4",

        // Más rojo, menos azul.
        VisualFilterKind.Warm => "colorbalance=rs=.15:bs=-.15",

        // Al revés que Warm.
        VisualFilterKind.Cool => "colorbalance=rs=-.15:bs=.15",

        _ => null,
    };
}
