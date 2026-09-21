// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Projects;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;
using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Timeline;

public class ClipAudioGainTests
{
    private static MediaInfo Media(bool audio = true, double seconds = 10) =>
        new("v.mp4", TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", audio);

    private static ExportSettings Settings() => new()
    {
        OutputPath = "out.mp4",
        Resolution = VideoResolution.P1080,
        EncoderName = "libx264",
        FrameRate = 30,
    };

    // ------------------------------------------------------------------ modelo

    [Theory]
    [InlineData(-200, -60)]
    [InlineData(40, 12)]
    [InlineData(6, 6)]
    public void The_video_clip_gain_is_clamped_like_an_audio_clip(double requested, double expected)
    {
        var clip = new Clip(Media()) { AudioGainDb = requested };

        Assert.Equal(expected, clip.AudioGainDb);
    }

    [Fact]
    public void A_normal_clip_contributes_its_own_sound()
    {
        Assert.True(new Clip(Media()).HasOwnAudio);
    }

    [Fact]
    public void A_silent_source_has_no_own_sound()
    {
        Assert.False(new Clip(Media(audio: false)).HasOwnAudio);
    }

    [Fact]
    public void A_muted_clip_has_no_own_sound_but_keeps_its_volume()
    {
        // Silenciar y reactivar debe devolver el volumen que había, no dejarlo a cero.
        var clip = new Clip(Media()) { AudioGainDb = -4, IsAudioMuted = true };
        Assert.False(clip.HasOwnAudio);

        clip.IsAudioMuted = false;

        Assert.True(clip.HasOwnAudio);
        Assert.Equal(-4, clip.AudioGainDb);
    }

    [Fact]
    public void A_clip_with_detached_audio_has_no_own_sound()
    {
        var sequence = new EditSequence();
        var clip = new Clip(Media());
        sequence.Video.Append(clip);
        new DetachAudioCommand(sequence, clip).Execute();

        Assert.False(clip.HasOwnAudio);
    }

    [Fact]
    public void Splitting_hands_the_volume_and_mute_to_the_second_half()
    {
        // Cortar un clip a -6 dB no debe devolver la segunda mitad a 0 dB: el volumen
        // saltaría justo en el corte.
        var timeline = new VideoTimeline();
        var clip = new Clip(Media()) { AudioGainDb = -6, IsAudioMuted = true };
        timeline.Append(clip);

        var second = timeline.SplitAt(TimeSpan.FromSeconds(4));

        Assert.NotNull(second);
        Assert.Equal(-6, second.AudioGainDb);
        Assert.True(second.IsAudioMuted);
    }

    [Fact]
    public void Cloning_keeps_the_audio_state()
    {
        var copy = new Clip(Media()) { AudioGainDb = 3, IsAudioMuted = true }.Clone();

        Assert.Equal(3, copy.AudioGainDb);
        Assert.True(copy.IsAudioMuted);
    }

    [Fact]
    public void Changing_the_volume_can_be_undone()
    {
        var clip = new Clip(Media()) { AudioGainDb = 2 };
        var history = new UndoHistory();

        history.Do(new SetClipAudioGainCommand(clip, -9));
        Assert.Equal(-9, clip.AudioGainDb);

        history.Undo();
        Assert.Equal(2, clip.AudioGainDb);
    }

    [Fact]
    public void Muting_can_be_undone()
    {
        var clip = new Clip(Media());
        var history = new UndoHistory();

        history.Do(new SetClipAudioMutedCommand(clip, muted: true));
        Assert.True(clip.IsAudioMuted);

        history.Undo();
        Assert.False(clip.IsAudioMuted);
    }

    // ------------------------------------------------------------------ grafo

    [Fact]
    public void The_volume_reaches_the_filter_graph()
    {
        var timeline = new VideoTimeline();
        timeline.Append(new Clip(Media()) { AudioGainDb = 6 });

        var plan = FilterGraphBuilder.Build(timeline, Settings());

        Assert.Contains("volume=6dB", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Unity_gain_adds_no_volume_filter()
    {
        var timeline = new VideoTimeline();
        timeline.Append(new Clip(Media()));

        var plan = FilterGraphBuilder.Build(timeline, Settings());

        Assert.DoesNotContain("volume=", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_muted_clip_is_replaced_by_silence()
    {
        // Se sustituye por silencio y no se elimina la rama: concat exige que todas sus
        // entradas tengan el mismo número de flujos.
        var timeline = new VideoTimeline();
        timeline.Append(new Clip(Media()) { IsAudioMuted = true, AudioGainDb = 6 });

        var plan = FilterGraphBuilder.Build(timeline, Settings());

        Assert.Contains("anullsrc", string.Join(' ', plan.InputArguments), StringComparison.Ordinal);
        Assert.DoesNotContain("volume=", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void The_volume_applies_to_the_right_clip_only()
    {
        var timeline = new VideoTimeline();
        timeline.Append(new Clip(Media()));
        timeline.Append(new Clip(Media()) { AudioGainDb = -6 });

        var plan = FilterGraphBuilder.Build(timeline, Settings());
        var lines = plan.FilterGraph.Split('\n');

        var first = lines.Single(l => l.Contains("[a0]", StringComparison.Ordinal) && l.StartsWith("[0:a]", StringComparison.Ordinal));
        var second = lines.Single(l => l.Contains("[a1]", StringComparison.Ordinal) && l.StartsWith("[1:a]", StringComparison.Ordinal));

        Assert.DoesNotContain("volume", first, StringComparison.Ordinal);
        Assert.Contains("volume=-6dB", second, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- guardado

    [Fact]
    public void Volume_and_mute_survive_the_file_format()
    {
        var project = new EditProject();
        var media = project.AddMedia(Media());
        project.Timeline.Append(new Clip(media) { AudioGainDb = -7.5, IsAudioMuted = true });

        var file = ProjectSerializer.ToFile(project, "p.editflow");

        Assert.Equal(-7.5, file.Clips[0].AudioGainDb);
        Assert.True(file.Clips[0].AudioMuted);
    }

    [Fact]
    public void A_project_from_before_this_field_opens_at_unity_gain()
    {
        // Añadir un campo no debe invalidar los archivos anteriores: el valor ausente es
        // cero y el silencio ausente es falso.
        var clip = new ProjectClip();

        Assert.Equal(0, clip.AudioGainDb);
        Assert.False(clip.AudioMuted);
    }
}
