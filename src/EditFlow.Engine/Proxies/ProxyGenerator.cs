// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Execution;
using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Proxies;

/// <summary>Genera la copia de edición de un video.</summary>
/// <remarks>
/// <para>
/// La copia está pensada para <b>buscar</b> rápido, no para verse bien: 480p, un fotograma
/// clave cada 12 y sin fotogramas B, que obligan a decodificar hacia delante antes de poder
/// mostrar uno. Sale unas cinco veces más pequeña que una copia con todo fotogramas clave y
/// salta casi igual de rápido.
/// </para>
/// <para>
/// No lleva audio: el sonido del preview sale de la mezcla, que se renderiza siempre a partir
/// de los originales.
/// </para>
/// <para>
/// Conserva los tiempos del original (<c>passthrough</c>). Si un video de fotogramas
/// variables se convirtiera a ritmo fijo, la copia se desplazaría respecto al original y los
/// cortes caerían en otro sitio del preview que en la exportación.
/// </para>
/// </remarks>
public sealed class ProxyGenerator
{
    private readonly FFmpegTools _tools;

    /// <summary>Crea un generador que usará los ejecutables indicados.</summary>
    public ProxyGenerator(FFmpegTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
    }

    /// <summary>Argumentos de FFmpeg para generar la copia. Expuestos para poder probarlos.</summary>
    public static IReadOnlyList<string> BuildArguments(string sourcePath, string outputPath) =>
    [
        "-hide_banner", "-loglevel", "error", "-y", "-nostats",
        "-progress", "pipe:1",
        "-i", sourcePath,
        "-map", "0:v:0",
        "-vf", $"scale=-2:{ProxyPolicy.ProxyHeight}",
        "-c:v", "libx264",
        "-preset", "veryfast",
        "-crf", "26",
        "-g", "12",
        "-bf", "0",
        "-pix_fmt", "yuv420p",
        "-fps_mode", "passthrough",

        // Dos hilos: con más, generar la copia compite con la edición por la CPU en un
        // equipo modesto, que es justo donde la copia hace falta.
        "-threads", "2",
        "-an",
        "-movflags", "+faststart",
        "-f", "mp4",
        outputPath,
    ];

    /// <summary>Genera la copia y la deja en <paramref name="outputPath"/>.</summary>
    /// <param name="sourcePath">Video original.</param>
    /// <param name="outputPath">Destino final; se escribe a un temporal y se renombra al acabar.</param>
    /// <param name="duration">Duración del original, para calcular el porcentaje.</param>
    /// <param name="progress">Recibe el avance, de 0 a 100. Se invoca en un hilo del pool.</param>
    /// <param name="cancellationToken">Cancela y elimina el temporal.</param>
    /// <exception cref="InvalidOperationException">Si FFmpeg falla.</exception>
    public async Task GenerateAsync(
        string sourcePath,
        string outputPath,
        TimeSpan duration,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Si se interrumpe a mitad, no debe quedar a la vista un archivo truncado que se
        // tomaría por una copia completa.
        var partial = ProxyCache.PartialPathFor(outputPath);
        var parser = new ProgressParser(duration);

        try
        {
            var result = await ProcessRunner.RunAsync(
                _tools.FFmpegPath,
                BuildArguments(sourcePath, partial),
                line =>
                {
                    if (parser.Feed(line) is { } update)
                    {
                        progress?.Invoke(update.Percentage);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    "No se pudo generar la copia de edición: " + Summarise(result.StandardError));
            }

            File.Move(partial, outputPath, overwrite: true);
            progress?.Invoke(100);
        }
        finally
        {
            try
            {
                if (File.Exists(partial))
                {
                    File.Delete(partial);
                }
            }
            catch (IOException)
            {
                // ProxyCache.TrimTo lo limpiará más adelante.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Summarise(string standardError) =>
        string.Join(
            " ",
            standardError.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3));
}
