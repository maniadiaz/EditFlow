// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace EditFlow.Engine.Exporting;

/// <summary>Convierte los fundidos de un clip en los filtros de FFmpeg que los aplican.</summary>
/// <remarks>
/// Único sitio para esta cuenta, usado por igual en el preview en vivo y en la exportación
/// (imagen y audio): los dos fundidos, sumados, nunca pueden pasarse de la duración del clip,
/// y aquí es donde se acotan antes de escribir el filtro.
/// </remarks>
public static class FadeFilter
{
    /// <summary>Filtro de video (<c>fade</c>), o <see langword="null"/> si no hay fundido.</summary>
    public static string? BuildVideo(TimeSpan fadeIn, TimeSpan fadeOut, TimeSpan duration) =>
        Build("fade", fadeIn, fadeOut, duration);

    /// <summary>Filtro de audio (<c>afade</c>), o <see langword="null"/> si no hay fundido.</summary>
    public static string? BuildAudio(TimeSpan fadeIn, TimeSpan fadeOut, TimeSpan duration) =>
        Build("afade", fadeIn, fadeOut, duration);

    private static string? Build(string filterName, TimeSpan fadeIn, TimeSpan fadeOut, TimeSpan duration)
    {
        var (inDuration, outDuration) = Clamp(fadeIn, fadeOut, duration);
        if (inDuration <= TimeSpan.Zero && outDuration <= TimeSpan.Zero)
        {
            return null;
        }

        var parts = new List<string>();

        if (inDuration > TimeSpan.Zero)
        {
            parts.Add($"{filterName}=t=in:st=0:d={Seconds(inDuration)}");
        }

        if (outDuration > TimeSpan.Zero)
        {
            var start = duration - outDuration;
            parts.Add($"{filterName}=t=out:st={Seconds(start)}:d={Seconds(outDuration)}");
        }

        return string.Join(',', parts);
    }

    /// <summary>
    /// Acota los dos fundidos a la duración real del clip: uno guardado antes de acelerarlo,
    /// por ejemplo, podría pedir ahora más de lo que dura.
    /// </summary>
    private static (TimeSpan In, TimeSpan Out) Clamp(TimeSpan fadeIn, TimeSpan fadeOut, TimeSpan duration)
    {
        var inDuration = Min(Max(fadeIn, TimeSpan.Zero), duration);
        var outDuration = Min(Max(fadeOut, TimeSpan.Zero), duration - inDuration);
        return (inDuration, outDuration);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
}
