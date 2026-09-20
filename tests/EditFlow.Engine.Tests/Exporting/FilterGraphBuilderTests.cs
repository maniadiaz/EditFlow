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

        Assert.Contains("[v0][a0][v1][a1][v2][a2]concat=n=3:v=1:a=1[vout][aout]",
            plan.FilterGraph, StringComparison.Ordinal);
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
