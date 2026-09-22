// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Execution;

namespace EditFlow.Engine.Exporting;

/// <summary>Resultado de una exportación.</summary>
/// <param name="Succeeded">Si el archivo se generó correctamente.</param>
/// <param name="OutputPath">Ruta del archivo generado.</param>
/// <param name="Elapsed">Tiempo que tardó.</param>
/// <param name="ErrorMessage">Explicación del fallo, si lo hubo.</param>
/// <param name="Command">Comando ejecutado, para mostrarlo o depurar.</param>
public sealed record ExportResult(
    bool Succeeded,
    string OutputPath,
    TimeSpan Elapsed,
    string? ErrorMessage = null,
    string? Command = null,
    IReadOnlyList<string>? Files = null);

/// <summary>
/// Ejecuta una exportación informando del avance y permitiendo cancelarla.
/// </summary>
public sealed class ExportJob
{
    private readonly FFmpegTools _tools;

    /// <summary>Crea un ejecutor que usará los binarios indicados.</summary>
    public ExportJob(FFmpegTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
    }

    /// <summary>
    /// Exporta una timeline a un archivo de video.
    /// </summary>
    /// <param name="timeline">Secuencia de clips a exportar.</param>
    /// <param name="settings">Resolución, codificador, control de tasa y destino.</param>
    /// <param name="progress">Receptor del avance; puede ser <see langword="null"/>.</param>
    /// <param name="cancellationToken">Permite cancelar la exportación.</param>
    /// <exception cref="OperationCanceledException">Si se cancela.</exception>
    public Task<ExportResult> RunAsync(
        EditSequence sequence,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(settings);

        return RunCoreAsync(ExportCommandBuilder.Build(sequence, settings), settings, progress, cancellationToken);
    }

    /// <summary>Exporta solo una pista de video, sin pistas de audio.</summary>
    public Task<ExportResult> RunAsync(
        VideoTimeline timeline,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(settings);

        return RunCoreAsync(ExportCommandBuilder.Build(timeline, settings), settings, progress, cancellationToken);
    }

    private async Task<ExportResult> RunCoreAsync(
        ExportCommand builtCommand,
        ExportSettings settings,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var command = builtCommand;

        // El progreso se mide contra la duración real del archivo, que puede superar la
        // del video si una pista de audio dura más.
        var parser = new ProgressParser(command.Duration);
        var stopwatch = Stopwatch.StartNew();
        var startedAt = DateTime.UtcNow.AddSeconds(-1);

        try
        {
            var result = await ProcessRunner.RunAsync(
                _tools.FFmpegPath,
                command.Arguments,
                line =>
                {
                    var update = parser.Feed(line);
                    if (update is not null)
                    {
                        progress?.Report(update);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            if (result.Succeeded && File.Exists(settings.PrimaryOutputPath))
            {
                return new ExportResult(
                    Succeeded: true,
                    settings.OutputPath,
                    stopwatch.Elapsed,
                    Command: command.ToDisplayString(_tools.FFmpegPath),
                    Files: ProducedFiles(settings, startedAt));
            }

            // Una exportación fallida no debe dejar un archivo a medias: quien lo
            // encuentre después no tendrá forma de saber que está incompleto.
            await DeleteOutputsAsync(settings, startedAt).ConfigureAwait(false);

            return new ExportResult(
                Succeeded: false,
                settings.OutputPath,
                stopwatch.Elapsed,
                ErrorMessage: SummariseError(result),
                Command: command.ToDisplayString(_tools.FFmpegPath));
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            await DeleteOutputsAsync(settings, startedAt).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Extrae el mensaje de error relevante de la salida de FFmpeg.</summary>
    internal static string SummariseError(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // FFmpeg escribe el error real en las últimas líneas; todo lo anterior suele ser
        // información de flujos que no explica nada. Se descarta también el banner de
        // SVT-AV1, que aparece aunque no tenga relación con el fallo.
        var meaningful = result.StandardError
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Where(line => !line.StartsWith("Svt[", StringComparison.Ordinal))
            .ToArray();

        if (meaningful.Length == 0)
        {
            return $"FFmpeg terminó con código {result.ExitCode} sin dar detalles.";
        }

        return string.Join(Environment.NewLine, meaningful.TakeLast(3));
    }

    /// <summary>Archivos que esta exportación ha producido: el propio o todas sus partes.</summary>
    private static List<string> ProducedFiles(ExportSettings settings, DateTime startedAt)
    {
        if (!settings.IsSegmented)
        {
            return [settings.OutputPath];
        }

        return EnumerateSegments(settings, startedAt).ToList();
    }

    // Solo cuentan los archivos escritos durante esta exportación: una carpeta puede guardar partes
    // de una exportación anterior con el mismo nombre, y esas no son ni de esta ni se deben borrar.
    private static IEnumerable<string> EnumerateSegments(ExportSettings settings, DateTime startedAt)
    {
        for (var number = 1; ; number++)
        {
            var path = settings.SegmentPath(number);
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < startedAt)
            {
                yield break;
            }

            yield return path;
        }
    }

    private static async Task DeleteOutputsAsync(ExportSettings settings, DateTime startedAt)
    {
        if (!settings.IsSegmented)
        {
            await DeletePartialOutputAsync(settings.OutputPath).ConfigureAwait(false);
            return;
        }

        foreach (var path in EnumerateSegments(settings, startedAt).ToList())
        {
            await DeletePartialOutputAsync(path).ConfigureAwait(false);
        }
    }

    private static async Task DeletePartialOutputAsync(string path)
    {
        // Al cancelar se mata FFmpeg, pero Windows tarda unos milisegundos en soltar el archivo
        // que tenía abierto. Con un solo intento, el borrado fallaba de vez en cuando y quedaba
        // a la vista justo lo que no debe quedar: un archivo a medias que parece completo.
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        // Tras tres segundos se renuncia: no merece enmascarar el error original que
        // provocó la limpieza.
    }
}
