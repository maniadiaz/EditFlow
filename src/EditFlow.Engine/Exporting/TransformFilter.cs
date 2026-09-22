// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Exporting;

/// <summary>
/// Convierte un encuadre (zoom, posición, rotación) en los filtros de FFmpeg que lo aplican.
/// </summary>
/// <remarks>
/// Único sitio donde el encuadre se traduce a filtros, usado por igual en el preview en vivo y
/// en la exportación. Trabaja sobre las dimensiones del propio fotograma del clip (o de la
/// resolución a la que se decodifica en el preview, que es proporcional): el resultado tiene el
/// mismo tamaño que la entrada, así que encaja sin más en el resto de la rama —normalización de
/// tamaño y color— que ya existía antes del encuadre.
/// </remarks>
public static class TransformFilter
{
    /// <summary>Filtros que aplican el encuadre, o <see langword="null"/> si es el normal.</summary>
    public static string? Build(ClipTransform? transform, int width, int height)
    {
        if (transform is null || transform.IsNone || width <= 0 || height <= 0)
        {
            return null;
        }

        var t = transform.Clamped();

        var scaledWidth = EvenRound(width * t.Scale);
        var scaledHeight = EvenRound(height * t.Scale);

        var cropX = Math.Clamp(
            EvenRound(((scaledWidth - width) / 2.0) + (t.OffsetX * width)), 0, Math.Max(0, scaledWidth - width));
        var cropY = Math.Clamp(
            EvenRound(((scaledHeight - height) / 2.0) + (t.OffsetY * height)), 0, Math.Max(0, scaledHeight - height));

        var filters = new List<string>
        {
            $"scale={scaledWidth}:{scaledHeight}",
            $"crop={width}:{height}:{cropX}:{cropY}",
        };

        // Girar deja ver las esquinas del fotograma original si no se acercó lo suficiente antes:
        // se rellenan de negro, igual que en cualquier editor, y es el propio zoom quien lo evita.
        if (Math.Abs(t.Rotation) >= 0.05)
        {
            var radians = t.Rotation * Math.PI / 180;
            filters.Add($"rotate={Number(radians)}:ow={width}:oh={height}:c=black");
        }

        return string.Join(',', filters);
    }

    private static int EvenRound(double value) => 2 * (int)Math.Round(value / 2.0);

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
