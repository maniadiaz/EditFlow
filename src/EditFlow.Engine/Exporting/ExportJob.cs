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
    string? Command = null);

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
    public async Task<ExportResult> RunAsync(
        VideoTimeline timeline,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(settings);

        using var command = ExportCommandBuilder.Build(timeline, settings);
        var parser = new ProgressParser(timeline.Duration);
        var stopwatch = Stopwatch.StartNew();

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

            if (result.Succeeded && File.Exists(settings.OutputPath))
            {
                return new ExportResult(
                    Succeeded: true,
                    settings.OutputPath,
                    stopwatch.Elapsed,
                    Command: command.ToDisplayString(_tools.FFmpegPath));
            }

            // Una exportación fallida no debe dejar un archivo a medias: quien lo
            // encuentre después no tendrá forma de saber que está incompleto.
            DeletePartialOutput(settings.OutputPath);

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
            DeletePartialOutput(settings.OutputPath);
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

    private static void DeletePartialOutput(string path)
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
            // FFmpeg puede tardar un instante en soltar el archivo. No merece enmascarar
            // el error original que provocó la limpieza.
        }
        catch (UnauthorizedAccessException)
        {
            // Idem.
        }
    }
}
