// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Playback;

/// <summary>Resultado de renderizar la mezcla de audio del preview.</summary>
/// <param name="Path">Archivo generado.</param>
/// <param name="Duration">Duración de la mezcla.</param>
public sealed record PreviewMix(string Path, TimeSpan Duration);

/// <summary>
/// Renderiza el audio de toda la secuencia a un único archivo para el preview.
/// </summary>
/// <remarks>
/// <para>
/// Reproducir el audio clip por clip tenía dos problemas: un pequeño hueco en cada corte,
/// al abrir el archivo siguiente, y ninguna forma de oír la música, porque cada reproductor
/// carga un solo archivo. Con la mezcla ya renderizada hay <b>un solo audio continuo</b>
/// cuyo reloj es el de la timeline, y el video se limita a seguirlo.
/// </para>
/// <para>
/// El grafo es el mismo que el de la exportación, sin la parte de video, de modo que lo que
/// se oye es exactamente lo que se exportará.
/// </para>
/// <para>
/// Se escribe en FLAC con la compresión más baja: sin pérdida, sin retardo de codificación
/// que desplace el audio respecto a la imagen —como sí introduce AAC—, y unos cinco
/// megabytes por minuto, frente a los diez del WAV.
/// </para>
/// </remarks>
public sealed class PreviewMixRenderer
{
    private const int InlineGraphLimit = 8_000;

    private readonly FFmpegTools _tools;

    /// <summary>Crea un renderizador que usará los ejecutables indicados.</summary>
    public PreviewMixRenderer(FFmpegTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
    }

    /// <summary>Renderiza la mezcla de la secuencia en la ruta indicada.</summary>
    /// <exception cref="ArgumentException">Si la secuencia no tiene video.</exception>
    /// <exception cref="InvalidOperationException">Si FFmpeg falla.</exception>
    /// <exception cref="OperationCanceledException">Si se cancela.</exception>
    public async Task<PreviewMix> RenderAsync(
        EditSequence sequence,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var plan = FilterGraphBuilder.BuildAudioOnly(sequence);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Se escribe a un temporal y se renombra al final. Si se cancela o falla a mitad,
        // no queda a la vista un archivo truncado que el reproductor podría abrir creyendo
        // que está completo.
        var partial = outputPath + ".partial";
        string? scriptPath = null;

        try
        {
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-nostats" };
            arguments.AddRange(plan.InputArguments);

            if (plan.FilterGraph.Length > InlineGraphLimit)
            {
                // Windows limita la línea de comandos a unos 32 000 caracteres.
                scriptPath = Path.Combine(Path.GetTempPath(), $"editflow-{Guid.NewGuid():N}.filter");
                await File.WriteAllTextAsync(
                    scriptPath, plan.FilterGraph, new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
                arguments.AddRange(["-filter_complex_script", scriptPath]);
            }
            else
            {
                arguments.AddRange(["-filter_complex", plan.FilterGraph]);
            }

            arguments.AddRange(
            [
                "-map", plan.AudioLabel,
                "-c:a", "flac",
                "-compression_level", "0",
                "-f", "flac",
                partial,
            ]);

            var result = await ProcessRunner
                .RunAsync(_tools.FFmpegPath, arguments, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    "No se pudo preparar el audio del preview: " + Summarise(result.StandardError));
            }

            File.Move(partial, outputPath, overwrite: true);
            return new PreviewMix(outputPath, plan.Duration);
        }
        finally
        {
            DeleteQuietly(partial);
            if (scriptPath is not null)
            {
                DeleteQuietly(scriptPath);
            }
        }
    }

    private static string Summarise(string standardError)
    {
        var lines = standardError
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .TakeLast(3);

        return string.Join(" ", lines);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Un temporal que no se puede borrar ahora queda para la limpieza del sistema.
        }
        catch (UnauthorizedAccessException)
        {
            // Idem.
        }
    }
}
