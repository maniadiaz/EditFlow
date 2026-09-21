// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Exporting;

/// <summary>Entradas y grafo de filtros necesarios para exportar una timeline.</summary>
/// <param name="InputArguments">Argumentos <c>-ss/-t/-i</c> de cada entrada, en orden.</param>
/// <param name="FilterGraph">Contenido del <c>filter_complex</c>.</param>
/// <param name="VideoLabel">Etiqueta de salida del video, para <c>-map</c>.</param>
/// <param name="AudioLabel">Etiqueta de salida del audio, para <c>-map</c>.</param>
/// <param name="Duration">
/// Duración real de la exportación: la del video, o la de la última pista de audio
/// audible si esta la supera.
/// </param>
public sealed record FilterGraphPlan(
    IReadOnlyList<string> InputArguments,
    string FilterGraph,
    string VideoLabel,
    string AudioLabel,
    TimeSpan Duration);

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

    /// <summary>Construye el plan para una secuencia completa: video y pistas de audio.</summary>
    /// <exception cref="ArgumentException">Si no hay ningún clip de video.</exception>
    public static FilterGraphPlan Build(EditSequence sequence, ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return BuildCore(sequence.Video, sequence.AudioTracks, settings);
    }

    /// <summary>Construye el plan para una pista de video sin pistas de audio.</summary>
    /// <exception cref="ArgumentException">Si la timeline está vacía.</exception>
    public static FilterGraphPlan Build(VideoTimeline timeline, ExportSettings settings) =>
        BuildCore(timeline, [], settings);

    /// <summary>
    /// Construye el plan de la mezcla de <b>solo audio</b> de una secuencia.
    /// </summary>
    /// <remarks>
    /// Es el mismo grafo que la exportación, sin la parte de video. La razón de compartirlo
    /// en lugar de escribir uno aparte para el preview: así lo que se oye al reproducir es
    /// exactamente lo que saldrá exportado —volúmenes, fundidos, silencios, solo—, y no hay
    /// dos implementaciones que puedan divergir sin que nadie se dé cuenta.
    ///
    /// Los clips que no aportan su propio sonido ni siquiera abren su archivo de video: no
    /// hace falta leer un 4K entero para producir silencio.
    /// </remarks>
    /// <exception cref="ArgumentException">Si no hay ningún clip de video.</exception>
    public static FilterGraphPlan BuildAudioOnly(EditSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return BuildCore(sequence.Video, sequence.AudioTracks, settings: null, includeVideo: false);
    }

    private static FilterGraphPlan BuildCore(
        VideoTimeline timeline,
        IReadOnlyList<AudioTrack> audioTracks,
        ExportSettings? settings,
        bool includeVideo = true)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        if (includeVideo)
        {
            ArgumentNullException.ThrowIfNull(settings);
        }

        if (timeline.IsEmpty)
        {
            throw new ArgumentException("No hay nada que exportar: la timeline está vacía.", nameof(timeline));
        }

        var inputs = new List<string>();
        var graph = new StringBuilder();
        var concatInputs = new StringBuilder();

        var width = settings?.Resolution.Width ?? 0;
        var height = settings?.Resolution.Height ?? 0;
        var inputIndex = 0;

        for (var i = 0; i < timeline.Clips.Count; i++)
        {
            var clip = timeline.Clips[i];

            // '-ss' antes de '-i' hace un salto rápido por índice en lugar de decodificar
            // desde el principio. FFmpeg lo refina hasta el fotograma exacto por su cuenta.
            var videoInput = -1;
            if (includeVideo || clip.HasOwnAudio)
            {
                inputs.AddRange([
                    "-ss", Seconds(clip.SourceIn),
                    "-t", Seconds(clip.Duration),
                    "-i", clip.Source.Path,
                ]);

                videoInput = inputIndex++;
            }

            // Un clip sin pista de audio necesita silencio sintético: 'concat' exige que
            // todas sus entradas tengan el mismo número de flujos, y sin esto falla con
            // un error que no menciona el audio por ninguna parte.
            // Con el audio separado tampoco se usa el del clip: ya suena desde su pista, y
            // usarlo también lo duplicaría. Se sustituye por silencio, que además mantiene
            // el número de flujos que 'concat' exige.
            int audioInput;
            if (clip.HasOwnAudio)
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
            if (includeVideo)
            {
                graph.Append(CultureInfo.InvariantCulture, $"[{videoInput}:v]");
                graph.Append(CultureInfo.InvariantCulture, $"fps={Rate(settings!.FrameRate)},");
                graph.Append(CultureInfo.InvariantCulture,
                    $"scale={width}:{height}:force_original_aspect_ratio=decrease,");
                graph.Append(CultureInfo.InvariantCulture,
                    $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,");
                graph.Append("setsar=1,format=yuv420p");
                graph.Append(CultureInfo.InvariantCulture, $"[v{i}];");
                graph.Append('\n');
            }

            graph.Append(CultureInfo.InvariantCulture, $"[{audioInput}:a]");
            graph.Append(CultureInfo.InvariantCulture,
                $"aformat=sample_fmts=fltp:sample_rates={AudioSampleRate}:channel_layouts=stereo");

            // El volumen solo se aplica al audio propio del clip: la fuente de silencio no
            // tiene nada que amplificar, y añadirle un filtro solo complicaría el grafo.
            if (clip.HasOwnAudio && Math.Abs(clip.AudioGainDb) > 0.001)
            {
                graph.Append(CultureInfo.InvariantCulture,
                    $",volume={clip.AudioGainDb.ToString("0.##", CultureInfo.InvariantCulture)}dB");
            }

            graph.Append(CultureInfo.InvariantCulture, $"[a{i}];");
            graph.Append('\n');

            if (includeVideo)
            {
                concatInputs.Append(CultureInfo.InvariantCulture, $"[v{i}]");
            }

            concatInputs.Append(CultureInfo.InvariantCulture, $"[a{i}]");
        }

        // Solo cuentan las pistas que se oyen. Una pista en solo silencia a las demás aunque
        // no estén silenciadas; ignorarlo exportaría lo que el usuario dejó fuera del solo.
        var anySolo = audioTracks.Any(t => t.IsSolo);
        var audible = new List<(AudioClip Clip, AudioTrack Track)>();
        foreach (var track in audioTracks)
        {
            if (!track.IsAudible(anySolo))
            {
                continue;
            }

            foreach (var audio in track.Clips)
            {
                if (!audio.IsMuted)
                {
                    audible.Add((audio, track));
                }
            }
        }

        var videoDuration = timeline.Duration;
        var duration = videoDuration;
        foreach (var (audio, _) in audible)
        {
            if (audio.TimelineEnd > duration)
            {
                duration = audio.TimelineEnd;
            }
        }

        // Si la música dura más que el video, este se extiende con negro. Sin ello el
        // archivo tendría el audio más largo que la imagen y muchos reproductores
        // congelan el último fotograma o cortan el sonido.
        var extra = duration - videoDuration;
        var padVideo = includeVideo && extra > TimeSpan.FromMilliseconds(40);
        var mix = audible.Count > 0;

        var videoBase = padVideo ? "[vbase]" : "[vout]";
        var audioBase = mix ? "[abase]" : "[aout]";

        if (includeVideo)
        {
            graph.Append(CultureInfo.InvariantCulture,
                $"{concatInputs}concat=n={timeline.Clips.Count}:v=1:a=1{videoBase}{audioBase}");
        }
        else
        {
            graph.Append(CultureInfo.InvariantCulture,
                $"{concatInputs}concat=n={timeline.Clips.Count}:v=0:a=1{audioBase}");
        }

        if (padVideo)
        {
            graph.Append(";\n");
            graph.Append(CultureInfo.InvariantCulture,
                $"[vbase]tpad=stop_mode=add:stop_duration={Seconds(extra)}:color=black[vout]");
        }

        if (mix)
        {
            var labels = new StringBuilder("[abase]");

            for (var n = 0; n < audible.Count; n++)
            {
                var (audio, track) = audible[n];

                inputs.AddRange([
                    "-ss", Seconds(audio.SourceIn),
                    "-t", Seconds(audio.Duration),
                    "-i", audio.Source.Path,
                ]);

                var input = inputIndex++;
                graph.Append(";\n");
                graph.Append(CultureInfo.InvariantCulture, $"[{input}:a]");
                graph.Append(CultureInfo.InvariantCulture,
                    $"aformat=sample_fmts=fltp:sample_rates={AudioSampleRate}:channel_layouts=stereo");

                var gain = audio.GainDb + track.GainDb;
                if (Math.Abs(gain) > 0.001)
                {
                    graph.Append(CultureInfo.InvariantCulture,
                        $",volume={gain.ToString("0.##", CultureInfo.InvariantCulture)}dB");
                }

                if (audio.FadeIn > TimeSpan.Zero)
                {
                    graph.Append(CultureInfo.InvariantCulture,
                        $",afade=t=in:st=0:d={Seconds(audio.FadeIn)}");
                }

                if (audio.FadeOut > TimeSpan.Zero)
                {
                    graph.Append(CultureInfo.InvariantCulture,
                        $",afade=t=out:st={Seconds(audio.Duration - audio.FadeOut)}:d={Seconds(audio.FadeOut)}");
                }

                // El desfase se aplica al final, con el audio ya recortado y con sus
                // fundidos: los fundidos se miden desde el inicio del propio clip, no
                // desde el de la secuencia.
                var delayMs = (long)Math.Round(audio.TimelineStart.TotalMilliseconds);
                if (delayMs > 0)
                {
                    graph.Append(CultureInfo.InvariantCulture, $",adelay=delays={delayMs}:all=1");
                }

                graph.Append(CultureInfo.InvariantCulture, $"[m{n}]");
                labels.Append(CultureInfo.InvariantCulture, $"[m{n}]");
            }

            graph.Append(";\n");

            // normalize=0: por defecto amix divide el volumen de cada entrada entre el
            // número de entradas, así que añadir una música haría sonar más bajo el
            // audio del video sin que nadie lo hubiera tocado.
            graph.Append(CultureInfo.InvariantCulture,
                $"{labels}amix=inputs={audible.Count + 1}:duration=longest:normalize=0[aout]");
        }

        return new FilterGraphPlan(
            inputs, graph.ToString(), includeVideo ? "[vout]" : string.Empty, "[aout]", duration);
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
