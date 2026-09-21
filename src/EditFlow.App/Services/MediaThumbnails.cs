// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EditFlow.Core.Media;
using EditFlow.Engine;
using EditFlow.Engine.Thumbnails;

namespace EditFlow.App.Services;

/// <summary>Miniaturas de los medios del proyecto, guardadas en una caché del usuario.</summary>
/// <remarks>
/// La miniatura se identifica por ruta, tamaño y fecha del archivo, igual que las copias de
/// edición: si el video cambia, la miniatura vieja deja de encontrarse. Los nombres son un
/// hash, así que la carpeta no revela qué videos tiene el usuario.
/// </remarks>
public sealed class MediaThumbnails : IDisposable
{
    // Extraer una miniatura es barato, pero un proyecto con cien videos no debe lanzar
    // cien FFmpeg a la vez y competir con el preview.
    private readonly SemaphoreSlim _limit = new(2);
    private readonly FrameExtractor _extractor;

    /// <summary>Crea el servicio.</summary>
    public MediaThumbnails(FFmpegTools tools) => _extractor = new FrameExtractor(tools);

    /// <summary>Carpeta de la caché.</summary>
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EditFlow", "thumbs");

    /// <summary>Devuelve la ruta de la miniatura de un medio, generándola si hace falta.</summary>
    /// <returns>La imagen, o <see langword="null"/> si el medio no tiene imagen o no se pudo extraer.</returns>
    public async Task<string?> GetAsync(MediaInfo media, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);

        // Un audio no tiene fotogramas.
        if (media.Width <= 0 || !File.Exists(media.Path))
        {
            return null;
        }

        var info = new FileInfo(media.Path);
        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 12).ToLowerInvariant();
        var target = Path.Combine(Directory, name + ".jpg");

        if (File.Exists(target) && new FileInfo(target).Length > 0)
        {
            return target;
        }

        await _limit.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Un segundo dentro, o la mitad si el video es más corto: el primer fotograma
            // suele ser negro.
            var at = TimeSpan.FromSeconds(Math.Min(1, media.Duration.TotalSeconds / 2));
            return await _extractor.ExtractAsync(media.Path, at, target, width: 320, cancellationToken)
                .ConfigureAwait(false)
                ? target
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            _limit.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _limit.Dispose();
}
