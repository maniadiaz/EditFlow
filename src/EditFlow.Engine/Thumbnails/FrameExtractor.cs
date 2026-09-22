// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using EditFlow.Engine.Execution;

namespace EditFlow.Engine.Thumbnails;

/// <summary>Extrae un fotograma de un video como imagen, para portadas y miniaturas.</summary>
public sealed class FrameExtractor
{
    private readonly FFmpegTools _tools;

    /// <summary>Crea un extractor que usará los ejecutables indicados.</summary>
    public FrameExtractor(FFmpegTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
    }

    /// <summary>Argumentos de FFmpeg para extraer el fotograma. Expuestos para poder probarlos.</summary>
    /// <remarks>
    /// El recorte por color, si lo hay, va después del ajuste de color y trae sus propias
    /// conversiones de formato. Quien lo pida debe escribir en un formato que conserve el canal
    /// alfa —un PNG—: un JPEG lo tiraría y el fondo volvería a verse.
    /// </remarks>
    public static IReadOnlyList<string> BuildArguments(
        string sourcePath,
        TimeSpan at,
        int width,
        string outputPath,
        string? colorFilter = null,
        string? keyFilter = null) =>
    [
        "-hide_banner", "-loglevel", "error", "-y",

        // -ss antes de -i busca por índice en vez de decodificar hasta ahí: sale al instante
        // incluso en un 4K de una hora.
        "-ss", at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        "-i", sourcePath,
        "-frames:v", "1",

        // Alto par calculado a partir del ancho, conservando la proporción.
        "-vf", $"scale={width}:-2"
            + (colorFilter is null ? string.Empty : ",format=yuv420p," + colorFilter)
            + (keyFilter is null ? string.Empty : "," + keyFilter),
        "-q:v", "4",
        outputPath,
    ];

    /// <summary>Extrae un fotograma a un archivo de imagen; el formato lo decide la extensión.</summary>
    /// <param name="sourcePath">Video de origen.</param>
    /// <param name="at">Instante dentro del archivo.</param>
    /// <param name="outputPath">Imagen de destino; se sobrescribe. Con <paramref name="keyFilter"/> debe ser un <c>.png</c>.</param>
    /// <param name="width">Ancho de la imagen.</param>
    /// <param name="colorFilter">Ajuste de color a aplicar, o <see langword="null"/>.</param>
    /// <param name="keyFilter">Recorte por color a aplicar, o <see langword="null"/>.</param>
    /// <param name="cancellationToken">Para abandonar la extracción.</param>
    /// <returns><see langword="true"/> si se creó la imagen.</returns>
    /// <remarks>
    /// No lanza si FFmpeg falla: una portada que no sale es un detalle estético, y quien la
    /// pide debe poder seguir sin ella.
    /// </remarks>
    public async Task<bool> ExtractAsync(
        string sourcePath,
        TimeSpan at,
        string outputPath,
        int width = 480,
        string? colorFilter = null,
        string? keyFilter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 16);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var result = await ProcessRunner.RunAsync(
            _tools.FFmpegPath,
            BuildArguments(sourcePath, at < TimeSpan.Zero ? TimeSpan.Zero : at, width, outputPath, colorFilter, keyFilter),
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }
}
