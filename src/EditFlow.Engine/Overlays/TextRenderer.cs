// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Timeline;
using SkiaSharp;

namespace EditFlow.Engine.Overlays;

/// <summary>Dibuja un texto superpuesto como una imagen PNG con fondo transparente.</summary>
/// <remarks>
/// <para>
/// Es el <b>único</b> código que dibuja texto: el preview muestra el PNG que genera y la
/// exportación lo compone con <c>overlay</c>. Por eso lo que se ve al editar es lo que sale
/// exportado, cosa que no ocurriría con el filtro <c>drawtext</c> de FFmpeg: tiene su propio
/// motor de fuentes, exige escapar las rutas de forma distinta en cada sistema y no coincide
/// con lo que se dibuja en pantalla.
/// </para>
/// <para>
/// El tamaño se expresa como fracción del alto del video y aquí se traduce a píxeles para el
/// alto concreto que se pida: 480 para el preview, el de la resolución elegida al exportar.
/// </para>
/// </remarks>
public static class TextRenderer
{
    /// <summary>Tamaño en píxeles de una imagen de texto.</summary>
    public readonly record struct Size(int Width, int Height);

    /// <summary>
    /// Tipografías instaladas en el equipo, tal como las ve Skia —el mismo motor que dibuja el
    /// texto—, ordenadas alfabéticamente y sin repetidos.
    /// </summary>
    /// <remarks>
    /// Se consultan aquí y no desde la app porque SkiaSharp es una dependencia solo del motor:
    /// así la interfaz no necesita conocerla para poder ofrecer la lista.
    /// </remarks>
    public static IReadOnlyList<string> AvailableFontFamilies() =>
        SKFontManager.Default.FontFamilies.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Calcula qué tamaño tendrá la imagen de un texto, o <see langword="null"/> si no hay nada que dibujar.</summary>
    public static Size? Measure(TextStyle style, int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentOutOfRangeException.ThrowIfLessThan(canvasHeight, 16);

        if (string.IsNullOrWhiteSpace(style.Content))
        {
            return null;
        }

        using var typeface = CreateTypeface(style);
        using var font = CreateFont(style, typeface, canvasHeight);
        var layout = Layout(style, font);
        return new Size(layout.Width, layout.Height);
    }

    /// <summary>Dibuja el texto y lo guarda como PNG.</summary>
    /// <param name="style">Qué y cómo dibujar.</param>
    /// <param name="canvasHeight">Alto del video para el que se dibuja; fija el tamaño de la letra en píxeles.</param>
    /// <param name="path">Archivo PNG de destino.</param>
    /// <returns><see langword="false"/> si el texto está vacío y no se creó ninguna imagen.</returns>
    public static bool RenderToFile(TextStyle style, int canvasHeight, string path)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(canvasHeight, 16);

        if (string.IsNullOrWhiteSpace(style.Content))
        {
            return false;
        }

        using var typeface = CreateTypeface(style);
        using var font = CreateFont(style, typeface, canvasHeight);
        var layout = Layout(style, font);

        using var surface = SKSurface.Create(new SKImageInfo(layout.Width, layout.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var px = font.Size;
        var fill = SKColor.TryParse(style.Color, out var parsed) ? parsed : SKColors.White;

        using var fillPaint = new SKPaint { IsAntialias = true, Color = fill };
        using var shadowPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 170),

            // Un desenfoque en lugar de una copia dura desplazada: se lee sobre fondos claros y
            // oscuros sin el borde de "sombra de los años noventa".
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, px * 0.06f),
        };

        for (var i = 0; i < layout.Lines.Length; i++)
        {
            var x = (layout.Width - layout.Widths[i]) / 2f;
            var baseline = layout.Padding + (i * layout.LineHeight) - font.Metrics.Ascent;

            if (style.Shadow)
            {
                canvas.DrawText(layout.Lines[i], x + (px * 0.04f), baseline + (px * 0.05f), font, shadowPaint);
            }

            canvas.DrawText(layout.Lines[i], x, baseline, font, fillPaint);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(path);
        data.SaveTo(output);
        return true;
    }

    private static SKTypeface CreateTypeface(TextStyle style)
    {
        // Una tipografía propia, traída de un archivo, tiene prioridad: es justo lo que se pidió
        // al importarla, y su archivo ya trae su propio peso y estilo (no hay un "negrita de este
        // archivo" que pedirle a Skia, así que Negrita/Cursiva no le afectan).
        if (style.FontFilePath is { Length: > 0 } path && File.Exists(path))
        {
            return SKTypeface.FromFile(path) ?? SKTypeface.Default;
        }

        return SKTypeface.FromFamilyName(
            // Sin elegir ninguna, la de sans-serif del sistema: existe en Windows, Linux y macOS.
            // Si se pidió una que no está instalada, Skia cae sola a esa misma por defecto.
            style.FontFamily,
            style.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            style.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
    }

    private static SKFont CreateFont(TextStyle style, SKTypeface typeface, int canvasHeight) =>
        new(typeface, (float)(Math.Clamp(style.Size, TextStyle.MinimumSize, TextStyle.MaximumSize) * canvasHeight))
        {
            Edging = SKFontEdging.SubpixelAntialias,
            Subpixel = true,
        };

    private readonly record struct TextLayout(
        string[] Lines, float[] Widths, float LineHeight, float Padding, int Width, int Height);

    private static TextLayout Layout(TextStyle style, SKFont font)
    {
        var lines = style.Content.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        using var measure = new SKPaint(font);
        var widths = lines.Select(line => measure.MeasureText(line)).ToArray();

        var metrics = font.Metrics;
        var lineHeight = metrics.Descent - metrics.Ascent + metrics.Leading;

        // Margen alrededor del texto para que la sombra difuminada no se corte en el borde.
        var padding = font.Size * 0.3f;

        var width = (int)Math.Ceiling(widths.Max() + (2 * padding));
        var height = (int)Math.Ceiling((lineHeight * lines.Length) + (2 * padding));
        return new TextLayout(lines, widths, lineHeight, padding, Math.Max(width, 1), Math.Max(height, 1));
    }
}
