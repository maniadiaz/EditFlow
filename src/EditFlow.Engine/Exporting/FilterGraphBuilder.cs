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

    /// <summary>Construye el plan para una secuencia completa: video, pistas de audio y superposiciones.</summary>
    /// <param name="sequence">Montaje.</param>
    /// <param name="settings">Ajustes de exportación.</param>
    /// <param name="overlayAssets">
    /// Imagen ya dibujada de cada elemento superpuesto, por identidad. Sin ella, las superposiciones no se componen.
    /// </param>
    /// <exception cref="ArgumentException">Si no hay ningún clip de video.</exception>
    public static FilterGraphPlan Build(
        EditSequence sequence,
        ExportSettings settings,
        IReadOnlyDictionary<Guid, string>? overlayAssets = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return BuildCore(sequence.Video, sequence.AudioTracks, settings, includeVideo: true, sequence.OverlayTracks, overlayAssets);
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
        return BuildCore(sequence.Video, sequence.AudioTracks, settings: null, includeVideo: false, sequence.OverlayTracks, null);
    }

    private static FilterGraphPlan BuildCore(
        VideoTimeline timeline,
        IReadOnlyList<AudioTrack> audioTracks,
        ExportSettings? settings,
        bool includeVideo = true,
        IReadOnlyList<OverlayTrack>? overlayTracks = null,
        IReadOnlyDictionary<Guid, string>? overlayAssets = null)
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

        var width = settings?.Resolution.Width ?? 0;
        var height = settings?.Resolution.Height ?? 0;
        var inputIndex = 0;

        for (var i = 0; i < timeline.Clips.Count; i++)
        {
            var clip = timeline.Clips[i];

            // '-ss' antes de '-i' hace un salto rápido por índice en lugar de decodificar
            // desde el principio. FFmpeg lo refina hasta el fotograma exacto por su cuenta.
            var videoInput = -1;
            if (clip.IsGap)
            {
                // Un hueco es negro sin archivo detrás: se genera, y solo si hace falta la imagen.
                if (includeVideo)
                {
                    inputs.AddRange([
                        "-f", "lavfi",
                        "-t", Seconds(clip.Duration),
                        "-i", string.Create(CultureInfo.InvariantCulture,
                            $"color=c=black:s={width}x{height}:r={Rate(settings!.FrameRate)}"),
                    ]);

                    videoInput = inputIndex++;
                }
            }
            else if (includeVideo || clip.HasOwnAudio)
            {
                // Se lee lo que el clip usa del archivo (SourceDuration), no lo que ocupa en la
                // timeline (Duration): a una velocidad distinta de 1 no son lo mismo, y 'setpts'/
                // 'atempo' son los que estiran ese material para que ocupe su sitio.
                inputs.AddRange([
                    "-ss", Seconds(clip.SourceIn),
                    "-t", Seconds(clip.SourceDuration),
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
            var sped = !clip.IsGap && Math.Abs(clip.Speed - 1) > 0.0001;

            if (includeVideo)
            {
                graph.Append(CultureInfo.InvariantCulture, $"[{videoInput}:v]");

                // 'setpts' reescala las marcas de tiempo: a la mitad se ve el doble de rápido,
                // al doble a cámara lenta. Va antes que el resto porque no depende del tamaño
                // ni del formato, y así el resto de la rama no necesita saber si hay velocidad.
                if (sped)
                {
                    graph.Append(CultureInfo.InvariantCulture,
                        $"setpts={(1 / clip.Speed).ToString("0.######", CultureInfo.InvariantCulture)}*PTS,");
                }

                graph.Append(CultureInfo.InvariantCulture, $"fps={Rate(settings!.FrameRate)},");
                graph.Append(CultureInfo.InvariantCulture,
                    $"scale={width}:{height}:force_original_aspect_ratio=decrease,");
                graph.Append(CultureInfo.InvariantCulture,
                    $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,");

                // El encuadre trabaja sobre el fotograma ya normalizado al lienzo (width×height):
                // el resultado mide lo mismo, así que no le importa a nadie que venga después.
                if (TransformFilter.Build(clip.Transform, width, height) is { } clipTransform)
                {
                    graph.Append(clipTransform).Append(',');
                }

                graph.Append("setsar=1,format=yuv420p");

                if (ColorFilter.Build(clip.Color) is { } clipColor)
                {
                    graph.Append(',').Append(clipColor);
                }

                if (VisualFilterCatalog.Build(clip.Filter) is { } visualFilter)
                {
                    graph.Append(',').Append(visualFilter);
                }

                if (VisualEffectCatalog.Build(clip.Effect) is { } visualEffect)
                {
                    graph.Append(',').Append(visualEffect);
                }

                if (!clip.IsGap && FadeFilter.BuildVideo(clip.FadeIn, clip.FadeOut, clip.Duration) is { } videoFade)
                {
                    graph.Append(',').Append(videoFade);
                }

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

            if (clip.HasOwnAudio && AudioEffectCatalog.Build(clip.AudioEffect) is { } clipAudioEffect)
            {
                graph.Append(',').Append(clipAudioEffect);
            }

            if (clip.HasOwnAudio && AudioEffectCatalog.BuildPan(clip.Pan) is { } clipPan)
            {
                graph.Append(',').Append(clipPan);
            }

            // El silencio sintético ya se generó con la duración que toca en la timeline: no
            // hay nada que estirar. Solo el audio de verdad necesita 'atempo'.
            if (sped && clip.HasOwnAudio)
            {
                foreach (var factor in AtempoFactors(clip.Speed))
                {
                    graph.Append(CultureInfo.InvariantCulture,
                        $",atempo={factor.ToString("0.######", CultureInfo.InvariantCulture)}");
                }
            }

            // El fundido de un clip funde a la vez su imagen y su propio sonido: una música que
            // sonara de golpe justo cuando la imagen aparece despacio desentonaría. El silencio
            // sintético no necesita fundirse con nada.
            if (clip.HasOwnAudio && FadeFilter.BuildAudio(clip.FadeIn, clip.FadeOut, clip.Duration) is { } audioFade)
            {
                graph.Append(',').Append(audioFade);
            }

            graph.Append(CultureInfo.InvariantCulture, $"[a{i}];");
            graph.Append('\n');
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

        // Un título que dura más que el video alarga el montaje, igual que una música. Se
        // decide por lo que se dibujaría, no por si ya hay imagen preparada: así el plan solo
        // de audio y el de video acaban con la misma duración.
        var overlays = CollectOverlays(overlayTracks ?? []);
        foreach (var overlay in overlays)
        {
            if (overlay.End > duration)
            {
                duration = overlay.End;
            }
        }

        // Si la música dura más que el video, este se extiende con negro. Sin ello el
        // archivo tendría el audio más largo que la imagen y muchos reproductores
        // congelan el último fotograma o cortan el sonido.
        var extra = duration - videoDuration;
        var padVideo = includeVideo && extra > TimeSpan.FromMilliseconds(40);

        // El sonido de los videos superpuestos entra en la mezcla como una pista más.
        var videoAudio = overlays.Where(o => o.Kind == OverlayKind.Video && o.PlaysAudio).ToList();
        var mix = audible.Count > 0 || videoAudio.Count > 0;

        var composite = includeVideo && overlays.Any(o => o.Kind == OverlayKind.Video
            || (overlayAssets is not null && overlayAssets.ContainsKey(o.Id)));
        var videoFinal = composite ? "[vstack]" : "[vout]";
        var videoBase = padVideo ? "[vbase]" : videoFinal;
        var audioBase = mix ? "[abase]" : "[aout]";

        AppendClipChain(graph, timeline, includeVideo, videoBase, audioBase);

        if (padVideo)
        {
            graph.Append(";\n");
            graph.Append(CultureInfo.InvariantCulture,
                $"[vbase]tpad=stop_mode=add:stop_duration={Seconds(extra)}:color=black{videoFinal}");
        }

        if (composite)
        {
            AppendOverlays(graph, inputs, ref inputIndex, overlays, overlayAssets!, settings!, width, height, duration);
        }

        if (mix)
        {
            var labels = new StringBuilder("[abase]");

            // Pistas de audio y videos superpuestos, con lo que la mezcla necesita de cada uno.
            var sources = new List<(string Path, TimeSpan SourceIn, TimeSpan Duration, double Gain, TimeSpan FadeIn, TimeSpan FadeOut, TimeSpan Start, AudioEffectKind Effect, double Pan)>();
            foreach (var (audio, track) in audible)
            {
                sources.Add((audio.Source.Path, audio.SourceIn, audio.Duration, audio.GainDb + track.GainDb,
                    audio.FadeIn, audio.FadeOut, audio.TimelineStart, audio.Effect, audio.Pan));
            }

            foreach (var overlay in videoAudio)
            {
                // Lo que se ve del video sobre el principal; si la timeline lo corta, el sonido también.
                // Un video en una capa no tiene efecto de sonido ni balance propios, igual que
                // tampoco tiene fundidos: son ajustes reservados a un clip de audio de verdad.
                var length = overlay.End > duration ? duration - overlay.Start : overlay.Duration;
                sources.Add((overlay.Media!.Path, overlay.SourceIn, length, overlay.AudioGainDb,
                    TimeSpan.Zero, TimeSpan.Zero, overlay.Start, AudioEffectKind.None, 0));
            }

            for (var n = 0; n < sources.Count; n++)
            {
                var source = sources[n];

                inputs.AddRange([
                    "-ss", Seconds(source.SourceIn),
                    "-t", Seconds(source.Duration),
                    "-i", source.Path,
                ]);

                var input = inputIndex++;
                graph.Append(";\n");
                graph.Append(CultureInfo.InvariantCulture, $"[{input}:a]");
                graph.Append(CultureInfo.InvariantCulture,
                    $"aformat=sample_fmts=fltp:sample_rates={AudioSampleRate}:channel_layouts=stereo");

                var gain = source.Gain;
                if (Math.Abs(gain) > 0.001)
                {
                    graph.Append(CultureInfo.InvariantCulture,
                        $",volume={gain.ToString("0.##", CultureInfo.InvariantCulture)}dB");
                }

                if (AudioEffectCatalog.Build(source.Effect) is { } sourceEffect)
                {
                    graph.Append(',').Append(sourceEffect);
                }

                if (AudioEffectCatalog.BuildPan(source.Pan) is { } sourcePan)
                {
                    graph.Append(',').Append(sourcePan);
                }

                if (source.FadeIn > TimeSpan.Zero)
                {
                    graph.Append(CultureInfo.InvariantCulture,
                        $",afade=t=in:st=0:d={Seconds(source.FadeIn)}");
                }

                if (source.FadeOut > TimeSpan.Zero)
                {
                    graph.Append(CultureInfo.InvariantCulture,
                        $",afade=t=out:st={Seconds(source.Duration - source.FadeOut)}:d={Seconds(source.FadeOut)}");
                }

                // El desfase se aplica al final, con el audio ya recortado y con sus
                // fundidos: los fundidos se miden desde el inicio del propio clip, no
                // desde el de la secuencia.
                var delayMs = (long)Math.Round(source.Start.TotalMilliseconds);
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
                $"{labels}amix=inputs={sources.Count + 1}:duration=longest:normalize=0[aout]");
        }

        return new FilterGraphPlan(
            inputs, graph.ToString(), includeVideo ? "[vout]" : string.Empty, "[aout]", duration);
    }

    /// <summary>
    /// Encadena la rama de cada clip en la timeline compuesta final: con un corte seco entre dos
    /// clips consecutivos, o solapándolos con <c>xfade</c>/<c>acrossfade</c> cuando el clip
    /// entrante pide una transición.
    /// </summary>
    /// <remarks>
    /// Es el mismo cálculo de solape que <see cref="VideoTimeline.Layout"/> —vía
    /// <see cref="TransitionMath.Overlap"/>—, así que la imagen exportada dura exactamente lo
    /// que la timeline dice que dura; calcularlo dos veces por separado habría sido la forma
    /// más segura de que un día discreparan.
    /// </remarks>
    private static void AppendClipChain(
        StringBuilder graph, VideoTimeline timeline, bool includeVideo, string videoOut, string audioOut)
    {
        var count = timeline.Clips.Count;

        if (count == 1)
        {
            // Un único clip no tiene nada que fundir ni concatenar: se renombra su propia
            // rama a las etiquetas finales que el resto del grafo espera.
            if (includeVideo)
            {
                graph.Append(CultureInfo.InvariantCulture, $"[v0][a0]concat=n=1:v=1:a=1{videoOut}{audioOut};\n");
            }
            else
            {
                graph.Append(CultureInfo.InvariantCulture, $"[a0]concat=n=1:v=0:a=1{audioOut};\n");
            }
        }
        else
        {
            var runningVideo = "[v0]";
            var runningAudio = "[a0]";
            var runningDuration = timeline.Clips[0].Duration;

            for (var i = 1; i < count; i++)
            {
                var clip = timeline.Clips[i];
                var overlap = TransitionMath.Overlap(timeline.Clips[i - 1], clip);
                var isLast = i == count - 1;
                var stepVideo = isLast ? videoOut : $"[vc{i}]";
                var stepAudio = isLast ? audioOut : $"[ac{i}]";

                if (overlap > TimeSpan.Zero)
                {
                    var name = XfadeName(clip.TransitionIn.Kind);
                    var duration = Seconds(overlap);
                    var offset = Seconds(runningDuration - overlap);

                    if (includeVideo)
                    {
                        graph.Append(CultureInfo.InvariantCulture,
                            $"{runningVideo}[v{i}]xfade=transition={name}:duration={duration}:offset={offset}{stepVideo};\n");
                    }

                    graph.Append(CultureInfo.InvariantCulture,
                        $"{runningAudio}[a{i}]acrossfade=d={duration}{stepAudio};\n");

                    runningDuration = runningDuration + clip.Duration - overlap;
                }
                else
                {
                    if (includeVideo)
                    {
                        graph.Append(CultureInfo.InvariantCulture,
                            $"{runningVideo}{runningAudio}[v{i}][a{i}]concat=n=2:v=1:a=1{stepVideo}{stepAudio};\n");
                    }
                    else
                    {
                        graph.Append(CultureInfo.InvariantCulture,
                            $"{runningAudio}[a{i}]concat=n=2:v=0:a=1{stepAudio};\n");
                    }

                    runningDuration += clip.Duration;
                }

                runningVideo = stepVideo;
                runningAudio = stepAudio;
            }
        }

        // Lo que sigue (relleno de audio, composición de capas, mezcla) antepone su propio
        // separador a la siguiente sentencia: se retira el que acabamos de dejar colgando,
        // igual que dejaba pendiente el 'concat' plano al que sustituye esta cadena.
        if (graph.Length >= 2 && graph[^1] == '\n' && graph[^2] == ';')
        {
            graph.Length -= 2;
        }
    }

    /// <summary>Nombre que entiende el filtro <c>xfade</c> de FFmpeg para cada tipo de transición.</summary>
    private static string XfadeName(TransitionKind kind) => kind switch
    {
        TransitionKind.Dissolve => "fade",
        TransitionKind.FadeToBlack => "fadeblack",
        TransitionKind.FadeToWhite => "fadewhite",
        TransitionKind.WipeLeft => "wipeleft",
        TransitionKind.WipeRight => "wiperight",
        TransitionKind.SlideLeft => "slideleft",
        TransitionKind.SlideRight => "slideright",
        TransitionKind.CircleOpen => "circleopen",
        _ => "fade",
    };

    /// <summary>Elementos que se dibujarían: de capas visibles y con algo que mostrar, de abajo arriba.</summary>
    private static List<OverlayItem> CollectOverlays(IReadOnlyList<OverlayTrack> tracks)
    {
        var items = new List<OverlayItem>();

        // La primera capa es la de delante: se recorre desde la última para componer de abajo arriba.
        for (var t = tracks.Count - 1; t >= 0; t--)
        {
            if (tracks[t].IsHidden)
            {
                continue;
            }

            foreach (var item in tracks[t].Items)
            {
                var drawable = item.Kind switch
                {
                    OverlayKind.Text => !string.IsNullOrWhiteSpace(item.Text?.Content),
                    OverlayKind.Video => item.Media is not null,
                    _ => !string.IsNullOrWhiteSpace(item.ImagePath),
                };

                if (drawable)
                {
                    items.Add(item);
                }
            }
        }

        return items;
    }

    /// <summary>Encadena un <c>overlay</c> por cada elemento sobre el video ya montado.</summary>
    private static void AppendOverlays(
        StringBuilder graph,
        List<string> inputs,
        ref int inputIndex,
        List<OverlayItem> overlays,
        IReadOnlyDictionary<Guid, string> assets,
        ExportSettings settings,
        int width,
        int height,
        TimeSpan duration)
    {
        var current = "[vstack]";
        var drawn = overlays.Where(o => (o.Kind == OverlayKind.Video || assets.ContainsKey(o.Id)) && o.Start < duration).ToList();

        for (var n = 0; n < drawn.Count; n++)
        {
            var item = drawn[n];
            var visibleFor = item.End > duration ? duration - item.Start : item.Duration;

            if (item.Kind == OverlayKind.Video)
            {
                // Un video superpuesto se lee del archivo, desde el punto donde empieza lo que se ve.
                inputs.AddRange([
                    "-ss", Seconds(item.SourceIn),
                    "-t", Seconds(visibleFor),
                    "-i", item.Media!.Path,
                ]);
            }
            else
            {
                // Un PNG suelto es un solo fotograma: '-loop 1' lo repite durante el tiempo que
                // se ve, a la cadencia del video, y '-t' lo corta ahí.
                inputs.AddRange([
                    "-loop", "1",
                    "-framerate", Rate(settings.FrameRate),
                    "-t", Seconds(visibleFor),
                    "-i", assets[item.Id],
                ]);
            }

            var input = inputIndex++;
            var transform = item.Transform;

            graph.Append(";\n");

            if (item.Kind == OverlayKind.Video)
            {
                // Se reduce antes de pasar a RGBA: convertir un 4K entero a RGBA para luego encogerlo
                // sería mucho más trabajo del necesario.
                var videoPixels = Math.Max(2, (int)Math.Round(width * transform.Width) / 2 * 2);
                graph.Append(CultureInfo.InvariantCulture,
                    $"[{input}:v]fps={Rate(settings.FrameRate)},scale={videoPixels}:-2");

                // El color se aplica antes de pasar a RGBA: los filtros trabajan en YUV.
                if (ColorFilter.Build(item.Color) is { } overlayColor)
                {
                    graph.Append(",format=yuv420p,").Append(overlayColor);
                }

                // El recorte del fondo trae sus propias conversiones de formato y acaba en RGBA,
                // así que ocupa el sitio de la conversión suelta. Va antes de la opacidad de la
                // capa: lo recortado queda transparente del todo y lo que se conserva obedece a
                // la opacidad.
                graph.Append(',').Append(ChromaKeyFilter.Build(item.ChromaKey) ?? "format=rgba");
            }
            else
            {
                graph.Append(CultureInfo.InvariantCulture, $"[{input}:v]format=rgba");
            }

            if (item.Kind == OverlayKind.Image)
            {
                // El ancho se da como fracción del video; el alto sale de la proporción de la imagen.
                var pixels = Math.Max(2, (int)Math.Round(width * transform.Width));
                graph.Append(CultureInfo.InvariantCulture, $",scale={pixels}:-1");
            }

            if (transform.Opacity < 0.999)
            {
                graph.Append(CultureInfo.InvariantCulture,
                    $",colorchannelmixer=aa={transform.Opacity.ToString("0.###", CultureInfo.InvariantCulture)}");
            }

            var overlayFade = FadeFilter.BuildAlpha(item.FadeIn, item.FadeOut, visibleFor);

            if (overlayFade is not null)
            {
                // Se pone a cero antes del fundido para poder escribirlo en el mismo tiempo local
                // (0 a 'visibleFor') que usa el resto de este elemento: un video superpuesto no
                // llega necesariamente con marca de tiempo exacta 0 como sí lo hace el fotograma
                // único de un texto o una imagen, y sin este primer reajuste el fundido caería en
                // el instante equivocado.
                graph.Append(",setpts=PTS-STARTPTS,").Append(overlayFade);
            }

            // Se desplaza al instante en que el elemento debe aparecer en la timeline, y 'enable'
            // lo limita a ese tramo. Si ya se puso a cero arriba, este segundo ajuste parte de ahí
            // en vez de restar otra vez 'STARTPTS', que ya no pinta nada tras el primero.
            var ptsBase = overlayFade is null ? "PTS-STARTPTS" : "PTS";
            graph.Append(CultureInfo.InvariantCulture, $",setpts={ptsBase}+{Seconds(item.Start)}/TB[ov{n}];\n");

            var next = n == drawn.Count - 1 ? "[vout]" : $"[vs{n}]";
            graph.Append(CultureInfo.InvariantCulture,
                $"{current}[ov{n}]overlay=" +
                $"x=main_w*{Rate(transform.CenterX)}-overlay_w/2:" +
                $"y=main_h*{Rate(transform.CenterY)}-overlay_h/2:" +
                $"enable='between(t,{Seconds(item.Start)},{Seconds(item.Start + visibleFor)})':" +
                $"eof_action=pass{next}");

            current = next;
        }
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

    /// <summary>
    /// Descompone un cambio de velocidad en los factores que hacen falta encadenar en <c>atempo</c>.
    /// </summary>
    /// <remarks>
    /// El filtro <c>atempo</c> de FFmpeg solo admite un factor entre 0,5 y 2 por instancia; fuera
    /// de ese rango hay que encadenar varias. Se van sacando mitades o dobles hasta que lo que
    /// queda cae dentro del rango, lo que cubre de sobra el 0,1–16 que admite <see cref="Clip.Speed"/>.
    /// </remarks>
    private static IEnumerable<double> AtempoFactors(double speed)
    {
        var remaining = speed;

        while (remaining < 0.5)
        {
            yield return 0.5;
            remaining /= 0.5;
        }

        while (remaining > 2.0)
        {
            yield return 2.0;
            remaining /= 2.0;
        }

        yield return remaining;
    }
}
