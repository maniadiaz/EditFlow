// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Exporting;

/// <summary>
/// Traduce un <see cref="ChromaKey"/> al filtro de FFmpeg que recorta el fondo.
/// </summary>
/// <remarks>
/// <para>
/// El fragmento sale entero, con sus dos conversiones de formato dentro, y termina en
/// <c>rgba</c>. No es un detalle de comodidad: <c>chromakey</c> mide la distancia de color sobre
/// los planos de croma, así que hay que dárselo en <b>YUV con alfa</b>. Pasado en RGBA, compara
/// canales que no son los que cree y recorta de más: en las pruebas, un azul saturado salía
/// medio transparente. Y en <c>yuva444p</c> en vez de <c>yuva420p</c> porque el croma a media
/// resolución evalúa el recorte por bloques de 2×2 y deja el contorno dentado.
/// </para>
/// <para>
/// Es el mismo fragmento para el preview y para la exportación, igual que con
/// <see cref="ColorFilter"/>: si se calcularan por separado, un día dejarían de coincidir.
/// </para>
/// </remarks>
public static class ChromaKeyFilter
{
    /// <summary>
    /// Construye el fragmento de filtros, o <see langword="null"/> si no hay nada que recortar.
    /// </summary>
    public static string? Build(ChromaKey? key)
    {
        if (key is null || !key.Enabled)
        {
            return null;
        }

        var value = key.Clamped();
        var builder = new StringBuilder("format=yuva444p,chromakey=");

        // FFmpeg escribe los colores como 0xRRGGBB; '#' es un comentario en un guion de filtros.
        builder.Append("0x").Append(value.Color.TrimStart('#'));
        builder.Append(':').Append(Number(value.Similarity));
        builder.Append(':').Append(Number(value.Blend));

        // Se vuelve a RGBA, que es donde componen tanto el preview como el 'overlay' de la
        // exportación, y de paso es el formato que pide el filtro que quita el derrame.
        builder.Append(",format=rgba");

        if (value.Despill)
        {
            // Se aplica después de recortar: trabaja sobre lo que queda, que es justo el sujeto
            // con el tinte del fondo encima.
            builder.Append(",despill=type=").Append(value.SpillChannel);
        }

        return builder.ToString();
    }

    private static string Number(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
