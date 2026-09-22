// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Exporting;

/// <summary>
/// Convierte un ajuste de color en los filtros de FFmpeg que lo aplican.
/// </summary>
/// <remarks>
/// <para>
/// Es el único sitio donde los cuatro controles se traducen a filtros, y lo usan por igual el preview,
/// las copias de preview y la exportación: lo que se ve al ajustar es lo que sale exportado.
/// </para>
/// <para>
/// Los filtros trabajan sobre video YUV (el formato habitual) sin convertirlo a RGB, así que son baratos:
/// <c>lutyuv</c> aplica la exposición (multiplicando la luminancia) y la temperatura (desplazando los dos
/// canales de color en sentido contrario), y <c>eq</c> el contraste y la saturación. La entrada debe ser
/// <c>yuv420p</c>; quien lo use añade antes el <c>format</c> necesario.
/// </para>
/// </remarks>
public static class ColorFilter
{
    // A +100 la exposición sube o baja 1,5 pasos de diafragma (una ganancia de 2,8x o de 0,35x): más se
    // quema o se pierde la imagen sin aportar nada útil.
    private const double MaxStops = 1.5;

    // A +100 la temperatura desplaza los canales de color unos 14 niveles de 224: se nota mucho sin virar la imagen.
    private const double MaxShift = 14;

    /// <summary>Filtros que aplican el ajuste, o <see langword="null"/> si no cambia nada.</summary>
    public static string? Build(ColorAdjust? adjust)
    {
        if (adjust is null || adjust.IsNone)
        {
            return null;
        }

        var color = adjust.Clamped();
        var filters = new List<string>();

        var exposure = Math.Abs(color.Exposure) >= 0.05;
        var temperature = Math.Abs(color.Temperature) >= 0.05;

        if (exposure || temperature)
        {
            var parts = new List<string>();

            if (exposure)
            {
                var gain = Math.Pow(2, color.Exposure / ColorAdjust.Limit * MaxStops);
                parts.Add($"y='clip(val*{Number(gain)},minval,maxval)'");
            }

            if (temperature)
            {
                // Más cálido = menos azul (U baja) y más rojo (V sube). El desplazamiento se escala con el
                // rango del formato para que valga igual en 8 y en 10 bits.
                var shift = color.Temperature / ColorAdjust.Limit * MaxShift;
                var scaled = $"(maxval-minval)/224*{Number(Math.Abs(shift))}";
                var sign = shift >= 0;

                parts.Add($"u='clip(val{(sign ? "-" : "+")}{scaled},minval,maxval)'");
                parts.Add($"v='clip(val{(sign ? "+" : "-")}{scaled},minval,maxval)'");
            }

            filters.Add("lutyuv=" + string.Join(':', parts));
        }

        var eq = new List<string>();
        if (Math.Abs(color.Contrast) >= 0.05)
        {
            eq.Add("contrast=" + Number(1 + (color.Contrast / ColorAdjust.Limit)));
        }

        if (Math.Abs(color.Saturation) >= 0.05)
        {
            eq.Add("saturation=" + Number(1 + (color.Saturation / ColorAdjust.Limit)));
        }

        if (eq.Count > 0)
        {
            filters.Add("eq=" + string.Join(':', eq));
        }

        return string.Join(',', filters);
    }

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
