// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Exporting;

public class AudioMixGraphTests
{
    private static MediaInfo Media(string name, double seconds, bool audio = true) =>
        new(name, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", audio);

    private static ExportSettings Settings() => new()
    {
        OutputPath = "out.mp4",
        Resolution = VideoResolution.P1080,
        EncoderName = "libx264",
        FrameRate = 30,
    };

    private static EditSequence WithVideo(double seconds = 10)
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("video.mp4", seconds)));
        return sequence;
    }

    private static AudioClip Music(double start, double seconds, string name = "musica.mp3") =>
        new(Media(name, 300), TimeSpan.Zero, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(start));

    [Fact]
    public void Without_audio_tracks_the_graph_is_unchanged()
    {
        // Añadir el soporte de mezcla no debe tocar la exportación de siempre.
        var plan = FilterGraphBuilder.Build(WithVideo(), Settings());

        Assert.DoesNotContain("amix", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("concat=n=1:v=1:a=1[vout][aout]", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void An_audio_clip_is_mixed_in_without_lowering_the_others()
    {
        // Por defecto amix divide el volumen de cada entrada entre el número de entradas:
        // añadir una música haría sonar más bajo el audio del video sin que nadie lo tocara.
        var sequence = WithVideo();
        sequence.AddAudioTrack().TryAdd(Music(0, 5));

        var plan = FilterGraphBuilder.Build(sequence, Settings());

        Assert.Contains("amix=inputs=2", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("normalize=0", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("duration=longest", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clip_placed_later_is_delayed_by_its_position()
    {
        var sequence = WithVideo();
        sequence.AddAudioTrack().TryAdd(Music(start: 7, seconds: 2));

        var plan = FilterGraphBuilder.Build(sequence, Settings());

        Assert.Contains("adelay=delays=7000:all=1", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clip_at_the_start_needs_no_delay()
    {
        var sequence = WithVideo();
        sequence.AddAudioTrack().TryAdd(Music(0, 3));

        var plan = FilterGraphBuilder.Build(sequence, Settings());

        Assert.DoesNotContain("adelay", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Clip_and_track_gain_add_up_in_decibels()
    {
        var sequence = WithVideo();
        var track = sequence.AddAudioTrack();
        track.GainDb = -2;
        var clip = Music(0, 4);
        clip.GainDb = -6;
        track.TryAdd(clip);

        var plan = FilterGraphBuilder.Build(sequence, Settings());

        Assert.Contains("volume=-8dB", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Unity_gain_adds_no_volume_filter()
    {
        var sequence = WithVideo();
        sequence.AddAudioTrack().TryAdd(Music(0, 4));

        var plan = FilterGraphBuilder.Build(sequence, Settings());

        Assert.DoesNotContain("volume=", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Fades_are_measured_from_the_start_of_the_clip_not_of_the_sequence()
    {
        // El fundido de salida de un clip de 10 s con 3 s de fundido empieza en el
        // segundo 7 DEL CLIP, aunque el clip esté colocado en el segundo 20 de la timeline.
        // El desfase se aplica después, precisamente para que sea así.
        var sequence = WithVideo(30);
        var clip = Music(start: 20, seconds: 10);
        clip.FadeIn = TimeSpan.FromSeconds(2);
        clip.FadeOut = TimeSpan.FromSeconds(3);
        sequence.AddAudioTrack().TryAdd(clip);

        var plan = FilterGraphBuilder.Build(sequence, Settings());

        Assert.Contains("afade=t=in:st=0:d=2", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("afade=t=out:st=7:d=3", plan.FilterGraph, StringComparison.Ordinal);

        var fadeOut = plan.FilterGraph.IndexOf("afade=t=out", StringComparison.Ordinal);
        var delay = plan.FilterGraph.IndexOf("adelay", StringComparison.Ordinal);
        Assert.True(fadeOut < delay, "el desfase debe aplicarse después de los fundidos");
    }

    [Fact]
    public void A_muted_track_contributes_nothing()
    {
        var sequence = WithVideo();
        var track = sequence.AddAudioTrack();
        track.TryAdd(Music(0, 4));
        track.IsMuted = true;

        var plan = FilterGraphBuilder.Build(sequence, Settings());

        Assert.DoesNotContain("amix", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_muted_clip_contributes_nothing()
    {
        var sequence = WithVideo();
        var clip = Music(0, 4);
        clip.IsMuted = true;
        sequence.AddAudioTrack().TryAdd(clip);

        var plan = FilterGraphBuilder.Build(sequence, Settings());

        Assert.DoesNotContain("amix", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Solo_leaves_out_the_other_tracks()
    {
        // Una pista en solo silencia a las demás aunque no estén silenciadas. Ignorarlo
        // exportaría lo que el usuario dejó fuera al activar el solo.
        var sequence = WithVideo();
        var soloed = sequence.AddAudioTrack();
        soloed.TryAdd(Music(0, 4, "elegida.mp3"));
        soloed.IsSolo = true;
        sequence.AddAudioTrack().TryAdd(Music(0, 4, "descartada.mp3"));

        var plan = FilterGraphBuilder.Build(sequence, Settings());
        var inputs = string.Join(' ', plan.InputArguments);

        Assert.Contains("elegida.mp3", inputs, StringComparison.Ordinal);
        Assert.DoesNotContain("descartada.mp3", inputs, StringComparison.Ordinal);
        Assert.Contains("amix=inputs=2", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clip_with_detached_audio_no_longer_uses_its_own_sound()
    {
        // Ya suena desde su pista; usarlo también lo duplicaría. Se sustituye por silencio,
        // que además mantiene el número de flujos que concat exige.
        var sequence = WithVideo();
        var clip = sequence.Video.Clips[0];
        new DetachAudioCommand(sequence, clip).Execute();

        var plan = FilterGraphBuilder.Build(sequence, Settings());

        Assert.Contains("anullsrc", string.Join(' ', plan.InputArguments), StringComparison.Ordinal);
        Assert.Contains("amix=inputs=2", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Music_longer_than_the_video_extends_the_picture_with_black()
    {
        // Sin esto el archivo tendría el audio más largo que la imagen y muchos
        // reproductores congelan el último fotograma o cortan el sonido.
        var sequence = WithVideo(seconds: 4);
        sequence.AddAudioTrack().TryAdd(Music(0, 9));

        var plan = FilterGraphBuilder.Build(sequence, Settings());

        Assert.Contains("tpad=stop_mode=add:stop_duration=5", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(9), plan.Duration);
    }

    [Fact]
    public void Music_that_ends_within_the_video_does_not_pad_it()
    {
        var sequence = WithVideo(seconds: 10);
        sequence.AddAudioTrack().TryAdd(Music(0, 4));

        var plan = FilterGraphBuilder.Build(sequence, Settings());

        Assert.DoesNotContain("tpad", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(10), plan.Duration);
    }

    [Fact]
    public void A_muted_long_track_does_not_extend_the_export()
    {
        // La duración solo cuenta lo que se oye. Una pista silenciada más larga que el
        // video alargaría la exportación con negro y silencio sin que nadie lo pidiera.
        var sequence = WithVideo(seconds: 4);
        var track = sequence.AddAudioTrack();
        track.TryAdd(Music(0, 30));
        track.IsMuted = true;

        var plan = FilterGraphBuilder.Build(sequence, Settings());

        Assert.Equal(TimeSpan.FromSeconds(4), plan.Duration);
        Assert.DoesNotContain("tpad", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Audio_inputs_come_after_all_video_inputs()
    {
        // Los índices de entrada deben seguir a los de los clips de video, incluidos los
        // silencios sintéticos: si no, el mezclador leería un flujo equivocado.
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("a.mp4", 5, audio: false)));
        sequence.Video.Append(new Clip(Media("b.mp4", 5)));
        sequence.AddAudioTrack().TryAdd(Music(0, 3));

        var plan = FilterGraphBuilder.Build(sequence, Settings());

        // Entradas: 0 video mudo, 1 silencio, 2 segundo video, 3 música.
        Assert.Contains("[3:a]", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void The_export_command_reports_the_real_duration()
    {
        var sequence = WithVideo(seconds: 4);
        sequence.AddAudioTrack().TryAdd(Music(0, 9));

        using var command = ExportCommandBuilder.Build(sequence, Settings());

        Assert.Equal(TimeSpan.FromSeconds(9), command.Duration);
    }
}
