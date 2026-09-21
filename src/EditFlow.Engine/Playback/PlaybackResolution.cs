// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EditFlow.Engine.Playback;

/// <summary>
/// Resolución de reproducción del preview: completa, 1/2, 1/4, 1/8 o 1/16.
/// </summary>
/// <remarks>
/// Es una fracción de la resolución del propio video, como en Premiere. Un video 4K a 1/2 se
/// decodifica en 1920×1080 y a 1/4 en 960×540, con mucha menos carga para la CPU y la tarjeta.
/// Afecta solo a lo que se ve en el preview: ni el archivo original ni la exportación se tocan.
/// </remarks>
public static class PlaybackResolution
{
    /// <summary>Divisores disponibles; 1 es la resolución completa.</summary>
    public static IReadOnlyList<int> Divisors { get; } = [1, 2, 4, 8, 16];

    /// <summary>Por debajo de esto la imagen deja de ser reconocible.</summary>
    public const int MinimumHeight = 90;

    /// <summary>Texto para mostrar: «Completa», «1/2», «1/4»…</summary>
    public static string Label(int divisor) => divisor <= 1 ? "Completa" : $"1/{divisor}";

    /// <summary>Devuelve el divisor válido más cercano por abajo; cualquier valor raro es «completa».</summary>
    public static int Normalize(int divisor) => Divisors.Contains(divisor) ? divisor : 1;

    /// <summary>
    /// Altura a la que decodificar el preview.
    /// </summary>
    /// <param name="divisor">Fracción elegida (1, 2, 4, 8 o 16).</param>
    /// <param name="sourceHeight">Altura del video original.</param>
    /// <param name="fullHeight">
    /// Altura que se usaría con la resolución completa: la que ocupa el preview en pantalla, sin
    /// pasar de la del video.
    /// </param>
    /// <returns>
    /// La fracción del original, sin superar nunca <paramref name="fullHeight"/>: bajar la
    /// resolución de reproducción jamás puede costar más que dejarla completa.
    /// </returns>
    public static int DecodeHeight(int divisor, int sourceHeight, int fullHeight)
    {
        var full = Even(Math.Max(fullHeight, 2));
        if (Normalize(divisor) == 1 || sourceHeight <= 0)
        {
            return full;
        }

        var fraction = Even(Math.Max(sourceHeight / divisor, MinimumHeight));
        return Math.Min(fraction, full);
    }

    /// <summary>Ancho de un fotograma 16:9 de la altura dada, en un número par (lo exigen los códecs).</summary>
    public static int WidthFor(int height) => (int)Math.Round(height * 16.0 / 9 / 2) * 2;

    private static int Even(int value) => value / 2 * 2;
}
