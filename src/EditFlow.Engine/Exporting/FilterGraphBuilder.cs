using System.Globalization;
using System.Text;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Exporting;

/// <summary>Entradas y grafo de filtros necesarios para exportar una timeline.</summary>
/// <param name="InputArguments">Argumentos <c>-ss/-t/-i</c> de cada entrada, en orden.</param>
/// <param name="FilterGraph">Contenido del <c>filter_complex</c>.</param>
/// <param name="VideoLabel">Etiqueta de salida del video, para <c>-map</c>.</param>
/// <param name="AudioLabel">Etiqueta de salida del audio, para <c>-map</c>.</param>
public sealed record FilterGraphPlan(
    IReadOnlyList<string> InputArguments,
    string FilterGraph,
    string VideoLabel,
    string AudioLabel);

/// <summary>
/// Construye el grafo de filtros que recorta cada clip, los normaliza a un lienzo común
/// y los concatena.
/// </summary>
/// <remarks>
/// La normalización va en <b>cada rama</b> del grafo, no al final. Los clips pueden venir
/// con resoluciones, fotogramas por segundo y relaciones de píxel distintas, y
/// <c>concat</c> exige que todas sus entradas coincidan: si no lo hacen, aborta o produce
/// una salida corrupta. Normalizar después de concatenar llegaría tarde.
/// </remarks>
public static class FilterGraphBuilder
{
    /// <summary>Frecuencia de muestreo a la que se normaliza todo el audio.</summary>
    public const int AudioSampleRate = 48_000;

    /// <summary>Construye el plan de entradas y filtros para una timeline.</summary>
    /// <exception cref="ArgumentException">Si la timeline está vacía.</exception>
    public static FilterGraphPlan Build(VideoTimeline timeline, ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(settings);

        if (timeline.IsEmpty)
        {
            throw new ArgumentException("No hay nada que exportar: la timeline está vacía.", nameof(timeline));
        }

        var inputs = new List<string>();
        var graph = new StringBuilder();
        var concatInputs = new StringBuilder();

        var width = settings.Resolution.Width;
        var height = settings.Resolution.Height;
        var inputIndex = 0;

        for (var i = 0; i < timeline.Clips.Count; i++)
        {
            var clip = timeline.Clips[i];

            // '-ss' antes de '-i' hace un salto rápido por índice en lugar de decodificar
            // desde el principio. FFmpeg lo refina hasta el fotograma exacto por su cuenta.
            inputs.AddRange([
                "-ss", Seconds(clip.SourceIn),
                "-t", Seconds(clip.Duration),
                "-i", clip.Source.Path,
            ]);

            var videoInput = inputIndex++;

            // Un clip sin pista de audio necesita silencio sintético: 'concat' exige que
            // todas sus entradas tengan el mismo número de flujos, y sin esto falla con
            // un error que no menciona el audio por ninguna parte.
            int audioInput;
            if (clip.Source.HasAudio)
            {
                audioInput = videoInput;
            }
            else
            {
                inputs.AddRange([
                    "-f", "lavfi",
                    "-t", Seconds(clip.Duration),
                    "-i", $"anullsrc=r={AudioSampleRate}:cl=stereo",
                ]);
                audioInput = inputIndex++;
            }

            // No se aplica 'transpose' aunque el clip declare rotación: FFmpeg rota por
            // su cuenta al decodificar (autorotate está activo por defecto), de modo que
            // el grafo ya recibe el fotograma en su orientación correcta. Rotar aquí
            // además lo dejaría tumbado. Comprobado con un archivo 640x360 marcado a 90
            // grados: el grafo lo recibe como 360x640.
            graph.Append(CultureInfo.InvariantCulture, $"[{videoInput}:v]");
            graph.Append(CultureInfo.InvariantCulture, $"fps={Rate(settings.FrameRate)},");
            graph.Append(CultureInfo.InvariantCulture,
                $"scale={width}:{height}:force_original_aspect_ratio=decrease,");
            graph.Append(CultureInfo.InvariantCulture,
                $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,");
            graph.Append("setsar=1,format=yuv420p");
            graph.Append(CultureInfo.InvariantCulture, $"[v{i}];");
            graph.Append('\n');

            graph.Append(CultureInfo.InvariantCulture, $"[{audioInput}:a]");
            graph.Append(CultureInfo.InvariantCulture,
                $"aformat=sample_fmts=fltp:sample_rates={AudioSampleRate}:channel_layouts=stereo");
            graph.Append(CultureInfo.InvariantCulture, $"[a{i}];");
            graph.Append('\n');

            concatInputs.Append(CultureInfo.InvariantCulture, $"[v{i}][a{i}]");
        }

        graph.Append(CultureInfo.InvariantCulture,
            $"{concatInputs}concat=n={timeline.Clips.Count}:v=1:a=1[vout][aout]");

        return new FilterGraphPlan(inputs, graph.ToString(), "[vout]", "[aout]");
    }

    /// <summary>Formatea una duración en segundos, independiente del idioma del sistema.</summary>
    /// <remarks>
    /// Con un formateo dependiente del idioma, una máquina en español escribiría "1,5"
    /// y FFmpeg leería 1 segundo, descartando la parte decimal sin avisar.
    /// </remarks>
    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Rate(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}
