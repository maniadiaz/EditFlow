// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using SkiaSharp;

namespace EditFlow.Engine.Overlays;

/// <summary>Lee las dimensiones de una imagen sin decodificarla entera.</summary>
public static class ImageProbe
{
    /// <summary>Ancho y alto de una imagen, o <see langword="null"/> si no es una imagen legible.</summary>
    /// <remarks>
    /// Lee solo la cabecera del archivo: importar un logotipo no necesita FFmpeg ni cargar un
    /// PNG de varios megapíxeles solo para saber su proporción.
    /// </remarks>
    public static (int Width, int Height)? TryRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            {
                return null;
            }

            return (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
