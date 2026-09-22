// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Exporting;

public class AudioOnlyGraphTests
{
    private static MediaInfo Media(string name, double seconds, bool audio = true) =>
        new(name, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", audio);

    private static AudioClip Music(double start, double seconds, string name = "musica.mp3") =>
        new(Media(name, 300), TimeSpan.Zero, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(start));

    private static EditSequence WithVideo(double seconds = 10)
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("video.mp4", seconds)));
        return sequence;
    }

    [Fact]
    public void The_audio_graph_has_no_video_branch()
    {
        var plan = FilterGraphBuilder.BuildAudioOnly(WithVideo());

        Assert.DoesNotContain("[v0]", plan.FilterGraph, StringComparison.Ordinal);
        Assert.DoesNotContain("scale=", plan.FilterGraph, StringComparison.Ordinal);
        Assert.DoesNotContain("pad=", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("concat=n=1:v=0:a=1", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Equal(string.Empty, plan.VideoLabel);
        Assert.Equal("[aout]", plan.AudioLabel);
    }

    [Fact]
    public void A_clip_that_contributes_no_sound_does_not_open_its_video_file()
    {
        // No hace falta leer un 4K entero para producir silencio. Sin el video, la mezcla
        // de un montaje largo tarda una fracción de lo que costaría abrirlos todos.
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("mudo.mp4", 5, audio: false)));
        sequence.Video.Append(new Clip(Media("suena.mp4", 5)));

        var plan = FilterGraphBuilder.BuildAudioOnly(sequence);
        var inputs = string.Join(' ', plan.InputArguments);

        Assert.DoesNotContain("mudo.mp4", inputs, StringComparison.Ordinal);
        Assert.Contains("suena.mp4", inputs, StringComparison.Ordinal);
        Assert.Contains("anullsrc", inputs, StringComparison.Ordinal);
    }

    [Fact]
    public void A_detached_clip_does_not_open_its_video_file_either()
    {
        var sequence = WithVideo();
        new DetachAudioCommand(sequence, sequence.Video.Clips[0]).Execute();

        var plan = FilterGraphBuilder.BuildAudioOnly(sequence);

        // Solo entra el audio separado desde su pista; el archivo del clip de video no se
        // abre para su propio sonido, que ya no aporta.
        var videoOpenings = plan.InputArguments
            .Where((a, i) => i > 0 && plan.InputArguments[i - 1] == "-i" && a == "video.mp4")
            .Count();

        // Una vez, como fuente del clip de audio separado; nunca dos.
        Assert.Equal(1, videoOpenings);
    }

    [Fact]
    public void Volume_fades_and_delay_match_the_export()
    {
        // Comparten grafo a propósito: lo que se oye en el preview debe ser lo exportado.
        var sequence = WithVideo(20);
        var clip = Music(start: 4, seconds: 8);
        clip.GainDb = -6;
        clip.FadeIn = TimeSpan.FromSeconds(1);
        clip.FadeOut = TimeSpan.FromSeconds(2);
        sequence.AddAudioTrack().TryAdd(clip);

        var plan = FilterGraphBuilder.BuildAudioOnly(sequence);

        Assert.Contains("volume=-6dB", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("afade=t=in:st=0:d=1", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("afade=t=out:st=6:d=2", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("adelay=delays=4000:all=1", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("amix=inputs=2", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void The_video_export_graph_is_unaffected()
    {
        // Añadir el modo de solo audio no debe alterar la exportación de siempre.
        var sequence = WithVideo();
        var settings = new ExportSettings
        {
            OutputPath = "o.mp4",
            Resolution = VideoResolution.P1080,
            EncoderName = "libx264",
        };

        var plan = FilterGraphBuilder.Build(sequence, settings);

        Assert.Contains("concat=n=1:v=1:a=1[vout][aout]", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Equal("[vout]", plan.VideoLabel);
    }

    [Fact]
    public void The_mix_lasts_as_long_as_the_longest_audible_thing()
    {
        var sequence = WithVideo(4);
        sequence.AddAudioTrack().TryAdd(Music(0, 9));

        var plan = FilterGraphBuilder.BuildAudioOnly(sequence);

        Assert.Equal(TimeSpan.FromSeconds(9), plan.Duration);
    }

    [Fact]
    public void Audio_indices_stay_consistent_when_some_video_files_are_skipped()
    {
        // Al saltarse el archivo de un clip sin sonido, los índices de entrada se desplazan.
        // Si el grafo siguiera usando los del caso completo, leería un flujo equivocado.
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("a.mp4", 5, audio: false)));
        sequence.Video.Append(new Clip(Media("b.mp4", 5)));
        sequence.AddAudioTrack().TryAdd(Music(0, 3));

        var plan = FilterGraphBuilder.BuildAudioOnly(sequence);

        // Entradas: 0 silencio del primer clip, 1 archivo b.mp4, 2 la música.
        Assert.Contains("[0:a]", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("[1:a]", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("[2:a]", plan.FilterGraph, StringComparison.Ordinal);
        Assert.DoesNotContain("[3:a]", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_sequence_is_refused()
    {
        Assert.Throws<ArgumentException>(() => FilterGraphBuilder.BuildAudioOnly(new EditSequence()));
    }

    [Fact]
    public void A_transitioned_boundary_crossfades_the_audio_instead_of_a_hard_cut()
    {
        // El preview de solo audio comparte el mismo cálculo de solape que la exportación
        // con imagen, o el sonido y la imagen se desincronizarían a partir de la transición.
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("a.mp4", 10)));
        sequence.Video.Append(new Clip(Media("b.mp4", 10))
        {
            TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1)),
        });

        var plan = FilterGraphBuilder.BuildAudioOnly(sequence);

        Assert.Contains("[a0][a1]acrossfade=d=1[aout]", plan.FilterGraph, StringComparison.Ordinal);
        Assert.DoesNotContain("concat=", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(19), plan.Duration);
    }

    [Fact]
    public void The_audio_only_mix_shortens_by_the_same_overlap_as_the_video_export()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("a.mp4", 10)));
        sequence.Video.Append(new Clip(Media("b.mp4", 10))
        {
            TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1)),
        });

        var settings = new ExportSettings
        {
            OutputPath = "o.mp4",
            Resolution = VideoResolution.P1080,
            EncoderName = "libx264",
        };

        var videoPlan = FilterGraphBuilder.Build(sequence, settings);
        var audioOnlyPlan = FilterGraphBuilder.BuildAudioOnly(sequence);

        Assert.Equal(videoPlan.Duration, audioOnlyPlan.Duration);
    }
}
