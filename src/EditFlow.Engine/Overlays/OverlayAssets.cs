// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Overlays;

/// <summary>
/// Imágenes ya dibujadas de los textos, guardadas por contenido para no repetir el trabajo.
/// </summary>
/// <remarks>
/// El nombre del archivo sale de un hash de todo lo que influye en el dibujo (texto, tamaño,
/// color, estilo y alto del video): el mismo título se dibuja una sola vez por resolución, y
/// cualquier cambio da otro archivo en lugar de reutilizar uno viejo.
/// </remarks>
public sealed class TextRenderCache
{
    private readonly string _directory;

    /// <summary>Crea la caché en la carpeta indicada.</summary>
    public TextRenderCache(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <summary>Caché compartida, en la carpeta temporal del sistema.</summary>
    public static TextRenderCache Shared { get; } =
        new(Path.Combine(Path.GetTempPath(), "editflow-text"));

    /// <summary>Ruta del PNG de un texto, dibujándolo si aún no existe.</summary>
    /// <returns>La imagen, o <see langword="null"/> si el texto está vacío.</returns>
    public string? GetPath(TextStyle style, int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(style);

        if (string.IsNullOrWhiteSpace(style.Content))
        {
            return null;
        }

        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{canvasHeight}|{style.Size:R}|{style.Color}|{style.Bold}|{style.Italic}|{style.Shadow}|{style.FontFamily}|{style.Content}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var path = Path.Combine(_directory, Convert.ToHexString(hash, 0, 12).ToLowerInvariant() + ".png");

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            TextRenderer.RenderToFile(style, canvasHeight, path);
        }

        return path;
    }
}

/// <summary>Las imágenes que hay que componer sobre el video, una por elemento superpuesto visible.</summary>
public static class OverlayAssets
{
    /// <summary>Prepara las imágenes de todos los elementos visibles de una secuencia.</summary>
    /// <param name="sequence">Montaje.</param>
    /// <param name="canvasHeight">Alto del video para el que se dibujan los textos.</param>
    /// <param name="cache">Dónde se guardan los textos dibujados.</param>
    /// <returns>Ruta de la imagen de cada elemento, por su identidad. Los vacíos o ilocalizables no aparecen.</returns>
    public static IReadOnlyDictionary<Guid, string> Prepare(
        EditSequence sequence, int canvasHeight, TextRenderCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        cache ??= TextRenderCache.Shared;

        var paths = new Dictionary<Guid, string>();

        foreach (var track in sequence.OverlayTracks)
        {
            if (track.IsHidden)
            {
                continue;
            }

            foreach (var item in track.Items)
            {
                var path = item.Kind == OverlayKind.Text && item.Text is not null
                    ? cache.GetPath(item.Text, canvasHeight)
                    : item.ImagePath;

                if (path is not null && File.Exists(path))
                {
                    paths[item.Id] = path;
                }
            }
        }

        return paths;
    }
}
