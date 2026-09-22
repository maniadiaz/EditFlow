// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;
using EditFlow.Engine.Playback;

namespace EditFlow.Engine.Tests.Timeline;

public class VideoLayerAudioLowerTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static MediaInfo Media(bool audio = true) =>
        new("C:/no-existe/a.mp4", S(10), 1920, 1080, 30, "h264", audio);

    private static (EditSequence Sequence, OverlayTrack Track, OverlayItem Item) Lifted()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media()));
        var second = sequence.Video.SplitAt(S(4))!;
        var command = new LiftClipToLayerCommand(sequence, second);
        command.Execute();
        return (sequence, command.Track!, command.Item!);
    }

    // ------------------------------------------------------------------ sonido

    [Fact]
    public void The_sound_of_a_video_layer_can_be_muted_and_turned_down_and_undone()
    {
        var (_, _, item) = Lifted();
        var history = new UndoHistory();

        history.Do(new SetOverlayAudioCommand(item, playsAudio: false, gainDb: -12));
        Assert.False(item.PlaysAudio);
        Assert.Equal(-12, item.AudioGainDb);

        history.Undo();
        Assert.True(item.PlaysAudio);
        Assert.Equal(0, item.AudioGainDb);
    }

    [Fact]
    public void The_volume_is_kept_within_the_range_of_a_clip_and_a_silent_file_stays_silent()
    {
        var (_, _, item) = Lifted();
        new SetOverlayAudioCommand(item, true, 500).Execute();
        Assert.Equal(AudioClip.MaximumGainDb, item.AudioGainDb);

        var silent = OverlayItem.CreateVideo(Media(audio: false), TimeSpan.Zero, S(1), S(2));
        new SetOverlayAudioCommand(silent, playsAudio: true, gainDb: 0).Execute();
        Assert.False(silent.PlaysAudio);                                   // no hay sonido que activar
    }

    [Fact]
    public void Changing_the_layer_volume_changes_the_audio_signature()
    {
        var (sequence, _, item) = Lifted();
        var before = AudioMixSignature.Compute(sequence);

        new SetOverlayAudioCommand(item, true, -6).Execute();
        var quieter = AudioMixSignature.Compute(sequence);
        new SetOverlayAudioCommand(item, false, -6).Execute();
        var muted = AudioMixSignature.Compute(sequence);

        Assert.NotEqual(before, quieter);
        Assert.NotEqual(quieter, muted);
    }

    // -------------------------------------------------------- bajar a la pista

    [Fact]
    public void Lowering_a_lifted_clip_puts_it_back_in_the_gap_it_left()
    {
        var (sequence, track, item) = Lifted();
        var source = (item.SourceIn, item.Duration);

        var command = new LowerOverlayToMainCommand(sequence, track, item);
        command.Execute();

        Assert.Equal(2, sequence.Video.Clips.Count);
        Assert.DoesNotContain(sequence.Video.Clips, c => c.IsGap);
        Assert.Equal(S(10), sequence.Video.Duration);
        Assert.Empty(track.Items);
        Assert.Equal(source.SourceIn, command.Result!.SourceIn);
        Assert.Equal(source.Duration, command.Result.Duration);
        Assert.Same(command.Result, sequence.Video.Clips[1]);
    }

    [Fact]
    public void A_smaller_video_leaves_the_rest_of_the_gap_on_both_sides()
    {
        var (sequence, track, item) = Lifted();                            // gap de 4 a 10 s
        Assert.True(track.TryPlace(item, S(5), S(2)));                     // recortado: ocupa de 5 a 7 s

        new LowerOverlayToMainCommand(sequence, track, item).Execute();

        Assert.Equal(4, sequence.Video.Clips.Count);                       // A, hueco 4-5, clip 5-7, hueco 7-10
        Assert.Equal([false, true, false, true], sequence.Video.Clips.Select(c => c.IsGap).ToArray());
        Assert.Equal(S(1), sequence.Video.Clips[1].Duration);
        Assert.Equal(S(2), sequence.Video.Clips[2].Duration);
        Assert.Equal(S(3), sequence.Video.Clips[3].Duration);
        Assert.Equal(S(10), sequence.Video.Duration);
    }

    [Fact]
    public void Lowering_keeps_the_sound_settings_and_the_colour()
    {
        var (sequence, track, item) = Lifted();
        new SetOverlayAudioCommand(item, playsAudio: true, gainDb: -8).Execute();
        item.Color = new ColorAdjust(10, 20, 30, 40);

        var command = new LowerOverlayToMainCommand(sequence, track, item);
        command.Execute();

        Assert.Equal(-8, command.Result!.AudioGainDb);
        Assert.False(command.Result.IsAudioMuted);
        Assert.Equal(new ColorAdjust(10, 20, 30, 40), command.Result.Color);

        // Y un video silenciado baja silenciado.
        var (sequence2, track2, item2) = Lifted();
        new SetOverlayAudioCommand(item2, playsAudio: false, gainDb: 0).Execute();
        var command2 = new LowerOverlayToMainCommand(sequence2, track2, item2);
        command2.Execute();
        Assert.True(command2.Result!.IsAudioMuted);
    }

    [Fact]
    public void A_video_over_real_footage_cannot_be_lowered()
    {
        var (sequence, track, item) = Lifted();
        Assert.True(track.TryPlace(item, S(2), item.Duration - S(2)));     // ahora pisa un trozo de la pista principal

        Assert.False(LowerOverlayToMainCommand.CanLower(sequence, item));
        Assert.Throws<InvalidOperationException>(() => new LowerOverlayToMainCommand(sequence, track, item).Execute());
    }

    [Fact]
    public void A_video_past_the_end_of_the_main_track_is_appended_with_a_gap_in_front()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media()));                          // 10 s
        var track = sequence.AddOverlayTrack();
        var item = OverlayItem.CreateVideo(Media(), TimeSpan.Zero, S(12), S(3));
        track.TryAdd(item);

        new LowerOverlayToMainCommand(sequence, track, item).Execute();

        Assert.Equal(3, sequence.Video.Clips.Count);
        Assert.True(sequence.Video.Clips[1].IsGap);
        Assert.Equal(S(2), sequence.Video.Clips[1].Duration);
        Assert.Equal(S(15), sequence.Video.Duration);
    }

    [Fact]
    public void Lowering_can_be_undone_and_redone()
    {
        var (sequence, track, item) = Lifted();
        var history = new UndoHistory();

        history.Do(new LowerOverlayToMainCommand(sequence, track, item));
        Assert.Empty(track.Items);

        history.Undo();
        Assert.Same(item, Assert.Single(track.Items));
        Assert.True(sequence.Video.Clips[1].IsGap);
        Assert.Equal(S(10), sequence.Video.Duration);

        history.Redo();
        Assert.Empty(track.Items);
        Assert.DoesNotContain(sequence.Video.Clips, c => c.IsGap);
    }

    [Fact]
    public void Text_and_images_cannot_be_lowered()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media()));
        var text = OverlayItem.CreateText(new TextStyle("hola"), S(1), S(2));

        Assert.False(LowerOverlayToMainCommand.CanLower(sequence, text));
    }
}
