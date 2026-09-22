// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Exporting;

public class FilterGraphBuilderTests
{
    private static MediaInfo Source(
        string path = "a.mp4",
        double seconds = 10,
        int width = 1920,
        int height = 1080,
        bool hasAudio = true,
        int rotation = 0) =>
        new(path, TimeSpan.FromSeconds(seconds), width, height, 30, "h264", hasAudio, rotation);

    private static ExportSettings Settings(VideoResolution? resolution = null) => new()
    {
        OutputPath = "out.mp4",
        Resolution = resolution ?? VideoResolution.P1080,
        EncoderName = "libx264",
        FrameRate = 30,
    };

    private static VideoTimeline Timeline(params Clip[] clips)
    {
        var timeline = new VideoTimeline();
        foreach (var clip in clips)
        {
            timeline.Append(clip);
        }

        return timeline;
    }

    [Fact]
    public void An_empty_timeline_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            FilterGraphBuilder.Build(new VideoTimeline(), Settings()));
    }

    [Fact]
    public void Each_clip_becomes_a_trimmed_input()
    {
        var clip = new Clip(Source(seconds: 30), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12));
        var plan = FilterGraphBuilder.Build(Timeline(clip), Settings());

        var arguments = string.Join(' ', plan.InputArguments);

        // '-ss' antes de '-i' es un salto rápido; '-t' limita cuánto se lee.
        Assert.Contains("-ss 5 -t 7 -i a.mp4", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sped_up_clip_still_reads_the_full_source_material()
    {
        // Se lee lo que hay que leer del archivo (7s), no lo que ocupa en la timeline (3,5s a
        // doble velocidad): 'setpts' es quien encoge eso después, no el recorte de entrada.
        var clip = new Clip(Source(seconds: 30), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12)) { Speed = 2 };
        var plan = FilterGraphBuilder.Build(Timeline(clip), Settings());

        var arguments = string.Join(' ', plan.InputArguments);
        Assert.Contains("-ss 5 -t 7 -i a.mp4", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sped_up_clip_gets_a_setpts_filter()
    {
        var clip = new Clip(Source()) { Speed = 2 };
        var plan = FilterGraphBuilder.Build(Timeline(clip), Settings());

        Assert.Contains("setpts=0.5*PTS", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_slowed_down_clip_gets_a_setpts_filter_that_stretches_it()
    {
        var clip = new Clip(Source()) { Speed = 0.5 };
        var plan = FilterGraphBuilder.Build(Timeline(clip), Settings());

        Assert.Contains("setpts=2*PTS", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_normal_speed_clip_gets_no_setpts_filter()
    {
        var plan = FilterGraphBuilder.Build(Timeline(new Clip(Source())), Settings());

        Assert.DoesNotContain("setpts=", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sped_up_clips_own_audio_is_stretched_with_atempo()
    {
        var clip = new Clip(Source()) { Speed = 2 };
        var plan = FilterGraphBuilder.Build(Timeline(clip), Settings());

        Assert.Contains(",atempo=2[a0]", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Extreme_speeds_chain_several_atempo_filters_within_range()
    {
        // atempo de FFmpeg solo admite un factor de 0,5 a 2 por instancia.
        var slow = new Clip(Source()) { Speed = Clip.MinimumSpeed };
        var slowPlan = FilterGraphBuilder.Build(Timeline(slow), Settings());
        foreach (var factor in ExtractAtempoFactors(slowPlan.FilterGraph))
        {
            Assert.InRange(factor, 0.5, 2.0);
        }

        var fast = new Clip(Source()) { Speed = Clip.MaximumSpeed };
        var fastPlan = FilterGraphBuilder.Build(Timeline(fast), Settings());
        foreach (var factor in ExtractAtempoFactors(fastPlan.FilterGraph))
        {
            Assert.InRange(factor, 0.5, 2.0);
        }
    }

    private static double[] ExtractAtempoFactors(string graph) =>
        System.Text.RegularExpressions.Regex.Matches(graph, @"atempo=([\d.]+)")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

    [Fact]
    public void Silence_synthesized_for_a_muted_clip_is_not_stretched()
    {
        // El silencio ya se genera con la duración que toca en la timeline: no hay nada que
        // 'atempo' tenga que estirar, y el archivo real de audio ni se lee.
        var clip = new Clip(Source(hasAudio: false)) { Speed = 2 };
        var plan = FilterGraphBuilder.Build(Timeline(clip), Settings());

        Assert.DoesNotContain("atempo=", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void The_composed_duration_reflects_every_clips_speed()
    {
        var a = new Clip(Source("a.mp4", seconds: 10)) { Speed = 2 };   // 5s
        var b = new Clip(Source("b.mp4", seconds: 10)) { Speed = 0.5 }; // 20s
        var plan = FilterGraphBuilder.Build(Timeline(a, b), Settings());

        Assert.Equal(TimeSpan.FromSeconds(25), plan.Duration);
    }

    [Fact]
    public void Normalisation_is_applied_to_every_branch_not_once_at_the_end()
    {
        // concat exige que todas sus entradas coincidan en resolución, fps y SAR.
        // Normalizar después de concatenar llegaría tarde.
        var plan = FilterGraphBuilder.Build(
            Timeline(new Clip(Source("a.mp4")), new Clip(Source("b.mp4"))),
            Settings());

        var scaleCount = CountOccurrences(plan.FilterGraph, "scale=1920:1080");
        var sarCount = CountOccurrences(plan.FilterGraph, "setsar=1");
        var fpsCount = CountOccurrences(plan.FilterGraph, "fps=30");

        Assert.Equal(2, scaleCount);
        Assert.Equal(2, sarCount);
        Assert.Equal(2, fpsCount);
    }

    [Fact]
    public void Aspect_ratio_is_preserved_with_padding_rather_than_stretched()
    {
        var plan = FilterGraphBuilder.Build(Timeline(new Clip(Source())), Settings());

        Assert.Contains("force_original_aspect_ratio=decrease", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("pad=1920:1080:(ow-iw)/2:(oh-ih)/2", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clip_without_audio_gets_synthetic_silence()
    {
        // Sin esto, concat falla con un error que no menciona el audio por ninguna parte.
        var plan = FilterGraphBuilder.Build(
            Timeline(new Clip(Source("silent.mp4", hasAudio: false))),
            Settings());

        var arguments = string.Join(' ', plan.InputArguments);

        Assert.Contains("anullsrc=r=48000:cl=stereo", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Silence_inputs_do_not_shift_the_indices_of_later_clips()
    {
        // La entrada sintética ocupa un índice propio. Si no se contabiliza, el segundo
        // clip leería el flujo equivocado y la exportación saldría descuadrada.
        var plan = FilterGraphBuilder.Build(
            Timeline(
                new Clip(Source("silent.mp4", hasAudio: false)),
                new Clip(Source("withaudio.mp4"))),
            Settings());

        // Entrada 0: el video mudo. Entrada 1: el silencio sintético. Entrada 2: el segundo clip.
        Assert.Contains("[0:v]", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("[1:a]", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("[2:v]", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("[2:a]", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Rotated_clips_are_not_rotated_again_by_the_graph()
    {
        // FFmpeg rota al decodificar (autorotate está activo por defecto), así que el
        // grafo ya recibe el fotograma bien orientado. Verificado con un archivo 640x360
        // marcado a 90 grados: el grafo lo recibe como 360x640. Aplicar transpose aquí
        // lo dejaría tumbado.
        var plan = FilterGraphBuilder.Build(
            Timeline(new Clip(Source("phone.mp4", rotation: 90))),
            Settings());

        Assert.DoesNotContain("transpose", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Clips_are_concatenated_with_matching_stream_counts()
    {
        var plan = FilterGraphBuilder.Build(
            Timeline(new Clip(Source("a.mp4")), new Clip(Source("b.mp4")), new Clip(Source("c.mp4"))),
            Settings());

        // Sin transiciones, cada corte es seco: se encadenan de dos en dos en vez de un
        // 'concat' plano de N, para poder mezclar 'xfade'/'acrossfade' en los cortes que sí
        // pidan transición sin cambiar de mecanismo a mitad del grafo.
        Assert.Contains("[v0][a0][v1][a1]concat=n=2:v=1:a=1[vc1][ac1]",
            plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("[vc1][ac1][v2][a2]concat=n=2:v=1:a=1[vout][aout]",
            plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clip_with_a_transition_crossfades_instead_of_concatenating()
    {
        var incoming = new Clip(Source("b.mp4"))
        {
            TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1)),
        };
        var plan = FilterGraphBuilder.Build(Timeline(new Clip(Source("a.mp4")), incoming), Settings());

        // Clips de 10s cada uno: el solape de 1s empieza en el segundo 9 del primero.
        Assert.Contains("[v0][v1]xfade=transition=fade:duration=1:offset=9[vout]",
            plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("[a0][a1]acrossfade=d=1[aout]", plan.FilterGraph, StringComparison.Ordinal);
        Assert.DoesNotContain("concat=", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void The_transition_kind_selects_the_matching_xfade_transition()
    {
        var incoming = new Clip(Source("b.mp4"))
        {
            TransitionIn = new Transition(TransitionKind.WipeLeft, TimeSpan.FromSeconds(1)),
        };
        var plan = FilterGraphBuilder.Build(Timeline(new Clip(Source("a.mp4")), incoming), Settings());

        Assert.Contains("xfade=transition=wipeleft:", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void The_composed_duration_shrinks_by_the_overlap()
    {
        var incoming = new Clip(Source("b.mp4"))
        {
            TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1)),
        };
        var plan = FilterGraphBuilder.Build(Timeline(new Clip(Source("a.mp4")), incoming), Settings());

        Assert.Equal(TimeSpan.FromSeconds(19), plan.Duration);
    }

    [Fact]
    public void A_transition_too_long_for_the_clips_is_capped_to_what_they_can_lend()
    {
        // El clip entrante pide 5s de solape, pero ninguno de los dos tiene más de 2s.
        var incoming = new Clip(Source("b.mp4", seconds: 2))
        {
            TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(5)),
        };
        var plan = FilterGraphBuilder.Build(
            Timeline(new Clip(Source("a.mp4", seconds: 2)), incoming), Settings());

        Assert.Contains("duration=2:offset=0", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(2), plan.Duration);
    }

    [Fact]
    public void A_transition_only_applies_at_the_boundary_that_asked_for_it()
    {
        // A -> B corte seco, B -> C disolvencia: solo el segundo par debe fundirse.
        var b = new Clip(Source("b.mp4"));
        var c = new Clip(Source("c.mp4"))
        {
            TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1)),
        };
        var plan = FilterGraphBuilder.Build(Timeline(new Clip(Source("a.mp4")), b, c), Settings());

        Assert.Contains("[v0][a0][v1][a1]concat=n=2:v=1:a=1[vc1][ac1]", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("[vc1][v2]xfade=transition=fade:duration=1:offset=19[vout]",
            plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("[ac1][a2]acrossfade=d=1[aout]", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zoomed_clip_gets_the_transform_filter_after_padding_to_the_canvas()
    {
        var clip = new Clip(Source()) { Transform = new ClipTransform(2, 0, 0, 0) };
        var plan = FilterGraphBuilder.Build(Timeline(clip), Settings());

        Assert.Contains(
            "pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=black,scale=3840:2160,crop=1920:1080:960:540,setsar=1",
            plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clip_with_the_normal_framing_gets_no_transform_filter()
    {
        var plan = FilterGraphBuilder.Build(Timeline(new Clip(Source())), Settings());

        Assert.DoesNotContain("crop=", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_labels_match_what_the_graph_produces()
    {
        var plan = FilterGraphBuilder.Build(Timeline(new Clip(Source())), Settings());

        Assert.Equal("[vout]", plan.VideoLabel);
        Assert.Equal("[aout]", plan.AudioLabel);
        Assert.Contains(plan.VideoLabel, plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains(plan.AudioLabel, plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Durations_are_formatted_independently_of_the_system_locale()
    {
        // En una máquina en español, un formateo descuidado escribiría "1,5" y FFmpeg
        // leería 1 segundo, descartando los decimales sin avisar.
        var clip = new Clip(Source(seconds: 10), TimeSpan.Zero, TimeSpan.FromSeconds(1.5));
        var plan = FilterGraphBuilder.Build(Timeline(clip), Settings());

        var arguments = string.Join(' ', plan.InputArguments);

        Assert.Contains("-t 1.5", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("1,5", arguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(854, 480)]
    [InlineData(1280, 720)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void The_canvas_matches_the_requested_resolution(int width, int height)
    {
        var resolution = VideoResolution.Presets.Single(r => r.Width == width && r.Height == height);
        var plan = FilterGraphBuilder.Build(Timeline(new Clip(Source())), Settings(resolution));

        Assert.Contains($"scale={width}:{height}", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains($"pad={width}:{height}", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_timeline_moves_the_graph_into_a_script_file()
    {
        // Windows corta la línea de comandos en unos 32 000 caracteres, y el fallo no
        // menciona la longitud: FFmpeg recibe argumentos truncados sin más.
        var timeline = new VideoTimeline();
        for (var i = 0; i < 200; i++)
        {
            timeline.Append(new Clip(Source($"clip-with-a-reasonably-long-name-{i}.mp4")));
        }

        using var command = ExportCommandBuilder.Build(timeline, Settings());

        Assert.True(command.FilterGraph.Length > ExportCommandBuilder.InlineGraphLimit);
        Assert.True(command.UsesScriptFile);
        Assert.Contains("-filter_complex_script", command.Arguments);
        Assert.DoesNotContain("-filter_complex", command.Arguments);
    }

    [Fact]
    public void A_short_timeline_keeps_the_graph_inline()
    {
        using var command = ExportCommandBuilder.Build(Timeline(new Clip(Source())), Settings());

        Assert.False(command.UsesScriptFile);
        Assert.Contains("-filter_complex", command.Arguments);
    }

    [Fact]
    public void The_script_file_is_removed_when_the_command_is_disposed()
    {
        var timeline = new VideoTimeline();
        for (var i = 0; i < 200; i++)
        {
            timeline.Append(new Clip(Source($"clip-with-a-reasonably-long-name-{i}.mp4")));
        }

        var command = ExportCommandBuilder.Build(timeline, Settings());
        var scriptPath = command.Arguments[command.Arguments.ToList().IndexOf("-filter_complex_script") + 1];

        Assert.True(File.Exists(scriptPath));
        command.Dispose();
        Assert.False(File.Exists(scriptPath));
    }

    [Fact]
    public void Progress_reporting_is_requested_on_stdout()
    {
        using var command = ExportCommandBuilder.Build(Timeline(new Clip(Source())), Settings());

        Assert.Contains("-progress", command.Arguments);
        Assert.Contains("pipe:1", command.Arguments);
        Assert.Contains("-nostats", command.Arguments);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
