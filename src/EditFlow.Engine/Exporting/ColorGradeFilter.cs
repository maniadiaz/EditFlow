// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Exporting;

/// <summary>
/// Traduce una corrección de color avanzada a los filtros de FFmpeg que la aplican.
/// </summary>
/// <remarks>
/// <para>
/// El orden no es casual y es el mismo que sigue cualquier sala de etalonaje: primero el LUT,
/// que suele traer el aspecto base de la cámara; luego las ruedas, que reparten el color por
/// tramos de luminosidad; después las curvas, que dan la forma fina; y al final el ajuste
/// selectivo, que retoca una familia concreta sobre el resultado. Cambiar el orden cambia la
/// imagen, así que vive aquí y en un solo sitio.
/// </para>
/// <para>
/// Como el resto de catálogos, es el mismo fragmento para el preview y para la exportación.
/// </para>
/// </remarks>
public static class ColorGradeFilter
{
    /// <summary>Fragmento de filtros, o <see langword="null"/> si no hay nada que corregir.</summary>
    public static string? Build(ColorGrade? grade)
    {
        if (grade is null || grade.IsNone)
        {
            return null;
        }

        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(grade.LutPath))
        {
            // La ruta se entrecomilla y se escapa: una letra de unidad lleva dos puntos, que es
            // justo lo que separa las opciones de un filtro.
            parts.Add("lut3d=file=" + FilterPath.Quote(grade.LutPath));
        }

        if (grade.HasWheels)
        {
            parts.Add(BuildWheels(grade));
        }

        if (grade.HasCurves)
        {
            parts.Add(BuildCurves(grade));
        }

        if (!grade.SelectiveAdjust.IsNone)
        {
            parts.Add(BuildSelective(grade.SelectiveAdjust));
        }

        return parts.Count == 0 ? null : string.Join(',', parts);
    }

    /// <summary>Cuánto puede levantar el negro de un canal una rueda de sombras al tope.</summary>
    private const double LiftRange = 0.25;

    /// <summary>Cuánto puede bajar el blanco de un canal una rueda de luces al tope.</summary>
    private const double GainRange = 0.35;

    /// <summary>Cuánto puede apartar de 1 la gamma de un canal una rueda de medios al tope.</summary>
    private const double GammaRange = 0.6;

    /// <summary>
    /// Traduce las tres ruedas al modelo <i>lift / gamma / gain</i>, que es lo que una rueda de
    /// color es en cualquier sala de etalonaje.
    /// </summary>
    /// <remarks>
    /// <para>
    /// El camino evidente era <c>colorbalance</c>, que ya trae tres rangos llamados sombras,
    /// medios y luces. Medido contra el FFmpeg empaquetado, sus rangos no caen donde el nombre
    /// promete: sobre un gris medio (0x80) la rueda de «medios» no hace absolutamente nada y quien
    /// actúa es la de «luces». Un panel con tres ruedas donde la del medio no toca el tono más
    /// común de cualquier plano no sirve de nada.
    /// </para>
    /// <para>
    /// Lift, gamma y gain sí se reparten como se espera, y está comprobado midiendo píxeles: subir
    /// el punto de negro tiñe sobre todo las sombras y se desvanece en las luces; la gamma actúa
    /// en los medios; y bajar el punto de blanco tiñe las luces y no toca los negros.
    /// </para>
    /// <para>
    /// Los tres valores de cada rueda se normalizan antes de escribirlos porque el filtro solo
    /// admite de 0 a 1: empujar un canal hacia abajo se expresa subiendo los otros dos, que da
    /// exactamente el mismo viraje. Como efecto secundario, empujar las tres a la vez no hace
    /// nada, que es lo correcto en una rueda de <i>color</i>: aclarar u oscurecer es trabajo de
    /// la exposición, no de aquí.
    /// </para>
    /// </remarks>
    private static string BuildWheels(ColorGrade grade)
    {
        var parts = new List<string>();
        var levels = new List<string>();

        var shadows = grade.ShadowWheel.Clamped();
        if (!shadows.IsNeutral)
        {
            // Se resta el mínimo para que ninguno quede por debajo de 0, que es el suelo del filtro.
            var floor = Math.Min(shadows.Red, Math.Min(shadows.Green, shadows.Blue));
            levels.Add($"romin={Number((shadows.Red - floor) * LiftRange)}");
            levels.Add($"gomin={Number((shadows.Green - floor) * LiftRange)}");
            levels.Add($"bomin={Number((shadows.Blue - floor) * LiftRange)}");
        }

        var highlights = grade.HighlightWheel.Clamped();
        if (!highlights.IsNeutral)
        {
            // Y aquí se resta el máximo, para que ninguno pase de 1, que es el techo del filtro.
            var ceiling = Math.Max(highlights.Red, Math.Max(highlights.Green, highlights.Blue));
            levels.Add($"romax={Number(1 - ((ceiling - highlights.Red) * GainRange))}");
            levels.Add($"gomax={Number(1 - ((ceiling - highlights.Green) * GainRange))}");
            levels.Add($"bomax={Number(1 - ((ceiling - highlights.Blue) * GainRange))}");
        }

        if (levels.Count > 0)
        {
            parts.Add("colorlevels=" + string.Join(':', levels));
        }

        var midtones = grade.MidtoneWheel.Clamped();
        if (!midtones.IsNeutral)
        {
            // La gamma no tiene tope, así que basta con centrarla en la media para que empujar las
            // tres por igual siga sin teñir.
            var middle = (midtones.Red + midtones.Green + midtones.Blue) / 3;
            parts.Add(
                $"eq=gamma_r={Number(Gamma(midtones.Red - middle))}"
                + $":gamma_g={Number(Gamma(midtones.Green - middle))}"
                + $":gamma_b={Number(Gamma(midtones.Blue - middle))}");
        }

        return string.Join(',', parts);

        static double Gamma(double value) => Math.Clamp(1 + (value * GammaRange), 0.2, 3);
    }

    private static string BuildCurves(ColorGrade grade)
    {
        var options = new List<string>();

        // Una curva sin forma no se escribe: 'curves' con un canal vacío lo dejaría plano.
        Add("all", grade.MasterCurve);
        Add("r", grade.RedCurve);
        Add("g", grade.GreenCurve);
        Add("b", grade.BlueCurve);

        return "curves=" + string.Join(':', options);

        void Add(string channel, ToneCurve curve)
        {
            if (!curve.IsIdentity)
            {
                // Los puntos llevan barras y espacios: entrecomillados, el analizador no los
                // confunde con separadores de opciones.
                options.Add($"{channel}='{curve.ToFilterValue()}'");
            }
        }
    }

    private static string BuildSelective(SelectiveColor selective)
    {
        var value = selective.Clamped();
        var range = value.Family switch
        {
            ColorFamily.Reds => "reds",
            ColorFamily.Yellows => "yellows",
            ColorFamily.Greens => "greens",
            ColorFamily.Cyans => "cyans",
            ColorFamily.Blues => "blues",
            _ => "magentas",
        };

        // El filtro se expresa en tinta: subir el cian quita rojo. El modelo se expresa al revés,
        // como un deslizador que va del cian al rojo, que es como lo entiende quien lo mueve; de
        // ahí el cambio de signo. El cuarto valor es el negro, que oscurece.
        var values = string.Join(
            ' ',
            Number(-value.CyanRed),
            Number(-value.MagentaGreen),
            Number(-value.YellowBlue),
            Number(-value.Lightness));

        return $"selectivecolor={range}='{values}'";
    }

    // Negar un cero da "-0", que es feo en el grafo y no significa nada: se normaliza.
    private static string Number(double value) =>
        (Math.Abs(value) < 1e-9 ? 0 : value).ToString("0.###", CultureInfo.InvariantCulture);
}
